namespace Docs.Snippets.WorldConfig {
    using KenseiECS;

    class Example {
        World Create() {
            // #region config
            var config = new WorldConfig {
                InitialEntityCapacity = 512,       // entity slots, mask words, filter sparse arrays
                InitialPoolSparseCapacity = 512,   // per-pool sparse array
                InitialPoolDenseCapacity = 128,    // per-pool and per-filter dense arrays
                InitialPoolCount = 64              // pool registry size
            };
            var world = new World(config);
            // #endregion config
            return world;
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
