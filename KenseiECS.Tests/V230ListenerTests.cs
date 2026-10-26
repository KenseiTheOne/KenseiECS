using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using NUnit.Framework;

namespace KenseiECS.Tests {
    // 2.3.0: a system calls the listeners in Listeners<T>.Values itself, a listener leaves from inside
    // its callback with UnsubscribeLater (applied when the outermost Run returns), and Listeners<T>.Invoke,
    // Add and Remove are obsolete.
    [TestFixture]
    public class V230ListenerTests {
        public interface IPing {
            void OnPing(World world, Entity entity);
        }

        private sealed class Counter : IPing {
            public int Calls;
            public bool LeaveOnPing;
            public bool UnsubscribeAtOnce;

            public void OnPing(World world, Entity entity) {
                Calls++;
                if (LeaveOnPing) {
                    world.UnsubscribeLater<IPing>(entity, this);
                }
                if (UnsubscribeAtOnce) {
                    world.Unsubscribe<IPing>(entity, this);
                }
            }
        }

        // The dispatch the docs show: the system walks the list and calls each listener.
        private sealed class PingSystem : IInitSystem, IRunSystem {
            private Filter _filter;
            private ComponentPool<Listeners<IPing>> _listeners;

            public void Init(World world, SharedData shared) {
                _filter = world.Filter().Inc<Listeners<IPing>>().End();
                _listeners = world.Pool<Listeners<IPing>>();
            }

            public void Run(World world) {
                foreach (int e in _filter) {
                    var entity = world.GetEntity(e);
                    var listeners = _listeners.Get(e).Values;
                    for (int i = 0; i < listeners.Count; i++) {
                        listeners[i].OnPing(world, entity);
                    }
                }
            }
        }

        // The variant the docs offer as a check: foreach over the list throws when it changes.
        private sealed class ForeachPingSystem : IInitSystem, IRunSystem {
            private Filter _filter;
            private ComponentPool<Listeners<IPing>> _listeners;

            public void Init(World world, SharedData shared) {
                _filter = world.Filter().Inc<Listeners<IPing>>().End();
                _listeners = world.Pool<Listeners<IPing>>();
            }

            public void Run(World world) {
                foreach (int e in _filter) {
                    var entity = world.GetEntity(e);
                    foreach (var listener in _listeners.Get(e).Values) {
                        listener.OnPing(world, entity);
                    }
                }
            }
        }

        private sealed class RunAction : IRunSystem {
            private readonly Action<World> _action;

            public RunAction(Action<World> action) {
                _action = action;
            }

            public void Run(World world) => _action(world);
        }

        private static SystemsRunner PingRunner(World world) {
            var runner = new SystemsRunner(world).Add(new PingSystem());
            runner.Init();
            return runner;
        }

        [Test]
        public void UnsubscribeLater_InsideTheDispatch_RestIsCalledAndTheListenerIsGoneAfterRun() {
            var world = new World();
            var leaver = new Counter { LeaveOnPing = true };
            var second = new Counter();
            var third = new Counter();
            var e = world.CreateWithListener<IPing>(leaver);
            world.Subscribe<IPing>(e, second);
            world.Subscribe<IPing>(e, third);
            var runner = PingRunner(world);

            Assert.That(() => runner.Run(), Throws.Nothing, "the list does not change while the system iterates it");
            Assert.That(new[] { leaver.Calls, second.Calls, third.Calls }, Is.EqualTo(new[] { 1, 1, 1 }));
            Assert.That(world.Get<Listeners<IPing>>(e).Count, Is.EqualTo(2), "removed when Run returned");

            runner.Run();
            Assert.That(new[] { leaver.Calls, second.Calls, third.Calls }, Is.EqualTo(new[] { 1, 2, 2 }),
                "the next tick does not call the listener that left");
        }

        [Test]
        public void UnsubscribeLater_InANestedRunner_IsAppliedWhenTheOutermostRunReturns() {
            var world = new World();
            var e = world.CreateWithListener<IPing>(new Counter { LeaveOnPing = true });
            world.Subscribe<IPing>(e, new Counter());
            int countAfterTheChild = -1;
            var child = new SystemsRunner(world).Add(new PingSystem());
            var root = new SystemsRunner(world)
                .Add(child)
                .Add(new RunAction(w => countAfterTheChild = w.Get<Listeners<IPing>>(e).Count));
            root.Init();

            root.Run();

            Assert.That(countAfterTheChild, Is.EqualTo(2), "the child's Run is not the end of the tick");
            Assert.That(world.Get<Listeners<IPing>>(e).Count, Is.EqualTo(1));
        }

        [Test]
        public void UnsubscribeLater_InAPhaseRunOnItsOwn_IsAppliedWhenThatRunReturns() {
            // EcsBootstrap drives its "late" phase from LateUpdate: that Run is the outermost one.
            var world = new World();
            var e = world.CreateWithListener<IPing>(new Counter { LeaveOnPing = true });
            var late = new SystemsRunner(world).Add(new PingSystem());
            var root = new SystemsRunner(world).AddPhase("late", late);
            root.Init();

            late.Run();

            Assert.That(world.Get<Listeners<IPing>>(e).Count, Is.Zero);
        }

