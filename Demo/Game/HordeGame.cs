using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace KenseiECS.Demo {
    /// <summary> Snapshot of what the HUD shows. </summary>
    public readonly struct HudState {
        public readonly float Hp, MaxHp;
        public readonly int Level;
        public readonly float Xp, XpToNext;
        public readonly float TimeSec;
        public readonly int Kills;
        public readonly GameStatus Status;
        public readonly int Choice0, Choice1, Choice2;

        public HudState(float hp, float maxHp, int level, float xp, float xpToNext, float timeSec, int kills,
            GameStatus status, int choice0, int choice1, int choice2) {
            Hp = hp;
            MaxHp = maxHp;
            Level = level;
            Xp = xp;
            XpToNext = xpToNext;
            TimeSec = timeSec;
            Kills = kills;
            Status = status;
            Choice0 = choice0;
            Choice1 = choice1;
            Choice2 = choice2;
        }
    }

    /// <summary>
    /// "Horde" — a survivors-like built on KenseiECS. The host calls Tick once per
    /// frame with the elapsed time and movement input, then draws RenderBuffer.
    ///
    /// Two named phases hang off one root SystemsRunner:
    ///   "sim"    — gameplay; runs only while Status == Playing
    ///   "render" — fills the sprite buffer; runs every Tick, also while paused
    /// </summary>
    public sealed class HordeGame {
        public const int SpriteStride = SpriteBuffer.Stride;
        public const float ViewHeight = 36f;

        private readonly World _world;
        private readonly SharedData _shared;
        private readonly GameConfig _config;
        private readonly SpatialGrid _grid;
        private readonly SpriteBuffer _render;
        private readonly GameRandom _rng;
        private readonly SystemsRunner _root;
        private readonly SystemsRunner _sim;
        private readonly SystemsRunner _renderPhase;
        private readonly ContactDamageSystem _contact;
        private readonly int _seed;
        private int _runs;
        private float _cameraX, _cameraY;
        private double _frameMs;

        public HordeGame(int seed = 1) {
            _seed = seed;
            _world = new World(new WorldConfig {
                InitialEntityCapacity = 8192,
                InitialPoolSparseCapacity = 8192,
                InitialPoolDenseCapacity = 1024,
                InitialPoolCount = 64
            });

            _config = new GameConfig();
            _grid = new SpatialGrid();
            _render = new SpriteBuffer();
            _rng = new GameRandom(seed);
            // #region shared-data
            _shared = new SharedData();
            _shared.Add(_config);
            _shared.Add(_grid);
            _shared.Add(_render);
            _shared.Add(_rng);
            // #endregion shared-data

            _contact = new ContactDamageSystem();

            // #region runner
            // Gameplay phase. Order matters: producers of events run before consumers,
            // and the OneFrame types are removed after the last system.
            _sim = new SystemsRunner(_world)
                .Add(new ClockSystem(), "clock")
                .Add(new PlayerMoveSystem(), "player-move")
                .Add(new WaveSpawnSystem(), "spawner")
                .Add(new GridBuildSystem(), "grid")
                .Add(new EnemySteerSystem(), "steer")
                .Add(new IntegrateSystem(), "integrate")
                .Add(new BoltWeaponSystem(), "bolt-fire")
                .Add(new BoltHitSystem(), "bolt-hit")
                .Add(new BladeSystem(), "blades")
                .Add(new NovaSystem(), "nova")
                .Add(_contact, "contact")
                .Add(new KnockbackSystem(), "knockback")
                .Add(new DamageSystem(), "damage")
                .Add(new LootSystem(), "loot")
                .Add(new MagnetSystem(), "magnet")
                .Add(new LevelUpSystem(), "level-up")
                .Add(new LifetimeSystem(), "lifetime")
                // #region one-frame
                .OneFrame<EventBuffer<Hit>>()
                .OneFrame<Died>()
                .OneFrame<XpGained>();
                // #endregion one-frame

            // Presentation phase, in draw order.
            _renderPhase = new SystemsRunner(_world)
                .Add(new RenderGemsSystem(), "render-gems")
                .Add(new RenderEnemiesSystem(), "render-enemies")
                .Add(new RenderProjectilesSystem(), "render-projectiles")
                .Add(new RenderParticlesSystem(), "render-particles")
                .Add(new RenderPlayerSystem(), "render-player");

            // Named children are separate phases: Init cascades from the root,
            // but each phase is driven explicitly with its own Run().
            _root = new SystemsRunner(_world, _shared)
                .Add(_sim, "sim")
                .Add(_renderPhase, "render");

            _root.Warmup();   // Init every system, then pre-touch pools and filters
            // #endregion runner
            Restart();
        }

        // =================================================================
        // Frame
        // =================================================================

        public void Tick(float dt, float inputX, float inputY) {
            long start = Stopwatch.GetTimestamp();

            if (!(dt > 0f)) {
                dt = 0f;
            } else if (dt > 1f / 20f) {
                dt = 1f / 20f;
            }
            if (!float.IsFinite(inputX) || !float.IsFinite(inputY)) {
                inputX = inputY = 0f;
            }
            float len2 = inputX * inputX + inputY * inputY;
            if (len2 > 1f) {
                float inv = 1f / MathF.Sqrt(len2);
                inputX *= inv;
                inputY *= inv;
            }

            // #region tick
            ref var input = ref _world.GetSingleton<PlayerInput>();
            input.X = inputX;
            input.Y = inputY;
            _world.GetSingleton<GameState>().Dt = dt;

            if (dt > 0f && _world.GetSingleton<GameState>().Status == GameStatus.Playing) {
                _world.NextTick();   // only a root Run() ticks; phases are driven directly
                _grid.Clear();
                _sim.Run();
            }

            RenderFrame();
            // #endregion tick
            _frameMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        }

        private void RenderFrame() {
            var p = _world.Get<Position>(_world.GetSingletonEntity<Player>());
            _cameraX = p.X;
            _cameraY = p.Y;
            float halfH = ViewHeight * 0.5f + 1f;
            _render.Begin(_cameraX, _cameraY, halfH * 2.6f, halfH);
            _renderPhase.Run();
        }

        // =================================================================
        // Output
        // =================================================================

        public ReadOnlySpan<float> RenderBuffer => new(_render.Data, 0, _render.Count * SpriteStride);

        public int SpriteCount => _render.Count;

        public float CameraX => _cameraX;

        public float CameraY => _cameraY;

        public HudState Hud {
            get {
                ref var s = ref _world.GetSingleton<GameState>();
                ref var p = ref _world.GetSingleton<Player>();
                return new HudState(p.Hp, p.MaxHp, s.Level, s.Xp, s.XpToNext, s.Time, s.Kills, s.Status,
                    s.Choice0, s.Choice1, s.Choice2);
            }
        }

        /// <summary> Enemies currently alive. </summary>
        public int EnemyCount => _world.Pool<Enemy>().Count;

        /// <summary> Most enemies alive at once during this run. </summary>
        public int PeakEnemies => _world.GetSingleton<GameState>().PeakEnemies;

        /// <summary> Wall time of the last Tick, in milliseconds. </summary>
        public double FrameMs => _frameMs;

        public static string UpgradeName(int id) => Upgrades.Name(id);

        public static string UpgradeDescription(int id) => Upgrades.Description(id);

        // =================================================================
        // Control
        // =================================================================

        public void ChooseUpgrade(int slot) {
            ref var state = ref _world.GetSingleton<GameState>();
            if (state.Status != GameStatus.LevelUp || (uint)slot > 2) {
                return;
            }
            int id = slot == 0 ? state.Choice0 : slot == 1 ? state.Choice1 : state.Choice2;
            Upgrades.Apply(_world, _config, id);
            state.PendingLevels--;
            if (state.PendingLevels > 0) {
                Upgrades.Roll(_world, _rng, ref state);
            } else {
                state.Status = GameStatus.Playing;
            }
        }

        public void Restart() {
            _world.Clear();
            _rng.Reset(_seed + _runs++ * 7919);

            var game = _world.CreateEntity(new GameState {
                Level = 1,
                XpToNext = _config.XpForLevel(1),
                Status = GameStatus.Playing,
                NextSwarm = 35f,
                NextElite = 90f
            });
            _world.Add(game, new PlayerInput());
            _world.SetName(game, "Game");

            var player = _world.CreateEntity(new Player {
                Hp = _config.PlayerMaxHp,
                MaxHp = _config.PlayerMaxHp,
                Speed = _config.PlayerSpeed,
                Magnet = _config.PlayerMagnet,
                DamageMul = 1f,
                CooldownMul = 1f
            });
            _world.Add(player, new Position());
            _world.Add(player, new Velocity());
            _world.Add(player, new Radius { Value = _config.PlayerRadius });
            _world.Add(player, new UpgradeLevels());
            _world.Add(player, new BoltWeapon { Cooldown = 0.5f, Damage = 12f, Count = 1 });
            _world.SetName(player, "Player");

            RenderFrame();
        }

        public float StressMultiplier {
            get => _config.StressMultiplier;
            set => _config.StressMultiplier = float.IsNaN(value) ? 1f : MathF.Max(0.25f, MathF.Min(25f, value));
        }

        /// <summary> Lethal damage refills HP instead of ending the run (for soak tests). </summary>
        public bool GodMode {
            get => _contact.GodMode;
            set => _contact.GodMode = value;
        }

        /// <summary> Enable or disable a system (or a whole phase: "sim", "render") by name. </summary>
        public bool SetSystemActive(string name, bool active) {
            if (name == "sim" || name == "render") {
                _root.SetActive(name, active);
                return true;
            }
            return SetActiveIn(_sim, name, active) || SetActiveIn(_renderPhase, name, active);
        }

        private static bool SetActiveIn(SystemsRunner runner, string name, bool active) {
            for (int i = 0; i < runner.SystemCount; i++) {
                if (runner.GetSystemInfo(i).Name == name) {
                    runner.SetActive(i, active);
                    return true;
                }
            }
            return false;
        }

        // =================================================================
        // Introspection (allocates; meant for a few calls per second)
        // =================================================================

        public string StatsJson() {
            var sb = new StringBuilder(4096);
            sb.Append("{\"entities\":").Append(_world.EntityCount);
            sb.Append(",\"enemies\":").Append(EnemyCount);
            sb.Append(",\"frameMs\":").Append(Num(_frameMs));
#if KENSEI_DEBUG
            sb.Append(",\"timings\":true");
#else
            sb.Append(",\"timings\":false");
#endif

            sb.Append(",\"filters\":[");
            for (int i = 0; i < _world.FilterCount; i++) {
                var filter = _world.GetFilter(i);
                if (i > 0) {
                    sb.Append(',');
                }
                sb.Append("{\"name\":");
                Str(sb, FilterName(filter));
                sb.Append(",\"count\":").Append(filter.Count).Append('}');
            }
            sb.Append(']');

            sb.Append(",\"pools\":[");
            bool first = true;
            foreach (var pool in _world.ActivePools) {
                if (!first) {
                    sb.Append(',');
                }
                first = false;
                sb.Append("{\"type\":");
                Str(sb, TypeName(pool.ComponentType));
                sb.Append(",\"count\":").Append(pool.Count);
                sb.Append(",\"bytes\":").Append(pool.AllocatedBytes).Append('}');
            }
            sb.Append(']');

            sb.Append(",\"systems\":[");
            first = true;
            AppendSystems(sb, _sim, "sim", ref first);
            AppendSystems(sb, _renderPhase, "render", ref first);
            sb.Append("]}");
            return sb.ToString();
        }

        private static void AppendSystems(StringBuilder sb, SystemsRunner runner, string phase, ref bool first) {
            bool phaseActive = runner.IsEnabled;
            for (int i = 0; i < runner.SystemCount; i++) {
                var info = runner.GetSystemInfo(i);
                if (!first) {
                    sb.Append(',');
                }
                first = false;
                sb.Append("{\"name\":");
                Str(sb, info.Name);
                sb.Append(",\"phase\":\"").Append(phase).Append('"');
                sb.Append(",\"active\":").Append(info.IsEnabled && phaseActive ? "true" : "false");
#if KENSEI_DEBUG
                sb.Append(",\"lastMs\":").Append(Num(info.LastRunMs));
                sb.Append(",\"peakMs\":").Append(Num(info.PeakRunMs));
#else
                sb.Append(",\"lastMs\":0,\"peakMs\":0");
#endif
                sb.Append(",\"file\":");
                Str(sb, "Demo/Game/Systems/" + info.System.GetType().Name + ".cs");
                sb.Append('}');
            }
        }

        private static string FilterName(Filter filter) {
            var sb = new StringBuilder();
            AppendTypes(sb, "Inc", filter.IncludedTypes);
            AppendTypes(sb, "Exc", filter.ExcludedTypes);
            AppendTypes(sb, "Any", filter.AnyTypes);
            return sb.ToString();
        }

        private static void AppendTypes(StringBuilder sb, string label, ReadOnlySpan<int> types) {
            if (types.Length == 0) {
                return;
            }
            if (sb.Length > 0) {
                sb.Append(' ');
            }
            sb.Append(label).Append('<');
            for (int i = 0; i < types.Length; i++) {
                if (i > 0) {
                    sb.Append(", ");
                }
                sb.Append(TypeName(ComponentType.TypeOf(types[i])));
            }
            sb.Append('>');
        }

        private static string TypeName(Type type) {
            if (!type.IsGenericType) {
                return type.Name;
            }
            string name = type.Name;
            int tick = name.IndexOf('`');
            var sb = new StringBuilder(tick >= 0 ? name.Substring(0, tick) : name).Append('<');
            var args = type.GetGenericArguments();
            for (int i = 0; i < args.Length; i++) {
                if (i > 0) {
                    sb.Append(", ");
                }
                sb.Append(TypeName(args[i]));
            }
            return sb.Append('>').ToString();
        }

        private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        private static void Str(StringBuilder sb, string value) {
            sb.Append('"');
            foreach (char c in value) {
                if (c == '"' || c == '\\') {
                    sb.Append('\\');
                }
                sb.Append(c);
            }
            sb.Append('"');
        }
    }
}
