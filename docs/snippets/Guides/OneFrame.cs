namespace Docs.Snippets.OneFrame {
    using KenseiECS;

    struct Health : IComponent { public float Value; }
    struct Hit { public int Amount; }

    // #region event-type
    struct DamageEvent : IComponent { public float Value; }
    // #endregion event-type

    class Registration {
        void Register(World world, Entity entity) {
            var systems = new SystemsRunner(world);
            // #region register
            systems.OneFrame<DamageEvent>();
            // #endregion register

            // #region produce
            // In a system — create event
            world.Add(entity, new DamageEvent { Value = 10 });
            // All systems later in the pipeline see it this frame
            // Removed automatically at the end of Run()
            // #endregion produce
        }

        void Pipeline(World world) {
            // #region del-here
            var systems = new SystemsRunner(world)
                .Add(new DamageSystem())          // sees DamageEvent
                .DelHere<DamageEvent>()           // removes every DamageEvent right here
                .Add(new SpawnSystem());          // does not see it
            // #endregion del-here
        }

        void EventEntity(World world) {
            // #region event-entity
            // An entity whose only component is the event:
            // the cleanup removes the component, the empty entity is destroyed with it.
            world.CreateEntity(new DamageEvent { Value = 5 });
            // #endregion event-entity
        }
    }

    class DamageSystem : IRunSystem { public void Run(World world) { } }
    class SpawnSystem : IRunSystem { public void Run(World world) { } }

    class Buffers {
        void Add(World world, Entity target) {
            // #region add-event
            world.AddEvent(target, new Hit { Amount = 10 });
            world.AddEvent(target, new Hit { Amount = 5 });
            // #endregion add-event
        }
    }

    // #region read-events
    class HitSystem : IInitSystem, IRunSystem {
        Filter _hitTargets;
        ComponentPool<EventBuffer<Hit>> _hits;
        ComponentPool<Health> _health;

        public void Init(World world, SharedData shared) {
            _hitTargets = world.Filter().Inc<EventBuffer<Hit>>().Inc<Health>().End();
            _hits = world.Pool<EventBuffer<Hit>>();
            _health = world.Pool<Health>();
        }

        public void Run(World world) {
            foreach (int e in _hitTargets) {
                var hits = _hits.Get(e).Values;
                for (int i = 0; i < hits.Count; i++) {
                    _health.Get(e).Value -= hits[i].Amount;
                }
            }
        }
    }
    // #endregion read-events

    class BufferRegistration {
        void Register(SystemsRunner systems) {
            // #region register-buffer
            systems.OneFrame<EventBuffer<Hit>>();            // lists are pooled and reused
            // #endregion register-buffer
        }
    }
}
