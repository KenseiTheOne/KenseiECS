using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace KenseiECS.Demo.Headless {
    /// <summary>
    /// Plays Horde without a screen: circle-strafing input, always takes the
    /// first upgrade offer. Usage:
    ///   KenseiECS.Demo.Headless [--frames 5000] [--stress 1] [--god] [--seed 1] [--toggle] [--json]
    /// --toggle switches every system off for 100 frames in turn (overlay robustness).
    /// Exits 0 on success, 1 on any exception.
    /// </summary>
    public static class Program {
        public static int Main(string[] args) {
            try {
                return Run(args);
            } catch (Exception e) {
                Console.Error.WriteLine("FAILED: " + e);
                return 1;
            }
        }

        private static int Run(string[] args) {
            int frames = 5000;
            float stress = 1f;
            bool god = false;
            bool json = false;
            bool toggle = false;
            int seed = 1;
            for (int i = 0; i < args.Length; i++) {
                switch (args[i]) {
                    case "--frames": frames = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--stress": stress = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--seed": seed = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--god": god = true; break;
                    case "--json": json = true; break;
                    case "--toggle": toggle = true; break;
                    default: throw new ArgumentException("Unknown argument " + args[i]);
                }
            }

            const float dt = 1f / 60f;
            const int warmFrames = 120;
            var game = new HordeGame(seed) { StressMultiplier = stress, GodMode = god };

#if KENSEI_DEBUG
            string mode = "KENSEI_DEBUG";
#else
            string mode = "release";
#endif
            Console.WriteLine($"Horde headless: frames={frames} stress={stress} god={god} seed={seed} lib={mode}");

            int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
            long allocated = 0;
            int allocFrames = 0;
            double totalMs = 0, peakMs = 0;
            int measured = 0, levelUps = 0, played = 0;
            var buckets = new SortedDictionary<int, (double ms, int n)>();
            var sw = new Stopwatch();
            float nextReport = 30f;
            double windowMs = 0;
            int windowN = 0;
            var choices = new int[HordeGameUpgradeCount];
            var systemNames = new List<string>();
            using (var doc = JsonDocument.Parse(game.StatsJson())) {
                foreach (var sys in doc.RootElement.GetProperty("systems").EnumerateArray()) {
                    systemNames.Add(sys.GetProperty("name").GetString());
                }
            }
            systemNames.Add("sim");
            systemNames.Add("render");
            string disabled = null;

            for (int f = 0; f < frames; f++) {
                float t = f * dt;
                // Circle-strafe with a slow wobble so the path is not a perfect loop.
                float angle = t * 0.55f + 0.6f * MathF.Sin(t * 0.13f);
                float ix = MathF.Cos(angle), iy = MathF.Sin(angle);
                Steer(game, ref ix, ref iy);

                if (toggle && f % 200 == 0) {
                    int k = f / 200;
                    if (k < systemNames.Count) {
                        disabled = systemNames[k];
                        if (!game.SetSystemActive(disabled, false)) {
                            throw new InvalidOperationException("SetSystemActive did not find " + disabled);
                        }
                    }
                } else if (toggle && f % 200 == 100 && disabled != null) {
                    game.SetSystemActive(disabled, true);
                    disabled = null;
                }

                long before = GC.GetAllocatedBytesForCurrentThread();
                sw.Restart();
                game.Tick(dt, ix, iy);
                sw.Stop();
                long after = GC.GetAllocatedBytesForCurrentThread();
                played++;

                double ms = sw.Elapsed.TotalMilliseconds;
                if (f >= warmFrames) {
                    allocated += after - before;
                    if (after != before) {
                        allocFrames++;
                    }
                    totalMs += ms;
                    measured++;
                    if (ms > peakMs) {
                        peakMs = ms;
                    }
                    int bucket = game.EnemyCount / 2500 * 2500;
                    buckets.TryGetValue(bucket, out var b);
                    buckets[bucket] = (b.ms + ms, b.n + 1);
                }
                windowMs += ms;
                windowN++;

                var hud = game.Hud;
                if (hud.TimeSec >= nextReport) {
                    nextReport += 30f;
                    Console.WriteLine($"  t={hud.TimeSec,6:0.0}s  enemies={game.EnemyCount,6}  sprites={game.SpriteCount,6}  lvl={hud.Level,3}  kills={hud.Kills,7}  hp={hud.Hp,5:0}/{hud.MaxHp:0}  tick avg={windowMs / windowN:0.000} ms");
                    windowMs = 0;
                    windowN = 0;
                }

                if (hud.Status == GameStatus.LevelUp) {
                    CheckOffers(game, hud);
                    choices[hud.Choice0]++;
                    game.ChooseUpgrade(0);
                    levelUps++;
                } else if (hud.Status == GameStatus.GameOver) {
                    break;
                }

                if (f == frames / 2) {
                    JsonDocument.Parse(game.StatsJson()).Dispose();   // must stay valid JSON mid-run
                }
            }

            var final = game.Hud;
            Console.WriteLine();
            Console.WriteLine($"Result:        {(final.Status == GameStatus.GameOver ? "died" : "alive")} after {final.TimeSec:0.0}s ({played} frames)");
            Console.WriteLine($"Kills:         {final.Kills}");
            Console.WriteLine($"Level:         {final.Level} ({levelUps} upgrades taken)");
            Console.WriteLine($"Peak enemies:  {game.PeakEnemies}  (alive at end: {game.EnemyCount})");
            Console.WriteLine($"Tick:          avg {(measured > 0 ? totalMs / measured : 0):0.000} ms, peak {peakMs:0.000} ms (after {warmFrames} warm-up frames)");
            Console.Write("Tick by enemy count:");
            foreach (var kv in buckets) {
                Console.Write($"  {kv.Key / 1000.0:0.#}k+: {kv.Value.ms / kv.Value.n:0.00}ms");
            }
            Console.WriteLine();
            Console.WriteLine($"GC:            gen0={GC.CollectionCount(0) - gen0} gen1={GC.CollectionCount(1) - gen1} gen2={GC.CollectionCount(2) - gen2}; allocated in Tick after warm-up: {allocated} bytes in {allocFrames} of {measured} frames (pool/filter/buffer growth; 0 B once capacities settle)");
            Console.Write("Upgrades taken:");
            for (int i = 0; i < choices.Length; i++) {
                if (choices[i] > 0) {
                    Console.Write($"  {HordeGame.UpgradeName(i)} x{choices[i]}");
                }
            }
            Console.WriteLine();

            string stats = game.StatsJson();

            // Restart must bring back a fresh run on the same World.
            game.Restart();
            for (int f = 0; f < 120; f++) {
                game.Tick(dt, 1f, 0f);
            }
            var fresh = game.Hud;
            if (fresh.Kills > 50 || fresh.Level > 3 || fresh.TimeSec > 2.1f || fresh.Status == GameStatus.GameOver) {
                throw new InvalidOperationException($"Restart did not reset the run: t={fresh.TimeSec} kills={fresh.Kills} level={fresh.Level}");
            }
            Console.WriteLine($"Restart:       ok (t={fresh.TimeSec:0.0}s, enemies={game.EnemyCount}, sprites={game.SpriteCount})");
            using (var doc = JsonDocument.Parse(stats)) {
                var root = doc.RootElement;
                Console.WriteLine($"Stats:         entities={root.GetProperty("entities").GetInt32()} filters={root.GetProperty("filters").GetArrayLength()} pools={root.GetProperty("pools").GetArrayLength()} systems={root.GetProperty("systems").GetArrayLength()} timings={root.GetProperty("timings").GetBoolean()}");
                if (root.GetProperty("timings").GetBoolean()) {
                    Console.WriteLine("Systems (last / peak ms):");
                    foreach (var s in root.GetProperty("systems").EnumerateArray()) {
                        Console.WriteLine($"  {s.GetProperty("phase").GetString(),-6} {s.GetProperty("name").GetString(),-20} {s.GetProperty("lastMs").GetDouble(),8:0.000} {s.GetProperty("peakMs").GetDouble(),8:0.000}");
                    }
                }
            }
            if (json) {
                Console.WriteLine(stats);
            }
            return 0;
        }

        private const int HordeGameUpgradeCount = 10;

        // Scripted pilot: circle-strafe, drift towards the nearest XP gem on
        // screen and push away from nearby enemies — a rough stand-in for a
        // player, so runs last long enough to exercise level-ups and upgrades.
        /// <summary>
        /// The level-up contract: distinct offers, none of them maxed, Recovery only in the
        /// last filled slot and only when fewer than three upgrades remain, and never alone
        /// (a Recovery-only level-up is applied without stopping the run).
        /// </summary>
        private static void CheckOffers(HordeGame game, in HudState hud) {
            Span<int> ids = stackalloc int[] { hud.Choice0, hud.Choice1, hud.Choice2 };
            int filled = 0;
            while (filled < 3 && ids[filled] != Upgrades.None) {
                filled++;
            }
            for (int i = filled; i < 3; i++) {
                if (ids[i] != Upgrades.None) {
                    throw new InvalidOperationException($"Offer gap: {hud.Choice0}, {hud.Choice1}, {hud.Choice2}");
                }
            }
            int available = 0;
            for (int id = 0; id < HordeGame.UpgradeCount; id++) {
                if (id != Upgrades.Recovery && game.UpgradeLevel(id) < HordeGame.UpgradeMaxLevel(id)) {
                    available++;
                }
            }
            int expectedUpgrades = Math.Min(3, available);
            int expected = expectedUpgrades < 3 ? expectedUpgrades + 1 : 3;
            if (filled != expected || filled == 1 && ids[0] == Upgrades.Recovery && available == 0) {
                throw new InvalidOperationException($"Expected {expected} offers with {available} upgrades left, got {filled}");
            }
            for (int i = 0; i < filled; i++) {
                int id = ids[i];
                bool last = i == filled - 1;
                if (id == Upgrades.Recovery ? !(last && available < 3) : game.UpgradeLevel(id) >= HordeGame.UpgradeMaxLevel(id)) {
                    throw new InvalidOperationException($"Bad offer {Upgrades.Name(id)} in slot {i} of {filled}");
                }
                for (int j = 0; j < i; j++) {
                    if (ids[j] == id) {
                        throw new InvalidOperationException($"Duplicate offer {Upgrades.Name(id)}");
                    }
                }
            }
        }

        private static void Steer(HordeGame game, ref float ix, ref float iy) {
            var sprites = game.RenderBuffer;
            float best = 14f * 14f, gx = 0f, gy = 0f;
            float ax = 0f, ay = 0f;
            for (int i = 0; i < game.SpriteCount; i++) {
                int o = i * HordeGame.SpriteStride;
                float kind = sprites[o + 3];
                float dx = sprites[o] - game.CameraX, dy = sprites[o + 1] - game.CameraY;
                float d2 = dx * dx + dy * dy + 1e-3f;
                if (kind == 8f || kind == 9f) {
                    if (d2 < best) {
                        best = d2;
                        gx = dx;
                        gy = dy;
                    }
                } else if (kind >= 1f && kind <= 4f && d2 < 64f) {
                    ax -= dx / d2;
                    ay -= dy / d2;
                }
            }
            float sx = ix * 0.35f, sy = iy * 0.35f;
            if (gx != 0f || gy != 0f) {
                float d = MathF.Sqrt(best);
                sx += gx / d * 0.6f;
                sy += gy / d * 0.6f;
            }
            ix = sx + ax * 3f;
            iy = sy + ay * 3f;
        }
    }
}
