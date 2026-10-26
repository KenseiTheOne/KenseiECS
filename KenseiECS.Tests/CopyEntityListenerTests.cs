using System.Collections.Generic;
using System.Runtime.CompilerServices;
using NUnit.Framework;

namespace KenseiECS.Tests {
    // CopyEntity and CreateEntity<T> with listeners that destroy the entity being made (or the
    // source), and may create an entity that takes the freed slot.
    [TestFixture]
    public class CopyEntityListenerTests {
        // Registered in this order (lowest index first), so CopyEntity copies the trigger first.
        private struct CeTrigger : IComponent {
        }

        private struct CePosition : IComponent {
            public int V;
        }

        private struct CeVelocity : IComponent {
            public int V;
        }

        private struct CeMarker : IComponent {
        }

        private struct CeEnemy : IComponent {
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int[] RegisterInOrder() {
            return new[] {
                ComponentType<CeTrigger>.Index,
                ComponentType<CePosition>.Index,
                ComponentType<CeVelocity>.Index,
                ComponentType<CeMarker>.Index,
                ComponentType<CeEnemy>.Index
            };
        }

        [OneTimeSetUp]
        public void RegisterTypes() {
            var indices = RegisterInOrder();
            for (int i = 1; i < indices.Length; i++) {
                Assert.That(indices[i], Is.GreaterThan(indices[i - 1]), "test setup: CopyEntity must reach the trigger type first");
            }
        }

        // Destroys every entity that gets the trigger except the source; optionally spawns an
        // entity right after, which takes the freed slot.
        private sealed class DestroyOnAdded : IComponentListener<CeTrigger> {
            public World World;
            public int Source = -1;
            public bool Spawn;
            public Entity Spawned;

            public void OnAdded(int entityIndex, ref CeTrigger component) {
                if (entityIndex == Source) {
                    return;
                }
                World.DestroyEntity(World.GetEntity(entityIndex));
                if (Spawn) {
                    Spawned = World.CreateEntity(new CeEnemy());
                }
            }

            public void OnRemoved(int entityIndex, ref CeTrigger component) {
            }
        }

        private sealed class DestroyOnEnter : IFilterListener {
            public World World;
            public int Source = -1;

            public void OnEntityAdded(Filter filter, int entityIndex) {
                if (entityIndex != Source) {
                    World.DestroyEntity(World.GetEntity(entityIndex));
                }
            }

            public void OnEntityRemoved(Filter filter, int entityIndex) {
            }
        }

        // The copy's first component destroys the source and spawns an entity in its slot.
        private sealed class ReplaceSourceOnAdded : IComponentListener<CeTrigger> {
            public World World;
            public Entity Source;
            public Entity Spawned;

            public void OnAdded(int entityIndex, ref CeTrigger component) {
                if (entityIndex == Source.Index || !World.IsAlive(Source)) {
                    return;
                }
                World.DestroyEntity(Source);
                Spawned = World.CreateEntity(new CePosition { V = 99 });
                World.Add(Spawned, new CeVelocity { V = 98 });
            }

            public void OnRemoved(int entityIndex, ref CeTrigger component) {
            }
        }

        // OnEntityCreated with the handle the slot holds at that moment.
        private sealed class CreatedLog : WorldListenerBase {
            public World World;
            public readonly List<Entity> Created = new();

            public override void OnEntityCreated(int entityIndex) =>
                Created.Add(World.GetEntity(entityIndex));
        }

        private static Entity Source(World world) {
            var source = world.CreateEntity(new CeTrigger());
            world.Add(source, new CePosition { V = 5 });
            world.Add(source, new CeVelocity { V = 6 });
            return source;
        }

