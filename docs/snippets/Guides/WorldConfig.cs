namespace Docs.Snippets.WorldConfig {
    using KenseiECS;

    struct Position : IComponent { public float X, Y; }
    struct Velocity : IComponent { public float X, Y; }
    struct Projectile : IComponent { public float Speed; }
    struct Burning : IComponent { public float Left; }

    class Example {
        World Create() {
            // #region config
            var config = new WorldConfig {
                InitialEntityCapacity = 512,       // entity slots and mask words
                InitialPoolSparseCapacity = 512,   // per-pool sparse array
                InitialPoolDenseCapacity = 128,    // per-pool and per-filter dense arrays
                InitialPoolCount = 64              // pool registry size
            };
            var world = new World(config);
            // #endregion config
            return world;
        }

        void Reserve(World world, int maxUnits, int maxProjectiles) {
            // #region reserve
            // At load: the world's own entity storage, then every type whose count changes during play.
            int maxEntities = maxUnits + maxProjectiles;
            world.ReserveEntities(maxEntities);
            world.Pool<Position>().Reserve(maxEntities);
            world.Pool<Velocity>().Reserve(maxEntities);
            world.Pool<Projectile>().Reserve(maxProjectiles);
            // Filters over reserved types are reserved by them, the ones systems create later too:
            // Filter<Inc<Projectile, Position>> has room for maxProjectiles entities.
            // Singletons and data created at load need nothing.
            // #endregion reserve
        }

        void ReserveFilter(World world, int maxBurning) {
            // #region reserve-filter
            // Burning is not reserved, so the filter is not reserved by its types: reserve it by hand.
            world.Filter<Inc<Position, Burning>>().Reserve(maxBurning);
            // #endregion reserve-filter
        }

        // #region reserve-all
        // A small world that reserves every pool. Registered last (in an EcsBootstrap: last in lateUpdate),
        // so its Init runs after every other system created its pools; a pool created later is not reserved.
        sealed class ReserveEveryPool : IInitSystem {
            private readonly int _entities;

            public ReserveEveryPool(int entities) => _entities = entities;

            public void Init(World world, SharedData shared) {
                world.ReserveEntities(_entities);
                foreach (var pool in world.ActivePools) {
                    pool.Reserve(_entities);
                }
            }
        }
        // #endregion reserve-all

        World Partial() {
            // #region partial
            var config = WorldConfig.Default();
            config.InitialEntityCapacity = 10_000;   // only raise what you need
            var world = new World(config);
            // #endregion partial
            return world;
        }
    }
}
