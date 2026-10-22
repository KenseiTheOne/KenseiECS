using System;
using KenseiECS;

namespace Docs.Snippets.Runner {
    struct HitEvent : IComponent { public float Value; }
    struct DamageEvent : IComponent { public float Value; }

    class InputSystem : IRunSystem { public void Run(World world) { } }
    class MovementSystem : IRunSystem { public void Run(World world) { } }
    class DamageSystem : IRunSystem { public void Run(World world) { } }
    class RenderSystem : IRunSystem { public void Run(World world) { } }
    class PhysicsSystem : IRunSystem { public void Run(World world) { } }

    static class Pipeline {
        static void Example(World world, SharedData shared) {
            // #region pipeline
            var systems = new SystemsRunner(world, shared)
                .Add(new InputSystem())
                .Add(new MovementSystem(), "movement")   // named
                .Add(new DamageSystem())
                .DelHere<HitEvent>()                     // remove HitEvent here, mid-pipeline
                .Add(new RenderSystem())
                .OneFrame<DamageEvent>();                // remove DamageEvent at end of Run

            systems.Init();
            systems.Run();

            // Enable/disable at runtime
            systems.SetActive("movement", false);
            systems.SetActive("movement", true);

            systems.Destroy();
            // #endregion pipeline
        }
    }

    // #region nested
    class GameLoop {
        SystemsRunner _root;

        public void Start(World world, SharedData shared) {
            var fixedSystems = new SystemsRunner(world)
                .Add(new PhysicsSystem());
            var combat = new SystemsRunner(world)
                .Add(new DamageSystem());

            _root = new SystemsRunner(world, shared)
                .Add(new MovementSystem())
                .AddGroup("combat", combat)      // inline: runs inside _root.Run()
                .AddPhase("fixed", fixedSystems); // separate phase

            _root.Init();
        }

        public void Update() =>
            _root.Run();                     // ticks the world, runs root systems

        public void FixedUpdate() =>
            _root.GetRunner("fixed").Run();  // separate phase, no tick

        public void PausePhysics() =>
            _root.SetActive("fixed", false); // pause a whole phase

        public void PauseCombat() =>
            _root.SetActive("combat", false); // skip a group inside Run

        public void OnDestroy() =>
            _root.Destroy();
    }
    // #endregion nested

    static class GroupHandle {
        static void Example(World world, SystemsRunner root) {
            // #region group-handle
            var sim = new SystemsRunner(world).Add(new MovementSystem());
            root.AddGroup("sim", sim);
            // Later, anywhere that holds `sim`:
            sim.SetActive(false);   // same as root.SetActive("sim", false)
            // #endregion group-handle
        }
    }

    static class CleanWhenDisabled {
        static void Example(World world) {
            // #region clean-when-disabled
            var combat = new SystemsRunner(world)
                .Add(new DamageSystem())
                .OneFrame<DamageEvent>(cleanWhenDisabled: true); // removed even while combat is paused

            var root = new SystemsRunner(world)
                .Add(new InputSystem())
                .AddGroup("combat", combat);

            root.SetActive("combat", false); // DamageEvent is still removed every frame
            // #endregion clean-when-disabled
        }
    }

    static class Introspection {
        static void SystemInfo(SystemsRunner systems) {
            // #region system-info
            for (int i = 0; i < systems.SystemCount; i++) {
                var info = systems.GetSystemInfo(i);   // Name, IsEnabled, ChildRunner, IsSeparatePhase
            #if KENSEI_DEBUG
                Console.WriteLine($"{info.Name}: {info.LastRunMs:F3} ms (peak {info.PeakRunMs:F3})");
            #endif
            }
            systems.SetActive(2, false);               // by position, for tooling
            int index = systems.IndexOf("movement");   // position of a name or a system, -1 if absent
            // #endregion system-info
        }

        static void WorldInfo(World world) {
            // #region world-info
            for (int i = 0; i < world.FilterCount; i++) {
                var filter = world.GetFilter(i);
                Console.WriteLine($"{filter}: {filter.Count} entities, {filter.AllocatedBytes} bytes");
            }
            foreach (var pool in world.ActivePools) {
                Console.WriteLine($"{pool.ComponentType.Name}: {pool.Count}/{pool.DenseCapacity}, {pool.AllocatedBytes} bytes");
            }
            // #endregion world-info
        }
    }
}
