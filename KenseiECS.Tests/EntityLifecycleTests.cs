using NUnit.Framework;

namespace KenseiECS.Tests {
    [TestFixture]
    public class EntityLifecycleTests {
        [Test]
        public void CreateEntity_EntityIsAlive() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            Assert.That(world.IsAlive(e), Is.True, "created entity must be alive");
        }

        [Test]
        public void CreateEntity_EntityCountIsOne() {
            var world = new World();
            world.CreateEntity(new Position());
            Assert.That(world.EntityCount, Is.EqualTo(1), "world must contain exactly one entity after CreateEntity");
        }

        [Test]
        public void DestroyEntity_EntityIsNotAlive() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.DestroyEntity(e);
            Assert.That(world.IsAlive(e), Is.False, "destroyed entity must not be alive");
        }

        [Test]
        public void DestroyEntity_EntityCountIsZero() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.DestroyEntity(e);
            Assert.That(world.EntityCount, Is.EqualTo(0), "entity count must drop to zero after destroy");
        }

        [Test]
        public void DoubleDestroy_EntityCountStaysZero() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.DestroyEntity(e);
            world.DestroyEntity(e);
            Assert.That(world.EntityCount, Is.EqualTo(0), "second destroy of the same handle must be a no-op");
        }

        private static (World world, Entity stale, Entity reborn) CreateStaleAndReborn() {
            var world = new World();
            var stale = world.CreateEntity(new Position());
            world.DestroyEntity(stale);
            var reborn = world.CreateEntity(new Position());
            return (world, stale, reborn);
        }

        [Test]
        public void Aliasing_RebornEntityReusesIndex() {
            var (_, stale, reborn) = CreateStaleAndReborn();
            Assert.That(reborn.Index, Is.EqualTo(stale.Index), "new entity must reuse the freed slot index");
        }

        [Test]
        public void Aliasing_RebornEntityHasDifferentGeneration() {
            var (_, stale, reborn) = CreateStaleAndReborn();
            Assert.That(reborn.Generation, Is.Not.EqualTo(stale.Generation), "reused slot must get a new generation");
        }

        [Test]
        public void Aliasing_StaleHandleIsDead() {
            var (world, stale, _) = CreateStaleAndReborn();
            Assert.That(world.IsAlive(stale), Is.False, "stale handle must not alias the reborn entity");
        }

        [Test]
        public void Aliasing_RebornEntityIsAlive() {
            var (world, _, reborn) = CreateStaleAndReborn();
            Assert.That(world.IsAlive(reborn), Is.True, "reborn entity must be alive");
        }

        [Test]
        public void SlotReuse_RebornEntityReusesIndex() {
            var (_, stale, reborn) = CreateStaleAndReborn();
            Assert.That(reborn.Index, Is.EqualTo(stale.Index), "freed slot must be reused first");
        }

        [Test]
        public void SlotReuse_GenerationIsIncremented() {
            var (_, _, reborn) = CreateStaleAndReborn();
            Assert.That(reborn.Generation, Is.EqualTo(2), "generation must be incremented on slot reuse");
        }

        [Test]
        public void SlotReuse_RebornEntityHasNoStaleComponents() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.Add(e, new Velocity());
            world.DestroyEntity(e);
            var reborn = world.CreateEntity(new Health());
            Assert.That(world.Has<Position>(reborn), Is.False, "reused slot must start with a clean component mask");
            Assert.That(world.Has<Velocity>(reborn), Is.False, "reused slot must start with a clean component mask");
        }

        [Test]
        public void RemoveNonLastComponent_EntityStaysAlive() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.Add(e, new Velocity());
            world.Remove<Velocity>(e);
            Assert.That(world.IsAlive(e), Is.True, "entity with a remaining component must stay alive");
        }

        [Test]
        public void RemoveLastComponent_EntityIsDestroyed() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.Remove<Position>(e);
            Assert.That(world.IsAlive(e), Is.False, "entity must be auto-destroyed when its last component is removed");
        }

        [Test]
        public void RemoveLastComponent_EntityCountIsZero() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.Remove<Position>(e);
            Assert.That(world.EntityCount, Is.EqualTo(0), "auto-destroy must update the entity count");
        }

        [Test]
        public void RemoveLastComponent_AfterPriorRemoval_EntityIsDestroyed() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.Add(e, new Velocity());
            world.Remove<Velocity>(e);
            world.Remove<Position>(e);
            Assert.That(world.IsAlive(e), Is.False, "auto-destroy must fire when the last component is removed after prior removals");
        }

        private static (World world, ReentrantDestroyListener listener) RunReentrantDestroy() {
            var world = new World();
            var listener = new ReentrantDestroyListener { World = world };
            world.AddEventListener(listener);
            var e = world.CreateEntity(new Position());
            world.DestroyEntity(e);
            world.RemoveEventListener(listener);
            return (world, listener);
        }

        [Test]
        public void ReentrantDestroy_EntityCountIsZero() {
            var (world, _) = RunReentrantDestroy();
            Assert.That(world.EntityCount, Is.EqualTo(0), "reentrant destroy must not corrupt the entity count");
        }

        [Test]
        public void ReentrantDestroy_ListenerObservesDeadEntity() {
            var (_, listener) = RunReentrantDestroy();
            Assert.That(listener.WasDeadInsideListener, Is.True, "entity must already be dead inside OnEntityDestroyed");
        }

        [Test]
        public void ReentrantDestroy_SubsequentCreatesUseDistinctSlots() {
            var (world, _) = RunReentrantDestroy();
            var a = world.CreateEntity(new Position());
            var b = world.CreateEntity(new Velocity());
            Assert.That(a.Index, Is.Not.EqualTo(b.Index), "reentrant destroy must not put the slot on the free list twice");
        }

        [Test]
        public void ReentrantDestroy_SubsequentCreatesAreAlive() {
            var (world, _) = RunReentrantDestroy();
            var a = world.CreateEntity(new Position());
            var b = world.CreateEntity(new Velocity());
            Assert.That(world.IsAlive(a), Is.True, "first entity created after reentrant destroy must be alive");
            Assert.That(world.IsAlive(b), Is.True, "second entity created after reentrant destroy must be alive");
        }

        [Test]
        public void ReentrantDestroy_SubsequentEntityCountIsTwo() {
            var (world, _) = RunReentrantDestroy();
            world.CreateEntity(new Position());
            world.CreateEntity(new Velocity());
            Assert.That(world.EntityCount, Is.EqualTo(2), "entity count must stay consistent after reentrant destroy");
        }

        private static (World world, Entity destroyed) RunReAddDuringDestroy() {
            var world = new World();
            var listener = new ReAddOnRemoveListener { World = world };
            world.AddEventListener(listener);
            var e = world.CreateEntity(new Position());
            listener.Armed = true;
            world.DestroyEntity(e);
            world.RemoveEventListener(listener);
            return (world, e);
        }

        [Test]
        public void ReAddDuringDestroy_PoolStaysEmpty() {
            var (world, destroyed) = RunReAddDuringDestroy();
            Assert.That(world.Pool<Position>().Has(destroyed.Index), Is.False, "component re-added during destroy must not survive");
        }

        [Test]
        public void ReAddDuringDestroy_EntityCountIsZero() {
            var (world, _) = RunReAddDuringDestroy();
            Assert.That(world.EntityCount, Is.EqualTo(0), "entity must stay destroyed despite re-add from listener");
        }

        [Test]
        public void ReAddDuringDestroy_RebornEntityAcceptsComponents() {
            var (world, _) = RunReAddDuringDestroy();
            var reborn = world.CreateEntity(new Health());
            world.Add(reborn, new Position { X = 5 });
            Assert.That(world.Get<Position>(reborn).X, Is.EqualTo(5f), "reborn entity must work with the same pool");
        }
    }

    [TestFixture]
    public class CopyEntityTests {
        private World _world;
        private Entity _source;
        private Entity _copy;

        [SetUp]
        public void SetUp() {
            _world = new World();
            _source = _world.CreateEntity(new Position { X = 10, Y = 20 });
            _world.Add(_source, new Health { Value = 100 });
            _copy = _world.CopyEntity(_source);
        }

        [Test]
        public void Copy_IsAlive() {
            Assert.That(_world.IsAlive(_copy), Is.True, "copy must be alive");
        }

        [Test]
        public void Copy_IsDifferentEntity() {
            Assert.That(_copy != _source, Is.True, "copy must be a distinct entity");
        }

        [Test]
        public void Copy_HasPosition() {
            Assert.That(_world.Has<Position>(_copy), Is.True, "copy must receive Position");
        }

        [Test]
        public void Copy_HasHealth() {
            Assert.That(_world.Has<Health>(_copy), Is.True, "copy must receive Health");
        }

        [Test]
        public void Copy_PositionValueCopied() {
            Assert.That(_world.Get<Position>(_copy).X, Is.EqualTo(10f), "copy must carry the source Position data");
        }

        [Test]
        public void Copy_HealthValueCopied() {
            Assert.That(_world.Get<Health>(_copy).Value, Is.EqualTo(100f), "copy must carry the source Health data");
        }

        [Test]
        public void Copy_IsIndependentOfSource() {
            ref var srcPos = ref _world.Get<Position>(_source);
            srcPos.X = 999;
            Assert.That(_world.Get<Position>(_copy).X, Is.EqualTo(10f), "mutating the source must not affect the copy");
        }
    }
}
