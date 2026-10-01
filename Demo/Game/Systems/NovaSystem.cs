using System;

namespace KenseiECS.Demo {
    /// <summary>
    /// Nova pulse: periodically emits an expanding ring that damages every enemy
    /// the wavefront passes over (an annulus query on the grid).
    /// Shows: two filters in one system (weapon owners and live rings), an entity
    /// created with CommandBuffer and destroyed in place when the ring fades.
    /// </summary>
    public partial class NovaSystem : IRunSystem {
        [Inc(typeof(Player), typeof(NovaWeapon), typeof(Position))] private Filter _owners;
        [Inc(typeof(NovaRing), typeof(Position))] private Filter _rings;
        [Pool] private ComponentPool<Player> _players;
        [Pool] private ComponentPool<NovaWeapon> _weapons;
        [Pool] private ComponentPool<NovaRing> _ringData;
        [Pool] private ComponentPool<Position> _positions;
        [Shared] private SpatialGrid _grid;
        [Shared] private GameConfig _config;

        private readonly CommandBuffer _buffer = new();

        public void Run(World world) {
            float dt = world.GetSingleton<GameState>().Dt;
            float px = 0f, py = 0f;

            foreach (int e in _owners) {
                ref var weapon = ref _weapons.Get(e);
                ref var player = ref _players.Get(e);
                var p = _positions.Get(e);
                px = p.X;
                py = p.Y;
                weapon.Timer -= dt;
                if (weapon.Timer > 0f) {
                    continue;
                }
                weapon.Timer = weapon.Cooldown * player.CooldownMul;
                var ring = _buffer.CreateEntity(new NovaRing {
                    Radius = 0.5f, MaxRadius = weapon.MaxRadius, Speed = _config.NovaSpeed, Damage = weapon.Damage * player.DamageMul
                });
                _buffer.Add(ring, new Position { X = p.X, Y = p.Y });
            }
            _buffer.Playback(world);

            if (_rings.IsEmpty) {
                return;
            }
            if (_owners.IsEmpty) {
                var p = world.Get<Position>(world.GetSingletonEntity<Player>());
                px = p.X;
                py = p.Y;
            }

            foreach (int e in _rings) {
                ref var ring = ref _ringData.Get(e);
                ref var p = ref _positions.Get(e);
                p.X = px;   // the ring travels with the player
                p.Y = py;
                float inner = ring.Radius;
                ring.Radius = MathF.Min(ring.MaxRadius, ring.Radius + ring.Speed * dt);
                Sweep(world, px, py, inner, ring.Radius, ring.Damage);
                if (ring.Radius >= ring.MaxRadius) {
                    world.DestroyEntity(world.GetEntity(e));
                }
            }
        }

        private void Sweep(World world, float x, float y, float inner, float outer, float damage) {
            var g = _grid;
            int x0 = g.CellX(x - outer - 2f), x1 = g.CellX(x + outer + 2f);
            int y0 = g.CellY(y - outer - 2f), y1 = g.CellY(y + outer + 2f);
            for (int cy = y0; cy <= y1; cy++) {
                int j0 = g.CellStart[cy * SpatialGrid.Dim + x0];
                int j1 = g.CellStart[cy * SpatialGrid.Dim + x1 + 1];
                for (int j = j0; j < j1; j++) {
                    float dx = g.X[j] - x;
                    float dy = g.Y[j] - y;
                    float d = MathF.Sqrt(dx * dx + dy * dy);
                    float edge = d - g.R[j];   // nearest point of the enemy
                    if (edge >= outer || edge < inner - 0.001f && inner > 0.5f) {
                        continue;
                    }
                    float len = d + 1e-4f;
                    world.AddEvent(world.GetEntity(g.Entity[j]), new Hit {
                        Damage = damage, KnockX = dx / len * 14f, KnockY = dy / len * 14f
                    });
                }
            }
        }
    }
}
