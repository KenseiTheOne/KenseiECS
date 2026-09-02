using System;
using System.Collections.Generic;
using KenseiECS;

namespace KenseiECS.Example {
    // ==========================================================================
    // COMPONENTS
    // ==========================================================================

    internal struct Pos         : IComponent { public int X, Y; }
    internal struct PredatorTag : IComponent { }
    internal struct PreyTag     : IComponent { }
    internal struct MoveTimer   : IComponent { public int Ticks; public int Interval; }
    internal struct FreezeTimer : IComponent { public int Ticks; }
    internal struct Chasing     : IComponent { public int TargetIndex; }
    internal struct Fleeing     : IComponent { }

    // ==========================================================================
    // SHARED DATA
    // ==========================================================================

    internal class GameConfig {
        public int Width             = 50;
        public int Height            = 20;
        public int PredSightRange    = 10;
        public int PreySightRange    = 5;
        public int PreyFleeStopRange = 10;
        public int PredMoveInterval  = 2;  // ticks (2 * 0.5s = 1s)
        public int PreyMoveInterval  = 3;  // ticks (3 * 0.5s = 1.5s)
        public int FreezeTicks       = 20; // 10s = 20 ticks
        public Random Rng            = new Random();
    }

    internal class GameState {
        public bool   Over    = false;
        public string Message = "";
    }

    // ==========================================================================
    // SYSTEMS
    // ==========================================================================

    // --- Spawn 5 predators + 10 prey at random positions ---
    internal class InitSystem : IInitSystem {
        public void Init(World world, SharedData shared) {
            var cfg = shared.Get<GameConfig>();
            var rng = cfg.Rng;

            for (int i = 0; i < 5; i++) {
                var pred = world.CreateEntity(new Pos { X = rng.Next(cfg.Width), Y = rng.Next(cfg.Height) });
                world.Add(pred, new PredatorTag());
                world.Add(pred, new MoveTimer { Ticks = cfg.PredMoveInterval, Interval = cfg.PredMoveInterval });
            }

            for (int i = 0; i < 10; i++) {
                var prey = world.CreateEntity(new Pos { X = rng.Next(cfg.Width), Y = rng.Next(cfg.Height) });
                world.Add(prey, new PreyTag());
                world.Add(prey, new MoveTimer { Ticks = cfg.PreyMoveInterval, Interval = cfg.PreyMoveInterval });
            }
        }
    }

    // --- Count down freeze timers; remove when expired ---
    internal class FreezeTickSystem : IInitSystem, IRunSystem {
        private Filter _filter;
        private ComponentPool<FreezeTimer> _timers;

        public void Init(World world, SharedData shared) {
            _filter = world.Filter().Inc<FreezeTimer>().End();
            _timers = world.Pool<FreezeTimer>();
        }

        public void Run(World world) {
            foreach (int e in _filter) {
                ref var t = ref _timers.Get(e);
                if (--t.Ticks <= 0) {
                    world.Remove<FreezeTimer>(world.GetEntity(e));
                }
            }
        }
    }

    // --- Predator AI: find closest prey in sight, set/update Chasing ---
    internal class PredatorAISystem : IInitSystem, IRunSystem {
        private Filter _predFilter;
        private Filter _preyFilter;
        private ComponentPool<Pos> _pos;
        private ComponentPool<Chasing> _chasing;
        private GameConfig _cfg;

        public void Init(World world, SharedData shared) {
            _predFilter = world.Filter().Inc<PredatorTag>().Inc<Pos>().Exc<FreezeTimer>().End();
            _preyFilter = world.Filter().Inc<PreyTag>().Inc<Pos>().End();
            _pos     = world.Pool<Pos>();
            _chasing = world.Pool<Chasing>();
            _cfg     = shared.Get<GameConfig>();
        }

        public void Run(World world) {
            foreach (int pred in _predFilter) {
                ref var predPos = ref _pos.Get(pred);

                int best = -1, bestDist = int.MaxValue;
                foreach (int prey in _preyFilter) {
                    ref var preyPos = ref _pos.Get(prey);
                    int dist = MoveHelpers.Manhattan(predPos, preyPos);
                    if (dist <= _cfg.PredSightRange && dist < bestDist) {
                        bestDist = dist;
                        best = prey;
                    }
                }

                var entity = world.GetEntity(pred);
                if (best >= 0) {
                    if (_chasing.Has(pred)) {
                        _chasing.Get(pred).TargetIndex = best;
                    } else {
                        world.Add(entity, new Chasing { TargetIndex = best });
                    }
                } else if (_chasing.Has(pred)) {
                    world.Remove<Chasing>(entity);
                }
            }
        }
    }

