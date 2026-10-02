// "Before" listing of the worked migration in docs/migration-from-leoecslite.md:
// a complete LeoEcsLite program. The using directives sit inside the namespace so the
// region can show them.

namespace Docs.Snippets.Reference.Migration.Before {
    // #region before
    using System.Collections.Generic;
    using Leopotam.EcsLite;

    public struct Position { public float X, Y; }
    public struct Velocity { public float X, Y; }
    public struct Frozen { }
    public struct Expired { }

    public sealed class GameShared {
        public float DeltaTime;
    }

    public sealed class MovementSystem : IEcsInitSystem, IEcsRunSystem {
        private EcsWorld _world;
        private EcsFilter _moving;
        private EcsPool<Position> _positions;
        private EcsPool<Velocity> _velocities;
        private EcsPool<Expired> _expired;
        private GameShared _shared;

        public void Init(IEcsSystems systems) {
            _world = systems.GetWorld();
            _moving = _world.Filter<Position>().Inc<Velocity>().Exc<Frozen>().End();
            _positions = _world.GetPool<Position>();
            _velocities = _world.GetPool<Velocity>();
            _expired = _world.GetPool<Expired>();
            _shared = systems.GetShared<GameShared>();
        }

        public void Run(IEcsSystems systems) {
            foreach (int e in _moving) {
                ref Position pos = ref _positions.Get(e);
                ref Velocity vel = ref _velocities.Get(e);
                pos.X += vel.X * _shared.DeltaTime;
                pos.Y += vel.Y * _shared.DeltaTime;
                if (pos.Y < 0f) {
                    _expired.Add(e);
                }
            }
        }
    }

    public sealed class ExpiredCleanupSystem : IEcsRunSystem {
        public void Run(IEcsSystems systems) {
            EcsWorld world = systems.GetWorld();
            EcsFilter filter = world.Filter<Expired>().End();
            foreach (int e in filter) {
                world.DelEntity(e);
            }
        }
    }

    public sealed class Bootstrap {
        private EcsWorld _world;
        private IEcsSystems _systems;

        public void Start() {
            _world = new EcsWorld();
            _systems = new EcsSystems(_world, new GameShared { DeltaTime = 1f / 60f });
            _systems
                .Add(new MovementSystem())
                .Add(new ExpiredCleanupSystem())
                .Init();

            int e = _world.NewEntity();
            ref Position pos = ref _world.GetPool<Position>().Add(e);
            pos.X = 1f;
            ref Velocity vel = ref _world.GetPool<Velocity>().Add(e);
            vel.Y = -1f;
        }

        public void Update() {
            _systems.Run();
        }

        public void Stop() {
            _systems.Destroy();
            _world.Destroy();
        }
    }
    // #endregion before
}
