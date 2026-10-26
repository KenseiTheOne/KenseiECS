using System;
using System.Reflection;
using NUnit.Framework;

namespace KenseiECS.Tests {
    // 2.3.0: World.ReserveEntities reserves the world alone, ComponentPoolBase.Reserve reserves a hot
    // type (its sparse side follows the reserved entity slots), filters over reserved types are reserved
    // by them, and the obsolete World.Reserve keeps its 2.2 behaviour.

    public struct RsHot : IComponent { public int V; }
    public struct RsHot2 : IComponent { public float X, Y; }
    public struct RsRare : IComponent { public int V; }
    public struct RsCold : IComponent { public int V; }
    public struct RsSingleton : IComponent { public long V; }
    public struct RsAnyA : IComponent { }
    public struct RsAnyB : IComponent { }
    public struct RsFailed : IComponent { public int V; }
    public struct RsLegacyLate : IComponent { public int V; }
    public struct RsUnwatched : IComponent { public int V; }
    public struct RsWatchedDense : IComponent { public int V; }

    [TestFixture]
    public class V230ReserveTests {
        private static WorldConfig Small() =>
            new WorldConfig { InitialEntityCapacity = 16, InitialPoolSparseCapacity = 16, InitialPoolDenseCapacity = 4 };

        // =================================================================
        // World.ReserveEntities
        // =================================================================

        [Test]
        public void ReserveEntities_GrowsTheWorldOnly() {
            var world = new World(Small());
            var hot = world.Pool<RsHot>();
            var single = world.Pool<RsSingleton>();
            var filter = world.Filter().Inc<RsHot>().End();
            long hotBytes = hot.AllocatedBytes;
            long singleBytes = single.AllocatedBytes;
            long filterBytes = filter.AllocatedBytes;

            world.ReserveEntities(5000);

            Assert.That(world.ReservedEntities, Is.EqualTo(5000));
            Assert.That(world._generations.Length, Is.EqualTo(5000), "the slots are sized exactly");
            Assert.That(hot.AllocatedBytes, Is.EqualTo(hotBytes), "an unreserved pool is not touched");
            Assert.That(single.AllocatedBytes, Is.EqualTo(singleBytes));
            Assert.That(filter.AllocatedBytes, Is.EqualTo(filterBytes), "an unreserved filter is not touched");
            Assert.That(world.Pool<RsCold>().SparseCapacity, Is.EqualTo(16), "a pool created later is not sized for the slots");
        }

        [Test]
        public void PoolReserve_CoversReservedSlots_InEitherOrder() {
            var world = new World(Small());
            world.ReserveEntities(3000);
            var before = world.Pool<RsHot>();
            before.Reserve(100);

            Assert.That(before.ReservedComponents, Is.EqualTo(100));
            Assert.That(before.DenseCapacity, Is.EqualTo(100), "dense is sized exactly");
            Assert.That(before.SparseCapacity, Is.EqualTo(3000), "sparse covers the reserved slots");

            var after = world.Pool<RsHot2>();
            after.Reserve(50);
            world.ReserveEntities(8000);

            Assert.That(before.SparseCapacity, Is.EqualTo(8000), "a reserved pool follows a later ReserveEntities");
            Assert.That(after.SparseCapacity, Is.EqualTo(8000));
            Assert.That(after.DenseCapacity, Is.EqualTo(50), "the dense side does not follow the slots");
            Assert.That(world.Pool<RsCold>().SparseCapacity, Is.EqualTo(16), "an unreserved pool does not");
        }

        [Test]
        public void PoolReserve_WithoutReservedSlots_CoversItsOwnCount() {
            var world = new World(Small());
            var pool = world.Pool<RsHot>();

            pool.Reserve(1000);

            Assert.That(pool.SparseCapacity, Is.EqualTo(1000), "as in 2.2: indices below the count");
            Assert.That(pool.DenseCapacity, Is.EqualTo(1000));
        }

