namespace Docs.Snippets.WorldConfig {
    using KenseiECS;

    struct Position : IComponent { public float X, Y; }
    struct Projectile : IComponent { public float Speed; }

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

        void Reserve(World world, int maxEntities) {
            // #region reserve
            // At load: entity slots plus the sparse and dense side of every pool and filter,
            // including the ones systems create later.
            world.Reserve(maxEntities, maxEntities);
            // #endregion reserve
        }

        void ReserveOne(World world, int maxEntities, int maxProjectiles) {
            // #region reserve-one
            world.Reserve(maxEntities);                                         // entity slots, every sparse side
            world.Pool<Position>().Reserve(maxEntities);                        // dense arrays of one pool
            world.Pool<Projectile>().Reserve(maxProjectiles);
            // A filter holds at most as many entities as its rarest Inc component.
            world.Filter<Inc<Projectile, Position>>().Reserve(maxProjectiles);  // dense array of one filter
            // #endregion reserve-one
        }

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
