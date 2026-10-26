using System.Collections.Generic;
using NUnit.Framework;

namespace KenseiECS.Tests {
    // A struct copy of an EventBuffer<T> shares the entity's pooled list. Once the component is
    // removed the copy is stale: storing it again must never put that list on two entities, or
    // leave it both on an entity and in the static pool. One event type per test: the pool is
    // static and shared.
    [TestFixture]
    public class EventBufferOwnershipTests {
        private struct StaleAddHit { public int V; }
        private struct DeferredSetHit { public int V; }
        private struct OverwriteHit { public int V; }
        private struct RemoveSetHit { public int V; }
        private struct CopyOtherHit { public int V; }
        private struct FreshSetHit { public int V; }
        private struct TwiceHit { }
        private struct OwnListHit { public int V; }
        private struct AppendHit { public int V; }
        private struct ImmediateHit { public int V; }
        private struct InPlaceAddHit { public int V; }
        private struct InPlaceCreateHit { public int V; }
        private struct InPlaceSetHit { public int V; }
        private struct InPlaceWorldsHit { public int V; }
        private struct InPlaceForwardHit { public int V; }
        private struct PipelineHit { public int V; }
        private struct LeaseAddHit { public int V; }
        private struct LeaseSetHit { public int V; }
        private struct LeaseOwnHit { public int V; }

        private static List<T> ListOf<T>(World world, Entity entity) where T : struct =>
            world.Get<EventBuffer<T>>(entity).Values;

        private static int[] Values<T>(World world, Entity entity, System.Func<T, int> value) where T : struct {
            var list = ListOf<T>(world, entity);
            var result = new int[list?.Count ?? 0];
            for (int i = 0; i < result.Length; i++) {
                result[i] = value(list[i]);
            }
            return result;
        }

        [Test]
        public void Add_StaleCopyAfterRemove_DoesNotShareListWithNextBuffer() {
            var world = new World();
            var a = world.CreateEntity(new Position());
            world.AddEvent(a, new StaleAddHit { V = 1 });
            var copy = world.Get<EventBuffer<StaleAddHit>>(a);
            world.Remove<EventBuffer<StaleAddHit>>(a);

            world.Add(a, copy);
            var b = world.CreateEntity(new Position());
            world.AddEvent(b, new StaleAddHit { V = 2 });

            Assert.That(world.Get<EventBuffer<StaleAddHit>>(a).Count, Is.EqualTo(0), "the stale copy's events went back to the pool with its list");
            Assert.That(ListOf<StaleAddHit>(world, a), Is.Not.Null, "the stale copy is stored with an empty list of its own");
            Assert.That(ListOf<StaleAddHit>(world, a), Is.Not.SameAs(ListOf<StaleAddHit>(world, b)), "one pooled list on two entities");
            Assert.That(Values<StaleAddHit>(world, b, h => h.V), Is.EqualTo(new[] { 2 }));
            var listA = ListOf<StaleAddHit>(world, a);
            var listB = ListOf<StaleAddHit>(world, b);
            world.Remove<EventBuffer<StaleAddHit>>(a);
            world.Remove<EventBuffer<StaleAddHit>>(b);
            Assert.That(ListPool<StaleAddHit>.TimesFree(listA), Is.EqualTo(1), "each list must be in the pool once");
            Assert.That(ListPool<StaleAddHit>.TimesFree(listB), Is.EqualTo(1), "each list must be in the pool once");
            Assert.That(ListPool<StaleAddHit>.TimesFree(copy.Values), Is.EqualTo(1), "the stale copy's list must be in the pool once");
        }

