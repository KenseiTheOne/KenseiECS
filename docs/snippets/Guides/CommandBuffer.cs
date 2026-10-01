namespace Docs.Snippets.CommandBuffer {
    using KenseiECS;

    struct Projectile : IComponent { public float Radius; }
    struct Target : IComponent { }
    struct Explosion : IComponent { public float Radius; }
    struct DamageEvent : IComponent { public int Value; }

    // #region collisions
    partial class CollisionSystem : IInitSystem, IRunSystem {
        readonly CommandBuffer _buffer = new CommandBuffer();   // one per system; zero allocations after warmup
        Filter _projectiles;
        Filter _targets;

        public void Init(World world, SharedData shared) {
            _projectiles = world.Filter().Inc<Projectile>().End();
            _targets = world.Filter().Inc<Target>().End();
        }

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
    }
    // #endregion collisions

    partial class CollisionSystem {
        bool Hits(int projectile, int target) => projectile % 2 == target % 2;
    }

    class PendingExample {
        void Record(World world, CommandBuffer buffer, Entity projectile) {
            // #region pending
            PendingEntity blast = buffer.CreateEntity(new Explosion { Radius = 3f });
            buffer.Add(blast, new DamageEvent { Value = 25 });   // targets the entity created above
            buffer.DestroyEntity(projectile);                   // skipped if already dead at playback

            buffer.Playback(world);                             // applies in order, then clears the buffer
            // #endregion pending
        }
    }
}
