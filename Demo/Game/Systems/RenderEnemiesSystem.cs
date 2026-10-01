namespace KenseiECS.Demo {
    /// <summary>
    /// Render phase: writes every on-screen enemy with its hit flash.
    /// Shows: linear iteration over a pool's dense arrays (RawData/RawEntities)
    /// — the Enemy pool has no gaps, so no filter is needed when one component
    /// already identifies the set.
    /// </summary>
    public partial class RenderEnemiesSystem : IRunSystem {
        [Pool] private ComponentPool<Enemy> _enemies;
        [Pool] private ComponentPool<Position> _positions;
        [Pool] private ComponentPool<Radius> _radii;
        [Shared] private SpriteBuffer _out;

        public void Run(World world) {
            var data = _enemies.RawData;
            var entities = _enemies.RawEntities;
            for (int i = 0, n = _enemies.Count; i < n; i++) {
                int e = entities[i];
                var p = _positions.Get(e);
                _out.Push(p.X, p.Y, _radii.Get(e).Value, data[i].Kind, data[i].Flash);
            }
        }
    }
}
