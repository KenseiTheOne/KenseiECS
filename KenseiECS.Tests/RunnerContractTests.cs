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
        public void Init_Retry_StartsOverAfterRollback() {
            var world = new World();
            var first = new CountingInitSystem();
            var failing = new ThrowOnceInitSystem();
            var last = new CountingInitSystem();
            var systems = new SystemsRunner(world).Add(first).Add(failing).Add(last);

            Assert.That(() => systems.Init(), Throws.InvalidOperationException, "first Init must fail");
            systems.Init();

            Assert.That(first.InitCalls, Is.EqualTo(2), "2.2.0: a failed Init rolls back, so the retry starts from the first system");
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
            var root = new SystemsRunner(world).AddPhase("fixed", fixedRunner);
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
            var root = new SystemsRunner(world, shared).AddPhase("fixed", child);
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
            new SystemsRunner(world, shared).AddPhase("fixed", child);
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

        // Built from the bottom up: the innermost runner joins its group before the group joins the
        // root. Up to 2.2.2 its Shared kept the group's own SharedData from that moment, while its
        // systems got the root's in Init.
        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void RunnerAddedBeforeItsParentJoinsTheRoot_SharedIsWhatItsSystemsGet(bool rootHasShared, bool phases) {
            var world = new World();
            var shared = new SharedData();
            var capture = new SharedCaptureSystem();
            var inner = new SystemsRunner(world).Add(capture);
            var middle = new SystemsRunner(world);
            var root = rootHasShared ? new SystemsRunner(world, shared) : new SystemsRunner(world);
            if (phases) {
                middle.AddPhase("inner", inner);
                root.AddPhase("middle", middle);
            } else {
                middle.AddGroup("inner", inner);
                root.AddGroup("middle", middle);
            }

            root.Init();

            Assert.That(capture.Received, Is.SameAs(root.Shared), "test setup: systems get the root's SharedData");
            if (rootHasShared) {
                Assert.That(root.Shared, Is.SameAs(shared));
            }
            Assert.That(inner.Shared, Is.SameAs(root.Shared), "Shared is the SharedData passed to the runner's systems in Init");
            Assert.That(middle.Shared, Is.SameAs(root.Shared));
            Assert.That(root.GetRunner("middle").GetRunner("inner").Shared, Is.SameAs(root.Shared));
        }

        [Test]
        public void ChildWithItsOwnSharedData_KeepsIt() {
            var world = new World();
            var shared = new SharedData();
            var child = new SystemsRunner(world, shared);
            var group = new SystemsRunner(world).AddGroup("child", child);
            new SystemsRunner(world, shared).AddGroup("group", group);

            Assert.That(child.Shared, Is.SameAs(shared));
            Assert.That(group.Shared, Is.SameAs(shared));
        }

#if KENSEI_DEBUG
        // The group has no SharedData of its own yet, so the SharedData its runners will get is not
        // known: up to 2.2.2 the check compared it with the group's own empty one and rejected a runner
        // constructed with the root's.
        [Test]
        public void RunnerWithTheRootsSharedData_AddedToAGroupBeforeTheGroupJoinsTheRoot_IsAccepted() {
            var world = new World();
            var shared = new SharedData();
            var capture = new SharedCaptureSystem();
            var child = new SystemsRunner(world, shared).Add(capture);
            var group = new SystemsRunner(world);

            Assert.That(() => group.AddGroup("child", child), Throws.Nothing);
            var root = new SystemsRunner(world, shared).AddGroup("group", group);
            Assert.That(() => root.Init(), Throws.Nothing);
            Assert.That(capture.Received, Is.SameAs(shared));
        }

        [Test]
        public void RunnerWithOtherSharedData_InAGroupAddedToTheRoot_ThrowsWhenTheGroupIsAdded() {
            var world = new World();
            var child = new SystemsRunner(world, new SharedData());
            var group = new SystemsRunner(world);
            group.AddGroup("child", child);
            var root = new SystemsRunner(world, new SharedData());

            var error = Assert.Throws<System.InvalidOperationException>(() => root.AddGroup("group", group));

            Assert.That(error.Message, Does.Contain("different SharedData"));
            Assert.That(root.SystemCount, Is.EqualTo(0), "a rejected call registers nothing");
        }

        [Test]
        public void RunnerWithItsOwnSharedData_UnderARootWithout_ThrowsInInit() {
            var world = new World();
            var root = new SystemsRunner(world);
            Assert.That(() => root.AddGroup("child", new SystemsRunner(world, new SharedData())), Throws.Nothing,
                "the root may still be added to a runner with that SharedData");

            Assert.That(() => root.Init(), Throws.InvalidOperationException.With.Message.Contains("different SharedData"));
        }
#endif
    }
}
