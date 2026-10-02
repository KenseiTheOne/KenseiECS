using System.Collections.Generic;
using System.IO;
using KenseiECS;

// Code for docs/faq.md. One namespace per answer so type names can repeat.

namespace Docs.Snippets.Reference.Faq {
    public struct Position : IComponent { public float X, Y; }
    public struct Velocity : IComponent { public float X, Y; }
    public struct Health : IComponent { public float Value; }
    public struct DamageEvent : IComponent { public float Value; }
    public struct Projectile : IComponent { }
    public struct Expired : IComponent { }

    static class CreateWithComponent {
        static void Example(World world) {
            // #region create-entity
            Entity e = world.CreateEntity(new Position { X = 1f });
            world.Add(e, new Velocity());
            // #endregion create-entity
        }
    }

    sealed class TargetingSystem {
        private Filter _seekers;
        private ComponentPool<Target> _targets;

        // #region entity-reference
        public struct Target : IComponent {
            public Entity Enemy;
        }

        public void Aim(World world, int enemyIndex) {
            foreach (int e in _seekers) {
                ref var target = ref _targets.Get(e);
                target.Enemy = world.GetEntity(enemyIndex);   // convert inside the loop
            }
        }

        // later
        public void Attack(World world, Target target) {
            if (world.IsAlive(target.Enemy)) {
                ref var hp = ref world.Get<Health>(target.Enemy);
            }
        }
        // #endregion entity-reference
    }

    sealed class ExpireSystem : IRunSystem {
        private Filter _expired;

        public void Run(World world) {
            // #region destroy-current
            foreach (int e in _expired) {
                world.DestroyEntity(world.GetEntity(e));
            }
            // #endregion destroy-current
        }
    }

    sealed class ProjectileHitSystem : IRunSystem {
        private Filter _projectiles;
        private Filter _targets;

        // #region command-buffer
        private readonly CommandBuffer _buffer = new CommandBuffer();

        public void Run(World world) {
            foreach (int e in _projectiles) {
                foreach (int t in _targets) {
                    if (Hits(e, t)) {
                        _buffer.DestroyEntity(world.GetEntity(e));
                        // Set, not Add: another projectile may hit the same target this frame
                        _buffer.Set(world.GetEntity(t), new DamageEvent { Value = 10 });
                        break;   // a projectile hits one target and is destroyed once
                    }
                }
            }
            _buffer.Playback(world);
        }
        // #endregion command-buffer

        private static bool Hits(int projectile, int target) => projectile != target;
    }

    sealed class GroupMoveSystem : IRunSystem {
        private Group<Position, Velocity> _group;

        public void Run(World world) {
            // #region group-loop
            var pos = _group.Data1;
            var vel = _group.Data2;
            for (int i = 0; i < pos.Length; i++) {
                pos[i].X += vel[i].X;
            }
            // #endregion group-loop
        }
    }

    sealed class SyncViewsSystem : IInitSystem, IRunSystem {
        private Filter _views;
        private ComponentPool<Position> _positions;
        private int _lastSeen;

        // #region change-consumer
        public void Init(World world, SharedData shared) {
            _positions = world.Pool<Position>();
            _positions.TrackChanges();
            _views = world.Filter().Inc<Position>().End();
        }

        public void Run(World world) {
            foreach (int e in _views) {
                if (_positions.ChangedSince(e, _lastSeen)) {
                    SyncTransform(e);
                }
            }
            _lastSeen = world.ChangeVersion;
        }
        // #endregion change-consumer

        private void SyncTransform(int entity) { }
    }

    sealed class PushSystem : IRunSystem {
        private Filter _pushed;
        private ComponentPool<Position> _positions;

        public void Run(World world) {
            foreach (int e in _pushed) {
                // #region change-producer
                _positions.Modify(e).X += 1f;
                // #endregion change-producer
            }
        }
    }

    public struct Inventory : IComponent {
        public List<int> Items;
    }

    sealed class InventoryFormatter : IComponentFormatter<Inventory> {
        public void Write(BinaryWriter writer, ref Inventory component) {
            writer.Write(component.Items.Count);
            foreach (int item in component.Items) {
                writer.Write(item);
            }
        }

        public void Read(BinaryReader reader, out Inventory component) {
            int count = reader.ReadInt32();
            component = new Inventory { Items = new List<int>(count) };
            for (int i = 0; i < count; i++) {
                component.Items.Add(reader.ReadInt32());
            }
        }
    }

    static class SaveLoad {
        static void Example(World world, string path) {
            // #region save-load
            var serializer = new WorldSerializer();
            serializer.Register(new InventoryFormatter());   // only for components with reference fields

            using (var file = File.Create(path)) {
                serializer.Save(world, file);
            }

            world.Clear();
            using (var file = File.OpenRead(path)) {
                serializer.Load(world, file);
            }
            // #endregion save-load
        }
    }
}

namespace Docs.Snippets.Reference.Faq.Generator {
    public struct Position : IComponent { public float X, Y; }
    public struct Velocity : IComponent { public float X, Y; }
    public struct Frozen : IComponent { }
    public sealed class SpawnConfig { }

    // #region generator
    public sealed partial class MovementSystem : IRunSystem {
        [Inc(typeof(Position), typeof(Velocity))] [Exc(typeof(Frozen))]
        private Filter _moving;
        [Pool] private ComponentPool<Position> _positions;
        [Group] private Group<Position, Velocity> _group;
        [Shared("enemies")] private SpawnConfig _enemies;

        partial void OnInit(World world, SharedData shared) {
            // optional, runs after the fields are filled
        }

        public void Run(World world) { }
    }
    // #endregion generator
}

namespace Docs.Snippets.Reference.Faq.Unity {
    using System;
    using Docs.Snippets.Reference.UnityLayer;

    sealed class ArenaConfig { }
    struct BounceEvent : IComponent { }
    sealed class MovementSystem : IRunSystem { public void Run(World world) { } }
    sealed class BounceSystem : IRunSystem { public void Run(World world) { } }
    sealed class SyncTransformSystem : IRunSystem { public void Run(World world) { } }

    [Serializable]
    public struct Position : IComponent { public float X, Y; }

    // #region bootstrap
    public sealed class GameBootstrap : EcsBootstrap {
        protected override void Configure(SystemsRunner update, SystemsRunner fixedUpdate, SystemsRunner lateUpdate, SharedData shared) {
            shared.Add(new ArenaConfig());
            update.Add(new MovementSystem()).OneFrame<BounceEvent>();
            fixedUpdate.Add(new BounceSystem());
            lateUpdate.Add(new SyncTransformSystem());
        }
    }
    // #endregion bootstrap

    // #region provider
    public sealed class PositionProvider : EcsComponentProvider<Position> { }
    // #endregion provider
}
