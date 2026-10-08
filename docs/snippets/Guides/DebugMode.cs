namespace Docs.Snippets.DebugMode {
    using KenseiECS;

    struct Health : IComponent { public int Value; }

    class Example {
        void StaleHandle(World world) {
            // #region stale
            var entity = world.CreateEntity(new Health { Value = 10 });
            world.DestroyEntity(entity);

            // Release: undefined; the slot may already belong to another entity.
            // KENSEI_DEBUG: throws InvalidOperationException.
            world.Get<Health>(entity);

            // Safe in both modes: check first.
            if (world.IsAlive(entity)) {
                world.Get<Health>(entity).Value -= 1;
            }
            // #endregion stale
        }

        void DebugOnly(World world, Entity entity) {
            // #region debug-only
            world.SetName(entity, "Player");     // a no-op in release; arguments are still evaluated

            #if KENSEI_DEBUG
            EcsProfiler.Enable(world);           // EcsProfiler exists only under KENSEI_DEBUG
            #endif
            // #endregion debug-only
        }
    }
}
