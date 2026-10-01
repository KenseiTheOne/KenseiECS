using System;

namespace KenseiECS.Demo {
    /// <summary>
    /// Turns every Died enemy into an XP gem and a burst of sparks, counts the
    /// kill and destroys the enemy.
    /// Shows: CommandBuffer — gems, sparks and the destroy are recorded during
    /// the Died iteration and applied in one Playback after it. When the gem cap
    /// is reached the XP is folded into an existing gem through RawData instead.
    /// </summary>
    public partial class LootSystem : IRunSystem {
        [Inc(typeof(Died), typeof(Enemy), typeof(Position))] private Filter _dead;
        [Inc(typeof(Particle))] private Filter _particles;
        [Pool] private ComponentPool<Enemy> _enemies;
        [Pool] private ComponentPool<Position> _positions;
        [Pool] private ComponentPool<XpGem> _gems;
        [Shared] private GameConfig _config;
        [Shared] private GameRandom _rng;

        private readonly CommandBuffer _buffer = new();

        public void Run(World world) {
            ref var state = ref world.GetSingleton<GameState>();
            int gems = _gems.Count;
            int particles = _particles.Count;

            foreach (int e in _dead) {
                ref var enemy = ref _enemies.Get(e);
                var p = _positions.Get(e);
                state.Kills++;

                if (gems < _config.MaxGems || enemy.Kind == EnemyKind.Elite) {
                    var gem = _buffer.CreateEntity(new XpGem { Value = enemy.Xp });
                    _buffer.Add(gem, new Position { X = p.X, Y = p.Y });
                    gems++;
                } else if (_gems.Count > 0) {
                    _gems.RawData[_rng.Int(_gems.Count)].Value += enemy.Xp;
                }

                int sparks = enemy.Kind == EnemyKind.Elite ? 14 : enemy.Kind == EnemyKind.Tank ? 5 : 3;
                for (int k = 0; k < sparks && particles < _config.MaxParticles; k++, particles++) {
                    float a = _rng.Range(0f, MathF.PI * 2f);
                    float s = _rng.Range(4f, 11f);
                    var spark = _buffer.CreateEntity(new Particle());
                    _buffer.Add(spark, new Position { X = p.X, Y = p.Y });
                    _buffer.Add(spark, new Velocity { X = MathF.Cos(a) * s, Y = MathF.Sin(a) * s });
                    _buffer.Add(spark, new Radius { Value = 0.18f });
                    _buffer.Add(spark, new Lifetime { Left = _config.ParticleLife, Total = _config.ParticleLife });
                }

                _buffer.DestroyEntity(world.GetEntity(e));
            }
            _buffer.Playback(world);
        }
    }
}
