using BenchmarkDotNet.Attributes;
using K = EcsBenchmark.Components.Kensei;
using L = EcsBenchmark.Components.LeoLite;
using A = EcsBenchmark.Components.ArchEcs;

namespace EcsBenchmark {
    /// <summary>
    /// Iteration after a long session: entities were created and destroyed in
    /// shuffled order, so filter order, pool dense order and entity indices no
    /// longer line up. This is where sparse-set lookups pay for cache misses
    /// that the in-order IterationBenchmark never shows.
    /// </summary>
    [MemoryDiagnoser]
    public class FragmentedIterationBenchmark {
        [Params(10000)]
        public int N;

        private KenseiECS.World _kWorld = null!;
        private KenseiECS.Filter _kFilter = null!;
        private KenseiECS.ComponentPool<K.Position> _kPos = null!;
        private KenseiECS.ComponentPool<K.Velocity> _kVel = null!;
        private KenseiECS.Group<K.Position, K.Velocity> _kGroup = null!;

        private Leopotam.EcsLite.EcsWorld _lWorld = null!;
        private Leopotam.EcsLite.EcsFilter _lFilter = null!;
        private Leopotam.EcsLite.EcsPool<L.Position> _lPos = null!;
        private Leopotam.EcsLite.EcsPool<L.Velocity> _lVel = null!;

        private global::Arch.Core.World _aWorld = null!;
        private global::Arch.Core.QueryDescription _aQuery;

        [GlobalSetup]
        public void Setup() {
            var rng = new Random(12345);
            int total = N * 2;
            var order = new int[total];
            for (int i = 0; i < total; i++) {
                order[i] = i;
            }
            for (int i = total - 1; i > 0; i--) {
                int j = rng.Next(i + 1);
                (order[i], order[j]) = (order[j], order[i]);
            }

            // KenseiECS: create 2N, add Velocity in shuffled order, destroy half in shuffled order.
            _kWorld = new KenseiECS.World();
            var kEntities = new KenseiECS.Entity[total];
            for (int i = 0; i < total; i++) {
                kEntities[i] = _kWorld.CreateEntity(new K.Position { X = i, Y = i });
            }
            for (int i = 0; i < total; i++) {
                _kWorld.Add(kEntities[order[i]], new K.Velocity { X = 1, Y = 1 });
            }
            _kFilter = _kWorld.Filter().Inc<K.Position>().Inc<K.Velocity>().End();
            for (int i = 0; i < N; i++) {
                _kWorld.DestroyEntity(kEntities[order[i]]);
            }
            _kPos = _kWorld.Pool<K.Position>();
            _kVel = _kWorld.Pool<K.Velocity>();
            _kGroup = _kWorld.Group<K.Position, K.Velocity>();

            // LeoEcsLite
            _lWorld = new Leopotam.EcsLite.EcsWorld();
            _lPos = _lWorld.GetPool<L.Position>();
            _lVel = _lWorld.GetPool<L.Velocity>();
            var lEntities = new int[total];
            for (int i = 0; i < total; i++) {
                lEntities[i] = _lWorld.NewEntity();
                ref var pos = ref _lPos.Add(lEntities[i]);
                pos.X = i; pos.Y = i;
            }
            for (int i = 0; i < total; i++) {
                ref var vel = ref _lVel.Add(lEntities[order[i]]);
                vel.X = 1; vel.Y = 1;
            }
            _lFilter = _lWorld.Filter<L.Position>().Inc<L.Velocity>().End();
            for (int i = 0; i < N; i++) {
                _lWorld.DelEntity(lEntities[order[i]]);
            }

            // Arch
            _aWorld = global::Arch.Core.World.Create();
            var aEntities = new global::Arch.Core.Entity[total];
            for (int i = 0; i < total; i++) {
                aEntities[i] = _aWorld.Create(new A.Position { X = i, Y = i });
            }
            for (int i = 0; i < total; i++) {
                _aWorld.Add(aEntities[order[i]], new A.Velocity { X = 1, Y = 1 });
            }
            for (int i = 0; i < N; i++) {
                _aWorld.Destroy(aEntities[order[i]]);
            }
            _aQuery = new global::Arch.Core.QueryDescription().WithAll<A.Position, A.Velocity>();
        }

        [GlobalCleanup]
        public void Cleanup() {
            _kWorld?.Destroy();
            _lWorld?.Destroy();
            if (_aWorld != null) {
                global::Arch.Core.World.Destroy(_aWorld);
            }
        }

        [Benchmark(Baseline = true)]
        public void KenseiECS() {
            foreach (int e in _kFilter) {
                ref var pos = ref _kPos.Get(e);
                ref var vel = ref _kVel.Get(e);
                pos.X += vel.X;
                pos.Y += vel.Y;
            }
        }

        [Benchmark]
        public void KenseiECS_Group() {
            var pos = _kGroup.Data1;
            var vel = _kGroup.Data2;
            for (int i = 0; i < pos.Length; i++) {
                pos[i].X += vel[i].X;
                pos[i].Y += vel[i].Y;
            }
        }

        [Benchmark]
        public void LeoEcsLite() {
            foreach (int e in _lFilter) {
                ref var pos = ref _lPos.Get(e);
                ref var vel = ref _lVel.Get(e);
                pos.X += vel.X;
                pos.Y += vel.Y;
            }
        }

        [Benchmark]
        public void ArchEcs() {
            _aWorld.Query(in _aQuery, (ref A.Position pos, ref A.Velocity vel) => {
                pos.X += vel.X;
                pos.Y += vel.Y;
            });
        }
    }
}
