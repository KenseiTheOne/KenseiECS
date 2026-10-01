using System;
using KenseiECS;

namespace Docs.Snippets.Filters {
    struct Position : IComponent { public float X, Y; }
    struct Velocity : IComponent { public float X, Y; }
    struct Frozen : IComponent { }
    struct Health : IComponent { public float Value; }
    struct Shield : IComponent { public float Value; }
    struct ViewRequest : IComponent { }

    static class Builder {
        static void Example(World world) {
            // #region builder
            var filter = world.Filter()
                .Inc<Position>()
                .Inc<Velocity>()
                .Exc<Frozen>()
                .End();

            var positions = world.Pool<Position>();
            foreach (int e in filter) {
                ref var pos = ref positions.Get(e);
                world.DestroyEntity(world.GetEntity(e));  // OK: current entity
            }
            // #endregion builder
        }

        static void AnyExample(World world) {
            // #region any
            // Position and (Health or Shield), not Frozen
            var damageable = world.Filter()
                .Inc<Position>()
                .Any<Health>().Any<Shield>()
                .Exc<Frozen>()
                .End();
            // #endregion any
        }
    }

    class SpecSystem : IInitSystem {
        Filter _moving, _notFrozen, _targets;

        public void Init(World world, SharedData shared) {
            // #region specs
            _moving    = world.Filter<Inc<Position, Velocity>, Exc<Frozen>>();
            _notFrozen = world.Filter<Inc<Position>, Exc<Frozen>>();
            _targets   = world.Filter<Inc<Position>, Exc<Frozen>, Any<Health, Shield>>();
            // #endregion specs
        }
    }

    static class Helpers {
        static void Example(Filter filter, int entityIndex) {
            // #region helpers
            int count = filter.Count;                   // matching entities
            bool empty = filter.IsEmpty;
            bool contains = filter.Contains(entityIndex);
            int first = filter.First();                 // throws when empty
            bool found = filter.TryGetFirst(out int e); // false when empty
            int single = filter.Single();               // throws unless exactly one match
            ReadOnlySpan<int> all = filter.Entities;    // valid until the next structural change
            // #endregion helpers
        }
    }

    // #region listener
    class SpawnView : IFilterListener {
        public void OnEntityAdded(Filter filter, int entityIndex) { /* spawn */ }
        public void OnEntityRemoved(Filter filter, int entityIndex) { /* despawn */ }
    }

    class ViewSystem : IInitSystem {
        public void Init(World world, SharedData shared) {
            var viewRequests = world.Filter<Inc<ViewRequest>>();
            viewRequests.AddListener(new SpawnView());
        }
    }
    // #endregion listener
}
