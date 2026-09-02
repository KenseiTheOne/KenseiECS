using System.Collections.Generic;
using NUnit.Framework;

namespace KenseiECS.Tests {
    [TestFixture]
    public class RunnerInitContractTests {
        [Test]
        public void Init_Throws_RunnerStaysUninitialized() {
            var world = new World();
            var systems = new SystemsRunner(world).Add(new ThrowOnceInitSystem());
            Assert.That(() => systems.Init(), Throws.InvalidOperationException, "Init exception must propagate");
            Assert.That(systems.IsInitialized, Is.False, "runner must not report initialized after a failed Init");
        }

        [Test]
        public void Init_Retry_ResumesAtFailedSystem() {
            var world = new World();
            var first = new CountingInitSystem();
            var failing = new ThrowOnceInitSystem();
            var last = new CountingInitSystem();
            var systems = new SystemsRunner(world).Add(first).Add(failing).Add(last);

            Assert.That(() => systems.Init(), Throws.InvalidOperationException, "first Init must fail");
            systems.Init();

            Assert.That(first.InitCalls, Is.EqualTo(1), "system before the failure must not be initialized twice");
            Assert.That(failing.InitCalls, Is.EqualTo(2), "failed system must be retried");
            Assert.That(last.InitCalls, Is.EqualTo(1), "system after the failure must be initialized on retry");
            Assert.That(systems.IsInitialized, Is.True, "runner must be initialized after a successful retry");
        }

        [Test]
        public void Init_Twice_InitializesOnce() {
            var world = new World();
            var system = new CountingInitSystem();
            var systems = new SystemsRunner(world).Add(system);
            systems.Init();
            systems.Init();
            Assert.That(system.InitCalls, Is.EqualTo(1), "second Init must be a no-op");
        }

        [Test]
        public void Init_LogsRegistrationOrder() {
            var world = new World();
            var log = new List<string>();
            var systems = new SystemsRunner(world)
                .Add(new OrderTrackingSystem("a", log))
                .Add(new OrderTrackingSystem("b", log));
            systems.Init();
            Assert.That(log, Is.EqualTo(new[] { "init:a", "init:b" }), "Init must follow registration order");
        }
    }

    [TestFixture]
    public class RunnerRunContractTests {
        [Test]
        public void Run_SystemThrows_OneFrameStillCleaned() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.Add(e, new Damage());
            var systems = new SystemsRunner(world).Add(new ThrowingRunSystem()).OneFrame<Damage>();
            systems.Init();
            Assert.That(() => systems.Run(), Throws.InvalidOperationException, "system exception must propagate");
            Assert.That(world.Has<Damage>(e), Is.False, "one-frame components must be cleaned even when a system throws");
        }

        [Test]
        public void Run_SystemThrows_LaterSystemsSkipped() {
            var world = new World();
            var after = new CountingRunSystem();
            var systems = new SystemsRunner(world).Add(new ThrowingRunSystem()).Add(after);
            systems.Init();
            Assert.That(() => systems.Run(), Throws.InvalidOperationException, "system exception must propagate");
            Assert.That(after.Runs, Is.EqualTo(0), "systems after the failing one are not run in that frame");
        }

