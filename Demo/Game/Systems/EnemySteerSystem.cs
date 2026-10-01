using System;

namespace KenseiECS.Demo {
    /// <summary>
    /// Every enemy chases the player and is pushed apart from its grid neighbours,
    /// so the horde spreads into a crowd instead of collapsing to a point.
    /// Separation is recomputed for half of the horde per frame (by entity parity)
    /// and cached on the Enemy component — the other half reuses last frame's push.
    /// (Stragglers are recycled by GridBuildSystem, which sees every enemy.)
    /// Shows: iterating SharedData (the grid) in cache order and reaching
    /// components by entity index through pools (sparse-set O(1) Get).
    /// </summary>
    public partial class EnemySteerSystem : IRunSystem {
        [Pool] private ComponentPool<Enemy> _enemies;
        [Pool] private ComponentPool<Velocity> _velocities;
        [Shared] private SpatialGrid _grid;
        [Shared] private GameConfig _config;

        private const int MaxNeighbourChecks = 28;

        public void Run(World world) {
            ref var state = ref world.GetSingleton<GameState>();
            float dt = state.Dt;
            var playerEntity = world.GetSingletonEntity<Player>();
            var playerPos = world.Get<Position>(playerEntity);
            float playerR = _config.PlayerRadius;
            float px = playerPos.X, py = playerPos.Y;

            float knockDecay = MathF.Max(0f, 1f - 7f * dt);
            float flashDecay = dt * 6f;
            float sepK = _config.Separation;
            int parity = world.Tick & 1;

            var g = _grid;
            var gx = g.X;
            var gy = g.Y;
            var gr = g.R;
            var cellStart = g.CellStart;
            const int dim = SpatialGrid.Dim;

            for (int cy = 0; cy < dim; cy++) {
                int ny0 = cy > 0 ? cy - 1 : 0;
                int ny1 = cy < dim - 1 ? cy + 1 : dim - 1;
                for (int cx = 0; cx < dim; cx++) {
                    int cell = cy * dim + cx;
                    int start = cellStart[cell];
                    int end = cellStart[cell + 1];
                    if (start == end) {
                        continue;
                    }
                    int nx0 = cx > 0 ? cx - 1 : 0;
                    int nx1 = cx < dim - 1 ? cx + 1 : dim - 1;

                    for (int i = start; i < end; i++) {
                        int e = g.Entity[i];
                        ref var en = ref _enemies.Get(e);
                        float x = gx[i], y = gy[i], r = gr[i];

                        if (((e + parity) & 1) == 0) {
                            float sx = 0f, sy = 0f;
                            int checks = 0;
                            // Own row first, so a crowded cell is never starved by the budget.
                            for (int row = 0; row < 3 && checks < MaxNeighbourChecks; row++) {
                                int ny = row == 0 ? cy : row == 1 ? cy - 1 : cy + 1;
                                if (ny < ny0 || ny > ny1) {
                                    continue;
                                }
                                int rowBase = ny * dim;
                                int j0 = cellStart[rowBase + nx0];
                                int j1 = cellStart[rowBase + nx1 + 1];   // cells of a row are contiguous
                                for (int j = j0; j < j1; j++) {
                                    if (j == i) {
                                        continue;
                                    }
                                    float dx = x - gx[j];
                                    float dy = y - gy[j];
                                    float rr = r + gr[j];
                                    float d2 = dx * dx + dy * dy;
                                    if (d2 < rr * rr) {
                                        if (d2 > 1e-6f) {
                                            float d = MathF.Sqrt(d2);
                                            float f = (rr - d) / d;
                                            sx += dx * f;
                                            sy += dy * f;
                                        } else {
                                            sx += (i & 1) == 0 ? 0.1f : -0.1f;
                                            sy += (i & 2) == 0 ? 0.1f : -0.1f;
                                        }
                                    }
                                    if (++checks >= MaxNeighbourChecks) {
                                        break;
                                    }
                                }
                            }
                            en.SepX = sx * sepK;
                            en.SepY = sy * sepK;
                        }

                        float rx = px - x;
                        float ry = py - y;
                        float dist2 = rx * rx + ry * ry;

                        float dist = MathF.Sqrt(dist2) + 1e-5f;
                        float speed = en.Speed;
                        float vx = rx / dist * speed;
                        float vy = ry / dist * speed;

                        // Do not climb onto the player: past contact, only the push remains.
                        float contact = r + playerR;
                        if (dist < contact) {
                            float push = (contact - dist) * 6f;
                            vx = -rx / dist * push;
                            vy = -ry / dist * push;
                        }

                        float sepX = en.SepX, sepY = en.SepY;
                        float sep2 = sepX * sepX + sepY * sepY;
                        float maxSep = speed * 1.6f;
                        if (sep2 > maxSep * maxSep) {
                            float s = maxSep / MathF.Sqrt(sep2);
                            sepX *= s;
                            sepY *= s;
                        }

                        ref var v = ref _velocities.Get(e);
                        v.X = vx + sepX + en.KnockX;
                        v.Y = vy + sepY + en.KnockY;

                        en.KnockX *= knockDecay;
                        en.KnockY *= knockDecay;
                        en.Flash = en.Flash > flashDecay ? en.Flash - flashDecay : 0f;
                        en.BladeCooldown -= dt;
                    }
                }
            }
        }
    }
}
