using System;

namespace KenseiECS.Demo {
    /// <summary>
    /// Fires a volley of bolts at the nearest enemy whenever the cooldown is up.
    /// Shows: CommandBuffer — bolts are recorded while iterating the shooter
    /// filter and created on Playback, so the iteration never sees its own spawns.
    /// PendingEntity lets several components be added to a not-yet-created entity.
    /// </summary>
    public partial class BoltWeaponSystem : IRunSystem {
        [Inc(typeof(Player), typeof(BoltWeapon), typeof(Position))] private Filter _shooters;
        [Pool] private ComponentPool<Player> _players;
        [Pool] private ComponentPool<BoltWeapon> _weapons;
        [Pool] private ComponentPool<Position> _positions;
        [Shared] private SpatialGrid _grid;
        [Shared] private GameConfig _config;

        private readonly CommandBuffer _buffer = new();

        public void Run(World world) {
            float dt = world.GetSingleton<GameState>().Dt;
            foreach (int e in _shooters) {
                ref var weapon = ref _weapons.Get(e);
                ref var player = ref _players.Get(e);
                weapon.Timer -= dt;
                if (weapon.Timer > 0f) {
                    continue;
                }

                var p = _positions.Get(e);
                int target = _grid.Nearest(p.X, p.Y, _config.BoltRange);
                if (target < 0) {
                    weapon.Timer = 0f;   // stay ready until something is in range
                    continue;
                }
                weapon.Timer = weapon.Cooldown * player.CooldownMul;

                float aim = MathF.Atan2(_grid.Y[target] - p.Y, _grid.X[target] - p.X);
                const float spread = 0.13f;
                for (int k = 0; k < weapon.Count; k++) {
                    float a = aim + (k - (weapon.Count - 1) * 0.5f) * spread;
                    var bolt = _buffer.CreateEntity(new Bolt {
                        Damage = weapon.Damage * player.DamageMul,
                        PierceLeft = weapon.Pierce   // LastHit = default: Entity.Null
                    });
                    _buffer.Add(bolt, new Position { X = p.X, Y = p.Y });
                    _buffer.Add(bolt, new Velocity { X = MathF.Cos(a) * _config.BoltSpeed, Y = MathF.Sin(a) * _config.BoltSpeed });
                    _buffer.Add(bolt, new Radius { Value = _config.BoltRadius });
                    _buffer.Add(bolt, new Lifetime { Left = _config.BoltLife, Total = _config.BoltLife });
                }
            }
            _buffer.Playback(world);
        }
    }
}
