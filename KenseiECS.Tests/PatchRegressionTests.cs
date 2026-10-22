using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;

namespace KenseiECS.Tests {
    // Regression tests for the 2.1.1 fixes, plus tests that pin down current
    // behavior the audit found undocumented.

    public struct OverwriteTracked : IComponent, IAutoReset<OverwriteTracked> {
        public static int ResetCalls;
        public int V;

        public void AutoReset(ref OverwriteTracked c) {
            ResetCalls++;
            c.V = 0;
        }
    }

    public struct CopyTrigger : IComponent {
    }

    public struct CopyTarget : IComponent, IAutoReset<CopyTarget>, IAutoCopy<CopyTarget> {
        public static int ResetCalls;
        public int V;

        public void AutoReset(ref CopyTarget c) {
            ResetCalls++;
            c.V = 0;
        }

        public void AutoCopy(ref CopyTarget c) {
        }
    }

    // A user component holding a list, cleared on AutoReset, with no AutoCopy.
    public struct ItemsComponent : IComponent, IAutoReset<ItemsComponent> {
        public List<int> Items;

        public void AutoReset(ref ItemsComponent c) {
            c.Items?.Clear();
        }
    }

    internal sealed class CountingHealthListener : IComponentListener<Health> {
        public int Added;
        public int Removed;
        public float LastAddedValue = float.NaN;

        public void OnAdded(int entityIndex, ref Health component) {
            Added++;
            LastAddedValue = component.Value;
        }

        public void OnRemoved(int entityIndex, ref Health component) {
            Removed++;
        }
    }

    [TestFixture]
    public class ComponentPoolReentrancyTests {
        private sealed class DestroyOnRemove : IComponentListener<Health> {
            public World World;
            public int Removed;

            public void OnAdded(int entityIndex, ref Health component) {
            }

            public void OnRemoved(int entityIndex, ref Health component) {
                Removed++;
                World.DestroyEntity(World.GetEntity(entityIndex));
            }
        }

        [Test]
        public void Remove_ListenerDestroysOwnEntity_PoolStaysConsistent([Values(false, true)] bool trackChanges) {
            var world = new World();
            var pool = world.Pool<Health>();
            if (trackChanges) {
                pool.TrackChanges();
            }
            var destroyer = new DestroyOnRemove { World = world };
            var counter = new CountingHealthListener();
            pool.AddListener(destroyer);
            pool.AddListener(counter);

            var first = world.CreateEntity(new Health { Value = 1 });
            var victim = world.CreateEntity(new Health { Value = 2 });
            world.Add(victim, new Position());
            var last = world.CreateEntity(new Health { Value = 3 });

            world.Remove<Health>(victim);

            Assert.That(destroyer.Removed, Is.EqualTo(1), "the destroying listener must see OnRemoved exactly once");
            Assert.That(counter.Removed, Is.EqualTo(1), "a listener after the destroying one must still see OnRemoved exactly once");
            Assert.That(world.IsAlive(victim), Is.False, "the entity must be destroyed");
            Assert.That(pool.Count, Is.EqualTo(2), "only one Health must leave the pool");
            Assert.That(world.Get<Health>(first).Value, Is.EqualTo(1), "other components must keep their values");
            Assert.That(world.Get<Health>(last).Value, Is.EqualTo(3), "other components must keep their values");
            Assert.That(world.Pool<Position>().Count, Is.EqualTo(0), "the destroyed entity's other components must be removed");
            Assert.That(world.EntityCount, Is.EqualTo(2), "two entities must remain");
        }

        [Test]
        public void Remove_ListenerDestroysOwnEntity_SlotReusedCleanly() {
            var world = new World();
            var pool = world.Pool<Health>();
            pool.AddListener(new DestroyOnRemove { World = world });
            var victim = world.CreateEntity(new Health { Value = 2 });
            world.Add(victim, new Position());

            world.Remove<Health>(victim);
            var reused = world.CreateEntity(new Position());

            Assert.That(reused.Index, Is.EqualTo(victim.Index), "the freed slot is reused");
            Assert.That(world.Has<Health>(reused), Is.False, "the new occupant must not inherit Health");
            Assert.That(() => world.Add(reused, new Health { Value = 5 }), Throws.Nothing, "Add on the reused slot must succeed");
        }

