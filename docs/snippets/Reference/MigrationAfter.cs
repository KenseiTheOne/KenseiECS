// "After" listings of the worked migration in docs/migration-from-leoecslite.md:
// the same program in KenseiECS, with a hand-written Init and with the generator.
// The using directives sit inside the namespace so the region can show them.

namespace Docs.Snippets.Reference.Migration.After {
    // #region after
    using KenseiECS;

    public struct Position : IComponent { public float X, Y; }
    public struct Velocity : IComponent { public float X, Y; }
    public struct Frozen : IComponent { }
    public struct Expired : IComponent { }

    public sealed class GameShared {
        public float DeltaTime;
    }

    public sealed class MovementSystem : IInitSystem, IRunSystem {
        private Filter _moving;
        private ComponentPool<Position> _positions;
        private ComponentPool<Velocity> _velocities;
        private GameShared _shared;

        public void Init(World world, SharedData shared) {
            _moving = world.Filter<Inc<Position, Velocity>, Exc<Frozen>>();
            _positions = world.Pool<Position>();
            _velocities = world.Pool<Velocity>();
            _shared = shared.Get<GameShared>();
        }

        public void Run(World world) {
            foreach (int e in _moving) {
                ref Position pos = ref _positions.Get(e);
                ref Velocity vel = ref _velocities.Get(e);
                pos.X += vel.X * _shared.DeltaTime;
                pos.Y += vel.Y * _shared.DeltaTime;
                if (pos.Y < 0f) {
                    world.Add(world.GetEntity(e), new Expired());
                }
            }
        }
    }

    public sealed class ExpiredCleanupSystem : IInitSystem, IRunSystem {
        private Filter _expired;

        public void Init(World world, SharedData shared) {
            _expired = world.Filter<Inc<Expired>>();
        }

        public void Run(World world) {
            foreach (int e in _expired) {
                world.DestroyEntity(world.GetEntity(e));
            }
        }
    }

    public sealed class Bootstrap {
        private World _world;
        private SystemsRunner _systems;

        public void Start() {
            _world = new World();
            var shared = new SharedData();
            shared.Add(new GameShared { DeltaTime = 1f / 60f });

            _systems = new SystemsRunner(_world, shared)
                .Add(new MovementSystem())
                .Add(new ExpiredCleanupSystem());
            _systems.Init();

            Entity e = _world.CreateEntity(new Position { X = 1f });
            _world.Add(e, new Velocity { Y = -1f });
        }

        public void Update() {
            _systems.Run();
        }

        public void Stop() {
            _systems.Destroy();
            _world.Destroy();
        }
    }
    // #endregion after
}

namespace Docs.Snippets.Reference.Migration.AfterGenerated {
    using KenseiECS;
    using Docs.Snippets.Reference.Migration.After;

    // #region after-generated
    public sealed partial class MovementSystem : IRunSystem {
        [Inc(typeof(Position), typeof(Velocity))] [Exc(typeof(Frozen))]
        private Filter _moving;
        [Pool] private ComponentPool<Position> _positions;
        [Pool] private ComponentPool<Velocity> _velocities;
        [Shared] private GameShared _shared;

        public void Run(World world) {
            foreach (int e in _moving) {
                ref Position pos = ref _positions.Get(e);
                ref Velocity vel = ref _velocities.Get(e);
                pos.X += vel.X * _shared.DeltaTime;
                pos.Y += vel.Y * _shared.DeltaTime;
                if (pos.Y < 0f) {
                    world.Add(world.GetEntity(e), new Expired());
                }
            }
        }
    }
    // #endregion after-generated
}
