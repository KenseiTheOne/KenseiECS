using System;
using KenseiECS;

namespace KenseiStand.Alloc.Probe {
    // ==================================================================
    // SystemsRunner.Run
    // ==================================================================

    internal static class RunnerSetup {
        public const int Entities = 256;
        public const int Marks = 32;

        public static World World(out Entity[] entities) {
            var world = new World();
            entities = new Entity[Entities];
            for (int i = 0; i < Entities; i++) {
                entities[i] = world.CreateEntity(new Pos { X = i });
                world.Add(entities[i], new Vel { X = 1 });
            }
            return world;
        }
    }

    public struct RunOp : IAllocOp {
        public SystemsRunner Root;
        public void Invoke() {
            Root.Run();
        }
    }

    /// <summary>
    /// Root runner: two systems, a one-frame producer, an unnamed inline group (Add(runner)),
    /// a disabled system slot and a disabled unnamed inline group (SetActive(index, false)),
    /// OneFrame&lt;Mark&gt; on the root. API of 2.1.1 only.
    /// </summary>
    public sealed class RunnerInlineGroups : Scenario {
        public override string Id => "runner.inline_groups";

        public override void Run(Settings s, Result r) {
            var world = RunnerSetup.World(out var entities);
            var child = new SystemsRunner(world).Add(new CountSystem()).Add(new MoveSystem());
            var off = new SystemsRunner(world).Add(new CountSystem()).Add(new MoveSystem());
            var disabled = new CountSystem();
            var root = new SystemsRunner(world)
                .Add(new MoveSystem())
                .Add(new MarkProducer<Mark>(entities, RunnerSetup.Marks))
                .Add(child)
                .Add(disabled)
                .Add(off)
                .Add(new CountSystem())
                .OneFrame<Mark>();
            root.Init();
            root.SetActive(3, false);
            root.SetActive(4, false);
            var op = new RunOp { Root = root };
            r.FirstBytes = Census.Once(ref op, r, world);
            r.Set("systems", root.SystemCount);
            Steady(s, r, ref op, 10);
            Time(s, r, ref op);
        }
    }

    /// <summary> A named inline group (AddGroup, 2.2.0+) toggled off with SetActive(name). </summary>
    public sealed class RunnerNamedGroup : Scenario {
        public override string Id => "runner.named_group";

        public override void Run(Settings s, Result r) {
            if (Compat.AddGroup == null) {
                r.Available = false;
                r.Reason = Compat.Missing("SystemsRunner.AddGroup(name, runner)");
                return;
            }
            var world = RunnerSetup.World(out var entities);
            var group = new SystemsRunner(world).Add(new CountSystem()).Add(new MoveSystem());
            var root = new SystemsRunner(world)
                .Add(new MoveSystem())
                .Add(new MarkProducer<Mark>(entities, RunnerSetup.Marks));
            Compat.AddGroup(root, "group", group);
            root.Add(new CountSystem()).OneFrame<Mark>();
            root.Init();
            root.SetActive("group", false);
            var op = new RunOp { Root = root };
            r.FirstBytes = Census.Once(ref op, r, world);
            Steady(s, r, ref op, 10);
            Time(s, r, ref op);
        }
    }

    /// <summary>
    /// OneFrame&lt;Mark2&gt;(cleanWhenDisabled: true) in a disabled inline group (2.2.0+): a root
    /// producer adds the marker every frame, the skipped group's cleanup removes it.
    /// </summary>
    public sealed class RunnerCleanWhenDisabled : Scenario {
        public override string Id => "runner.clean_when_disabled";

        public override void Run(Settings s, Result r) {
            var clean = Compat.OneFrameClean<Mark2>();
            if (clean == null) {
                r.Available = false;
                r.Reason = Compat.Missing("SystemsRunner.OneFrame<T>(cleanWhenDisabled)");
                return;
            }
            var world = RunnerSetup.World(out var entities);
            var group = new SystemsRunner(world).Add(new CountSystem());
            clean(group, true);
            var root = new SystemsRunner(world)
                .Add(new MarkProducer<Mark2>(entities, RunnerSetup.Marks))
                .Add(group)
                .Add(new MoveSystem());
            root.Init();
            root.SetActive(1, false);
            var op = new RunOp { Root = root };
            r.FirstBytes = Census.Once(ref op, r, world);
            r.Set("marks_per_frame", RunnerSetup.Marks);
            Steady(s, r, ref op, 10);
            Time(s, r, ref op);
        }
    }
}
