namespace KenseiECS.Demo {
    /// <summary>
    /// Enemies touching the player deal contact damage, followed by a short
    /// invulnerability window; touching enemies are shoved back with a zero-damage Hit.
    /// Ends the run when HP reaches zero (unless HordeGame.GodMode).
    /// Shows: singletons (Player, GameState) read and written by ref, grid queries,
    /// a Hit event used purely as a knockback carrier.
    /// </summary>
    public partial class ContactDamageSystem : IRunSystem {
        [Inc(typeof(Player), typeof(Position))] private Filter _player;
        [Pool] private ComponentPool<Player> _players;
        [Pool] private ComponentPool<Position> _positions;
        [Pool] private ComponentPool<Enemy> _enemies;
        [Shared] private SpatialGrid _grid;
        [Shared] private GameConfig _config;

        /// <summary> Set by HordeGame.GodMode: damage is still taken but never kills. </summary>
        public bool GodMode;

        public void Run(World world) {
            ref var state = ref world.GetSingleton<GameState>();
            float dt = state.Dt;
            var g = _grid;

            foreach (int e in _player) {
                ref var player = ref _players.Get(e);
                var p = _positions.Get(e);
                player.Invulnerable -= dt;
                player.Flash = player.Flash > dt * 4f ? player.Flash - dt * 4f : 0f;

                float worst = 0f;
                float r = _config.PlayerRadius;
                int x0 = g.CellX(p.X - 3f), x1 = g.CellX(p.X + 3f);
                int y0 = g.CellY(p.Y - 3f), y1 = g.CellY(p.Y + 3f);
                bool canHurt = player.Invulnerable <= 0f;

                for (int cy = y0; cy <= y1; cy++) {
                    int j0 = g.CellStart[cy * SpatialGrid.Dim + x0];
                    int j1 = g.CellStart[cy * SpatialGrid.Dim + x1 + 1];
                    for (int j = j0; j < j1; j++) {
                        float dx = g.X[j] - p.X;
                        float dy = g.Y[j] - p.Y;
                        float rr = g.R[j] + r + 0.05f;
                        float d2 = dx * dx + dy * dy;
                        if (d2 > rr * rr) {
                            continue;
                        }
                        int target = g.Entity[j];
                        float damage = _enemies.Get(target).ContactDamage;
                        if (damage > worst) {
                            worst = damage;
                        }
                        if (canHurt) {
                            float len = System.MathF.Sqrt(d2) + 1e-4f;
                            world.AddEvent(world.GetEntity(target), new Hit { KnockX = dx / len * 16f, KnockY = dy / len * 16f });
                        }
                    }
                }

                if (canHurt && worst > 0f) {
                    player.Hp -= worst;
                    player.Invulnerable = _config.InvulnerableTime;
                    player.Flash = 1f;
                    if (player.Hp <= 0f) {
                        if (GodMode) {
                            player.Hp = player.MaxHp;
                        } else {
                            player.Hp = 0f;
                            state.Status = GameStatus.GameOver;
                        }
                    }
                }
            }
        }
    }
}