        [Test]
        public void CommandBufferSet_BufferRemovedBeforePlayback_StoresNoPooledList() {
            var world = new World();
            var a = world.CreateEntity(new Position());
            world.AddEvent(a, new DeferredSetHit { V = 1 });
            // The 2.1.1 docs pattern: read-modify-write recorded in a buffer...
            var copy = world.Get<EventBuffer<DeferredSetHit>>(a);
            copy.Add(new DeferredSetHit { V = 2 });
            var commands = new CommandBuffer();
            commands.Set(a, copy);
            // ...and a OneFrame cleanup (of a nested group, say) before the Playback.
            world.Remove<EventBuffer<DeferredSetHit>>(a);

            commands.Playback(world);
            var b = world.CreateEntity(new Position());
            world.AddEvent(b, new DeferredSetHit { V = 3 });

            Assert.That(world.Has<EventBuffer<DeferredSetHit>>(a), Is.True, "Set on a missing component adds it");
            Assert.That(world.Get<EventBuffer<DeferredSetHit>>(a).Count, Is.EqualTo(0), "the stale copy carries no events");
            Assert.That(ListOf<DeferredSetHit>(world, a), Is.Not.SameAs(ListOf<DeferredSetHit>(world, b)));
            Assert.That(Values<DeferredSetHit>(world, b, h => h.V), Is.EqualTo(new[] { 3 }), "b sees only its own event");
        }

        [Test]
        public void CommandBufferSet_StaleCopyOverNewBuffer_KeepsTheEntityBuffer() {
            var world = new World();
            var a = world.CreateEntity(new Position());
            var b = world.CreateEntity(new Position());
            world.AddEvent(a, new OverwriteHit { V = 1 });
            var copy = world.Get<EventBuffer<OverwriteHit>>(a);
            var commands = new CommandBuffer();
            commands.Set(a, copy);
            world.Remove<EventBuffer<OverwriteHit>>(a);
            // Next frame: b rents a's old list, a gets a new one; the Playback comes after.
            world.AddEvent(b, new OverwriteHit { V = 3 });
            world.AddEvent(a, new OverwriteHit { V = 4 });

            commands.Playback(world);

            Assert.That(ListOf<OverwriteHit>(world, a), Is.Not.SameAs(ListOf<OverwriteHit>(world, b)), "the stale copy must not hand b's list to a");
            Assert.That(Values<OverwriteHit>(world, a, h => h.V), Is.EqualTo(new[] { 4 }), "a keeps its own buffer");
            Assert.That(Values<OverwriteHit>(world, b, h => h.V), Is.EqualTo(new[] { 3 }));
        }

        [Test]
        public void CommandBufferRemoveThenSet_InOneBuffer_StoresNoPooledList() {
            var world = new World();
            var a = world.CreateEntity(new Position());
            world.AddEvent(a, new RemoveSetHit { V = 1 });
            var copy = world.Get<EventBuffer<RemoveSetHit>>(a);
            var commands = new CommandBuffer();
            commands.Remove<EventBuffer<RemoveSetHit>>(a);
            commands.Set(a, copy);

            commands.Playback(world);
            var b = world.CreateEntity(new Position());
            world.AddEvent(b, new RemoveSetHit { V = 2 });

            Assert.That(world.Get<EventBuffer<RemoveSetHit>>(a).Count, Is.EqualTo(0));
            Assert.That(ListOf<RemoveSetHit>(world, a), Is.Not.SameAs(ListOf<RemoveSetHit>(world, b)));
            Assert.That(Values<RemoveSetHit>(world, b, h => h.V), Is.EqualTo(new[] { 2 }));
        }

