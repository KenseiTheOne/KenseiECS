namespace Docs.Snippets.ComponentListeners {
    using System;
    using KenseiECS;

    struct Health : IComponent { public int Value; }

    // #region hooks
    class HealthHooks : IComponentListener<Health> {
        public void OnAdded(int entityIndex, ref Health c) => c.Value = Math.Min(c.Value, 100);
        public void OnRemoved(int entityIndex, ref Health c) { /* data still intact, AutoReset runs after */ }
    }
    // #endregion hooks

    class Registration {
        void Register(World world) {
            // #region register
            var hooks = new HealthHooks();
            world.Pool<Health>().AddListener(hooks);

            // later
            world.Pool<Health>().RemoveListener(hooks);
            // #endregion register
        }
    }
}
