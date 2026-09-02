using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace KenseiECS.Tests {
    [TestFixture]
    public class FilterAnyTests {
        private struct Shield : IComponent { public float Value; }

        [Test]
        public void AnyOnly_MatchesEntityWithEitherComponent() {
            var world = new World();
            var withHealth = world.CreateEntity(new Health());
            var withShield = world.CreateEntity(new Shield());
            world.CreateEntity(new Position());
            var filter = world.Filter().Any<Health>().Any<Shield>().End();
            Assert.That(filter.Count, Is.EqualTo(2), "Any filter must match entities with at least one listed component");
            Assert.That(filter.Contains(withHealth.Index) && filter.Contains(withShield.Index), Is.True, "both alternatives must match");
        }

        [Test]
        public void AnyWithInc_RequiresIncAndOneAlternative() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            var filter = world.Filter().Inc<Position>().Any<Health>().Any<Shield>().End();
            Assert.That(filter.Contains(e.Index), Is.False, "Inc alone must not satisfy an Any constraint");
            world.Add(e, new Shield());
            Assert.That(filter.Contains(e.Index), Is.True, "Inc plus one alternative must match");
        }

        [Test]
        public void Any_RemovingOneAlternative_KeepsEntityWhenOtherRemains() {
            var world = new World();
            var e = world.CreateEntity(new Health());
            world.Add(e, new Shield());
            var filter = world.Filter().Any<Health>().Any<Shield>().End();
            world.Remove<Health>(e);
            Assert.That(filter.Contains(e.Index), Is.True, "entity with a remaining alternative must stay in the filter");
        }

        [Test]
        public void Any_RemovingLastAlternative_DropsEntity() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.Add(e, new Health());
            var filter = world.Filter().Inc<Position>().Any<Health>().Any<Shield>().End();
            world.Remove<Health>(e);
            Assert.That(filter.Contains(e.Index), Is.False, "entity without any alternative must leave the filter");
        }

        [Test]
        public void Any_WithExc_ExcludeWins() {
            var world = new World();
            var e = world.CreateEntity(new Health());
            world.Add(e, new Frozen());
            var filter = world.Filter().Any<Health>().Any<Shield>().Exc<Frozen>().End();
            Assert.That(filter.Contains(e.Index), Is.False, "Exc must still exclude entities matched through Any");
        }

        [Test]
        public void Any_SameTypeAsInc_IsRedundantNotError() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            var filter = world.Filter().Inc<Position>().Any<Position>().Any<Health>().End();
            Assert.That(filter.Contains(e.Index), Is.False, "Any collapsed into Inc must still require another alternative");
            world.Add(e, new Health());
            Assert.That(filter.Contains(e.Index), Is.True, "remaining alternative must match");
        }

        [Test]
        public void Any_SameTypeAsExc_Throws() {
            var world = new World();
            Assert.That(() => world.Filter().Any<Health>().Any<Shield>().Exc<Health>().End(),
                Throws.InvalidOperationException, "Any and Exc of the same type must be rejected");
        }

        [Test]
        public void Any_DifferentAnySets_AreDistinctFilters() {
            var world = new World();
            var a = world.Filter().Inc<Position>().Any<Health>().Any<Shield>().End();
            var b = world.Filter().Inc<Position>().Any<Health>().Any<Frozen>().End();
            Assert.That(a, Is.Not.SameAs(b), "filters differing only in Any must not be deduplicated together");
        }

        [Test]
        public void Any_SpanningMultipleMaskWords_Matches() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            var filter = world.Filter().Inc<Position>().Any<FilterTests.WideTagA>().Any<Health>().End();
            Assert.That(filter.Contains(e.Index), Is.False, "no alternative present");
            world.Add(e, new FilterTests.WideTagA());
            Assert.That(filter.Contains(e.Index), Is.True, "alternative in another mask word must match");
        }
    }

    [TestFixture]
    public class FilterHelperTests {
        [Test]
        public void IsEmpty_ReflectsCount() {
            var world = new World();
            var filter = world.Filter().Inc<Position>().End();
            Assert.That(filter.IsEmpty, Is.True, "new filter over empty world is empty");
            world.CreateEntity(new Position());
            Assert.That(filter.IsEmpty, Is.False, "filter with a match is not empty");
        }

        [Test]
        public void First_ReturnsMatchingEntity() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            var filter = world.Filter().Inc<Position>().End();
            Assert.That(filter.First(), Is.EqualTo(e.Index), "First must return the matching entity");
        }

        [Test]
        public void First_OnEmpty_Throws() {
            var world = new World();
            var filter = world.Filter().Inc<Position>().End();
            Assert.That(() => filter.First(), Throws.InvalidOperationException, "First on an empty filter must throw");
        }

        [Test]
        public void TryGetFirst_OnEmpty_ReturnsFalse() {
            var world = new World();
            var filter = world.Filter().Inc<Position>().End();
            Assert.That(filter.TryGetFirst(out _), Is.False, "TryGetFirst on an empty filter must return false");
        }

        [Test]
        public void Single_WithTwoMatches_Throws() {
            var world = new World();
            world.CreateEntity(new Position());
            world.CreateEntity(new Position());
            var filter = world.Filter().Inc<Position>().End();
            Assert.That(() => filter.Single(), Throws.InvalidOperationException, "Single with two matches must throw");
        }

        [Test]
        public void Entities_SpanContainsAllMatches() {
            var world = new World();
            var a = world.CreateEntity(new Position());
            var b = world.CreateEntity(new Position());
            var filter = world.Filter().Inc<Position>().End();
            var span = filter.Entities;
            Assert.That(span.Length, Is.EqualTo(2), "span length must equal Count");
            Assert.That(span.ToArray(), Is.EquivalentTo(new[] { a.Index, b.Index }), "span must contain every matching entity");
        }
    }

    [TestFixture]
    public class FilterListenerTests {
        private sealed class Recorder : IFilterListener {
            public readonly List<string> Log = new();

            public void OnEntityAdded(Filter filter, int entityIndex) =>
                Log.Add("+" + entityIndex);

            public void OnEntityRemoved(Filter filter, int entityIndex) =>
                Log.Add("-" + entityIndex);
        }

        [Test]
        public void Listener_SeesEnterAndLeave() {
            var world = new World();
            var filter = world.Filter().Inc<Position>().Inc<Velocity>().End();
            var recorder = new Recorder();
            filter.AddListener(recorder);

            var e = world.CreateEntity(new Position());
            world.Add(e, new Velocity());
            world.Remove<Velocity>(e);

            Assert.That(recorder.Log, Is.EqualTo(new[] { "+" + e.Index, "-" + e.Index }), "listener must see enter then leave");
        }

        [Test]
        public void Listener_SeesLeaveOnDestroy() {
            var world = new World();
            var filter = world.Filter().Inc<Position>().End();
            var recorder = new Recorder();
            filter.AddListener(recorder);
            var e = world.CreateEntity(new Position());
            world.DestroyEntity(e);
            Assert.That(recorder.Log, Does.Contain("-" + e.Index), "destroying must notify leave");
        }

        [Test]
        public void Listener_NotCalledAfterRemove() {
            var world = new World();
            var filter = world.Filter().Inc<Position>().End();
            var recorder = new Recorder();
            filter.AddListener(recorder);
            filter.RemoveListener(recorder);
            world.CreateEntity(new Position());
            Assert.That(recorder.Log, Is.Empty, "removed listener must not be notified");
        }

        [Test]
        public void Listener_CanReactByAddingComponent() {
            var world = new World();
            var filter = world.Filter().Inc<Position>().End();
            filter.AddListener(new AddVelocityOnEnter(world));
            var e = world.CreateEntity(new Position());
            Assert.That(world.Has<Velocity>(e), Is.True, "listener must be able to modify the entering entity");
        }

        private sealed class AddVelocityOnEnter : IFilterListener {
            private readonly World _world;

            public AddVelocityOnEnter(World world) {
                _world = world;
            }

            public void OnEntityAdded(Filter filter, int entityIndex) =>
                _world.Add(_world.GetEntity(entityIndex), new Velocity());

            public void OnEntityRemoved(Filter filter, int entityIndex) {
            }
        }
    }

    [TestFixture]
    public class FilterSpecTests {
        [Test]
        public void IncSpec_EquivalentToBuilder() {
            var world = new World();
            var built = world.Filter().Inc<Position>().Inc<Velocity>().End();
            var spec = world.Filter<Inc<Position, Velocity>>();
            Assert.That(spec, Is.SameAs(built), "spec filter must deduplicate against the builder filter");
        }

        [Test]
        public void IncExcSpec_EquivalentToBuilder() {
            var world = new World();
            var built = world.Filter().Inc<Position>().Exc<Frozen>().End();
            var spec = world.Filter<Inc<Position>, Exc<Frozen>>();
            Assert.That(spec, Is.SameAs(built), "Inc+Exc spec must deduplicate against the builder filter");
        }

        [Test]
        public void IncExcAnySpec_EquivalentToBuilder() {
            var world = new World();
            var built = world.Filter().Inc<Position>().Exc<Frozen>().Any<Health>().Any<Damage>().End();
            var spec = world.Filter<Inc<Position>, Exc<Frozen>, Any<Health, Damage>>();
            Assert.That(spec, Is.SameAs(built), "three-part spec must deduplicate against the builder filter");
        }

        [Test]
        public void NoneSpec_IsIgnored() {
            var world = new World();
            var built = world.Filter().Inc<Position>().End();
            var spec = world.Filter<Inc<Position>, None>();
            Assert.That(spec, Is.SameAs(built), "None spec must add no constraint");
        }

        [Test]
        public void Spec_OrderOfTypesDoesNotMatter() {
            var world = new World();
            var a = world.Filter<Inc<Position, Velocity>>();
            var b = world.Filter<Inc<Velocity, Position>>();
            Assert.That(a, Is.SameAs(b), "type order inside a spec must not create a distinct filter");
        }
    }

    [TestFixture]
    public class SingletonTests {
        private struct GameState : IComponent { public int Level; }

        [Test]
        public void GetSingleton_ReturnsTheOnlyComponent() {
            var world = new World();
            world.CreateEntity(new GameState { Level = 3 });
            Assert.That(world.GetSingleton<GameState>().Level, Is.EqualTo(3), "GetSingleton must return the only component");
        }

        [Test]
        public void GetSingleton_ByRef_Writes() {
            var world = new World();
            var e = world.CreateEntity(new GameState());
            world.GetSingleton<GameState>().Level = 7;
            Assert.That(world.Get<GameState>(e).Level, Is.EqualTo(7), "GetSingleton must return a live ref");
        }

        [Test]
        public void GetSingleton_WhenAbsent_Throws() {
            var world = new World();
            Assert.That(() => world.GetSingleton<GameState>(), Throws.InvalidOperationException, "GetSingleton without the component must throw");
        }

        [Test]
        public void GetSingleton_WhenTwo_Throws() {
            var world = new World();
            world.CreateEntity(new GameState());
            world.CreateEntity(new GameState());
            Assert.That(() => world.GetSingleton<GameState>(), Throws.InvalidOperationException, "GetSingleton with two holders must throw");
        }

        [Test]
        public void HasSingleton_TracksCount() {
            var world = new World();
            Assert.That(world.HasSingleton<GameState>(), Is.False, "no holder");
            var e = world.CreateEntity(new GameState());
            Assert.That(world.HasSingleton<GameState>(), Is.True, "one holder");
            world.CreateEntity(new GameState());
            Assert.That(world.HasSingleton<GameState>(), Is.False, "two holders is not a singleton");
            Assert.That(world.GetSingletonEntity<GameState>, Throws.InvalidOperationException, "GetSingletonEntity with two holders must throw");
            world.DestroyEntity(e);
            Assert.That(world.HasSingleton<GameState>(), Is.True, "back to one holder");
        }

        [Test]
        public void GetSingletonEntity_ReturnsHolder() {
            var world = new World();
            var e = world.CreateEntity(new GameState());
            Assert.That(world.GetSingletonEntity<GameState>(), Is.EqualTo(e), "GetSingletonEntity must return the holder");
        }
    }

    [TestFixture]
    public class PoolListenerTests {
        private sealed class Recorder : IComponentListener<Health> {
            public readonly List<string> Log = new();

            public void OnAdded(int entityIndex, ref Health component) =>
                Log.Add($"+{entityIndex}:{component.Value}");

            public void OnRemoved(int entityIndex, ref Health component) =>
                Log.Add($"-{entityIndex}:{component.Value}");
        }

        [Test]
        public void Listener_SeesAddWithData() {
            var world = new World();
            var recorder = new Recorder();
            world.Pool<Health>().AddListener(recorder);
            var e = world.CreateEntity(new Health { Value = 50 });
            Assert.That(recorder.Log, Is.EqualTo(new[] { $"+{e.Index}:50" }), "OnAdded must receive the stored value");
        }

        [Test]
        public void Listener_SeesRemoveWithDataBeforeReset() {
            var world = new World();
            var recorder = new Recorder();
            world.Pool<Health>().AddListener(recorder);
            var e = world.CreateEntity(new Health { Value = 50 });
            world.Add(e, new Position());
            world.Remove<Health>(e);
            Assert.That(recorder.Log[1], Is.EqualTo($"-{e.Index}:50"), "OnRemoved must see the data before it is reset");
        }

        [Test]
        public void Listener_OnAdded_CanMutateComponent() {
            var world = new World();
            world.Pool<Health>().AddListener(new Clamp());
            var e = world.CreateEntity(new Health { Value = 500 });
            Assert.That(world.Get<Health>(e).Value, Is.EqualTo(100f), "OnAdded may write through the ref");
        }

        [Test]
        public void Listener_RemovedListener_IsSilent() {
            var world = new World();
            var recorder = new Recorder();
            var pool = world.Pool<Health>();
            pool.AddListener(recorder);
            pool.RemoveListener(recorder);
            world.CreateEntity(new Health());
            Assert.That(recorder.Log, Is.Empty, "removed listener must not be called");
        }

        [Test]
        public void Listener_OnRemoved_RemovingAnotherComponentOfSameType_IsSafe() {
            var world = new World();
            var a = world.CreateEntity(new Health { Value = 1 });
            var b = world.CreateEntity(new Health { Value = 2 });
            world.Add(a, new Position());
            world.Add(b, new Position());
            world.Pool<Health>().AddListener(new RemoveOtherOnRemove(world, b));
            world.Remove<Health>(a);
            Assert.That(world.Has<Health>(a) || world.Has<Health>(b), Is.False, "both components must be gone");
            Assert.That(world.Pool<Health>().Count, Is.EqualTo(0), "pool must be consistent after nested removal");
        }

        private sealed class Clamp : IComponentListener<Health> {
            public void OnAdded(int entityIndex, ref Health component) =>
                component.Value = Math.Min(component.Value, 100f);

            public void OnRemoved(int entityIndex, ref Health component) {
            }
        }

        private sealed class RemoveOtherOnRemove : IComponentListener<Health> {
            private readonly World _world;
            private readonly Entity _other;
            private bool _armed = true;

            public RemoveOtherOnRemove(World world, Entity other) {
                _world = world;
                _other = other;
            }

            public void OnAdded(int entityIndex, ref Health component) {
            }

            public void OnRemoved(int entityIndex, ref Health component) {
                if (_armed) {
                    _armed = false;
                    _world.Remove<Health>(_other);
                }
            }
        }
    }

    [TestFixture]
    public class EventBufferTests {
        private struct Hit { public float Amount; }

        [Test]
        public void AddEvent_AccumulatesMultipleEventsPerEntity() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.AddEvent(e, new Hit { Amount = 1 });
            world.AddEvent(e, new Hit { Amount = 2 });
            Assert.That(world.Get<EventBuffer<Hit>>(e).Count, Is.EqualTo(2), "two events must both be stored");
        }

        [Test]
        public void EventBuffer_OneFrame_ClearsAndReturnsList() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            var systems = new SystemsRunner(world).OneFrame<EventBuffer<Hit>>();
            systems.Init();
            world.AddEvent(e, new Hit { Amount = 1 });
            var list = world.Get<EventBuffer<Hit>>(e).Values;
            systems.Run();
            Assert.That(world.Has<EventBuffer<Hit>>(e), Is.False, "buffer must be removed at end of frame");
            world.AddEvent(e, new Hit { Amount = 2 });
            Assert.That(world.Get<EventBuffer<Hit>>(e).Values, Is.SameAs(list), "list must be pooled and reused");
            Assert.That(world.Get<EventBuffer<Hit>>(e).Count, Is.EqualTo(1), "reused list must start empty");
        }
    }
}
