using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;

namespace KenseiStand.Generator {
    public sealed class SourceFile {
        public string Path;
        public string Text;
    }

    /// <summary>
    /// Model to C#. The instrumented form (what the stand compiles) tags every class with
    /// [StandShared.ClsId] and calls StandShared.Probe.Hit from every hand-written Init, OnInit
    /// and hook; the clean form (for repro files) has comments there and short type names, and
    /// declares the components it uses.
    /// </summary>
    public static class Renderer {
        private const string W = "global::KenseiECS.World";
        private const string S = "global::KenseiECS.SharedData";

        public static List<SourceFile> Render(ProgramModel p, Asm asm, bool instrument) {
            var ctx = new Ctx { P = p, Instrument = instrument };
            var classes = p.Classes.Where(c => c.Asm == asm).ToList();
            int files = classes.Count == 0 ? 1 : Math.Max(1, classes.Where(c => c.NestedIn == null).Select(c => c.Parts).DefaultIfEmpty(1).Max());
            bool alias = classes.Any(c => c.Fields.Any(f => f.Spelling == "alias"));
            var cleanTypes = instrument ? new SortedSet<string>() : CleanTypes(p);
            var first = FirstAsm(p);
            var last = LastAsm(p);
            var result = new List<SourceFile>();
            for (int file = 0; file < files; file++) {
                var sb = new StringBuilder();
                sb.AppendLine($"// Stand program '{p.Title}', {asm} file {file}.");
                if (p.NullableEnable) {
                    sb.AppendLine("#nullable enable");
                }
                sb.AppendLine("using KenseiECS;");
                if (cleanTypes.Count > 0) {
                    sb.AppendLine("using StandShared;");
                }
                if (alias) {
                    foreach (var a in new[] { "Inc", "Exc", "Any", "Pool", "Group", "Shared" }) {
                        sb.AppendLine($"using Inj{a} = KenseiECS.{a}Attribute;");
                    }
                }
                sb.AppendLine();
                if (file == 0) {
                    foreach (var group in p.Interfaces.Where(i => i.Asm == asm).GroupBy(i => i.Ns)) {
                        int indent = OpenNamespace(sb, group.Key);
                        foreach (var i in group) {
                            var bases = new List<string>();
                            if (i.IncludesInit) {
                                bases.Add(ctx.K("IInitSystem"));
                            }
                            if (i.IncludesRun) {
                                bases.Add(ctx.K("IRunSystem"));
                            }
                            string list = bases.Count == 0 ? "" : " : " + string.Join(", ", bases);
                            sb.AppendLine($"{Pad(indent)}public interface {i.Name}{list} {{ }}");
                        }
                        CloseNamespace(sb, group.Key);
                    }
                }
                var parts = classes.Where(c => c.NestedIn == null && file < c.Parts).ToList();
                foreach (var nsGroup in parts.GroupBy(c => c.Ns)) {
                    int indent = OpenNamespace(sb, nsGroup.Key);
                    foreach (var chain in nsGroup.GroupBy(OuterKey)) {
                        var outers = chain.First().Outers;
                        foreach (var o in outers) {
                            string mods = "public " + (o.Kind == OuterKind.StaticClass ? "static " : "") + (string.IsNullOrEmpty(o.Modifiers) ? "" : o.Modifiers + " ") + (o.Partial ? "partial " : "");
                            string kw = o.Kind switch {
                                OuterKind.Struct => "struct",
                                OuterKind.Record => "record",
                                OuterKind.Interface => "interface",
                                _ => "class",
                            };
                            string tp = string.IsNullOrEmpty(o.TypeParams) ? "" : "<" + o.TypeParams + ">";
                            sb.AppendLine($"{Pad(indent)}{mods}{kw} {o.Name}{tp} {{");
                            indent++;
                        }
                        foreach (var c in chain) {
                            RenderPart(sb, ctx, c, file, indent);
                        }
                        foreach (var _ in outers) {
                            indent--;
                            sb.AppendLine($"{Pad(indent)}}}");
                        }
                    }
                    CloseNamespace(sb, nsGroup.Key);
                }
                if (!instrument && file == 0 && asm == first) {
                    AppendCleanTypes(sb, cleanTypes);
                    AppendReproLog(sb);
                }
                if (!instrument && file == files - 1 && asm == last) {
                    AppendReproMain(sb, p);
                }
                result.Add(new SourceFile { Path = $"{asm}{file}.cs", Text = sb.ToString() });
            }
            return result;
        }

        /// <summary> The assemblies a program compiles, in build order: Lib (when it has classes or interfaces) and Main. </summary>
        public static List<Asm> Assemblies(ProgramModel p) {
            var list = new List<Asm>();
            if (p.Classes.Any(c => c.Asm == Asm.Lib) || p.Interfaces.Any(i => i.Asm == Asm.Lib)) {
                list.Add(Asm.Lib);
            }
            if (p.Classes.Any(c => c.Asm == Asm.Main) || list.Count == 0) {
                list.Add(Asm.Main);
            }
            return list;
        }

