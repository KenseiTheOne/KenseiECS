using System;
using System.Runtime.CompilerServices;
using NUnit.Framework;

namespace KenseiECS.Tests {
    // World.Destroy letting go of everything, a Reserve that runs out of memory, and the exact
    // sizes Reserve gives filters.
    [TestFixture]
    public class WorldStorageRobustnessTests {
        private struct GroupedA : IComponent { public int V; }

        private struct GroupedB : IComponent { public int V; }

        private struct LateAfterFailedReserve : IComponent { public int V; }

        private struct LateAfterFailedEntityReserve : IComponent { public int V; }

        private struct FilterOnlyReserve : IComponent { public int V; }

        private struct LateAfterFilterOnlyReserve : IComponent { }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference PopulateGroupAndDestroy(World world) {
            var group = world.Group<GroupedA, GroupedB>();
            for (int i = 0; i < 100; i++) {
                var e = world.CreateEntity(new GroupedA { V = i });
                world.Add(e, new GroupedB { V = i });
            }
            Assert.That(group.Count, Is.EqualTo(100), "test setup");
            var pool = new WeakReference(world.Pool<GroupedA>());
            world.Destroy();
            return pool;
        }

        [Test]
        public void Destroy_ReleasesOwningGroupsAndTheirPools() {
            var world = new World();

            var pool = PopulateGroupAndDestroy(world);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.That(world.GroupCount, Is.EqualTo(0), "a destroyed world keeps no groups");
            Assert.That(pool.IsAlive, Is.False, "a group kept alive by the destroyed world must not hold its pools");
            GC.KeepAlive(world);
        }

        [Test]
        public void Reserve_ComponentsAllocationFails_WorldStaysUsable() {
            var world = new World();
            var early = world.Filter().Inc<Position>().End();
            var e = world.CreateEntity(new Position { X = 1 });

            Assert.Throws<OutOfMemoryException>(() => world.Reserve(0, int.MaxValue));

            Assert.That(() => world.Pool<LateAfterFailedReserve>(), Throws.Nothing, "a pool created after a failed Reserve must not inherit its size");
            Assert.That(() => world.Filter().Inc<LateAfterFailedReserve>().End(), Throws.Nothing, "nor a filter");
            var late = world.CreateEntity(new LateAfterFailedReserve { V = 2 });
            world.Add(late, new Position());
            Assert.That(world.Get<Position>(e).X, Is.EqualTo(1));
            Assert.That(early.Count, Is.EqualTo(2));
            WorldInvariants.AssertConsistent(world, "after the failed Reserve");
        }

        [Test]
        public void Reserve_EntitiesAllocationFails_WorldStaysUsable() {
            var world = new World();
            var e = world.CreateEntity(new Position { X = 1 });

            Assert.Throws<OutOfMemoryException>(() => world.Reserve(int.MaxValue));

            Assert.That(() => world.Pool<LateAfterFailedEntityReserve>(), Throws.Nothing);
            for (int i = 0; i < 1000; i++) {
                world.CreateEntity(new LateAfterFailedEntityReserve { V = i });
            }
            Assert.That(world.Get<Position>(e).X, Is.EqualTo(1));
            Assert.That(world.EntityCount, Is.EqualTo(1001));
            WorldInvariants.AssertConsistent(world, "after the failed Reserve");
        }

        [Test]
        public void FilterReserve_AllocatesExactlyTheRequestedCount() {
            var world = new World(new WorldConfig { InitialPoolDenseCapacity = 64 });
            var filter = world.Filter().Inc<Position>().End();

            filter.Reserve(100);

            Assert.That(filter.DenseCapacity, Is.EqualTo(100), "Reserve states the need; doubling would allocate about twice as much");
        }

        [Test]
        public void WorldReserveComponents_SizesExistingFiltersExactly() {
            var world = new World(new WorldConfig { InitialPoolDenseCapacity = 64 });
            var before = world.Filter().Inc<Position>().End();

            world.Reserve(0, 100);
            var after = world.Filter().Inc<Velocity>().End();

            Assert.That(before.DenseCapacity, Is.EqualTo(100));
            Assert.That(after.DenseCapacity, Is.EqualTo(100));
            Assert.That(world.Pool<Position>().DenseCapacity, Is.EqualTo(100));
        }

        [Test]
        public void FilterGrowth_DuringPlay_StillDoubles() {
            var world = new World(new WorldConfig { InitialPoolDenseCapacity = 4 });
            var filter = world.Filter().Inc<Position>().End();

            for (int i = 0; i < 5; i++) {
                world.CreateEntity(new Position());
            }

            Assert.That(filter.DenseCapacity, Is.EqualTo(9), "organic growth keeps doubling (5 slots incl. the terminator -> 10)");
        }

        // The exact size is count + 1 slots (slot 0 is the terminator); for int.MaxValue that overflowed
        // into a negative size, and the allocation threw OverflowException (2.2.1 did nothing).
        [Test]
        public void FilterReserve_IntMaxValue_ThrowsOutOfMemoryAndKeepsTheFilter() {
            var world = new World(new WorldConfig { InitialPoolDenseCapacity = 64 });
            var filter = world.Filter().Inc<Position>().End();
            int capacity = filter.DenseCapacity;

            Assert.Throws<OutOfMemoryException>(() => filter.Reserve(int.MaxValue));

            Assert.That(filter.DenseCapacity, Is.EqualTo(capacity));
            world.CreateEntity(new Position());
            Assert.That(filter.Count, Is.EqualTo(1));
        }

        // A filter does not create pools, so with no pool in the world the filter is the first array
        // World.Reserve grows.
        [Test]
        public void WorldReserve_IntMaxValueComponentsWithOnlyAFilter_ThrowsOutOfMemoryAndWorldStaysUsable() {
            var world = new World();
            var filter = world.Filter().Inc<FilterOnlyReserve>().End();

            Assert.Throws<OutOfMemoryException>(() => world.Reserve(0, int.MaxValue));

            Assert.That(() => world.Pool<LateAfterFilterOnlyReserve>(), Throws.Nothing, "a pool created after the failed Reserve is not sized for it");
            Assert.That(() => world.Reserve(10, 10), Throws.Nothing);
            var e = world.CreateEntity(new FilterOnlyReserve { V = 3 });
            world.Add(e, new LateAfterFilterOnlyReserve());
            Assert.That(filter.Count, Is.EqualTo(1));
            WorldInvariants.AssertConsistent(world, "after the failed Reserve");
        }
    }
}
