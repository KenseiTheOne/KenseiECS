using System;

namespace KenseiECS.Demo {
    /// <summary>
    /// Orbiting blades: keeps one blade entity per BladeWeapon.Count, spins them
    /// around the player and cuts every enemy they touch (per-enemy cooldown).
    /// Shows: a filter that starts matching the moment a component is added —
    /// the player has no BladeWeapon until the upgrade is picked; CommandBuffer
    /// for creating the blade entities from inside the player loop.
    /// </summary>
    public partial class BladeSystem : IRunSystem {
        [Inc(typeof(Player), typeof(BladeWeapon), typeof(Position))] private Filter _owners;
        [Inc(typeof(Blade), typeof(Position))] private Filter _blades;
        [Pool] private ComponentPool<Player> _players;
        [Pool] private ComponentPool<BladeWeapon> _weapons;
        [Pool] private ComponentPool<Blade> _bladeData;
        [Pool] private ComponentPool<Position> _positions;
        [Pool] private ComponentPool<Enemy> _enemies;
        [Shared] private SpatialGrid _grid;
        [Shared] private GameConfig _config;

        private readonly CommandBuffer _buffer = new();

        public void Run(World world) {
            float dt = world.GetSingleton<GameState>().Dt;

            foreach (int e in _owners) {
                ref var weapon = ref _weapons.Get(e);
                for (int k = _blades.Count; k < weapon.Count; k++) {
                    var blade = _buffer.CreateEntity(new Blade());
                    _buffer.Add(blade, new Position { X = _positions.Get(e).X, Y = _positions.Get(e).Y });
                    _buffer.Add(blade, new Radius { Value = _config.BladeRadius });
                }
            }
            _buffer.Playback(world);

            foreach (int e in _owners) {
                ref var weapon = ref _weapons.Get(e);
                var center = _positions.Get(e);
                ref var player = ref _players.Get(e);
                float damage = weapon.Damage * player.DamageMul;
                // Quick Hands shortens every cooldown; for blades that means a faster spin
                // and a shorter per-enemy re-hit delay.
                weapon.Angle += weapon.AngularSpeed / player.CooldownMul * dt;
                float hitCooldown = _config.BladeHitCooldown * player.CooldownMul;

                int slot = 0;
                int count = _blades.Count;
                foreach (int b in _blades) {
                    float a = weapon.Angle + slot * (MathF.PI * 2f / count);
                    _bladeData.Get(b).Slot = slot++;
                    ref var p = ref _positions.Get(b);
                    p.X = center.X + MathF.Cos(a) * weapon.OrbitRadius;
                    p.Y = center.Y + MathF.Sin(a) * weapon.OrbitRadius;
                    Cut(world, p.X, p.Y, center, damage, hitCooldown);
                }
            }
        }

        private void Cut(World world, float x, float y, Position center, float damage, float hitCooldown) {
            var g = _grid;
            float r = _config.BladeRadius;
            int cx = g.CellX(x), cy = g.CellY(y);
            int x0 = cx > 0 ? cx - 1 : 0;
            int x1 = cx < SpatialGrid.Dim - 1 ? cx + 1 : SpatialGrid.Dim - 1;
            for (int ny = cy - 1; ny <= cy + 1; ny++) {
                if ((uint)ny >= SpatialGrid.Dim) {
                    continue;
                }
                int j0 = g.CellStart[ny * SpatialGrid.Dim + x0];
                int j1 = g.CellStart[ny * SpatialGrid.Dim + x1 + 1];
                for (int j = j0; j < j1; j++) {
                    float dx = g.X[j] - x;
                    float dy = g.Y[j] - y;
                    float rr = g.R[j] + r;
                    if (dx * dx + dy * dy > rr * rr) {
                        continue;
                    }
                    int target = g.Entity[j];
                    ref var enemy = ref _enemies.Get(target);
                    if (enemy.BladeCooldown > 0f) {
                        continue;
                    }
                    enemy.BladeCooldown = hitCooldown;
                    float ox = g.X[j] - center.X, oy = g.Y[j] - center.Y;
                    float len = MathF.Sqrt(ox * ox + oy * oy) + 1e-4f;
                    world.AddEvent(world.GetEntity(target), new Hit {
                        Damage = damage, KnockX = ox / len * 9f, KnockY = oy / len * 9f
                    });
                }
            }
        }
    }
}