        // =================================================================
        // The contract: the world and the hot types, nothing else
        // =================================================================

        [Test]
        public void HotTypes_PeakCreateAndDestroy_AllocatesNothing_ColdPoolsUntouched() {
#if KENSEI_DEBUG
            Assume.That(EcsProfiler.IsEnabled, Is.False, "the profiler must be off for this test");
#endif
            const int peak = 4000;
            const int rare = 300;
            const int rareStep = 14; // every 14th of the 3999 entities: 286 components, within the reserve
            var world = new World(Small());
            // Load: the world, the types whose count changes during play, the singletons as they are.
            world.ReserveEntities(peak);
            world.Pool<RsHot>().Reserve(peak);
            world.Pool<RsHot2>().Reserve(peak);
            world.Pool<RsRare>().Reserve(rare);
            var single = world.Pool<RsSingleton>();
            world.CreateEntity(new RsSingleton { V = 1 });
            long singleBytes = single.AllocatedBytes;

            // Systems' Init, after the reserve: their filters are reserved by their types.
            var moving = world.Filter().Inc<RsHot>().Inc<RsHot2>().End();
            var rares = world.Filter().Inc<RsRare>().Inc<RsHot>().Exc<RsSingleton>().End();
            Assert.That(moving.ReservedCount, Is.EqualTo(peak));
            Assert.That(rares.ReservedCount, Is.EqualTo(rare), "the smallest reserve of its Inc types");

            var created = new Entity[peak - 1];
            created[0] = world.CreateEntity(new RsHot());
            world.Add(created[0], new RsHot2());
            world.Add(created[0], new RsRare());
            world.DestroyEntity(created[0]);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int round = 0; round < 2; round++) {
                for (int i = 0; i < created.Length; i++) {
                    var e = world.CreateEntity(new RsHot { V = i });
                    world.Add(e, new RsHot2());
                    if (i % rareStep == 0) {
                        world.Add(e, new RsRare());
                    }
                    created[i] = e;
                }
                for (int i = created.Length - 1; i >= 0; i--) {
                    world.DestroyEntity(created[i]);
                }
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.That(allocated, Is.Zero, "the reserved world, pools and filters cover the peak");
            Assert.That(single.AllocatedBytes, Is.EqualTo(singleBytes), "a singleton pool keeps its small arrays");
            Assert.That(world.GrowthsAfterReserve, Is.EqualTo(0));
            Assert.That(world.EntityCount, Is.EqualTo(1));
        }

        [Test]
        public void UnreservedPool_GrowsOnDemand_AndIsNotCounted() {
            var world = new World(Small());
            world.ReserveEntities(2000);
            world.Pool<RsHot>().Reserve(2000);
            var cold = world.Pool<RsCold>();
            Entity last = default;
            for (int i = 0; i < 1500; i++) {
                last = world.CreateEntity(new RsHot());
            }

            world.Add(last, new RsCold());

            Assert.That(cold.SparseCapacity, Is.GreaterThan(last.Index), "an unreserved pool grows to the index it sees");
            Assert.That(cold.ReservedComponents, Is.EqualTo(0));
            Assert.That(world.GrowthsAfterReserve, Is.EqualTo(0), "growth of an unreserved pool is not a reserve that was too small");
        }

        // =================================================================
        // Filters reserved by their types
        // =================================================================

        [Test]
        public void Filter_ReservedOnlyWhenEveryIncTypeIs() {
            var world = new World(Small());
            world.ReserveEntities(5000);
            var filter = world.Filter().Inc<RsHot>().Inc<RsCold>().End();
            world.Pool<RsHot>().Reserve(5000);

            Assert.That(filter.ReservedCount, Is.EqualTo(0), "RsCold is not reserved: entering the filter may grow RsCold anyway");
            Assert.That(filter.DenseCapacity, Is.EqualTo(4));

            world.Pool<RsCold>().Reserve(20);

            Assert.That(filter.ReservedCount, Is.EqualTo(20), "the smallest Inc reserve bounds it");
            Assert.That(filter.DenseCapacity, Is.EqualTo(20));
            Assert.That(filter.AllocatedBytes, Is.GreaterThanOrEqualTo(5000L * sizeof(int)), "its sparse pages cover the reserved slots");
        }

