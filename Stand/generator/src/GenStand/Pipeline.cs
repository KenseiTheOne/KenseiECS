using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using KenseiECS;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using StandShared;

namespace KenseiStand.Generator {
    /// <summary> A compiler or generator diagnostic, reduced to what the oracle and the reports need. </summary>
    public sealed class Diag {
        public string Id;
        public string Severity;
        public string Message;
        public string File;
        public int Line;
        /// <summary> The diagnostic is in a generated tree. </summary>
        public bool InGenerated;
        /// <summary> Model id of the innermost class around the location. </summary>
        public string ClassId;
        /// <summary> Name of that class (also known for the clean repro form, which has no model ids). </summary>
        public string ClassName;
        /// <summary> Identifier at the location (the member a hiding warning is about, for example). </summary>
        public string Token;
        /// <summary> The same error was there before the generator ran (user code that needs generated members). </summary>
        public bool Unresolved;

        public override string ToString() => $"{Id} {Severity} {File}:{Line} [{ClassId}] {Message}";
    }

    public sealed class UnitResult {
        public Asm Asm;
        public List<SourceFile> Files = new();
        /// <summary> Errors of the program compiled without the generator that generated code cannot explain: the program itself is broken. </summary>
        public List<Diag> InvalidErrors = new();
        /// <summary>
        /// Every error of the program compiled without the generator ("CS0115@K2"), explained ones included: a reduced
        /// program must not have one its original did not have (the minimizer keeps a repro valid C# without the generator).
        /// </summary>
        public List<string> PreErrors = new();
        public List<Diag> GeneratorDiagnostics = new();
        public string GeneratorException;
        public List<SourceFile> Generated = new();
        public List<Diag> Compile = new();
        public byte[] Image;
        public double GeneratorMs;

        public bool Invalid => InvalidErrors.Count > 0;
        public bool GeneratorErrors => GeneratorDiagnostics.Any(d => d.Severity == "Error");
    }

    public sealed class RunObservation {
        public string ClassId;
        public bool IsInitSystem;
        public string Exception;
        public List<ProbeEvent> Events = new();
        public List<string> NullAfter = new();
        public List<string> Chain = new();
        /// <summary> Injected fields that hold something other than what the documented call returns. </summary>
        public List<WrongValue> Wrong = new();
    }

    /// <summary> An injected field whose value is not the object the documented call returns on the same World / SharedData. </summary>
    public sealed class WrongValue {
        /// <summary> "ClsId.field". </summary>
        public string Field;
        /// <summary> Filter, Pool, Group or Shared. </summary>
        public string Kind;
        /// <summary> Name-free mechanism: exc-dropped, inc-dropped, any-dropped, any-as-inc, constraints-differ, key-ignored, other-key, other-instance... </summary>
        public string Mechanism;
        public string Expected;
        public string Actual;
    }

    public sealed class Observation {
        public UnitResult Lib;
        public UnitResult Main;
        public List<RunObservation> Runs = new();
        public string RuntimeError;
        /// <summary> Clean repro form only: what ReproMain.Run() returned. </summary>
        public string ReproOutput;
        public bool Invalid => (Lib?.Invalid ?? false) || (Main?.Invalid ?? false);
        public IEnumerable<UnitResult> Units => new[] { Lib, Main }.Where(u => u != null);

        /// <summary> The errors of both assemblies compiled without the generator (UnitResult.PreErrors). </summary>
        public HashSet<string> PreErrors => new(Units.SelectMany(u => u.PreErrors));
    }

    public sealed class Settings {
        public string Profile = "2.2";
        public bool Debug;
        /// <summary> The claims of the tree under test (the versioned contract profile). </summary>
        public ClaimSet Claims;
    }

    /// <summary>
    /// Generator run, compilation, emit into memory, load into a collectible AssemblyLoadContext and
    /// SystemsRunner.Init with a World and SharedData, as a Unity project would do with the two
    /// assemblies of the program.
    /// </summary>
    public static class Pipeline {
        // Errors that generated members resolve: OnInit without its declaration, IInitSystem listed
        // without an Init (CS0535/CS0736/CS0737/CS0738), an override or base.Init of a generated Init.
        private static readonly string[] Explained = { "CS0759", "CS0535", "CS0736", "CS0737", "CS0738", "CS0115", "CS0117", "CS1061", "CS0534" };

