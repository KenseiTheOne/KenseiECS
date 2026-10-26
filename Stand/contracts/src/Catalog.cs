using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KenseiECS;
using Stand.Contracts.Probe;

namespace Stand.Contracts {
    /// <summary> An API surface under test: a closed type, how a scenario provides an instance, and where it applies. </summary>
    public sealed class Target {
        public string Name;          // matrix label, e.g. ComponentPool<CA>
        public Type Type;
        public string Var;           // variable that holds the instance in repro code
        public Part Parts;           // setup parts the instance needs
        public string[] Scenarios;   // scenarios that make sense for it
    }

    /// <summary> One public method, property getter or constructor, closed over the stand's types. </summary>
    public sealed class Member {
        public Target Target;
        public MethodBase Method;
        public string Display;
        public string DocId;
        public string Name;          // simple name; the property name for a getter, the type name for a ctor
        public bool IsCtor;
        public bool IsStatic;
        public bool IsExtension;
        public bool IsGetter;
        public ParameterInfo[] Params;
        public ObsoleteAttribute Obsolete;
        public string WhyNotRun;

        public string Key => Target.Name + "|" + Display;
    }

    /// <summary>
    /// Reflection inventory of the public API of the targets the task lists (World, ComponentPool,
    /// Filter, FilterBuilder, CommandBuffer, SystemsRunner, Listeners, EventBuffer, SharedData,
    /// Group, the snapshot API), closed on the stand's probe types. A member added in a later version
    /// shows up in the matrix without any change here.
    /// </summary>
    public static class Catalog {
        public static readonly string[] AllScenarios = {
            Scenarios.Fresh, Scenarios.WorldDestroyed, Scenarios.RunnerDestroyed, Scenarios.RunnerReinit,
            Scenarios.StaleDead, Scenarios.StaleReused, Scenarios.NullEntity, Scenarios.Negative, Scenarios.Huge, Scenarios.NullArg
        };

        private static readonly string[] WorldScenarios = {
            Scenarios.Fresh, Scenarios.WorldDestroyed, Scenarios.StaleDead, Scenarios.StaleReused, Scenarios.NullEntity,
            Scenarios.Negative, Scenarios.Huge, Scenarios.NullArg
        };

        private static readonly string[] PlainScenarios = { Scenarios.Fresh, Scenarios.Negative, Scenarios.Huge, Scenarios.NullArg };

        public static List<Target> Targets() {
            return new List<Target> {
                new Target { Name = "World", Type = typeof(World), Var = "world", Parts = Part.World | Part.Entities, Scenarios = WorldScenarios },
                new Target { Name = "ComponentPool<CA>", Type = typeof(ComponentPool<CA>), Var = "poolA", Parts = Part.World | Part.Entities | Part.Pool, Scenarios = WorldScenarios },
                new Target { Name = "Filter", Type = typeof(Filter), Var = "fA", Parts = Part.World | Part.Entities | Part.Filters, Scenarios = WorldScenarios },
                new Target { Name = "FilterBuilder", Type = typeof(FilterBuilder), Var = "builder", Parts = Part.World | Part.Filters, Scenarios = new[] { Scenarios.Fresh, Scenarios.WorldDestroyed } },
                new Target { Name = "Group<CC, CD>", Type = typeof(Group<CC, CD>), Var = "groupCD", Parts = Part.World | Part.Entities | Part.Group, Scenarios = new[] { Scenarios.Fresh, Scenarios.WorldDestroyed, Scenarios.Negative, Scenarios.Huge } },
                new Target { Name = "CommandBuffer", Type = typeof(CommandBuffer), Var = "buffer", Parts = Part.World | Part.Entities | Part.Buffer, Scenarios = WorldScenarios },
                new Target { Name = "SystemsRunner", Type = typeof(SystemsRunner), Var = "runner", Parts = Part.World | Part.Shared | Part.Runner,
                    Scenarios = new[] { Scenarios.Fresh, Scenarios.WorldDestroyed, Scenarios.RunnerDestroyed, Scenarios.RunnerReinit, Scenarios.Negative, Scenarios.Huge, Scenarios.NullArg } },
                new Target { Name = "SharedData", Type = typeof(SharedData), Var = "shared", Parts = Part.Shared, Scenarios = PlainScenarios },
                new Target { Name = "WorldSerializer", Type = typeof(WorldSerializer), Var = "serializer", Parts = Part.World | Part.Entities | Part.Serializer,
                    Scenarios = new[] { Scenarios.Fresh, Scenarios.WorldDestroyed, Scenarios.NullArg } },
                new Target { Name = "Listeners<IProbeListener>", Type = typeof(Listeners<IProbeListener>), Var = "listeners", Parts = Part.Listeners, Scenarios = PlainScenarios },
                new Target { Name = "EventBuffer<Ev>", Type = typeof(EventBuffer<Ev>), Var = "events", Parts = Part.Events, Scenarios = PlainScenarios },
            };
        }

