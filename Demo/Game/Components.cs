namespace KenseiECS.Demo {
    // =====================================================================
    // Spatial data. Position + Velocity form the owning group iterated by
    // IntegrateSystem: anything that has both moves, nothing else to opt in.
    // =====================================================================

    public struct Position : IComponent {
        public float X, Y;
    }

    public struct Velocity : IComponent {
        public float X, Y;
    }

    /// <summary> Collision and render radius in world units. </summary>
    public struct Radius : IComponent {
        public float Value;
    }

    /// <summary> Seconds left before the entity is destroyed (bolts, sparks). </summary>
    public struct Lifetime : IComponent {
        public float Left;
        public float Total;
    }

    // =====================================================================
    // Singletons — exactly one entity holds each of these.
    // =====================================================================

    public enum GameStatus {
        Playing,
        LevelUp,
        GameOver
    }

    /// <summary> Run state: clock, score, XP, level-up offer and wave director timers. </summary>
    public struct GameState : IComponent {
        public float Dt;
        public float Time;
        public int Kills;
        public int Level;
        public float Xp;
        public float XpToNext;
        public GameStatus Status;
        public int PendingLevels;
        public int Choice0, Choice1, Choice2;
        public float SpawnAccumulator;
        public float NextSwarm;
        public int SwarmLeft, SwarmTotal;
        public float SwarmCenter, SwarmArc;
        public float NextElite;
        public int PeakEnemies;
    }

    /// <summary> Movement input for this frame, written by the host before the sim runs. </summary>
    public struct PlayerInput : IComponent {
        public float X, Y;
    }

    /// <summary> The player: survival stats and global weapon multipliers. </summary>
    public struct Player : IComponent {
        public float Hp;
        public float MaxHp;
        public float Speed;
        public float Magnet;
        public float Invulnerable;
        public float Flash;
        public float DamageMul;
        public float CooldownMul;
    }

    /// <summary> How many times each upgrade was taken (indexed by upgrade id). </summary>
    public struct UpgradeLevels : IComponent {
        public int U0, U1, U2, U3, U4, U5, U6, U7, U8, U9;

        public int Get(int id) {
            switch (id) {
                case 0: return U0;
                case 1: return U1;
                case 2: return U2;
                case 3: return U3;
                case 4: return U4;
                case 5: return U5;
                case 6: return U6;
                case 7: return U7;
                case 8: return U8;
                default: return U9;
            }
        }

        public void Increment(int id) {
            switch (id) {
                case 0: U0++; break;
                case 1: U1++; break;
                case 2: U2++; break;
                case 3: U3++; break;
                case 4: U4++; break;
                case 5: U5++; break;
                case 6: U6++; break;
                case 7: U7++; break;
                case 8: U8++; break;
                default: U9++; break;
            }
        }
    }

    // =====================================================================
    // Weapons live on the player entity. A weapon starts firing the frame its
    // component is added: the weapon system's filter picks the player up.
    // =====================================================================

    public struct BoltWeapon : IComponent {
        public float Timer;
        public float Cooldown;
        public float Damage;
        public int Count;
        public int Pierce;
    }

    public struct BladeWeapon : IComponent {
        public int Count;
        public float Angle;
        public float Damage;
        public float OrbitRadius;
        public float AngularSpeed;
    }

    public struct NovaWeapon : IComponent {
        public int Level;
        public float Timer;
        public float Cooldown;
        public float Damage;
        public float MaxRadius;
    }

    // =====================================================================
    // Enemies
    // =====================================================================

    public static class EnemyKind {
        public const int Basic = 1;
        public const int Fast = 2;
        public const int Tank = 3;
        public const int Elite = 4;
    }

    public struct Enemy : IComponent {
        public int Kind;
        public float Speed;
        public float ContactDamage;
        public float Xp;
        public float KnockX, KnockY;
        public float SepX, SepY;
        public float BladeCooldown;
        public float Flash;
    }

    public struct Health : IComponent {
        public float Value;
        public float Max;
    }

    /// <summary> Tag: tanks and elites shrug off knockback (KnockbackSystem excludes it). </summary>
    public struct Heavy : IComponent { }

    // =====================================================================
    // Projectiles and pickups
    // =====================================================================

    public struct Bolt : IComponent {
        public float Damage;
        public int PierceLeft;
        /// <summary>
        /// The enemy hit last, so a piercing bolt does not hit it again next frame.
        /// A reference to another entity kept across frames is always an Entity
        /// handle: an int index could name a newer enemy once the slot is reused.
        /// </summary>
        public Entity LastHit;
    }

    public struct Blade : IComponent {
        public int Slot;
    }

    public struct NovaRing : IComponent {
        public float Radius;
        public float MaxRadius;
        public float Speed;
        public float Damage;
    }

    public struct XpGem : IComponent {
        public float Value;
    }

    /// <summary> Tag: the gem is flying to the player. Added once, by MagnetSystem. </summary>
    public struct Magnetized : IComponent {
        public float Speed;
    }

    public struct Particle : IComponent { }

    // =====================================================================
    // Events. All three are OneFrame: the sim runner removes them after its
    // last system, so each lives exactly one frame.
    // =====================================================================

    /// <summary> One hit on an enemy. Several per frame are collected in EventBuffer&lt;Hit&gt;. </summary>
    public struct Hit {
        public float Damage;
        public float KnockX, KnockY;
    }

    /// <summary> Event component: the enemy's health reached zero this frame. </summary>
    public struct Died : IComponent { }

    /// <summary> Event entity: XP collected this frame. The entity dies with the event. </summary>
    public struct XpGained : IComponent {
        public float Amount;
    }
}