        // Errors of an override, `new` or base.Init in class X that bind, before the generator ran,
        // to an Init member above a generated class (abstract, generic, static, a field...). Once the
        // generated Init exists it is found first, so they count as resolvable only when the model
        // says the nearest Init member above X is a generated class (see ResolvedByGeneratedInit).
        private static readonly string[] BindingErrors = { "CS0205", "CS0411", "CS1955", "CS0176", "CS0505", "CS0506", "CS0239", "CS0507", "CS0533" };

        /// <summary> Whether the first class above <paramref name="c"/> with an Init member that an override or a base.Init call would reach is a generated class. </summary>
        public static bool ResolvedByGeneratedInit(ProgramModel p, ClassModel c) {
            foreach (var a in p.Ancestors(c)) {
                if (a.IsGenerated && p.IsActive(a)) {
                    return true;
                }
                switch (a.Init) {
                    // Passed over by override lookup (another signature) and by a base.Init call that
                    // could not bind to them (a non-inferable generic helper, an explicit implementation).
                    case InitKind.None:
                    case InitKind.Explicit:
                    case InitKind.GenericHelper:
                    case InitKind.InParam:
                    case InitKind.OverloadObject:
                    case InitKind.OverloadOptional:
                    case InitKind.OverloadParams:
                        continue;
                    default:
                        return false;
                }
            }
            return false;
        }
        private static readonly Lazy<List<MetadataReference>> BaseRefs = new(() => {
            string dir = Path.GetDirectoryName(typeof(object).Assembly.Location);
            var refs = new List<MetadataReference> {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(Path.Combine(dir, "System.Runtime.dll")),
                MetadataReference.CreateFromFile(Path.Combine(dir, "System.Collections.dll")),
                MetadataReference.CreateFromFile(Path.Combine(dir, "netstandard.dll")),
                MetadataReference.CreateFromFile(typeof(World).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Probe).Assembly.Location),
            };
            return refs;
        });

        private static readonly Lazy<List<MetadataReference>> CleanRefs = new(() =>
            BaseRefs.Value.Where(r => !(r is PortableExecutableReference pe) || Path.GetFileName(pe.FilePath) != "StandShared.dll").ToList());

        // The runtime part (World, SystemsRunner) runs one program at a time; compilation runs in parallel.
        private static readonly object RuntimeLock = new();

        private static readonly Lazy<Assembly> GeneratorAssembly = new(() => Assembly.Load(new AssemblyName("KenseiECS.Generators")));

        public static string GeneratorLocation => GeneratorAssembly.Value.Location;

        /// <summary> Every [Generator] type of the generator assembly, incremental ones wrapped. </summary>
        public static ISourceGenerator[] CreateGenerators() {
            var result = new List<ISourceGenerator>();
            foreach (var type in GeneratorAssembly.Value.GetTypes()) {
                if (type.IsAbstract || type.GetCustomAttributes().All(a => a.GetType().Name != "GeneratorAttribute")) {
                    continue;
                }
                object instance = Activator.CreateInstance(type);
                if (instance is ISourceGenerator source) {
                    result.Add(source);
                    continue;
                }
#if !STAND_ROSLYN3
                // Incremental generators (Roslyn 4+); a Roslyn 3.8 driver build has no such interface.
                if (instance is IIncrementalGenerator incremental) {
                    result.Add(incremental.AsSourceGenerator());
                }
#endif
            }
            if (result.Count == 0) {
                throw new InvalidOperationException("no [Generator] type in " + GeneratorAssembly.Value.Location);
            }
            return result.ToArray();
        }

        public static Observation Execute(ProgramModel p, Settings settings) => Execute(p, settings, instrument: true);

        /// <summary> The clean repro form of the program: compiled the same way, then ReproMain.Run() instead of the stand's own runs. </summary>
        public static Observation ExecuteClean(ProgramModel p, Settings settings) => Execute(p, settings, instrument: false);

