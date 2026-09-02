using BenchmarkDotNet.Attributes;
using K = EcsBenchmark.Components.Kensei;

namespace EcsBenchmark {
    /// <summary>
    /// Bytes a world holds after Types component types were each added to at
    /// least one high-index entity in a world of N entities, plus Filters
    /// filters. The Allocated column is the footprint; time is irrelevant.
    /// Every pool's sparse array grows to the highest entity index that ever
    /// had the component, so the cost model is O(Types x N).
    /// </summary>
    [MemoryDiagnoser]
    [SimpleJob(warmupCount: 1, iterationCount: 3, invocationCount: 1)]
    public class MemoryFootprintBenchmark {
        [Params(10000)]
        public int N;

        [Params(64, 1024)]
        public int Types;

        [Params(0, 100)]
        public int Filters;

        private static readonly Type[] _types = TypePadding.KenseiTypes(1024);

        [Benchmark]
        public KenseiECS.World BuildWorld() {
            var world = new KenseiECS.World();
            KenseiECS.Entity last = default;
            for (int i = 0; i < N; i++) {
                last = world.CreateEntity(new K.Position { X = i, Y = i });
            }

            for (int t = 0; t < Types; t++) {
                TypePadding.AddKenseiDefault(world, last, _types[t]);
            }

            var inc = typeof(KenseiECS.FilterBuilder).GetMethod(nameof(KenseiECS.FilterBuilder.Inc))!;
            for (int f = 0; f < Filters; f++) {
                var builder = world.Filter().Inc<K.Position>();
                inc.MakeGenericMethod(_types[f % Types]).Invoke(builder, null);
                builder.End();
            }

            return world;
        }
    }
}
