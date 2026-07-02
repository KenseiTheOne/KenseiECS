using NUnit.Framework;

namespace KenseiECS.Tests {
    [TestFixture]
    public class WorldLifecycleTests {
        private static World CreatePopulatedWorld() {
            var world = new World();
            world.CreateEntity(new Position());
            var e2 = world.CreateEntity(new Position());
            world.Add(e2, new Velocity());
            return world;
        }

        [Test]
        public void PopulatedWorld_HasTwoEntitiesBeforeClear() {
            var world = CreatePopulatedWorld();
            Assert.That(world.EntityCount, Is.EqualTo(2), "setup must produce two live entities");
        }

        [Test]
        public void Clear_EntityCountIsZero() {
            var world = CreatePopulatedWorld();
            world.Clear();
            Assert.That(world.EntityCount, Is.EqualTo(0), "Clear must destroy all entities");
        }

        [Test]
        public void Clear_PoolsAreEmpty() {
            var world = CreatePopulatedWorld();
            world.Clear();
            Assert.That(world.Pool<Position>().Count, Is.EqualTo(0), "Clear must empty component pools");
        }

        [Test]
        public void Clear_WorldIsReusable() {
            var world = CreatePopulatedWorld();
            world.Clear();
            var e = world.CreateEntity(new Position { X = 1 });
            Assert.That(world.IsAlive(e), Is.True, "world must accept new entities after Clear");
        }

        [Test]
        public void Clear_CallsAutoResetForEachLiveComponent() {
            var world = new World();
            world.CreateEntity(new ResetTracked { V = 1 });
            world.CreateEntity(new ResetTracked { V = 2 });

            ResetTracked.ResetCalls = 0;
            world.Clear();

            Assert.That(ResetTracked.ResetCalls, Is.EqualTo(2), "Clear must call AutoReset once per live component");
        }

        [Test]
        public void Destroy_CompletesWithoutErrors() {
            var world = new World();
            world.CreateEntity(new Position());
            Assert.That(() => world.Destroy(), Throws.Nothing, "Destroy on a populated world must not throw");
        }
    }

    [TestFixture]
    public class WarmupTests {
        private World _world;
        private Entity _entity;
        private Filter _filter;

        [SetUp]
        public void SetUp() {
            _world = new World();
            _entity = _world.CreateEntity(new Position { X = 7 });
            _world.Pool<Velocity>();
            _filter = _world.Filter().Inc<Position>().End();
            _world.Warmup();
        }

        [Test]
        public void Warmup_EntityStaysAlive() {
            Assert.That(_world.IsAlive(_entity), Is.True, "Warmup must not destroy live entities");
        }

        [Test]
        public void Warmup_ComponentDataIntact() {
            Assert.That(_world.Get<Position>(_entity).X, Is.EqualTo(7f), "Warmup must preserve component data");
        }

        [Test]
        public void Warmup_EntityCountUnchanged() {
            Assert.That(_world.EntityCount, Is.EqualTo(1), "Warmup must not change the entity count");
        }

        [Test]
        public void Warmup_FilterIntact() {
            Assert.That(_filter.Count, Is.EqualTo(1), "Warmup must preserve filter contents");
            Assert.That(_filter.Contains(_entity.Index), Is.True, "Warmup must keep the entity in the filter");
        }
    }
}