        private static Asm FirstAsm(ProgramModel p) => Assemblies(p)[0];

        private static Asm LastAsm(ProgramModel p) => Assemblies(p)[^1];

        /// <summary> All files of both assemblies in one text (clean repro). </summary>
        public static string RenderClean(ProgramModel p) {
            var sb = new StringBuilder();
            foreach (var asm in Assemblies(p)) {
                foreach (var f in Render(p, asm, instrument: false)) {
                    sb.AppendLine($"// ===== {(asm == Asm.Lib ? "assembly Lib (its own generator run; Main references it)" : "assembly Main" + (Assemblies(p).Contains(Asm.Lib) ? " (references Lib)" : ""))}: {f.Path}");
                    if (asm == Asm.Lib && p.LibDefines.Count > 0) {
                        sb.AppendLine($"// compiled with defines: {string.Join(", ", p.LibDefines)}");
                    }
                    if (asm == Asm.Main && p.MainDefines.Count > 0) {
                        sb.AppendLine($"// compiled with defines: {string.Join(", ", p.MainDefines)}");
                    }
                    sb.Append(f.Text);
                    sb.AppendLine();
                }
            }
            return sb.ToString();
        }

        /// <summary> Component and shared-data types of StandShared that the program names; the clean form declares them itself. </summary>
        private static SortedSet<string> CleanTypes(ProgramModel p) {
            var used = new SortedSet<string>(StringComparer.Ordinal);
            void scan(string t) {
                foreach (var part in (t ?? "").Split(',', ' ', '<', '>', '?', '[', ']')) {
                    if (part.StartsWith("global::StandShared.")) {
                        used.Add(part.Substring("global::StandShared.".Length));
                    }
                }
            }
            foreach (var c in p.Classes) {
                foreach (var f in c.Fields) {
                    foreach (var t in f.Inc.Concat(f.Exc).Concat(f.Any)) {
                        scan(t);
                    }
                    scan(f.Type);
                    scan(f.WrongType);
                }
                scan(c.BaseArgs);
                scan(c.BaseRef);
            }
            return used;
        }

        private static void AppendCleanTypes(StringBuilder sb, SortedSet<string> used) {
            if (used.Count == 0) {
                return;
            }
            sb.AppendLine("namespace StandShared {");
            foreach (var t in used) {
                if (SharedKinds.CleanDeclarations.TryGetValue(t, out var declaration)) {
                    sb.AppendLine("    " + declaration);
                } else if (t.StartsWith("Cfg")) {
                    sb.AppendLine($"    public class {t} {{ }}");
                } else if (t == "NotComponent") {
                    sb.AppendLine($"    public struct {t} {{ }}");
                } else {
                    sb.AppendLine($"    public struct {t} : IComponent {{ }}");
                }
            }
            sb.AppendLine("}");
            sb.AppendLine();
        }

