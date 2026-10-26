using System;
using System.Runtime.CompilerServices;
using NUnit.Framework;

namespace KenseiECS.Tests {
    // Findings of the 2.3.0 review: UnsubscribeLater while the entity is destroyed, on a destroyed
    // world and without a runner; the reserve filters take from their types follows the current
    // reserves in any order, also after the obsolete World.Reserve.
    [TestFixture]
    public class V230ReviewFixTests {
        public interface IDeath {
            void OnDeath(World world, Entity entity);
        }

        public interface INeverSubscribed {
        }

        private sealed class LeavingView : IDeath {
            public int Calls;

            public void OnDeath(World world, Entity entity) {
                Calls++;
                world.UnsubscribeLater<IDeath>(entity, this);
            }
        }

        private sealed class Unused : INeverSubscribed {
        }

        // "Health removed" (here: by DestroyEntity) tells the entity's views, which leave at once.
        private sealed class DeathNotifier : IComponentListener<Health> {
            public World World;

            public void OnAdded(int entityIndex, ref Health component) { }

            public void OnRemoved(int entityIndex, ref Health component) {
                var entity = World.GetEntity(entityIndex);
                var views = World.Pool<Listeners<IDeath>>();
                if (!views.Has(entityIndex)) {
                    return;
                }
                var values = views.Get(entityIndex).Values;
                for (int i = 0; values != null && i < values.Count; i++) {
                    values[i].OnDeath(World, entity);
                }
            }
        }

        private struct Rare : IComponent { }

        [Test]
        public void UnsubscribeLater_WhileTheEntityIsDestroyed_DestroyFinishesAndTheSlotIsClean() {
            var world = new World();
            var view = new LeavingView();
            var e = world.CreateEntity(new Health());
            world.Subscribe<IDeath>(e, view);
            world.Pool<Health>().AddListener(new DeathNotifier { World = world });
            var filter = world.Filter().Inc<Health>().End();

            Assert.That(() => world.DestroyEntity(e), Throws.Nothing);

            Assert.That(view.Calls, Is.EqualTo(1));
            Assert.That(world.IsAlive(e), Is.False);
            Assert.That(filter.Count, Is.Zero, "the destroy removed every component");
            var next = world.CreateEntity(new Health());
            Assert.That(world.Has<Health>(next), Is.True, "the reused slot starts clean");
            Assert.That(() => world.NextTick(), Throws.Nothing, "the queued removal of the dead entity is skipped");
        }