        private sealed class DestroyOnPositionRemove : IComponentListener<Position> {
            public World World;
            public int Removed;

            public void OnAdded(int entityIndex, ref Position component) {
            }

            public void OnRemoved(int entityIndex, ref Position component) {
                Removed++;
                World.DestroyEntity(World.GetEntity(entityIndex));
            }
        }

        [Test]
        public void Remove_ListenerDestroysOwnEntity_OwningGroupStaysAligned() {
            var world = new World();
            var group = world.Group<Position, Velocity>();
            var listener = new DestroyOnPositionRemove { World = world };
            world.Pool<Position>().AddListener(listener);
            var entities = new List<Entity>();
            for (int i = 0; i < 4; i++) {
                var e = world.CreateEntity(new Position { X = i });
                world.Add(e, new Velocity { X = i });
                world.Add(e, new Health { Value = i });
                entities.Add(e);
            }

            world.Remove<Position>(entities[1]);

            Assert.That(listener.Removed, Is.EqualTo(1), "OnRemoved must fire once");
            Assert.That(world.IsAlive(entities[1]), Is.False, "the entity must be destroyed");
            Assert.That(group.Count, Is.EqualTo(3), "the group must lose exactly one member");
            var pos = world.Pool<Position>();
            var vel = world.Pool<Velocity>();
            for (int i = 0; i < group.Count; i++) {
                int e = group.Entities[i];
                Assert.That(pos.RawEntities[i], Is.EqualTo(e), "Position dense array must stay aligned with the group");
                Assert.That(vel.RawEntities[i], Is.EqualTo(e), "Velocity dense array must stay aligned with the group");
                Assert.That(pos.Get(e).X, Is.EqualTo(vel.Get(e).X), "each member must keep its own data");
            }
        }

        // "Exactly one selected": adding Selected to one entity removes it from the
        // others, which swap-removes inside the same pool while Add is in progress.
        private sealed class SingleSelection : WorldListenerBase {
            public World World;

            public override void OnComponentAdded(int entityIndex, int typeIndex) {
                if (typeIndex != ComponentType<Health>.Index) {
                    return;
                }
                var pool = World.Pool<Health>();
                var entities = pool.RawEntities;
                for (int i = pool.Count - 1; i >= 0; i--) {
                    if (entities[i] != entityIndex) {
                        pool.Remove(entities[i]);
                    }
                }
            }
        }

        [Test]
        public void Add_ListenerRemovesSameTypeElsewhere_ReturnsRefToOwnSlot([Values(false, true)] bool hooked) {
            var world = new World();
            var counter = new CountingHealthListener();
            if (hooked) {
                world.Pool<Health>().AddListener(counter);
            }
            var other = world.CreateEntity(new Health { Value = 1 });
            world.Add(other, new Position());
            var e = world.CreateEntity(new Position());
            world.AddEventListener(new SingleSelection { World = world });

            ref var health = ref world.Add(e, new Health { Value = 5 });
            health.Value = 42;

            Assert.That(world.Has<Health>(other), Is.False, "the listener removed the other Health");
            Assert.That(world.Get<Health>(e).Value, Is.EqualTo(42), "the returned ref must point at the entity's own component");
            if (hooked) {
                Assert.That(counter.LastAddedValue, Is.EqualTo(5), "typed listeners must see the added component, not a moved or cleared slot");
            }
        }

        // Removes the Health just added, then gives Health to a bystander: the freed
        // dense slot goes to the bystander, which a stale ref from Add would overwrite.
        private sealed class RemoveSelfOnAdd : WorldListenerBase {
            public World World;
            public Entity Bystander;
            private bool _done;

            public override void OnComponentAdded(int entityIndex, int typeIndex) {
                if (typeIndex != ComponentType<Health>.Index || _done) {
                    return;
                }
                _done = true;
                World.Pool<Health>().Remove(entityIndex);
                World.Add(Bystander, new Health { Value = 1 });
            }
        }

