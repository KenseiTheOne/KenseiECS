using BenchmarkDotNet.Attributes;
using Leopotam.Ecs;
using K = EcsBenchmark.Components.Kensei;
using L = EcsBenchmark.Components.LeoLite;
using A = EcsBenchmark.Components.ArchEcs;
using O = EcsBenchmark.Components.Leo;

namespace EcsBenchmark {
    [MemoryDiagnoser]
    public class StructuralChangeBenchmark {
        [Params(100, 1000, 10000)]
        public int N;

        // ---- KenseiECS ----
        private KenseiECS.World _kWorld = null!;
        private KenseiECS.Entity[] _kEntities = null!;

        // ---- LeoECS ----
        private EcsWorld _oWorld = null!;
        private EcsEntity[] _oEntities = null!;

        // ---- LeoEcsLite ----
        private Leopotam.EcsLite.EcsWorld _lWorld = null!;
        private Leopotam.EcsLite.EcsPool<L.Health> _lHealthPool = null!;
        private int[] _lEntities = null!;

        // ---- Arch ----
        private global::Arch.Core.World _aWorld = null!;
        private global::Arch.Core.Entity[] _aEntities = null!;

        [GlobalSetup]
        public void Setup() {
            // KenseiECS
            _kWorld = new KenseiECS.World();
            _kEntities = new KenseiECS.Entity[N];
            for (int i = 0; i < N; i++) {
                _kEntities[i] = _kWorld.CreateEntity(
                    new K.Position { X = i, Y = i },
                    new K.Velocity { X = 1, Y = 1 });
            }

            // LeoECS
            _oWorld = new EcsWorld();
            _oEntities = new EcsEntity[N];
            for (int i = 0; i < N; i++) {
                _oEntities[i] = _oWorld.NewEntity();
                _oEntities[i].Get<O.Position>() = new O.Position { X = i, Y = i };
                _oEntities[i].Get<O.Velocity>() = new O.Velocity { X = 1, Y = 1 };
            }

            // LeoEcsLite
            _lWorld = new Leopotam.EcsLite.EcsWorld();
            var lPosPool = _lWorld.GetPool<L.Position>();
            var lVelPool = _lWorld.GetPool<L.Velocity>();
            _lHealthPool = _lWorld.GetPool<L.Health>();
            _lEntities = new int[N];
            for (int i = 0; i < N; i++) {
                _lEntities[i] = _lWorld.NewEntity();
                ref var pos = ref lPosPool.Add(_lEntities[i]);
                pos.X = i; pos.Y = i;
                ref var vel = ref lVelPool.Add(_lEntities[i]);
                vel.X = 1; vel.Y = 1;
            }

            // Arch
            _aWorld = global::Arch.Core.World.Create();
            _aEntities = new global::Arch.Core.Entity[N];
            for (int i = 0; i < N; i++) {
                _aEntities[i] = _aWorld.Create(
                    new A.Position { X = i, Y = i },
                    new A.Velocity { X = 1, Y = 1 });
            }
        }

        [GlobalCleanup]
        public void Cleanup() {
            _kWorld?.Destroy();
            _oWorld?.Destroy();
            _lWorld?.Destroy();
            if (_aWorld != null) {
                global::Arch.Core.World.Destroy(_aWorld);
            }
        }

        [Benchmark(Baseline = true)]
        public void KenseiECS() {
            for (int i = 0; i < N; i++) {
                _kWorld.Add(_kEntities[i], new K.Health { Value = 100 });
            }
            for (int i = 0; i < N; i++) {
                _kWorld.Remove<K.Health>(_kEntities[i]);
            }
        }

        [Benchmark]
        public void LeoECS() {
            for (int i = 0; i < N; i++) {
                _oEntities[i].Get<O.Health>().Value = 100;
            }
            for (int i = 0; i < N; i++) {
                _oEntities[i].Del<O.Health>();
            }
        }

        [Benchmark]
        public void LeoEcsLite() {
            for (int i = 0; i < N; i++) {
                ref var h = ref _lHealthPool.Add(_lEntities[i]);
                h.Value = 100;
            }
            for (int i = 0; i < N; i++) {
                _lHealthPool.Del(_lEntities[i]);
            }
        }

        [Benchmark]
        public void ArchEcs() {
            for (int i = 0; i < N; i++) {
                _aWorld.Add(_aEntities[i], new A.Health { Value = 100 });
            }
            for (int i = 0; i < N; i++) {
                _aWorld.Remove<A.Health>(_aEntities[i]);
            }
        }
    }
}