        [Test]
        public void Add_CopyOfAnotherEntitysBuffer_DoesNotShareList() {
            var world = new World();
            var a = world.CreateEntity(new Position());
            var c = world.CreateEntity(new Position());
            world.AddEvent(a, new CopyOtherHit { V = 1 });

            world.Add(c, world.Get<EventBuffer<CopyOtherHit>>(a));

            Assert.That(ListOf<CopyOtherHit>(world, c), Is.Not.SameAs(ListOf<CopyOtherHit>(world, a)), "the list belongs to a");
            Assert.That(ListOf<CopyOtherHit>(world, c), Is.Not.Null, "c gets an empty list of its own");
            Assert.That(world.Get<EventBuffer<CopyOtherHit>>(c).Count, Is.EqualTo(0));
            Assert.That(Values<CopyOtherHit>(world, a, h => h.V), Is.EqualTo(new[] { 1 }), "a keeps its events");
            var listA = ListOf<CopyOtherHit>(world, a);
            var listC = ListOf<CopyOtherHit>(world, c);
            world.DestroyEntity(a);
            world.DestroyEntity(c);
            Assert.That(ListPool<CopyOtherHit>.TimesFree(listA), Is.EqualTo(1), "a's list goes back once");
            Assert.That(ListPool<CopyOtherHit>.TimesFree(listC), Is.EqualTo(1), "c's list goes back once");
        }

        [Test]
        public void CommandBufferSet_ValueWithItsOwnList_ReturnsTheReplacedList() {
            var world = new World();
            var a = world.CreateEntity(new Position());
            world.AddEvent(a, new FreshSetHit { V = 1 });
            var old = ListOf<FreshSetHit>(world, a);
            var fresh = new EventBuffer<FreshSetHit>();
            fresh.Add(new FreshSetHit { V = 2 });
            int free = ListPool<FreshSetHit>.FreeCount;
            var commands = new CommandBuffer();
            commands.Set(a, fresh);

            commands.Playback(world);

            Assert.That(Values<FreshSetHit>(world, a, h => h.V), Is.EqualTo(new[] { 2 }));
            Assert.That(ListPool<FreshSetHit>.FreeCount, Is.EqualTo(free + 1), "the replaced list goes back to the pool instead of leaking");
            Assert.That(old.Count, Is.EqualTo(0));
        }

        [Test]
        public void ListPool_SameListReturnedTwice_IsPooledOnce() {
            var list = ListPool<TwiceHit>.Rent();
            int free = ListPool<TwiceHit>.FreeCount;

            ListPool<TwiceHit>.Return(list);
            ListPool<TwiceHit>.Return(list);

            Assert.That(ListPool<TwiceHit>.FreeCount, Is.EqualTo(free + 1));
            var first = ListPool<TwiceHit>.Rent();
            var second = ListPool<TwiceHit>.Rent();
            Assert.That(first, Is.Not.SameAs(second), "the pool must not hand one list out twice");
        }

        [Test]
        public void ListAssignedByHand_IsClearedButNotPooled() {
            var world = new World();
            var mine = new List<OwnListHit> { new OwnListHit { V = 1 } };
            var e = world.CreateEntity(new EventBuffer<OwnListHit> { Values = mine });
            int free = ListPool<OwnListHit>.FreeCount;

            world.DestroyEntity(e);
            var other = world.CreateEntity(new Position());
            world.AddEvent(other, new OwnListHit { V = 2 });

            Assert.That(mine.Count, Is.EqualTo(0), "AutoReset still clears the list");
            Assert.That(ListPool<OwnListHit>.FreeCount, Is.EqualTo(free), "a list the pool did not hand out is not pooled");
            Assert.That(ListOf<OwnListHit>(world, other), Is.Not.SameAs(mine), "the caller's own list must not be handed to another entity");
        }

        [Test]
        public void StaleCopyAppendsAfterRemove_NextBufferStartsEmpty() {
            var world = new World();
            var a = world.CreateEntity(new Position());
            world.AddEvent(a, new AppendHit { V = 1 });
            var copy = world.Get<EventBuffer<AppendHit>>(a);
            world.Remove<EventBuffer<AppendHit>>(a);
            copy.Add(new AppendHit { V = 99 });

            var b = world.CreateEntity(new Position());
            world.AddEvent(b, new AppendHit { V = 2 });

            Assert.That(Values<AppendHit>(world, b, h => h.V), Is.EqualTo(new[] { 2 }), "an event appended to a free list must not reach the next buffer");
        }

        // ---- A buffer the entity already has without a list: AddEvent rents the list in place.

