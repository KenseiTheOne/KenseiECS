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

            _root = new SystemsRunner(world, shared)
                .Add(new MovementSystem())
                .Add(fixedSystems, "fixed");

            _root.Init();
        }

        public void Update() =>
            _root.Run();                     // ticks the world, runs root systems

        public void FixedUpdate() =>
            _root.GetRunner("fixed").Run();  // separate phase, no tick

        public void PausePhysics() =>
            _root.SetActive("fixed", false); // pause a whole phase

        public void OnDestroy() =>
            _root.Destroy();
    }
    // #endregion nested

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
