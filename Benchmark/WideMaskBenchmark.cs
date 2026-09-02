using BenchmarkDotNet.Attributes;

namespace EcsBenchmark {
    /// <summary>
    /// Production-shaped world: 1024 registered component types, so the
    /// benchmarked types live in the 17th mask word and every entity carries
    /// a 17-word mask. Measures iteration and structural changes there.
    /// LeoEcsLite has no per-entity mask, so it is the "flat" reference.
    /// </summary>
    [MemoryDiagnoser]
    public class WideMaskBenchmark {
        private const int PaddedTypes = 1024;

        // Touched only after the padding, so these land past the first 16 words.
        private struct HighPosition : KenseiECS.IComponent { public float X, Y; }
        private struct HighVelocity : KenseiECS.IComponent { public float X, Y; }
        private struct HighHealth : KenseiECS.IComponent { public float Value; }
        private struct HighTag : KenseiECS.IComponent { }

        private struct LitePosition { public float X, Y; }
        private struct LiteVelocity { public float X, Y; }
        private struct LiteHealth { public float Value; }
        private struct LiteTag { }

        private static readonly Type[] _kenseiPad = TypePadding.KenseiTypes(PaddedTypes);
        private static readonly Type[] _litePad = TypePadding.LiteTypes(PaddedTypes);

        [Params(10000)]
        public int N;

        private KenseiECS.World _kWorld = null!;
        private KenseiECS.Filter _kMoveFilter = null!;
        private KenseiECS.ComponentPool<HighPosition> _kPos = null!;
        private KenseiECS.ComponentPool<HighVelocity> _kVel = null!;
        private KenseiECS.Entity[] _kEntities = null!;

        private Leopotam.EcsLite.EcsWorld _lWorld = null!;
        private Leopotam.EcsLite.EcsFilter _lMoveFilter = null!;
        private Leopotam.EcsLite.EcsPool<LitePosition> _lPos = null!;
        private Leopotam.EcsLite.EcsPool<LiteVelocity> _lVel = null!;
        private Leopotam.EcsLite.EcsPool<LiteHealth> _lHealth = null!;
        private int[] _lEntities = null!;

        [GlobalSetup]
        public void Setup() {
            _kWorld = new KenseiECS.World();
            TypePadding.CreateKenseiPools(_kWorld, _kenseiPad);
            _kEntities = new KenseiECS.Entity[N];
            for (int i = 0; i < N; i++) {
                _kEntities[i] = _kWorld.CreateEntity(new HighPosition { X = i, Y = i });
                _kWorld.Add(_kEntities[i], new HighVelocity { X = 1, Y = 1 });
            }
            _kMoveFilter = _kWorld.Filter().Inc<HighPosition>().Inc<HighVelocity>().Exc<HighTag>().End();
            // Filters that watch HighHealth, spanning two mask words each.
            _kWorld.Filter().Inc<HighHealth>().Inc<HighPosition>().End();
            _kWorld.Filter().Inc<HighHealth>().Inc<TypePadding.KenseiPad<int>>().End();
            _kWorld.Filter().Inc<HighPosition>().Exc<HighHealth>().End();
            _kPos = _kWorld.Pool<HighPosition>();
            _kVel = _kWorld.Pool<HighVelocity>();
            _kWorld.Pool<HighHealth>();

            _lWorld = new Leopotam.EcsLite.EcsWorld();
            TypePadding.CreateLitePools(_lWorld, _litePad);
            _lPos = _lWorld.GetPool<LitePosition>();
            _lVel = _lWorld.GetPool<LiteVelocity>();
            _lHealth = _lWorld.GetPool<LiteHealth>();
            _lEntities = new int[N];
            for (int i = 0; i < N; i++) {
                _lEntities[i] = _lWorld.NewEntity();
                ref var pos = ref _lPos.Add(_lEntities[i]);
                pos.X = i; pos.Y = i;
                ref var vel = ref _lVel.Add(_lEntities[i]);
                vel.X = 1; vel.Y = 1;
            }
            _lMoveFilter = _lWorld.Filter<LitePosition>().Inc<LiteVelocity>().Exc<LiteTag>().End();
            _lWorld.Filter<LiteHealth>().Inc<LitePosition>().End();
            _lWorld.Filter<LiteHealth>().Inc<TypePadding.LitePad<int>>().End();
            _lWorld.Filter<LitePosition>().Exc<LiteHealth>().End();
        }

        [GlobalCleanup]
        public void Cleanup() {
            _kWorld?.Destroy();
            _lWorld?.Destroy();
        }

        [Benchmark(Baseline = true)]
        public void Iteration_KenseiECS() {
            foreach (int e in _kMoveFilter) {
                ref var pos = ref _kPos.Get(e);
                ref var vel = ref _kVel.Get(e);
                pos.X += vel.X;
                pos.Y += vel.Y;
            }
        }

        [Benchmark]
        public void Iteration_LeoEcsLite() {
            foreach (int e in _lMoveFilter) {
                ref var pos = ref _lPos.Get(e);
                ref var vel = ref _lVel.Get(e);
                pos.X += vel.X;
                pos.Y += vel.Y;
            }
        }

        [Benchmark]
        public void Structural_KenseiECS() {
            for (int i = 0; i < N; i++) {
                _kWorld.Add(_kEntities[i], new HighHealth { Value = 100 });
            }
            for (int i = 0; i < N; i++) {
                _kWorld.Remove<HighHealth>(_kEntities[i]);
            }
        }

        [Benchmark]
        public void Structural_LeoEcsLite() {
            for (int i = 0; i < N; i++) {
                _lHealth.Add(_lEntities[i]).Value = 100;
            }
            for (int i = 0; i < N; i++) {
                _lHealth.Del(_lEntities[i]);
            }
        }

        [Benchmark]
        public void DestroyCreate_KenseiECS() {
            for (int i = 0; i < N; i++) {
                _kWorld.DestroyEntity(_kEntities[i]);
            }
            for (int i = 0; i < N; i++) {
                _kEntities[i] = _kWorld.CreateEntity(new HighPosition { X = i, Y = i });
                _kWorld.Add(_kEntities[i], new HighVelocity { X = 1, Y = 1 });
            }
        }

        [Benchmark]
        public void DestroyCreate_LeoEcsLite() {
            for (int i = 0; i < N; i++) {
                _lWorld.DelEntity(_lEntities[i]);
            }
            for (int i = 0; i < N; i++) {
                _lEntities[i] = _lWorld.NewEntity();
                ref var pos = ref _lPos.Add(_lEntities[i]);
                pos.X = i; pos.Y = i;
                ref var vel = ref _lVel.Add(_lEntities[i]);
                vel.X = 1; vel.Y = 1;
            }
        }
    }
}
