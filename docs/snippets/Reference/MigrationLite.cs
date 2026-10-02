using System.Collections.Generic;
using Leopotam.EcsLite;

// LeoEcsLite side of docs/migration-from-leoecslite.md, compiled against the
// Leopotam.EcsLite 1.0.1 NuGet package, an unofficial third-party repack of
// github.com/Leopotam/ecslite with the same API as the official sources. No KenseiECS in this file: the two frameworks share
// type names (Inc, Exc, World-like APIs), so each side lives in its own namespace.

namespace Docs.Snippets.Reference.Migration.Lite {
    public struct Position { public float X, Y; }
    public struct Velocity { public float X, Y; }
    public struct Health { public float Value; }
    public struct DamageEvent { public float Value; }
    public sealed class GameShared { public float DeltaTime; }

    sealed class Snippets {
        private EcsWorld world;
        private EcsWorld _world;
        private EcsPool<Position> positions;
        private EcsPool<Velocity> velocities;
        private EcsPool<Health> healths;
        private EcsPool<DamageEvent> _damage;
        private EcsFilter _projectiles;
        private EcsFilter _targets;

        void CreateEntity() {
            // #region create-entity
            // LeoEcsLite
            int e = world.NewEntity();
            ref var pos = ref positions.Add(e);
            pos.X = 10f;
            velocities.Add(e);
            // #endregion create-entity
        }

        void DeferredChanges() {
            // #region deferred-changes
            // LeoEcsLite: allowed, applied after the loop
            foreach (int e in _projectiles) {
                foreach (int t in _targets) {
                    if (Hits(e, t)) {
                        _world.DelEntity(e);
                        if (!_damage.Has(t)) {
                            _damage.Add(t).Value = 10;
                        }
                        break;
                    }
                }
            }
            // #endregion deferred-changes
        }

        void AddValue(int e) {
            // #region add-value
            // LeoEcsLite
            ref var hp = ref healths.Add(e);
            hp.Value = 100;
            // #endregion add-value
        }

        private static bool Hits(int projectile, int target) => projectile != target;
    }

    sealed class AutoResetExample {
        private EcsPool<Inventory> inventories;

        // #region auto-reset
        // LeoEcsLite
        struct Inventory : IEcsAutoReset<Inventory> {
            public List<int> Items;
            public void AutoReset(ref Inventory c) {
                c.Items ??= new List<int>();
                c.Items.Clear();
            }
        }

        void Spawn(int e) {
            ref var inv = ref inventories.Add(e);   // Items is ready
        }
        // #endregion auto-reset
    }

    sealed class GenerationExample {
        private EcsWorld world;
        private EcsPool<Health> healths;

        // #region generation
        // LeoEcsLite
        struct Target { public EcsPackedEntity Entity; }

        void Aim(ref Target target, int enemy) {
            target.Entity = world.PackEntity(enemy);
            // later
            if (target.Entity.Unpack(world, out int alive)) {
                ref var hp = ref healths.Get(alive);
            }
        }
        // #endregion generation
    }

    sealed class GroupExample {
        private EcsFilter _moving;
        private EcsPool<Position> _positions;
        private EcsPool<Velocity> _velocities;

        void Run() {
            // #region group
            // LeoEcsLite
            foreach (int e in _moving) {
                ref var pos = ref _positions.Get(e);
                ref var vel = ref _velocities.Get(e);
                pos.X += vel.X;
            }
            // #endregion group
        }
    }
}

namespace Docs.Snippets.Reference.Migration.Lite.Di {
    using Leopotam.EcsLite.Di;

    public struct Position { public float X, Y; }
    public struct Velocity { public float X, Y; }
    public struct Frozen { }
    public sealed class GameShared { public float DeltaTime; }

    // #region di
    // LeoEcsLite + ecslite-di
    public sealed class MovementSystem : IEcsRunSystem {
        private readonly EcsFilterInject<Inc<Position, Velocity>, Exc<Frozen>> _moving = default;
        private readonly EcsPoolInject<Position> _positions = default;
        private readonly EcsPoolInject<Velocity> _velocities = default;
        private readonly EcsSharedInject<GameShared> _shared = default;

        public void Run(IEcsSystems systems) {
            foreach (int e in _moving.Value) {
                ref Position pos = ref _positions.Value.Get(e);
                ref Velocity vel = ref _velocities.Value.Get(e);
                pos.X += vel.X * _shared.Value.DeltaTime;
            }
        }
    }
    // bootstrap: systems.Inject().Init();
    // #endregion di

    static class DiBootstrap {
        static void Example(IEcsSystems systems) {
            systems.Inject().Init();
        }
    }
}

namespace Docs.Snippets.Reference.Migration.Lite.Unity {
    using UnityEngine;

    public sealed class GameShared { }
    sealed class MovementSystem : IEcsRunSystem { public void Run(IEcsSystems systems) { } }

    // #region startup
    // LeoEcsLite
    public sealed class Startup : MonoBehaviour {
        private EcsWorld _world;
        private IEcsSystems _systems;

        private void Start() {
            _world = new EcsWorld();
            _systems = new EcsSystems(_world, new GameShared());
            _systems
                .Add(new MovementSystem())
                .Init();
        }

        private void Update() {
            _systems?.Run();
        }

        private void OnDestroy() {
            _systems?.Destroy();
            _world?.Destroy();
        }
    }
    // #endregion startup
}