        private static Observation Execute(ProgramModel p, Settings settings, bool instrument) {
            var obs = new Observation();
            byte[] libImage = null;
            var extra = new List<MetadataReference>();
            var defines = settings.Debug ? new[] { "KENSEI_DEBUG" } : Array.Empty<string>();
            var asms = Renderer.Assemblies(p);
            if (asms.Contains(Asm.Lib)) {
                obs.Lib = RunUnit(p, Asm.Lib, "Lib", extra, p.LibDefines.Concat(defines).ToArray(), instrument);
                if (obs.Lib.Invalid || obs.Lib.Image == null) {
                    return obs;
                }
                libImage = obs.Lib.Image;
                extra.Add(MetadataReference.CreateFromImage(libImage));
            }
            if (asms.Contains(Asm.Main)) {
                obs.Main = RunUnit(p, Asm.Main, "Main", extra, p.MainDefines.Concat(defines).ToArray(), instrument);
                if (obs.Main.Invalid || (obs.Main.Image == null && libImage == null)) {
                    return obs;
                }
            }
            lock (RuntimeLock) {
                try {
                    if (instrument) {
                        RunSystems(p, obs, libImage, obs.Main?.Image);
                    } else {
                        RunReproMain(obs, libImage, obs.Main?.Image);
                    }
                } catch (Exception e) {
                    obs.RuntimeError = e.GetType().Name + ": " + e.Message;
                }
            }
            return obs;
        }

        private static UnitResult RunUnit(ProgramModel p, Asm asm, string name, List<MetadataReference> extra, string[] defines, bool instrument) {
            var unit = new UnitResult { Asm = asm, Files = Renderer.Render(p, asm, instrument) };
            var parse = new CSharpParseOptions(LanguageVersion.CSharp9, preprocessorSymbols: defines);
            var trees = unit.Files.Select(f => CSharpSyntaxTree.ParseText(f.Text, parse, path: f.Path)).ToList();
            var refs = (instrument ? BaseRefs.Value : CleanRefs.Value).Concat(extra).ToList();
            var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Disable, allowUnsafe: true,
                concurrentBuild: false);
            var compilation = CSharpCompilation.Create(name, trees, refs, options);

            // Without the generator every error must be one that generated members resolve; anything
            // else means the stand produced a broken program, which is never reported as a finding.
            var before = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            var beforeKeys = new HashSet<string>(before.Select(Key));
            foreach (var d in before) {
                var diag = ToDiag(compilation, d, trees);
                unit.PreErrors.Add(diag.Id + "@" + (diag.ClassId ?? diag.ClassName ?? "?"));
                var owner = p.Cls(diag.ClassId) ?? p.Classes.FirstOrDefault(c => c.Asm == asm && c.Plain == diag.ClassName);
                // A missing OnInit declaration, Init or override target is explained by the generator only
                // in a class it extends, or below a generated class whose Init would be found first.
                bool generated = owner != null && owner.IsGenerated && p.IsActive(owner);
                if (Explained.Contains(d.Id) && (generated || (owner != null && ResolvedByGeneratedInit(p, owner)))) {
                    continue;
                }
                if (BindingErrors.Contains(d.Id) && owner != null && ResolvedByGeneratedInit(p, owner)) {
                    continue;
                }
                unit.InvalidErrors.Add(diag);
            }
            if (unit.Invalid) {
                return unit;
            }

