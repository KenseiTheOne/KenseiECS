using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace KenseiECS.Tests {
    // Which listeners a dispatch notifies when a listener of the same structural change subscribes or
    // unsubscribes others. The OnAdded dispatch of an Add starts after the filter and world listeners of
    // that Add, and the world dispatch of a removal after its filter listeners, so both take the
    // listeners subscribed by then, as 2.2.1 did (the hook frames of 2.2.2 first read the typed listeners
    // before the world update, and the lean removal path decided on the world dispatch before the filters).
    [TestFixture]
    public class ListenerSnapshotTests {
        private struct LsA : IComponent {
            public int V;
        }

        private struct LsB : IComponent {
        }

        private sealed class Typed : IComponentListener<LsA> {
            public readonly List<int> Added = new();
            public Action<int> OnAdd;

            public void OnAdded(int entityIndex, ref LsA component) {
                Added.Add(entityIndex);
                OnAdd?.Invoke(entityIndex);
            }

            public void OnRemoved(int entityIndex, ref LsA component) {
            }
        }

        private sealed class OnFilter : IFilterListener {
            public Action<int> Enter;
            public Action<int> Leave;

            public void OnEntityAdded(Filter filter, int entityIndex) =>
                Enter?.Invoke(entityIndex);

            public void OnEntityRemoved(Filter filter, int entityIndex) =>
                Leave?.Invoke(entityIndex);
        }

        private sealed class Events : WorldListenerBase {
            public readonly List<string> Log = new();
            public Action<int, int> OnAdd;

            public override void OnComponentAdded(int entityIndex, int typeIndex) {
                Log.Add("+" + ComponentType.NameOf(typeIndex) + entityIndex);
                OnAdd?.Invoke(entityIndex, typeIndex);
            }

            public override void OnComponentRemoved(int entityIndex, int typeIndex) =>
                Log.Add("-" + ComponentType.NameOf(typeIndex) + entityIndex);
        }

        // Runs the action once, on the first call.
        private static Action<int> Once(Action<int> action) {
            bool done = false;
            return entityIndex => {
                if (done) {
                    return;
                }
                done = true;
                action(entityIndex);
            };
        }

        [Test]
        public void Add_FilterListenerUnsubscribesTypedListener_ItGetsNoOnAdded() {
            var world = new World();
            var pool = world.Pool<LsA>();
            var typed = new Typed();
            pool.AddListener(typed);
            world.Filter().Inc<LsA>().End().AddListener(new OnFilter { Enter = _ => pool.RemoveListener(typed) });
            var e = world.CreateEntity(new LsB());

            world.Add(e, new LsA());

            Assert.That(typed.Added, Is.Empty, "a listener unsubscribed before the OnAdded dispatch must not get it");
        }

        [Test]
        public void Add_FilterListenerSubscribesTypedListener_ItGetsThisOnAdded() {
            var world = new World();
            var pool = world.Pool<LsA>();
            var first = new Typed();
            pool.AddListener(first);
            var late = new Typed();
            world.Filter().Inc<LsA>().End().AddListener(new OnFilter { Enter = Once(_ => pool.AddListener(late)) });
            var e = world.CreateEntity(new LsB());

            world.Add(e, new LsA());

            Assert.That(first.Added, Is.EqualTo(new[] { e.Index }));
            Assert.That(late.Added, Is.EqualTo(new[] { e.Index }), "a listener subscribed before the OnAdded dispatch must get it");
        }

        [Test]
        public void Add_WorldListenerUnsubscribesTypedListener_ItGetsNoOnAdded() {
            var world = new World();
            var pool = world.Pool<LsA>();
            var typed = new Typed();
            pool.AddListener(typed);
            world.AddEventListener(new Events {
                OnAdd = (_, type) => {
                    if (type == ComponentType<LsA>.Index) {
                        pool.RemoveListener(typed);
                    }
                }
            });
            var e = world.CreateEntity(new LsB());

            world.Add(e, new LsA());

            Assert.That(typed.Added, Is.Empty, "world listeners run before the OnAdded dispatch");
        }

        [Test]
        public void Add_OneShotTypedListener_NestedCreateFromWorldListener_RunsOnce() {
            var world = new World();
            var pool = world.Pool<LsA>();
            Typed once = null;
            once = new Typed { OnAdd = _ => pool.RemoveListener(once) };
            pool.AddListener(once);
            bool nested = false;
            world.AddEventListener(new Events {
                OnAdd = (_, type) => {
                    if (nested || type != ComponentType<LsA>.Index) {
                        return;
                    }
                    nested = true;
                    world.CreateEntity(new LsA { V = 2 });
                }
            });

            world.CreateEntity(new LsA { V = 1 });

            Assert.That(once.Added.Count, Is.EqualTo(1), "a listener that unsubscribes itself in its first OnAdded must run once");
        }

        [Test]
        public void Add_TypedListenerUnsubscribesTheNextOne_SameDispatchStillCallsIt() {
            var world = new World();
            var pool = world.Pool<LsA>();
            var second = new Typed();
            pool.AddListener(new Typed { OnAdd = _ => pool.RemoveListener(second) });
            pool.AddListener(second);
            var e = world.CreateEntity(new LsB());

            world.Add(e, new LsA());

            Assert.That(second.Added, Is.EqualTo(new[] { e.Index }), "a dispatch loops over the array it took when it started");
        }

        // Documented: a pool without typed listeners when the Add starts notifies none for that Add,
        // also when it has hooks (change tracking here); a listener that a filter or world listener
        // subscribes during the Add gets its first OnAdded from the next Add.
        [TestCase(false)]
        [TestCase(true)]
        public void Add_PoolWithoutTypedListeners_ListenerSubscribedDuringTheAdd_StartsWithTheNextAdd(bool trackChanges) {
            var world = new World();
            var pool = world.Pool<LsA>();
            if (trackChanges) {
                pool.TrackChanges();
            }
            var late = new Typed();
            world.Filter().Inc<LsA>().End().AddListener(new OnFilter { Enter = Once(_ => pool.AddListener(late)) });
            var a = world.CreateEntity(new LsB());
            var b = world.CreateEntity(new LsB());

            world.Add(a, new LsA());
            world.Add(b, new LsA());

            Assert.That(late.Added, Is.EqualTo(new[] { b.Index }));
        }

        [Test]
        public void Add_FilterListenerSubscribesFirstWorldListener_ItGetsTheAddition() {
            var world = new World();
            var late = new Events();
            world.Filter().Inc<LsA>().End().AddListener(new OnFilter { Enter = Once(_ => world.AddEventListener(late)) });
            var e = world.CreateEntity(new LsB());

            world.Add(e, new LsA());

            Assert.That(late.Log, Is.EqualTo(new[] { "+LsA" + e.Index }));
        }

        [Test]
        public void Remove_FilterListenerSubscribesFirstWorldListener_ItGetsTheRemoval() {
            var world = new World();
            var late = new Events();
            world.Filter().Inc<LsA>().End().AddListener(new OnFilter { Leave = Once(_ => world.AddEventListener(late)) });
            var e = world.CreateEntity(new LsB());
            world.Add(e, new LsA());

            world.Remove<LsA>(e);

            Assert.That(late.Log, Is.EqualTo(new[] { "-LsA" + e.Index }), "like an addition, a removal reaches a world listener its filter listeners subscribed");
        }

        [Test]
        public void Remove_FilterListenerSubscribesWorldListenerNextToAnother_ItGetsTheRemoval() {
            var world = new World();
            world.AddEventListener(new Events());
            var late = new Events();
            world.Filter().Inc<LsA>().End().AddListener(new OnFilter { Leave = Once(_ => world.AddEventListener(late)) });
            var e = world.CreateEntity(new LsB());
            world.Add(e, new LsA());

            world.Remove<LsA>(e);

            Assert.That(late.Log, Is.EqualTo(new[] { "-LsA" + e.Index }));
        }

        [Test]
        public void Remove_FilterListenerSubscribesFirstWorldListenerAndAddsTheComponentBack_NoRemovalAfterTheAddition() {
            var world = new World();
            var late = new Events();
            world.Filter().Inc<LsA>().End().AddListener(new OnFilter {
                Leave = Once(index => {
                    world.AddEventListener(late);
                    world.Add(world.GetEntity(index), new LsA { V = 3 });
                })
            });
            var e = world.CreateEntity(new LsB());
            world.Add(e, new LsA());

            world.Remove<LsA>(e);

            Assert.That(world.Has<LsA>(e), Is.True);
            Assert.That(late.Log, Is.EqualTo(new[] { "+LsA" + e.Index }), "the entity has the component: no removal may follow its addition");
        }

        [Test]
        public void Remove_FilterListenerSubscribesFirstWorldListenerAndReplacesTheEntity_NoRemovalForTheNewEntity() {
            var world = new World();
            var late = new Events();
            Entity spawned = Entity.Null;
            world.Filter().Inc<LsA>().End().AddListener(new OnFilter {
                Leave = Once(index => {
                    world.AddEventListener(late);
                    world.DestroyEntity(world.GetEntity(index));
                    spawned = world.CreateEntity(new LsB());
                })
            });
            var e = world.CreateEntity(new LsB());
            world.Add(e, new LsA());

            world.Remove<LsA>(e);

            Assert.That(spawned.Index, Is.EqualTo(e.Index), "test setup: the new entity takes the slot");
            Assert.That(late.Log, Has.None.EqualTo("-LsA" + e.Index), "the slot holds an entity that never had the component");
        }

        [Test]
        public void DestroyEntity_FilterListenerSubscribesFirstWorldListener_ItGetsThatRemoval() {
            var world = new World();
            var late = new Events();
            world.Filter().Inc<LsA>().End().AddListener(new OnFilter { Leave = Once(_ => world.AddEventListener(late)) });
            var e = world.CreateEntity(new LsA());
            world.Add(e, new LsB());

            world.DestroyEntity(e);

            Assert.That(late.Log, Does.Contain("-LsA" + e.Index), "the removal whose filter listener subscribed it is reported, as in a plain Remove");
        }
    }
}