        [Test]
        public void DelHere_SystemBeforeSeesComponent() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.Add(e, new Damage());
            var before = new DamageCountingSystem();
            var systems = new SystemsRunner(world).Add(before).DelHere<Damage>();
            systems.Init();
            systems.Run();
            Assert.That(before.SeenLastRun, Is.EqualTo(1), "system registered before DelHere must see the component");
        }

        [Test]
        public void DelHere_SystemAfterDoesNotSeeComponent() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.Add(e, new Damage());
            var after = new DamageCountingSystem();
            var systems = new SystemsRunner(world).DelHere<Damage>().Add(after);
            systems.Init();
            systems.Run();
            Assert.That(after.SeenLastRun, Is.EqualTo(0), "system registered after DelHere must not see the component");
        }

        [Test]
        public void DelHere_ComponentGoneAfterRun() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.Add(e, new Damage());
            var systems = new SystemsRunner(world).DelHere<Damage>();
            systems.Init();
            systems.Run();
            Assert.That(world.Has<Damage>(e), Is.False, "DelHere must remove the component");
        }

        [Test]
        public void SetActive_OnNamedChild_SkipsChildRun() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.Add(e, new Velocity { X = 1 });
            var fixedRunner = new SystemsRunner(world).Add(new TestMovementSystem());
            var root = new SystemsRunner(world).Add(fixedRunner, "fixed");
            root.Init();

            root.SetActive("fixed", false);
            root.GetRunner("fixed").Run();
            Assert.That(world.Get<Position>(e).X, Is.EqualTo(0f), "disabled child runner must not run its systems");
            Assert.That(root.IsActive("fixed"), Is.False, "IsActive must reflect the child runner state");

            root.SetActive("fixed", true);
            root.GetRunner("fixed").Run();
            Assert.That(world.Get<Position>(e).X, Is.EqualTo(1f), "re-enabled child runner must run again");
        }

        [Test]
        public void SetActive_UnknownName_IsIgnoredInRelease() {
#if !KENSEI_DEBUG
            var world = new World();
            var systems = new SystemsRunner(world);
            Assert.That(() => systems.SetActive("nope", false), Throws.Nothing, "unknown name is ignored in release");
            Assert.That(systems.IsActive("nope"), Is.False, "unknown name reports inactive in release");
            Assert.That(systems.GetRunner("nope"), Is.Null, "unknown runner name returns null in release");
#else
            Assert.Pass("release-only contract");
#endif
        }
    }

    [TestFixture]
    public class RunnerDestroyContractTests {
        [Test]
        public void Destroy_ReverseRegistrationOrder() {
            var world = new World();
            var log = new List<string>();
            var systems = new SystemsRunner(world)
                .Add(new OrderTrackingSystem("a", log))
                .Add(new OrderTrackingSystem("b", log));
            systems.Init();
            log.Clear();
            systems.Destroy();
            Assert.That(log, Is.EqualTo(new[] { "destroy:b", "destroy:a" }), "Destroy must run in reverse registration order");
        }

        [Test]
        public void Destroy_Twice_DestroysOnce() {
            var world = new World();
            var log = new List<string>();
            var systems = new SystemsRunner(world).Add(new OrderTrackingSystem("a", log));
            systems.Init();
            systems.Destroy();
            systems.Destroy();
            Assert.That(log.FindAll(s => s == "destroy:a").Count, Is.EqualTo(1), "second Destroy must be a no-op");
        }

        [Test]
        public void Destroy_WithoutInit_IsNoOp() {
            var world = new World();
            var system = new TestDestroySystem();
            var systems = new SystemsRunner(world).Add(system);
            systems.Destroy();
            Assert.That(system.Destroyed, Is.False, "Destroy on an uninitialized runner must not call systems");
        }

        [Test]
        public void Destroy_ThenInit_ReinitializesSystems() {
            var world = new World();
            var system = new CountingInitSystem();
            var systems = new SystemsRunner(world).Add(system);
            systems.Init();
            systems.Destroy();
            systems.Init();
            Assert.That(system.InitCalls, Is.EqualTo(2), "runner must be reusable after Destroy");
            Assert.That(systems.IsInitialized, Is.True, "runner must report initialized after re-Init");
        }
    }

    [TestFixture]
    public class RunnerSharedDataTests {
        [Test]
        public void ChildWithoutSharedData_InheritsParentShared() {
            var world = new World();
            var shared = new SharedData();
            var capture = new SharedCaptureSystem();
            var child = new SystemsRunner(world).Add(capture);
            var root = new SystemsRunner(world, shared).Add(child, "fixed");
            root.Init();
            Assert.That(capture.Received, Is.SameAs(shared), "child runner must receive the parent's SharedData");
        }

        [Test]
        public void ChildInitializedDirectly_UsesParentShared() {
            var world = new World();
            var shared = new SharedData();
            shared.Add(new TestService { Value = 7 });
            var system = new TestSharedDataSystem();
            var child = new SystemsRunner(world).Add(system);
            new SystemsRunner(world, shared).Add(child, "fixed");
            child.Init();
            Assert.That(system.ReceivedValue, Is.EqualTo(7), "child initialized on its own must still see the parent's services");
        }

        [Test]
        public void Shared_ExposesContainer() {
            var world = new World();
            var shared = new SharedData();
            var systems = new SystemsRunner(world, shared);
            Assert.That(systems.Shared, Is.SameAs(shared), "Shared must expose the container passed to the constructor");
        }
    }
}
