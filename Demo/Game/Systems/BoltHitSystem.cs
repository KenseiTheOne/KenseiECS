namespace KenseiECS.Demo {
    /// <summary>
    /// Bolts against the enemy grid. Each hit appends a Hit to the enemy's
    /// EventBuffer&lt;Hit&gt;; a bolt that runs out of pierce is destroyed.
    /// Shows: EventBuffer + world.AddEvent (any number of events per entity per
    /// frame, pooled lists), and destroying the current entity inside foreach —
    /// safe because filters iterate in reverse. Bolt.LastHit is an Entity handle,
    /// not an int index, because it outlives the frame.
    /// </summary>
    public partial class BoltHitSystem : IRunSystem {
        [Inc(typeof(Bolt), typeof(Position), typeof(Velocity), typeof(Radius))] private Filter _bolts;
        [Pool] private ComponentPool<Bolt> _boltData;
        [Pool] private ComponentPool<Position> _positions;
        [Pool] private ComponentPool<Velocity> _velocities;
        [Pool] private ComponentPool<Radius> _radii;
        [Shared] private SpatialGrid _grid;
        [Shared] private GameConfig _config;

        public void Run(World world) {
            var g = _grid;
            var pp = world.Get<Position>(world.GetSingletonEntity<Player>());
            float recycle2 = _config.RecycleDistance * _config.RecycleDistance;
            foreach (int e in _bolts) {
                ref var bolt = ref _boltData.Get(e);
                var p = _positions.Get(e);
                float ox = p.X - pp.X, oy = p.Y - pp.Y;
                if (ox * ox + oy * oy > recycle2) {
                    // Far off-screen: gone even if `lifetime` is switched off.
                    world.DestroyEntity(world.GetEntity(e));
                    continue;
                }
                var v = _velocities.Get(e);
                float r = _radii.Get(e).Value;
                int cx = g.CellX(p.X), cy = g.CellY(p.Y);
                bool spent = false;

                for (int ny = cy - 1; ny <= cy + 1 && !spent; ny++) {
                    if ((uint)ny >= SpatialGrid.Dim) {
                        continue;
                    }
                    int x0 = cx > 0 ? cx - 1 : 0;
                    int x1 = cx < SpatialGrid.Dim - 1 ? cx + 1 : SpatialGrid.Dim - 1;
                    int j0 = g.CellStart[ny * SpatialGrid.Dim + x0];
                    int j1 = g.CellStart[ny * SpatialGrid.Dim + x1 + 1];
                    for (int j = j0; j < j1; j++) {
                        float dx = g.X[j] - p.X;
                        float dy = g.Y[j] - p.Y;
                        float rr = g.R[j] + r;
                        if (dx * dx + dy * dy > rr * rr) {
                            continue;
                        }
                        var target = world.GetEntity(g.Entity[j]);
                        if (target == bolt.LastHit) {
                            continue;
                        }
                        world.AddEvent(target, new Hit {
                            Damage = bolt.Damage, KnockX = v.X * 0.25f, KnockY = v.Y * 0.25f
                        });
                        bolt.LastHit = target;
                        if (--bolt.PierceLeft < 0) {
                            spent = true;
                            break;
                        }
                    }
                }

                if (spent) {
                    world.DestroyEntity(world.GetEntity(e));
                }
            }
        }
    }
}
