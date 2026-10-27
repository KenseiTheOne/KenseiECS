using System;
using NUnit.Framework;

namespace KenseiECS.Tests {
    // 2.3.0, merged design: the tests carried over from design A (adapted to B's API) and the judge's
    // additions (the slot cap of a reserve taken from the types, the EcsBootstrap order).

    public struct RmPosition : IComponent { public float X, Y; }
    public struct RmVelocity : IComponent { public float X, Y; }
    public struct RmProjectile : IComponent { public float Speed; }
    public struct RmStunned : IComponent { public float Left; }
    public struct RmSingle : IComponent { public int V; }
    public struct RmCold : IComponent { public int V; }
    public struct RmAnyA : IComponent { }
    public struct RmAnyB : IComponent { }
    public struct RmAnyC : IComponent { }
    public struct RmPad<T> : IComponent { public int V; }

    [TestFixture]
    public class V230ReserveMergeTests {
        private static long Bytes(Action action) {
            long before = GC.GetAllocatedBytesForCurrentThread();
            action();
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        // From A: ReserveEntities allocates the world's own slot arrays and the free-slot list, nothing else.
        [Test]
        public void ReserveEntities_AllocatesOnlyTheWorldSlotArrays() {
            var world = new World();
            world.Pool<RmPad<byte>>();
            world.Pool<RmPad<short>>();
            world.Pool<RmPad<int>>();
            world.Pool<RmPad<long>>();
            world.Filter().Inc<RmPad<byte>>().End();
            world.Filter().Inc<RmPad<short>>().Exc<RmPad<int>>().End();
            const int n = 20_000;

            long allocated = Bytes(() => world.ReserveEntities(n));

            long perSlot = sizeof(int) + sizeof(bool) + sizeof(int) + (long)world._componentMasks.Length * sizeof(ulong)
                + (world._usedWords != null ? sizeof(ulong) : 0);
            long expected = perSlot * n + (long)n * sizeof(int);
            Assert.That(world._generations.Length, Is.EqualTo(n), "exact size");
            Assert.That(allocated, Is.InRange(expected, expected + 4096), "only the world's storage");
        }

        // From A: the attribute is the migration message.
        [Test]
        public void WorldReserve_IsObsoleteAsAWarning_TheNewCallsAreNot() {
            var reserve = typeof(World).GetMethod(nameof(World.Reserve), new[] { typeof(int), typeof(int) });
            var attribute = (ObsoleteAttribute)Attribute.GetCustomAttribute(reserve, typeof(ObsoleteAttribute));

            Assert.That(attribute, Is.Not.Null);
            Assert.That(attribute.IsError, Is.False, "2.2 code keeps compiling");
            Assert.That(attribute.Message, Does.Contain("ReserveEntities(entities)").And.Contain("Pool<T>().Reserve(count)").And.Contain("3.0"));
            Assert.That(Attribute.IsDefined(typeof(World).GetMethod(nameof(World.ReserveEntities)), typeof(ObsoleteAttribute)), Is.False);
            Assert.That(Attribute.IsDefined(typeof(ComponentPoolBase).GetMethod(nameof(ComponentPoolBase.Reserve)), typeof(ObsoleteAttribute)), Is.False);
            Assert.That(Attribute.IsDefined(typeof(Filter).GetMethod(nameof(Filter.Reserve)), typeof(ObsoleteAttribute)), Is.False);
        }

        // From A: an entity on the highest reserved index enters a reserved pool and its filter without allocating.
        [Test]
        public void HighestReservedIndex_EntersReservedPoolAndFilter_WithoutAllocating() {
            var world = new World();
            var positions = world.Pool<RmPosition>();
            var filter = world.Filter().Inc<RmPosition>().End();
            positions.Reserve(100);
            world.ReserveEntities(9000);

            for (int i = 0; i < 8999; i++) {
                world.CreateEntity(new RmCold());
            }
            var last = world.CreateEntity(new RmCold());
            Assert.That(last.Index, Is.EqualTo(8999));
            world.Add(world.GetEntity(0), new RmPosition());
            world.Remove<RmPosition>(world.GetEntity(0));

            Assert.That(Bytes(() => world.Add(last, new RmPosition())), Is.Zero);
            Assert.That(filter.Contains(last.Index), Is.True);
            Assert.That(filter.ReservedCount, Is.EqualTo(100));
        }

        // Judge: a reserve taken from the types never exceeds the reserved slots, and follows them up.
        [Test]
        public void DerivedReserve_IsCappedAtReservedEntities_AndRisesWithThem() {
            var world = new World();
            world.ReserveEntities(1500);
            var any = world.Filter().Any<RmAnyA>().Any<RmAnyB>().Any<RmAnyC>().End();
            var inc = world.Filter().Inc<RmPosition>().End();
            world.Pool<RmAnyA>().Reserve(1000);
            world.Pool<RmAnyB>().Reserve(1000);
            world.Pool<RmAnyC>().Reserve(1000);
            world.Pool<RmPosition>().Reserve(5000);

            Assert.That(any.ReservedCount, Is.EqualTo(1500), "the sum of the Any reserves is 3000, the slots are 1500");
            Assert.That(inc.ReservedCount, Is.EqualTo(1500), "a pool reserved for more than the slots does not raise its filters past them");
            Assert.That(any.DenseCapacity, Is.EqualTo(1500));

            world.ReserveEntities(2500);

            Assert.That(any.ReservedCount, Is.EqualTo(2500), "a later ReserveEntities raises the cap");
            Assert.That(any.DenseCapacity, Is.EqualTo(2500));
            Assert.That(inc.ReservedCount, Is.EqualTo(2500));
        }

        // Judge: without reserved slots there is no cap (2.2-style pool reserves).
        [Test]
        public void DerivedReserve_WithoutReservedSlots_IsNotCapped() {
            var world = new World();
            var any = world.Filter().Any<RmAnyA>().Any<RmAnyB>().End();
            world.Pool<RmAnyA>().Reserve(300);
            world.Pool<RmAnyB>().Reserve(200);

            Assert.That(any.ReservedCount, Is.EqualTo(500));
        }

        private sealed class FilterSystem : IInitSystem {
            public Filter Moving, Shots, Stunned, Singles;
            public void Init(World world, SharedData shared) {
                Moving = world.Filter().Inc<RmPosition>().Inc<RmVelocity>().End();
                Shots = world.Filter().Inc<RmProjectile>().Inc<RmPosition>().End();
                Stunned = world.Filter().Inc<RmStunned>().Inc<RmPosition>().Exc<RmProjectile>().End();
                Singles = world.Filter().Inc<RmSingle>().End();
            }
        }

        // Judge: the EcsBootstrap order. Configure reserves the world and the hot types, the systems create
        // their filters in Init afterwards, the battle allocates nothing.
        [Test]
        public void BootstrapOrder_ReserveThenSystemsInit_BattleAllocatesNothing() {
#if KENSEI_DEBUG
            Assume.That(EcsProfiler.IsEnabled, Is.False, "the profiler must be off for this test");
#endif
            const int peak = 6000, shots = 500, stuns = 64;
            var world = new World();
            // Configure
            world.ReserveEntities(peak);
            world.Pool<RmPosition>().Reserve(peak);
            world.Pool<RmVelocity>().Reserve(peak);
            world.Pool<RmProjectile>().Reserve(shots);
            world.Pool<RmStunned>().Reserve(stuns);
            world.CreateEntity(new RmSingle());
            var system = new FilterSystem();
            var runner = new SystemsRunner(world).Add(system);
            // Start
            runner.Init();
            Assert.That(system.Moving.ReservedCount, Is.EqualTo(peak - 0));
            Assert.That(system.Shots.ReservedCount, Is.EqualTo(shots));
            Assert.That(system.Stunned.ReservedCount, Is.EqualTo(stuns));
            Assert.That(system.Singles.ReservedCount, Is.Zero, "a singleton type is not reserved");

            var warm = world.CreateEntity(new RmPosition());
            world.Add(warm, new RmVelocity());
            world.Add(warm, new RmProjectile());
            world.Add(warm, new RmStunned());
            world.DestroyEntity(warm);

            var live = new Entity[peak - 1];
            long allocated = Bytes(() => {
                for (int round = 0; round < 2; round++) {
                    for (int i = 0; i < live.Length; i++) {
                        var e = world.CreateEntity(new RmPosition());
                        world.Add(e, new RmVelocity());
                        if (i % 13 == 0 && i / 13 < shots) {
                            world.Add(e, new RmProjectile());
                        }
                        live[i] = e;
                    }
                    int stride = live.Length / stuns;
                    for (int k = 0; k < stuns; k++) {
                        world.Add(live[k * stride + round], new RmStunned());
                    }
                    for (int k = 0; k < stuns; k++) {
                        world.Remove<RmStunned>(live[k * stride + round]);
                    }
                    for (int i = live.Length - 1; i >= 0; i--) {
                        world.DestroyEntity(live[i]);
                    }
                }
            });

            Assert.That(allocated, Is.Zero);
            Assert.That(world.GrowthsAfterReserve, Is.EqualTo(0));
            Assert.That(world.EntityCount, Is.EqualTo(1));
        }

        // From A: the documented pattern (docs/snippets/Guides/WorldConfig.cs#reserve), run.
        [Test]
        public void DocsPattern_Reserve_PeakAllocatesNothing_ColdPoolsStaySmall() {
            const int maxUnits = 3000, maxProjectiles = 400;
            var world = new World();
            int maxEntities = maxUnits + maxProjectiles;
            world.ReserveEntities(maxEntities);
            world.Pool<RmPosition>().Reserve(maxEntities);
            world.Pool<RmVelocity>().Reserve(maxEntities);
            world.Pool<RmProjectile>().Reserve(maxProjectiles);
            var single = world.Pool<RmSingle>();
            world.CreateEntity(new RmSingle());
            var flying = world.Filter<Inc<RmProjectile, RmPosition>>();
            var warm = world.CreateEntity(new RmPosition());
            world.Add(warm, new RmVelocity());
            world.Add(warm, new RmProjectile());
            world.DestroyEntity(warm);

            long allocated = Bytes(() => {
                for (int i = 0; i < maxEntities - 1; i++) {
                    var e = world.CreateEntity(new RmPosition());
                    world.Add(e, new RmVelocity());
                    if (i < maxProjectiles) {
                        world.Add(e, new RmProjectile());
                    }
                }
            });

            Assert.That(allocated, Is.Zero);
            Assert.That(flying.Count, Is.EqualTo(maxProjectiles));
            Assert.That(flying.ReservedCount, Is.EqualTo(maxProjectiles));
            Assert.That(single.SparseCapacity, Is.EqualTo(WorldConfig.Default().InitialPoolSparseCapacity));
            Assert.That(world.GrowthsAfterReserve, Is.EqualTo(0));
        }
    }
}
