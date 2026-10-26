using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using KenseiECS;
using Stand.Contracts.Probe;

namespace Stand.Contracts {
    /// <summary> A synthesized argument: its value and the C# that produces it in a repro. </summary>
    public sealed class ArgValue {
        public object Value;
        public string Code;
        public bool IsNull;      // null, or a struct whose reference fields are null (default(Listeners<T>))
        public bool IsOut;
        public string Prelude;   // a declaration the call needs first (ref arguments)
        public bool Unknown;     // the factory has no value for this type and passed null
        public List<ProbeDef> Probes;   // queries on this argument after the call (it may be changed by a failed call too)
        public string Skip;             // the argument cannot be built for this version: the case is not run, with this reason
    }

    /// <summary> Which argument a case varies, and how. "-" keeps every argument normal. </summary>
    public sealed class Variant {
        public string Name = "-";
        public int Slot = -1;
        public string Kind;      // stale | stale-index | null-entity | default | neg | min | huge | fields-neg | fields-huge | null

        public static readonly Variant None = new Variant();
    }

    /// <summary>
    /// Argument factory: a normal value for every parameter, chosen by type and name so that the
    /// call succeeds on a fresh state, and the misuse variants of each scenario (stale handles,
    /// Entity.Null, negative and huge ints, null references), one parameter at a time.
    /// </summary>
    public static class Args {
        public static List<Variant> VariantsFor(Member m, string scenario, bool full) {
            var list = new List<Variant>();
            switch (scenario) {
                case Scenarios.Fresh:
                case Scenarios.WorldDestroyed:
                case Scenarios.RunnerDestroyed:
                case Scenarios.RunnerReinit:
                    list.Add(Variant.None);
                    return list;
            }
            for (int i = 0; i < m.Params.Length; i++) {
                var p = m.Params[i];
                var t = p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType;
                if (p.IsOut) {
                    continue;
                }
                string pn = "p" + i + "(" + p.Name + ")";
                switch (scenario) {
                    case Scenarios.StaleDead:
                    case Scenarios.StaleReused:
                        if (t == typeof(Entity)) {
                            list.Add(new Variant { Name = pn + "=stale", Slot = i, Kind = "stale" });
                        } else if (t == typeof(int) && p.Name == "entityIndex") {
                            list.Add(new Variant { Name = pn + "=stale.Index", Slot = i, Kind = "stale-index" });
                        }
                        break;
                    case Scenarios.NullEntity:
                        if (t == typeof(Entity)) {
                            list.Add(new Variant { Name = pn + "=Entity.Null", Slot = i, Kind = "null-entity" });
                            list.Add(new Variant { Name = pn + "=default", Slot = i, Kind = "default" });
                        } else if (t.IsValueType && !t.IsPrimitive && t.Namespace == "KenseiECS" && !t.IsEnum) {
                            list.Add(new Variant { Name = pn + "=default", Slot = i, Kind = "default" });
                        }
                        break;
                    case Scenarios.Negative:
                        if (t == typeof(int)) {
                            list.Add(new Variant { Name = pn + "=-1", Slot = i, Kind = "neg" });
                            if (full) {
                                list.Add(new Variant { Name = pn + "=int.MinValue", Slot = i, Kind = "min" });
                            }
                        } else if (HasIntFields(t)) {
                            list.Add(new Variant { Name = pn + "=fields(-1)", Slot = i, Kind = "fields-neg" });
                        }
                        break;
                    case Scenarios.Huge:
                        if (t == typeof(int)) {
                            list.Add(new Variant { Name = pn + "=int.MaxValue", Slot = i, Kind = "huge" });
                        } else if (HasIntFields(t)) {
                            list.Add(new Variant { Name = pn + "=fields(int.MaxValue)", Slot = i, Kind = "fields-huge" });
                        }
                        break;
                    case Scenarios.NullArg:
                        if (!t.IsValueType && !p.ParameterType.IsByRef) {
                            // An optional parameter whose default is null is already null in the normal call.
                            if (p.HasDefaultValue && p.DefaultValue == null) {
                                break;
                            }
                            list.Add(new Variant { Name = pn + "=null", Slot = i, Kind = "null" });
                        }
                        break;
                }
            }
            return list;
        }

