using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace KenseiECS.Tests {
    // The fast paths of Add/Remove and of the OneFrame cleanup, against their general paths: a type no
    // filter mentions, a type whose filters have no listeners, and the same with filter, world and typed
    // listeners present. Each fast path must leave the world exactly as the general path does.
    [TestFixture]
    public class StructuralFastPathTests {
        private struct FpEvent : IComponent, IAutoReset<FpEvent> {
            public static readonly List<int> Resets = new();
            // Runs inside AutoReset with the number of resets so far: a reset that changes the world.
            public static Action<int> OnReset;
            public int Id;

            public void AutoReset(ref FpEvent c) {
                Resets.Add(c.Id);
                c.Id = 0;
                OnReset?.Invoke(Resets.Count);
            }
        }

        private struct FpPlain : IComponent {
            public int Id;
        }

        private struct FpTag : IComponent {
        }

        private struct FpOther : IComponent {
            public int V;
        }

        public enum Mode {
            NoFilter,
            QuietFilters,
            FilterListener,
            WorldListener,
            TypedListener
        }

        private sealed class LogWorldListener : WorldListenerBase {
            public readonly List<string> Log = new();
            public override void OnEntityDestroyed(int entityIndex) => Log.Add("destroyed " + entityIndex);
            public override void OnComponentAdded(int entityIndex, int typeIndex) => Log.Add($"added {entityIndex}:{typeIndex}");
            public override void OnComponentRemoved(int entityIndex, int typeIndex) => Log.Add($"removed {entityIndex}:{typeIndex}");
        }

        private sealed class LogFilterListener : IFilterListener {
            public readonly List<string> Log = new();
            public Action<int> OnRemoved;
            public void OnEntityAdded(Filter filter, int entityIndex) => Log.Add("in " + entityIndex);

            public void OnEntityRemoved(Filter filter, int entityIndex) {
                Log.Add("out " + entityIndex);
                OnRemoved?.Invoke(entityIndex);
            }
        }

        private sealed class LogTypedListener<T> : IComponentListener<T> where T : struct, IComponent {
            public readonly List<string> Log = new();
            public void OnAdded(int entityIndex, ref T component) => Log.Add("added " + entityIndex);
            public void OnRemoved(int entityIndex, ref T component) => Log.Add("removed " + entityIndex);
        }

        private sealed class Scene {
            public World World;
            public Filter[] Filters;
            public LogWorldListener WorldLog;
            public LogFilterListener FilterLog;
            public LogTypedListener<FpEvent> TypedLog;
            public List<Entity> Entities = new();
        }

        // Entities 0, 3, 6, ... hold only the event (the cleanup auto-destroys them), the others also FpOther.
        private static Scene Build(Mode mode, int count) {
            var s = new Scene { World = new World() };
            var world = s.World;
            world.Pool<FpOther>();
            if (mode != Mode.NoFilter) {
                s.Filters = new[] {
                    world.Filter().Inc<FpEvent>().End(),
                    world.Filter().Inc<FpOther>().Exc<FpEvent>().End(),
                    world.Filter().Any<FpEvent>().Any<FpTag>().End(),
                    world.Filter().Inc<FpEvent>().Inc<FpOther>().End()
                };
            } else {
                s.Filters = new[] { world.Filter().Inc<FpOther>().End() };
            }
            if (mode == Mode.FilterListener) {
                s.FilterLog = new LogFilterListener();
                s.Filters[0].AddListener(s.FilterLog);
            }
            if (mode == Mode.WorldListener) {
                s.WorldLog = new LogWorldListener();
                world.AddEventListener(s.WorldLog);
            }
            if (mode == Mode.TypedListener) {
                s.TypedLog = new LogTypedListener<FpEvent>();
                world.Pool<FpEvent>().AddListener(s.TypedLog);
            }
            for (int i = 0; i < count; i++) {
                var e = world.CreateEntity(new FpEvent { Id = i + 1 });
                if (i % 3 != 0) {
                    world.Add(e, new FpOther { V = i });
                }
                s.Entities.Add(e);
            }
            return s;
        }

        // The cleanup as it was written before the fast path: Remove over the entity array and the count
        // taken at the start, from the last dense slot down.
        private static void ReferenceCleanup<T>(World world) where T : struct, IComponent {
            var pool = world.Pool<T>();
            var entities = pool.RawEntities;
            int count = pool.Count;
            for (int i = count - 1; i >= 0; i--) {
                pool.Remove(entities[i]);
            }
        }

        private static string Describe(Scene s) {
            var world = s.World;
            var sb = new StringBuilder();
            for (int i = 0; i < world._nextIndex; i++) {
                sb.Append(i).Append(world._alive[i] ? " alive" : " dead").Append(" gen ").Append(world._generations[i]);
                if (world._alive[i]) {
                    var e = world.GetEntity(i);
                    if (world.Has<FpEvent>(e)) {
                        sb.Append(" ev=").Append(world.Get<FpEvent>(e).Id);
                    }
                    if (world.Has<FpOther>(e)) {
                        sb.Append(" other=").Append(world.Get<FpOther>(e).V);
                    }
                    if (world.Has<FpTag>(e)) {
                        sb.Append(" tag");
                    }
                }
                sb.Append('\n');
            }
            foreach (var f in s.Filters) {
                sb.Append(f).Append(": ").Append(string.Join(",", f.Entities.ToArray().OrderBy(x => x))).Append('\n');
            }
            var pool = world.Pool<FpEvent>();
            sb.Append("dense: ").Append(string.Join(",", pool.RawEntities.Take(pool.Count))).Append('\n');
            sb.Append("data: ").Append(string.Join(",", pool.RawData.Take(pool.Count).Select(c => c.Id))).Append('\n');
            sb.Append("resets: ").Append(string.Join(",", FpEvent.Resets)).Append('\n');
            if (s.WorldLog != null) {
                sb.Append("world: ").Append(string.Join(";", s.WorldLog.Log)).Append('\n');
            }
            if (s.FilterLog != null) {
                sb.Append("filter: ").Append(string.Join(";", s.FilterLog.Log)).Append('\n');
            }
            if (s.TypedLog != null) {
                sb.Append("typed: ").Append(string.Join(";", s.TypedLog.Log)).Append('\n');
            }
            return sb.ToString();
        }

        private static string RunCleanup(Mode mode, bool reference, Action<Scene> arrange = null) {
            FpEvent.Resets.Clear();
            var s = Build(mode, 10);
            arrange?.Invoke(s);
            if (reference) {
                ReferenceCleanup<FpEvent>(s.World);
            } else {
                s.World.Pool<FpEvent>().RemoveAll();
            }
            WorldInvariants.AssertConsistent(s.World, $"after the {(reference ? "reference" : "pool")} cleanup ({mode})");
            return Describe(s);
        }

        public enum ResetEffect {
            SubscribesTypedListener,
            SubscribesWorldListener,
            SubscribesFilterListener,
            AddsOtherComponent
        }

        // AutoReset is the one user code the in-place cleanup runs. One that changes the world anyway
        // (it should only reset the value) must leave it as the Remove loop does: the in-place path
        // finishes the removal it is in and hands the rest to Remove once a listener appeared.
        [Test]
        public void OneFrameCleanup_AutoResetChangesTheWorld_MatchesRemoveLoop(
            [Values(Mode.NoFilter, Mode.QuietFilters)] Mode mode, [Values] ResetEffect effect) {
            string Run(bool reference) {
                Scene scene = null;
                FpEvent.OnReset = n => {
                    if (n != 3) {
                        return;
                    }
                    switch (effect) {
                        case ResetEffect.SubscribesTypedListener:
                            scene.World.Pool<FpEvent>().AddListener(scene.TypedLog);
                            break;
                        case ResetEffect.SubscribesWorldListener:
                            scene.World.AddEventListener(scene.WorldLog);
                            break;
                        case ResetEffect.SubscribesFilterListener:
                            scene.World.Filter().Inc<FpEvent>().End().AddListener(scene.FilterLog);
                            break;
                        default:
                            foreach (var e in scene.Entities) {
                                if (scene.World.IsAlive(e) && !scene.World.Has<FpOther>(e)) {
                                    scene.World.Add(e, new FpOther { V = 100 });
                                }
                            }
                            break;
                    }
                };
                try {
                    return RunCleanup(mode, reference, s => {
                        scene = s;
                        s.TypedLog ??= new LogTypedListener<FpEvent>();
                        s.WorldLog ??= new LogWorldListener();
                        s.FilterLog ??= new LogFilterListener();
                    });
                } finally {
                    FpEvent.OnReset = null;
                }
            }

            string expected = Run(true);
            string actual = Run(false);
            Assert.That(actual, Is.EqualTo(expected), "the cleanup must leave the world as Remove over every component does");
        }

        // MoveNext on a default enumerator (not from GetEnumerator) throws, in every build: on IL2CPP the
        // array read without bounds and null checks would crash the process instead.
        [Test]
        public void FilterEnumerator_Default_MoveNextThrows() {
            Assert.That(() => {
                var enumerator = default(Filter.Enumerator);
                enumerator.MoveNext();
            }, Throws.TypeOf<IndexOutOfRangeException>());
        }

        [Test]
        public void OneFrameCleanup_MatchesRemoveLoop([Values] Mode mode) {
            string expected = RunCleanup(mode, true);
            string actual = RunCleanup(mode, false);
            Assert.That(actual, Is.EqualTo(expected), "the cleanup must leave the world as Remove over every component does");
        }

        [Test]
        public void OneFrameCleanup_QuietPath_RemovesEverythingInReverseDenseOrder() {
            FpEvent.Resets.Clear();
            var s = Build(Mode.QuietFilters, 10);
            var world = s.World;
            s.World.Pool<FpEvent>().RemoveAll();

            Assert.That(world.Pool<FpEvent>().Count, Is.Zero, "every event must be removed");
            Assert.That(FpEvent.Resets, Is.EqualTo(Enumerable.Range(1, 10).Reverse().ToList()), "AutoReset once per component, last dense slot first");
            for (int i = 0; i < 10; i++) {
                Assert.That(world.IsAlive(s.Entities[i]), Is.EqualTo(i % 3 != 0), $"entity {i}: an event-only entity is auto-destroyed, the others stay");
            }
            Assert.That(s.Filters[0].Count, Is.Zero, "Inc<FpEvent> must be empty");
            Assert.That(s.Filters[1].Count, Is.EqualTo(6), "Inc<FpOther> Exc<FpEvent> must take back every survivor");
            Assert.That(s.Filters[2].Count, Is.Zero, "Any<FpEvent, FpTag> must be empty");
            Assert.That(world.Pool<FpEvent>().RawData.Take(10).All(c => c.Id == 0), Is.True, "freed slots must hold default");
        }

        [Test]
        public void OneFrameCleanup_FilterListenerAddsAndRemovesEvents_MatchesRemoveLoop() {
            // A filter listener of the cleanup adds the event to a fresh entity and removes it from one the
            // cleanup has not reached yet: only Remove's own checks give the result.
            void Arrange(Scene s) {
                int calls = 0;
                s.FilterLog.OnRemoved = entityIndex => {
                    calls++;
                    if (calls == 1) {
                        s.World.CreateEntity(new FpEvent { Id = 100 });
                        s.World.Remove<FpEvent>(s.Entities[1]);
                    } else if (calls == 3) {
                        s.World.Add(s.Entities[4], new FpTag());
                    }
                };
            }

            string expected = RunCleanup(Mode.FilterListener, true, Arrange);
            string actual = RunCleanup(Mode.FilterListener, false, Arrange);
            Assert.That(actual, Is.EqualTo(expected), "a cleanup whose listeners change the pool must behave as Remove over every component");
        }

        [Test]
        public void OneFrameCleanup_WorldListenerSubscribedLater_TakesGeneralPath() {
            FpEvent.Resets.Clear();
            var s = Build(Mode.QuietFilters, 6);
            var log = new LogWorldListener();
            s.World.AddEventListener(log);
            s.World.Pool<FpEvent>().RemoveAll();
            Assert.That(log.Log.Count(x => x.StartsWith("removed")), Is.EqualTo(6), "every removal must reach the world listener");
            Assert.That(log.Log.Count(x => x.StartsWith("destroyed")), Is.EqualTo(2), "every auto-destroy must reach the world listener");
        }

        [Test]
        public void OneFrameCleanup_EmptyPool_DoesNothing() {
            var world = new World();
            world.Pool<FpEvent>().RemoveAll();
            Assert.That(world.Pool<FpEvent>().Count, Is.Zero);
        }

        [Test]
        public void OneFrameRunner_QuietAndListened_SameResult() {
            foreach (bool listened in new[] { false, true }) {
                var world = new World();
                var filter = world.Filter().Inc<FpPlain>().End();
                var log = new LogFilterListener();
                if (listened) {
                    filter.AddListener(log);
                }
                var keep = world.CreateEntity(new FpOther());
                world.Add(keep, new FpPlain { Id = 1 });
                var gone = world.CreateEntity(new FpPlain { Id = 2 });
                var runner = new SystemsRunner(world).OneFrame<FpPlain>();
                runner.Init();
                runner.Run();
                Assert.That(world.Has<FpPlain>(keep), Is.False, $"listened={listened}: the event must be cleaned");
                Assert.That(world.IsAlive(keep), Is.True, $"listened={listened}: an entity with other components stays");
                Assert.That(world.IsAlive(gone), Is.False, $"listened={listened}: an event-only entity is auto-destroyed");
                Assert.That(filter.Count, Is.Zero, $"listened={listened}: the filter must be empty");
                Assert.That(log.Log.Count, Is.EqualTo(listened ? 4 : 0), $"listened={listened}: two entries and two exits");
                WorldInvariants.AssertConsistent(world, $"after the runner cleanup (listened={listened})");
            }
        }

        // ---- Add ----

        private static World WorldFor(Mode mode, out LogWorldListener worldLog) {
            var world = new World(new WorldConfig {
                InitialEntityCapacity = 4, InitialPoolSparseCapacity = 2, InitialPoolDenseCapacity = 1, InitialPoolCount = 4
            });
            worldLog = null;
            world.Pool<FpOther>();
            if (mode != Mode.NoFilter) {
                var f = world.Filter().Inc<FpPlain>().End();
                world.Filter().Inc<FpOther>().Exc<FpPlain>().End();
                if (mode == Mode.FilterListener) {
                    f.AddListener(new LogFilterListener());
                }
            }
            if (mode == Mode.WorldListener) {
                worldLog = new LogWorldListener();
                world.AddEventListener(worldLog);
            }
            if (mode == Mode.TypedListener) {
                world.Pool<FpPlain>().AddListener(new LogTypedListener<FpPlain>());
            }
            return world;
        }

        [Test]
        public void Add_RefPointsAtStoredComponent_WithGrowth([Values] Mode mode) {
            var world = WorldFor(mode, out _);
            var entities = new List<Entity>();
            for (int i = 0; i < 40; i++) {
                var e = world.CreateEntity(new FpOther { V = i });
                ref var c = ref world.Add(e, new FpPlain { Id = 1 });
                c.Id = 1000 + i;
                entities.Add(e);
            }
            for (int i = 0; i < 40; i++) {
                Assert.That(world.Get<FpPlain>(entities[i]).Id, Is.EqualTo(1000 + i), $"{mode}: a write through Add's ref must land in entity {i}'s component");
            }
            WorldInvariants.AssertConsistent(world, $"after growing Adds ({mode})");
        }

        [Test]
        public void Add_Duplicate_ThrowsAndChangesNothing([Values] Mode mode) {
            var world = WorldFor(mode, out _);
            var a = world.CreateEntity(new FpPlain { Id = 1 });
            var b = world.CreateEntity(new FpPlain { Id = 2 });
            Assert.Throws<InvalidOperationException>(() => world.Add(a, new FpPlain { Id = 9 }), $"{mode}: a second Add must throw");
            Assert.That(world.Get<FpPlain>(a).Id, Is.EqualTo(1), $"{mode}: the existing component stays");
            Assert.That(world.Get<FpPlain>(b).Id, Is.EqualTo(2), $"{mode}: the other entity is untouched");
            Assert.That(world.Pool<FpPlain>().Count, Is.EqualTo(2), $"{mode}: the pool count is unchanged");
            WorldInvariants.AssertConsistent(world, $"after a duplicate Add ({mode})");
        }

        [Test]
        public void Add_FilterListenerRemovesIt_RefIsScratch() {
            var world = new World();
            var filter = world.Filter().Inc<FpPlain>().End();
            filter.AddListener(new RemoveOnEnter(world));
            var e = world.CreateEntity(new FpOther());
            ref var c = ref world.Add(e, new FpPlain { Id = 1 });
            c.Id = 77;
            Assert.That(world.Has<FpPlain>(e), Is.False, "the listener removed the component");
            WorldInvariants.AssertConsistent(world, "after a removed Add");
            world.Add(e, new FpPlain { Id = 5 });
            Assert.That(world.Get<FpPlain>(e).Id, Is.EqualTo(5), "the write through the stale ref must not land in a new component");
        }

        private sealed class RemoveOnEnter : IFilterListener {
            private readonly World _world;
            private bool _armed = true;
            public RemoveOnEnter(World world) => _world = world;

            public void OnEntityAdded(Filter filter, int entityIndex) {
                if (_armed) {
                    _armed = false;
                    _world.Remove<FpPlain>(_world.GetEntity(entityIndex));
                }
            }

            public void OnEntityRemoved(Filter filter, int entityIndex) {
            }
        }

        // ---- Remove ----

        [Test]
        public void Remove_MiddleLastAndAbsent_KeepsOthers([Values] Mode mode) {
            var world = WorldFor(mode, out _);
            var entities = new List<Entity>();
            for (int i = 0; i < 6; i++) {
                var e = world.CreateEntity(new FpOther { V = i });
                world.Add(e, new FpPlain { Id = i });
                entities.Add(e);
            }
            var bare = world.CreateEntity(new FpOther());
            world.Remove<FpPlain>(bare);
            world.Remove<FpPlain>(entities[2]);
            world.Remove<FpPlain>(entities[5]);
            world.Remove<FpPlain>(entities[2]);
            for (int i = 0; i < 6; i++) {
                bool kept = i != 2 && i != 5;
                Assert.That(world.Has<FpPlain>(entities[i]), Is.EqualTo(kept), $"{mode}: entity {i}");
                if (kept) {
                    Assert.That(world.Get<FpPlain>(entities[i]).Id, Is.EqualTo(i), $"{mode}: entity {i} keeps its value after the swap-remove");
                }
                Assert.That(world.IsAlive(entities[i]), Is.True, $"{mode}: entity {i} keeps FpOther and stays alive");
            }
            WorldInvariants.AssertConsistent(world, $"after Removes ({mode})");
        }

        [Test]
        public void Remove_HighIndexWithoutComponent_IsNoOp() {
            var world = new World(new WorldConfig {
                InitialEntityCapacity = 4, InitialPoolSparseCapacity = 2, InitialPoolDenseCapacity = 1, InitialPoolCount = 4
            });
            world.CreateEntity(new FpPlain());
            Entity far = default;
            for (int i = 0; i < 20; i++) {
                far = world.CreateEntity(new FpOther());
            }
            Assert.That(far.Index, Is.GreaterThanOrEqualTo(world.Pool<FpPlain>().SparseCapacity), "the entity must lie past the sparse array");
            world.Remove<FpPlain>(far);
            Assert.That(world.IsAlive(far), Is.True);
            WorldInvariants.AssertConsistent(world, "after a Remove past the sparse array");
        }

        [Test]
        public void Remove_LastComponent_AutoDestroys([Values] Mode mode) {
            var world = WorldFor(mode, out var log);
            var e = world.CreateEntity(new FpPlain { Id = 1 });
            var other = world.CreateEntity(new FpPlain { Id = 2 });
            world.Remove<FpPlain>(e);
            Assert.That(world.IsAlive(e), Is.False, $"{mode}: removing the last component destroys the entity");
            Assert.That(world.IsAlive(other), Is.True, $"{mode}: other entities stay");
            if (log != null) {
                Assert.That(log.Log.Last(), Is.EqualTo("destroyed " + e.Index), $"{mode}: the auto-destroy follows the removal");
            }
            WorldInvariants.AssertConsistent(world, $"after an auto-destroy ({mode})");
        }

        [Test]
        public void Remove_FilterListenerReAddsLastComponent_EntityStays() {
            var world = new World();
            var filter = world.Filter().Inc<FpPlain>().End();
            var listener = new LogFilterListener();
            filter.AddListener(listener);
            var e = world.CreateEntity(new FpPlain { Id = 1 });
            bool armed = true;
            listener.OnRemoved = idx => {
                if (armed) {
                    armed = false;
                    world.Add(world.GetEntity(idx), new FpTag());
                }
            };
            world.Remove<FpPlain>(e);
            Assert.That(world.IsAlive(e), Is.True, "a component re-added by a filter listener must keep the entity alive");
            Assert.That(world.Has<FpTag>(e), Is.True);
            Assert.That(world.GetComponentCount(e), Is.EqualTo(1));
            WorldInvariants.AssertConsistent(world, "after a re-add from a filter listener");
        }

        [Test]
        public void DestroyEntity_UnfilteredAndFilteredTypes_ReleasesOnce([Values] bool filtered) {
            var world = new World();
            var log = new LogWorldListener();
            if (filtered) {
                world.Filter().Inc<FpPlain>().End();
                world.Filter().Inc<FpOther>().Exc<FpTag>().End();
            }
            var e = world.CreateEntity(new FpPlain { Id = 1 });
            world.Add(e, new FpOther());
            world.Add(e, new FpTag());
            var keep = world.CreateEntity(new FpOther());
            world.DestroyEntity(e);
            Assert.That(world.IsAlive(e), Is.False);
            Assert.That(world.IsAlive(keep), Is.True);
            Assert.That(world.EntityCount, Is.EqualTo(1), "the destroyed slot is released once");
            var again = world.CreateEntity(new FpTag());
            var third = world.CreateEntity(new FpTag());
            Assert.That(again.Index, Is.EqualTo(e.Index), "the freed slot is reused");
            Assert.That(third.Index, Is.Not.EqualTo(e.Index), "and handed out only once");
            WorldInvariants.AssertConsistent(world, $"after DestroyEntity (filtered={filtered})");
        }

        // ---- CommandBuffer ----

        [Test]
        public void CommandBuffer_ReusedAcrossTypes_ClearsEveryStore() {
            var world = new World();
            var e = world.CreateEntity(new FpOther());
            var buffer = new CommandBuffer();
            buffer.Add(e, new FpPlain { Id = 1 });
            buffer.Add(e, new FpEvent { Id = 2 });
            buffer.Set(e, new FpOther { V = 3 });
            buffer.Clear();
            Assert.That(buffer.Count, Is.Zero);

            buffer.Set(e, new FpOther { V = 4 });
            buffer.Add(e, new FpEvent { Id = 5 });
            buffer.Playback(world);
            Assert.That(world.Has<FpPlain>(e), Is.False, "a cleared command must not apply");
            Assert.That(world.Get<FpOther>(e).V, Is.EqualTo(4), "the payload recorded after Clear must apply");
            Assert.That(world.Get<FpEvent>(e).Id, Is.EqualTo(5));

            for (int round = 0; round < 3; round++) {
                buffer.Add(e, new FpPlain { Id = 10 + round });
                buffer.Remove<FpEvent>(e);
                buffer.Add(e, new FpEvent { Id = 20 + round });
                buffer.Remove<FpPlain>(e);
                buffer.Set(e, new FpOther { V = 30 + round });
                buffer.Playback(world);
                Assert.That(world.Has<FpPlain>(e), Is.False, $"round {round}");
                Assert.That(world.Get<FpEvent>(e).Id, Is.EqualTo(20 + round), $"round {round}: payloads restart after Playback");
                Assert.That(world.Get<FpOther>(e).V, Is.EqualTo(30 + round), $"round {round}");
            }
        }

        [Test]
        public void CommandBuffer_ManyTypes_ClearReleasesReferencePayloads() {
            var buffer = new CommandBuffer();
            var world = new World();
            var e = world.CreateEntity(new FpOther());
            var weak = RecordInventory(buffer, e);
            buffer.Clear();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.That(weak.IsAlive, Is.False, "Clear must drop the payloads of types holding references");
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static WeakReference RecordInventory(CommandBuffer buffer, Entity e) {
            buffer.Add(e, new FpPlain());
            buffer.Add(e, new FpTag());
            var items = new List<int> { 1 };
            buffer.Add(e, new Inventory { Items = items });
            return new WeakReference(items);
        }
    }
}
