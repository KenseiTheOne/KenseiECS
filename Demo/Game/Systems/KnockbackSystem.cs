namespace KenseiECS.Demo {
    /// <summary>
    /// Adds the knockback of this frame's hits to every light enemy.
    /// Shows: an [Exc] filter — tanks and elites carry the Heavy tag and are
    /// simply not in this filter — and a second consumer of the same
    /// EventBuffer&lt;Hit&gt; events that DamageSystem reads.
    /// </summary>
    public partial class KnockbackSystem : IRunSystem {
        // #region knockback
        [Inc(typeof(EventBuffer<Hit>), typeof(Enemy))] [Exc(typeof(Heavy))] private Filter _hitLight;
        [Pool] private ComponentPool<EventBuffer<Hit>> _hits;
        [Pool] private ComponentPool<Enemy> _enemies;

        public void Run(World world) {
            foreach (int e in _hitLight) {
                var events = _hits.Get(e).Values;
                ref var enemy = ref _enemies.Get(e);
                for (int i = 0; i < events.Count; i++) {
                    var hit = events[i];
                    enemy.KnockX += hit.KnockX;
                    enemy.KnockY += hit.KnockY;
                }
            }
        }
        // #endregion knockback
    }
}