        // The list AddEvent rented in place had no holder, so a copy stored for another entity took
        // it (on 2.2.1 every copy did).
        [Test]
        public void AddEvent_OnBufferAddedWithoutList_CopyForAnotherEntityGetsItsOwnList() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.Add(e, new EventBuffer<InPlaceAddHit>());
            world.AddEvent(e, new InPlaceAddHit { V = 1 });
            var f = world.CreateEntity(new Position());

            world.Add(f, world.Get<EventBuffer<InPlaceAddHit>>(e));

            Assert.That(ListOf<InPlaceAddHit>(world, f), Is.Not.SameAs(ListOf<InPlaceAddHit>(world, e)), "one pooled list on two entities");
            Assert.That(world.Get<EventBuffer<InPlaceAddHit>>(f).Count, Is.EqualTo(0), "the copy of e's buffer carries none of e's events");
            Assert.That(Values<InPlaceAddHit>(world, e, h => h.V), Is.EqualTo(new[] { 1 }), "e keeps its events");
        }

        [Test]
        public void AddEvent_OnBufferCreatedWithoutList_ListIsPooledOnceAndNotHandedOutTwice() {
            var world = new World();
            var e = world.CreateEntity(new EventBuffer<InPlaceCreateHit>());
            world.AddEvent(e, new InPlaceCreateHit { V = 1 });
            var f = world.CreateEntity(new Position());
            world.Add(f, world.Get<EventBuffer<InPlaceCreateHit>>(e));
            var listOfE = ListOf<InPlaceCreateHit>(world, e);
            var listOfF = ListOf<InPlaceCreateHit>(world, f);

            world.Remove<EventBuffer<InPlaceCreateHit>>(e);
            var k = world.CreateEntity(new Position());
            world.AddEvent(k, new InPlaceCreateHit { V = 3 });

            Assert.That(listOfF, Is.Not.SameAs(listOfE));
            Assert.That(ListPool<InPlaceCreateHit>.TimesFree(listOfF), Is.EqualTo(0), "f's list must not be in the pool while f holds it");
            Assert.That(ListOf<InPlaceCreateHit>(world, k), Is.Not.SameAs(listOfF), "the next buffer must not get the list f holds");
            Assert.That(world.Get<EventBuffer<InPlaceCreateHit>>(f).Count, Is.EqualTo(0), "f does not see k's event");
            Assert.That(Values<InPlaceCreateHit>(world, k, h => h.V), Is.EqualTo(new[] { 3 }));
        }

        // The 2.1.1 pattern to clear a buffer, then the read-modify-write through a command buffer.
        [Test]
        public void AddEvent_AfterCommandBufferSetDefault_CommandBufferCopyGetsItsOwnList() {
            var world = new World();
            var commands = new CommandBuffer();
            var e = world.CreateEntity(new Position());
            world.AddEvent(e, new InPlaceSetHit { V = 1 });
            commands.Set(e, default(EventBuffer<InPlaceSetHit>));
            commands.Playback(world);
            world.AddEvent(e, new InPlaceSetHit { V = 2 });
            var f = world.CreateEntity(new Position());

            commands.Set(f, world.Get<EventBuffer<InPlaceSetHit>>(e));
            commands.Playback(world);

            Assert.That(ListOf<InPlaceSetHit>(world, f), Is.Not.SameAs(ListOf<InPlaceSetHit>(world, e)), "one pooled list on two entities");
            Assert.That(Values<InPlaceSetHit>(world, e, h => h.V), Is.EqualTo(new[] { 2 }));
            Assert.That(world.Get<EventBuffer<InPlaceSetHit>>(f).Count, Is.EqualTo(0));
        }

