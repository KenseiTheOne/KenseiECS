namespace Docs.Snippets.SourceGenerator {
    using KenseiECS;

    struct Position : IComponent { public float X, Y; }
    struct Velocity : IComponent { public float X, Y; }
    struct Frozen : IComponent { }

    class GameConfig { public float Speed = 1f; }
    class SpawnConfig { public int Max; }

    // #region system
    public partial class MovementSystem : IRunSystem {
        [Inc(typeof(Position), typeof(Velocity))] [Exc(typeof(Frozen))]
        private Filter _moving;
        [Pool] private ComponentPool<Position> _positions;
        [Pool] private ComponentPool<Velocity> _velocities;
        [Group] private Group<Position, Velocity> _group;
        [Shared] private GameConfig _config;
        [Shared("enemies")] private SpawnConfig _enemies;

        partial void OnInit(World world, SharedData shared) {
            // optional: runs after the fields above are filled
        }

        public void Run(World world) {
            foreach (int e in _moving) {
                _positions.Get(e).X += _velocities.Get(e).X * _config.Speed;
            }
        }
    }
    // #endregion system

    // #region equivalent
    // What the generator writes for MovementSystem, spelled out by hand
    public class HandWrittenMovementSystem : IInitSystem, IRunSystem {
        private Filter _moving;
        private ComponentPool<Position> _positions;
        private ComponentPool<Velocity> _velocities;
        private Group<Position, Velocity> _group;
        private GameConfig _config;
        private SpawnConfig _enemies;

        public void Init(World world, SharedData shared) {
            _moving = world.Filter().Inc<Position>().Inc<Velocity>().Exc<Frozen>().End();
            _positions = world.Pool<Position>();
            _velocities = world.Pool<Velocity>();
            _group = world.Group<Position, Velocity>();
            _config = shared.Get<GameConfig>();
            _enemies = shared.Get<SpawnConfig>("enemies");
            // ...then OnInit(world, shared) if the class implements it
        }

        public void Run(World world) { }
    }
    // #endregion equivalent

    class Usage {
        void Run() {
            // #region usage
            var world = new World();
            var shared = new SharedData();
            shared.Add(new GameConfig());
            shared.Add(new SpawnConfig { Max = 100 }, "enemies");

            var systems = new SystemsRunner(world, shared)
                .Add(new MovementSystem());   // registered as an init system via the generated partial

            systems.Init();                   // calls the generated Init
            // #endregion usage
        }
    }

    // #region nested
    public partial class Gameplay {                 // every containing type must be partial too (KECS005)
        public partial class FreezeSystem : IRunSystem {
            [Inc(typeof(Position))] [Any(typeof(Frozen), typeof(Velocity))]
            private Filter _targets;

            public void Run(World world) { }
        }
    }
    // #endregion nested
}
