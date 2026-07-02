using NUnit.Framework;

namespace KenseiECS.Tests {
    [TestFixture]
    public class SystemsRunnerTests {
        private static (World world, Entity entity) RunMovementSystem() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.Add(e, new Velocity { X = 1, Y = 2 });

            var systems = new SystemsRunner(world)
                .Add(new TestMovementSystem());
            systems.Init();
            systems.Run();
            return (world, e);
        }

        [Test]
        public void Run_AppliesVelocityToX() {
            var (world, entity) = RunMovementSystem();
            Assert.That(world.Get<Position>(entity).X, Is.EqualTo(1f), "movement system must apply Velocity.X");
        }

        [Test]
        public void Run_AppliesVelocityToY() {
            var (world, entity) = RunMovementSystem();
            Assert.That(world.Get<Position>(entity).Y, Is.EqualTo(2f), "movement system must apply Velocity.Y");
        }

        [Test]
        public void NestedRunner_UnnamedGroupRunsInline() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.Add(e, new Velocity { X = 1, Y = 2 });

            var inner = new SystemsRunner(world).Add(new TestMovementSystem());
            var root = new SystemsRunner(world).Add(inner);
            root.Init();
            root.Run();

            Assert.That(world.Get<Position>(e).X, Is.EqualTo(1f), "unnamed nested runner must run with the parent");
        }

        private static (World world, Entity entity, SystemsRunner systems) CreateNamedMovementRunner() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.Add(e, new Velocity { X = 1 });

            var systems = new SystemsRunner(world)
                .Add(new TestMovementSystem(), "movement");
            systems.Init();
            return (world, e, systems);
        }

        [Test]
        public void SetActiveFalse_SystemIsSkipped() {
            var (world, entity, systems) = CreateNamedMovementRunner();
            systems.SetActive("movement", false);
            systems.Run();
            Assert.That(world.Get<Position>(entity).X, Is.EqualTo(0f), "disabled system must not run");
        }

        [Test]
        public void SetActiveTrue_SystemRunsAgain() {
            var (world, entity, systems) = CreateNamedMovementRunner();
            systems.SetActive("movement", false);
            systems.Run();
            systems.SetActive("movement", true);
            systems.Run();
            Assert.That(world.Get<Position>(entity).X, Is.EqualTo(1f), "re-enabled system must run");
        }

        private static (World world, Entity entity, SystemsRunner systems) CreateOneFrameRunner() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.Add(e, new Damage { Value = 10 });

            var systems = new SystemsRunner(world)
                .OneFrame<Damage>();
            systems.Init();
            return (world, e, systems);
        }

        [Test]
        public void OneFrame_ComponentPresentBeforeRun() {
            var (world, entity, _) = CreateOneFrameRunner();
            Assert.That(world.Has<Damage>(entity), Is.True, "one-frame component must survive until the first run");
        }

        [Test]
        public void OneFrame_ComponentRemovedAfterRun() {
            var (world, entity, systems) = CreateOneFrameRunner();
            systems.Run();
            Assert.That(world.Has<Damage>(entity), Is.False, "one-frame component must be removed by Run");
        }

        [Test]
        public void OneFrame_EntityStaysAlive() {
            var (world, entity, systems) = CreateOneFrameRunner();
            systems.Run();
            Assert.That(world.IsAlive(entity), Is.True, "entity with other components must survive one-frame cleanup");
        }

        [Test]
        public void Destroy_CallsDestroySystems() {
            var world = new World();
            var system = new TestDestroySystem();
            var systems = new SystemsRunner(world).Add(system);
            systems.Init();
            systems.Run();
            systems.Destroy();
            Assert.That(system.Destroyed, Is.True, "Destroy must invoke IDestroySystem.Destroy");
        }

        [Test]
        public void SharedData_ServiceIsInjectedIntoSystem() {
            var world = new World();
            var shared = new SharedData();
            shared.Add(new TestService { Value = 42 });

            var system = new TestSharedDataSystem();
            var systems = new SystemsRunner(world, shared)
                .Add(system);
            systems.Init();

            Assert.That(system.ReceivedValue, Is.EqualTo(42), "system must receive the shared service in Init");
        }
    }

    [TestFixture]
    public class RootChildRunnerTests {
        private World _world;
        private Entity _entity;
        private SystemsRunner _root;
        private SystemsRunner _fixedRunner;

        [SetUp]
        public void SetUp() {
            _world = new World();
            _entity = _world.CreateEntity(new Position());
            _world.Add(_entity, new Velocity { X = 1 });
            _world.Add(_entity, new Damage { Value = 10 });
            _world.Add(_entity, new Frozen());

            _fixedRunner = new SystemsRunner(_world).OneFrame<Frozen>();
            _root = new SystemsRunner(_world)
                .Add(new TestMovementSystem())
                .Add(_fixedRunner, "fixed")
                .OneFrame<Damage>();
            _root.Init();
        }

        [Test]
        public void GetRunner_ReturnsNamedChild() {
            Assert.That(_root.GetRunner("fixed"), Is.SameAs(_fixedRunner), "GetRunner must return the registered child");
        }

        [Test]
        public void RootRun_AdvancesTick() {
            _root.Run();
            Assert.That(_world.Tick, Is.EqualTo(1), "root Run must advance the world tick");
        }

        [Test]
        public void RootRun_ExecutesOwnSystems() {
            _root.Run();
            Assert.That(_world.Get<Position>(_entity).X, Is.EqualTo(1f), "root Run must execute its own systems");
        }

        [Test]
        public void RootRun_CleansOwnOneFrameComponents() {
            _root.Run();
            Assert.That(_world.Has<Damage>(_entity), Is.False, "root Run must clean its own one-frame components");
        }

        [Test]
        public void RootRun_DoesNotRunNamedChild() {
            _root.Run();
            Assert.That(_world.Has<Frozen>(_entity), Is.True, "named child must not run as part of root Run");
        }

        [Test]
        public void NamedChildRun_DoesNotAdvanceTick() {
            _root.Run();
            _root.GetRunner("fixed").Run();
            Assert.That(_world.Tick, Is.EqualTo(1), "child Run must not advance the world tick");
        }

        [Test]
        public void NamedChildRun_CleansChildOneFrameComponents() {
            _root.Run();
            _root.GetRunner("fixed").Run();
            Assert.That(_world.Has<Frozen>(_entity), Is.False, "child Run must clean its own one-frame components");
        }

        [Test]
        public void SecondRootRun_AdvancesTickAgain() {
            _root.Run();
            _root.GetRunner("fixed").Run();
            _root.Run();
            Assert.That(_world.Tick, Is.EqualTo(2), "each root Run must advance the tick exactly once");
        }
    }
}
