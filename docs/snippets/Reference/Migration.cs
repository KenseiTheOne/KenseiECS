using System.Collections.Generic;
using KenseiECS;

// KenseiECS side of docs/migration-from-leoecslite.md. The LeoEcsLite side of each
// comparison is in MigrationLite.cs under the same region name.

namespace Docs.Snippets.Reference.Migration.Kensei {
    public struct Position : IComponent { public float X, Y; }
    public struct Velocity : IComponent { public float X, Y; }
    public struct Health : IComponent { public float Value; }
    public struct DamageEvent : IComponent { public float Value; }

    sealed class Snippets {
        private World world;
        private CommandBuffer _buffer = new CommandBuffer();
        private Filter _projectiles;
        private Filter _targets;

        void CreateEntity() {
            // #region create-entity
            // KenseiECS
            Entity e = world.CreateEntity(new Position { X = 10f });
            world.Add(e, new Velocity());
            // #endregion create-entity
        }

        void DeferredChanges() {
            // #region deferred-changes
            // KenseiECS: record, then play back
            foreach (int e in _projectiles) {
                foreach (int t in _targets) {
                    if (Hits(e, t)) {
                        _buffer.DestroyEntity(world.GetEntity(e));
                        // Set, not Add: another projectile may hit the same target this frame
                        _buffer.Set(world.GetEntity(t), new DamageEvent { Value = 10 });
                        break;
                    }
                }
            }
            _buffer.Playback(world);
            // #endregion deferred-changes
        }

        void AddValue(Entity e) {
            // #region add-value
            // KenseiECS
            world.Add(e, new Health { Value = 100 });
            // #endregion add-value
        }

        private static bool Hits(int projectile, int target) => projectile != target;
    }

    sealed class AutoResetExample {
        private World world;

        // #region auto-reset
        // KenseiECS
        struct Inventory : IComponent, IAutoReset<Inventory> {
            public List<int> Items;
            public void AutoReset(ref Inventory c) {
                c.Items?.Clear();
                c.Items = null;
            }
        }

        void Spawn(Entity e) {
            world.Add(e, new Inventory { Items = new List<int>() });
        }
        // #endregion auto-reset
    }

    sealed class GenerationExample {
        private World world;

        // #region generation
        // KenseiECS
        struct Target : IComponent { public Entity Entity; }

        void Aim(ref Target target, int enemy) {
            target.Entity = world.GetEntity(enemy);
            // later
            if (world.IsAlive(target.Entity)) {
                ref var hp = ref world.Get<Health>(target.Entity);
            }
        }
        // #endregion generation
    }

    sealed class GroupExample {
        private Group<Position, Velocity> _group;

        void Run() {
            // #region group
            // KenseiECS, with a group
            var pos = _group.Data1;
            var vel = _group.Data2;
            for (int i = 0; i < pos.Length; i++) {
                pos[i].X += vel[i].X;
            }
            // #endregion group
        }
    }
}

namespace Docs.Snippets.Reference.Migration.Kensei.Generator {
    public struct Position : IComponent { public float X, Y; }
    public struct Velocity : IComponent { public float X, Y; }
    public struct Frozen : IComponent { }
    public sealed class GameShared { public float DeltaTime; }

    // #region di
    // KenseiECS + generator
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
            }
        }
    }
    // bootstrap: nothing extra; the generated Init runs in systems.Init()
    // #endregion di

    static class Check {
        // The generated part implements IInitSystem.
        static IInitSystem AsInit(MovementSystem system) => system;
    }
}

namespace Docs.Snippets.Reference.Migration.Kensei.Unity {
    using Docs.Snippets.Reference.UnityLayer;

    public sealed class GameShared { }
    sealed class MovementSystem : IRunSystem { public void Run(World world) { } }
    sealed class PhysicsSystem : IRunSystem { public void Run(World world) { } }
    sealed class SyncTransformSystem : IRunSystem { public void Run(World world) { } }

    // #region startup
    // KenseiECS
    public sealed class GameBootstrap : EcsBootstrap {
        protected override void Configure(SystemsRunner update, SystemsRunner fixedUpdate, SystemsRunner lateUpdate, SharedData shared) {
            shared.Add(new GameShared());
            update.Add(new MovementSystem());
            fixedUpdate.Add(new PhysicsSystem());
            lateUpdate.Add(new SyncTransformSystem());
        }
    }
    // #endregion startup
}