        public static List<Member> Members(Target target) {
            var list = new List<Member>();
            const BindingFlags inst = BindingFlags.Public | BindingFlags.Instance;
            const BindingFlags stat = BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var methods = target.Type.GetMethods(inst).Where(m => Relevant(m))
                .Concat(target.Type.GetMethods(stat).Where(m => Relevant(m)))
                .Cast<MethodBase>().ToList();
            if (target.Type == typeof(World)) {
                // Extension methods over World live in static classes of the package.
                foreach (var t in typeof(World).Assembly.GetTypes()) {
                    if (!t.IsPublic || !t.IsAbstract || !t.IsSealed || t.Namespace != "KenseiECS") {
                        continue;
                    }
                    foreach (var m in t.GetMethods(stat)) {
                        if (TypeNames.IsExtension(m) && m.GetParameters()[0].ParameterType == typeof(World)) {
                            methods.Add(m);
                        }
                    }
                }
            }
            methods.AddRange(target.Type.GetConstructors(inst));

            foreach (var raw in methods) {
                MethodBase closed = raw;
                string why = null;
                if (raw is MethodInfo mi && mi.IsGenericMethodDefinition) {
                    var args = ChooseTypeArguments(mi, out why);
                    if (args != null) {
                        try {
                            closed = mi.MakeGenericMethod(args);
                        } catch (ArgumentException e) {
                            why = "type arguments violate constraints: " + e.Message;
                        }
                    }
                }
                var member = Describe(target, closed, raw);
                if (why != null) {
                    member.WhyNotRun = why;
                }
                list.Add(member);
            }
            list.Sort((a, b) => string.CompareOrdinal(a.Display, b.Display));
            return list;
        }

        /// <summary> Member record for a (closed) method; raw is the declaration that carries the attributes. </summary>
        public static Member Describe(Target target, MethodBase closed, MethodBase raw) {
            var member = new Member { Target = target, Method = closed };
            member.IsCtor = closed is ConstructorInfo;
            member.IsStatic = closed.IsStatic && !member.IsCtor;
            member.IsExtension = closed is MethodInfo cm && TypeNames.IsExtension(cm);
            member.IsGetter = closed.IsSpecialName && closed.Name.StartsWith("get_", StringComparison.Ordinal);
            member.Name = member.IsCtor ? target.Type.Name : member.IsGetter ? closed.Name.Substring(4) : closed.Name;
            int tick = member.Name.IndexOf('`');
            if (tick >= 0) {
                member.Name = member.Name.Substring(0, tick);
            }
            member.Params = closed.GetParameters();
            member.Display = TypeNames.Display(closed, target.Type);
            member.DocId = TypeNames.DocId(closed);
            member.Obsolete = (ObsoleteAttribute)Attribute.GetCustomAttribute(raw ?? closed, typeof(ObsoleteAttribute));
            if (!(closed is MethodInfo gm && gm.IsGenericMethodDefinition)) {
                Invoker.For(closed, out member.WhyNotRun);
            } else {
                member.WhyNotRun = "open generic method";
            }
            return member;
        }