        // The pool is static: a copy stored in another world must not share the list either.
        [Test]
        public void AddEvent_OnBufferWithoutList_CopyInAnotherWorldGetsItsOwnList() {
            var first = new World();
            var second = new World();
            var e = first.CreateEntity(new EventBuffer<InPlaceWorldsHit>());
            first.AddEvent(e, new InPlaceWorldsHit { V = 1 });
            var g = second.CreateEntity(new Position());

            second.Add(g, first.Get<EventBuffer<InPlaceWorldsHit>>(e));

            Assert.That(ListOf<InPlaceWorldsHit>(second, g), Is.Not.SameAs(ListOf<InPlaceWorldsHit>(first, e)));
            Assert.That(second.Get<EventBuffer<InPlaceWorldsHit>>(g).Count, Is.EqualTo(0));
            Assert.That(Values<InPlaceWorldsHit>(first, e, h => h.V), Is.EqualTo(new[] { 1 }));
        }

        // Forwards a child's buffer to its parent from OnRemoved, before the list goes back to the pool.
        private sealed class ForwardToParent : IComponentListener<EventBuffer<InPlaceForwardHit>> {
            public World World;
            public Entity Parent;

            public void OnAdded(int entityIndex, ref EventBuffer<InPlaceForwardHit> component) {
            }

            public void OnRemoved(int entityIndex, ref EventBuffer<InPlaceForwardHit> component) {
                if (entityIndex != Parent.Index && !World.Has<EventBuffer<InPlaceForwardHit>>(Parent)) {
                    World.Add(Parent, component);
                }
            }
        }

        [Test]
        public void ForwardedFromOnRemoved_BufferRentedInPlace_ParentDoesNotKeepThePooledList() {
            var world = new World();
            var parent = world.CreateEntity(new Position());
            world.Pool<EventBuffer<InPlaceForwardHit>>().AddListener(new ForwardToParent { World = world, Parent = parent });
            var child = world.CreateEntity(new EventBuffer<InPlaceForwardHit>());
            world.AddEvent(child, new InPlaceForwardHit { V = 1 });
            var childList = ListOf<InPlaceForwardHit>(world, child);

            world.DestroyEntity(child);
            var parentList = ListOf<InPlaceForwardHit>(world, parent);
            var x = world.CreateEntity(new Position());
            world.AddEvent(x, new InPlaceForwardHit { V = 7 });

            Assert.That(parentList, Is.Not.SameAs(childList), "the child's list went back to the pool");
            Assert.That(ListPool<InPlaceForwardHit>.TimesFree(childList) + (ReferenceEquals(ListOf<InPlaceForwardHit>(world, x), childList) ? 1 : 0),
                Is.EqualTo(1), "the child's list is in the pool or on x, once");
            Assert.That(ListOf<InPlaceForwardHit>(world, x), Is.Not.SameAs(parentList), "x must not get the parent's list");
            Assert.That(world.Get<EventBuffer<InPlaceForwardHit>>(parent).Count, Is.EqualTo(0), "the parent does not see x's event");
        }

        // ---- A stale copy stored back by the 2.1.1 pattern, OneFrame in a nested group, Playback last.

        private sealed class RunAction : IRunSystem {
            private readonly System.Action<World> _action;

            public RunAction(System.Action<World> action) {
                _action = action;
            }

            public void Run(World world) => _action(world);
        }

