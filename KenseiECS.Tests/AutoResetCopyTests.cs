using System.Collections.Generic;
using NUnit.Framework;

namespace KenseiECS.Tests {
    [TestFixture]
    public class AutoCopyTests {
        [Test]
        public void CopyEntity_DeepCopiesList() {
            var world = new World();
            var src = world.CreateEntity(new DeepInventory { Items = new List<int> { 1, 2 } });
            var copy = world.CopyEntity(src);
            world.Get<DeepInventory>(src).Items.Add(3);
            Assert.That(world.Get<DeepInventory>(copy).Items, Is.EqualTo(new[] { 1, 2 }), "IAutoCopy must give the copy its own list");
        }

        [Test]
        public void CopyEntity_WithoutAutoCopy_SharesReference() {
            var world = new World();
            var src = world.CreateEntity(new Inventory { Items = new List<int> { 1 } });
            var copy = world.CopyEntity(src);
            Assert.That(world.Get<Inventory>(copy).Items, Is.SameAs(world.Get<Inventory>(src).Items), "without IAutoCopy the copy is shallow");
        }

        [Test]
        public void CopyEntity_NullList_StaysNull() {
            var world = new World();
            var src = world.CreateEntity(new DeepInventory());
            var copy = world.CopyEntity(src);
            Assert.That(world.Get<DeepInventory>(copy).Items, Is.Null, "AutoCopy must handle a null reference field");
        }

        [Test]
        public void ExplicitInterfaceAutoCopy_IsInvoked() {
            var world = new World();
            var src = world.CreateEntity(new ExplicitCopy { V = 4 });
            var copy = world.CopyEntity(src);
            Assert.That(world.Get<ExplicitCopy>(copy).V, Is.EqualTo(40), "explicitly implemented IAutoCopy must be picked up");
        }

        [Test]
        public void CopyEntity_DeadSource_ReturnsNullInRelease() {
#if !KENSEI_DEBUG
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.DestroyEntity(e);
            Assert.That(world.CopyEntity(e), Is.EqualTo(Entity.Null), "CopyEntity of a dead entity returns Entity.Null in release");
#else
            Assert.Pass("release-only contract");
#endif
        }
    }

    [TestFixture]
    public class AutoResetOrderTests {
        [Test]
        public void ExplicitInterfaceAutoReset_IsInvokedOnRemove() {
            var world = new World();
            var e = world.CreateEntity(new ExplicitReset { V = 5 });
            world.Add(e, new Health());
            ExplicitReset.ResetCalls = 0;
            world.Remove<ExplicitReset>(e);
            Assert.That(ExplicitReset.ResetCalls, Is.EqualTo(1), "explicitly implemented IAutoReset must be picked up");
        }

        [Test]
        public void SwapRemove_DoesNotResetMovedLiveComponent() {
            var world = new World();
            var removed = world.CreateEntity(new Inventory { Items = new List<int> { 1 } });
            world.Add(removed, new Health());
            var survivor = world.CreateEntity(new Inventory { Items = new List<int> { 2, 3 } });

            world.Remove<Inventory>(removed);

            Assert.That(world.Get<Inventory>(survivor).Items, Is.EqualTo(new[] { 2, 3 }), "the live component moved into the freed slot must keep its data");
        }

        [Test]
        public void SwapRemove_ResetsExactlyOnce() {
            var world = new World();
            var removed = world.CreateEntity(new ResetTracked { V = 1 });
            world.Add(removed, new Health());
            world.CreateEntity(new ResetTracked { V = 2 });

            ResetTracked.ResetCalls = 0;
            world.Remove<ResetTracked>(removed);

            Assert.That(ResetTracked.ResetCalls, Is.EqualTo(1), "swap-remove must call AutoReset once, for the removed component only");
        }

        [Test]
        public void RemoveLast_ResetsExactlyOnce() {
            var world = new World();
            world.CreateEntity(new ResetTracked { V = 1 });
            var last = world.CreateEntity(new ResetTracked { V = 2 });
            world.Add(last, new Health());

            ResetTracked.ResetCalls = 0;
            world.Remove<ResetTracked>(last);

            Assert.That(ResetTracked.ResetCalls, Is.EqualTo(1), "removing the tail component must call AutoReset once");
        }

        [Test]
        public void DestroyEntity_ResetsEachComponent() {
            var world = new World();
            var e = world.CreateEntity(new ResetTracked { V = 1 });
            world.Add(e, new Inventory { Items = new List<int> { 1 } });

            ResetTracked.ResetCalls = 0;
            world.DestroyEntity(e);

            Assert.That(ResetTracked.ResetCalls, Is.EqualTo(1), "DestroyEntity must AutoReset every component that implements it");
        }
    }
}
