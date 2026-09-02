#if KENSEI_DEBUG
using NUnit.Framework;

namespace KenseiECS.Tests {
    [TestFixture]
    public class DebugIterationGuardTests {
        [Test]
        public void DestroyingCurrentEntity_IsAllowed() {
            var world = new World();
            world.CreateEntity(new Position());
            world.CreateEntity(new Position());
            var filter = world.Filter().Inc<Position>().End();
            Assert.That(() => {
                foreach (int e in filter) {
                    world.DestroyEntity(world.GetEntity(e));
                }
            }, Throws.Nothing, "destroying the current entity inside foreach is allowed");
        }

        [Test]
        public void RemovingCurrentEntityComponent_IsAllowed() {
            var world = new World();
            var e0 = world.CreateEntity(new Position());
            world.Add(e0, new Velocity());
            var filter = world.Filter().Inc<Position>().Inc<Velocity>().End();
            Assert.That(() => {
                foreach (int e in filter) {
                    world.Remove<Velocity>(world.GetEntity(e));
                }
            }, Throws.Nothing, "removing an included component from the current entity is allowed");
        }

        [Test]
        public void DestroyingOtherEntity_Throws() {
            var world = new World();
            var a = world.CreateEntity(new Position());
            var b = world.CreateEntity(new Position());
            var filter = world.Filter().Inc<Position>().End();
            Assert.That(() => {
                foreach (int e in filter) {
                    var victim = e == a.Index ? b : a;
                    world.DestroyEntity(victim);
                }
            }, Throws.InvalidOperationException, "destroying a different entity inside foreach must throw in debug mode");
        }

        [Test]
        public void DestroyingOtherEntity_NotInFilter_IsAllowed() {
            var world = new World();
            world.CreateEntity(new Position());
            var other = world.CreateEntity(new Velocity());
            var filter = world.Filter().Inc<Position>().End();
            Assert.That(() => {
                foreach (int e in filter) {
                    world.DestroyEntity(other);
                }
            }, Throws.Nothing, "entities outside the iterated filter may be destroyed");
        }

        [Test]
        public void GuardResets_AfterLoop() {
            var world = new World();
            var a = world.CreateEntity(new Position());
            world.CreateEntity(new Position());
            var filter = world.Filter().Inc<Position>().End();
            foreach (int e in filter) {
            }
            Assert.That(() => world.DestroyEntity(a), Throws.Nothing, "guard must be released when the loop ends");
        }

        [Test]
        public void GuardResets_AfterBreak() {
            var world = new World();
            var a = world.CreateEntity(new Position());
            world.CreateEntity(new Position());
            var filter = world.Filter().Inc<Position>().End();
            foreach (int e in filter) {
                break;
            }
            Assert.That(() => world.DestroyEntity(a), Throws.Nothing, "guard must be released on break");
        }

        [Test]
        public void AddingEntitiesDuringIteration_IsAllowed() {
            var world = new World();
            world.CreateEntity(new Position());
            var filter = world.Filter().Inc<Position>().End();
            Assert.That(() => {
                foreach (int e in filter) {
                    world.CreateEntity(new Position());
                }
            }, Throws.Nothing, "creating matching entities inside foreach is allowed");
        }

        [Test]
        public void DestroyingUnvisitedEntity_WhenSwapStaysUnvisited_IsAllowed() {
            var world = new World();
            var entities = new Entity[5];
            for (int i = 0; i < entities.Length; i++) {
                entities[i] = world.CreateEntity(new Position());
            }
            var filter = world.Filter().Inc<Position>().End();
            Assert.That(() => {
                bool first = true;
                foreach (int e in filter) {
                    if (first) {
                        first = false;
                        world.DestroyEntity(world.GetEntity(e));
                        world.DestroyEntity(entities[0]);
                    }
                }
            }, Throws.Nothing, "after the current entity is gone, the tail below the cursor may shrink freely");
        }

        [Test]
        public void NestedLoop_InnerRemovesUnvisitedOfOuter_Throws() {
            var world = new World();
            world.CreateEntity(new Position());
            world.CreateEntity(new Position());
            var filter = world.Filter().Inc<Position>().End();
            Assert.That(() => {
                foreach (int outer in filter) {
                    foreach (int inner in filter) {
                        if (inner != outer) {
                            world.DestroyEntity(world.GetEntity(inner));
                        }
                    }
                }
            }, Throws.InvalidOperationException, "destroying a non-current entity of an iterated filter must throw even from a nested loop");
        }
    }
}
#endif