        // Storing the stale copies with Values = null made the reader of the docs throw
        // NullReferenceException in every later frame; 2.2.1 shared the lists instead.
        [Test]
        public void StaleCopiesPlayedBack_ReaderOfTheDocsSeesEmptyBuffers() {
            var world = new World();
            var commands = new CommandBuffer();
            var a = world.CreateEntity(new Health { Value = 100 });
            var b = world.CreateEntity(new Health { Value = 100 });
            var buffers = world.Pool<EventBuffer<PipelineHit>>();
            var health = world.Pool<Health>();
            var targets = world.Filter().Inc<EventBuffer<PipelineHit>>().Inc<Health>().End();
            int frame = 0;
            var counts = new List<int>();

            var root = new SystemsRunner(world);
            root.Add(new RunAction(w => {
                // docs/snippets/Guides/OneFrame.cs, #read-events
                foreach (int e in targets) {
                    var hits = buffers.Get(e).Values;
                    counts.Add(hits.Count);
                    for (int i = 0; i < hits.Count; i++) {
                        health.Get(e).Value -= hits[i].V;
                    }
                }
            }), "reader");
            root.Add(new RunAction(w => {
                if (frame != 1) {
                    return;
                }
                w.AddEvent(a, new PipelineHit { V = 1 });
                w.AddEvent(b, new PipelineHit { V = 1 });
                var copyA = w.Get<EventBuffer<PipelineHit>>(a);
                copyA.Add(new PipelineHit { V = 2 });
                commands.Set(a, copyA);
                var copyB = w.Get<EventBuffer<PipelineHit>>(b);
                copyB.Add(new PipelineHit { V = 2 });
                commands.Set(b, copyB);
            }), "producer");
            var inner = new SystemsRunner(world);
            inner.Add(new RunAction(_ => { }), "noop");
            inner.OneFrame<EventBuffer<PipelineHit>>();
            root.AddGroup("inner", inner);
            root.Add(new RunAction(w => commands.Playback(w)), "playback");
            root.Init();

            frame = 1;
            root.Run();
            Assert.That(ListOf<PipelineHit>(world, a), Is.Not.Null, "a stale copy is stored with an empty list of its own");
            Assert.That(ListOf<PipelineHit>(world, a), Is.Not.SameAs(ListOf<PipelineHit>(world, b)), "one pooled list on two entities");
            Assert.That(ListPool<PipelineHit>.TimesFree(ListOf<PipelineHit>(world, a)), Is.EqualTo(0), "a's list is not free");
            Assert.That(ListPool<PipelineHit>.TimesFree(ListOf<PipelineHit>(world, b)), Is.EqualTo(0), "b's list is not free");
            for (frame = 2; frame <= 3; frame++) {
                Assert.That(() => root.Run(), Throws.Nothing, $"frame {frame}");
            }

            Assert.That(counts, Is.EqualTo(new[] { 0, 0 }), "frame 2 reads the two empty buffers, frame 3 nothing");
            Assert.That(health.Get(a.Index).Value, Is.EqualTo(100));
            Assert.That(world.Has<EventBuffer<PipelineHit>>(a), Is.False, "the nested OneFrame removed them in frame 2");
        }

        [Test]
        public void CommandBufferSet_ImmediatePlayback_KeepsListAndEvents() {
            var world = new World();
            var a = world.CreateEntity(new Position());
            world.AddEvent(a, new ImmediateHit { V = 1 });
            var list = ListOf<ImmediateHit>(world, a);
            var copy = world.Get<EventBuffer<ImmediateHit>>(a);
            copy.Add(new ImmediateHit { V = 2 });
            var commands = new CommandBuffer();
            commands.Set(a, copy);

            commands.Playback(world);

            Assert.That(ListOf<ImmediateHit>(world, a), Is.SameAs(list), "a modified copy of the entity's own buffer keeps its list");
            Assert.That(Values<ImmediateHit>(world, a, h => h.V), Is.EqualTo(new[] { 1, 2 }));
            world.Remove<EventBuffer<ImmediateHit>>(a);
            var b = world.CreateEntity(new Position());
            world.AddEvent(b, new ImmediateHit { V = 3 });
            Assert.That(Values<ImmediateHit>(world, b, h => h.V), Is.EqualTo(new[] { 3 }));
        }

