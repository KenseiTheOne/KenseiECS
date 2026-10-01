namespace Docs.Snippets.WorldLifecycle {
    using KenseiECS;

    class Example {
        void Lifecycle(World world, SystemsRunner systems) {
            // #region lifecycle
            systems.Warmup(); // at startup: Init + JIT pre-touch + memory pre-alloc
            // ... gameplay ...
            world.Clear();    // on restart: reset data, preserve allocations, invalidate all handles
            // ... more gameplay ...
            world.Destroy();  // at shutdown: null everything for GC
            // #endregion lifecycle
        }
    }
}