    // --- Prey AI: start fleeing when predator is close, stop when far enough ---
    internal class PreyAISystem : IInitSystem, IRunSystem {
        private Filter _preyFilter;
        private Filter _predFilter;
        private ComponentPool<Pos> _pos;
        private ComponentPool<Fleeing> _fleeing;
        private GameConfig _cfg;

        public void Init(World world, SharedData shared) {
            _preyFilter = world.Filter().Inc<PreyTag>().Inc<Pos>().End();
            _predFilter = world.Filter().Inc<PredatorTag>().Inc<Pos>().End();
            _pos     = world.Pool<Pos>();
            _fleeing = world.Pool<Fleeing>();
            _cfg     = shared.Get<GameConfig>();
        }

        public void Run(World world) {
            foreach (int prey in _preyFilter) {
                ref var preyPos = ref _pos.Get(prey);

                int minDist = int.MaxValue;
                foreach (int pred in _predFilter) {
                    ref var predPos = ref _pos.Get(pred);
                    int d = MoveHelpers.Manhattan(preyPos, predPos);
                    if (d < minDist) {
                        minDist = d;
                    }
                }

                if (minDist == int.MaxValue) {
                    if (_fleeing.Has(prey)) {
                        world.Remove<Fleeing>(world.GetEntity(prey));
                    }
                    continue;
                }

                bool fleeing = _fleeing.Has(prey);
                bool shouldFlee = fleeing
                    ? minDist <= _cfg.PreyFleeStopRange
                    : minDist <= _cfg.PreySightRange;

                var entity = world.GetEntity(prey);
                if (shouldFlee && !fleeing) {
                    world.Add(entity, new Fleeing());
                } else if (!shouldFlee && fleeing) {
                    world.Remove<Fleeing>(entity);
                }
            }
        }
    }

    // --- Move predator: chase prey or wander randomly ---
    internal class PredatorMoveSystem : IInitSystem, IRunSystem {
        private Filter _filter;
        private ComponentPool<Pos> _pos;
        private ComponentPool<MoveTimer> _timer;
        private ComponentPool<Chasing> _chasing;
        private GameConfig _cfg;

        public void Init(World world, SharedData shared) {
            _filter  = world.Filter().Inc<PredatorTag>().Inc<Pos>().Inc<MoveTimer>().Exc<FreezeTimer>().End();
            _pos     = world.Pool<Pos>();
            _timer   = world.Pool<MoveTimer>();
            _chasing = world.Pool<Chasing>();
            _cfg     = shared.Get<GameConfig>();
        }

        public void Run(World world) {
            foreach (int e in _filter) {
                ref var t = ref _timer.Get(e);
                if (--t.Ticks > 0) {
                    continue;
                }
                t.Ticks = t.Interval;

                ref var pos = ref _pos.Get(e);

                if (_chasing.Has(e)) {
                    int target = _chasing.Get(e).TargetIndex;
                    if (_pos.Has(target)) {
                        MoveHelpers.StepTowards(ref pos, _pos.Get(target));
                    }
                } else if (_cfg.Rng.NextDouble() < 0.5) {
                    MoveHelpers.RandomStep(ref pos, _cfg.Rng);
                }

                MoveHelpers.Clamp(ref pos, _cfg);
            }
        }
    }

    // --- Move prey: flee from nearest predator or wander randomly ---
    internal class PreyMoveSystem : IInitSystem, IRunSystem {
        private Filter _filter;
        private Filter _predFilter;
        private ComponentPool<Pos> _pos;
        private ComponentPool<MoveTimer> _timer;
        private ComponentPool<Fleeing> _fleeing;
        private GameConfig _cfg;

        public void Init(World world, SharedData shared) {
            _filter     = world.Filter().Inc<PreyTag>().Inc<Pos>().Inc<MoveTimer>().End();
            _predFilter = world.Filter().Inc<PredatorTag>().Inc<Pos>().End();
            _pos     = world.Pool<Pos>();
            _timer   = world.Pool<MoveTimer>();
            _fleeing = world.Pool<Fleeing>();
            _cfg     = shared.Get<GameConfig>();
        }

