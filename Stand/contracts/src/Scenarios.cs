using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using KenseiECS;
using Stand.Contracts.Probe;

namespace Stand.Contracts {
    /// <summary> Pieces of the scenario setup. A repro keeps only the pieces its case needs. </summary>
    [Flags]
    public enum Part {
        None = 0,
        World = 1,
        Entities = 2,
        Pool = 4,
        Filters = 8,
        Group = 16,
        Shared = 32,
        Runner = 64,
        Buffer = 128,
        Serializer = 256,
        Listeners = 512,
        Events = 1024,
        All = World | Entities | Pool | Filters | Group | Shared | Runner | Buffer | Serializer | Listeners | Events
    }

    /// <summary> Objects of one scenario instance, and the C# that builds the same state (for repros). </summary>
    public sealed class Ctx {
        public string Scenario;
        public Part Parts;
        public World World;
        public Entity E0, E1, E2, E3;
        public Entity Stale, Reused;
        public bool HasEntities, HasStale, HasReused;
        public ComponentPool<CA> PoolA;
        public Filter FA, FAnotB;
        public FilterBuilder Builder;
        public Group<CC, CD> GroupCD;
        public SharedData Shared;
        public SystemsRunner Runner, Inline, Phase;
        public ProbeSystem Sys, InlineSys, PhaseSys;
        public bool InlineNamed;          // AddGroup exists (2.2.0+): the inline group is registered as "grp"
        public CommandBuffer Buffer;
        public PendingEntity Pending;
        public WorldSerializer Serializer;
        public MemoryStream Snapshot;
        public object ListenersBox;       // boxed Listeners<IProbeListener>, mutated in place by calls
        public object EventsBox;          // boxed EventBuffer<Ev>
        public readonly List<string> Code = new();

        public object TargetObject(Target t) {
            if (t.Type == typeof(World)) return World;
            if (t.Type == typeof(ComponentPool<CA>)) return PoolA;
            if (t.Type == typeof(Filter)) return FA;
            if (t.Type == typeof(FilterBuilder)) return Builder;
            if (t.Type == typeof(Group<CC, CD>)) return GroupCD;
            if (t.Type == typeof(CommandBuffer)) return Buffer;
            if (t.Type == typeof(SystemsRunner)) return Runner;
            if (t.Type == typeof(SharedData)) return Shared;
            if (t.Type == typeof(WorldSerializer)) return Serializer;
            if (t.Type == typeof(Listeners<IProbeListener>)) return ListenersBox;
            if (t.Type == typeof(EventBuffer<Ev>)) return EventsBox;
            return null;
        }
    }

    /// <summary>
    /// The lifecycle and misuse scenarios. Each Build returns a fresh, independent instance: a call is
    /// made on one instance, and the probes compare it with another instance built the same way.
    /// The C# in Ctx.Code is written next to the code that builds the state, so a repro and the stand
    /// do the same thing; the repro check (Stand/contracts/repro) runs the repros to make sure.
    /// </summary>
    public static class Scenarios {
        public const string Fresh = "fresh";
        public const string WorldDestroyed = "world-destroyed";
        public const string RunnerDestroyed = "runner-destroyed";
        public const string RunnerReinit = "runner-reinit";
        public const string StaleDead = "stale-dead";
        public const string StaleReused = "stale-reused";
        public const string NullEntity = "null-entity";
        public const string Negative = "negative";
        public const string Huge = "huge";
        public const string NullArg = "null-arg";

        public static string Describe(string scenario) {
            switch (scenario) {
                case Fresh: return "a live world, normal arguments";
                case WorldDestroyed: return "after world.Destroy()";
                case RunnerDestroyed: return "runner: Init, then Destroy";
                case RunnerReinit: return "runner: Init, Destroy, Init again";
                case StaleDead: return "stale handle: the entity is destroyed, its slot is free";
                case StaleReused: return "stale handle: the slot holds a new entity";
                case NullEntity: return "Entity.Null and default(T) of the package structs";
                case Negative: return "negative int arguments";
                case Huge: return "huge int arguments (int.MaxValue)";
                case NullArg: return "null for a reference parameter";
                default: return scenario;
            }
        }

