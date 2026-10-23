using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace KenseiECS.Tests {
    // Tests for the 2.2.0 core additions: EventBuffer<T>.Prewarm, World/Pool/Filter
    // Reserve and Listeners<T>.Invoke. ListPool<T> is static, so every Prewarm test
    // uses its own event type to start from an empty pool.

    public struct PwFill { public int V; }
    public struct PwGrow { public int V; }
    public struct PwTwice { public int V; }
    public struct PwFrame { public int V; }
    public struct PwWorlds { public int V; }
    public struct PwPeak { public int V; }
    public struct PwLargest { public int V; }

    public struct ReserveTag : IComponent { }
    public struct ReserveLate : IComponent { public int V; }

    public interface IV220Listener {
        void On(int value);
    }

    internal sealed class RecordingV220Listener : IV220Listener {
        public readonly string Name;
        public readonly List<string> Log;
        public Action<int> Action;

        public RecordingV220Listener(string name, List<string> log) {
            Name = name;
            Log = log;
        }

        public void On(int value) {
            Log.Add(Name + value);
            Action?.Invoke(value);
        }
    }

    internal sealed class CallbackV220Listener : IV220Listener {
        public Action Callback;

        public void On(int value) {
            Callback?.Invoke();
        }
    }

    internal sealed class CountingV220Listener : IV220Listener {
        public int Sum;

        public void On(int value) {
            Sum += value;
        }
    }

    // Counts Equals calls: Invoke's fast path compares references, so every Equals is a step
    // of the search after a change.
    internal sealed class EqualsCountingV220Listener : IV220Listener {
        public static int EqualsCalls;
        public Action<EqualsCountingV220Listener> Action;
        public int Calls;

        public void On(int value) {
            Calls++;
            Action?.Invoke(this);
        }

        public override bool Equals(object obj) {
            EqualsCalls++;
            return ReferenceEquals(this, obj);
        }

        public override int GetHashCode() {
            return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
        }
    }

    // A struct listener behind the interface: Remove matches its boxed copy by value.
    internal struct StructV220Listener : IV220Listener {
        public int Id;
        public List<int> Log;

        public void On(int value) {
            Log.Add(Id);
        }
    }

    [TestFixture]
    public class V220CoreTests {
        // =================================================================
        // EventBuffer<T>.Prewarm
        // =================================================================

        [Test]
        public void Prewarm_FillsPoolWithListsOfRequestedCapacity() {
            EventBuffer<PwFill>.Prewarm(5, 16);

            Assert.That(ListPool<PwFill>.FreeCount, Is.EqualTo(5));
            var rented = new List<List<PwFill>>();
            for (int i = 0; i < 5; i++) {
                rented.Add(ListPool<PwFill>.Rent());
            }
            foreach (var list in rented) {
                Assert.That(list.Capacity, Is.GreaterThanOrEqualTo(16));
                ListPool<PwFill>.Return(list);
            }
        }

        [Test]
        public void Prewarm_GrowsFreeListsAlreadyInThePool() {
            var small = ListPool<PwGrow>.Rent();
            ListPool<PwGrow>.Return(small);
            Assert.That(small.Capacity, Is.LessThan(32));

            EventBuffer<PwGrow>.Prewarm(1, 32);

            Assert.That(ListPool<PwGrow>.FreeCount, Is.EqualTo(1), "an existing free list counts towards the requested number");
            Assert.That(small.Capacity, Is.GreaterThanOrEqualTo(32), "the existing free list must be grown");
            Assert.That(ListPool<PwGrow>.Rent(), Is.SameAs(small));
        }

        [Test]
        public void Prewarm_Repeated_AddsNothing() {
            EventBuffer<PwTwice>.Prewarm(4, 8);
            EventBuffer<PwTwice>.Prewarm(4, 8);
            EventBuffer<PwTwice>.Prewarm(2, 4);

            Assert.That(ListPool<PwTwice>.FreeCount, Is.EqualTo(4));
        }

        [Test]
        public void Prewarm_NegativeArguments_Throw() {
            Assert.Throws<ArgumentOutOfRangeException>(() => EventBuffer<PwFill>.Prewarm(-1, 4));
            Assert.Throws<ArgumentOutOfRangeException>(() => EventBuffer<PwFill>.Prewarm(4, -1));
        }

        [Test]
        public void Prewarm_FirstPeakFrameOfOneFrameBuffers_AllocatesNothing() {
#if KENSEI_DEBUG
            Assume.That(EcsProfiler.IsEnabled, Is.False, "the profiler must be off for this test");
#endif
            const int entities = 64;
            const int eventsPerEntity = 8;
            var world = new World();
            var systems = new SystemsRunner(world).OneFrame<EventBuffer<PwFrame>>();
            systems.Init();
            var targets = new Entity[entities];
            for (int i = 0; i < entities; i++) {
                targets[i] = world.CreateEntity(new Health());
            }
            world.Pool<EventBuffer<PwFrame>>().Reserve(entities);

            // One small frame runs every code path once, with a single list.
            world.AddEvent(targets[0], new PwFrame());
            systems.Run();

            EventBuffer<PwFrame>.Prewarm(entities, eventsPerEntity);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < entities; i++) {
                for (int k = 0; k < eventsPerEntity; k++) {
                    world.AddEvent(targets[i], new PwFrame { V = k });
                }
            }
            systems.Run();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.That(allocated, Is.Zero, "a prewarmed peak frame must not allocate lists or grow them");
            Assert.That(ListPool<PwFrame>.FreeCount, Is.EqualTo(entities), "OneFrame cleanup returns every list");
        }

        [Test]
        public void Prewarm_PoolIsSharedByAllWorlds() {
            EventBuffer<PwWorlds>.Prewarm(2, 4);
            var a = new World();
            var b = new World();
            var ea = a.CreateEntity(new Health());
            var eb = b.CreateEntity(new Health());

            a.AddEvent(ea, new PwWorlds());
            b.AddEvent(eb, new PwWorlds());

            Assert.That(ListPool<PwWorlds>.FreeCount, Is.EqualTo(0), "both worlds rent from the one static pool");
            Assert.That(a.Get<EventBuffer<PwWorlds>>(ea).Values, Is.Not.SameAs(b.Get<EventBuffer<PwWorlds>>(eb).Values));
        }

        [Test]
        public void Prewarm_AfterLargePeak_GrowsOnlyRequestedLists() {
            var rented = new List<List<PwPeak>>();
            for (int i = 0; i < 300; i++) {
                rented.Add(ListPool<PwPeak>.Rent());
            }
            foreach (var list in rented) {
                ListPool<PwPeak>.Return(list);
            }

            EventBuffer<PwPeak>.Prewarm(10, 64);

            Assert.That(ListPool<PwPeak>.FreeCount, Is.EqualTo(300), "enough free lists exist, none is added");
            int grown = 0;
            foreach (var list in rented) {
                if (list.Capacity >= 64) {
                    grown++;
                }
            }
            Assert.That(grown, Is.EqualTo(10), "only as many lists as requested are grown, the rest of the peak stays small");
            for (int k = 0; k < 10; k++) {
                Assert.That(ListPool<PwPeak>.FreeCapacityFromTop(k), Is.GreaterThanOrEqualTo(64), "the grown lists are handed out first");
            }
        }

        [Test]
        public void Prewarm_PrefersReadyThenLargestFreeLists() {
            var lists = new List<List<PwLargest>>();
            for (int i = 0; i < 6; i++) {
                lists.Add(ListPool<PwLargest>.Rent());
            }
            lists[0].Capacity = 100; // already large enough, deepest in the pool
            lists[1].Capacity = 40;  // the largest of the rest
            lists[4].Capacity = 8;
            foreach (var list in lists) {
                ListPool<PwLargest>.Return(list);
            }

            EventBuffer<PwLargest>.Prewarm(2, 64);

            Assert.That(lists[0].Capacity, Is.EqualTo(100), "a ready list is used as it is");
            Assert.That(lists[1].Capacity, Is.GreaterThanOrEqualTo(64), "the largest too-small list is grown");
            Assert.That(lists[4].Capacity, Is.EqualTo(8), "other lists are not touched");
            Assert.That(lists[5].Capacity, Is.EqualTo(0));
            var first = ListPool<PwLargest>.Rent();
            var second = ListPool<PwLargest>.Rent();
            Assert.That(new[] { first, second }, Is.EquivalentTo(new[] { lists[0], lists[1] }), "the prepared lists are rented first");
            ListPool<PwLargest>.Return(first);
            ListPool<PwLargest>.Return(second);

            long before = GC.GetAllocatedBytesForCurrentThread();
            EventBuffer<PwLargest>.Prewarm(2, 64);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(allocated, Is.Zero, "a repeated call finds the top lists ready");
        }

        // =================================================================
        // World.Reserve, ComponentPool<T>.Reserve, Filter.Reserve
        // =================================================================

        [Test]
        public void Reserve_CreateAndDestroyReservedEntities_AllocatesNothing() {
#if KENSEI_DEBUG
            Assume.That(EcsProfiler.IsEnabled, Is.False, "the profiler must be off for this test");
#endif
            const int n = 5000;
            var world = new World();
            var early = world.Filter().Inc<Position>().Inc<Velocity>().End();
            var positions = world.Pool<Position>();

            world.Reserve(n);

            // Created after Reserve: sparse side already sized for n entities.
            var velocities = world.Pool<Velocity>();
            var late = world.Filter().Inc<Position>().Exc<Frozen>().End();
            Assert.That(velocities.SparseCapacity, Is.GreaterThanOrEqualTo(n), "a pool created after Reserve starts with the reserved sparse size");
            Assert.That(positions.SparseCapacity, Is.GreaterThanOrEqualTo(n), "Reserve grows the sparse array of an existing pool");

            positions.Reserve(n);
            velocities.Reserve(n);
            early.Reserve(n);
            late.Reserve(n);
            world.Pool<Frozen>();

            var created = new Entity[n];
            // Run the paths once so the measured loop sees no first-call work.
            created[0] = world.CreateEntity(new Position());
            world.Add(created[0], new Velocity());
            world.DestroyEntity(created[0]);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < n; i++) {
                created[i] = world.CreateEntity(new Position { X = i });
                world.Add(created[i], new Velocity());
            }
            int matchedEarly = early.Count;
            int matchedLate = late.Count;
            for (int i = 0; i < n; i++) {
                world.DestroyEntity(created[i]);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.That(allocated, Is.Zero, "after Reserve, creating and destroying the reserved entities must not allocate");
            Assert.That(matchedEarly, Is.EqualTo(n));
            Assert.That(matchedLate, Is.EqualTo(n));
            Assert.That(world.EntityCount, Is.EqualTo(0));
        }

        [Test]
        public void ReservePerPoolAndFilter_DocsPattern_PeakLoadDoesNotGrow() {
            // docs/snippets/Guides/WorldConfig.cs#reserve-one: the filter is reserved for the
            // count of its rarest Inc component, which bounds how many entities it can hold.
            const int maxEntities = 2000;
            const int maxProjectiles = 300;
            var world = new World(new WorldConfig { InitialPoolDenseCapacity = 16 });
            world.Reserve(maxEntities);
            var positions = world.Pool<Position>();
            var projectiles = world.Pool<Velocity>();
            var filter = world.Filter().Inc<Velocity>().Inc<Position>().End();
            positions.Reserve(maxEntities);
            projectiles.Reserve(maxProjectiles);
            filter.Reserve(maxProjectiles);
            int positionsCap = positions.DenseCapacity;
            int projectilesCap = projectiles.DenseCapacity;
            int filterCap = filter.DenseCapacity;

            for (int i = 0; i < maxEntities; i++) {
                var e = world.CreateEntity(new Position());
                if (i < maxProjectiles) {
                    world.Add(e, new Velocity());
                }
            }

            Assert.That(filter.Count, Is.EqualTo(maxProjectiles));
            Assert.That(positions.DenseCapacity, Is.EqualTo(positionsCap));
            Assert.That(projectiles.DenseCapacity, Is.EqualTo(projectilesCap));
            Assert.That(filter.DenseCapacity, Is.EqualTo(filterCap), "the filter never holds more than its rarest component");
        }

        [Test]
        public void Reserve_NeverShrinksAndRejectsNegative() {
            var world = new World(new WorldConfig { InitialPoolSparseCapacity = 1024, InitialPoolDenseCapacity = 512 });
            var pool = world.Pool<Position>();
            var filter = world.Filter().Inc<Position>().End();

            world.Reserve(10);
            pool.Reserve(10);
            filter.Reserve(10);

            Assert.That(pool.SparseCapacity, Is.EqualTo(1024));
            Assert.That(pool.DenseCapacity, Is.EqualTo(512));
            Assert.That(filter.DenseCapacity, Is.EqualTo(512));
            Assert.Throws<ArgumentOutOfRangeException>(() => world.Reserve(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => pool.Reserve(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => filter.Reserve(-1));
        }

        [Test]
        public void PoolReserve_GrowsDenseAndKeepsData() {
            var world = new World();
            var pool = world.Pool<ReserveLate>();
            pool.TrackChanges();
            var e = world.CreateEntity(new ReserveLate { V = 7 });
            int version = pool.ChangedVersion(e.Index);

            pool.Reserve(1000);

            Assert.That(pool.DenseCapacity, Is.GreaterThanOrEqualTo(1000));
            Assert.That(pool.SparseCapacity, Is.GreaterThanOrEqualTo(1000));
            Assert.That(pool.Get(e.Index).V, Is.EqualTo(7));
            Assert.That(pool.ChangedVersion(e.Index), Is.EqualTo(version), "change versions move with the data");

            var many = new List<Entity>();
            for (int i = 0; i < 900; i++) {
                many.Add(world.CreateEntity(new ReserveLate { V = i }));
            }
            Assert.That(pool.ChangedSince(many[899].Index, version), Is.True, "the version array grew with the dense arrays");
        }

        [Test]
        public void FilterReserve_DuringIteration_VisitsEveryEntityOnce() {
            var world = new World();
            var filter = world.Filter().Inc<ReserveTag>().End();
            for (int i = 0; i < 3; i++) {
                world.CreateEntity(new ReserveTag());
            }

            var seen = new List<int>();
            foreach (int e in filter) {
                filter.Reserve(1000);
                seen.Add(e);
            }

            Assert.That(seen, Is.EquivalentTo(new[] { 0, 1, 2 }));
            Assert.That(filter.DenseCapacity, Is.GreaterThanOrEqualTo(1000));
        }

        [Test]
        public void WorldReserve_KeepsExistingEntitiesAndHandles() {
            var world = new World(new WorldConfig { InitialEntityCapacity = 4 });
            var a = world.CreateEntity(new Position { X = 1 });
            var b = world.CreateEntity(new Position { X = 2 });
            world.DestroyEntity(a);

            world.Reserve(10_000);

            Assert.That(world.IsAlive(a), Is.False);
            Assert.That(world.IsAlive(b), Is.True);
            Assert.That(world.Get<Position>(b).X, Is.EqualTo(2));
            var c = world.CreateEntity(new Position());
            Assert.That(c.Index, Is.EqualTo(a.Index), "the free list survives Reserve");
            Assert.That(c.Generation, Is.Not.EqualTo(a.Generation));
        }

        [Test]
        public void ReserveWithComponents_OneWorldCall_CreateDestroyAllocatesNothing() {
#if KENSEI_DEBUG
            Assume.That(EcsProfiler.IsEnabled, Is.False, "the profiler must be off for this test");
#endif
            const int n = 3000;
            var world = new World();
            // Existing before Reserve.
            var early = world.Filter().Inc<Position>().Inc<Velocity>().End();
            var positions = world.Pool<Position>();

            world.Reserve(n, n);

            // Created after Reserve: they start with the reserved sizes.
            var velocities = world.Pool<Velocity>();
            var late = world.Filter().Inc<Position>().Exc<Frozen>().End();
            world.Pool<Frozen>();
            Assert.That(positions.DenseCapacity, Is.GreaterThanOrEqualTo(n), "Reserve grows the dense arrays of an existing pool");
            Assert.That(velocities.DenseCapacity, Is.GreaterThanOrEqualTo(n), "a pool created after Reserve starts with the reserved dense size");
            Assert.That(early.DenseCapacity, Is.GreaterThanOrEqualTo(n), "Reserve grows an existing filter");
            Assert.That(late.DenseCapacity, Is.GreaterThanOrEqualTo(n), "a filter created after Reserve starts with the reserved dense size");

            var created = new Entity[n];
            created[0] = world.CreateEntity(new Position());
            world.Add(created[0], new Velocity());
            world.DestroyEntity(created[0]);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < n; i++) {
                created[i] = world.CreateEntity(new Position { X = i });
                world.Add(created[i], new Velocity());
            }
            int matchedEarly = early.Count;
            int matchedLate = late.Count;
            for (int i = 0; i < n; i++) {
                world.DestroyEntity(created[i]);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.That(allocated, Is.Zero, "one World.Reserve(n, n) covers world, pools and filters");
            Assert.That(matchedEarly, Is.EqualTo(n));
            Assert.That(matchedLate, Is.EqualTo(n));
            Assert.That(world.GrowthsAfterReserve, Is.EqualTo(0));
        }

        [Test]
        public void ReserveWithComponents_RejectsNegativeAndNeverShrinks() {
            var world = new World(new WorldConfig { InitialPoolDenseCapacity = 512 });
            var pool = world.Pool<Position>();

            world.Reserve(10, 10);

            Assert.That(pool.DenseCapacity, Is.EqualTo(512));
            Assert.That(world.Pool<Velocity>().DenseCapacity, Is.EqualTo(512), "the config wins when it is larger");
            Assert.Throws<ArgumentOutOfRangeException>(() => world.Reserve(0, -1));
        }

        [Test]
        public void Reserve_AfterDestroy_ThrowsObjectDisposed() {
            var world = new World();
            world.Destroy();

            var ex = Assert.Throws<ObjectDisposedException>(() => world.Reserve(10));
            Assert.That(ex.Message, Does.Contain("Reserve"));
            Assert.Throws<ObjectDisposedException>(() => world.Reserve(10, 10));
            Assert.Throws<ObjectDisposedException>(() => world.Filter().Inc<Position>().End());
#if KENSEI_DEBUG
            Assert.Throws<ObjectDisposedException>(() => world.CreateEntity(new Position()));
            Assert.Throws<ObjectDisposedException>(() => world.Pool<Position>());
#endif
            Assert.DoesNotThrow(() => world.Destroy(), "a repeated Destroy stays a no-op");
        }

        [Test]
        public void GrowthsAfterReserve_CountsOnlyReservedDimensions() {
            var unreserved = new World(new WorldConfig { InitialEntityCapacity = 4, InitialPoolDenseCapacity = 2 });
            for (int i = 0; i < 20; i++) {
                unreserved.CreateEntity(new Position());
            }
            Assert.That(unreserved.GrowthsAfterReserve, Is.EqualTo(0), "without Reserve growth is normal");

            var entitiesOnly = new World(new WorldConfig { InitialEntityCapacity = 4, InitialPoolDenseCapacity = 2 });
            entitiesOnly.Reserve(64);
            for (int i = 0; i < 20; i++) {
                entitiesOnly.CreateEntity(new Position());
            }
            Assert.That(entitiesOnly.GrowthsAfterReserve, Is.EqualTo(0), "dense growth is not counted when only entities were reserved");

            var tooSmall = new World(new WorldConfig { InitialEntityCapacity = 4, InitialPoolDenseCapacity = 2 });
            var filter = tooSmall.Filter().Inc<Position>().End();
            tooSmall.Reserve(8, 4);
            for (int i = 0; i < 20; i++) {
                tooSmall.CreateEntity(new Position());
            }
#if KENSEI_DEBUG
            Assert.That(tooSmall.GrowthsAfterReserve, Is.GreaterThan(0), "outgrowing the reserve is counted");
#else
            Assert.That(tooSmall.GrowthsAfterReserve, Is.EqualTo(0), "the counter only exists under KENSEI_DEBUG");
#endif
            Assert.That(filter.Count, Is.EqualTo(20));
        }

#if KENSEI_DEBUG
        [Test]
        public void GrowthAfterReserve_RecordedByProfiler() {
            Assume.That(EcsProfiler.IsEnabled, Is.False, "the profiler must be off before this test");
            var world = new World(new WorldConfig { InitialPoolDenseCapacity = 2 });
            world.Reserve(4, 2);
            var a = world.CreateEntity(new Position());
            world.CreateEntity(new Position());
            EcsProfiler.Enable(world);
            try {
                world.CreateEntity(new Position());
                var grown = EcsProfiler.GetEventsByType(ProfileEventType.StorageGrown);

                Assert.That(grown.Count, Is.EqualTo(1));
                Assert.That(grown[0].EntityIndex, Is.EqualTo(-1));
                Assert.That(grown[0].ComponentType, Is.EqualTo("Position pool dense 2 -> 4 (reserved 2)"));
                Assert.That(world.GrowthsAfterReserve, Is.EqualTo(1));
            } finally {
                EcsProfiler.Disable();
                EcsProfiler.Clear();
            }
            Assert.That(world.IsAlive(a), Is.True);
        }

        [Test]
        public void ProfilerSearch_EntityIndex_DoesNotMatchStorageGrown() {
            var grown = new ProfileEvent {
                Type = ProfileEventType.StorageGrown, EntityIndex = -1,
                ComponentType = "Position pool dense 64 -> 128 (reserved 64)"
            };
            var added = new ProfileEvent {
                Type = ProfileEventType.ComponentAdded, EntityIndex = 1, ComponentType = "Velocity"
            };

            Assert.That(grown.MatchesSearch("1"), Is.False, "neither \"-1\" nor \"128\" matches a search for entity 1");
            Assert.That(grown.MatchesSearch("12"), Is.False);
            Assert.That(grown.MatchesSearch("dense"), Is.True);
            Assert.That(grown.MatchesSearch("position"), Is.True, "a growth is still found by its text");
            Assert.That(added.MatchesSearch("1"), Is.True);
            Assert.That(added.MatchesSearch("VELO"), Is.True);
            Assert.That(added.MatchesSearch("2"), Is.False);
            Assert.That(new ProfileEvent { EntityIndex = 12, ComponentType = "Velocity" }.MatchesSearch("1"), Is.True,
                "an entity index matches by substring, as before");
        }
#endif

        // =================================================================
        // Listeners<T>.Invoke
        // =================================================================

        [Test]
        public void Invoke_CallsEveryListenerOnceInOrder() {
            var log = new List<string>();
            var listeners = new Listeners<IV220Listener>();
            listeners.Add(new RecordingV220Listener("a", log));
            listeners.Add(new RecordingV220Listener("b", log));
            listeners.Add(new RecordingV220Listener("c", log));

            listeners.Invoke(l => l.On(1));
            listeners.Invoke(2, (l, v) => l.On(v));

            Assert.That(log, Is.EqualTo(new[] { "a1", "b1", "c1", "a2", "b2", "c2" }));
        }

        [Test]
        public void Invoke_NoList_DoesNothing() {
            var listeners = new Listeners<IV220Listener>();
            int calls = 0;

            listeners.Invoke(_ => calls++);
            listeners.Invoke(1, (_, _) => calls++);

            Assert.That(calls, Is.EqualTo(0));
        }

        [Test]
        public void Invoke_RemovingLowerNeighbour_CallsEveryoneExactlyOnce() {
            var world = new World();
            var log = new List<string>();
            var a = new RecordingV220Listener("a", log);
            var b = new RecordingV220Listener("b", log);
            var c = new RecordingV220Listener("c", log);
            var e = world.CreateWithListener<IV220Listener>(a);
            world.Subscribe<IV220Listener>(e, b);
            world.Subscribe<IV220Listener>(e, c);
            // b unsubscribes a, which sits before it: reverse iteration of Values
            // would call someone twice here.
            b.Action = _ => world.Unsubscribe<IV220Listener>(e, a);

            world.Get<Listeners<IV220Listener>>(e).Invoke(1, (l, v) => l.On(v));

            Assert.That(log, Is.EqualTo(new[] { "a1", "b1", "c1" }));
            Assert.That(world.Get<Listeners<IV220Listener>>(e).Values, Is.EqualTo(new IV220Listener[] { b, c }));
        }

        // Dispatch through either overload, so every semantics test covers both.
        private static void InvokeOn(Listeners<IV220Listener> listeners, bool withArg, int value) {
            if (withArg) {
                listeners.Invoke(value, static (l, v) => l.On(v));
            } else {
                listeners.Invoke(l => l.On(value));
            }
        }

        [Test]
        public void Invoke_RemovedBeforeItsTurn_IsSkipped_AddedDuringDispatch_IsNot([Values(false, true)] bool withArg) {
            var world = new World();
            var log = new List<string>();
            var a = new RecordingV220Listener("a", log);
            var b = new RecordingV220Listener("b", log);
            var late = new RecordingV220Listener("late", log);
            var e = world.CreateWithListener<IV220Listener>(a);
            world.Subscribe<IV220Listener>(e, b);
            a.Action = _ => {
                world.Unsubscribe<IV220Listener>(e, b);
                world.Subscribe<IV220Listener>(e, late);
            };

            InvokeOn(world.Get<Listeners<IV220Listener>>(e), withArg, 1);

            Assert.That(log, Is.EqualTo(new[] { "a1" }), "b was unsubscribed before its turn; late joined during the dispatch");

            log.Clear();
            a.Action = null;
            InvokeOn(world.Get<Listeners<IV220Listener>>(e), withArg, 2);
            Assert.That(log, Is.EqualTo(new[] { "a2", "late2" }), "the next dispatch sees the changes");
        }

        [Test]
        public void Invoke_DuplicateListener_MatchedByValue_NotPerCopy([Values(false, true)] bool withArg) {
            var log = new List<string>();
            var listeners = new Listeners<IV220Listener>();
            var a = new RecordingV220Listener("a", log);
            var x = new RecordingV220Listener("x", log);
            listeners.Add(a);
            listeners.Add(x);
            listeners.Add(x);
            a.Action = _ => listeners.Remove(x);

            InvokeOn(listeners, withArg, 1);

            Assert.That(log, Is.EqualTo(new[] { "a1", "x1", "x1" }),
                "while one copy is subscribed every snapshot copy is called (documented)");

            log.Clear();
            listeners.Add(x);
            a.Action = _ => {
                listeners.Remove(x);
                listeners.Remove(x);
            };
            InvokeOn(listeners, withArg, 2);
            Assert.That(log, Is.EqualTo(new[] { "a2" }), "no copy left: none is called");
        }

        [Test]
        public void Invoke_StructListenerResubscribed_IsCalledOnce([Values(false, true)] bool withArg) {
            var log = new List<string>();
            var structLog = new List<int>();
            var listeners = new Listeners<IV220Listener>();
            var a = new RecordingV220Listener("a", log);
            listeners.Add(a);
            listeners.Add(new StructV220Listener { Id = 1, Log = structLog });
            listeners.Add(new StructV220Listener { Id = 2, Log = structLog });
            // Remove matches the boxed copy by value; the new box is another object.
            a.Action = _ => {
                listeners.Remove(new StructV220Listener { Id = 1, Log = structLog });
                listeners.Add(new StructV220Listener { Id = 1, Log = structLog });
            };

            InvokeOn(listeners, withArg, 1);

            Assert.That(structLog, Is.EqualTo(new[] { 1, 2 }),
                "a resubscribed struct counts as subscribed (like a resubscribed class instance) and keeps its snapshot turn");
        }

        [Test]
        public void Invoke_FirstOfManyUnsubscribesItself_SearchStaysShort([Values(false, true)] bool withArg) {
            const int N = 1024;
            var listeners = new Listeners<IV220Listener>();
            var all = new EqualsCountingV220Listener[N];
            for (int i = 0; i < N; i++) {
                all[i] = new EqualsCountingV220Listener();
                listeners.Add(all[i]);
            }
            all[0].Action = self => listeners.Remove(self);
            EqualsCountingV220Listener.EqualsCalls = 0;

            InvokeOn(listeners, withArg, 1);

            // List.Remove(self) compares once (self is first); the dispatch then resynchronises
            // with one step instead of searching for each of the remaining 1023 listeners.
            Assert.That(EqualsCountingV220Listener.EqualsCalls, Is.LessThanOrEqualTo(4));
            foreach (var l in all) {
                Assert.That(l.Calls, Is.EqualTo(1));
            }
        }

        [Test]
        public void Invoke_EveryListenerUnsubscribesItself_StaysLinear([Values(false, true)] bool withArg) {
            const int N = 1024;
            var listeners = new Listeners<IV220Listener>();
            var all = new EqualsCountingV220Listener[N];
            for (int i = 0; i < N; i++) {
                all[i] = new EqualsCountingV220Listener { Action = self => listeners.Remove(self) };
                listeners.Add(all[i]);
            }
            EqualsCountingV220Listener.EqualsCalls = 0;

            InvokeOn(listeners, withArg, 1);

            Assert.That(EqualsCountingV220Listener.EqualsCalls, Is.LessThanOrEqualTo(3 * N));
            Assert.That(listeners.Count, Is.EqualTo(0));
            foreach (var l in all) {
                Assert.That(l.Calls, Is.EqualTo(1));
            }
        }

        [Test]
        public void Invoke_EveryListenerInsertsAtFront_StaysLinear([Values(false, true)] bool withArg) {
            const int N = 1024;
            var listeners = new Listeners<IV220Listener>();
            var all = new EqualsCountingV220Listener[N];
            for (int i = 0; i < N; i++) {
                all[i] = new EqualsCountingV220Listener {
                    Action = _ => listeners.Values.Insert(0, new EqualsCountingV220Listener())
                };
                listeners.Add(all[i]);
            }
            EqualsCountingV220Listener.EqualsCalls = 0;

            InvokeOn(listeners, withArg, 1);

            // Each insertion shifts the rest up by one: the two-way search finds the next entry
            // a few steps from the cursor (a backward-first search made N * N / 2 comparisons).
            Assert.That(EqualsCountingV220Listener.EqualsCalls, Is.LessThanOrEqualTo(6 * N));
            Assert.That(listeners.Count, Is.EqualTo(2 * N));
            foreach (var l in all) {
                Assert.That(l.Calls, Is.EqualTo(1));
            }
        }

        [Test]
        public void Invoke_InsertBeforeCurrent_NewNotCalled_OthersOnce([Values(false, true)] bool withArg) {
            var log = new List<string>();
            var listeners = new Listeners<IV220Listener>();
            var a = new RecordingV220Listener("a", log);
            var b = new RecordingV220Listener("b", log);
            var c = new RecordingV220Listener("c", log);
            var z = new RecordingV220Listener("z", log);
            listeners.Add(a);
            listeners.Add(b);
            listeners.Add(c);
            b.Action = _ => listeners.Values.Insert(0, z);

            InvokeOn(listeners, withArg, 1);

            Assert.That(log, Is.EqualTo(new[] { "a1", "b1", "c1" }));
        }

        [Test]
        public void Invoke_RemovesListenerAhead_ItIsSkipped([Values(false, true)] bool withArg) {
            var log = new List<string>();
            var listeners = new Listeners<IV220Listener>();
            var a = new RecordingV220Listener("a", log);
            var b = new RecordingV220Listener("b", log);
            var c = new RecordingV220Listener("c", log);
            var d = new RecordingV220Listener("d", log);
            listeners.Add(a);
            listeners.Add(b);
            listeners.Add(c);
            listeners.Add(d);
            b.Action = _ => {
                listeners.Remove(b);
                listeners.Remove(c);
            };

            InvokeOn(listeners, withArg, 1);

            Assert.That(log, Is.EqualTo(new[] { "a1", "b1", "d1" }));
        }

        [Test]
        public void Invoke_SelfUnsubscribe_OthersStillCalledOnce([Values(false, true)] bool withArg) {
            var log = new List<string>();
            var listeners = new Listeners<IV220Listener>();
            var a = new RecordingV220Listener("a", log);
            var b = new RecordingV220Listener("b", log);
            var c = new RecordingV220Listener("c", log);
            listeners.Add(a);
            listeners.Add(b);
            listeners.Add(c);
            b.Action = _ => listeners.Remove(b);

            InvokeOn(listeners, withArg, 1);

            Assert.That(log, Is.EqualTo(new[] { "a1", "b1", "c1" }));
            Assert.That(listeners.Values, Is.EqualTo(new IV220Listener[] { a, c }));
        }

        [Test]
        public void Invoke_ResubscribedBeforeItsTurn_IsCalledOnce([Values(false, true)] bool withArg) {
            var log = new List<string>();
            var listeners = new Listeners<IV220Listener>();
            var a = new RecordingV220Listener("a", log);
            var b = new RecordingV220Listener("b", log);
            var c = new RecordingV220Listener("c", log);
            listeners.Add(a);
            listeners.Add(b);
            listeners.Add(c);
            // a moves b to the end and itself too: both stay subscribed, nobody runs twice.
            a.Action = _ => {
                listeners.Remove(b);
                listeners.Add(b);
                listeners.Remove(a);
                listeners.Add(a);
            };

            InvokeOn(listeners, withArg, 1);

            Assert.That(log, Is.EqualTo(new[] { "a1", "b1", "c1" }), "a re-subscribed listener is still subscribed at its turn; its new position does not call it again");
            Assert.That(listeners.Values, Is.EqualTo(new IV220Listener[] { c, b, a }));
        }

        [Test]
        public void Invoke_DestroyEntityDuringDispatch_SkipsTheRest([Values(false, true)] bool withArg) {
            var world = new World();
            var log = new List<string>();
            var a = new RecordingV220Listener("a", log);
            var b = new RecordingV220Listener("b", log);
            var e = world.CreateWithListener<IV220Listener>(a);
            world.Subscribe<IV220Listener>(e, b);
            a.Action = _ => world.DestroyEntity(e);

            InvokeOn(world.Get<Listeners<IV220Listener>>(e), withArg, 1);

            Assert.That(log, Is.EqualTo(new[] { "a1" }), "removing the component clears the list, so b is no longer subscribed");
            Assert.That(world.IsAlive(e), Is.False);
        }

        [Test]
        public void Invoke_NestedDispatchUnsubscribes_OuterSkipsIt([Values(false, true)] bool withArg) {
            var log = new List<string>();
            var listeners = new Listeners<IV220Listener>();
            var a = new RecordingV220Listener("a", log);
            var b = new RecordingV220Listener("b", log);
            var c = new RecordingV220Listener("c", log);
            listeners.Add(a);
            listeners.Add(b);
            listeners.Add(c);
            int depth = 0;
            a.Action = v => {
                if (depth++ == 0) {
                    InvokeOn(listeners, withArg, v + 10);
                }
            };
            b.Action = v => {
                if (v == 11) {
                    listeners.Remove(c);
                }
            };

            InvokeOn(listeners, withArg, 1);

            Assert.That(log, Is.EqualTo(new[] { "a1", "a11", "b11", "b1" }), "the inner dispatch removed c before either level reached it");
        }

        [Test]
        public void Invoke_OnErrorThrows_PropagatesAndReturnsSnapshot([Values(false, true)] bool withArg) {
            var log = new List<string>();
            var listeners = new Listeners<IV220Listener>();
            var a = new RecordingV220Listener("a", log) { Action = _ => throw new InvalidOperationException("call") };
            var b = new RecordingV220Listener("b", log);
            listeners.Add(a);
            listeners.Add(b);

            TestDelegate dispatch = withArg
                ? () => listeners.Invoke(1, static (l, v) => l.On(v), static (_, _, ex) => throw new ArgumentException("handler", ex))
                : () => listeners.Invoke(l => l.On(1), static (_, ex) => throw new ArgumentException("handler", ex));
            var thrown = Assert.Throws<ArgumentException>(dispatch);

            Assert.That(thrown.InnerException?.Message, Is.EqualTo("call"));
            Assert.That(log, Is.EqualTo(new[] { "a1" }), "a throwing onError stops the dispatch");
            var snapshot = System.Buffers.ArrayPool<IV220Listener>.Shared.Rent(2);
            System.Buffers.ArrayPool<IV220Listener>.Shared.Return(snapshot);
            Assert.That(snapshot[0], Is.Null, "the snapshot is cleared and returned even then");
        }

        [Test]
        public void Invoke_WithUnsubscribeDuringDispatch_AllocatesNothing() {
            var listeners = new Listeners<IV220Listener>();
            var counters = new CountingV220Listener[4];
            var mover = new CallbackV220Listener();
            listeners.Add(mover);
            for (int i = 0; i < counters.Length; i++) {
                counters[i] = new CountingV220Listener();
                listeners.Add(counters[i]);
            }
            // Every dispatch moves a listener that has not had its turn yet to the front, so the
            // list differs from the snapshot and the search path runs for the rest.
            var moved = counters[2];
            var values = listeners.Values;
            mover.Callback = () => {
                values.Remove(moved);
                values.Insert(0, moved);
            };
            DispatchCounting(listeners);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) {
                DispatchCounting(listeners);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.That(allocated, Is.Zero, "the subscription check after a change searches the list without allocating");
            Assert.That(counters[3].Sum, Is.GreaterThan(0));
        }

        private static void DispatchCounting(Listeners<IV220Listener> listeners) {
            listeners.Invoke(1, static (l, v) => l.On(v));
            listeners.Invoke(static l => l.On(1));
        }

        [Test]
        public void Invoke_Nested_EachLevelCallsItsOwnSnapshot() {
            var log = new List<string>();
            var listeners = new Listeners<IV220Listener>();
            var a = new RecordingV220Listener("a", log);
            var b = new RecordingV220Listener("b", log);
            listeners.Add(a);
            listeners.Add(b);
            int depth = 0;
            a.Action = v => {
                if (depth++ == 0) {
                    listeners.Invoke(v + 10, (l, x) => l.On(x));
                }
            };

            listeners.Invoke(1, (l, x) => l.On(x));

            Assert.That(log, Is.EqualTo(new[] { "a1", "a11", "b11", "b1" }));
        }

        [Test]
        public void Invoke_WithoutOnError_PropagatesAndStops() {
            var log = new List<string>();
            var listeners = new Listeners<IV220Listener>();
            var a = new RecordingV220Listener("a", log);
            var b = new RecordingV220Listener("b", log);
            listeners.Add(a);
            listeners.Add(b);
            a.Action = _ => throw new InvalidOperationException("boom");

            var ex = Assert.Throws<InvalidOperationException>(() => listeners.Invoke(1, (l, v) => l.On(v)));

            Assert.That(ex.Message, Is.EqualTo("boom"));
            Assert.That(log, Is.EqualTo(new[] { "a1" }), "the dispatch stops at the throwing listener");

            a.Action = null;
            log.Clear();
            listeners.Invoke(l => l.On(2));
            Assert.That(log, Is.EqualTo(new[] { "a2", "b2" }), "a later dispatch works normally");
        }

        [Test]
        public void Invoke_WithOnError_ReportsAndContinues() {
            var log = new List<string>();
            var listeners = new Listeners<IV220Listener>();
            var a = new RecordingV220Listener("a", log);
            var b = new RecordingV220Listener("b", log);
            var c = new RecordingV220Listener("c", log);
            listeners.Add(a);
            listeners.Add(b);
            listeners.Add(c);
            a.Action = _ => throw new InvalidOperationException("a failed");
            c.Action = _ => throw new ArgumentException("c failed");
            var errors = new List<(IV220Listener, Exception)>();

            listeners.Invoke(1, (l, v) => l.On(v), (l, _, ex) => errors.Add((l, ex)));
            listeners.Invoke(l => l.On(2), (l, ex) => errors.Add((l, ex)));

            Assert.That(log, Is.EqualTo(new[] { "a1", "b1", "c1", "a2", "b2", "c2" }));
            Assert.That(errors.Count, Is.EqualTo(4));
            Assert.That(errors[0].Item1, Is.SameAs(a));
            Assert.That(errors[0].Item2, Is.TypeOf<InvalidOperationException>());
            Assert.That(errors[1].Item1, Is.SameAs(c));
            Assert.That(errors[1].Item2, Is.TypeOf<ArgumentException>());
        }

        [Test]
        public void Invoke_WithArgAndStaticLambda_AllocatesNothing() {
            var listeners = new Listeners<IV220Listener>();
            var counters = new CountingV220Listener[4];
            for (int i = 0; i < counters.Length; i++) {
                counters[i] = new CountingV220Listener();
                listeners.Add(counters[i]);
            }
            // The first call caches the static lambda's delegate and rents the snapshot array.
            Dispatch(listeners);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) {
                Dispatch(listeners);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.That(allocated, Is.Zero, "the snapshot comes from ArrayPool and the static lambda is cached");
            Assert.That(counters[3].Sum, Is.EqualTo(101));
        }

        private static void Dispatch(Listeners<IV220Listener> listeners) {
            listeners.Invoke(1, static (l, v) => l.On(v));
        }

        [Test]
        public void Invoke_WithArgAndStaticOnError_PassesArgAndAllocatesNothing() {
            var listeners = new Listeners<IV220Listener>();
            var log = new List<string>();
            var failing = new RecordingV220Listener("f", log) { Action = _ => throw new InvalidOperationException("boom") };
            listeners.Add(failing);
            listeners.Add(new CountingV220Listener());
            var state = new ErrorState();
            // Warm up: the first call caches both static delegates and rents the snapshot.
            DispatchWithErrors(listeners, state);
            Assert.That(state.LastArg, Is.SameAs(state), "onError must receive the dispatch argument");
            Assert.That(state.Errors, Is.EqualTo(1));

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) {
                DispatchWithErrors(listeners, state);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            // The thrown exceptions themselves allocate; count only what happens without one.
            listeners.Remove(failing);
            before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) {
                DispatchWithErrors(listeners, state);
            }
            long allocatedNoThrow = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.That(allocatedNoThrow, Is.Zero, "a static onError reading the argument needs no closure");
            Assert.That(allocated, Is.GreaterThan(0), "sanity: the throwing pass allocates the exceptions");
            Assert.That(state.Errors, Is.EqualTo(101));
        }

        private sealed class ErrorState {
            public int Errors;
            public ErrorState LastArg;
        }

        private static void DispatchWithErrors(Listeners<IV220Listener> listeners, ErrorState state) {
            listeners.Invoke(state, static (l, _) => l.On(1), static (_, s, _) => {
                s.Errors++;
                s.LastArg = s;
            });
        }

        [Test]
        public void Invoke_ReturnsClearedSnapshot() {
            var listeners = new Listeners<IV220Listener>();
            listeners.Add(new CountingV220Listener());
            listeners.Add(new CountingV220Listener());
            // ArrayPool hands the array the dispatch returned back to the next Rent
            // on this thread, so peek at it after the dispatch.
            listeners.Invoke(0, static (l, v) => l.On(v));
            var snapshot = System.Buffers.ArrayPool<IV220Listener>.Shared.Rent(2);
            System.Buffers.ArrayPool<IV220Listener>.Shared.Return(snapshot);

            Assert.That(snapshot[0], Is.Null, "a returned snapshot must not keep listeners alive");
            Assert.That(snapshot[1], Is.Null);
        }

        // =================================================================
        // Listeners<T> lists stay unpooled (2.2.0 design item A4 declined)
        // =================================================================

        [Test]
        public void Listeners_WriteBackOfCopyAfterRemove_DoesNotShareListWithNewSubscriber() {
            var world = new World();
            var log = new List<string>();
            var e = world.CreateEntity(new Position());
            world.Subscribe<IV220Listener>(e, new RecordingV220Listener("a", log));

            var copy = world.Get<Listeners<IV220Listener>>(e);
            world.Remove<Listeners<IV220Listener>>(e);
            world.Add(e, copy);
            var other = world.CreateWithListener<IV220Listener>(new RecordingV220Listener("b", log));

            Assert.That(world.Get<Listeners<IV220Listener>>(e).Values,
                Is.Not.SameAs(world.Get<Listeners<IV220Listener>>(other).Values),
                "a list dropped by AutoReset must never be handed to another entity");
            Assert.That(world.Get<Listeners<IV220Listener>>(e).Count, Is.EqualTo(0));
        }
    }
}