        public void Run(World world) {
            foreach (int e in _filter) {
                ref var t = ref _timer.Get(e);
                if (--t.Ticks > 0) {
                    continue;
                }
                t.Ticks = t.Interval;

                ref var pos = ref _pos.Get(e);

                if (_fleeing.Has(e)) {
                    int nearX = -1, nearY = -1, minDist = int.MaxValue;
                    foreach (int p in _predFilter) {
                        ref var pp = ref _pos.Get(p);
                        int d = MoveHelpers.Manhattan(pos, pp);
                        if (d < minDist) {
                            minDist = d;
                            nearX = pp.X;
                            nearY = pp.Y;
                        }
                    }
                    if (nearX >= 0) {
                        int dx = pos.X - nearX;
                        int dy = pos.Y - nearY;
                        if (dx == 0 && dy == 0) {
                            MoveHelpers.RandomStep(ref pos, _cfg.Rng);
                        } else if (Math.Abs(dx) >= Math.Abs(dy)) {
                            pos.X += Math.Sign(dx);
                        } else {
                            pos.Y += Math.Sign(dy);
                        }
                    }
                } else if (_cfg.Rng.NextDouble() < 0.5) {
                    MoveHelpers.RandomStep(ref pos, _cfg.Rng);
                }

                MoveHelpers.Clamp(ref pos, _cfg);
            }
        }
    }

    // --- Catch logic: predator on same cell as prey ---
    internal class CatchSystem : IInitSystem, IRunSystem {
        private Filter _predFilter;
        private Filter _preyFilter;
        private ComponentPool<Pos> _pos;
        private ComponentPool<FreezeTimer> _freeze;
        private ComponentPool<Chasing> _chasing;
        private GameConfig _cfg;
        private GameState _state;
        private readonly List<int> _predSnap = new List<int>();
        private readonly List<int> _preySnap = new List<int>();

        public void Init(World world, SharedData shared) {
            _predFilter = world.Filter().Inc<PredatorTag>().Inc<Pos>().Exc<FreezeTimer>().End();
            _preyFilter = world.Filter().Inc<PreyTag>().Inc<Pos>().End();
            _pos    = world.Pool<Pos>();
            _freeze = world.Pool<FreezeTimer>();
            _chasing = world.Pool<Chasing>();
            _cfg    = shared.Get<GameConfig>();
            _state  = shared.Get<GameState>();
        }

        public void Run(World world) {
            _predSnap.Clear();
            _preySnap.Clear();
            foreach (int e in _predFilter) { _predSnap.Add(e); }
            foreach (int e in _preyFilter) { _preySnap.Add(e); }

            foreach (int pred in _predSnap) {
                if (!_pos.Has(pred)) { continue; }
                ref var predPos = ref _pos.Get(pred);

                foreach (int prey in _preySnap) {
                    if (!_pos.Has(prey)) { continue; }
                    ref var preyPos = ref _pos.Get(prey);
                    if (predPos.X != preyPos.X || predPos.Y != preyPos.Y) { continue; }

                    if (_cfg.Rng.NextDouble() < 0.5) {
                        world.DestroyEntity(world.GetEntity(prey));
                    } else {
                        var predEntity = world.GetEntity(pred);
                        world.Add(predEntity, new FreezeTimer { Ticks = _cfg.FreezeTicks });
                        if (_chasing.Has(pred)) {
                            world.Remove<Chasing>(predEntity);
                        }
                        SpawnPrey(world);
                    }
                    break;
                }
            }

            if (_preyFilter.Count == 0) {
                _state.Over    = true;
                _state.Message = "Хищники поймали всех жертв!";
            }
        }

        private void SpawnPrey(World world) {
            var e = world.CreateEntity(new Pos { X = _cfg.Rng.Next(_cfg.Width), Y = _cfg.Rng.Next(_cfg.Height) });
            world.Add(e, new PreyTag());
            world.Add(e, new MoveTimer { Ticks = _cfg.PreyMoveInterval, Interval = _cfg.PreyMoveInterval });
        }
    }