            var sw = Stopwatch.StartNew();
            GeneratorDriver driver = CSharpGeneratorDriver.Create(CreateGenerators(), parseOptions: parse);
            driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var genDiagnostics);
            unit.GeneratorMs = sw.Elapsed.TotalMilliseconds;
            var run = driver.GetRunResult();
            foreach (var r in run.Results) {
                if (r.Exception != null) {
                    unit.GeneratorException = r.Exception.GetType().Name + ": " + StripPaths(r.Exception.Message);
                }
                foreach (var g in r.GeneratedSources) {
                    unit.Generated.Add(new SourceFile { Path = g.HintName, Text = g.SourceText.ToString() });
                }
            }
            foreach (var d in genDiagnostics) {
                unit.GeneratorDiagnostics.Add(ToDiag(output, d, trees));
            }
            foreach (var d in output.GetDiagnostics()) {
                if (d.Severity != DiagnosticSeverity.Error && d.Severity != DiagnosticSeverity.Warning) {
                    continue;
                }
                var diag = ToDiag(output, d, trees);
                if (!diag.InGenerated && d.Severity == DiagnosticSeverity.Warning) {
                    // Warnings in the program's own code (unused fields and the like) are not the generator's.
                    continue;
                }
                diag.Unresolved = !diag.InGenerated && beforeKeys.Contains(Key(d));
                unit.Compile.Add(diag);
            }
            if (unit.Compile.Any(d => d.Severity == "Error") || unit.GeneratorErrors) {
                return unit;
            }
            using var stream = new MemoryStream();
            var emit = output.Emit(stream);
            if (!emit.Success) {
                foreach (var d in emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)) {
                    unit.Compile.Add(ToDiag(output, d, trees));
                }
                return unit;
            }
            unit.Image = stream.ToArray();
            return unit;
        }

        private static string Key(Diagnostic d) => d.Id + "@" + d.Location.SourceTree?.FilePath + ":" + d.Location.SourceSpan.Start;

        /// <summary> Stack frames of a generator exception without the build machine's folders ("in File.cs:line 12"). </summary>
        public static string StripPaths(string text) =>
            text == null ? null : System.Text.RegularExpressions.Regex.Replace(text, @" in (?:[A-Za-z]:)?[\\/][^\r\n:]*[\\/]([^\\/\r\n:]+):line ", " in $1:line ");

        private static Diag ToDiag(Compilation compilation, Diagnostic d, List<SyntaxTree> userTrees) {
            var diag = new Diag { Id = d.Id, Severity = d.Severity.ToString(), Message = StripPaths(d.GetMessage()) };
            if (d.Location.IsInSource) {
                var tree = d.Location.SourceTree;
                var span = d.Location.GetLineSpan();
                diag.File = Path.GetFileName(span.Path);
                diag.Line = span.StartLinePosition.Line + 1;
                diag.InGenerated = !userTrees.Contains(tree);
                try {
                    var root = tree.GetRoot();
                    var token = root.FindToken(d.Location.SourceSpan.Start);
                    diag.Token = token.ValueText;
                    var decl = token.Parent?.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
                    if (decl != null && compilation.SyntaxTrees.Contains(tree)) {
                        var symbol = compilation.GetSemanticModel(tree).GetDeclaredSymbol(decl);
                        diag.ClassId = ClsIdOf(symbol);
                        diag.ClassName = symbol?.Name;
                    }
                } catch (Exception) {
                    // A location that cannot be mapped keeps ClassId null.
                }
            }
            return diag;
        }

        private static string ClsIdOf(ISymbol symbol) {
            if (symbol == null) {
                return null;
            }
            foreach (var a in symbol.GetAttributes()) {
                if (a.AttributeClass?.Name == "ClsIdAttribute" && a.ConstructorArguments.Length == 1) {
                    return a.ConstructorArguments[0].Value as string;
                }
            }
            return null;
        }

        private sealed class ProgramContext : AssemblyLoadContext {
            private readonly Dictionary<string, Assembly> _own = new();

            public ProgramContext() : base("stand-" + Guid.NewGuid().ToString("N"), isCollectible: true) {
            }

            public Assembly Add(string name, byte[] image) {
                var assembly = LoadFromStream(new MemoryStream(image));
                _own[name] = assembly;
                return assembly;
            }

            protected override Assembly Load(AssemblyName name) {
                if (_own.TryGetValue(name.Name ?? "", out var own)) {
                    return own;
                }
                if (name.Name == "KenseiECS") {
                    return typeof(World).Assembly;
                }
                if (name.Name == "StandShared") {
                    return typeof(Probe).Assembly;
                }
                return null;
            }
        }

        private static void RunSystems(ProgramModel p, Observation obs, byte[] libImage, byte[] mainImage) {
            var context = new ProgramContext();
            try {
                var types = new Dictionary<string, Type>();
                foreach (var assembly in Load(context, libImage, mainImage)) {
                    foreach (var type in assembly.GetTypes()) {
                        var id = type.GetCustomAttribute<ClsIdAttribute>(false)?.Id;
                        if (id != null) {
                            types[id] = type;
                        }
                    }
                }
                // In model order, the same classes ReproMain of the clean form runs (only those of Lib
                // when Main does not build).
                foreach (var model in Renderer.RunnableClasses(p).Where(c => mainImage != null || c.Asm == Asm.Lib)) {
                    if (!types.TryGetValue(model.Id, out var type) || type.IsAbstract || type.ContainsGenericParameters || !type.IsClass) {
                        continue;
                    }
                    obs.Runs.Add(RunOne(p, type, model));
                }
            } finally {
                context.Unload();
            }
        }

        private static List<Assembly> Load(ProgramContext context, byte[] libImage, byte[] mainImage) {
            var assemblies = new List<Assembly>();
            if (libImage != null) {
                assemblies.Add(context.Add("Lib", libImage));
            }
            if (mainImage != null) {
                assemblies.Add(context.Add("Main", mainImage));
            }
            return assemblies;
        }

        private static void RunReproMain(Observation obs, byte[] libImage, byte[] mainImage) {
            var context = new ProgramContext();
            try {
                var assemblies = Load(context, libImage, mainImage);
                var main = assemblies.Select(a => a.GetType("ReproMain")).FirstOrDefault(t => t != null);
                if (main == null) {
                    obs.RuntimeError = "ReproMain not found";
                    return;
                }
                try {
                    obs.ReproOutput = (string)main.GetMethod("Run").Invoke(null, null);
                } catch (Exception e) {
                    obs.RuntimeError = "ReproMain.Run threw " + Unwrap(e);
                }
            } finally {
                context.Unload();
            }
        }

        private static RunObservation RunOne(ProgramModel p, Type type, ClassModel model) {
            var run = new RunObservation { ClassId = model.Id };
            for (var t = type; t != null && t != typeof(object); t = t.BaseType) {
                var id = t.GetCustomAttribute<ClsIdAttribute>(false)?.Id;
                if (id != null) {
                    run.Chain.Add(id);
                }
            }
            object instance;
            try {
                instance = Activator.CreateInstance(type);
            } catch (Exception e) {
                run.Exception = "ctor: " + Unwrap(e);
                return run;
            }
            run.IsInitSystem = instance is IInitSystem;
            Probe.Reset();
            var world = new World();
            SharedData shared = null;
            try {
                shared = BuildShared(p);
                if (instance is ISystem system) {
                    new SystemsRunner(world, shared).Add(system).Init();
                }
            } catch (Exception e) {
                run.Exception = Unwrap(e);
            }
            run.Events = Probe.Events.ToList();
            Probe.Reset();
            for (var t = type; t != null && t != typeof(object); t = t.BaseType) {
                var c = p.Cls(t.GetCustomAttribute<ClsIdAttribute>(false)?.Id);
                if (c == null) {
                    continue;
                }
                foreach (var f in c.Fields.Where(f => !f.Static)) {
                    var info = t.GetField(f.Name.TrimStart('@'), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (info == null) {
                        continue;
                    }
                    object value = info.GetValue(instance);
                    if (value == null) {
                        run.NullAfter.Add(c.Id + "." + f.Name.TrimStart('@'));
                        continue;
                    }
                    if (f.WrongType != null || shared == null) {
                        continue;
                    }
                    WrongValue wrong;
                    try {
                        wrong = InjectionCheck.Check(f, info.FieldType, value, world, shared);
                    } catch (Exception) {
                        // An expectation the stand cannot build (an invalid filter of a negative layout) proves nothing.
                        wrong = null;
                    }
                    if (wrong != null) {
                        wrong.Field = c.Id + "." + f.Name.TrimStart('@');
                        run.Wrong.Add(wrong);
                    }
                }
            }
            try {
                world.Destroy();
            } catch (Exception) {
                // Not the generator's business.
            }
            return run;
        }

        private static string Unwrap(Exception e) {
            while (e is TargetInvocationException t && t.InnerException != null) {
                e = t.InnerException;
            }
            return e.GetType().Name + ": " + e.Message;
        }

        private static readonly MethodInfo SharedAdd = typeof(SharedData).GetMethods().First(m => m.Name == "Add" && m.IsGenericMethodDefinition
            && m.GetParameters().Length == 2 && m.GetParameters()[1].ParameterType == typeof(string));

        private static SharedData BuildShared(ProgramModel p) {
            var shared = new SharedData();
            var done = new HashSet<string>();
            foreach (var f in p.Classes.SelectMany(c => c.Fields).Where(f => f.Kind == FieldKind.Shared && f.WrongType == null)) {
                string typeName = f.Type.TrimEnd('?');
                if (!done.Add(typeName + (f.Key == null ? "|<null>" : "|=" + f.Key))) {
                    continue;
                }
                Type type;
                object value;
                // A distinct object per (type, key), so a value injected from another key is told apart.
                switch (typeName) {
                    case "string":
                        type = typeof(string);
                        value = new string('s', 1) + ":" + (f.Key ?? "<null>");
                        break;
                    case "object":
                        type = typeof(object);
                        value = new object();
                        break;
                    case "int[]":
                        type = typeof(int[]);
                        value = new int[1];
                        break;
                    default:
                        (type, value) = SpecialShared(typeName);
                        if (type != null) {
                            break;
                        }
                        type = typeof(Probe).Assembly.GetType(typeName.Replace("global::", ""));
                        if (type == null) {
                            continue;
                        }
                        value = Activator.CreateInstance(type);
                        break;
                }
                SharedAdd.MakeGenericMethod(type).Invoke(shared, new[] { value, f.Key });
            }
            return shared;
        }

        /// <summary> The [Shared] types of SharedKinds: the type a field asks for and the instance registered under it. </summary>
        private static (Type Type, object Value) SpecialShared(string typeName) => typeName switch {
            SharedKinds.Iface => (typeof(ICfg), new CfgImpl()),
            SharedKinds.Abstract => (typeof(CfgBase), new CfgDerived()),
            SharedKinds.Generic => (typeof(Box<int>), new Box<int>()),
            SharedKinds.Nested => (typeof(SharedHolder.Inner), new SharedHolder.Inner()),
            _ => (null, null),
        };
    }

    /// <summary>
    /// What an injected field must hold, per the "Injected with" table of docs/guides/source-generator.md,
    /// computed on the same World and SharedData after SystemsRunner.Init: the filter that
    /// <c>world.Filter().Inc&lt;...&gt;().Exc&lt;...&gt;().Any&lt;...&gt;().End()</c> returns for the field's
    /// attributes (End returns the existing filter for identical constraints), <c>world.Pool&lt;T&gt;()</c>,
    /// <c>world.Group&lt;...&gt;()</c> and <c>shared.Get&lt;T&gt;(key)</c>. Everything goes through reflection,
    /// so the same code builds against every package version.
    /// </summary>
    public static class InjectionCheck {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static WrongValue Check(FieldModel f, Type fieldType, object value, World world, SharedData shared) {
            switch (f.Kind) {
                case FieldKind.Filter:
                    return CheckFilter(f, value, world);
                case FieldKind.Pool: {
                    if (!fieldType.IsGenericType) {
                        return null;
                    }
                    var t = fieldType.GetGenericArguments()[0];
                    var method = typeof(World).GetMethods().First(m => m.Name == "Pool" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                    object expected = method.MakeGenericMethod(t).Invoke(world, null);
                    return ReferenceEquals(expected, value) ? null : new WrongValue {
                        Kind = "Pool", Mechanism = value.GetType() == expected.GetType() ? "other-instance" : "other-type",
                        Expected = $"world.Pool<{t.Name}>()", Actual = $"another {Describe(value)} (not the world's pool)",
                    };
                }
                case FieldKind.Group: {
                    if (!fieldType.IsGenericType) {
                        return null;
                    }
                    var args = fieldType.GetGenericArguments();
                    var method = typeof(World).GetMethods().FirstOrDefault(m => m.Name == "Group" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == args.Length && m.GetParameters().Length == 0);
                    if (method == null) {
                        return null;
                    }
                    object expected = method.MakeGenericMethod(args).Invoke(world, null);
                    return ReferenceEquals(expected, value) ? null : new WrongValue {
                        Kind = "Group", Mechanism = "other-instance",
                        Expected = $"world.Group<{string.Join(", ", args.Select(a => a.Name))}>()", Actual = $"another {Describe(value)}",
                    };
                }
                default: {
                    var get = typeof(SharedData).GetMethods().First(m => m.Name == "Get" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);
                    object Lookup(string key) {
                        try {
                            return get.MakeGenericMethod(fieldType).Invoke(shared, new object[] { key });
                        } catch (TargetInvocationException) {
                            return null;
                        }
                    }
                    object expected = Lookup(f.Key);
                    if (expected == null || ReferenceEquals(expected, value)) {
                        return null;
                    }
                    string mechanism = f.Key != null && ReferenceEquals(Lookup(null), value) ? "key-ignored" : "other-object";
                    return new WrongValue {
                        Kind = "Shared", Mechanism = mechanism,
                        Expected = f.Key == null ? $"shared.Get<{fieldType.Name}>()" : $"shared.Get<{fieldType.Name}>({Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(f.Key, true)})",
                        Actual = mechanism == "key-ignored" ? $"the object registered without a key (shared.Get<{fieldType.Name}>())" : "an object registered under another type or key",
                    };
                }
            }
        }

        private static Type ComponentOf(string name) => typeof(Probe).Assembly.GetType(name.Replace("global::", "").TrimEnd('?'));

        private static WrongValue CheckFilter(FieldModel f, object value, World world) {
            var inc = f.Inc.Where(t => t != "").Select(ComponentOf).ToList();
            var exc = f.Exc.Select(ComponentOf).ToList();
            var any = f.Any.Select(ComponentOf).ToList();
            if (inc.Concat(exc).Concat(any).Any(t => t == null || !typeof(IComponent).IsAssignableFrom(t)) || inc.Count + any.Count == 0) {
                return null;
            }
            object builder = typeof(World).GetMethods().First(m => m.Name == "Filter" && !m.IsGenericMethod && m.GetParameters().Length == 0).Invoke(world, null);
            var builderType = builder.GetType();
            foreach (var (name, types) in new[] { ("Inc", inc), ("Exc", exc), ("Any", any) }) {
                var method = builderType.GetMethods().First(m => m.Name == name && m.IsGenericMethodDefinition);
                foreach (var t in types) {
                    builder = method.MakeGenericMethod(t).Invoke(builder, null);
                }
            }
            object expected = builderType.GetMethod("End").Invoke(builder, null);
            if (ReferenceEquals(expected, value)) {
                return null;
            }
            var (ai, ae, aa) = Constraints(value);
            var (ei, ee, ea) = Constraints(expected);
            string mechanism;
            if (ai == null || ei == null) {
                mechanism = "other-instance";
            } else if (Same(ai, ei) && Same(ae, ee) && Same(aa, ea)) {
                mechanism = "other-instance";
            } else if (Same(ai, ei) && Same(aa, ea) && ae.IsSubsetOf(ee)) {
                mechanism = "exc-dropped";
            } else if (Same(ae, ee) && Same(aa, ea) && ai.IsSubsetOf(ei)) {
                mechanism = "inc-dropped";
            } else if (Same(ai, ei) && Same(ae, ee) && aa.IsSubsetOf(ea)) {
                mechanism = "any-dropped";
            } else if (Same(ae, ee) && aa.Count == 0 && Same(ai, new HashSet<int>(ei.Concat(ea)))) {
                mechanism = "any-as-inc";
            } else {
                mechanism = "constraints-differ";
            }
            return new WrongValue {
                Kind = "Filter", Mechanism = mechanism,
                Expected = "world.Filter()" + string.Concat(inc.Select(t => $".Inc<{t.Name}>()")) + string.Concat(exc.Select(t => $".Exc<{t.Name}>()")) + string.Concat(any.Select(t => $".Any<{t.Name}>()")) + ".End()",
                Actual = mechanism == "other-instance" ? "a filter with the same constraints that is not the world's (registered elsewhere)" : "a filter " + Describe(value),
            };
        }

        private static bool Same(HashSet<int> a, HashSet<int> b) => a.SetEquals(b);

        // Constraint sets of a filter (the internal type index arrays every package version has).
        private static (HashSet<int>, HashSet<int>, HashSet<int>) Constraints(object filter) {
            int[] Read(string name) => filter.GetType().GetField(name, Fields)?.GetValue(filter) as int[];
            var i = Read("IncludedTypeIndices");
            var e = Read("ExcludedTypeIndices");
            var a = Read("AnyTypeIndices");
            if (i == null || e == null || a == null) {
                return (null, null, null);
            }
            return (new HashSet<int>(i), new HashSet<int>(e), new HashSet<int>(a));
        }

        private static string TypeName(int index) {
            try {
                var nameOf = typeof(World).Assembly.GetType("KenseiECS.ComponentType")?.GetMethod("NameOf", BindingFlags.Public | BindingFlags.Static);
                return nameOf?.Invoke(null, new object[] { index }) as string ?? "#" + index;
            } catch (Exception) {
                return "#" + index;
            }
        }

        /// <summary> Filter Inc&lt;A, B&gt; Exc&lt;C&gt; for a filter, the type name otherwise. </summary>
        public static string Describe(object value) {
            if (value is Filter) {
                var (i, e, a) = Constraints(value);
                if (i == null) {
                    return "Filter";
                }
                string Part(string label, HashSet<int> set) => set.Count == 0 ? "" : $" {label}<{string.Join(", ", set.OrderBy(x => x).Select(TypeName))}>";
                return "Filter" + Part("Inc", i) + Part("Exc", e) + Part("Any", a);
            }
            var type = value.GetType();
            return type.IsGenericType ? type.Name.Split('`')[0] + "<" + string.Join(", ", type.GetGenericArguments().Select(t => t.Name)) + ">" : type.Name;
        }
    }
}