        [Test]
        public void Filter_AnyTypes_BoundBySumOfTheirReserves() {
            var world = new World(Small());
            var anyOnly = world.Filter().Any<RsAnyA>().Any<RsAnyB>().End();
            var incAndAny = world.Filter().Inc<RsHot>().Any<RsAnyA>().Any<RsAnyB>().End();
            var incOnlyBound = world.Filter().Inc<RsHot2>().Any<RsAnyA>().Any<RsCold>().End();

            world.Pool<RsAnyA>().Reserve(30);
            Assert.That(anyOnly.ReservedCount, Is.EqualTo(0), "RsAnyB is not reserved yet");

            world.Pool<RsAnyB>().Reserve(12);
            Assert.That(anyOnly.ReservedCount, Is.EqualTo(42), "an entity has at least one Any type");
            Assert.That(incAndAny.ReservedCount, Is.EqualTo(0), "its Inc type is not reserved");

            world.Pool<RsHot>().Reserve(1000);
            Assert.That(incAndAny.ReservedCount, Is.EqualTo(42), "the Any sum tightens the Inc bound");

            world.Pool<RsHot2>().Reserve(500);
            Assert.That(incOnlyBound.ReservedCount, Is.EqualTo(500), "with an Any type not reserved, the Inc types alone bound it");
        }

        [Test]
        public void Filter_CreatedAfterItsTypes_StartsReserved_AndPopulatesWithoutGrowth() {
            var world = new World(Small());
            world.ReserveEntities(700);
            world.Pool<RsHot>().Reserve(700);
            for (int i = 0; i < 600; i++) {
                world.CreateEntity(new RsHot());
            }

            var filter = world.Filter().Inc<RsHot>().End();

            Assert.That(filter.Count, Is.EqualTo(600));
            Assert.That(filter.ReservedCount, Is.EqualTo(700));
            Assert.That(filter.DenseCapacity, Is.EqualTo(700), "sized before it was populated, so population did not double it");
            Assert.That(world.GrowthsAfterReserve, Is.EqualTo(0));
        }

        [Test]
        public void FilterReserve_RaisesAboveItsTypes_AndCoversSlots() {
            var world = new World(Small());
            world.ReserveEntities(3000);
            var filter = world.Filter().Inc<RsHot>().Inc<RsCold>().End();

            filter.Reserve(64);

            Assert.That(filter.ReservedCount, Is.EqualTo(64), "a filter over an unreserved type can be reserved by hand");
            Assert.That(filter.DenseCapacity, Is.EqualTo(64));
            long bytes = filter.AllocatedBytes;
            Assert.That(bytes, Is.GreaterThanOrEqualTo(3000L * sizeof(int)), "its pages cover the reserved slots");

            world.Pool<RsHot>().Reserve(10);
            world.Pool<RsCold>().Reserve(10);
            Assert.That(filter.ReservedCount, Is.EqualTo(64), "the larger of the two reserves");
            Assert.That(filter.AllocatedBytes, Is.EqualTo(bytes), "a smaller bound from the types grows nothing");

            world.ReserveEntities(6000);
            Assert.That(filter.AllocatedBytes, Is.GreaterThanOrEqualTo(6000L * sizeof(int)), "a reserved filter follows ReserveEntities");
        }

        // =================================================================
        // Arguments, destroyed worlds, failed allocations
        // =================================================================

