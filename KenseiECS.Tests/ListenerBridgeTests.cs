using NUnit.Framework;

namespace KenseiECS.Tests {
    [TestFixture]
    public class ListenerBridgeTests {
        [Test]
        public void Subscribe_AddsListener() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            var listener = new TestDamageListener();
            world.Subscribe(e, listener);
            Assert.That(world.HasListeners<TestDamageListener>(e), Is.True, "Subscribe must register the listener");
        }

        [Test]
        public void Subscribe_Twice_KeepsBoth() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.Subscribe(e, new TestDamageListener());
            world.Subscribe(e, new TestDamageListener());
            Assert.That(world.Get<Listeners<TestDamageListener>>(e).Count, Is.EqualTo(2), "each Subscribe must add a listener");
        }

        [Test]
        public void Unsubscribe_LastListener_KeepsEntityAlive() {
            var world = new World();
            var listener = new TestDamageListener();
            var e = world.CreateWithListener(listener);
            world.Unsubscribe(e, listener);
            Assert.That(world.IsAlive(e), Is.True, "unsubscribing the last listener must not auto-destroy the entity");
        }

        [Test]
        public void Unsubscribe_LastListener_HasListenersIsFalse() {
            var world = new World();
            var listener = new TestDamageListener();
            var e = world.CreateWithListener(listener);
            world.Unsubscribe(e, listener);
            Assert.That(world.HasListeners<TestDamageListener>(e), Is.False, "HasListeners must be false once the list is empty");
        }

        [Test]
        public void Resubscribe_AfterUnsubscribe_Works() {
            var world = new World();
            var listener = new TestDamageListener();
            var e = world.CreateWithListener(listener);
            world.Unsubscribe(e, listener);
            world.Subscribe(e, listener);
            Assert.That(world.HasListeners<TestDamageListener>(e), Is.True, "OnDisable/OnEnable style resubscribe must work");
        }

        [Test]
        public void Unsubscribe_WithoutComponent_IsNoOp() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            Assert.That(() => world.Unsubscribe(e, new TestDamageListener()), Throws.Nothing, "Unsubscribe without a listeners component must be a no-op");
        }

        [Test]
        public void HasListeners_WithoutComponent_IsFalse() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            Assert.That(world.HasListeners<TestDamageListener>(e), Is.False, "HasListeners must be false without the component");
        }

        [Test]
        public void ListenersAutoReset_ClearsListOnRemove() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.Subscribe(e, new TestDamageListener());
            world.Remove<Listeners<TestDamageListener>>(e);
            world.Subscribe(e, new TestDamageListener());
            Assert.That(world.Get<Listeners<TestDamageListener>>(e).Count, Is.EqualTo(1), "removed listener list must not leak into a re-added component");
        }

        [Test]
        public void ReverseIteration_AllowsUnsubscribeFromCallback() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            var a = new TestDamageListener();
            var b = new TestDamageListener();
            world.Subscribe(e, a);
            world.Subscribe(e, b);

            ref var listeners = ref world.Get<Listeners<TestDamageListener>>(e);
            for (int i = listeners.Values.Count - 1; i >= 0; i--) {
                var l = listeners.Values[i];
                l.Hits++;
                world.Unsubscribe(e, l);
            }

            Assert.That(a.Hits + b.Hits, Is.EqualTo(2), "reverse iteration must visit every listener even when each unsubscribes itself");
            Assert.That(world.HasListeners<TestDamageListener>(e), Is.False, "all listeners must be gone");
        }
    }
}