        // new EventBuffer<T>().Add rents a list without a holder: the list a stale copy still holds,
        // back from the pool. Until 2.2.2 the stale copy stored meanwhile claimed it, and the new
        // buffer, played back later, got an empty list: its event ended up on the stale copy's entity.
        [Test]
        public void NewBufferWaitingForPlayback_StaleCopyStoredMeanwhile_NewBufferKeepsItsEvent() {
            var world = new World();
            var e1 = world.CreateEntity(new Position());
            var e2 = world.CreateEntity(new Position());
            world.AddEvent(e1, new LeaseAddHit { V = 1 });
            var stale = world.Get<EventBuffer<LeaseAddHit>>(e1);
            world.Remove<EventBuffer<LeaseAddHit>>(e1);
            var fresh = new EventBuffer<LeaseAddHit>();
            fresh.Add(new LeaseAddHit { V = 5 });
            Assert.That(fresh.Values, Is.SameAs(stale.Values), "test setup: the new buffer rents the list the stale copy holds");
            var commands = new CommandBuffer();
            commands.Add(e2, fresh);

            world.Add(e1, stale);
            commands.Playback(world);

            Assert.That(Values<LeaseAddHit>(world, e1, h => h.V), Is.Empty, "the stale copy is stored with an empty list");
            Assert.That(Values<LeaseAddHit>(world, e2, h => h.V), Is.EqualTo(new[] { 5 }), "the new buffer keeps its event");
            Assert.That(ListOf<LeaseAddHit>(world, e2), Is.SameAs(fresh.Values));
            Assert.That(ListOf<LeaseAddHit>(world, e1), Is.Not.SameAs(ListOf<LeaseAddHit>(world, e2)), "one pooled list on two entities");
        }

        [Test]
        public void StaleCopySetBeforeNewBuffer_InOneCommandBuffer_NewBufferKeepsItsEvent() {
            var world = new World();
            var e1 = world.CreateEntity(new Position());
            var e2 = world.CreateEntity(new Position());
            world.AddEvent(e1, new LeaseSetHit { V = 6 });
            var stale = world.Get<EventBuffer<LeaseSetHit>>(e1);
            world.Remove<EventBuffer<LeaseSetHit>>(e1);
            var commands = new CommandBuffer();
            commands.Set(e1, stale);
            var fresh = new EventBuffer<LeaseSetHit>();
            fresh.Add(new LeaseSetHit { V = 5 });
            commands.Add(e2, fresh);

            commands.Playback(world);

            Assert.That(Values<LeaseSetHit>(world, e2, h => h.V), Is.EqualTo(new[] { 5 }), "the new buffer keeps its event");
            Assert.That(Values<LeaseSetHit>(world, e1, h => h.V), Is.Empty);
            Assert.That(ListOf<LeaseSetHit>(world, e1), Is.Not.SameAs(ListOf<LeaseSetHit>(world, e2)));
        }

        // The stale copy holds the list the entity rented again (another lease of the same list): the
        // entity keeps its buffer, and copies of that buffer keep working.
        [Test]
        public void StaleCopyOfTheSameListFromAnEarlierLease_EntityKeepsItsBuffer() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.AddEvent(e, new LeaseOwnHit { V = 1 });
            var stale = world.Get<EventBuffer<LeaseOwnHit>>(e);
            world.Remove<EventBuffer<LeaseOwnHit>>(e);
            world.AddEvent(e, new LeaseOwnHit { V = 2 });
            Assert.That(ListOf<LeaseOwnHit>(world, e), Is.SameAs(stale.Values), "test setup: the entity rented the same list again");

            var commands = new CommandBuffer();
            commands.Set(e, stale);
            commands.Playback(world);
            var copy = world.Get<EventBuffer<LeaseOwnHit>>(e);
            copy.Add(new LeaseOwnHit { V = 3 });
            commands.Set(e, copy);
            commands.Playback(world);

            Assert.That(Values<LeaseOwnHit>(world, e, h => h.V), Is.EqualTo(new[] { 2, 3 }));
            var other = world.CreateEntity(new Position());
            world.Add(other, world.Get<EventBuffer<LeaseOwnHit>>(e));
            Assert.That(ListOf<LeaseOwnHit>(world, other), Is.Not.SameAs(ListOf<LeaseOwnHit>(world, e)), "a copy for another entity still gets its own list");
        }
    }
}
