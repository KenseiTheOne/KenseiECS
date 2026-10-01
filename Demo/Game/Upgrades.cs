using System;

namespace KenseiECS.Demo {
    /// <summary> The level-up offers: names, rolling three choices, applying one. </summary>
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

        // Times each upgrade can be taken.
        private static readonly int[] MaxLevel = { 8, 8, 7, 6, 5, 5, 8, 7, 6, 1000 };

        public static string Name(int id) => (uint)id < Count ? Names[id] : "";

        public static string Description(int id) => (uint)id < Count ? Descriptions[id] : "";

        /// <summary> Pick three distinct offers. New weapons are weighted up so they show early. </summary>
        public static void Roll(World world, GameRandom rng, ref GameState state) {
            ref var levels = ref world.GetSingleton<UpgradeLevels>();
            ref var player = ref world.GetSingleton<Player>();

            Span<float> weights = stackalloc float[Count];
            float total = 0f;
            for (int id = 0; id < Count; id++) {
                float w = levels.Get(id) < MaxLevel[id] ? 1f : 0f;
                if (id == Recovery) {
                    w = player.Hp < player.MaxHp * 0.7f ? 0.8f : 0f;
                } else if ((id == Blades || id == Nova) && levels.Get(id) == 0) {
                    w = 3f;
                }
                weights[id] = w;
                total += w;
            }

            Span<int> picks = stackalloc int[3];
            for (int k = 0; k < 3; k++) {
                int pick = Recovery;   // fallback when everything is maxed
                if (total > 0f) {
                    float r = rng.Next() * total;
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
                }
                picks[k] = pick;
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
