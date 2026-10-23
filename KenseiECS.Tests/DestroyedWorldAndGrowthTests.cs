using System;
using System.Reflection;
using NUnit.Framework;

namespace KenseiECS.Tests {
    // Fixes found by Elemental Survivors on 2.2.0: calls on a destroyed world, and the
    // growth-after-Reserve signal (which storage grew, without building names for nothing).

    public struct GrowthProbe : IComponent { public int V; }
    public struct GrowthUnwatched : IComponent { public int V; }
    public struct GrowthFiltered : IComponent { public int V; }

    [TestFixture]
    public class DestroyedWorldAndGrowthTests {
        [Test]
        public void CreateEntity_OnDestroyedWorld_LeavesIsAliveAndDestroyEntitySafe() {
            var world = new World();
            var entity = world.CreateEntity(new Position());
            world.Destroy();

#if KENSEI_DEBUG
            Assert.Throws<ObjectDisposedException>(() => world.CreateEntity(new Position()));
#else
            // Documented: in release the call itself fails without a check.
            Assert.Throws<NullReferenceException>(() => world.CreateEntity(new Position()));
#endif
            // The failed call must not have claimed a slot: these read only what is still there.
            Assert.That(world.IsAlive(entity), Is.False);
            Assert.That(world.IsAlive(new Entity(0, 1)), Is.False);
            Assert.DoesNotThrow(() => world.DestroyEntity(entity));
            Assert.That(world.EntityCount, Is.EqualTo(0));
        }

        [Test]
        public void Clear_AfterDestroy_ThrowsObjectDisposed() {
            var world = new World();
            world.CreateEntity(new Position());
            world.Destroy();

            var ex = Assert.Throws<ObjectDisposedException>(() => world.Clear());
            Assert.That(ex.Message, Does.Contain("World.Clear after World.Destroy"));
            Assert.DoesNotThrow(() => world.Destroy(), "a repeated Destroy stays a no-op");
        }

        [Test]
        public void FirstGrowthAfterReserve_KeepsTheFirstGrowthOnly() {
            var world = new World(new WorldConfig { InitialPoolDenseCapacity = 2 });
            Assert.That(world.FirstGrowthAfterReserve.IsEmpty, Is.True);

            world.Reserve(4, 2);
            world.CreateEntity(new GrowthProbe());
            world.CreateEntity(new GrowthProbe());
            Assert.That(world.FirstGrowthAfterReserve.IsEmpty, Is.True, "nothing outgrew the reserve yet");

            world.CreateEntity(new GrowthProbe());
            for (int i = 0; i < 20; i++) {
                world.CreateEntity(new GrowthProbe());
            }
            var first = world.FirstGrowthAfterReserve;
#if KENSEI_DEBUG
            Assert.That(first.IsEmpty, Is.False);
            Assert.That(first.Owner, Is.EqualTo(nameof(GrowthProbe)));
            Assert.That(first.Storage, Is.EqualTo("pool dense"));
            Assert.That(first.OldSize, Is.EqualTo(2));
            Assert.That(first.NewSize, Is.EqualTo(4));
            Assert.That(first.Reserved, Is.EqualTo(2));
            Assert.That(first.Tick, Is.EqualTo(world.Tick));
            Assert.That(first.ToString(), Is.EqualTo("GrowthProbe pool dense 2 -> 4 (reserved 2) at tick 0"));
            Assert.That(world.GrowthsAfterReserve, Is.GreaterThan(1), "later growths are counted, not recorded");

            world.Clear();
            Assert.That(world.FirstGrowthAfterReserve.ToString(), Is.EqualTo(first.ToString()), "Clear does not reset it");
#else
            Assert.That(first.IsEmpty, Is.True, "release builds record nothing");
            Assert.That(first.ToString(), Is.EqualTo("none"));
#endif
        }

        [Test]
        public void FirstGrowthAfterReserve_NamesTheFilter() {
            var world = new World(new WorldConfig { InitialPoolDenseCapacity = 2 });
            var filter = world.Filter().Inc<GrowthFiltered>().End();
            world.Reserve(64, 2);
            // The pool is reserved on its own, so the filter's dense array is the first to grow.
            world.Pool<GrowthFiltered>().Reserve(64);
            for (int i = 0; i < 10; i++) {
                world.CreateEntity(new GrowthFiltered());
            }

            Assert.That(filter.Count, Is.EqualTo(10));
#if KENSEI_DEBUG
            var first = world.FirstGrowthAfterReserve;
            Assert.That(first.Owner, Is.EqualTo(filter.ToString()));
            Assert.That(first.Owner, Is.EqualTo("Filter Inc<GrowthFiltered>"));
            Assert.That(first.Storage, Is.EqualTo("filter dense"));
            Assert.That(first.OldSize, Is.EqualTo(2), "a filter's dense size is its capacity in entities");
            Assert.That(first.NewSize, Is.GreaterThan(2));
#else
            Assert.That(world.FirstGrowthAfterReserve.IsEmpty, Is.True);
#endif
        }

#if KENSEI_DEBUG
        [Test]
        public void UnwatchedPoolGrowth_DoesNotBuildTheTypeName() {
            // typeof(T).Name is a new string on Mono and IL2CPP; without Reserve a growing pool
            // must not build it just to have it ignored.
            var field = typeof(ComponentPool<GrowthUnwatched>).GetField("_typeName", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, "the cached name field exists under KENSEI_DEBUG");

            var world = new World(new WorldConfig { InitialPoolDenseCapacity = 2, InitialPoolSparseCapacity = 2 });
            for (int i = 0; i < 40; i++) {
                world.CreateEntity(new GrowthUnwatched());
            }
            var pool = world.Pool<GrowthUnwatched>();
            Assert.That(world.GrowthsAfterReserve, Is.EqualTo(0));
            Assert.That(field.GetValue(pool), Is.Null, "dense and sparse grew, nothing was watched, no name was built");

            // Organic growth already took dense and sparse to 64; 100 more entities pass that.
            world.Reserve(48, 40);
            for (int i = 0; i < 100; i++) {
                world.CreateEntity(new GrowthUnwatched());
            }
            Assert.That(world.GrowthsAfterReserve, Is.GreaterThan(0));
            Assert.That(field.GetValue(pool), Is.EqualTo(nameof(GrowthUnwatched)), "a watched growth names the pool");
        }
#endif
    }
}