        // A config-like struct (WorldConfig): public int fields a caller can set. Handles (Entity,
        // PendingEntity) have readonly fields and cannot carry arbitrary values through the public API.
        private static bool HasIntFields(Type t) {
            return t.IsValueType && !t.IsPrimitive && t.Namespace == "KenseiECS"
                && t.GetFields(BindingFlags.Public | BindingFlags.Instance).Any(f => f.FieldType == typeof(int) && !f.IsInitOnly);
        }

        public static ArgValue[] Build(Member m, Ctx c, Variant v) {
            var result = new ArgValue[m.Params.Length];
            for (int i = 0; i < m.Params.Length; i++) {
                result[i] = i == v.Slot ? Vary(m, i, c, v) : Normal(m, i, c);
            }
            return result;
        }

        private static ArgValue Vary(Member m, int i, Ctx c, Variant v) {
            var p = m.Params[i];
            var t = p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType;
            switch (v.Kind) {
                case "stale": return new ArgValue { Value = c.Stale, Code = "stale" };
                case "stale-index": return new ArgValue { Value = c.Stale.Index, Code = "stale.Index" };
                case "null-entity": return new ArgValue { Value = Entity.Null, Code = "Entity.Null" };
                case "default": {
                    object d = Activator.CreateInstance(t);
                    return new ArgValue { Value = d, Code = "default(" + TypeNames.CSharp(t) + ")", IsNull = HoldsNull(d) };
                }
                case "neg": return new ArgValue { Value = -1, Code = "-1" };
                case "min": return new ArgValue { Value = int.MinValue, Code = "int.MinValue" };
                case "huge": return new ArgValue { Value = int.MaxValue, Code = "int.MaxValue" };
                case "fields-neg": return WithIntFields(t, -1, "-1");
                case "fields-huge": return WithIntFields(t, int.MaxValue, "int.MaxValue");
                case "null": return new ArgValue { Value = null, Code = "null", IsNull = true };
            }
            throw new InvalidOperationException("unknown variant " + v.Kind);
        }