        /// <summary> The scenario, and for a curated case what it adds, for titles. </summary>
        public static string DescribeCase(string scenario, string variant) {
            const string prefix = "curated:";
            if (variant == null || !variant.StartsWith(prefix, StringComparison.Ordinal)) {
                return Describe(scenario);
            }
            string label = variant.Substring(prefix.Length);
            string what;
            if (label.StartsWith("null@uninit:", StringComparison.Ordinal)) {
                what = "null for " + label.Substring("null@uninit:".Length) + " on an uninitialized runner";
            } else {
                switch (label) {
                    case "load-truncated": what = "a snapshot cut short by 1 byte, loaded into an empty world"; break;
                    case "load-garbage": what = "a stream that is not a snapshot"; break;
                    case "load-empty-stream": what = "an empty stream"; break;
                    case "load-into-empty": what = "loaded into an empty world"; break;
                    case "load-into-destroyed": what = "loaded into a destroyed world"; break;
                    case "save-ref-without-formatter": what = "a component with references and no formatter"; break;
                    case "nested-other-world": what = "a nested runner over another World, the runner not initialized"; break;
                    case "unknown-name": what = "an unknown name"; break;
                    case "setactive-no-run": what = "SetActive of a system without Run"; break;
                    case "indexof-none": what = "an unregistered name or system"; break;
                    case "listeners-default": what = "default(Listeners<T>), Values == null"; break;
                    case "empty-filter": what = "an empty filter"; break;
                    case "missing-key": what = "an unregistered key"; break;
                    case "no-pool": what = "a type without a pool"; break;
                    case "remove-missing":
                    case "remove-missing-nopool": what = "Remove of a missing component"; break;
                    case "already-has": what = "the component is already there"; break;
                    case "load-unknown-type": what = "a snapshot that names a component type this process does not have"; break;
                    default: what = label; break;
                }
            }
            return (scenario == Fresh ? "a live world" : Describe(scenario)) + "; " + what;
        }

        private static readonly MethodInfo AddGroupMethod = typeof(SystemsRunner).GetMethod("AddGroup", new[] { typeof(string), typeof(SystemsRunner) });

