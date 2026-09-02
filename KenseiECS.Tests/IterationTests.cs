using NUnit.Framework;

namespace KenseiECS.Tests {
    [TestFixture]
    public class IterationTests {
        [Test]
        public void DestroyDuringIteration_FilterReflectsRemovals() {
            var world = new World();
            for (int i = 0; i < 10; i++) {
                world.CreateEntity(new Health { Value = i });
            }

            var filter = world.Filter().Inc<Health>().End();
            foreach (int e in filter) {
                ref var hp = ref world.Pool<Health>().Get(e);
                if (hp.Value < 5) {
                    world.DestroyEntity(world.GetEntity(e));
                }
            }

            Assert.That(filter.Count, Is.EqualTo(5), "entities destroyed during iteration must leave the filter");
        }

        [Test]
        public void AddComponentDuringIteration_AllEntitiesReceiveComponent() {
            var world = new World();
            for (int i = 0; i < 5; i++) {
                world.CreateEntity(new Position { X = i });
            }

            var posFilter = world.Filter().Inc<Position>().End();
            var bothFilter = world.Filter().Inc<Position>().Inc<Velocity>().End();

            foreach (int e in posFilter) {
                if (!world.Pool<Velocity>().Has(e)) {
                    world.Pool<Velocity>().Add(e, new Velocity { X = 1 });
                }
            }

            Assert.That(bothFilter.Count, Is.EqualTo(5), "every iterated entity must receive Velocity exactly once");
        }

        [Test]
        public void RemoveComponentDuringIteration_FilterReflectsRemovals() {
            var world = new World();
            for (int i = 0; i < 10; i++) {
                var ent = world.CreateEntity(new Position { X = i });
                world.Add(ent, new Health { Value = i });
            }

            var filter = world.Filter().Inc<Position>().Inc<Health>().End();
            foreach (int e in filter) {
                ref var hp = ref world.Pool<Health>().Get(e);
                if (hp.Value < 5) {
                    world.Remove<Health>(world.GetEntity(e));
                }
            }

            Assert.That(filter.Count, Is.EqualTo(5), "entities losing an included component during iteration must leave the filter");
        }

        [Test]
        public void DestroyAlreadyVisitedEntity_IsSafe() {
            var world = new World();
            var entities = new Entity[6];
            for (int i = 0; i < entities.Length; i++) {
                entities[i] = world.CreateEntity(new Health { Value = i });
            }

            var filter = world.Filter().Inc<Health>().End();
            int visits = 0;
            int previous = -1;
            foreach (int e in filter) {
                visits++;
                if (previous >= 0) {
                    world.DestroyEntity(world.GetEntity(previous));
                }
                previous = e;
            }

            Assert.That(visits, Is.EqualTo(6), "destroying an already visited entity must not disturb the loop");
            Assert.That(filter.Count, Is.EqualTo(1), "all but the last visited entity must be destroyed");
        }

        // Destroying entities the loop has not reached yet is the documented
        // limitation: the swap-remove can move an already visited entity into an
        // unvisited slot. Release tolerates it (the enumerator still never yields
        // a dead entity); KENSEI_DEBUG turns it into an exception.

        private static (Filter filter, int visits, bool visitedDead) RunMultiDestroyIteration() {
            var world = new World();
            var entities = new Entity[12];
            for (int i = 0; i < entities.Length; i++) {
                entities[i] = world.CreateEntity(new Health { Value = i });
            }

            var filter = world.Filter().Inc<Health>().End();
            int visits = 0;
            bool visitedDead = false;
            bool first = true;
            foreach (int e in filter) {
                visits++;
                if (!world.IsAlive(world.GetEntity(e))) {
                    visitedDead = true;
                }
                if (first) {
                    first = false;
                    world.DestroyEntity(entities[0]);
                    world.DestroyEntity(entities[1]);
                    world.DestroyEntity(entities[2]);
                }
            }

            return (filter, visits, visitedDead);
        }

        private static (Filter filter, Entity doomed, bool visitedDead) RunFilterResizeIteration() {
            var config = WorldConfig.Default();
            config.InitialPoolDenseCapacity = 2;
            var world = new World(config);
            var filter = world.Filter().Inc<Health>().End();

            var doomed = world.CreateEntity(new Health { Value = 0 });
            world.CreateEntity(new Health { Value = 1 });

            bool visitedDead = false;
            bool first = true;
            foreach (int e in filter) {
                if (!world.IsAlive(world.GetEntity(e))) {
                    visitedDead = true;
                }
                if (first) {
                    first = false;
                    world.CreateEntity(new Health { Value = 2 });
                    world.DestroyEntity(doomed);
                }
            }

            return (filter, doomed, visitedDead);
        }

#if KENSEI_DEBUG
        [Test]
        public void MultiDestroyPerStep_OfUnvisitedEntities_ThrowsInDebug() {
            Assert.That(() => RunMultiDestroyIteration(), Throws.InvalidOperationException,
                "destroying unvisited entities that would be double-visited must throw in debug mode");
        }

        [Test]
        public void FilterResizeDuringIteration_DestroyingUnvisited_ThrowsInDebug() {
            Assert.That(() => RunFilterResizeIteration(), Throws.InvalidOperationException,
                "destroying an unvisited entity after growth must throw in debug mode");
        }
#else
        [Test]
        public void MultiDestroyPerStep_NoDeadEntityVisited() {
            var (_, _, visitedDead) = RunMultiDestroyIteration();
            Assert.That(visitedDead, Is.False, "iteration must never yield a destroyed entity");
        }

        [Test]
        public void MultiDestroyPerStep_FilterCountIsNine() {
            var (filter, _, _) = RunMultiDestroyIteration();
            Assert.That(filter.Count, Is.EqualTo(9), "three destroyed entities must leave the filter");
        }

        [Test]
        public void MultiDestroyPerStep_VisitCountIsBounded() {
            var (_, visits, _) = RunMultiDestroyIteration();
            Assert.That(visits, Is.LessThanOrEqualTo(10), "iteration must not revisit slots after multiple removals");
        }

        [Test]
        public void FilterResizeDuringIteration_NoDeadEntityVisited() {
            var (_, _, visitedDead) = RunFilterResizeIteration();
            Assert.That(visitedDead, Is.False, "resize during iteration must not expose a destroyed entity");
        }

        [Test]
        public void FilterResizeDuringIteration_DestroyedEntityNotInFilter() {
            var (filter, doomed, _) = RunFilterResizeIteration();
            Assert.That(filter.Contains(doomed.Index), Is.False, "destroyed entity must not remain in the resized filter");
        }

        [Test]
        public void FilterResizeDuringIteration_CountIsTwo() {
            var (filter, _, _) = RunFilterResizeIteration();
            Assert.That(filter.Count, Is.EqualTo(2), "one added and one destroyed entity must net to two");
        }
#endif
    }
}
