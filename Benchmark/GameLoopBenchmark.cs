using BenchmarkDotNet.Attributes;
using K = EcsBenchmark.Components.Kensei;
using L = EcsBenchmark.Components.LeoLite;
using A = EcsBenchmark.Components.ArchEcs;

namespace EcsBenchmark {
    /// <summary>
    /// Simulates a single game frame: multiple filter iterations +
    /// one-frame structural changes (add/remove event component).
    ///
    /// Realistic ratio: ~80% iteration, ~20% structural changes.
    /// In real games, filters are iterated every frame while structural
    /// changes happen occasionally — this amortizes per-change overhead.
    /// </summary>
    [MemoryDiagnoser]
    public class GameLoopBenchmark {
        [Params(1000, 10000)]
        public int N;

        private int _damageCount;

        // ---- KenseiECS ----
        private KenseiECS.World _kWorld = null!;
        private KenseiECS.Filter _kMoveFilter = null!;
        private KenseiECS.Filter _kHealthFilter = null!;
        private KenseiECS.Filter _kDamageFilter = null!;
        private KenseiECS.ComponentPool<K.Position> _kPos = null!;
        private KenseiECS.ComponentPool<K.Velocity> _kVel = null!;
        private KenseiECS.ComponentPool<K.Health> _kHealth = null!;
        private KenseiECS.ComponentPool<K.Damage> _kDamage = null!;
        private int[] _kEntityIndices = null!;

        // ---- LeoEcsLite ----
        private Leopotam.EcsLite.EcsWorld _lWorld = null!;
        private Leopotam.EcsLite.EcsFilter _lMoveFilter = null!;
        private Leopotam.EcsLite.EcsFilter _lHealthFilter = null!;
        private Leopotam.EcsLite.EcsFilter _lDamageFilter = null!;
        private Leopotam.EcsLite.EcsPool<L.Position> _lPos = null!;
        private Leopotam.EcsLite.EcsPool<L.Velocity> _lVel = null!;
        private Leopotam.EcsLite.EcsPool<L.Health> _lHealth = null!;
        private Leopotam.EcsLite.EcsPool<L.Damage> _lDamage = null!;
        private int[] _lEntities = null!;

        // ---- Arch ----
        private global::Arch.Core.World _aWorld = null!;
        private global::Arch.Core.QueryDescription _aMoveQuery;
        private global::Arch.Core.QueryDescription _aHealthQuery;
        private global::Arch.Core.QueryDescription _aDamageQuery;
        private global::Arch.Core.Entity[] _aEntities = null!;