    // --- Render the field to console ---
    internal class RenderSystem : IInitSystem, IRunSystem {
        private Filter _predFilter;
        private Filter _preyFilter;
        private ComponentPool<Pos> _pos;
        private ComponentPool<FreezeTimer> _freeze;
        private ComponentPool<Chasing> _chasing;
        private ComponentPool<Fleeing> _fleeing;
        private GameConfig _cfg;
        private GameState _state;
        private int _tick;
        private (char Ch, ConsoleColor Color)[] _row = Array.Empty<(char, ConsoleColor)>();

        public void Init(World world, SharedData shared) {
            _predFilter = world.Filter().Inc<PredatorTag>().Inc<Pos>().End();
            _preyFilter = world.Filter().Inc<PreyTag>().Inc<Pos>().End();
            _pos     = world.Pool<Pos>();
            _freeze  = world.Pool<FreezeTimer>();
            _chasing = world.Pool<Chasing>();
            _fleeing = world.Pool<Fleeing>();
            _cfg     = shared.Get<GameConfig>();
            _state   = shared.Get<GameState>();
            _row     = new (char, ConsoleColor)[_cfg.Width];
        }

        public void Run(World world) {
            _tick++;
            int W = _cfg.Width, H = _cfg.Height;

            Console.SetCursorPosition(0, 0);

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write('+');
            Console.Write(new string('-', W));
            Console.WriteLine('+');

            for (int y = 0; y < H; y++) {
                for (int x = 0; x < W; x++) {
                    _row[x] = ('.', ConsoleColor.DarkGray);
                }

                foreach (int e in _preyFilter) {
                    ref var p = ref _pos.Get(e);
                    if (p.Y != y) { continue; }
                    bool fl = _fleeing.Has(e);
                    _row[p.X] = (fl ? 'v' : 'V', fl ? ConsoleColor.Green : ConsoleColor.DarkGreen);
                }

                foreach (int e in _predFilter) {
                    ref var p = ref _pos.Get(e);
                    if (p.Y != y) { continue; }
                    bool isFrozen  = _freeze.Has(e);
                    bool isChasing = _chasing.Has(e);
                    char ch  = isFrozen ? 'F' : (isChasing ? 'P' : 'p');
                    var clr  = isFrozen ? ConsoleColor.DarkRed : ConsoleColor.Red;
                    _row[p.X] = (ch, clr);
                }

                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write('|');
                for (int x = 0; x < W; x++) {
                    Console.ForegroundColor = _row[x].Color;
                    Console.Write(_row[x].Ch);
                }
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine('|');
            }

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write('+');
            Console.Write(new string('-', W));
            Console.WriteLine('+');

            Console.ResetColor();

            int  preyCount  = _preyFilter.Count;
            int  predCount  = _predFilter.Count;
            int  frozen     = 0;
            foreach (int e in _predFilter) {
                if (_freeze.Has(e)) { frozen++; }
            }

            Console.Write($" Тик {_tick,-5} | Жертв: {preyCount,-3} | Хищников: {predCount} ({frozen} заморожено)   ");
            Console.WriteLine("[Q] выход");

            Console.ForegroundColor = ConsoleColor.Red;         Console.Write(" P/p");
            Console.ResetColor();                               Console.Write(" хищник  ");
            Console.ForegroundColor = ConsoleColor.DarkRed;    Console.Write("F");
            Console.ResetColor();                               Console.Write(" заморожен  ");
            Console.ForegroundColor = ConsoleColor.DarkGreen;  Console.Write("V");
            Console.ResetColor();                               Console.Write(" жертва  ");
            Console.ForegroundColor = ConsoleColor.Green;      Console.Write("v");
            Console.ResetColor();                               Console.WriteLine(" убегает  ");

            Console.ResetColor();
        }
    }

    // ==========================================================================
    // HELPERS
    // ==========================================================================

    internal static class MoveHelpers {
        public static int Manhattan(Pos a, Pos b) {
            return Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
        }

        public static void StepTowards(ref Pos from, Pos to) {
            int dx = to.X - from.X;
            int dy = to.Y - from.Y;
            if (Math.Abs(dx) >= Math.Abs(dy)) {
                from.X += Math.Sign(dx);
            } else {
                from.Y += Math.Sign(dy);
            }
        }

        public static void RandomStep(ref Pos pos, Random rng) {
            switch (rng.Next(4)) {
                case 0: pos.X++; break;
                case 1: pos.X--; break;
                case 2: pos.Y++; break;
                default: pos.Y--; break;
            }
        }