        // Trace of the hand-written Init, OnInit and hook calls, with the injected fields that are still null at each call.
        private static void AppendReproLog(StringBuilder sb) {
            sb.AppendLine("/// <summary> Calls of the hand-written Init/OnInit/hook methods, each with the injected fields still null at that moment. </summary>");
            sb.AppendLine("public static class ReproLog {");
            sb.AppendLine("    public static readonly System.Collections.Generic.List<string> Calls = new System.Collections.Generic.List<string>();");
            sb.AppendLine();
            sb.AppendLine("    public static void Hit(object self, string what) {");
            sb.AppendLine("        var nulls = NullInjectedFields(self);");
            sb.AppendLine("        Calls.Add(nulls.Count == 0 ? what : what + \" (null: \" + string.Join(\", \", nulls) + \")\");");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    /// <summary> \"Type.field\" of every field with a KenseiECS injection attribute that is null. </summary>");
            sb.AppendLine("    public static System.Collections.Generic.List<string> NullInjectedFields(object self) {");
            sb.AppendLine("        var result = new System.Collections.Generic.List<string>();");
            sb.AppendLine("        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;");
            sb.AppendLine("        for (var t = self.GetType(); t != null && t != typeof(object); t = t.BaseType) {");
            sb.AppendLine("            foreach (var f in t.GetFields(flags)) {");
            sb.AppendLine("                foreach (var a in f.CustomAttributes) {");
            sb.AppendLine("                    if (a.AttributeType.Namespace == \"KenseiECS\" && a.AttributeType.Name is \"IncAttribute\" or \"ExcAttribute\" or \"AnyAttribute\" or \"PoolAttribute\" or \"GroupAttribute\" or \"SharedAttribute\") {");
            sb.AppendLine("                        if (f.GetValue(self) == null) {");
            sb.AppendLine("                            result.Add(t.Name.Split('`')[0] + \".\" + f.Name);");
            sb.AppendLine("                        }");
            sb.AppendLine("                        break;");
            sb.AppendLine("                    }");
            sb.AppendLine("                }");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine("        return result;");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    /// <summary> \"Type.field\" of every injected field whose value is not what the documented call returns on this World and SharedData:");
            sb.AppendLine("    /// world.Filter().Inc<...>().Exc<...>().Any<...>().End(), world.Pool<T>(), world.Group<...>(), shared.Get<T>(key). </summary>");
            sb.AppendLine("    public static System.Collections.Generic.List<string> WrongInjections(object self, World world, SharedData shared) {");
            sb.AppendLine("        var result = new System.Collections.Generic.List<string>();");
            sb.AppendLine("        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;");
            sb.AppendLine("        for (var t = self.GetType(); t != null && t != typeof(object); t = t.BaseType) {");
            sb.AppendLine("            foreach (var f in t.GetFields(flags)) {");
            sb.AppendLine("                object value = f.GetValue(self);");
            sb.AppendLine("                if (value == null) {");
            sb.AppendLine("                    continue;");
            sb.AppendLine("                }");
            sb.AppendLine("                object expected;");
            sb.AppendLine("                try {");
            sb.AppendLine("                    expected = Expected(f, world, shared);");
            sb.AppendLine("                } catch (System.Exception) {");
            sb.AppendLine("                    continue;");
            sb.AppendLine("                }");
            sb.AppendLine("                if (expected != null && !ReferenceEquals(expected, value)) {");
            sb.AppendLine("                    result.Add(t.Name.Split('`')[0] + \".\" + f.Name);");
            sb.AppendLine("                }");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine("        return result;");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    private static object Expected(System.Reflection.FieldInfo f, World world, SharedData shared) {");
            sb.AppendLine("        var inc = (IncAttribute)System.Attribute.GetCustomAttribute(f, typeof(IncAttribute));");
            sb.AppendLine("        var exc = (ExcAttribute)System.Attribute.GetCustomAttribute(f, typeof(ExcAttribute));");
            sb.AppendLine("        var any = (AnyAttribute)System.Attribute.GetCustomAttribute(f, typeof(AnyAttribute));");
            sb.AppendLine("        if (inc != null || exc != null || any != null) {");
            sb.AppendLine("            object builder = world.Filter();");
            sb.AppendLine("            foreach (var (name, types) in new[] { (\"Inc\", inc?.Types), (\"Exc\", exc?.Types), (\"Any\", any?.Types) }) {");
            sb.AppendLine("                foreach (var type in types ?? new System.Type[0]) {");
            sb.AppendLine("                    builder = Generic(typeof(FilterBuilder), name, 1).MakeGenericMethod(type).Invoke(builder, null);");
            sb.AppendLine("                }");
            sb.AppendLine("            }");
            sb.AppendLine("            return ((FilterBuilder)builder).End();");
            sb.AppendLine("        }");
            sb.AppendLine("        if (System.Attribute.IsDefined(f, typeof(PoolAttribute))) {");
            sb.AppendLine("            return Generic(typeof(World), \"Pool\", 1).MakeGenericMethod(f.FieldType.GetGenericArguments()).Invoke(world, null);");
            sb.AppendLine("        }");
            sb.AppendLine("        if (System.Attribute.IsDefined(f, typeof(GroupAttribute))) {");
            sb.AppendLine("            var args = f.FieldType.GetGenericArguments();");
            sb.AppendLine("            return Generic(typeof(World), \"Group\", args.Length).MakeGenericMethod(args).Invoke(world, null);");
            sb.AppendLine("        }");
            sb.AppendLine("        var key = (SharedAttribute)System.Attribute.GetCustomAttribute(f, typeof(SharedAttribute));");
            sb.AppendLine("        return key == null ? null : Generic(typeof(SharedData), \"Get\", 1).MakeGenericMethod(f.FieldType).Invoke(shared, new object[] { key.Key });");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    private static System.Reflection.MethodInfo Generic(System.Type type, string name, int arity) {");
            sb.AppendLine("        foreach (var m in type.GetMethods()) {");
            sb.AppendLine("            if (m.Name == name && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == arity && (name == \"Get\" || m.GetParameters().Length == 0)) {");
            sb.AppendLine("                return m;");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine("        throw new System.MissingMethodException(type.Name, name);");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        /// <summary> Classes the stand (and ReproMain) runs: concrete, non-generic classes with something that gets or has an Init in their chain. </summary>
        public static IEnumerable<ClassModel> RunnableClasses(ProgramModel p) {
            foreach (var c in p.Classes) {
                if (!p.IsActive(c) || c.Kind != "class" || c.Abstract || c.IsGeneric || p.Ancestors(c).Count() != p.Ancestors(c).Distinct().Count()) {
                    continue;
                }
                if (OuterChainGeneric(p, c)) {
                    continue;
                }
                if (new[] { c }.Concat(p.Ancestors(c)).Any(x => x.IsGenerated || x.Init != InitKind.None || x.ListsInit || x.IfaceId != null)) {
                    yield return c;
                }
            }
        }

        private static bool OuterChainGeneric(ProgramModel p, ClassModel c) {
            for (var x = c; x != null; x = p.Cls(x.NestedIn)) {
                if (x.IsGeneric) {
                    return true;
                }
            }
            return false;
        }

        /// <summary> The registrations every [Shared] field of the program needs, as C# statements on 'shared'. </summary>
        public static List<string> SharedRegistrations(ProgramModel p) {
            var lines = new List<string>();
            var done = new HashSet<string>();
            foreach (var f in p.Classes.SelectMany(c => c.Fields).Where(f => f.Kind == FieldKind.Shared && f.WrongType == null && !f.Static)) {
                string typeName = f.Type.TrimEnd('?');
                if (!done.Add(typeName + (f.Key == null ? "|<null>" : "|=" + f.Key))) {
                    continue;
                }
                string value = typeName switch {
                    "string" => SymbolDisplay.FormatLiteral("s:" + (f.Key ?? "<null>"), true),
                    "object" => "new object()",
                    "int[]" => "new int[1]",
                    _ => "new " + typeName.Replace("global::StandShared.", "") + "()",
                };
                string generic = typeName is "object" ? "<object>" : "";
                var (special, typeArg) = SharedKinds.Registration(typeName);
                if (special != null) {
                    // An interface or abstract class is registered under its own type with an implementation.
                    value = special;
                    generic = typeArg != null ? "<" + typeArg + ">" : "";
                }
                lines.Add(f.Key == null ? $"shared.Add{generic}({value});" : $"shared.Add{generic}({value}, {SymbolDisplay.FormatLiteral(f.Key, true)});");
            }
            return lines;
        }

        private static void AppendReproMain(StringBuilder sb, ProgramModel p) {
            var runnable = RunnableClasses(p).ToList();
            sb.AppendLine("/// <summary> Registers each system of the repro alone in a SystemsRunner, calls Init and returns what happened, one line per system. </summary>");
            sb.AppendLine("public static class ReproMain {");
            sb.AppendLine("    public static string Run() {");
            sb.AppendLine("        var report = new System.Text.StringBuilder();");
            foreach (var c in runnable) {
                sb.AppendLine($"        Check(report, \"{DisplayName(p, c)}\", () => new {TypeRef(p, c, null, instrument: false)}());");
            }
            sb.AppendLine("        return report.ToString();");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    private static void Check(System.Text.StringBuilder report, string name, System.Func<object> create) {");
            sb.AppendLine("        ReproLog.Calls.Clear();");
            sb.AppendLine("        object system = create();");
            sb.AppendLine("        var world = new World();");
            sb.AppendLine("        var shared = new SharedData();");
            foreach (var line in SharedRegistrations(p)) {
                sb.AppendLine("        " + line);
            }
            sb.AppendLine("        string error = null;");
            sb.AppendLine("        try {");
            sb.AppendLine("            if (system is ISystem s) {");
            sb.AppendLine("                new SystemsRunner(world, shared).Add(s).Init();");
            sb.AppendLine("            }");
            sb.AppendLine("        } catch (System.Exception e) {");
            sb.AppendLine("            error = (e is System.Reflection.TargetInvocationException t && t.InnerException != null ? t.InnerException : e).GetType().Name;");
            sb.AppendLine("        }");
            sb.AppendLine("        var nulls = ReproLog.NullInjectedFields(system);");
            sb.AppendLine("        nulls.Sort(System.StringComparer.Ordinal);");
            sb.AppendLine("        var wrong = ReproLog.WrongInjections(system, world, shared);");
            sb.AppendLine("        wrong.Sort(System.StringComparer.Ordinal);");
            sb.AppendLine("        report.Append(name).Append(\": IInitSystem=\").Append(system is IInitSystem ? \"yes\" : \"no\")");
            sb.AppendLine("            .Append(\"; calls: \").Append(ReproLog.Calls.Count == 0 ? \"-\" : string.Join(\" -> \", ReproLog.Calls))");
            sb.AppendLine("            .Append(\"; null after Init: \").Append(nulls.Count == 0 ? \"-\" : string.Join(\", \", nulls))");
            sb.AppendLine("            .Append(wrong.Count == 0 ? \"\" : \"; wrong injection: \" + string.Join(\", \", wrong))");
            sb.AppendLine("            .Append(error == null ? \"\" : \"; Init threw \" + error).AppendLine();");
            sb.AppendLine("    }");
            sb.AppendLine("}");
        }

        /// <summary> The text ReproMain.Run() prints for one run, built from an instrumented observation (ids mapped to class names). </summary>
        public static string ExpectedReproLine(ProgramModel p, RunObservation run) {
            var c = p.Cls(run.ClassId);
            string Map(string idDotRest) {
                int dot = idDotRest.IndexOf('.');
                var x = p.Cls(idDotRest.Substring(0, dot));
                return (x?.Plain ?? idDotRest.Substring(0, dot)) + idDotRest.Substring(dot);
            }
            var calls = run.Events.Select(e => {
                int colon = e.Tag.IndexOf(':');
                var who = p.Cls(e.Tag.Substring(colon + 1));
                string what = (who?.Plain ?? e.Tag.Substring(colon + 1)) + "." + e.Tag.Substring(0, colon);
                var nulls = e.NullFields.Select(Map).ToList();
                return nulls.Count == 0 ? what : what + " (null: " + string.Join(", ", nulls) + ")";
            }).ToList();
            var after = run.NullAfter.Select(Map).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var wrong = run.Wrong.Select(w => Map(w.Field)).OrderBy(x => x, StringComparer.Ordinal).ToList();
            string error = run.Exception == null ? "" : "; Init threw " + run.Exception.Split(':')[0];
            return $"{(c == null ? "?" : DisplayName(p, c))}: IInitSystem={(run.IsInitSystem ? "yes" : "no")}; calls: {(calls.Count == 0 ? "-" : string.Join(" -> ", calls))}; null after Init: {(after.Count == 0 ? "-" : string.Join(", ", after))}" +
                   $"{(wrong.Count == 0 ? "" : "; wrong injection: " + string.Join(", ", wrong))}{error}";
        }

        private sealed class Ctx {
            public ProgramModel P;
            public bool Instrument;

            public string K(string name) => Instrument ? "global::KenseiECS." + name : name;

            public string T(string type) => Instrument || type == null ? type : type.Replace("global::StandShared.", "").Replace("global::KenseiECS.", "");

            public string Hit(ClassModel c, string what) {
                return Instrument
                    ? $"global::StandShared.Probe.Hit(this, \"{what}:{c.Id}\");"
                    : $"global::ReproLog.Hit(this, \"{c.Plain}.{what}\");";
            }
        }

        /// <summary> Name of a class with its namespace and containing types, without type parameters (Game.AI.MoveSystem). </summary>
        public static string DisplayName(ProgramModel p, ClassModel c) {
            var parts = new List<string>();
            for (var x = c; x != null; x = p.Cls(x.NestedIn)) {
                parts.Insert(0, x.Plain);
                if (x.NestedIn == null) {
                    for (int i = x.Outers.Count - 1; i >= 0; i--) {
                        parts.Insert(0, x.Outers[i].Name.TrimStart('@'));
                    }
                    if (!string.IsNullOrEmpty(x.Ns)) {
                        parts.Insert(0, x.Ns.Replace("@", ""));
                    }
                }
            }
            return string.Join(".", parts);
        }

        /// <summary>
        /// The hint name the generator gives the class: <c>symbol.ToDisplayString()</c> with &lt; &gt; , and
        /// spaces replaced by '_', plus ".Injection.g.cs" (the same in 2.1.1 and 2.2.0).
        /// </summary>
        public static string HintOf(ProgramModel p, ClassModel c) {
            var sb = new StringBuilder();
            if (c.NestedIn != null && p.Cls(c.NestedIn) is ClassModel outer) {
                sb.Append(HintOf(p, outer).Replace(".Injection.g.cs", "")).Append('.');
            } else {
                if (!string.IsNullOrEmpty(c.Ns)) {
                    sb.Append(c.Ns).Append('.');
                }
                foreach (var o in c.Outers) {
                    sb.Append(o.Name);
                    if (!string.IsNullOrEmpty(o.TypeParams)) {
                        sb.Append('_').Append(o.TypeParams.Replace(",", "_").Replace(" ", "_")).Append('_');
                    }
                    sb.Append('.');
                }
            }
            sb.Append(c.Name);
            if (!string.IsNullOrEmpty(c.TypeParams)) {
                sb.Append('_').Append(c.TypeParams.Replace(",", "_").Replace(" ", "_")).Append('_');
            }
            return sb + ".Injection.g.cs";
        }

        private static string OuterKey(ClassModel c) => string.Join("/", c.Outers.Select(o => $"{o.Kind}:{o.Name}:{o.Partial}:{o.TypeParams}:{o.Modifiers}"));

        private static int OpenNamespace(StringBuilder sb, string ns) {
            if (string.IsNullOrEmpty(ns)) {
                return 0;
            }
            sb.AppendLine($"namespace {ns} {{");
            return 1;
        }

        private static void CloseNamespace(StringBuilder sb, string ns) {
            if (!string.IsNullOrEmpty(ns)) {
                sb.AppendLine("}");
            }
            sb.AppendLine();
        }

        private static string Pad(int indent) => new string(' ', indent * 4);

        /// <summary> Fully qualified reference to a model class from code outside it (outer type parameters by name). </summary>
        public static string TypeRef(ProgramModel p, ClassModel c, string args, bool instrument = true) {
            var sb = new StringBuilder();
            if (c.NestedIn != null) {
                sb.Append(TypeRef(p, p.Cls(c.NestedIn), null, instrument)).Append('.');
            } else {
                sb.Append("global::");
                if (!string.IsNullOrEmpty(c.Ns)) {
                    sb.Append(c.Ns).Append('.');
                }
                foreach (var o in c.Outers) {
                    sb.Append(o.Name);
                    if (!string.IsNullOrEmpty(o.TypeParams)) {
                        sb.Append('<').Append(o.TypeParams).Append('>');
                    }
                    sb.Append('.');
                }
            }
            sb.Append(c.Name);
            if (!string.IsNullOrEmpty(args)) {
                sb.Append('<').Append(instrument ? args : args.Replace("global::StandShared.", "")).Append('>');
            }
            return sb.ToString();
        }

        public static string IfaceRef(InterfaceModel i) => "global::" + (string.IsNullOrEmpty(i.Ns) ? "" : i.Ns + ".") + i.Name;

        private static void RenderPart(StringBuilder sb, Ctx ctx, ClassModel c, int file, int indent) {
            var p = ctx.P;
            string pad = Pad(indent);
            if (c.Define != null) {
                sb.AppendLine($"#if {c.Define}");
            }
            if (file == 0 && ctx.Instrument) {
                sb.AppendLine($"{pad}[global::StandShared.ClsId(\"{c.Id}\")]");
            }
            string mods = "public ";
            if (file == 0 && c.Abstract) {
                mods += "abstract ";
            }
            if (file == 0 && c.Sealed) {
                mods += "sealed ";
            }
            if (c.Partial) {
                mods += "partial ";
            }
            string tp = string.IsNullOrEmpty(c.TypeParams) ? "" : "<" + c.TypeParams + ">";
            string bases = file == 0 ? BaseList(ctx, c) : "";
            string constraints = file == 0 && !string.IsNullOrEmpty(c.TypeParams)
                ? string.Concat(c.TypeParams.Split(',').Select(t => $" where {t.Trim()} : struct, {ctx.K("IComponent")}"))
                : "";
            sb.AppendLine($"{pad}{mods}{c.Kind} {c.Name}{tp}{bases}{constraints} {{");
            string inner = Pad(indent + 1);
            foreach (var f in c.Fields.Where(x => Math.Min(Math.Max(x.Part, 0), c.Parts - 1) == file)) {
                sb.AppendLine(inner + RenderField(ctx, f));
            }
            if (file == 0) {
                RenderInit(sb, ctx, c, inner);
                RenderHook(sb, ctx, c, inner);
                if (NeedsRun(p, c)) {
                    sb.AppendLine($"{inner}public void Run({ctx.K("World")} world) {{ }}");
                }
                foreach (var nested in p.Classes.Where(x => x.NestedIn == c.Id)) {
                    RenderPart(sb, ctx, nested, 0, indent + 1);
                }
            }
            if (file == c.Parts - 1 && c.OnInit) {
                string call = c.OnInitCallsBase ? "base.Init(world, shared); " : "";
                sb.AppendLine($"{inner}partial void OnInit({ctx.K("World")} world, {ctx.K("SharedData")} shared) {{ {call}{ctx.Hit(c, "OnInit")} }}");
            }
            sb.AppendLine($"{pad}}}");
            if (c.Define != null) {
                sb.AppendLine("#endif");
            }
        }

        private static string BaseList(Ctx ctx, ClassModel c) {
            var p = ctx.P;
            var items = new List<string>();
            var b = p.BaseOf(c);
            if (c.BaseRef != null) {
                items.Add(ctx.Instrument ? c.BaseRef : c.BaseRef.Replace("global::StandShared.", ""));
            } else if (b != null) {
                items.Add(TypeRef(p, b, c.BaseArgs, ctx.Instrument));
            }
            if (c.ListsInit) {
                items.Add(ctx.K("IInitSystem"));
            }
            if (c.ListsRun) {
                items.Add(ctx.K("IRunSystem"));
            }
            if (c.IfaceId != null) {
                items.Add(IfaceRef(p.Iface(c.IfaceId)));
            }
            return items.Count == 0 ? "" : " : " + string.Join(", ", items);
        }

        private static string Attr(string name, string spelling) => spelling switch {
            "long" => name + "Attribute",
            "qualified" => "KenseiECS." + name,
            "global" => "global::KenseiECS." + name,
            "alias" => "Inj" + name,
            _ => name,
        };

        private static string Typeofs(Ctx ctx, IEnumerable<string> types) => string.Join(", ", types.Select(t => $"typeof({ctx.T(t)})"));

        /// <summary> The foreign attribute that FieldModel.Foreign puts in front of the injection attribute. </summary>
        public const string ForeignAttribute = "System.NonSerialized";

        private static string RenderField(Ctx ctx, FieldModel f) {
            string q = f.Nullable ? "?" : "";
            // The attributes without brackets; FieldModel.AttrList and Foreign decide how they are bracketed.
            var items = new List<string>();
            string type;
            switch (f.Kind) {
                case FieldKind.Filter:
                    if (f.Inc.Count > 0) {
                        items.Add(f.Inc.Count == 1 && f.Inc[0] == "" ? Attr("Inc", f.Spelling) : $"{Attr("Inc", f.Spelling)}({Typeofs(ctx, f.Inc)})");
                    }
                    if (f.Exc.Count > 0) {
                        items.Add($"{Attr("Exc", f.Spelling)}({Typeofs(ctx, f.Exc)})");
                    }
                    if (f.Any.Count > 0) {
                        items.Add($"{Attr("Any", f.Spelling)}({Typeofs(ctx, f.Any)})");
                    }
                    type = ctx.K("Filter") + q;
                    break;
                case FieldKind.Pool:
                    items.Add(Attr("Pool", f.Spelling));
                    type = $"{ctx.K("ComponentPool")}<{ctx.T(f.Type)}>{q}";
                    break;
                case FieldKind.Group:
                    items.Add(Attr("Group", f.Spelling));
                    type = $"{ctx.K("Group")}<{string.Join(", ", f.Type.Split(',').Select(t => ctx.T(t.Trim())))}>{q}";
                    break;
                default:
                    items.Add(f.Key == null ? Attr("Shared", f.Spelling) : $"{Attr("Shared", f.Spelling)}({SymbolDisplay.FormatLiteral(f.Key, true)})");
                    type = ctx.T(f.Type) + (f.Nullable && !f.Type.EndsWith("?") ? "?" : "");
                    break;
            }
            string attrs;
            if (f.Foreign == "list") {
                attrs = "[" + string.Join(", ", new[] { ForeignAttribute }.Concat(items)) + "]";
            } else {
                attrs = f.AttrList && items.Count > 1 ? "[" + string.Join(", ", items) + "]" : string.Concat(items.Select(x => "[" + x + "]"));
                if (f.Foreign == "before") {
                    attrs = "[" + ForeignAttribute + "] " + attrs;
                }
            }
            if (f.WrongType != null) {
                type = ctx.T(f.WrongType);
            }
            string mods = f.Access + (f.Static ? " static" : "") + (f.Readonly ? " readonly" : "");
            return $"{attrs} {mods} {type} {f.Name};";
        }

        /// <summary> The nearest ancestor that declares an exact Init(World, SharedData) method or gets one from the generator. </summary>
        public static (ClassModel Owner, InitKind Kind) NearestInit(ProgramModel p, ClassModel c) {
            foreach (var a in p.Ancestors(c)) {
                if (a.IsGenerated && p.IsActive(a)) {
                    return (a, InitKind.None);
                }
                if (Contract.IsExactInitMethod(a.Init)) {
                    return (a, a.Init);
                }
            }
            return (null, InitKind.None);
        }

        private static void RenderInit(StringBuilder sb, Ctx ctx, ClassModel c, string pad) {
            string w = ctx.K("World"), s = ctx.K("SharedData");
            string sig = $"Init({w} world, {s} shared)";
            string hook = c.Hook == HookKind.Virtual || c.Hook == HookKind.NonVirtual ? " OnInit(world, shared);" : "";
            string hit = ctx.Hit(c, "Init");
            var nearest = NearestInit(ctx.P, c);
            string baseCall = nearest.Kind == InitKind.PublicAbstract ? "" : "base.Init(world, shared); ";
            string explicitSig = $"void {ctx.K("IInitSystem")}.Init({w} world, {s} shared)";
            if (c.InitNextToFields) {
                sb.AppendLine($"{pad}public void {sig} {{ {hit} }}");
            }
            if (c.ExplicitInitNextToFields) {
                sb.AppendLine($"{pad}{explicitSig} {{ {hit} }}");
            }
            switch (c.Init) {
                case InitKind.PublicVirtual:
                    sb.AppendLine($"{pad}public virtual void {sig} {{ {hit}{hook} }}");
                    break;
                case InitKind.PublicAbstract:
                    sb.AppendLine($"{pad}public abstract void {sig};");
                    break;
                case InitKind.PublicNonVirtual:
                    sb.AppendLine($"{pad}public {(nearest.Owner != null ? "new " : "")}void {sig} {{ {hit}{hook} }}");
                    break;
                case InitKind.PublicNewVirtual:
                    sb.AppendLine($"{pad}public new virtual void {sig} {{ {hit}{hook} }}");
                    break;
                case InitKind.PublicNewNonVirtual:
                    sb.AppendLine($"{pad}public new void {sig} {{ {hit}{hook} }}");
                    break;
                case InitKind.PublicNewForward:
                    sb.AppendLine($"{pad}public new void {sig} {{ {(Contract.CanCallBase(ctx.P, c) ? baseCall : "")}{hit}{hook} }}");
                    break;
                case InitKind.ExplicitForward:
                    sb.AppendLine($"{pad}protected virtual void {sig} {{ {ctx.Hit(c, "InitV")}{hook} }}");
                    sb.AppendLine($"{pad}{explicitSig} {{ {ctx.Hit(c, "InitX")} Init(world, shared); }}");
                    break;
                case InitKind.OverloadParams:
                    sb.AppendLine($"{pad}public void Init({w} world, params {s}[] shared) {{ {ctx.Hit(c, "InitO")} }}");
                    break;
                case InitKind.PublicOverride:
                    sb.AppendLine($"{pad}public override void {sig} {{ {baseCall}{hit}{hook} }}");
                    break;
                case InitKind.PublicOverrideNoBase:
                    sb.AppendLine($"{pad}public override void {sig} {{ {hit}{hook} }}");
                    break;
                case InitKind.SealedOverride:
                    sb.AppendLine($"{pad}public sealed override void {sig} {{ {baseCall}{hit}{hook} }}");
                    break;
                case InitKind.Explicit:
                    sb.AppendLine($"{pad}{explicitSig} {{ {hit}{hook} }}");
                    break;
                case InitKind.VirtualThenExplicit:
                    sb.AppendLine($"{pad}public virtual void {sig} {{ {ctx.Hit(c, "InitV")}{hook} }}");
                    sb.AppendLine($"{pad}{explicitSig} {{ {ctx.Hit(c, "InitX")} }}");
                    break;
                case InitKind.ExplicitThenVirtual:
                    sb.AppendLine($"{pad}{explicitSig} {{ {ctx.Hit(c, "InitX")} }}");
                    sb.AppendLine($"{pad}public virtual void {sig} {{ {ctx.Hit(c, "InitV")}{hook} }}");
                    break;
                case InitKind.OverloadObject:
                    sb.AppendLine($"{pad}public void Init({w} world, object shared) {{ {ctx.Hit(c, "InitO")} }}");
                    break;
                case InitKind.OverloadOptional:
                    sb.AppendLine($"{pad}public void Init({w} world, {s} shared, int extra = 0) {{ {ctx.Hit(c, "InitO")} }}");
                    break;
                case InitKind.GenericHelper:
                    sb.AppendLine($"{pad}protected void Init<TX>({w} world, {s} shared) {{ {ctx.Hit(c, "InitO")} }}");
                    break;
                case InitKind.InParam:
                    sb.AppendLine($"{pad}public void Init(in {w} world, {s} shared) {{ {ctx.Hit(c, "InitO")} }}");
                    break;
                case InitKind.StaticInit:
                    sb.AppendLine($"{pad}public static void {sig} {{ }}");
                    break;
                case InitKind.FieldNamedInit:
                    sb.AppendLine($"{pad}public bool Init;");
                    break;
                case InitKind.PropertyNamedInit:
                    sb.AppendLine($"{pad}public bool Init {{ get; set; }}");
                    break;
            }
        }

        private static void RenderHook(StringBuilder sb, Ctx ctx, ClassModel c, string pad) {
            string sig = $"OnInit({ctx.K("World")} world, {ctx.K("SharedData")} shared)";
            switch (c.Hook) {
                case HookKind.Virtual:
                    sb.AppendLine($"{pad}protected virtual void {sig} {{ {ctx.Hit(c, "Hook")} }}");
                    break;
                case HookKind.NonVirtual:
                    sb.AppendLine($"{pad}protected void {sig} {{ {ctx.Hit(c, "Hook")} }}");
                    break;
                case HookKind.Override:
                    sb.AppendLine($"{pad}protected override void {sig} {{ base.OnInit(world, shared); {ctx.Hit(c, "HookOv")} }}");
                    break;
            }
        }

        /// <summary> The first class of a chain that lists IRunSystem (directly or through a model interface) declares Run. </summary>
        private static bool NeedsRun(ProgramModel p, ClassModel c) {
            bool listsRun(ClassModel x) => x.ListsRun || (x.IfaceId != null && p.Iface(x.IfaceId)?.IncludesRun == true);
            return listsRun(c) && !p.Ancestors(c).Any(listsRun) && c.BaseRef == null;
        }

        /// <summary> One-line description of a class for the layout table. </summary>
        public static string Describe(ProgramModel p, ClassModel c) {
            var parts = new List<string>();
            parts.Add(c.IsGenerated ? $"gen[{c.Fields.Count}]" : "hand");
            if (c.Init != InitKind.None) {
                parts.Add("Init=" + c.Init);
            }
            if (c.ListsInit) {
                parts.Add("lists IInitSystem");
            }
            if (c.IfaceId != null) {
                parts.Add("lists " + p.Iface(c.IfaceId)?.Name);
            }
            if (c.OnInit) {
                parts.Add(c.OnInitCallsBase ? "OnInit{base.Init}" : "OnInit");
            }
            if (c.Hook != HookKind.None) {
                parts.Add("hook " + c.Hook);
            }
            if (c.Asm == Asm.Lib) {
                parts.Add("Lib");
            }
            if (c.Define != null) {
                parts.Add("#if " + c.Define);
            }
            if (c.Abstract) {
                parts.Add("abstract");
            }
            if (!string.IsNullOrEmpty(c.TypeParams)) {
                parts.Add("<" + c.TypeParams + ">");
            }
            if (c.Parts > 1) {
                parts.Add(c.Parts + " parts");
            }
            if (c.Outers.Count > 0) {
                parts.Add("in " + string.Join(".", c.Outers.Select(o => o.Name)));
            }
            return $"{c.Plain}({string.Join(", ", parts)})";
        }
    }
}
