using NUnit.Framework;

namespace KenseiECS.Tests {
    [TestFixture]
    public class WorldEventsTests {
        private World _world;
        private CountingWorldListener _listener;
        private Entity _entity;

        [SetUp]
        public void SetUp() {
            _world = new World();
            _listener = new CountingWorldListener();
            _world.AddEventListener(_listener);
            _entity = _world.CreateEntity(new Position());
        }

        [Test]
        public void CreateEntity_FiresEntityCreated() {
            Assert.That(_listener.CreatedCount, Is.EqualTo(1), "CreateEntity must fire OnEntityCreated");
        }

        [Test]
        public void CreateEntity_FiresComponentAdded() {
            Assert.That(_listener.AddedCount, Is.EqualTo(1), "CreateEntity must fire OnComponentAdded for the initial component");
        }

        [Test]
        public void AddComponent_FiresComponentAdded() {
            _world.Add(_entity, new Velocity());
            Assert.That(_listener.AddedCount, Is.EqualTo(2), "Add must fire OnComponentAdded");
        }

        [Test]
        public void RemoveComponent_FiresComponentRemoved() {
            _world.Add(_entity, new Velocity());
            _world.Remove<Velocity>(_entity);
            Assert.That(_listener.RemovedCount, Is.EqualTo(1), "Remove must fire OnComponentRemoved");
        }

        [Test]
        public void DestroyEntity_FiresEntityDestroyed() {
            _world.DestroyEntity(_entity);
            Assert.That(_listener.DestroyedCount, Is.EqualTo(1), "DestroyEntity must fire OnEntityDestroyed");
        }
    }
}