        [Test]
        public void UnsubscribeLater_OnADestroyedWorld_DoesNothingAndKeepsNoListener() {
            var world = new World();
            var e = world.CreateEntity(new Health());
            world.Destroy();

            var queued = CallOnDestroyedWorld(world, e);
            GC.Collect();
            GC.WaitForPendingFinalizers();

            Assert.That(queued.IsAlive, Is.False, "a destroyed world holds no listener");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference CallOnDestroyedWorld(World world, Entity entity) {
            var view = new LeavingView();
            Assert.That(() => world.UnsubscribeLater<IDeath>(entity, view), Throws.Nothing);
            return new WeakReference(view);
        }

        [Test]
        public void UnsubscribeLater_WithoutARunner_IsAppliedAtNextTick() {
            var world = new World();
            var view = new LeavingView();
            var e = world.CreateWithListener<IDeath>(view);

            world.UnsubscribeLater<IDeath>(e, view);
            Assert.That(world.Get<Listeners<IDeath>>(e).Count, Is.EqualTo(1));

            world.NextTick();
            Assert.That(world.Get<Listeners<IDeath>>(e).Count, Is.Zero);
        }

        [Test]
        public void UnsubscribeLater_ForATypeNobodySubscribed_CreatesNoPool() {
            var world = new World();
            var e = world.CreateEntity(new Health());

            world.UnsubscribeLater<INeverSubscribed>(e, new Unused());
            world.NextTick();

            Assert.That(world.GetPool(ComponentType<Listeners<INeverSubscribed>>.Index), Is.Null);
        }

        // List.Remove compares with Equals of the elements before the one removed.
        private sealed class ThrowingEquals : IDeath {
            public void OnDeath(World world, Entity entity) { }
            public override bool Equals(object obj) => throw new InvalidOperationException("Equals failed");
            public override int GetHashCode() => 0;
        }

        private sealed class NotifyDeath : IRunSystem {
            public Entity Entity;

            public void Run(World world) {
                var values = world.Get<Listeners<IDeath>>(Entity).Values;
                for (int i = 0; i < values.Count; i++) {
                    values[i].OnDeath(world, Entity);
                }
            }
        }

        private sealed class Failing : IRunSystem {
            public void Run(World world) => throw new ApplicationException("system failed");
        }

        [Test]
        public void SystemThrows_AndApplyingTheQueueThrowsToo_RunReportsTheSystemException() {
            var world = new World();
            var e = world.CreateWithListener<IDeath>(new ThrowingEquals());
            world.Subscribe<IDeath>(e, new LeavingView());
            var runner = new SystemsRunner(world).Add(new NotifyDeath { Entity = e }).Add(new Failing());
            runner.Init();

            var thrown = Assert.Throws<ApplicationException>(() => runner.Run());
            Assert.That(thrown.Message, Is.EqualTo("system failed"));
            Assert.That(() => runner.Run(), Throws.TypeOf<ApplicationException>(), "the runner keeps working");
        }

#if KENSEI_DEBUG
        [Test]
        public void ProfilerMaxEvents_SetBeforeEnable_BoundsTheBuffer() {
            int saved = EcsProfiler.MaxEvents;
            try {
                EcsProfiler.Disable();
                EcsProfiler.MaxEvents = 3;
                var world = new World();
                EcsProfiler.Enable(world);
                for (int i = 0; i < 10; i++) {
                    world.CreateEntity(new Health());
                }
                Assert.That(EcsProfiler.GetEvents().Count, Is.EqualTo(3));
                Assert.That(EcsProfiler.MaxEvents, Is.EqualTo(3));
            } finally {
                EcsProfiler.Disable();
                EcsProfiler.MaxEvents = saved;
                EcsProfiler.Clear();
            }
        }
#endif

        [Test]
        public void SystemsRunnerAdd_NullSystem_ThrowsArgumentNull() {
            var runner = new SystemsRunner(new World());
            Assert.Throws<ArgumentNullException>(() => runner.Add(null));
        }

        [Test]
        public void DerivedReserve_FollowsTheReservesInAnyOrder() {
            var poolFirst = new World();
            poolFirst.Pool<Position>().Reserve(5000);
            var a = poolFirst.Filter().Inc<Position>().End();
            poolFirst.ReserveEntities(1500);

            var slotsFirst = new World();
            slotsFirst.ReserveEntities(1500);
            slotsFirst.Pool<Position>().Reserve(5000);
            var b = slotsFirst.Filter().Inc<Position>().End();

            Assert.That(a.ReservedCount, Is.EqualTo(1500), "a later ReserveEntities caps it");
            Assert.That(b.ReservedCount, Is.EqualTo(1500));
        }

        [Test]
        public void DerivedReserve_AnyTypes_FollowTheirCurrentReserves() {
            var world = new World();
            world.ReserveEntities(10_000);
            world.Pool<Position>().Reserve(5000);
            world.Pool<Velocity>().Reserve(10);
            world.Pool<Rare>().Reserve(10);
            var filter = world.Filter().Inc<Position>().Any<Velocity>().Any<Rare>().End();

            Assert.That(filter.ReservedCount, Is.EqualTo(20), "at most the sum of its Any types");
        }

        [Test]
        public void ObsoleteWorldReserve_MovesTheCapOfTheDerivedReserve() {
            var world = new World();
            world.ReserveEntities(100);
            world.Pool<Position>().Reserve(1000);
            var filter = world.Filter().Inc<Position>().End();
            Assert.That(filter.ReservedCount, Is.EqualTo(100));

#pragma warning disable CS0618
            world.Reserve(1000);
#pragma warning restore CS0618

            Assert.That(filter.ReservedCount, Is.EqualTo(1000));
            for (int i = 0; i < 1000; i++) {
                world.CreateEntity(new Position());
            }
            Assert.That(filter.Count, Is.EqualTo(1000));
            Assert.That(world.GrowthsAfterReserve, Is.Zero, "1000 entities fit every reserve");
        }
    }
}
