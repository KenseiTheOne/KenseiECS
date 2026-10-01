using System;

namespace KenseiECS.Demo {
    /// <summary>
    /// XP gems: idle gems inside the magnet radius get the Magnetized tag and a
    /// Velocity (which drops them into the Position+Velocity owning group, so
    /// IntegrateSystem moves them); magnetized gems accelerate towards the player
    /// and on pickup become an XpGained event entity. Idle gems left far behind
    /// are moved ahead of the player (like enemies), so with the gem cap reached
    /// the XP folded into existing gems stays within reach.
    /// Shows: [Exc] filter (idle = XpGem without Magnetized), structural changes
    /// on the current entity during foreach, and event entities via CommandBuffer.
    /// </summary>
    public partial class MagnetSystem : IRunSystem {
        [Inc(typeof(XpGem), typeof(Position))] [Exc(typeof(Magnetized))] private Filter _idle;
        [Inc(typeof(XpGem), typeof(Magnetized), typeof(Position), typeof(Velocity))] private Filter _flying;
        [Pool] private ComponentPool<XpGem> _gems;
        [Pool] private ComponentPool<Magnetized> _magnetized;
        [Pool] private ComponentPool<Position> _positions;
        [Pool] private ComponentPool<Velocity> _velocities;
        [Shared] private GameConfig _config;

        private readonly CommandBuffer _buffer = new();

        public void Run(World world) {
            float dt = world.GetSingleton<GameState>().Dt;
            var playerEntity = world.GetSingletonEntity<Player>();
            var player = world.Get<Player>(playerEntity);
            var pp = world.Get<Position>(playerEntity);
            float magnet2 = player.Magnet * player.Magnet;
            float recycle2 = _config.RecycleDistance * _config.RecycleDistance;

            foreach (int e in _idle) {
                ref var p = ref _positions.Get(e);
                float dx = pp.X - p.X, dy = pp.Y - p.Y;
                float d2 = dx * dx + dy * dy;
                if (d2 > recycle2) {
                    // Left far behind: reappear just off-screen on the opposite side.
                    float inv = _config.SpawnDistance / MathF.Sqrt(d2);
                    p.X = pp.X + dx * inv;
                    p.Y = pp.Y + dy * inv;
                } else if (d2 < magnet2) {
                    _magnetized.Add(e, new Magnetized { Speed = 4f });   // leaves _idle, joins _flying
                    _velocities.Add(e, new Velocity());
                }
            }

            foreach (int e in _flying) {
                var p = _positions.Get(e);
                ref var m = ref _magnetized.Get(e);
                float dx = pp.X - p.X, dy = pp.Y - p.Y;
                float d = MathF.Sqrt(dx * dx + dy * dy) + 1e-4f;
                m.Speed += 45f * dt;
                // Collect when this frame's step would reach the player: at a low
                // frame rate a fast gem would otherwise overshoot and oscillate.
                if (d < 0.9f || m.Speed * dt >= d) {
                    _buffer.CreateEntity(new XpGained { Amount = _gems.Get(e).Value });
                    _buffer.DestroyEntity(world.GetEntity(e));
                    continue;
                }
                ref var v = ref _velocities.Get(e);
                v.X = dx / d * m.Speed;
                v.Y = dy / d * m.Speed;
            }
            _buffer.Playback(world);
        }
    }
}
