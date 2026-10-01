using System;

namespace KenseiECS.Demo {
    /// <summary>
    /// Wave director: a ramping trickle of enemies plus periodic swarms and elites,
    /// all spawned just off-screen. Rate and cap scale with HordeGame.StressMultiplier.
    /// Shows: creating entities with World.CreateEntity + Add outside any iteration,
    /// [Shared] config injected from SharedData, a tag component (Heavy).
    /// </summary>
    public partial class WaveSpawnSystem : IRunSystem {
        [Pool] private ComponentPool<Enemy> _enemies;
        [Shared] private GameConfig _config;
        [Shared] private GameRandom _rng;

        public void Run(World world) {
            ref var state = ref world.GetSingleton<GameState>();
            var playerPos = world.Get<Position>(world.GetSingletonEntity<Player>());
            float t = state.Time;

            int cap = _config.EnemyCap(t);
            int budget = _config.MaxSpawnsPerFrame;

            // Steady trickle.
            state.SpawnAccumulator += _config.SpawnRate(t) * state.Dt;
            while (state.SpawnAccumulator >= 1f) {
                state.SpawnAccumulator -= 1f;
                if (_enemies.Count >= cap || budget <= 0) {
                    state.SpawnAccumulator = MathF.Min(state.SpawnAccumulator, 4f);
                    break;
                }
                float angle = _rng.Range(0f, MathF.PI * 2f);
                float dist = _config.SpawnDistance + _rng.Range(0f, 6f);
                Spawn(world, PickArchetype(t), playerPos.X + MathF.Cos(angle) * dist, playerPos.Y + MathF.Sin(angle) * dist, t);
                budget--;
            }

            // Swarm: a wall of fast enemies sweeping in from one side; the arc
            // widens over the run until, late on, it is a closing ring.
            // A big swarm is laid down over several frames, MaxSpawnsPerFrame at a time.
            if (t >= state.NextSwarm) {
                state.NextSwarm = t + 40f;
                int count = (int)MathF.Min((24f + t * 0.3f) * MathF.Max(1f, _config.StressMultiplier), 3000f);
                state.SwarmTotal = count;
                state.SwarmLeft = count;
                state.SwarmArc = MathF.Min(MathF.PI * 2f, MathF.PI * (0.5f + t / 120f));
                state.SwarmCenter = _rng.Range(0f, MathF.PI * 2f);
            }
            if (state.SwarmLeft > 0) {
                float r = _config.SpawnDistance - 4f;
                int swarmBudget = _config.MaxSpawnsPerFrame;
                while (state.SwarmLeft > 0 && swarmBudget > 0 && _enemies.Count < _config.HardEnemyCap) {
                    int i = state.SwarmTotal - state.SwarmLeft;
                    float angle = state.SwarmCenter + (i / (float)state.SwarmTotal - 0.5f) * state.SwarmArc;
                    float jitter = _rng.Range(-1.5f, 1.5f);
                    Spawn(world, _config.Fast, playerPos.X + MathF.Cos(angle) * (r + jitter), playerPos.Y + MathF.Sin(angle) * (r + jitter), t);
                    state.SwarmLeft--;
                    swarmBudget--;
                }
                if (_enemies.Count >= _config.HardEnemyCap) {
                    state.SwarmLeft = 0;   // no room: drop the rest of the wave
                }
            }

            // Elites.
            if (t >= state.NextElite) {
                state.NextElite = t + 45f;
                int count = 1 + (int)(t / 120f);
                for (int i = 0; i < count; i++) {
                    float angle = _rng.Range(0f, MathF.PI * 2f);
                    Spawn(world, _config.Elite, playerPos.X + MathF.Cos(angle) * _config.SpawnDistance, playerPos.Y + MathF.Sin(angle) * _config.SpawnDistance, t);
                }
            }

            if (_enemies.Count > state.PeakEnemies) {
                state.PeakEnemies = _enemies.Count;
            }
        }

        private EnemyArchetype PickArchetype(float t) {
            float fast = t < 20f ? 0f : MathF.Min(0.35f, (t - 20f) / 150f);
            float tank = t < 60f ? 0f : MathF.Min(0.22f, (t - 60f) / 350f);
            float r = _rng.Next();
            if (r < tank) {
                return _config.Tank;
            }
            if (r < tank + fast) {
                return _config.Fast;
            }
            return _config.Basic;
        }

        private void Spawn(World world, EnemyArchetype a, float x, float y, float t) {
            float hp = a.Hp * _config.HpScale(t);
            var e = world.CreateEntity(new Enemy {
                Kind = a.Kind,
                Speed = a.Speed * _rng.Range(0.9f, 1.1f),
                ContactDamage = a.ContactDamage,
                Xp = a.Xp
            });
            world.Add(e, new Position { X = x, Y = y });
            world.Add(e, new Velocity());
            world.Add(e, new Radius { Value = a.Radius });
            world.Add(e, new Health { Value = hp, Max = hp });
            if (a.Heavy) {
                world.Add(e, new Heavy());
            }
        }
    }
}
