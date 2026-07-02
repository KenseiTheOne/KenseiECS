using System;
using System.Runtime.CompilerServices;
using NUnit.Framework;

namespace KenseiECS.Tests {
    [TestFixture]
    public class FilterTests {
        public struct WideTagA : IComponent { }
        public struct WideTagB : IComponent { }
        private struct Pad<T> : IComponent { }

        // Pushes the global type-index counter past one mask word so types
        // touched afterwards land in word 1+ — the only way to exercise
        // multi-word filter matching with a shared process-wide registry.
        private static void RegisterPaddingTypes(int count) {
            Type arg = typeof(Position);
            for (int i = 0; i < count; i++) {
                arg = typeof(Pad<>).MakeGenericType(arg);
                RuntimeHelpers.RunClassConstructor(typeof(ComponentType<>).MakeGenericType(arg).TypeHandle);
            }
        }

        [Test]
        public void Filter_SpanningMultipleMaskWords_UpdatesReactively() {
            int positionWord = ComponentType<Position>.Index >> 6;
            RegisterPaddingTypes(64);
            var world = new World();
            var filter = world.Filter().Inc<Position>().Inc<WideTagA>().Exc<WideTagB>().End();
            Assert.That(ComponentType<WideTagA>.Index >> 6, Is.GreaterThan(positionWord),
                "test setup must place the tag types in a higher mask word than Position");

            var e = world.CreateEntity(new Position());
            Assert.That(filter.Contains(e.Index), Is.False, "entity missing the high-word include must not match");

            world.Add(e, new WideTagA());
            Assert.That(filter.Contains(e.Index), Is.True, "entity with includes across words must match");

            world.Add(e, new WideTagB());
            Assert.That(filter.Contains(e.Index), Is.False, "high-word exclude must drop the entity");

            world.Remove<WideTagB>(e);
            Assert.That(filter.Contains(e.Index), Is.True, "removing the exclude must restore the match");
        }

        [Test]
        public void Filter_SpanningMultipleMaskWords_PopulatesExistingMatches() {
            RegisterPaddingTypes(64);
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.Add(e, new WideTagA());
            var filter = world.Filter().Inc<Position>().Inc<WideTagA>().End();
            Assert.That(filter.Contains(e.Index), Is.True, "new filter must pick up existing multi-word matches");
        }

        [Test]
        public void Filter_MatchesOnlyEntitiesWithAllIncludedComponents() {
            var world = new World();
            var e1 = world.CreateEntity(new Position());
            world.Add(e1, new Velocity());
            world.CreateEntity(new Position());
            var filter = world.Filter().Inc<Position>().Inc<Velocity>().End();
            Assert.That(filter.Count, Is.EqualTo(1), "only the entity with both components must match");
        }

        [Test]
        public void Filter_ExcludesEntitiesWithExcludedComponent() {
            var world = new World();
            var e1 = world.CreateEntity(new Position());
            world.Add(e1, new Frozen());
            world.CreateEntity(new Position());
            var filter = world.Filter().Inc<Position>().Exc<Frozen>().End();
            Assert.That(filter.Count, Is.EqualTo(1), "entity with the excluded component must not match");
        }

        [Test]
        public void ReactiveAdd_NewFilterIsEmpty() {
            var world = new World();
            var filter = world.Filter().Inc<Position>().Inc<Velocity>().End();
            Assert.That(filter.Count, Is.EqualTo(0), "filter over an empty world must be empty");
        }

        [Test]
        public void ReactiveAdd_PartialMatchNotIncluded() {
            var world = new World();
            var filter = world.Filter().Inc<Position>().Inc<Velocity>().End();
            world.CreateEntity(new Position());
            Assert.That(filter.Count, Is.EqualTo(0), "entity missing one included component must not match");
        }

        [Test]
        public void ReactiveAdd_FullMatchIncluded() {
            var world = new World();
            var filter = world.Filter().Inc<Position>().Inc<Velocity>().End();
            var e = world.CreateEntity(new Position());
            world.Add(e, new Velocity());
            Assert.That(filter.Count, Is.EqualTo(1), "filter must pick up the entity once all included components are present");
        }

        [Test]
        public void ReactiveRemove_MatchingEntityIsInFilter() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.Add(e, new Velocity());
            var filter = world.Filter().Inc<Position>().Inc<Velocity>().End();
            Assert.That(filter.Count, Is.EqualTo(1), "matching entity must be in the filter before removal");
        }

        [Test]
        public void ReactiveRemove_RemovingIncludedComponentRemovesFromFilter() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.Add(e, new Velocity());
            var filter = world.Filter().Inc<Position>().Inc<Velocity>().End();
            world.Remove<Velocity>(e);
            Assert.That(filter.Count, Is.EqualTo(0), "filter must drop the entity when an included component is removed");
        }

        [Test]
        public void DuplicateFilter_ReturnsSameInstance() {
            var world = new World();
            var f1 = world.Filter().Inc<Position>().Inc<Velocity>().End();
            var f2 = world.Filter().Inc<Position>().Inc<Velocity>().End();
            Assert.That(f2, Is.SameAs(f1), "identical filter definitions must reuse one instance");
        }

        [Test]
        public void NewFilter_PopulatedWithExistingEntities() {
            var world = new World();
            var e1 = world.CreateEntity(new Position());
            world.Add(e1, new Velocity());
            world.CreateEntity(new Position());
            var filter = world.Filter().Inc<Position>().Inc<Velocity>().End();
            Assert.That(filter.Count, Is.EqualTo(1), "filter created after entities must include existing matches");
        }

        [Test]
        public void End_WithOnlyExcludes_Throws() {
            var world = new World();
            Assert.That(() => world.Filter().Exc<Frozen>().End(),
                Throws.InvalidOperationException, "exclude-only filter must be rejected");
        }

        [Test]
        public void End_WithoutConstraints_Throws() {
            var world = new World();
            Assert.That(() => world.Filter().End(),
                Throws.InvalidOperationException, "empty filter must be rejected");
        }

        [Test]
        public void End_IncAndExcOfSameType_Throws() {
            var world = new World();
            Assert.That(() => world.Filter().Inc<Position>().Exc<Position>().End(),
                Throws.InvalidOperationException, "Inc and Exc of the same type must be rejected");
        }
    }
}