        [Test]
        public void UnsubscribeLater_OutsideRun_IsAppliedAtTheNextTick() {
            var world = new World();
            var listener = new Counter();
            var e = world.CreateWithListener<IPing>(listener);
            var runner = PingRunner(world);

            world.UnsubscribeLater<IPing>(e, listener);
            Assert.That(world.Get<Listeners<IPing>>(e).Count, Is.EqualTo(1), "not removed at once");

            runner.Run();
            Assert.That(listener.Calls, Is.Zero, "the root Run starts with NextTick, which applied it");
            Assert.That(world.Get<Listeners<IPing>>(e).Count, Is.Zero);
        }

        [Test]
        public void UnsubscribeLater_ASystemThrows_IsAppliedAnyway() {
            var world = new World();
            var e = world.CreateWithListener<IPing>(new Counter { LeaveOnPing = true });
            var runner = new SystemsRunner(world)
                .Add(new PingSystem())
                .Add(new RunAction(w => throw new InvalidOperationException("system failed")));
            runner.Init();

            Assert.Throws<InvalidOperationException>(() => runner.Run());
            Assert.That(world.Get<Listeners<IPing>>(e).Count, Is.Zero);
        }

        [Test]
        public void UnsubscribeLater_EntityDestroyedAndSlotReused_LeavesTheNewEntityAlone() {
            var world = new World();
            var listener = new Counter();
            var first = world.CreateWithListener<IPing>(listener);
            world.UnsubscribeLater<IPing>(first, listener);
            world.DestroyEntity(first);
            var second = world.CreateWithListener<IPing>(listener);
            Assert.That(second.Index, Is.EqualTo(first.Index), "the new entity takes the freed slot");

            PingRunner(world).Run();

            Assert.That(world.Get<Listeners<IPing>>(second).Count, Is.EqualTo(1),
                "the removal belonged to the destroyed entity");
        }

        [Test]
        public void UnsubscribeLater_AfterThePeak_AllocatesNothing() {
            var world = new World();
            var leaver = new Counter { LeaveOnPing = true };
            var e = world.CreateWithListener<IPing>(new Counter());
            var runner = PingRunner(world);
            for (int i = 0; i < 3; i++) {
                world.Subscribe<IPing>(e, leaver);
                runner.Run();
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) {
                world.Subscribe<IPing>(e, leaver);
                runner.Run();
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.That(allocated, Is.Zero, "the queue and the list are reused");
            Assert.That(leaver.Calls, Is.EqualTo(103));
            Assert.That(world.Get<Listeners<IPing>>(e).Count, Is.EqualTo(1));
        }

        [Test]
        public void Clear_DropsTheQueuedRemovals() {
            var world = new World();
            var queued = QueueRemovalOfAListenerNothingElseHolds(world);

            world.Clear();
            GC.Collect();
            GC.WaitForPendingFinalizers();

            Assert.That(queued.IsAlive, Is.False, "the queue must not keep a listener (a view) alive");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference QueueRemovalOfAListenerNothingElseHolds(World world) {
            var e = world.CreateEntity(new Position());
            var listener = new Counter();
            world.UnsubscribeLater<IPing>(e, listener);
            return new WeakReference(listener);
        }

        // The rule of the direct dispatch: a callback does not change the list it is called from.
        // The foreach variant turns a broken rule into an exception instead of a skipped listener.
        [Test]
        public void ForeachDispatch_UnsubscribeInsideTheCallback_Throws() {
            var world = new World();
            var e = world.CreateWithListener<IPing>(new Counter { UnsubscribeAtOnce = true });
            world.Subscribe<IPing>(e, new Counter());
            var runner = new SystemsRunner(world).Add(new ForeachPingSystem());
            runner.Init();

            Assert.Throws<InvalidOperationException>(() => runner.Run());
        }

        [Test]
        public void ObsoleteMembers_NameTheReplacementAndTheRemoval() {
            var type = typeof(Listeners<IPing>);
            AssertObsolete(type.GetMethod("Add"), "World.Subscribe");
            AssertObsolete(type.GetMethod("Remove"), "World.UnsubscribeLater");
            int invokes = 0;
            foreach (var method in type.GetMethods()) {
                if (method.Name == "Invoke") {
                    AssertObsolete(method, "for (int i = 0; values != null && i < values.Count; i++) values[i]");
                    invokes++;
                }
            }
            Assert.That(invokes, Is.EqualTo(2), "both 2.2 overloads");
        }

        private static void AssertObsolete(MethodInfo method, string replacement) {
            var attribute = method.GetCustomAttribute<ObsoleteAttribute>();
            Assert.That(attribute, Is.Not.Null, method + " is obsolete");
            Assert.That(attribute.IsError, Is.False, "a warning until 3.0");
            Assert.That(attribute.Message, Does.Contain(replacement).And.Contain("Removed in 3.0"));
        }

#if KENSEI_DEBUG
        [Test]
        public void UnsubscribeLater_DeadEntity_Throws() {
            var world = new World();
            var e = world.CreateWithListener<IPing>(new Counter());
            world.DestroyEntity(e);

            Assert.Throws<InvalidOperationException>(() => world.UnsubscribeLater<IPing>(e, new Counter()));
        }
#endif
    }
}
