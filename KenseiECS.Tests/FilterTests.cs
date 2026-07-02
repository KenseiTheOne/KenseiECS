using NUnit.Framework;

namespace KenseiECS.Tests {
    [TestFixture]
    public class FilterTests {
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