        [Test]
        public void Add_ListenerRemovesTheAddedComponent_RefDoesNotTouchOthers([Values(false, true)] bool hooked) {
            var world = new World();
            var counter = new CountingHealthListener();
            if (hooked) {
                world.Pool<Health>().AddListener(counter);
            }
            var other = world.CreateEntity(new Health { Value = 1 });
            var bystander = world.CreateEntity(new Position());
            var e = world.CreateEntity(new Position());
            world.AddEventListener(new RemoveSelfOnAdd { World = world, Bystander = bystander });
            counter.Added = 0;

            ref var health = ref world.Add(e, new Health { Value = 5 });
            health.Value = 42;

            Assert.That(world.Has<Health>(e), Is.False, "the listener removed the component again");
            Assert.That(world.Get<Health>(other).Value, Is.EqualTo(1), "a write through the returned ref must not land on another entity");
            Assert.That(world.Get<Health>(bystander).Value, Is.EqualTo(1), "a write through the returned ref must not land on the entity that took the freed slot");
            if (hooked) {
                Assert.That(counter.Added, Is.EqualTo(1), "typed OnAdded must fire for the bystander only, not for the component already removed");
                Assert.That(counter.LastAddedValue, Is.EqualTo(1), "the one OnAdded call is the bystander's");
            }
        }
    }

    [TestFixture]
    public class FilterReentrancyTests {
        private sealed class ReAddPositionOnLeave : IFilterListener {
            public World World;
            public bool Armed = true;

            public void OnEntityAdded(Filter filter, int entityIndex) {
            }

            public void OnEntityRemoved(Filter filter, int entityIndex) {
                if (Armed) {
                    Armed = false;
                    World.Add(World.GetEntity(entityIndex), new Position { X = 7 });
                }
            }
        }

        [Test]
        public void Remove_FilterListenerReAddsComponent_LaterIncludeFiltersKeepEntity() {
            var world = new World();
            var first = world.Filter().Inc<Position>().End();
            var second = world.Filter().Inc<Position>().Inc<Velocity>().End();
            first.AddListener(new ReAddPositionOnLeave { World = world });
            var e = world.CreateEntity(new Position());
            world.Add(e, new Velocity());

            world.Remove<Position>(e);

            Assert.That(world.Has<Position>(e), Is.True, "the listener re-added Position");
            Assert.That(first.Contains(e.Index), Is.True, "the filter whose listener re-added must contain the entity");
            Assert.That(second.Contains(e.Index), Is.True, "a later Inc filter must not drop an entity that has the component again");
        }
    }

    [TestFixture]
    public class WorldLifecyclePatchTests {
        private sealed class ThrowOnFirstAdd : IComponentListener<Health> {
            public bool Armed = true;

            public void OnAdded(int entityIndex, ref Health component) {
                if (Armed) {
                    Armed = false;
                    throw new InvalidOperationException("listener failed");
                }
            }

            public void OnRemoved(int entityIndex, ref Health component) {
            }
        }

        [Test]
        public void Warmup_ListenerThrows_DummyEntityIsDestroyed() {
            var world = new World();
            var filter = world.Filter().Inc<Health>().End();
            world.Pool<Position>();
            world.Pool<Health>().AddListener(new ThrowOnFirstAdd());

            Assert.That(() => world.Warmup(), Throws.InvalidOperationException, "the listener exception must surface");

            Assert.That(world.EntityCount, Is.EqualTo(0), "the temporary entity must not survive a failed warmup");
            Assert.That(filter.Count, Is.EqualTo(0), "filters must not keep the temporary entity");
            Assert.That(world.Pool<Health>().Count, Is.EqualTo(0), "pools must not keep the temporary entity's components");
            var e = world.CreateEntity(new Position());
            Assert.That(world.EntityCount, Is.EqualTo(1), "the world must stay usable");
            Assert.That(world.IsAlive(e), Is.True, "the world must stay usable");
        }

        [Test]
        public void Destroy_Twice_IsNoOp() {
            var world = new World();
            world.CreateEntity(new Position());
            world.Destroy();
            Assert.That(() => world.Destroy(), Throws.Nothing, "a second Destroy must be a no-op");
            Assert.That(world.IsDestroyed, Is.True, "the world stays destroyed");
        }
    }

