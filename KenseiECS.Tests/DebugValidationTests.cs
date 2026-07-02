#if KENSEI_DEBUG
using NUnit.Framework;

namespace KenseiECS.Tests {
    [TestFixture]
    public class DebugValidationTests {
        [Test]
        public void Add_OnStaleHandle_Throws() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.DestroyEntity(e);
            Assert.That(() => world.Add(e, new Velocity()),
                Throws.InvalidOperationException, "Add with a stale handle must throw in debug mode");
        }

        [Test]
        public void Get_OnStaleHandle_Throws() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.DestroyEntity(e);
            Assert.That(() => world.Get<Position>(e),
                Throws.InvalidOperationException, "Get with a stale handle must throw in debug mode");
        }

        [Test]
        public void Get_WithoutComponent_Throws() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            Assert.That(() => world.Get<Velocity>(e),
                Throws.InvalidOperationException, "Get without the component must throw in debug mode");
        }

        [Test]
        public void GetEntity_OnDeadSlot_Throws() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.DestroyEntity(e);
            Assert.That(() => world.GetEntity(e.Index),
                Throws.InvalidOperationException, "GetEntity on a dead slot must throw in debug mode");
        }
    }
}
#endif