        // Up to 2.2.1 the copy loop went on into the freed slot: in release the next entity there
        // inherited the components (count 1, three in the pools), OnEntityCreated came for the dead
        // copy; under KENSEI_DEBUG CopyEntity threw ("an int index from a filter...").
        [Test]
        public void ListenerDestroysCopy_FreedSlotGetsNoComponents() {
            var world = new World();
            var source = Source(world);
            world.Pool<CeTrigger>().AddListener(new DestroyOnAdded { World = world, Source = source.Index });
            var log = new CreatedLog { World = world };
            world.AddEventListener(log);

            var copy = world.CopyEntity(source);

            Assert.That(world.IsAlive(copy), Is.False, "the listener destroyed the copy");
            Assert.That(log.Created, Is.Empty, "the destroyed copy was never created for world listeners");
            WorldInvariants.AssertConsistent(world, "after CopyEntity");
            var next = world.CreateEntity(new CeMarker());
            Assert.That(next.Index, Is.EqualTo(copy.Index), "test setup: the next entity reuses the copy's slot");
            Assert.That(world.Has<CePosition>(next), Is.False, "the next entity in the freed slot inherits nothing");
            Assert.That(world.Has<CeVelocity>(next), Is.False);
            Assert.That(world.GetComponentCount(next), Is.EqualTo(1));
            Assert.That(world.GetComponentCount(source), Is.EqualTo(3), "the source keeps its components");
            WorldInvariants.AssertConsistent(world, "after the next CreateEntity");
        }

        // The spawned entity took the freed slot: on 2.2.1 it got the remaining components (both
        // builds) and a second OnEntityCreated.
        [Test]
        public void ListenerDestroysCopyAndSpawns_SpawnedEntityKeepsItsOwnComponent() {
            var world = new World();
            var source = Source(world);
            var listener = new DestroyOnAdded { World = world, Source = source.Index, Spawn = true };
            world.Pool<CeTrigger>().AddListener(listener);
            var log = new CreatedLog { World = world };
            world.AddEventListener(log);

            var copy = world.CopyEntity(source);

            var spawned = listener.Spawned;
            Assert.That(spawned.Index, Is.EqualTo(copy.Index), "test setup: the spawned entity takes the copy's slot");
            Assert.That(world.IsAlive(copy), Is.False);
            Assert.That(world.Has<CeEnemy>(spawned), Is.True);
            Assert.That(world.Has<CePosition>(spawned), Is.False, "the copy's remaining components must not reach the spawned entity");
            Assert.That(world.Has<CeVelocity>(spawned), Is.False);
            Assert.That(world.GetComponentCount(spawned), Is.EqualTo(1));
            Assert.That(log.Created, Is.EqualTo(new[] { spawned }), "one OnEntityCreated, from the spawn itself");
            WorldInvariants.AssertConsistent(world, "after CopyEntity");
        }

        [Test]
        public void FilterListenerDestroysCopy_FreedSlotGetsNoComponents() {
            var world = new World();
            var source = Source(world);
            world.Filter().Inc<CeTrigger>().End().AddListener(new DestroyOnEnter { World = world, Source = source.Index });

            var copy = world.CopyEntity(source);

            Assert.That(world.IsAlive(copy), Is.False);
            WorldInvariants.AssertConsistent(world, "after CopyEntity");
            var next = world.CreateEntity(new CeMarker());
            Assert.That(world.GetComponentCount(next), Is.EqualTo(1), "the next entity in the freed slot inherits nothing");
            WorldInvariants.AssertConsistent(world, "after the next CreateEntity");
        }

        // The entity spawned in the source's slot has components of the types still to be copied:
        // 2.2.1 copied those (its position and velocity) into the copy.
        [Test]
        public void ListenerReplacesSource_CopyGetsNothingOfTheNewEntity() {
            var world = new World();
            var source = Source(world);
            var listener = new ReplaceSourceOnAdded { World = world, Source = source };
            world.Pool<CeTrigger>().AddListener(listener);
            var log = new CreatedLog { World = world };
            world.AddEventListener(log);

            var copy = world.CopyEntity(source);

            Assert.That(listener.Spawned.Index, Is.EqualTo(source.Index), "test setup: the spawned entity takes the source's slot");
            Assert.That(world.IsAlive(copy), Is.True, "the copy keeps what it got before the source was destroyed");
            Assert.That(world.Has<CeTrigger>(copy), Is.True);
            Assert.That(world.Has<CePosition>(copy), Is.False, "the spawned entity's position is not the source's");
            Assert.That(world.Has<CeVelocity>(copy), Is.False);
            Assert.That(world.Get<CePosition>(listener.Spawned).V, Is.EqualTo(99));
            Assert.That(log.Created, Does.Contain(copy), "the copy exists, so it is created for world listeners");
            WorldInvariants.AssertConsistent(world, "after CopyEntity");
        }