        private static bool Relevant(MethodInfo m) {
            var d = m.DeclaringType;
            if (d == typeof(object) || d == typeof(ValueType)) {
                return false;
            }
            if (m.IsSpecialName && !m.Name.StartsWith("get_", StringComparison.Ordinal)) {
                return false; // setters, operators
            }
            if (m.Name == "GetType" || m.Name == "GetHashCode" || m.Name == "Equals") {
                return false;
            }
            return true;
        }

        private static readonly Type[] ComponentChoices = { typeof(CA), typeof(CB), typeof(CE), typeof(CF) };

        /// <summary> Type arguments for a generic method, from its constraints and position. </summary>
        public static Type[] ChooseTypeArguments(MethodInfo m, out string whyNot) {
            whyNot = null;
            var gps = m.GetGenericArguments();
            var result = new Type[gps.Length];
            if (m.DeclaringType == typeof(WorldSerializer) && m.Name == "Register" && m.GetParameters().Length == 1) {
                return new[] { typeof(CRef) };
            }
            int comp = 0;
            int spec = 0;
            for (int i = 0; i < gps.Length; i++) {
                var gp = gps[i];
                // A struct constraint shows up as a ValueType constraint too.
                var cons = gp.GetGenericParameterConstraints().Where(c => c != typeof(ValueType)).ToArray();
                var attrs = gp.GenericParameterAttributes;
                bool isStruct = (attrs & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0;
                bool isClass = (attrs & GenericParameterAttributes.ReferenceTypeConstraint) != 0;
                if (cons.Contains(typeof(IComponent))) {
                    if (comp >= ComponentChoices.Length) {
                        whyNot = "more component type parameters than probe types";
                        return null;
                    }
                    result[i] = ComponentChoices[comp++];
                } else if (cons.Contains(typeof(IFilterSpec))) {
                    result[i] = SpecFor(gps.Length, spec++);
                } else if (cons.Length > 0) {
                    whyNot = "unknown constraint " + string.Join(", ", cons.Select(c => c.Name));
                    return null;
                } else if (isClass) {
                    result[i] = m.DeclaringType == typeof(SharedData) ? typeof(ProbeConfig) : typeof(IProbeListener);
                } else if (isStruct) {
                    result[i] = typeof(Ev);
                } else {
                    result[i] = typeof(int);
                }
            }
            return result;
        }

        private static Type SpecFor(int arity, int position) {
            if (position == 0) {
                return typeof(Inc<CA>);
            }
            if (position == 1) {
                return typeof(Exc<CB>);
            }
            return typeof(Any<CE, CF>);
        }

        // The compiler marks every ref struct [Obsolete("Types with embedded references are not supported
        // in this version of your compiler.", true)] for old compilers; that is not the package's text.
        private static bool CompilerMarker(Type t, ObsoleteAttribute a) {
            return t.IsByRefLike && a.Message != null && a.Message.StartsWith("Types with embedded references are not supported", StringComparison.Ordinal);
        }

        /// <summary> Every [Obsolete] member of the package assembly, whatever its type. </summary>
        public static List<(string Member, ObsoleteAttribute Attr, MemberInfo Info)> ObsoleteMembers() {
            var list = new List<(string, ObsoleteAttribute, MemberInfo)>();
            foreach (var t in typeof(World).Assembly.GetTypes()) {
                if (t.Namespace == null || !t.Namespace.StartsWith("KenseiECS", StringComparison.Ordinal)) {
                    continue;
                }
                var ta = (ObsoleteAttribute)Attribute.GetCustomAttribute(t, typeof(ObsoleteAttribute));
                if (ta != null && !CompilerMarker(t, ta)) {
                    list.Add((t.FullName, ta, t));
                }
                const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
                foreach (var m in t.GetMembers(all)) {
                    if (m is Type) {
                        continue;   // nested types are listed on their own
                    }
                    var a = (ObsoleteAttribute)Attribute.GetCustomAttribute(m, typeof(ObsoleteAttribute));
                    if (a == null) {
                        continue;
                    }
                    string name = m is MethodBase mb ? TypeNames.Display(mb, t) : TypeNames.CSharp(t) + "." + m.Name;
                    list.Add((name, a, m));
                }
            }
            list.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
            return list;
        }
    }
}
