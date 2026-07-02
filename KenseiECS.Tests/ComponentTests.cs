using System.Collections.Generic;
using NUnit.Framework;

namespace KenseiECS.Tests {
    [TestFixture]
    public class ComponentTests {
        [Test]
        public void Add_ComponentIsPresent() {
            var world = new World();
            var e = world.CreateEntity(new Position { X = 1, Y = 2 });
            Assert.That(world.Has<Position>(e), Is.True, "added component must be reported by Has");
        }

        [Test]
        public void Get_ReturnsStoredX() {
            var world = new World();
            var e = world.CreateEntity(new Position { X = 1, Y = 2 });
            Assert.That(world.Get<Position>(e).X, Is.EqualTo(1f), "Get must return the stored X value");
        }

        [Test]
        public void Get_ReturnsStoredY() {
            var world = new World();
            var e = world.CreateEntity(new Position { X = 1, Y = 2 });
            Assert.That(world.Get<Position>(e).Y, Is.EqualTo(2f), "Get must return the stored Y value");
        }

        [Test]
        public void Remove_ComponentIsGone() {
            var world = new World();
            var e = world.CreateEntity(new Position { X = 1 });
            world.Add(e, new Health());
            world.Remove<Position>(e);
            Assert.That(world.Has<Position>(e), Is.False, "removed component must not be reported by Has");
        }

        [Test]
        public void GetByRef_ModifiesComponentInPlace() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            ref var pos = ref world.Get<Position>(e);
            pos.X = 42;
            Assert.That(world.Get<Position>(e).X, Is.EqualTo(42f), "write through ref must be visible on next Get");
        }

        [Test]
        public void MultipleComponents_HasPosition() {
            var world = new World();
            var e = world.CreateEntity(new Position { X = 1 });
            world.Add(e, new Velocity { Y = 4 });
            world.Add(e, new Health { Value = 100 });
            Assert.That(world.Has<Position>(e), Is.True, "entity must keep Position alongside other components");
        }

        [Test]
        public void MultipleComponents_HasVelocity() {
            var world = new World();
            var e = world.CreateEntity(new Position { X = 1 });
            world.Add(e, new Velocity { Y = 4 });
            world.Add(e, new Health { Value = 100 });
            Assert.That(world.Has<Velocity>(e), Is.True, "entity must keep Velocity alongside other components");
        }

        [Test]
        public void MultipleComponents_HasHealth() {
            var world = new World();
            var e = world.CreateEntity(new Position { X = 1 });
            world.Add(e, new Velocity { Y = 4 });
            world.Add(e, new Health { Value = 100 });
            Assert.That(world.Has<Health>(e), Is.True, "entity must keep Health alongside other components");
        }

        [Test]
        public void Has_UnpooledType_ReturnsFalse() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            Assert.That(world.Has<Damage>(e), Is.False, "Has must be false for a type that was never added");
        }

        [Test]
        public void Has_UnpooledType_DoesNotCreatePool() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.Has<Damage>(e);
            int pools = 0;
            foreach (var pool in world.ActivePools) {
                pools++;
            }
            Assert.That(pools, Is.EqualTo(1), "read-only Has must not lazily allocate a pool");
        }

        [Test]
        public void AutoReset_ClearsComponentOnReAdd() {
            var world = new World();
            var e = world.CreateEntity(new Inventory { Items = new List<int> { 1, 2, 3 } });
            world.Add(e, new Health()); // keep entity alive after removing Inventory
            world.Remove<Inventory>(e);
            world.Add(e, new Inventory());
            ref var inv = ref world.Get<Inventory>(e);
            Assert.That(inv.Items, Is.Null, "IAutoReset must null out Items on remove");
        }

        [Test]
        public void DefaultReset_XIsZeroOnReAdd() {
            var world = new World();
            var e = world.CreateEntity(new Position { X = 99, Y = 88 });
            world.Add(e, new Health()); // keep entity alive after removing Position
            world.Remove<Position>(e);
            world.Add(e, new Position());
            Assert.That(world.Get<Position>(e).X, Is.EqualTo(0f), "removed component must not leak old X into re-add");
        }

        [Test]
        public void DefaultReset_YIsZeroOnReAdd() {
            var world = new World();
            var e = world.CreateEntity(new Position { X = 99, Y = 88 });
            world.Add(e, new Health()); // keep entity alive after removing Position
            world.Remove<Position>(e);
            world.Add(e, new Position());
            Assert.That(world.Get<Position>(e).Y, Is.EqualTo(0f), "removed component must not leak old Y into re-add");
        }
    }
}