    [TestFixture]
    public class OverwriteTests {
        [Test]
        public void CommandBufferSet_Overwrite_IsSeenByChangeTracking() {
            var world = new World();
            var pool = world.Pool<Health>();
            pool.TrackChanges();
            var e = world.CreateEntity(new Health { Value = 1 });
            int version = world.ChangeVersion;

            var buffer = new CommandBuffer();
            buffer.Set(e, new Health { Value = 2 });
            buffer.Playback(world);

            Assert.That(world.Get<Health>(e).Value, Is.EqualTo(2), "Set must overwrite the value");
            Assert.That(pool.ChangedSince(e.Index, version), Is.True, "an overwrite through CommandBuffer.Set must count as a change");
        }

        // Set keeps the 2.1.0 contract: a plain write, no AutoReset of the old value,
        // because the new value is often a copy of the old one sharing its references.
        [Test]
        public void CommandBufferSet_Overwrite_DoesNotResetOldValue() {
            var world = new World();
            var e = world.CreateEntity(new OverwriteTracked { V = 1 });
            int resetsBefore = OverwriteTracked.ResetCalls;

            var buffer = new CommandBuffer();
            buffer.Set(e, new OverwriteTracked { V = 2 });
            buffer.Playback(world);

            Assert.That(OverwriteTracked.ResetCalls, Is.EqualTo(resetsBefore), "Set must not run AutoReset on the overwritten value");
            Assert.That(world.Get<OverwriteTracked>(e).V, Is.EqualTo(2), "Set must overwrite the value");
        }

        private struct RmwHit { public int V; }

        [Test]
        public void CommandBufferSet_ReadModifyWrite_EventBuffer_KeepsEventsAndOwnList() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.AddEvent(e, new RmwHit { V = 1 });
            var buffer = world.Get<EventBuffer<RmwHit>>(e);
            buffer.Add(new RmwHit { V = 2 });

            var commands = new CommandBuffer();
            commands.Set(e, buffer);
            commands.Playback(world);

            var list = world.Get<EventBuffer<RmwHit>>(e).Values;
            Assert.That(list.Count, Is.EqualTo(2), "Set of a modified copy must keep its events");
            var other = world.CreateEntity(new Position());
            world.AddEvent(other, new RmwHit { V = 3 });
            Assert.That(world.Get<EventBuffer<RmwHit>>(other).Values, Is.Not.SameAs(list), "the list must not go back to the pool while the entity still owns it");
        }

        private interface IRmwListener {
        }

        private sealed class RmwListener : IRmwListener {
        }

        [Test]
        public void CommandBufferSet_ReadModifyWrite_Listeners_KeepsSubscribers() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.Subscribe<IRmwListener>(e, new RmwListener());
            var listeners = world.Get<Listeners<IRmwListener>>(e);
            listeners.Add(new RmwListener());

            var commands = new CommandBuffer();
            commands.Set(e, listeners);
            commands.Playback(world);