        private static ArgValue WithIntFields(Type t, int value, string code) {
            object boxed = Activator.CreateInstance(t);
            var parts = new List<string>();
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance)) {
                if (f.FieldType == typeof(int) && !f.IsInitOnly) {
                    f.SetValue(boxed, value);
                    parts.Add(f.Name + " = " + code);
                }
            }
            return new ArgValue { Value = boxed, Code = "new " + TypeNames.CSharp(t) + " { " + string.Join(", ", parts) + " }" };
        }

        // =====================================================================
        // Normal values
        // =====================================================================

        public static ArgValue Normal(Member m, int i, Ctx c) {
            var p = m.Params[i];
            var pt = p.ParameterType;
            if (p.IsOut) {
                return new ArgValue { Value = null, Code = "out var o" + i, IsOut = true };
            }
            var t = pt.IsByRef ? pt.GetElementType() : pt;
            string name = m.Name;

            if (pt.IsByRef) {
                // ref Listeners<T> / ref EventBuffer<T> (AutoReset, AutoCopy): a populated copy of the target's type.
                if (t == typeof(Listeners<IProbeListener>)) {
                    var l = new Listeners<IProbeListener>();
                    l.Add(new ProbeListener());
                    return new ArgValue { Value = l, Code = "ref r" + i, Prelude = "var r" + i + " = new Listeners<IProbeListener>(); r" + i + ".Add(new ProbeListener());" };
                }
                if (t == typeof(EventBuffer<Ev>)) {
                    var e = new EventBuffer<Ev>();
                    e.Add(new Ev { V = 3 });
                    return new ArgValue { Value = e, Code = "ref r" + i, Prelude = "var r" + i + " = new EventBuffer<Ev>(); r" + i + ".Add(new Ev { V = 3 });" };
                }
                object d = t.IsValueType ? Activator.CreateInstance(t) : null;
                return new ArgValue { Value = d, Code = "ref r" + i, Prelude = "var r" + i + " = default(" + TypeNames.CSharp(t) + ");", IsNull = d == null || HoldsNull(d) };
            }

            if (t == typeof(Entity)) {
                if (!c.HasEntities) {
                    return new ArgValue { Value = Entity.Null, Code = "Entity.Null" };
                }
                if (name == "Add") {
                    return new ArgValue { Value = c.E3, Code = "e3" };   // e3 has no CA
                }
                if (name == "DestroyEntity" || name == "Remove") {
                    return new ArgValue { Value = c.E1, Code = "e1" };
                }
                return new ArgValue { Value = c.E0, Code = "e0" };
            }
            if (t == typeof(PendingEntity)) {
                return c.Buffer != null ? new ArgValue { Value = c.Pending, Code = "pending" } : new ArgValue { Value = default(PendingEntity), Code = "default(PendingEntity)" };
            }
            if (t == typeof(int)) {
                return IntFor(m, p, c);
            }
            if (t == typeof(bool)) {
                return new ArgValue { Value = true, Code = "true" };
            }
            if (t == typeof(string)) {
                return StringFor(m, p);
            }
            if (t == typeof(World)) {
                return new ArgValue { Value = c.World, Code = "world", IsNull = c.World == null };
            }
            if (t == typeof(SharedData)) {
                return c.Shared != null ? new ArgValue { Value = c.Shared, Code = "shared" } : new ArgValue { Value = new SharedData(), Code = "new SharedData()" };
            }
            if (t == typeof(SystemsRunner)) {
                var child = new SystemsRunner(c.World);
                child.Add(new ProbeSystem());
                return new ArgValue { Value = child, Code = "new SystemsRunner(world).Add(new ProbeSystem())" };
            }
            if (t == typeof(ISystem)) {
                return new ArgValue { Value = new ProbeSystem(), Code = "new ProbeSystem()" };
            }
            if (t == typeof(IFilterListener) || t == typeof(IComponentListener<CA>) || t == typeof(IWorldEventListener) || t == typeof(IProbeListener)) {
                return new ArgValue { Value = new ProbeListener(), Code = "new ProbeListener()" };
            }
            if (t == typeof(ProbeConfig)) {
                return new ArgValue { Value = new ProbeConfig { V = 3 }, Code = "new ProbeConfig { V = 3 }" };
            }
            if (t == typeof(Stream)) {
                if (name == "Load" && c.Snapshot != null) {
                    c.Snapshot.Position = 0;
                    return new ArgValue { Value = c.Snapshot, Code = "snapshot" };
                }
                return new ArgValue { Value = new MemoryStream(), Code = "new MemoryStream()" };
            }
            if (t == typeof(IComponentFormatter<CRef>)) {
                return new ArgValue { Value = new CRefFormatter(), Code = "new CRefFormatter()" };
            }
            if (t == typeof(List<int>)) {
                return new ArgValue { Value = new List<int>(), Code = "new List<int>()" };
            }
            if (t == typeof(WorldConfig)) {
                return new ArgValue { Value = WorldConfig.Default(), Code = "WorldConfig.Default()" };
            }
            if (t == typeof(object)) {
                return new ArgValue { Value = new CA { V = 7 }, Code = "(object)new CA { V = 7 }" };
            }
            if (typeof(Delegate).IsAssignableFrom(t)) {
                return NoOpDelegate(t);
            }
            if (t.IsValueType && typeof(IComponent).IsAssignableFrom(t) || t == typeof(Ev)) {
                object value = Activator.CreateInstance(t);
                var f = t.GetField("V");
                if (f != null && f.FieldType == typeof(int)) {
                    f.SetValue(value, 7);
                    return new ArgValue { Value = value, Code = "new " + TypeNames.CSharp(t) + " { V = 7 }" };
                }
                return new ArgValue { Value = value, Code = "default(" + TypeNames.CSharp(t) + ")" };
            }
            if (t.IsValueType) {
                object d = Activator.CreateInstance(t);
                return new ArgValue { Value = d, Code = "default(" + TypeNames.CSharp(t) + ")", IsNull = HoldsNull(d) };
            }
            // A type added in a later version that the factory does not know: the case still runs,
            // with null, and the report lists the parameter so the factory can be taught the type.
            return new ArgValue { Value = null, Code = "null", IsNull = true, Unknown = true };
        }

        /// <summary>
        /// A struct value that carries a null into the call through one of its reference fields:
        /// default(Listeners&lt;T&gt;) or default(EventBuffer&lt;T&gt;) has Values == null. An
        /// ArgumentNullException caused by it is not "ArgumentNullException without a null argument".
        /// </summary>
        public static bool HoldsNull(object value) {
            if (value == null) {
                return true;
            }
            var vt = value.GetType();
            if (!vt.IsValueType || vt.IsPrimitive || vt.IsEnum) {
                return false;
            }
            foreach (var f in vt.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)) {
                if (!f.FieldType.IsValueType && f.GetValue(value) == null) {
                    return true;
                }
            }
            return false;
        }

        private static ArgValue IntFor(Member m, ParameterInfo p, Ctx c) {
            switch (p.Name) {
                case "entityIndex":
                    if (m.Name == "Add" && m.Target.Type == typeof(ComponentPool<CA>)) {
                        return new ArgValue { Value = c.HasEntities ? c.E3.Index : 0, Code = c.HasEntities ? "e3.Index" : "0" };
                    }
                    return new ArgValue { Value = c.HasEntities ? c.E0.Index : 0, Code = c.HasEntities ? "e0.Index" : "0" };
                case "typeIndex":
                    return new ArgValue { Value = ComponentType<CA>.Index, Code = "ComponentType<CA>.Index" };
                case "entities": return new ArgValue { Value = 128, Code = "128" };
                case "components": return new ArgValue { Value = 16, Code = "16" };
                case "count": return new ArgValue { Value = 64, Code = "64" };
                case "lists": return new ArgValue { Value = 2, Code = "2" };
                case "capacity": return new ArgValue { Value = 4, Code = "4" };
                case "version": return new ArgValue { Value = 0, Code = "0" };
                case "index": return new ArgValue { Value = 0, Code = "0" };
                default: return new ArgValue { Value = 1, Code = "1" };
            }
        }

        private static ArgValue StringFor(Member m, ParameterInfo p) {
            string s;
            if (m.Target.Type == typeof(SystemsRunner)) {
                switch (m.Name) {
                    case "SetActive":
                    case "IsActive":
                    case "IndexOf":
                        s = "sys";
                        break;
                    case "GetRunner":
                        s = "phase";
                        break;
                    case "AddGroup":
                        s = "grp2";
                        break;
                    case "AddPhase":
                        s = "phase2";
                        break;
                    default:
                        s = "sys2";
                        break;
                }
            } else if (m.Target.Type == typeof(SharedData)) {
                s = m.Name == "Add" ? "k2" : "k";
            } else {
                s = "probe";
            }
            return new ArgValue { Value = s, Code = "\"" + s + "\"" };
        }

        private static ArgValue NoOpDelegate(Type t) {
            var invoke = t.GetMethod("Invoke");
            var ps = invoke.GetParameters().Select(x => Expression.Parameter(x.ParameterType, x.Name)).ToArray();
            Expression body = invoke.ReturnType == typeof(void) ? Expression.Empty() : (Expression)Expression.Default(invoke.ReturnType);
            var lambda = Expression.Lambda(t, body, ps).Compile();
            string args = ps.Length == 1 ? "_" : "(" + string.Join(", ", ps.Select((x, k) => "_" + k)) + ")";
            string code = "(" + TypeNames.CSharp(t) + ")(" + args + " => { })";
            if (invoke.ReturnType != typeof(void)) {
                code = "(" + TypeNames.CSharp(t) + ")(" + args + " => default)";
            }
            return new ArgValue { Value = lambda, Code = code };
        }
    }
}
