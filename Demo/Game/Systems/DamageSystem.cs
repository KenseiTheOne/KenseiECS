namespace KenseiECS.Demo {
    /// <summary>
    /// Applies this frame's hits to enemy health and flags the dead with Died.
    /// Shows: consuming EventBuffer&lt;Hit&gt; (registered as OneFrame, so the
    /// buffers and their pooled lists vanish at the end of the frame) and adding
    /// a OneFrame event component (Died) to the entity being iterated.
    /// </summary>
    public partial class DamageSystem : IRunSystem {
        // #region consume-hits
        [Inc(typeof(EventBuffer<Hit>), typeof(Health), typeof(Enemy))] private Filter _hit;
        [Pool] private ComponentPool<EventBuffer<Hit>> _hits;
        [Pool] private ComponentPool<Health> _health;
        [Pool] private ComponentPool<Enemy> _enemies;
        [Pool] private ComponentPool<Died> _died;

        public void Run(World world) {
            foreach (int e in _hit) {
                var events = _hits.Get(e).Values;
                float total = 0f;
                for (int i = 0; i < events.Count; i++) {
                    total += events[i].Damage;
                }
                if (total <= 0f) {
                    continue;
                }
                ref var hp = ref _health.Get(e);
                _enemies.Get(e).Flash = 1f;
                hp.Value -= total;
                if (hp.Value <= 0f && !_died.Has(e)) {
                    _died.Add(e, new Died());
                }
            }
        }
        // #endregion consume-hits
    }
}