            Assert.That(world.Get<Listeners<IRmwListener>>(e).Count, Is.EqualTo(2), "Set of a modified copy must keep its subscribers");
        }

        [Test]
        public void CommandBufferSet_ReadModifyWrite_UserAutoReset_KeepsItems() {
            var world = new World();
            var e = world.CreateEntity(new ItemsComponent { Items = new List<int> { 1 } });
            var items = world.Get<ItemsComponent>(e);
            items.Items.Add(2);

            var commands = new CommandBuffer();
            commands.Set(e, items);
            commands.Playback(world);

            Assert.That(world.Get<ItemsComponent>(e).Items, Is.EqualTo(new[] { 1, 2 }), "Set of a modified copy must keep the shared list's items");
        }

        [Test]
        public void CommandBufferSet_OnMissingComponent_AddsWithoutReset() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            int resetsBefore = OverwriteTracked.ResetCalls;

            var buffer = new CommandBuffer();
            buffer.Set(e, new OverwriteTracked { V = 3 });
            buffer.Playback(world);

            Assert.That(OverwriteTracked.ResetCalls, Is.EqualTo(resetsBefore), "adding through Set resets nothing");
            Assert.That(world.Get<OverwriteTracked>(e).V, Is.EqualTo(3), "Set must add a missing component");
        }

        // Adds TTarget to every entity that gets TTrigger — makes CopyEntity meet a
        // destination that already has the component it is about to copy.
        private sealed class AddTargetOnAdd<TTrigger, TTarget> : IComponentListener<TTrigger>
            where TTrigger : struct, IComponent
            where TTarget : struct, IComponent {
            public World World;
            public int VersionAfterAdd;

            public void OnAdded(int entityIndex, ref TTrigger component) {
                var entity = World.GetEntity(entityIndex);
                if (!World.Has<TTarget>(entity)) {
                    World.Add(entity, default(TTarget));
                    VersionAfterAdd = World.ChangeVersion;
                }
            }

            public void OnRemoved(int entityIndex, ref TTrigger component) {
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int RegisterCopyTrigger() =>
            ComponentType<CopyTrigger>.Index;

        [Test]
        public void CopyEntity_DestinationAlreadyHasComponent_OverwriteIsTrackedAndReset() {
            // CopyEntity walks component types in index order, so the trigger type
            // must be registered first; both types are used by this test only.
            int triggerIndex = RegisterCopyTrigger();
            var world = new World();
            var pool = world.Pool<CopyTarget>();
            Assert.That(triggerIndex, Is.LessThan(pool.TypeIndex), "test setup: the trigger type must have the lower index");
            pool.TrackChanges();
            var listener = new AddTargetOnAdd<CopyTrigger, CopyTarget> { World = world };
            var source = world.CreateEntity(new CopyTarget { V = 9 });
            world.Add(source, new CopyTrigger());
            world.Pool<CopyTrigger>().AddListener(listener);
            int resetsBefore = CopyTarget.ResetCalls;

            var copy = world.CopyEntity(source);

            Assert.That(world.Get<CopyTarget>(copy).V, Is.EqualTo(9), "the copy must hold the source value");
            Assert.That(CopyTarget.ResetCalls - resetsBefore, Is.EqualTo(1), "the value the listener added must go through AutoReset before being overwritten");
            Assert.That(pool.ChangedVersion(copy.Index), Is.GreaterThan(listener.VersionAfterAdd), "the overwrite must be visible to change tracking");
        }
    }

    [TestFixture]
    public class EventBufferPatchTests {
        private struct CopyHit { public int V; }
        private struct PoolHit { public int V; }
        private struct ClearHit { public int V; }
        private struct SharedHit { public int V; }

        [Test]
        public void CopyEntity_EventBuffer_CopyGetsOwnList() {
            var world = new World();
            var systems = new SystemsRunner(world).OneFrame<EventBuffer<CopyHit>>();
            systems.Init();
            var source = world.CreateEntity(new Position());
            world.AddEvent(source, new CopyHit { V = 1 });

            var copy = world.CopyEntity(source);

            Assert.That(world.Get<EventBuffer<CopyHit>>(copy).Values, Is.Not.SameAs(world.Get<EventBuffer<CopyHit>>(source).Values),
                "the copy must own a separate list");
            Assert.That(world.Get<EventBuffer<CopyHit>>(copy).Count, Is.EqualTo(1), "the copy must hold the source events");
            world.AddEvent(copy, new CopyHit { V = 2 });
            Assert.That(world.Get<EventBuffer<CopyHit>>(source).Count, Is.EqualTo(1), "an event on the copy must not reach the source");

            systems.Run();
            var a = world.CreateEntity(new Position());
            var b = world.CreateEntity(new Position());
            world.AddEvent(a, new CopyHit { V = 3 });
            world.AddEvent(b, new CopyHit { V = 4 });

            Assert.That(world.Get<EventBuffer<CopyHit>>(a).Values, Is.Not.SameAs(world.Get<EventBuffer<CopyHit>>(b).Values),
                "after OneFrame the pool must not hand one list to two entities");
            Assert.That(world.Get<EventBuffer<CopyHit>>(a).Count, Is.EqualTo(1), "events must not mix between entities");
            Assert.That(world.Get<EventBuffer<CopyHit>>(b).Count, Is.EqualTo(1), "events must not mix between entities");
        }

        [Test]
        public void DestroyEntity_ReturnsListToPool() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.AddEvent(e, new PoolHit { V = 1 });
            var list = world.Get<EventBuffer<PoolHit>>(e).Values;

            world.DestroyEntity(e);
            var next = world.CreateEntity(new Position());
            world.AddEvent(next, new PoolHit { V = 2 });

            Assert.That(list.Count, Is.EqualTo(1), "the returned list is reused for the next buffer");
            Assert.That(world.Get<EventBuffer<PoolHit>>(next).Values, Is.SameAs(list), "DestroyEntity must return the list to the pool");
        }

        [Test]
        public void WorldClear_ReturnsListToPool() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.AddEvent(e, new ClearHit { V = 1 });
            var list = world.Get<EventBuffer<ClearHit>>(e).Values;

            world.Clear();
            Assert.That(list.Count, Is.EqualTo(0), "Clear must empty the list it returns");
            var next = world.CreateEntity(new Position());
            world.AddEvent(next, new ClearHit { V = 2 });

            Assert.That(world.Get<EventBuffer<ClearHit>>(next).Values, Is.SameAs(list), "World.Clear must return the list to the pool");
        }

        [Test]
        public void ListPool_IsSharedBetweenWorlds() {
            var first = new World();
            var second = new World();
            var e1 = first.CreateEntity(new Position());
            first.AddEvent(e1, new SharedHit { V = 1 });
            var list = first.Get<EventBuffer<SharedHit>>(e1).Values;
            first.DestroyEntity(e1);

            var e2 = second.CreateEntity(new Position());
            second.AddEvent(e2, new SharedHit { V = 2 });

            Assert.That(second.Get<EventBuffer<SharedHit>>(e2).Values, Is.SameAs(list),
                "the list pool is static: a list returned by one world is rented by another");
        }
    }

    [TestFixture]
    public class ListenersPatchTests {
        private interface IPing {
            void Ping();
        }

        private sealed class Ping : IPing {
            public readonly List<string> Log;
            public readonly string Name;
            public Action OnPing;

            public Ping(List<string> log, string name) {
                Log = log;
                Name = name;
            }

            void IPing.Ping() {
                Log.Add(Name);
                OnPing?.Invoke();
            }
        }

        [Test]
        public void CopyEntity_Listeners_CopyGetsOwnList() {
            var world = new World();
            var log = new List<string>();
            IPing a = new Ping(log, "a");
            var source = world.CreateEntity(new Position());
            world.Subscribe(source, a);

            var copy = world.CopyEntity(source);

            Assert.That(world.Get<Listeners<IPing>>(copy).Values, Is.Not.SameAs(world.Get<Listeners<IPing>>(source).Values),
                "the copy must own a separate list");
            Assert.That(world.Get<Listeners<IPing>>(copy).Count, Is.EqualTo(1), "the copy must hold the source listeners");
            world.Remove<Listeners<IPing>>(copy);
            Assert.That(world.Get<Listeners<IPing>>(source).Count, Is.EqualTo(1), "clearing the copy must not touch the source");
            world.Unsubscribe(source, a);
            Assert.That(world.IsAlive(copy), Is.True, "copy stays alive through Position");
        }

        // Listener lists are not pooled (as in 2.1.0): a copy of Values kept past
        // Remove stays an empty list of its own and never sees another entity's listeners.
        [Test]
        public void RemovedListenerList_IsNotReusedByAnotherEntity() {
            var world = new World();
            var log = new List<string>();
            var first = world.CreateEntity(new Position());
            world.Subscribe<IPing>(first, new Ping(log, "a"));
            var list = world.Get<Listeners<IPing>>(first).Values;

            world.Remove<Listeners<IPing>>(first);
            var second = world.CreateEntity(new Position());
            world.Subscribe<IPing>(second, new Ping(log, "b"));

            Assert.That(world.Get<Listeners<IPing>>(second).Values, Is.Not.SameAs(list), "a removed listener list must not be handed to another entity");
            Assert.That(list.Count, Is.EqualTo(0), "the removed list stays cleared");
        }

        [Test]
        public void DestroyEntity_ClearsListenerList() {
            var world = new World();
            var log = new List<string>();
            var e = world.CreateEntity(new Position());
            world.Subscribe<IPing>(e, new Ping(log, "a"));
            var list = world.Get<Listeners<IPing>>(e).Values;

            world.DestroyEntity(e);

            Assert.That(list.Count, Is.EqualTo(0), "DestroyEntity must clear the listener list");
        }

        // Pins down 2.1.x behavior: reverse iteration only protects against
        // self-unsubscribe. A callback that unsubscribes a neighbour with a lower
        // index shifts the list, so one listener runs twice and another not at all.
        [Test]
        public void ReverseIteration_NeighbourUnsubscribed_CurrentBehavior() {
            var world = new World();
            var log = new List<string>();
            var a = new Ping(log, "a");
            var b = new Ping(log, "b");
            var c = new Ping(log, "c");
            var e = world.CreateEntity(new Position());
            world.Subscribe<IPing>(e, a);
            world.Subscribe<IPing>(e, b);
            world.Subscribe<IPing>(e, c);
            c.OnPing = () => world.Unsubscribe<IPing>(e, a);

            var values = world.Get<Listeners<IPing>>(e).Values;
            for (int i = values.Count - 1; i >= 0; i--) {
                values[i].Ping();
            }

            Assert.That(log, Is.EqualTo(new[] { "c", "c", "b" }), "documented 2.1.x behavior: c runs twice, a is skipped");
        }
    }

    [TestFixture]
    public class ParallelRunnerAbortTests {
        // Throws on the first chunk; the others are slow, so workers are still
        // handing out chunks when the failure stops the run.
        private struct RecordingJob : IRangeJob {
            public ConcurrentBag<int> Starts;

            public void Execute(int start, int end) {
                Starts.Add(start);
                if (start == 0) {
                    throw new InvalidOperationException("job failed");
                }
                Thread.Sleep(5);
            }
        }

        [Test]
        public void Run_JobThrows_NoChunkRunsTwiceOrWithNegativeStart() {
            using var runner = new ParallelRunner(2);
            var starts = new ConcurrentBag<int>();

            Assert.That(() => runner.Run(new RecordingJob { Starts = starts }, 1024 * 32, 1024), Throws.InvalidOperationException,
                "the job exception must surface");

            var all = starts.ToArray();
            Assert.That(all.Where(s => s < 0), Is.Empty, "no chunk may start at a negative index: " + string.Join(",", all));
            Assert.That(all.Length, Is.EqualTo(all.Distinct().Count()), "no chunk may run twice: " + string.Join(",", all.OrderBy(s => s)));
            Assert.That(all.Length, Is.LessThan(32), "chunks after the failure must not all be executed");
        }
    }

    [TestFixture]
    public class InlineRunnerContractTests {
        private sealed class CountingRun : IRunSystem {
            public int Runs;

            public void Run(World world) =>
                Runs++;
        }

        [Test]
        public void SetActiveByIndex_OnUnnamedChild_DisablesSlotAndChild() {
            var world = new World();
            var system = new CountingRun();
            var child = new SystemsRunner(world).Add(system);
            var root = new SystemsRunner(world).Add(child);
            root.Init();

            root.SetActive(0, false);
            root.Run();

            Assert.That(system.Runs, Is.EqualTo(0), "a disabled slot must skip the inline group");
            Assert.That(root.GetSystemInfo(0).IsEnabled, Is.False, "the parent reports the slot as disabled");
            Assert.That(child.IsEnabled, Is.False, "2.2.0: SetActive(int) on an inline group also switches child.IsEnabled");

            root.SetActive(0, true);
            root.Run();
            Assert.That(system.Runs, Is.EqualTo(1), "re-enabling the slot must run the group again");
        }

        // Pins down 2.1.x behavior: OneFrame cleanup is part of the runner's own
        // Run, so a disabled runner keeps its OneFrame components until re-enabled.
        [Test]
        public void DisabledChild_OneFrameNotCleaned_CurrentBehavior([Values(false, true)] bool named) {
            var world = new World();
            var child = new SystemsRunner(world).OneFrame<Damage>();
            var root = named
                ? new SystemsRunner(world).AddPhase("phase", child)
                : new SystemsRunner(world).Add(child);
            root.Init();
            var e = world.CreateEntity(new Position());

            if (named) {
                root.SetActive("phase", false);
            } else {
                root.SetActive(0, false);
            }
            world.Add(e, new Damage { Value = 1 });
            root.Run();
            if (named) {
                child.Run();
            }

            Assert.That(world.Has<Damage>(e), Is.True, "2.1.x: a disabled runner does not clean its OneFrame components");

            if (named) {
                root.SetActive("phase", true);
                child.Run();
            } else {
                root.SetActive(0, true);
                root.Run();
            }
            Assert.That(world.Has<Damage>(e), Is.False, "the first enabled Run cleans them");
        }
    }
}
