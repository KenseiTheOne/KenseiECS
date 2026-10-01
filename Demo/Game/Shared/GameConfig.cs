using System;

namespace KenseiECS.Demo {
    /// <summary> Sprite kinds written to the render buffer (see HordeGame.RenderBuffer). </summary>
    public static class SpriteKind {
        public const float Player = 0;
        public const float EnemyBasic = 1;
        public const float EnemyFast = 2;
        public const float EnemyTank = 3;
        public const float Elite = 4;
        public const float Bolt = 5;
        public const float Blade = 6;
        public const float Nova = 7;
        public const float GemSmall = 8;
        public const float GemBig = 9;
        public const float Spark = 10;
    }

    /// <summary> Stats of one enemy archetype. </summary>
    public sealed class EnemyArchetype {
        public int Kind;
        public float Radius;
        public float Speed;
        public float Hp;
        public float ContactDamage;
        public float Xp;
        public bool Heavy;
    }

    /// <summary>
    /// Tuning knobs, shared with every system through SharedData. One instance
    /// per game; StressMultiplier is the only value changed at runtime.
    /// </summary>
    public sealed class GameConfig {
        // --- Player ---
        public float PlayerRadius = 0.6f;
        public float PlayerSpeed = 7.5f;
        public float PlayerMaxHp = 100f;
        public float PlayerMagnet = 4f;
        public float InvulnerableTime = 0.8f;

        // --- Arena ---
        /// <summary> Enemies are spawned this far from the player (just off-screen). </summary>
        public float SpawnDistance = 42f;
        /// <summary> Enemies farther than this are recycled to the opposite side. </summary>
        public float RecycleDistance = 70f;
        /// <summary> Strength of the soft push that keeps the horde from collapsing. </summary>
        public float Separation = 7f;

        // --- Director ---
        public float StressMultiplier = 1f;
        public int HardEnemyCap = 30000;   // keeps "Max out" smooth in the browser interpreter
        public int MaxSpawnsPerFrame = 600;
        public int MaxGems = 1500;
        public int MaxParticles = 1500;
        public float ParticleLife = 0.35f;

        // --- Weapons ---
        public float BoltSpeed = 24f;
        public float BoltLife = 1.1f;
        public float BoltRadius = 0.35f;
        public float BoltRange = 24f;
        public float BladeRadius = 0.6f;
        public float BladeHitCooldown = 0.4f;
        public float NovaSpeed = 18f;

        public readonly EnemyArchetype Basic = new() {
            Kind = EnemyKind.Basic, Radius = 0.55f, Speed = 3.3f, Hp = 9f, ContactDamage = 5f, Xp = 1f
        };

        public readonly EnemyArchetype Fast = new() {
            Kind = EnemyKind.Fast, Radius = 0.42f, Speed = 5.6f, Hp = 6f, ContactDamage = 3f, Xp = 1f
        };

        public readonly EnemyArchetype Tank = new() {
            Kind = EnemyKind.Tank, Radius = 1.0f, Speed = 2.3f, Hp = 45f, ContactDamage = 10f, Xp = 4f, Heavy = true
        };

        public readonly EnemyArchetype Elite = new() {
            Kind = EnemyKind.Elite, Radius = 1.7f, Speed = 2.8f, Hp = 450f, ContactDamage = 20f, Xp = 25f, Heavy = true
        };

        /// <summary> Enemy health multiplier at a given run time. </summary>
        public float HpScale(float time) => 1f + time / 115f + (time / 200f) * (time / 200f);

        /// <summary> Enemies per second at a given run time. </summary>
        public float SpawnRate(float time) {
            float stress = StressMultiplier;
            float baseRate = 2f + 0.03f * MathF.Pow(time, 1.55f);
            // Above 1x, stress adds a flat surge so viewers reach 20k+ in seconds.
            float surge = stress > 1f ? (stress - 1f) * 80f : 0f;
            return baseRate * stress + surge;
        }

        /// <summary> Maximum simultaneous enemies at a given run time. </summary>
        public int EnemyCap(float time) {
            float stress = StressMultiplier;
            float cap = (60f + 14f * time) * stress + (stress > 1f ? (stress - 1f) * 800f : 0f);
            return (int)MathF.Min(cap, HardEnemyCap);
        }

        /// <summary> XP needed to go from this level to the next. </summary>
        public float XpForLevel(int level) => 3f + 2.5f * level + 0.35f * level * level;
    }

    /// <summary> Seeded random source shared by all systems. </summary>
    public sealed class GameRandom {
        private Random _random;

        public GameRandom(int seed) {
            _random = new Random(seed);
        }

        public void Reset(int seed) {
            _random = new Random(seed);
        }

        public float Next() => (float)_random.NextDouble();

        public float Range(float min, float max) => min + (max - min) * (float)_random.NextDouble();

        public int Int(int maxExclusive) => _random.Next(maxExclusive);
    }
}
