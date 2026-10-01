namespace KenseiECS.Demo {
    /// <summary>
    /// Rebuilds the uniform spatial grid from every enemy, centred on the player.
    /// On the way it recycles stragglers: an enemy farther than RecycleDistance
    /// reappears just off-screen on the opposite side, ahead of the player. This
    /// is the one pass that sees every enemy, so nothing can drift out of the
    /// grid window for good — even with `steer` switched off for a while.
    /// Shows: SharedData holding a mutable service object (SpatialGrid) that later
    /// systems query; a filter combined with direct pool reads and writes; zero allocations.
    /// </summary>
    public partial class GridBuildSystem : IRunSystem {
        [Inc(typeof(Enemy), typeof(Position), typeof(Radius))] private Filter _enemies;
        [Pool] private ComponentPool<Enemy> _enemyData;
        [Pool] private ComponentPool<Position> _positions;
        [Pool] private ComponentPool<Radius> _radii;
        [Shared] private SpatialGrid _grid;
        [Shared] private GameConfig _config;

        public void Run(World world) {
            var center = world.Get<Position>(world.GetSingletonEntity<Player>());
            float recycle2 = _config.RecycleDistance * _config.RecycleDistance;
            float spawn = _config.SpawnDistance;
            _grid.Begin(center.X, center.Y);
            foreach (int e in _enemies) {
                ref var p = ref _positions.Get(e);
                float rx = center.X - p.X;
                float ry = center.Y - p.Y;
                float dist2 = rx * rx + ry * ry;
                if (dist2 > recycle2) {
                    // Fell far behind: reappear ahead on the opposite side.
                    float inv = spawn / System.MathF.Sqrt(dist2);
                    p.X = center.X + rx * inv;
                    p.Y = center.Y + ry * inv;
                    ref var enemy = ref _enemyData.Get(e);
                    enemy.KnockX = enemy.KnockY = 0f;
                }
                _grid.Insert(e, p.X, p.Y, _radii.Get(e).Value);
            }
            _grid.End();
        }
    }
}
