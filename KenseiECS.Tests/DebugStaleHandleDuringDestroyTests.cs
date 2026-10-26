#if KENSEI_DEBUG
using System;
using NUnit.Framework;

namespace KenseiECS.Tests {
    // Under KENSEI_DEBUG a dead handle is accepted only for the entity whose DestroyEntity is
    // running (its listeners may still touch it), not for any dead handle while some entity dies.
    [TestFixture]
    public class DebugStaleHandleDuringDestroyTests {
        private struct StaleHit { public int V; }

        private struct DyingHit { public int V; }

        private sealed class OnRemovedAction : IComponentListener<Health> {
            public Action<int> Action;

            public void OnAdded(int entityIndex, ref Health component) {
            }

            public void OnRemoved(int entityIndex, ref Health component) {
                Action?.Invoke(entityIndex);
            }
        }

        private static Exception Capture(Action action) {
            try {
                action();
                return null;
            } catch (Exception ex) {
                return ex;
            }
        }

        [Test]
        public void AddEvent_StaleHandleWhileAnotherEntityIsDestroyed_Throws() {
            var world = new World();
            var stale = world.CreateEntity(new Position());
            var dying = world.CreateEntity(new Health());
            world.DestroyEntity(stale);
            Exception thrown = null;
            world.Pool<Health>().AddListener(new OnRemovedAction {
                Action = _ => thrown = Capture(() => world.AddEvent(stale, new StaleHit { V = 1 }))
            });

            world.DestroyEntity(dying);

            Assert.That(thrown, Is.InstanceOf<InvalidOperationException>(), "a stale handle must throw even while another entity is being destroyed");
            Assert.That(world.Pool<EventBuffer<StaleHit>>().Has(stale.Index), Is.False, "nothing may be added to the dead slot");
            WorldInvariants.AssertConsistent(world, "after the destroy");
        }

        [Test]
        public void Add_StaleHandleWhileAnotherEntityIsDestroyed_Throws() {
            var world = new World();
            var stale = world.CreateEntity(new Position());
            var dying = world.CreateEntity(new Health());
            world.DestroyEntity(stale);
            Exception thrown = null;
            world.Pool<Health>().AddListener(new OnRemovedAction {
                Action = _ => thrown = Capture(() => world.Add(stale, new Velocity()))
            });

            world.DestroyEntity(dying);

            Assert.That(thrown, Is.InstanceOf<InvalidOperationException>());
            Assert.That(world.Pool<Velocity>().Has(stale.Index), Is.False);
        }

        [Test]
        public void PoolAdd_DeadSlotWhileAnotherEntityIsDestroyed_Throws() {
            var world = new World();
            var stale = world.CreateEntity(new Position());
            var dying = world.CreateEntity(new Health());
            world.DestroyEntity(stale);
            Exception thrown = null;
            world.Pool<Health>().AddListener(new OnRemovedAction {
                Action = _ => thrown = Capture(() => world.Pool<Velocity>().Add(stale.Index, new Velocity()))
            });

            world.DestroyEntity(dying);

            Assert.That(thrown, Is.InstanceOf<InvalidOperationException>());
            Assert.That(world.Pool<Velocity>().Has(stale.Index), Is.False);
        }

        [Test]
        public void AddEvent_OnTheEntityBeingDestroyed_IsAccepted() {
            var world = new World();
            var dying = world.CreateEntity(new Health());
            Exception thrown = new Exception("listener did not run");
            world.Pool<Health>().AddListener(new OnRemovedAction {
                Action = index => thrown = Capture(() => world.AddEvent(world.GetEntity(index), new DyingHit { V = 1 }))
            });

            world.DestroyEntity(dying);

            Assert.That(thrown, Is.Null, "listeners may still touch the entity whose destroy is running");
            Assert.That(world.IsAlive(dying), Is.False);
            Assert.That(world.Pool<EventBuffer<DyingHit>>().Count, Is.EqualTo(0), "the drain removes what the listener added");
            WorldInvariants.AssertConsistent(world, "after the destroy");
        }
    }
}
#endif