        public static Ctx Build(string scenario, Part parts) {
            var c = new Ctx { Scenario = scenario, Parts = parts };
            var code = c.Code;
            if ((parts & (Part.Runner | Part.Serializer)) != 0 || parts.HasFlag(Part.Entities) || parts.HasFlag(Part.Pool)
                || parts.HasFlag(Part.Filters) || parts.HasFlag(Part.Group) || parts.HasFlag(Part.Buffer)) {
                parts |= Part.World;
            }
            if (parts.HasFlag(Part.Runner)) {
                parts |= Part.Shared;
            }
            c.Parts = parts;

            if (parts.HasFlag(Part.World)) {
                c.World = new World();
                code.Add("var world = new World();");
            }
            if (parts.HasFlag(Part.Entities)) {
                c.HasEntities = true;
                c.E0 = c.World.CreateEntity(new CA { V = 1 });
                c.World.Add(c.E0, new CB { V = 2 });
                c.E1 = c.World.CreateEntity(new CA { V = 3 });
                c.E2 = c.World.CreateEntity(new CC { V = 4 });
                c.World.Add(c.E2, new CD { V = 5 });
                c.E3 = c.World.CreateEntity(new CB { V = 6 });
                code.Add("var e0 = world.CreateEntity(new CA { V = 1 }); // slot 0: CA, CB");
                code.Add("world.Add(e0, new CB { V = 2 });");
                code.Add("var e1 = world.CreateEntity(new CA { V = 3 }); // slot 1: CA");
                code.Add("var e2 = world.CreateEntity(new CC { V = 4 }); // slot 2: CC, CD");
                code.Add("world.Add(e2, new CD { V = 5 });");
                code.Add("var e3 = world.CreateEntity(new CB { V = 6 }); // slot 3: CB");
            }
            if (parts.HasFlag(Part.Pool)) {
                c.PoolA = c.World.Pool<CA>();
                code.Add("var poolA = world.Pool<CA>();");
            }
            if (parts.HasFlag(Part.Filters)) {
                c.FA = c.World.Filter().Inc<CA>().End();
                c.FAnotB = c.World.Filter().Inc<CA>().Exc<CB>().End();
                c.Builder = c.World.Filter().Inc<CB>();
                code.Add("var fA = world.Filter().Inc<CA>().End();");
                code.Add("var fAnotB = world.Filter().Inc<CA>().Exc<CB>().End();");
                code.Add("var builder = world.Filter().Inc<CB>();");
            }
            if (parts.HasFlag(Part.Group)) {
                c.GroupCD = c.World.Group<CC, CD>();
                code.Add("var groupCD = world.Group<CC, CD>();");
            }
            if (parts.HasFlag(Part.Shared)) {
                c.Shared = new SharedData();
                c.Shared.Add(new ProbeConfig { V = 1 });
                c.Shared.Add(new ProbeConfig { V = 2 }, "k");
                code.Add("var shared = new SharedData();");
                code.Add("shared.Add(new ProbeConfig { V = 1 });");
                code.Add("shared.Add(new ProbeConfig { V = 2 }, \"k\");");
            }
            if (parts.HasFlag(Part.Runner)) {
                c.Sys = new ProbeSystem();
                c.Runner = new SystemsRunner(c.World, c.Shared);
                c.Runner.Add(c.Sys, "sys");
                c.Runner.Add(new InitOnlySystem(), "initOnly");
                code.Add("var sys = new ProbeSystem();");
                code.Add("var runner = new SystemsRunner(world, shared);");
                code.Add("runner.Add(sys, \"sys\");");
                code.Add("runner.Add(new InitOnlySystem(), \"initOnly\");");

                c.InlineSys = new ProbeSystem();
                c.Inline = new SystemsRunner(c.World);
                c.Inline.Add(c.InlineSys);
                code.Add("var inlineSys = new ProbeSystem();");
                code.Add("var inline = new SystemsRunner(world);");
                code.Add("inline.Add(inlineSys);");
                if (AddGroupMethod != null) {
                    AddGroupMethod.Invoke(c.Runner, new object[] { "grp", c.Inline });
                    c.InlineNamed = true;
                    code.Add("runner.AddGroup(\"grp\", inline);");
                } else {
                    c.Runner.Add((ISystem)c.Inline);
                    code.Add("runner.Add((ISystem)inline); // 2.1.1: no AddGroup, an unnamed inline group");
                }

                c.PhaseSys = new ProbeSystem();
                c.Phase = new SystemsRunner(c.World);
                c.Phase.Add(c.PhaseSys);
                // Add(ISystem, name) with a runner is a phase in every version from 2.1.1 on.
                c.Runner.Add((ISystem)c.Phase, "phase");
                c.Runner.OneFrame<CE>();
                c.Runner.Init();
                code.Add("var phaseSys = new ProbeSystem();");
                code.Add("var phase = new SystemsRunner(world);");
                code.Add("phase.Add(phaseSys);");
                code.Add("runner.Add((ISystem)phase, \"phase\");");
                code.Add("runner.OneFrame<CE>();");
                code.Add("runner.Init();");
            }
            if (parts.HasFlag(Part.Buffer)) {
                c.Buffer = new CommandBuffer();
                c.Pending = c.Buffer.CreateEntity(new CA { V = 8 });
                code.Add("var buffer = new CommandBuffer();");
                code.Add("var pending = buffer.CreateEntity(new CA { V = 8 });");
            }
            if (parts.HasFlag(Part.Serializer)) {
                c.Serializer = new WorldSerializer();
                c.Serializer.Register(new CRefFormatter());
                var source = new World();
                source.CreateEntity(new CA { V = 11 });
                source.CreateEntity(new CRef { S = "s" });
                c.Snapshot = new MemoryStream();
                c.Serializer.Save(source, c.Snapshot);
                c.Snapshot.Position = 0;
                code.Add("var serializer = new WorldSerializer();");
                code.Add("serializer.Register(new CRefFormatter());");
                code.Add("var source = new World();");
                code.Add("source.CreateEntity(new CA { V = 11 });");
                code.Add("source.CreateEntity(new CRef { S = \"s\" });");
                code.Add("var snapshot = new MemoryStream();");
                code.Add("serializer.Save(source, snapshot);");
                code.Add("snapshot.Position = 0;");
            }
            if (parts.HasFlag(Part.Listeners)) {
                var l = new Listeners<IProbeListener>();
                l.Add(new ProbeListener());
                l.Add(new ProbeListener());
                c.ListenersBox = l;
                code.Add("var listeners = new Listeners<IProbeListener>();");
                code.Add("listeners.Add(new ProbeListener());");
                code.Add("listeners.Add(new ProbeListener());");
            }
            if (parts.HasFlag(Part.Events)) {
                var ev = new EventBuffer<Ev>();
                ev.Add(new Ev { V = 1 });
                ev.Add(new Ev { V = 2 });
                c.EventsBox = ev;
                code.Add("var events = new EventBuffer<Ev>();");
                code.Add("events.Add(new Ev { V = 1 });");
                code.Add("events.Add(new Ev { V = 2 });");
            }

            switch (scenario) {
                case WorldDestroyed:
                    if (c.World != null) {
                        c.World.Destroy();
                        code.Add("world.Destroy();");
                        if (c.HasEntities) {
                            // Slot 0: the slot a failed CreateEntity claims on a destroyed world, so the
                            // DestroyEntity(stale) probe reads exactly what such a call left behind.
                            c.Stale = c.E0;
                            c.HasStale = true;
                            code.Add("var stale = e0;");
                        }
                    }
                    break;
                case StaleDead:
                case StaleReused:
                    if (c.World != null) {
                        c.Stale = c.World.CreateEntity(new CA { V = 9 });
                        c.World.DestroyEntity(c.Stale);
                        c.HasStale = true;
                        code.Add("var stale = world.CreateEntity(new CA { V = 9 }); // slot 4");
                        code.Add("world.DestroyEntity(stale);");
                        if (scenario == StaleReused) {
                            c.Reused = c.World.CreateEntity(new CB { V = 10 });
                            c.HasReused = true;
                            code.Add("var reused = world.CreateEntity(new CB { V = 10 }); // slot 4 again, next generation");
                        }
                    }
                    break;
                case RunnerDestroyed:
                    if (c.Runner != null) {
                        c.Runner.Destroy();
                        code.Add("runner.Destroy();");
                    }
                    break;
                case RunnerReinit:
                    if (c.Runner != null) {
                        c.Runner.Destroy();
                        c.Runner.Init();
                        code.Add("runner.Destroy();");
                        code.Add("runner.Init();");
                    }
                    break;
            }
            return c;
        }
    }
}
