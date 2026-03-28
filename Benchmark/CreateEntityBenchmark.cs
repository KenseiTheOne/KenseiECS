using BenchmarkDotNet.Attributes;
using Leopotam.Ecs;
using K = EcsBenchmark.Components.Kensei;
using L = EcsBenchmark.Components.LeoLite;
using A = EcsBenchmark.Components.ArchEcs;
using O = EcsBenchmark.Components.Leo;

namespace EcsBenchmark {
    [MemoryDiagnoser]
    public class CreateEntityBenchmark {
        [Params(100, 1000, 10000)]
        public int N;

        [Benchmark(Baseline = true)]
        public void KenseiECS() {
            var world = new KenseiECS.World();
            for (int i = 0; i < N; i++) {
                world.CreateEntity(
                    new K.Position { X = i, Y = i },
                    new K.Velocity { X = 1, Y = 1 });
            }
            world.Destroy();
        }

        [Benchmark]
        public void LeoECS() {
            var world = new EcsWorld();
            for (int i = 0; i < N; i++) {
                var entity = world.NewEntity();
                ref var pos = ref entity.Get<O.Position>();
                pos.X = i; pos.Y = i;
                ref var vel = ref entity.Get<O.Velocity>();
                vel.X = 1; vel.Y = 1;
            }
            world.Destroy();
        }

        [Benchmark]
        public void LeoEcsLite() {
            var world = new Leopotam.EcsLite.EcsWorld();
            var posPool = world.GetPool<L.Position>();
            var velPool = world.GetPool<L.Velocity>();
            for (int i = 0; i < N; i++) {
                int e = world.NewEntity();
                ref var pos = ref posPool.Add(e);
                pos.X = i; pos.Y = i;
                ref var vel = ref velPool.Add(e);
                vel.X = 1; vel.Y = 1;
            }
            world.Destroy();
        }

        [Benchmark]
        public void ArchEcs() {
            var world = global::Arch.Core.World.Create();
            for (int i = 0; i < N; i++) {
                world.Create(
                    new A.Position { X = i, Y = i },
                    new A.Velocity { X = 1, Y = 1 });
            }
            global::Arch.Core.World.Destroy(world);
        }
    }
}
