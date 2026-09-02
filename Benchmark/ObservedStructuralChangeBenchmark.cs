using BenchmarkDotNet.Attributes;
using K = EcsBenchmark.Components.Kensei;
using L = EcsBenchmark.Components.LeoLite;

namespace EcsBenchmark {
    /// <summary>
    /// Add + remove of a component that F filters constrain (Inc with two
    /// other types, or Exc). StructuralChangeBenchmark has no filter on the
    /// changed type, which hides the cost model of reactive filters:
    /// KenseiECS tests one mask word per filter, LeoEcsLite walks a Has chain
    /// per included type.
    /// </summary>
    [MemoryDiagnoser]
    public class ObservedStructuralChangeBenchmark {
        [Params(10000)]
        public int N;

        [Params(1, 8, 32)]
        public int Filters;

        private struct Extra<T> : KenseiECS.IComponent { }
        private struct LiteExtra<T> { }

        private KenseiECS.World _kWorld = null!;
        private KenseiECS.Entity[] _kEntities = null!;

        private Leopotam.EcsLite.EcsWorld _lWorld = null!;
        private Leopotam.EcsLite.EcsPool<L.Health> _lHealth = null!;
        private int[] _lEntities = null!;

        [GlobalSetup]
        public void Setup() {
            _kWorld = new KenseiECS.World();
            _kEntities = new KenseiECS.Entity[N];
            for (int i = 0; i < N; i++) {
                _kEntities[i] = _kWorld.CreateEntity(new K.Position { X = i, Y = i });
                _kWorld.Add(_kEntities[i], new K.Velocity { X = 1, Y = 1 });
            }
            RegisterKenseiFilters(typeof(Extra<int>), Filters);

            _lWorld = new Leopotam.EcsLite.EcsWorld();
            var lPos = _lWorld.GetPool<L.Position>();
            var lVel = _lWorld.GetPool<L.Velocity>();
            _lHealth = _lWorld.GetPool<L.Health>();
            _lEntities = new int[N];
            for (int i = 0; i < N; i++) {
                _lEntities[i] = _lWorld.NewEntity();
                lPos.Add(_lEntities[i]);
                lVel.Add(_lEntities[i]);
            }
            RegisterLiteFilters(typeof(LiteExtra<int>), Filters);
        }

        // Each filter gets a distinct extra type so they do not deduplicate:
        // half of them Inc<Health, Position, Extra>, the other half Inc<Position> Exc<Health, Extra>.
        private void RegisterKenseiFilters(Type extra, int count) {
            var inc = typeof(global::KenseiECS.FilterBuilder).GetMethod("Inc")!;
            var exc = typeof(global::KenseiECS.FilterBuilder).GetMethod("Exc")!;
            for (int i = 0; i < count; i++) {
                extra = typeof(Extra<>).MakeGenericType(extra);
                var builder = _kWorld.Filter().Inc<K.Position>();
                if (i % 2 == 0) {
                    builder.Inc<K.Health>();
                    inc.MakeGenericMethod(extra).Invoke(builder, null);
                } else {
                    builder.Exc<K.Health>();
                    exc.MakeGenericMethod(extra).Invoke(builder, null);
                }
                builder.End();
            }
        }

        private void RegisterLiteFilters(Type extra, int count) {
            var inc = typeof(Leopotam.EcsLite.EcsWorld.Mask).GetMethod(nameof(Leopotam.EcsLite.EcsWorld.Mask.Inc))!;
            var exc = typeof(Leopotam.EcsLite.EcsWorld.Mask).GetMethod(nameof(Leopotam.EcsLite.EcsWorld.Mask.Exc))!;
            for (int i = 0; i < count; i++) {
                extra = typeof(LiteExtra<>).MakeGenericType(extra);
                var mask = _lWorld.Filter<L.Position>();
                if (i % 2 == 0) {
                    mask = mask.Inc<L.Health>();
                    mask = (Leopotam.EcsLite.EcsWorld.Mask)inc.MakeGenericMethod(extra).Invoke(mask, null)!;
                } else {
                    mask = mask.Exc<L.Health>();
                    mask = (Leopotam.EcsLite.EcsWorld.Mask)exc.MakeGenericMethod(extra).Invoke(mask, null)!;
                }
                mask.End();
            }
        }

        [GlobalCleanup]
        public void Cleanup() {
            _kWorld?.Destroy();
            _lWorld?.Destroy();
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
        public void LeoEcsLite() {
            for (int i = 0; i < N; i++) {
                _lHealth.Add(_lEntities[i]).Value = 100;
            }
            for (int i = 0; i < N; i++) {
                _lHealth.Del(_lEntities[i]);
            }
        }
    }
}
