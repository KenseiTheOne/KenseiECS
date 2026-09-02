#if KENSEI_DEBUG
using System.Collections.Generic;
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
        public void Add_OnReusedSlotWithOldHandle_Throws() {
            var world = new World();
            var stale = world.CreateEntity(new Position());
            world.DestroyEntity(stale);
            world.CreateEntity(new Position());
            Assert.That(() => world.Add(stale, new Velocity()),
                Throws.InvalidOperationException, "old handle must be rejected after the slot is reused");
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
        public void Has_OnStaleHandle_Throws() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.DestroyEntity(e);
            Assert.That(() => world.Has<Position>(e),
                Throws.InvalidOperationException, "Has with a stale handle must throw in debug mode");
        }

        [Test]
        public void Get_WithoutComponent_Throws() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            Assert.That(() => world.Get<Velocity>(e),
                Throws.InvalidOperationException, "Get without the component must throw in debug mode");
        }

        [Test]
        public void CopyEntity_DeadSource_Throws() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.DestroyEntity(e);
            Assert.That(() => world.CopyEntity(e),
                Throws.InvalidOperationException, "CopyEntity of a dead entity must throw in debug mode");
        }

        [Test]
        public void GetComponentTypes_DeadEntity_Throws() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.DestroyEntity(e);
            Assert.That(() => world.GetComponentTypes(e, new List<int>()),
                Throws.InvalidOperationException, "GetComponentTypes on a dead entity must throw in debug mode");
        }

        [Test]
        public void PoolAdd_OnDeadSlot_Throws() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            int index = e.Index;
            world.DestroyEntity(e);
            Assert.That(() => world.Pool<Velocity>().Add(index, new Velocity()),
                Throws.InvalidOperationException, "pool int-API Add on a dead slot must throw in debug mode");
        }

        [Test]
        public void PoolAdd_OnNeverUsedSlot_Throws() {
            var world = new World();
            Assert.That(() => world.Pool<Velocity>().Add(7, new Velocity()),
                Throws.InvalidOperationException, "pool int-API Add on a slot that was never allocated must throw in debug mode");
        }

        [Test]
        public void PoolAdd_OnDyingEntityFromListener_IsAllowed() {
            var world = new World();
            var listener = new ReAddOnRemoveListener { World = world };
            world.AddEventListener(listener);
            var e = world.CreateEntity(new Position());
            listener.Armed = true;
            Assert.That(() => world.DestroyEntity(e), Throws.Nothing,
                "a listener must be allowed to touch the dying entity during destroy");
        }

        [Test]
        public void Run_BeforeInit_Throws() {
            var world = new World();
            var systems = new SystemsRunner(world).Add(new TestMovementSystem());
            Assert.That(() => systems.Run(),
                Throws.InvalidOperationException, "Run before Init must throw in debug mode");
        }

        [Test]
        public void Add_AfterInit_Throws() {
            var world = new World();
            var systems = new SystemsRunner(world);
            systems.Init();
            Assert.That(() => systems.Add(new TestMovementSystem()),
                Throws.InvalidOperationException, "Add after Init must throw in debug mode");
        }

        [Test]
        public void SetActive_UnknownName_Throws() {
            var world = new World();
            var systems = new SystemsRunner(world).Add(new TestMovementSystem(), "movement");
            Assert.That(() => systems.SetActive("movment", false),
                Throws.InvalidOperationException, "SetActive with an unknown name must throw in debug mode");
        }

        [Test]
        public void IsActive_UnknownName_Throws() {
            var world = new World();
            var systems = new SystemsRunner(world);
            Assert.That(() => systems.IsActive("nope"),
                Throws.InvalidOperationException, "IsActive with an unknown name must throw in debug mode");
        }

        [Test]
        public void GetRunner_UnknownName_Throws() {
            var world = new World();
            var systems = new SystemsRunner(world);
            Assert.That(() => systems.GetRunner("fixed"),
                Throws.InvalidOperationException, "GetRunner with an unknown name must throw in debug mode");
        }

        [Test]
        public void ChildRunner_DifferentWorld_Throws() {
            var root = new SystemsRunner(new World());
            var child = new SystemsRunner(new World());
            Assert.That(() => root.Add(child),
                Throws.InvalidOperationException, "nested runner with a different World must be rejected");
        }

        [Test]
        public void ChildRunner_DifferentExplicitSharedData_Throws() {
            var world = new World();
            var root = new SystemsRunner(world, new SharedData());
            var child = new SystemsRunner(world, new SharedData());
            Assert.That(() => root.Add(child),
                Throws.InvalidOperationException, "nested runner with a different explicit SharedData must be rejected");
        }
    }
}
#endif