        [Test]
        public void Reserves_NeverShrink_ZeroDoesNothing_NegativeThrows() {
            var world = new World(new WorldConfig { InitialEntityCapacity = 1024, InitialPoolSparseCapacity = 1024, InitialPoolDenseCapacity = 512 });
            var pool = world.Pool<RsHot>();
            var filter = world.Filter().Inc<RsCold>().End();

            world.ReserveEntities(10);
            pool.Reserve(10);
            filter.Reserve(10);
            pool.Reserve(0);
            filter.Reserve(0);
            world.ReserveEntities(0);

            Assert.That(world._generations.Length, Is.EqualTo(1024));
            Assert.That(pool.SparseCapacity, Is.EqualTo(1024));
            Assert.That(pool.DenseCapacity, Is.EqualTo(512));
            Assert.That(pool.ReservedComponents, Is.EqualTo(10));
            Assert.That(filter.DenseCapacity, Is.EqualTo(512));
            Assert.That(world.ReservedEntities, Is.EqualTo(10));
            Assert.Throws<ArgumentOutOfRangeException>(() => world.ReserveEntities(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => pool.Reserve(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => filter.Reserve(-1));
        }

        [Test]
        public void Reserves_AfterDestroy_ThrowObjectDisposed() {
            var world = new World();
            var pool = world.Pool<RsHot>();
            var filter = world.Filter().Inc<RsHot>().End();
            world.Destroy();

            Assert.That(Assert.Throws<ObjectDisposedException>(() => world.ReserveEntities(10)).Message, Does.Contain("ReserveEntities"));
            Assert.That(Assert.Throws<ObjectDisposedException>(() => pool.Reserve(10)).Message, Does.Contain("Reserve"));
            Assert.That(Assert.Throws<ObjectDisposedException>(() => filter.Reserve(10)).Message, Does.Contain("Reserve"));
        }

        [Test]
        public void ReserveEntities_AllocationFails_WorldStaysUsable_AndReservesNothing() {
            var world = new World();
            var e = world.CreateEntity(new RsHot { V = 1 });

            Assert.Throws<OutOfMemoryException>(() => world.ReserveEntities(int.MaxValue));

            Assert.That(world.ReservedEntities, Is.EqualTo(0), "raised only after everything was allocated");
            world.Pool<RsHot2>().Reserve(10);
            Assert.That(world.Pool<RsHot2>().SparseCapacity, Is.LessThan(1 << 20), "a pool reserved afterwards does not take the failed size");
            world.Add(e, new RsHot2());
            Assert.That(world.Get<RsHot>(e).V, Is.EqualTo(1));
            WorldInvariants.AssertConsistent(world, "after the failed ReserveEntities");
        }

        [Test]
        public void PoolReserve_AllocationFails_PoolAndItsFiltersStayUnreserved() {
            var world = new World();
            var filter = world.Filter().Inc<RsFailed>().End();
            var pool = world.Pool<RsFailed>();
            var e = world.CreateEntity(new RsFailed { V = 3 });

            Assert.Throws<OutOfMemoryException>(() => pool.Reserve(int.MaxValue));

            Assert.That(pool.ReservedComponents, Is.EqualTo(0));
            Assert.That(filter.ReservedCount, Is.EqualTo(0));
            pool.Reserve(100);
            Assert.That(filter.ReservedCount, Is.EqualTo(100), "a later Reserve still reserves the filters");
            Assert.That(pool.Get(e.Index).V, Is.EqualTo(3));
            WorldInvariants.AssertConsistent(world, "after the failed pool Reserve");
        }

        // =================================================================
        // The obsolete World.Reserve keeps its 2.2 behaviour
        // =================================================================

#pragma warning disable CS0618
        [Test]
        public void ObsoleteReserve_StillSizesEveryPoolAndFilter_WithoutReservingThem() {
            var world = new World(Small());
            var existing = world.Pool<RsHot>();
            var filter = world.Filter().Inc<RsHot>().End();

            world.Reserve(2000, 300);

            var late = world.Pool<RsLegacyLate>();
            var lateFilter = world.Filter().Inc<RsLegacyLate>().End();
            Assert.That(existing.SparseCapacity, Is.EqualTo(2000));
            Assert.That(existing.DenseCapacity, Is.EqualTo(300));
            Assert.That(late.SparseCapacity, Is.EqualTo(2000), "pools created later too");
            Assert.That(late.DenseCapacity, Is.EqualTo(300));
            Assert.That(filter.DenseCapacity, Is.EqualTo(300));
            Assert.That(lateFilter.DenseCapacity, Is.EqualTo(300));
            Assert.That(world.ReservedEntities, Is.EqualTo(2000));
            Assert.That(existing.ReservedComponents, Is.EqualTo(0), "it does not reserve the pools");

            world.ReserveEntities(4000);
            Assert.That(existing.SparseCapacity, Is.EqualTo(2000), "so a later ReserveEntities does not extend them");
        }
#pragma warning restore CS0618

        // =================================================================
        // KENSEI_DEBUG: what is counted, what is only logged
        // =================================================================

#if KENSEI_DEBUG
        [Test]
        public void GrowthsAfterReserve_ReservedPoolPastItsReserve_CountsWithThatReserve() {
            Assume.That(EcsProfiler.IsEnabled, Is.False, "the profiler must be off for this test");
            var world = new World(Small());
            world.ReserveEntities(64);
            world.Pool<RsWatchedDense>().Reserve(8);

            for (int i = 0; i < 9; i++) {
                world.CreateEntity(new RsWatchedDense());
            }

            var first = world.FirstGrowthAfterReserve;
            Assert.That(world.GrowthsAfterReserve, Is.EqualTo(1));
            Assert.That(first.ToString(), Is.EqualTo("RsWatchedDense pool dense 8 -> 16 (reserved 8) at tick 0"));
        }

        [Test]
        public void GrowthsAfterReserve_WorldSlotsPastReservedEntities() {
            Assume.That(EcsProfiler.IsEnabled, Is.False, "the profiler must be off for this test");
            var world = new World(Small());
            world.ReserveEntities(20);
            world.Pool<RsHot>().Reserve(40);

            for (int i = 0; i < 21; i++) {
                world.CreateEntity(new RsHot());
            }

            Assert.That(world.FirstGrowthAfterReserve.ToString(), Is.EqualTo("World entity slots 20 -> 40 (reserved 20) at tick 0"));
        }

        [Test]
        public void UnreservedGrowth_NotCounted_LoggedAsNotReservedWhileRecorded() {
            Assume.That(EcsProfiler.IsEnabled, Is.False, "the profiler must be off before this test");
            var world = new World(Small());
            world.ReserveEntities(100);
            world.Pool<RsHot>().Reserve(100);
            var entities = new Entity[40];
            for (int i = 0; i < entities.Length; i++) {
                entities[i] = world.CreateEntity(new RsHot());
            }
            world.Pool<RsCold>();
            EcsProfiler.Enable(world);
            try {
                world.Add(entities[39], new RsCold());
                var grown = EcsProfiler.GetEventsByType(ProfileEventType.StorageGrown);

                Assert.That(grown.Count, Is.EqualTo(1));
                Assert.That(grown[0].ComponentType, Is.EqualTo("RsCold pool sparse 16 -> 40 (not reserved)"));
                Assert.That(world.GrowthsAfterReserve, Is.EqualTo(0), "logged, not counted");
            } finally {
                EcsProfiler.Disable();
                EcsProfiler.Clear();
            }
        }

        [Test]
        public void UnreservedGrowth_ProfilerOff_BuildsNoName() {
            var field = typeof(ComponentPool<RsUnwatched>).GetField("_typeName", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null);
            var world = new World(Small());
            world.ReserveEntities(200);
            for (int i = 0; i < 150; i++) {
                world.CreateEntity(new RsUnwatched());
            }

            Assert.That(field.GetValue(world.Pool<RsUnwatched>()), Is.Null, "an unreserved pool grew with the profiler off: no name");
            Assert.That(world.GrowthsAfterReserve, Is.EqualTo(0));
        }
#endif
    }
}
