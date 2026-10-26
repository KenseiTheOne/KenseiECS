using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace KenseiECS.Tests {
    // Typed listeners that remove, destroy and re-create components of their own pool from
    // OnRemoved/OnAdded: every listener must see each component instance added and removed at most
    // once, cycles must end, and a component re-added to the slot must not be taken for the old one.
    [TestFixture]
    public class ReentrantRemoveTests {
        private struct Link : IComponent {
            public int Id;
            public int Other;
        }

        private struct LinkA : IComponent {
            public int Id;
            public int Other;
        }

        private struct LinkB : IComponent {
            public int Id;
            public int Other;
        }

        private struct Held : IComponent {
            public int Id;
        }

        private struct Tag : IComponent {
        }

        private struct Side : IComponent {
        }

        // Counts OnAdded/OnRemoved per component instance (Id) and runs an optional reaction.
        // Reactions are capped so a regression ends in a failed assertion, not a stack overflow.
        private sealed class Recorder<T> : IComponentListener<T> where T : struct, IComponent {
            private readonly Func<T, int> _id;
            public readonly Dictionary<int, int> Added = new();
            public readonly Dictionary<int, int> Removed = new();
            public Action<int, T> OnRemovedAction;
            public Action<int, T> OnAddedAction;
            public int Reactions;
            public const int Cap = 50;

            public Recorder(Func<T, int> id) {
                _id = id;
            }

            public void OnAdded(int entityIndex, ref T component) {
                var copy = component;
                Count(Added, _id(copy));
                if (OnAddedAction != null && ++Reactions <= Cap) {
                    OnAddedAction(entityIndex, copy);
                }
            }

            public void OnRemoved(int entityIndex, ref T component) {
                var copy = component;
                Count(Removed, _id(copy));
                if (OnRemovedAction != null && ++Reactions <= Cap) {
                    OnRemovedAction(entityIndex, copy);
                }
            }

            private static void Count(Dictionary<int, int> counts, int id) {
                counts[id] = (counts.TryGetValue(id, out int n) ? n : 0) + 1;
            }

            public int RemovedOf(int id) => Removed.TryGetValue(id, out int n) ? n : 0;
            public int AddedOf(int id) => Added.TryGetValue(id, out int n) ? n : 0;
        }

        private static Recorder<Link> LinkRecorder() => new(c => c.Id);

        // =================================================================
        // Mutual and cyclic removals in one pool
        // =================================================================

        [Test]
        public void Remove_LinkedPairDestroyEachOther_EachListenerSeesEachLinkOnce([Values(false, true)] bool groupAndTracking) {
            var world = new World();
            var pool = world.Pool<Link>();
            if (groupAndTracking) {
                world.Group<Link, Side>();
                pool.TrackChanges();
            }
            world.Filter().Inc<Link>().End();
            world.Filter().Inc<Tag>().Exc<Link>().End();
            var l0 = LinkRecorder();
            var l1 = LinkRecorder();
            l0.OnRemovedAction = (e, c) => {
                if (c.Other < 0) {
                    return;
                }
                var partner = world.GetEntity(c.Other);
                if (world.IsAlive(partner)) {
                    world.DestroyEntity(partner);
                }
            };
            pool.AddListener(l0);
            pool.AddListener(l1);
            var bystander = world.CreateEntity(new Tag());
            var e1 = world.CreateEntity(new Tag());
            var e2 = world.CreateEntity(new Tag());
            world.Add(e1, new Link { Id = 1, Other = e2.Index });
            world.Add(e2, new Link { Id = 2, Other = e1.Index });
            world.Add(bystander, new Link { Id = 3, Other = -1 });
            if (groupAndTracking) {
                world.Add(e1, new Side());
                world.Add(e2, new Side());
                world.Add(bystander, new Side());
            }

            world.Remove<Link>(e1);

            Assert.That(l0.RemovedOf(1), Is.EqualTo(1), "the outer listener must not be notified twice for e1's Link");
            Assert.That(l0.RemovedOf(2), Is.EqualTo(1));
            Assert.That(l1.RemovedOf(1), Is.EqualTo(1));
            Assert.That(l1.RemovedOf(2), Is.EqualTo(1));
            Assert.That(world.IsAlive(e1), Is.False);
            Assert.That(world.IsAlive(e2), Is.False);
            Assert.That(world.Get<Link>(bystander).Id, Is.EqualTo(3), "the bystander keeps its Link");
            WorldInvariants.AssertConsistent(world, "after the mutual removal");
        }

        [Test]
        public void Remove_LinkedPairWithHasGuard_EndsWithoutRecursion() {
            var world = new World();
            var pool = world.Pool<Link>();
            var l0 = LinkRecorder();
            var l1 = LinkRecorder();
            l0.OnRemovedAction = (e, c) => {
                if (pool.Has(c.Other)) {
                    pool.Remove(c.Other);
                }
            };
            pool.AddListener(l0);
            pool.AddListener(l1);
            var e1 = world.CreateEntity(new Tag());
            var e2 = world.CreateEntity(new Tag());
            world.Add(e1, new Link { Id = 1, Other = e2.Index });
            world.Add(e2, new Link { Id = 2, Other = e1.Index });

            pool.Remove(e1.Index);

            Assert.That(l0.Reactions, Is.EqualTo(2), "each Link's removal reacts once; a restart would recurse without end");
            Assert.That(l0.RemovedOf(1), Is.EqualTo(1));
            Assert.That(l0.RemovedOf(2), Is.EqualTo(1));
            Assert.That(l1.RemovedOf(1), Is.EqualTo(1));
            Assert.That(l1.RemovedOf(2), Is.EqualTo(1));
            Assert.That(pool.Count, Is.EqualTo(0));
            Assert.That(world.IsAlive(e1) && world.IsAlive(e2), Is.True, "both keep their Tag");
            WorldInvariants.AssertConsistent(world, "after the mutual removal");
        }

        [Test]
        public void Remove_CycleOfThree_EachLinkRemovedOnceAndCycleEnds([Values(false, true)] bool destroy) {
            var world = new World();
            var pool = world.Pool<Link>();
            pool.TrackChanges();
            world.Filter().Inc<Link>().End();
            var l0 = LinkRecorder();
            var l1 = LinkRecorder();
            l0.OnRemovedAction = (e, c) => {
                if (c.Other < 0) {
                    return;
                }
                if (destroy) {
                    var next = world.GetEntity(c.Other);
                    if (world.IsAlive(next)) {
                        world.DestroyEntity(next);
                    }
                } else if (pool.Has(c.Other)) {
                    pool.Remove(c.Other);
                }
            };
            pool.AddListener(l0);
            pool.AddListener(l1);
            var es = new Entity[4];
            for (int i = 0; i < es.Length; i++) {
                es[i] = world.CreateEntity(new Tag());
            }
            // e0 -> e1 -> e2 -> e0; e3 is a bystander.
            world.Add(es[0], new Link { Id = 1, Other = es[1].Index });
            world.Add(es[1], new Link { Id = 2, Other = es[2].Index });
            world.Add(es[2], new Link { Id = 3, Other = es[0].Index });
            world.Add(es[3], new Link { Id = 4, Other = -1 });

            world.Remove<Link>(es[0]);

            Assert.That(l0.Reactions, Is.LessThan(Recorder<Link>.Cap), "the cycle must end");
            for (int id = 1; id <= 3; id++) {
                Assert.That(l0.RemovedOf(id), Is.EqualTo(1), $"listener 0, Link #{id}");
                Assert.That(l1.RemovedOf(id), Is.EqualTo(1), $"listener 1, Link #{id}");
            }
            Assert.That(l0.RemovedOf(4), Is.EqualTo(0), "the bystander's Link is untouched");
            Assert.That(pool.Count, Is.EqualTo(1));
            WorldInvariants.AssertConsistent(world, "after the cycle");
        }

        [Test]
        public void Remove_CycleAcrossTwoPools_EachComponentRemovedOnce([Values(false, true)] bool destroy) {
            var world = new World();
            var pa = world.Pool<LinkA>();
            var pb = world.Pool<LinkB>();
            var la = new Recorder<LinkA>(c => c.Id);
            var la2 = new Recorder<LinkA>(c => c.Id);
            var lb = new Recorder<LinkB>(c => c.Id);
            if (destroy) {
                la.OnRemovedAction = (e, c) => {
                    var next = world.GetEntity(c.Other);
                    if (world.IsAlive(next)) {
                        world.DestroyEntity(next);
                    }
                };
                lb.OnRemovedAction = (e, c) => {
                    var next = world.GetEntity(c.Other);
                    if (world.IsAlive(next)) {
                        world.DestroyEntity(next);
                    }
                };
            } else {
                la.OnRemovedAction = (e, c) => {
                    if (pb.Has(c.Other)) {
                        pb.Remove(c.Other);
                    }
                };
                lb.OnRemovedAction = (e, c) => {
                    if (pa.Has(c.Other)) {
                        pa.Remove(c.Other);
                    }
                };
            }
            pa.AddListener(la);
            pa.AddListener(la2);
            pb.AddListener(lb);
            var es = new Entity[4];
            for (int i = 0; i < es.Length; i++) {
                es[i] = world.CreateEntity(new Tag());
            }
            // A(e0) -> B(e1) -> A(e2) -> B(e3) -> A(e0)
            world.Add(es[0], new LinkA { Id = 1, Other = es[1].Index });
            world.Add(es[1], new LinkB { Id = 2, Other = es[2].Index });
            world.Add(es[2], new LinkA { Id = 3, Other = es[3].Index });
            world.Add(es[3], new LinkB { Id = 4, Other = es[0].Index });

            world.Remove<LinkA>(es[0]);

            Assert.That(la.Reactions + lb.Reactions, Is.LessThan(Recorder<LinkA>.Cap), "the cycle must end");
            Assert.That(la.RemovedOf(1), Is.EqualTo(1));
            Assert.That(la.RemovedOf(3), Is.EqualTo(1));
            Assert.That(la2.RemovedOf(1), Is.EqualTo(1));
            Assert.That(la2.RemovedOf(3), Is.EqualTo(1));
            Assert.That(lb.RemovedOf(2), Is.EqualTo(1));
            Assert.That(lb.RemovedOf(4), Is.EqualTo(1));
            Assert.That(pa.Count + pb.Count, Is.EqualTo(0));
            WorldInvariants.AssertConsistent(world, "after the cycle");
        }

        [Test]
        public void Remove_MutualAcrossPoolsOnOneEntity_EachComponentRemovedOnce() {
            var world = new World();
            var pa = world.Pool<LinkA>();
            var pb = world.Pool<LinkB>();
            var la = new Recorder<LinkA>(c => c.Id);
            var lb = new Recorder<LinkB>(c => c.Id);
            la.OnRemovedAction = (e, c) => {
                if (pb.Has(e)) {
                    pb.Remove(e);
                }
            };
            lb.OnRemovedAction = (e, c) => {
                if (pa.Has(e)) {
                    pa.Remove(e);
                }
            };
            pa.AddListener(la);
            pb.AddListener(lb);
            var e1 = world.CreateEntity(new Tag());
            world.Add(e1, new LinkA { Id = 1 });
            world.Add(e1, new LinkB { Id = 2 });

            world.Remove<LinkA>(e1);

            Assert.That(la.RemovedOf(1), Is.EqualTo(1));
            Assert.That(lb.RemovedOf(2), Is.EqualTo(1));
            Assert.That(pa.Count + pb.Count, Is.EqualTo(0));
            WorldInvariants.AssertConsistent(world, "after the mutual removal");
        }

        // =================================================================
        // Re-creation in the slot being removed
        // =================================================================

        private static World MakeHeldWorld(bool groupAndTracking, out ComponentPool<Held> pool, out Recorder<Held> l0, out Recorder<Held> l1) {
            var world = new World();
            pool = world.Pool<Held>();
            if (groupAndTracking) {
                world.Group<Held, Side>();
                pool.TrackChanges();
            }
            world.Filter().Inc<Held>().End();
            world.Filter().Inc<Tag>().Exc<Held>().End();
            l0 = new Recorder<Held>(c => c.Id);
            l1 = new Recorder<Held>(c => c.Id);
            pool.AddListener(l0);
            pool.AddListener(l1);
            return world;
        }

        [Test]
        public void Remove_ListenerRemovesAndReAdds_OuterRemoveLeavesTheNewComponent([Values(false, true)] bool groupAndTracking) {
            var world = MakeHeldWorld(groupAndTracking, out var pool, out var l0, out var l1);
            var bystander = world.CreateEntity(new Tag());
            world.Add(bystander, new Held { Id = 9 });
            var e = world.CreateEntity(new Tag());
            world.Add(e, new Held { Id = 1 });
            if (groupAndTracking) {
                world.Add(e, new Side());
                world.Add(bystander, new Side());
            }
            l0.OnRemovedAction = (idx, c) => {
                if (c.Id == 1) {
                    pool.Remove(idx);
                    world.Add(world.GetEntity(idx), new Held { Id = 2 });
                }
            };

            world.Remove<Held>(e);

            Assert.That(world.Has<Held>(e), Is.True, "the outer Remove must not remove the component a listener added afterwards");
            Assert.That(world.Get<Held>(e).Id, Is.EqualTo(2));
            Assert.That(l0.RemovedOf(1) + l1.RemovedOf(1), Is.EqualTo(2), "each listener sees the old component removed once");
            Assert.That(l1.RemovedOf(2), Is.EqualTo(0), "the new component was not removed");
            Assert.That(l0.AddedOf(2), Is.EqualTo(1));
            Assert.That(l1.AddedOf(2), Is.EqualTo(1));
            Assert.That(world.Get<Held>(bystander).Id, Is.EqualTo(9));
            WorldInvariants.AssertConsistent(world, "after the outer Remove");
        }

        [Test]
        public void Remove_ListenerRemovesReAddsAndRemovesAgain_EveryListenerSeesBothRemovals([Values(false, true)] bool groupAndTracking) {
            var world = MakeHeldWorld(groupAndTracking, out var pool, out var l0, out var l1);
            var e = world.CreateEntity(new Tag());
            world.Add(e, new Held { Id = 1 });
            if (groupAndTracking) {
                world.Add(e, new Side());
            }
            l0.OnRemovedAction = (idx, c) => {
                if (c.Id == 1) {
                    pool.Remove(idx);
                    world.Add(world.GetEntity(idx), new Held { Id = 2 });
                    pool.Remove(idx);
                }
            };

            world.Remove<Held>(e);

            Assert.That(world.Has<Held>(e), Is.False);
            Assert.That(l0.RemovedOf(1), Is.EqualTo(1));
            Assert.That(l1.RemovedOf(1), Is.EqualTo(1));
            Assert.That(l0.RemovedOf(2), Is.EqualTo(1), "the new component's removal notifies every listener, the first one included");
            Assert.That(l1.RemovedOf(2), Is.EqualTo(1));
            WorldInvariants.AssertConsistent(world, "after the outer Remove");
        }

        [Test]
        public void Remove_ListenerDestroysAndSpawnsInSameSlot_OuterRemoveLeavesTheNewEntity([Values(false, true)] bool spawnWithTag) {
            var world = MakeHeldWorld(false, out _, out var l0, out var l1);
            world.CreateEntity(new Held { Id = 9 });
            var e = world.CreateEntity(new Held { Id = 1 });
            var spawned = Entity.Null;
            l0.OnRemovedAction = (idx, c) => {
                if (c.Id != 1) {
                    return;
                }
                world.DestroyEntity(world.GetEntity(idx));
                if (spawnWithTag) {
                    spawned = world.CreateEntity(new Tag());
                    world.Add(spawned, new Held { Id = 2 });
                } else {
                    spawned = world.CreateEntity(new Held { Id = 2 });
                }
            };

            world.Remove<Held>(e);

            Assert.That(spawned.Index, Is.EqualTo(e.Index), "test setup: the replacement takes the freed slot");
            Assert.That(world.IsAlive(e), Is.False);
            Assert.That(world.IsAlive(spawned), Is.True, "the outer Remove must not destroy the entity that took the slot");
            Assert.That(world.Has<Held>(spawned), Is.True);
            Assert.That(world.Get<Held>(spawned).Id, Is.EqualTo(2));
            Assert.That(l1.RemovedOf(1), Is.EqualTo(1));
            Assert.That(l1.RemovedOf(2), Is.EqualTo(0), "the replacement's component was not removed");
            Assert.That(l0.AddedOf(2) + l1.AddedOf(2), Is.EqualTo(2));
            WorldInvariants.AssertConsistent(world, "after the outer Remove");
        }

        [Test]
        public void Remove_ListenerUnsubscribesAndDestroys_RemainingListenersStillNotified() {
            var world = new World();
            var pool = world.Pool<Link>();
            var l0 = LinkRecorder();
            var l1 = LinkRecorder();
            var l2 = LinkRecorder();
            l0.OnRemovedAction = (idx, c) => {
                pool.RemoveListener(l0);
                world.DestroyEntity(world.GetEntity(idx));
            };
            pool.AddListener(l0);
            pool.AddListener(l1);
            pool.AddListener(l2);
            var e = world.CreateEntity(new Tag());
            world.Add(e, new Link { Id = 1 });

            world.Remove<Link>(e);

            Assert.That(l0.RemovedOf(1), Is.EqualTo(1));
            Assert.That(l1.RemovedOf(1), Is.EqualTo(1), "an unsubscribe during the removal must not shift the others");
            Assert.That(l2.RemovedOf(1), Is.EqualTo(1));
            Assert.That(world.IsAlive(e), Is.False);
            WorldInvariants.AssertConsistent(world, "after the removal");
        }

        [Test]
        public void Remove_LastListenerUnsubscribesAndReAdds_OuterRemoveLeavesTheNewComponent() {
            var world = new World();
            var pool = world.Pool<Held>();
            var l0 = new Recorder<Held>(c => c.Id);
            l0.OnRemovedAction = (idx, c) => {
                if (c.Id != 1) {
                    return;
                }
                pool.RemoveListener(l0);
                pool.Remove(idx);
                world.Add(world.GetEntity(idx), new Held { Id = 2 });
            };
            pool.AddListener(l0);
            var e = world.CreateEntity(new Tag());
            world.Add(e, new Held { Id = 1 });

            world.Remove<Held>(e);

            Assert.That(world.Has<Held>(e), Is.True, "without listeners left, the nested calls must still end the outer Remove");
            Assert.That(world.Get<Held>(e).Id, Is.EqualTo(2));
            WorldInvariants.AssertConsistent(world, "after the outer Remove");

            world.Remove<Held>(e);
            Assert.That(world.Has<Held>(e), Is.False, "the pool takes the plain path again afterwards");
        }

        [Test]
        public void Remove_WorldClearedByListener_OuterRemoveStops() {
            var world = new World();
            var pool = world.Pool<Held>();
            var l0 = new Recorder<Held>(c => c.Id);
            var l1 = new Recorder<Held>(c => c.Id);
            l0.OnRemovedAction = (idx, c) => world.Clear();
            pool.AddListener(l0);
            pool.AddListener(l1);
            var e = world.CreateEntity(new Held { Id = 1 });

            Assert.That(() => world.Remove<Held>(e), Throws.Nothing);

            Assert.That(l1.RemovedOf(1), Is.EqualTo(0), "Clear removed the component without notifications; the outer Remove stops");
            Assert.That(pool.Count, Is.EqualTo(0));
            Assert.That(world.EntityCount, Is.EqualTo(0));
        }

        // =================================================================
        // Add: remaining OnAdded and the returned ref after a re-creation
        // =================================================================

        [Test]
        public void Add_TypedListenerDestroysAndSpawnsInSameSlot_RestOfAddSkipsTheNewEntity() {
            var world = new World();
            var pool = world.Pool<Held>();
            var l0 = new Recorder<Held>(c => c.Id);
            var l1 = new Recorder<Held>(c => c.Id);
            var spawned = Entity.Null;
            l0.OnAddedAction = (idx, c) => {
                if (c.Id == 1) {
                    world.DestroyEntity(world.GetEntity(idx));
                    spawned = world.CreateEntity(new Held { Id = 2 });
                }
            };
            pool.AddListener(l0);
            pool.AddListener(l1);
            var e = world.CreateEntity(new Tag());

            ref var held = ref world.Add(e, new Held { Id = 1 });
            held.Id = 77;

            Assert.That(spawned.Index, Is.EqualTo(e.Index), "test setup: the replacement takes the freed slot");
            Assert.That(l1.AddedOf(2), Is.EqualTo(1), "the replacement's component is announced once, by its own Add");
            Assert.That(l1.AddedOf(1), Is.EqualTo(0), "the destroyed component gets no further OnAdded");
            Assert.That(world.Get<Held>(spawned).Id, Is.EqualTo(2), "a write through the ref must not land on the entity that took the slot");
            WorldInvariants.AssertConsistent(world, "after Add");
        }

        [Test]
        public void Add_TypedListenerRemovesAndReAdds_LaterListenersSeeTheNewComponentOnce() {
            var world = new World();
            var pool = world.Pool<Held>();
            var l0 = new Recorder<Held>(c => c.Id);
            var l1 = new Recorder<Held>(c => c.Id);
            l0.OnAddedAction = (idx, c) => {
                if (c.Id == 1) {
                    pool.Remove(idx);
                    pool.Add(idx, new Held { Id = 2 });
                }
            };
            pool.AddListener(l0);
            pool.AddListener(l1);
            var e = world.CreateEntity(new Tag());

            ref var held = ref world.Add(e, new Held { Id = 1 });
            held.Id = 77;

            Assert.That(l1.AddedOf(2), Is.EqualTo(1), "the re-added component must not get OnAdded twice from one listener");
            Assert.That(l1.AddedOf(1), Is.EqualTo(0));
            Assert.That(l1.RemovedOf(1), Is.EqualTo(1));
            Assert.That(world.Get<Held>(e).Id, Is.EqualTo(77), "the same entity has the type again: the ref points at its component");
            WorldInvariants.AssertConsistent(world, "after Add");
        }

        private sealed class DestroyAndSpawnOnEnter : IFilterListener {
            public World World;
            public bool Armed = true;
            public Entity Spawned;

            public void OnEntityAdded(Filter filter, int entityIndex) {
                if (!Armed) {
                    return;
                }
                Armed = false;
                World.DestroyEntity(World.GetEntity(entityIndex));
                Spawned = World.CreateEntity(new Held { Id = 2 });
            }

            public void OnEntityRemoved(Filter filter, int entityIndex) {
            }
        }

        [Test]
        public void Add_FilterListenerDestroysAndSpawnsInSameSlot_RefIsScratch([Values(false, true)] bool hooked) {
            var world = new World();
            var pool = world.Pool<Held>();
            var counter = new Recorder<Held>(c => c.Id);
            if (hooked) {
                pool.AddListener(counter);
            }
            var listener = new DestroyAndSpawnOnEnter { World = world };
            world.Filter().Inc<Held>().End().AddListener(listener);
            var e = world.CreateEntity(new Tag());

            ref var held = ref world.Add(e, new Held { Id = 1 });
            held.Id = 77;

            Assert.That(listener.Spawned.Index, Is.EqualTo(e.Index), "test setup: the replacement takes the freed slot");
            Assert.That(world.Get<Held>(listener.Spawned).Id, Is.EqualTo(2), "a write through the ref must not land on the entity that took the slot");
            if (hooked) {
                Assert.That(counter.AddedOf(2), Is.EqualTo(1));
                Assert.That(counter.AddedOf(1), Is.EqualTo(0), "the destroyed component gets no OnAdded");
            }
            WorldInvariants.AssertConsistent(world, "after Add");
        }
    }
}
