using BenchmarkDotNet.Attributes;
using Leopotam.Ecs;
using K = EcsBenchmark.Components.Kensei;
using L = EcsBenchmark.Components.LeoLite;
using A = EcsBenchmark.Components.ArchEcs;
using O = EcsBenchmark.Components.Leo;

namespace EcsBenchmark {
    [MemoryDiagnoser]
    public class IterationBenchmark {
        [Params(100, 1000, 10000)]
        public int N;

        // ---- KenseiECS ----
        private KenseiECS.World _kWorld = null!;
        private KenseiECS.Filter _kFilter = null!;
        private KenseiECS.ComponentPool<K.Position> _kPos = null!;
        private KenseiECS.ComponentPool<K.Velocity> _kVel = null!;

        // ---- LeoECS ----
        private EcsWorld _oWorld = null!;
        private EcsSystems _oSystems = null!;
        private LeoIterateSystem _oSystem = null!;

        // ---- LeoEcsLite ----
        private Leopotam.EcsLite.EcsWorld _lWorld = null!;
        private Leopotam.EcsLite.EcsFilter _lFilter = null!;
        private Leopotam.EcsLite.EcsPool<L.Position> _lPos = null!;
        private Leopotam.EcsLite.EcsPool<L.Velocity> _lVel = null!;

        // ---- Arch ----
        private global::Arch.Core.World _aWorld = null!;
        private global::Arch.Core.QueryDescription _aQuery;

        [GlobalSetup]
        public void Setup() {
            // KenseiECS
            _kWorld = new KenseiECS.World();
            for (int i = 0; i < N; i++) {
                var e = _kWorld.CreateEntity(new K.Position { X = i, Y = i });
                _kWorld.Add(e, new K.Velocity { X = 1, Y = 1 });
            }
            _kFilter = _kWorld.Filter().Inc<K.Position>().Inc<K.Velocity>().End();
            _kPos = _kWorld.Pool<K.Position>();
            _kVel = _kWorld.Pool<K.Velocity>();

            // LeoECS — filters are injected through systems
            _oWorld = new EcsWorld();
            for (int i = 0; i < N; i++) {
                var e = _oWorld.NewEntity();
                e.Get<O.Position>() = new O.Position { X = i, Y = i };
                e.Get<O.Velocity>() = new O.Velocity { X = 1, Y = 1 };
            }
            _oSystem = new LeoIterateSystem();
            _oSystems = new EcsSystems(_oWorld).Add(_oSystem);
            _oSystems.Init();

            // LeoEcsLite
            _lWorld = new Leopotam.EcsLite.EcsWorld();
            var lPosPool = _lWorld.GetPool<L.Position>();
            var lVelPool = _lWorld.GetPool<L.Velocity>();
            for (int i = 0; i < N; i++) {
                int e = _lWorld.NewEntity();
                ref var pos = ref lPosPool.Add(e);
                pos.X = i; pos.Y = i;
                ref var vel = ref lVelPool.Add(e);
                vel.X = 1; vel.Y = 1;
            }
            _lFilter = _lWorld.Filter<L.Position>().Inc<L.Velocity>().End();
            _lPos = lPosPool;
            _lVel = lVelPool;

            // Arch
            _aWorld = global::Arch.Core.World.Create();
            for (int i = 0; i < N; i++) {
                _aWorld.Create(
                    new A.Position { X = i, Y = i },
                    new A.Velocity { X = 1, Y = 1 });
            }
            _aQuery = new global::Arch.Core.QueryDescription().WithAll<A.Position, A.Velocity>();
        }

        [GlobalCleanup]
        public void Cleanup() {
            _kWorld?.Destroy();
            _oSystems?.Destroy();
            _oWorld?.Destroy();
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
        public void LeoECS() {
            _oSystems.Run();
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

    // LeoECS requires a system class for filter injection
    internal class LeoIterateSystem : IEcsRunSystem {
        private EcsFilter<O.Position, O.Velocity> _filter = null!;

        public void Run() {
            foreach (int i in _filter) {
                ref var pos = ref _filter.Get1(i);
                ref var vel = ref _filter.Get2(i);
                pos.X += vel.X;
                pos.Y += vel.Y;
            }
        }
    }
}