        [GlobalSetup]
        public void Setup() {
            _damageCount = N / 10;

            // KenseiECS
            _kWorld = new KenseiECS.World();
            _kEntityIndices = new int[N];
            for (int i = 0; i < N; i++) {
                var e = _kWorld.CreateEntity(
                    new K.Position { X = i, Y = i },
                    new K.Velocity { X = 1, Y = 1 });
                _kWorld.Add(e, new K.Health { Value = 100 });
                _kEntityIndices[i] = e.Index;
            }
            _kMoveFilter = _kWorld.Filter().Inc<K.Position>().Inc<K.Velocity>().End();
            _kHealthFilter = _kWorld.Filter().Inc<K.Health>().End();
            _kDamageFilter = _kWorld.Filter().Inc<K.Health>().Inc<K.Damage>().End();
            _kPos = _kWorld.Pool<K.Position>();
            _kVel = _kWorld.Pool<K.Velocity>();
            _kHealth = _kWorld.Pool<K.Health>();
            _kDamage = _kWorld.Pool<K.Damage>();

            // LeoEcsLite
            _lWorld = new Leopotam.EcsLite.EcsWorld();
            _lPos = _lWorld.GetPool<L.Position>();
            _lVel = _lWorld.GetPool<L.Velocity>();
            _lHealth = _lWorld.GetPool<L.Health>();
            _lDamage = _lWorld.GetPool<L.Damage>();
            _lEntities = new int[N];
            for (int i = 0; i < N; i++) {
                _lEntities[i] = _lWorld.NewEntity();
                ref var pos = ref _lPos.Add(_lEntities[i]);
                pos.X = i; pos.Y = i;
                ref var vel = ref _lVel.Add(_lEntities[i]);
                vel.X = 1; vel.Y = 1;
                ref var h = ref _lHealth.Add(_lEntities[i]);
                h.Value = 100;
            }
            _lMoveFilter = _lWorld.Filter<L.Position>().Inc<L.Velocity>().End();
            _lHealthFilter = _lWorld.Filter<L.Health>().End();
            _lDamageFilter = _lWorld.Filter<L.Health>().Inc<L.Damage>().End();

            // Arch
            _aWorld = global::Arch.Core.World.Create();
            _aEntities = new global::Arch.Core.Entity[N];
            for (int i = 0; i < N; i++) {
                _aEntities[i] = _aWorld.Create(
                    new A.Position { X = i, Y = i },
                    new A.Velocity { X = 1, Y = 1 });
                _aWorld.Add(_aEntities[i], new A.Health { Value = 100 });
            }
            _aMoveQuery = new global::Arch.Core.QueryDescription()
                .WithAll<A.Position, A.Velocity>();
            _aHealthQuery = new global::Arch.Core.QueryDescription()
                .WithAll<A.Health>();
            _aDamageQuery = new global::Arch.Core.QueryDescription()
                .WithAll<A.Health, A.Damage>();
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
            // Movement system — iterate Pos+Vel
            foreach (int e in _kMoveFilter) {
                ref var pos = ref _kPos.Get(e);
                ref var vel = ref _kVel.Get(e);
                pos.X += vel.X;
                pos.Y += vel.Y;
            }

            // Health regen system — iterate Health
            foreach (int e in _kHealthFilter) {
                ref var h = ref _kHealth.Get(e);
                h.Value += 0.1f;
            }

            // Structural: apply damage events to 10% of entities
            for (int i = 0; i < _damageCount; i++) {
                _kDamage.Add(_kEntityIndices[i], new K.Damage());
            }

            // Damage system — iterate Health+Damage
            foreach (int e in _kDamageFilter) {
                ref var h = ref _kHealth.Get(e);
                h.Value -= 10f;
            }

            // Cleanup: remove one-frame damage events
            for (int i = _damageCount - 1; i >= 0; i--) {
                _kDamage.Remove(_kEntityIndices[i]);
            }
        }

        [Benchmark]
        public void LeoEcsLite() {
            // Movement system
            foreach (int e in _lMoveFilter) {
                ref var pos = ref _lPos.Get(e);
                ref var vel = ref _lVel.Get(e);
                pos.X += vel.X;
                pos.Y += vel.Y;
            }

            // Health regen system
            foreach (int e in _lHealthFilter) {
                ref var h = ref _lHealth.Get(e);
                h.Value += 0.1f;
            }

            // Structural: apply damage events to 10%
            for (int i = 0; i < _damageCount; i++) {
                _lDamage.Add(_lEntities[i]);
            }

            // Damage system
            foreach (int e in _lDamageFilter) {
                ref var h = ref _lHealth.Get(e);
                h.Value -= 10f;
            }

            // Cleanup: remove one-frame damage events
            for (int i = _damageCount - 1; i >= 0; i--) {
                _lDamage.Del(_lEntities[i]);
            }
        }

        [Benchmark]
        public void ArchEcs() {
            // Movement system
            _aWorld.Query(in _aMoveQuery, (ref A.Position pos, ref A.Velocity vel) => {
                pos.X += vel.X;
                pos.Y += vel.Y;
            });

            // Health regen system
            _aWorld.Query(in _aHealthQuery, (ref A.Health h) => {
                h.Value += 0.1f;
            });

            // Structural: apply damage events to 10%
            for (int i = 0; i < _damageCount; i++) {
                _aWorld.Add(_aEntities[i], new A.Damage());
            }

            // Damage system
            _aWorld.Query(in _aDamageQuery, (ref A.Health h, ref A.Damage d) => {
                h.Value -= 10f;
            });

            // Cleanup: remove one-frame damage events
            for (int i = 0; i < _damageCount; i++) {
                _aWorld.Remove<A.Damage>(_aEntities[i]);
            }
        }
    }
}
