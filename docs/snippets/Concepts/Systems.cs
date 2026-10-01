using KenseiECS;

namespace Docs.Snippets.Systems {
    class TimeService { public float DeltaTime; }
    class SpawnConfig { public int Max; }

    // #region system
    class MySystem : IInitSystem, IRunSystem, IDestroySystem {
        public void Init(World world, SharedData shared) { }
        public void Run(World world) { }
        public void Destroy(World world) { }
    }
    // #endregion system

    static class Setup {
        static void Example(World world) {
            // #region shared-setup
            var shared = new SharedData();
            shared.Add(new TimeService());
            shared.Add(new SpawnConfig { Max = 100 }, "enemies");
            shared.Add(new SpawnConfig { Max = 20 }, "pickups");

            var systems = new SystemsRunner(world, shared)
                .Add(new SpawnSystem());
            // #endregion shared-setup
        }
    }

    // #region shared-get
    class SpawnSystem : IInitSystem {
        TimeService _time;
        SpawnConfig _enemyConfig;

        public void Init(World world, SharedData shared) {
            _time = shared.Get<TimeService>();                  // throws if not registered
            _enemyConfig = shared.Get<SpawnConfig>("enemies");

            if (shared.TryGet(out SpawnConfig pickups, "pickups")) {
                // optional service: false when not registered
            }
        }
    }
    // #endregion shared-get
}