        // 2.2.1 sent OnEntityCreated for the entity its first component's listener had destroyed.
        [Test]
        public void CreateEntity_ListenerDestroysTheEntity_NoEntityCreatedEvent() {
            var world = new World();
            world.Pool<CeTrigger>().AddListener(new DestroyOnAdded { World = world });
            var log = new CreatedLog { World = world };
            world.AddEventListener(log);

            var e = world.CreateEntity(new CeTrigger());

            Assert.That(world.IsAlive(e), Is.False);
            Assert.That(log.Created, Is.Empty, "the entity never existed for world listeners");
            WorldInvariants.AssertConsistent(world, "after CreateEntity");
        }

        [Test]
        public void CreateEntity_ListenerDestroysAndSpawns_OneEntityCreatedEventForTheSpawn() {
            var world = new World();
            var listener = new DestroyOnAdded { World = world, Spawn = true };
            world.Pool<CeTrigger>().AddListener(listener);
            var log = new CreatedLog { World = world };
            world.AddEventListener(log);

            var e = world.CreateEntity(new CeTrigger());

            Assert.That(listener.Spawned.Index, Is.EqualTo(e.Index), "test setup: the spawn takes the slot");
            Assert.That(log.Created, Is.EqualTo(new[] { listener.Spawned }), "OnEntityCreated once, for the spawned entity");
        }

        // Copies the entity whose last component is being removed, once.
        private sealed class CopyOnLastRemoved : WorldListenerBase, IFilterListener {
            public World World;
            public Entity Copy = Entity.Null;
            public bool Copied;

            public override void OnComponentRemoved(int entityIndex, int typeIndex) =>
                CopyOnce(entityIndex);

            public void OnEntityAdded(Filter filter, int entityIndex) {
            }

            public void OnEntityRemoved(Filter filter, int entityIndex) =>
                CopyOnce(entityIndex);

            private void CopyOnce(int entityIndex) {
                if (Copied) {
                    return;
                }
                Copied = true;
                Copy = World.CopyEntity(World.GetEntity(entityIndex));
            }
        }

        // Up to 2.2.1 the copy of a source inside the removal of its last component was an alive
        // entity without components (no filter saw it, nothing destroyed it), and world listeners got
        // OnEntityCreated for it.
        [TestCase(false)]
        [TestCase(true)]
        public void SourceInsideRemovalOfItsLastComponent_IsNotCopied(bool fromFilterListener) {
            var world = new World();
            var filter = world.Filter().Inc<CeMarker>().End();
            var copier = new CopyOnLastRemoved { World = world };
            if (fromFilterListener) {
                filter.AddListener(copier);
            } else {
                world.AddEventListener(copier);
            }
            var log = new CreatedLog { World = world };
            world.AddEventListener(log);
            var source = world.CreateEntity(new CeMarker());
            log.Created.Clear();

            world.Remove<CeMarker>(source);

            Assert.That(copier.Copied, Is.True, "test setup: the listener ran");
            Assert.That(copier.Copy, Is.EqualTo(Entity.Null), "a source without components has nothing to copy");
            Assert.That(world.EntityCount, Is.EqualTo(0), "no entity without components is left behind");
            Assert.That(log.Created, Is.Empty);
            WorldInvariants.AssertConsistent(world, "after the copy of an emptied source");
        }

        [Test]
        public void SourceInsideRemovalOfAComponentThatIsNotItsLast_IsCopied() {
            var world = new World();
            var copier = new CopyOnLastRemoved { World = world };
            world.AddEventListener(copier);
            var source = world.CreateEntity(new CeMarker());
            world.Add(source, new CePosition { V = 4 });

            world.Remove<CeMarker>(source);

            Assert.That(world.IsAlive(copier.Copy), Is.True);
            Assert.That(world.Get<CePosition>(copier.Copy).V, Is.EqualTo(4));
            Assert.That(world.Has<CeMarker>(copier.Copy), Is.False, "the removed component is gone before world listeners run");
        }
    }
}
