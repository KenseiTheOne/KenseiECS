using System;

namespace KenseiECS.Demo {
    /// <summary> The level-up offers: names, rolling up to three choices, applying one. </summary>
    public static class Upgrades {
        public const int Damage = 0;
        public const int FireRate = 1;
        public const int Multishot = 2;
        public const int Pierce = 3;
        public const int MoveSpeed = 4;
        public const int Magnet = 5;
        public const int Vitality = 6;
        public const int Blades = 7;
        public const int Nova = 8;
        public const int Recovery = 9;
        public const int Count = 10;

        private static readonly string[] Names = {
            "Might", "Quick Hands", "Multishot", "Piercing", "Swift Boots",
            "Magnet", "Vitality", "Orbiting Blades", "Nova Pulse", "Recovery"
        };

        private static readonly string[] Descriptions = {
            "+25% damage for every weapon",
            "+15% attack speed for every weapon",
            "+1 bolt per volley",
            "Bolts pass through +1 enemy",
            "+10% movement speed",
            "+40% XP pickup radius",
            "+25 max HP and heal 25",
            "Blades orbit you and cut through the horde. Level up: +1 blade, +damage",
            "A shockwave bursts out of you periodically. Level up: bigger, stronger, faster",
            "Heal 50% of max HP"
        };

        // Times each upgrade can be taken. Recovery has no cap: it is the filler offer.
        private static readonly int[] MaxLevel = { 8, 8, 7, 6, 5, 5, 8, 7, 6, 0 };

        /// <summary> Marks an empty offer slot. </summary>
        public const int None = -1;

        public static string Name(int id) => (uint)id < Count ? Names[id] : "";

        public static string Description(int id) => (uint)id < Count ? Descriptions[id] : "";

        /// <summary> Times an upgrade can be taken; 0 for Recovery, which has no cap. </summary>
        public static int MaxLevelOf(int id) => (uint)id < Count ? MaxLevel[id] : 0;

        /// <summary>
        /// Handle the pending level-ups: roll the offers and pause for a choice, or, once
        /// every upgrade is maxed and Recovery is the only offer, apply it right away
        /// without stopping the run. Leaves Status at LevelUp or Playing.
        /// </summary>
        public static void OfferNext(World world, GameConfig config, GameRandom rng, ref GameState state) {
            while (state.PendingLevels > 0) {
                Roll(world, rng, ref state);
                if (state.Choice0 != Recovery) {
                    state.Status = GameStatus.LevelUp;
                    return;
                }
                Apply(world, config, Recovery);
                state.PendingLevels--;
            }
            state.Status = GameStatus.Playing;
        }

        /// <summary>
        /// Up to three distinct upgrades that are not maxed yet; new weapons are weighted up so
        /// they show early. When fewer than three remain, Recovery takes the last slot, so it
        /// showing up means the build is nearly complete. Unused slots are <see cref="None"/>.
        /// </summary>
        private static void Roll(World world, GameRandom rng, ref GameState state) {
            ref var levels = ref world.GetSingleton<UpgradeLevels>();

            Span<float> weights = stackalloc float[Count];
            float total = 0f;
            for (int id = 0; id < Count; id++) {
                float w = 0f;
                if (id != Recovery && levels.Get(id) < MaxLevel[id]) {
                    w = (id == Blades || id == Nova) && levels.Get(id) == 0 ? 3f : 1f;
                }
                weights[id] = w;
                total += w;
            }

            Span<int> picks = stackalloc int[3] { None, None, None };
            int n = 0;
            while (n < 3 && total > 0f) {
                float r = rng.Next() * total;
                int pick = None;
                for (int id = 0; id < Count; id++) {
                    if (weights[id] <= 0f) {
                        continue;
                    }
                    pick = id;
                    r -= weights[id];
                    if (r <= 0f) {
                        break;
                    }
                }
                total -= weights[pick];
                weights[pick] = 0f;
                picks[n++] = pick;
            }
            if (n < 3) {
                picks[n] = Recovery;
            }
            state.Choice0 = picks[0];
            state.Choice1 = picks[1];
            state.Choice2 = picks[2];
        }

        /// <summary>
        /// Apply an upgrade to the player entity. Adding a weapon component is all it takes to start firing it.
        /// Percentage upgrades are additive from the base, as the descriptions read: +15% attack speed
        /// three times is x1.45, not x1.15^3.
        /// </summary>
        public static void Apply(World world, GameConfig config, int id) {
            var playerEntity = world.GetSingletonEntity<Player>();
            ref var levels = ref world.GetSingleton<UpgradeLevels>();
            levels.Increment(id);
            ref var player = ref world.Get<Player>(playerEntity);

            switch (id) {
                case Damage:
                    player.DamageMul = 1f + 0.25f * levels.Get(Damage);
                    break;
                case FireRate:
                    // Attack speed 1 + 0.15n; a cooldown is the inverse of attack speed.
                    player.CooldownMul = 1f / (1f + 0.15f * levels.Get(FireRate));
                    break;
                case Multishot:
                    world.Get<BoltWeapon>(playerEntity).Count++;
                    break;
                case Pierce:
                    world.Get<BoltWeapon>(playerEntity).Pierce++;
                    break;
                case MoveSpeed:
                    player.Speed = config.PlayerSpeed * (1f + 0.10f * levels.Get(MoveSpeed));
                    break;
                case Magnet:
                    player.Magnet = config.PlayerMagnet * (1f + 0.40f * levels.Get(Magnet));
                    break;
                case Vitality:
                    player.MaxHp += 25f;
                    player.Hp = MathF.Min(player.MaxHp, player.Hp + 25f);
                    break;
                case Blades:
                    if (world.Has<BladeWeapon>(playerEntity)) {
                        ref var blades = ref world.Get<BladeWeapon>(playerEntity);
                        blades.Count++;
                        blades.Damage += 3f;
                        blades.OrbitRadius += 0.25f;
                    } else {
                        world.Add(playerEntity, new BladeWeapon {
                            Count = 2, Damage = 9f, OrbitRadius = 3.2f, AngularSpeed = 3.4f
                        });
                    }
                    break;
                case Nova:
                    if (world.Has<NovaWeapon>(playerEntity)) {
                        ref var nova = ref world.Get<NovaWeapon>(playerEntity);
                        nova.Level++;
                        nova.Damage *= 1.3f;
                        nova.MaxRadius += 1.3f;
                        nova.Cooldown *= 0.9f;
                    } else {
                        world.Add(playerEntity, new NovaWeapon {
                            Level = 1, Timer = 0.5f, Cooldown = 3.2f, Damage = 20f, MaxRadius = 7f
                        });
                    }
                    break;
                case Recovery:
                    player.Hp = MathF.Min(player.MaxHp, player.Hp + player.MaxHp * 0.5f);
                    break;
            }
        }
    }
}