        public static void Clamp(ref Pos pos, GameConfig cfg) {
            pos.X = Math.Clamp(pos.X, 0, cfg.Width  - 1);
            pos.Y = Math.Clamp(pos.Y, 0, cfg.Height - 1);
        }
    }

    // ==========================================================================
    // ENTRY POINT
    // ==========================================================================

    internal static class Program {
        private static int Main(string[] args) {
            if (args.Length == 2 && args[0] == "--headless") {
                return RunHeadless(int.Parse(args[1]));
            }

            RunInteractive();
            return 0;
        }

        // Same simulation without the console renderer: for CI and for checking
        // the framework end to end without a terminal.
        private static int RunHeadless(int ticks) {
            var world = new World(new WorldConfig {
                InitialEntityCapacity    = 64,
                InitialPoolDenseCapacity = 16
            });

            var cfg    = new GameConfig();
            var state  = new GameState();
            var shared = new SharedData();
            shared.Add(cfg);
            shared.Add(state);

            var systems = new SystemsRunner(world, shared)
                .Add(new InitSystem())
                .Add(new FreezeTickSystem())
                .Add(new PredatorAISystem())
                .Add(new PreyAISystem())
                .Add(new PredatorMoveSystem())
                .Add(new PreyMoveSystem())
                .Add(new CatchSystem());

            systems.Warmup();

            var predators = world.Filter().Inc<PredatorTag>().End();
            var prey      = world.Filter().Inc<PreyTag>().End();
            var frozen    = world.Filter().Inc<PredatorTag>().Inc<FreezeTimer>().End();

            int tick = 0;
            for (; tick < ticks && !state.Over; tick++) {
                systems.Run();
                if (tick % 100 == 0) {
                    Console.WriteLine($"tick {tick,5}: prey {prey.Count,3}, predators {predators.Count} ({frozen.Count} frozen), entities {world.EntityCount}");
                }
            }

            Console.WriteLine(state.Over
                ? $"finished at tick {tick}: {state.Message}"
                : $"stopped after {tick} ticks: prey {prey.Count}, predators {predators.Count}");

            systems.Destroy();
            world.Destroy();
            return 0;
        }

        private static void RunInteractive() {
            try {
                int needW = 52, needH = 27;
                if (Console.WindowWidth  < needW) { Console.SetWindowSize(Math.Min(needW, Console.LargestWindowWidth),  Console.WindowHeight); }
                if (Console.WindowHeight < needH) { Console.SetWindowSize(Console.WindowWidth, Math.Min(needH, Console.LargestWindowHeight)); }
                Console.SetBufferSize(Math.Max(Console.BufferWidth, needW), Math.Max(Console.BufferHeight, 200));
            } catch { }

            Console.CursorVisible = false;
            Console.Clear();

            var world = new World(new WorldConfig {
                InitialEntityCapacity    = 64,
                InitialPoolDenseCapacity = 16
            });

            var cfg    = new GameConfig();
            var state  = new GameState();
            var shared = new SharedData();
            shared.Add(cfg);
            shared.Add(state);

            var systems = new SystemsRunner(world, shared)
                .Add(new InitSystem())
                .Add(new FreezeTickSystem())
                .Add(new PredatorAISystem())
                .Add(new PreyAISystem())
                .Add(new PredatorMoveSystem())
                .Add(new PreyMoveSystem())
                .Add(new CatchSystem())
                .Add(new RenderSystem());

            systems.Init();

            var tickDuration = TimeSpan.FromSeconds(0.5);
            var lastTick     = DateTime.UtcNow - tickDuration;

            while (!state.Over) {
                if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Q) {
                    break;
                }

                if (DateTime.UtcNow - lastTick >= tickDuration) {
                    lastTick = DateTime.UtcNow;
                    systems.Run();
                }

                System.Threading.Thread.Sleep(10);
            }

            if (state.Over) {
                Console.SetCursorPosition(0, cfg.Height + 4);
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(' ' + state.Message);
                Console.ResetColor();
            }

            systems.Destroy();
            world.Destroy();
            Console.CursorVisible = true;
            Console.WriteLine("\n Нажмите любую клавишу...");
            Console.ReadKey(true);
        }
    }
}
