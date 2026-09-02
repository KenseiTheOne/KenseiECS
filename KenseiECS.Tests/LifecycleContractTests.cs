using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace KenseiECS.Tests {
    [TestFixture]
    public class GenerationTests {
        [Test]
        public void GetEntity_OnDeadSlot_ReturnsDeadHandle() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.DestroyEntity(e);
            var handle = world.GetEntity(e.Index);
            Assert.That(world.IsAlive(handle), Is.False, "GetEntity on a dead slot must not yield a live handle");
        }

        [Test]
        public void GetEntity_OnDeadSlot_EqualsDestroyedHandle() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.DestroyEntity(e);
            Assert.That(world.GetEntity(e.Index), Is.EqualTo(e), "dead slot must keep the generation of the entity that died there until reuse");
        }

        [Test]
        public void GetEntity_AfterReuse_DoesNotEqualStaleHandle() {
            var world = new World();
            var stale = world.CreateEntity(new Position());
            world.DestroyEntity(stale);
            var reborn = world.CreateEntity(new Position());
            Assert.That(world.GetEntity(stale.Index), Is.EqualTo(reborn), "after reuse the slot names the reborn entity");
            Assert.That(world.GetEntity(stale.Index), Is.Not.EqualTo(stale), "stale handle must not match the reborn entity");
        }

        [Test]
        public void StaleHandle_AfterClear_IsDead() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.Clear();
            var reborn = world.CreateEntity(new Position());
            Assert.That(reborn.Index, Is.EqualTo(e.Index), "Clear must let the slot be reused from index 0");
            Assert.That(world.IsAlive(e), Is.False, "handle from before Clear must be dead even though the slot is live again");
        }

        [Test]
        public void StaleHandle_IndexBeyondNextIndex_IsDead() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.Clear();
            Assert.That(world.IsAlive(e), Is.False, "handle whose index is beyond the current allocation must be dead");
        }

        [Test]
        public void ManyReuses_GenerationKeepsGrowing() {
            var world = new World();
            int lastGeneration = 0;
            for (int i = 0; i < 5; i++) {
                var e = world.CreateEntity(new Position());
                Assert.That(e.Generation, Is.GreaterThan(lastGeneration), "each reuse must produce a higher generation");
                lastGeneration = e.Generation;
                world.DestroyEntity(e);
            }
        }
    }

    [TestFixture]
    public class EntityCreatedOrderTests {
        [Test]
        public void OnEntityCreated_SeesFirstComponent() {
            var world = new World();
            var observer = new CreatedObserverListener { World = world };
            world.AddEventListener(observer);
            world.CreateEntity(new Position());
            Assert.That(observer.HadPositionAtCreate, Is.True, "OnEntityCreated must fire after the initial component is added");
        }

        [Test]
        public void OnEntityCreated_ComponentCountIsOne() {
            var world = new World();
            var observer = new CreatedObserverListener { World = world };
            world.AddEventListener(observer);
            world.CreateEntity(new Position());
            Assert.That(observer.ComponentCountAtCreate, Is.EqualTo(1), "OnEntityCreated must observe exactly one component");
        }

        [Test]
        public void OnEntityCreated_FiresAfterComponentAdded() {
            var world = new World();
            var log = new List<string>();
            world.AddEventListener(new LoggingListener(log));
            world.CreateEntity(new Position());
            Assert.That(log, Is.EqualTo(new[] { "added", "created" }), "component add event must precede entity created event");
        }

        [Test]
        public void CopyEntity_OnEntityCreated_SeesAllComponents() {
            var world = new World();
            var src = world.CreateEntity(new Position());
            world.Add(src, new Velocity());
            var observer = new CreatedObserverListener { World = world };
            world.AddEventListener(observer);
            world.CopyEntity(src);
            Assert.That(observer.ComponentCountAtCreate, Is.EqualTo(2), "OnEntityCreated for a copy must observe all copied components");
        }

        [Test]
        public void DestroyInsideOnEntityCreated_LeavesWorldConsistent() {
            var world = new World();
            world.AddEventListener(new DestroyOnCreateListener { World = world });
            var e = world.CreateEntity(new Position());
            Assert.That(world.IsAlive(e), Is.False, "entity destroyed from OnEntityCreated must be dead");
            Assert.That(world.EntityCount, Is.EqualTo(0), "entity count must be zero after destroy inside OnEntityCreated");
            Assert.That(world.Pool<Position>().Count, Is.EqualTo(0), "pool must be empty after destroy inside OnEntityCreated");
        }

        [Test]
        public void DestroyInsideOnEntityCreated_NextEntityIsClean() {
            var world = new World();
            var listener = new DestroyOnCreateListener { World = world };
            world.AddEventListener(listener);
            world.CreateEntity(new Position());
            world.RemoveEventListener(listener);
            var next = world.CreateEntity(new Velocity());
            Assert.That(world.Has<Position>(next), Is.False, "reused slot must not inherit a component from the destroyed entity");
            Assert.That(world.GetComponentCount(next), Is.EqualTo(1), "reused slot must start with a clean component count");
        }

        private sealed class LoggingListener : WorldListenerBase {
            private readonly List<string> _log;

            public LoggingListener(List<string> log) {
                _log = log;
            }

            public override void OnEntityCreated(int entityIndex) =>
                _log.Add("created");

            public override void OnComponentAdded(int entityIndex, int typeIndex) =>
                _log.Add("added");
        }
    }

    [TestFixture]
    public class ExceptionSafetyTests {
        private static (World world, Entity entity, Filter filter) DestroyWithThrowingListener() {
            var world = new World();
            var filter = world.Filter().Inc<Position>().End();
            var e = world.CreateEntity(new Position());
            world.Add(e, new Velocity());
            var listener = new ThrowOnDestroyListener();
            world.AddEventListener(listener);
            Assert.That(() => world.DestroyEntity(e), Throws.InvalidOperationException, "listener exception must propagate");
            world.RemoveEventListener(listener);
            return (world, e, filter);
        }

        [Test]
        public void ThrowInOnEntityDestroyed_EntityCountIsZero() {
            var (world, _, _) = DestroyWithThrowingListener();
            Assert.That(world.EntityCount, Is.EqualTo(0), "slot must be released even when a destroy listener throws");
        }

        [Test]
        public void ThrowInOnEntityDestroyed_ComponentsAreRemoved() {
            var (world, _, _) = DestroyWithThrowingListener();
            Assert.That(world.Pool<Position>().Count, Is.EqualTo(0), "components must be drained even when a destroy listener throws");
            Assert.That(world.Pool<Velocity>().Count, Is.EqualTo(0), "components must be drained even when a destroy listener throws");
        }

        [Test]
        public void ThrowInOnEntityDestroyed_FilterIsEmpty() {
            var (_, _, filter) = DestroyWithThrowingListener();
            Assert.That(filter.Count, Is.EqualTo(0), "filters must not keep a destroyed entity when a listener throws");
        }

        [Test]
        public void ThrowInOnEntityDestroyed_SlotIsReusable() {
            var (world, e, _) = DestroyWithThrowingListener();
            var reborn = world.CreateEntity(new Health());
            Assert.That(reborn.Index, Is.EqualTo(e.Index), "slot must return to the free list when a listener throws");
            Assert.That(world.IsAlive(reborn), Is.True, "reborn entity must be alive");
        }

        [Test]
        public void ThrowInOnComponentRemoved_DuringDestroy_SlotIsReleased() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            var listener = new ThrowOnRemoveListener();
            world.AddEventListener(listener);
            Assert.That(() => world.DestroyEntity(e), Throws.InvalidOperationException, "listener exception must propagate");
            world.RemoveEventListener(listener);
            Assert.That(world.EntityCount, Is.EqualTo(0), "slot must be released when a remove listener throws mid-drain");
            Assert.That(world.IsAlive(e), Is.False, "entity must be dead when a remove listener throws mid-drain");
        }

        [Test]
        public void ThrowInAutoReset_DuringDestroy_SlotIsReleased() {
            var world = new World();
            var e = world.CreateEntity(new ThrowingReset { Throw = true });
            Assert.That(() => world.DestroyEntity(e), Throws.InvalidOperationException, "AutoReset exception must propagate");
            Assert.That(world.EntityCount, Is.EqualTo(0), "slot must be released when AutoReset throws");
        }

        [Test]
        public void ThrowInOnEntityCreated_EntityIsFullyFormed() {
            var world = new World();
            var listener = new ThrowOnCreateListener();
            world.AddEventListener(listener);
            Entity e = default;
            Assert.That(() => e = world.CreateEntity(new Position { X = 3 }), Throws.InvalidOperationException, "listener exception must propagate");
            world.RemoveEventListener(listener);
            var alive = world.AliveEntities.GetEnumerator();
            Assert.That(alive.MoveNext(), Is.True, "entity must exist after a create listener throws");
            Assert.That(world.Get<Position>(alive.Current).X, Is.EqualTo(3f), "entity must carry its component after a create listener throws");
        }
    }

    [TestFixture]
    public class ListenerDispatchTests {
        [Test]
        public void RemovingEarlierListenerDuringDispatch_DoesNotRepeatCurrent() {
            var world = new World();
            var first = new CountingWorldListener();
            var second = new RemoveOtherListener { World = world, Other = first };
            world.AddEventListener(first);
            world.AddEventListener(second);
            world.CreateEntity(new Position());
            Assert.That(second.Calls, Is.EqualTo(1), "listener that removes an earlier one must not be invoked twice");
        }

        [Test]
        public void RemovingEarlierListenerDuringDispatch_RemovedStillSeesCurrentEvent() {
            var world = new World();
            var first = new CountingWorldListener();
            var second = new RemoveOtherListener { World = world, Other = first };
            world.AddEventListener(first);
            world.AddEventListener(second);
            world.CreateEntity(new Position());
            Assert.That(first.CreatedCount, Is.EqualTo(1), "dispatch iterates the listener set captured at the start");
        }

        [Test]
        public void RemovedListener_DoesNotReceiveLaterEvents() {
            var world = new World();
            var first = new CountingWorldListener();
            var second = new RemoveOtherListener { World = world, Other = first };
            world.AddEventListener(first);
            world.AddEventListener(second);
            world.CreateEntity(new Position());
            world.CreateEntity(new Position());
            Assert.That(first.CreatedCount, Is.EqualTo(1), "removed listener must not receive later events");
        }

        [Test]
        public void RemoveEventListener_UnknownListener_IsNoOp() {
            var world = new World();
            Assert.That(() => world.RemoveEventListener(new CountingWorldListener()), Throws.Nothing, "removing an unregistered listener must be a no-op");
        }

        [Test]
        public void OnComponentRemoved_LastComponent_EntityStillAliveWithZeroComponents() {
            var world = new World();
            var observer = new RemovedObserverListener { World = world };
            world.AddEventListener(observer);
            var e = world.CreateEntity(new Position());
            world.Remove<Position>(e);
            Assert.That(observer.WasAliveInsideRemoved, Is.True, "entity is still alive inside OnComponentRemoved for its last component");
            Assert.That(observer.CountInsideRemoved, Is.EqualTo(0), "component count is already zero inside OnComponentRemoved for the last component");
        }
    }

    [TestFixture]
    public class WarmupEventTests {
        [Test]
        public void Warmup_FiresNoWorldEvents() {
            var world = new World();
            world.Pool<Position>();
            world.Pool<Velocity>();
            var listener = new CountingWorldListener();
            world.AddEventListener(listener);
            world.Warmup();
            Assert.That(listener.CreatedCount + listener.DestroyedCount + listener.AddedCount + listener.RemovedCount, Is.EqualTo(0),
                "Warmup must be invisible to world event listeners");
        }

        [Test]
        public void Warmup_EventsResumeAfterwards() {
            var world = new World();
            world.Pool<Position>();
            var listener = new CountingWorldListener();
            world.AddEventListener(listener);
            world.Warmup();
            world.CreateEntity(new Position());
            Assert.That(listener.CreatedCount, Is.EqualTo(1), "events must be delivered again after Warmup");
        }

        [Test]
        public void Warmup_WithNoPools_DoesNotThrow() {
            var world = new World();
            Assert.That(() => world.Warmup(), Throws.Nothing, "Warmup on a world without pools must succeed");
            Assert.That(world.EntityCount, Is.EqualTo(0), "Warmup must leave no entity behind");
        }
    }

    [TestFixture]
    public class ComponentRegistryTests {
        [Test]
        public void TypeOf_ResolvesRegisteredIndex() {
            int idx = ComponentType<Position>.Index;
            Assert.That(ComponentType.TypeOf(idx), Is.EqualTo(typeof(Position)), "TypeOf must resolve a registered index");
        }

        [Test]
        public void NameOf_ReturnsShortName() {
            int idx = ComponentType<Velocity>.Index;
            Assert.That(ComponentType.NameOf(idx), Is.EqualTo("Velocity"), "NameOf must return the type's short name");
        }

        [Test]
        public void TypeOf_UnregisteredIndex_Throws() {
            Assert.That(() => ComponentType.TypeOf(int.MaxValue), Throws.TypeOf<ArgumentOutOfRangeException>(),
                "TypeOf on an unregistered index must throw");
        }

        [Test]
        public void GetComponentTypes_ListsAllComponents() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.Add(e, new Health());
            var types = new List<int>();
            int count = world.GetComponentTypes(e, types);
            Assert.That(count, Is.EqualTo(2), "GetComponentTypes must return the component count");
            Assert.That(types, Is.EquivalentTo(new[] { ComponentType<Position>.Index, ComponentType<Health>.Index }),
                "GetComponentTypes must list every component's type index");
        }

        [Test]
        public void GetComponentTypes_AfterRemove_OmitsRemoved() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.Add(e, new Health());
            world.Remove<Health>(e);
            var types = new List<int>();
            world.GetComponentTypes(e, types);
            Assert.That(types, Is.EqualTo(new[] { ComponentType<Position>.Index }), "removed component must not be listed");
        }

        [Test]
        public void GetComponentCount_TracksAddAndRemove() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.Add(e, new Health());
            world.Add(e, new Velocity());
            world.Remove<Health>(e);
            Assert.That(world.GetComponentCount(e), Is.EqualTo(2), "component count must track adds and removes");
        }

        [Test]
        public void ListenerTypeIndex_ResolvesToComponentType() {
            var world = new World();
            var capture = new TypeCaptureListener();
            world.AddEventListener(capture);
            world.CreateEntity(new Damage());
            Assert.That(ComponentType.TypeOf(capture.LastAddedType), Is.EqualTo(typeof(Damage)),
                "typeIndex passed to listeners must resolve through ComponentType.TypeOf");
        }

        [Test]
        public void Pool_ExposesComponentType() {
            var world = new World();
            Assert.That(world.Pool<Health>().ComponentType, Is.EqualTo(typeof(Health)), "pool must expose its component Type");
        }

        private sealed class TypeCaptureListener : WorldListenerBase {
            public int LastAddedType = -1;

            public override void OnComponentAdded(int entityIndex, int typeIndex) =>
                LastAddedType = typeIndex;
        }
    }

    [TestFixture]
    public class RemoveContractTests {
        [Test]
        public void Remove_UnpooledType_DoesNotCreatePool() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.Remove<Damage>(e);
            int pools = 0;
            foreach (var pool in world.ActivePools) {
                pools++;
            }
            Assert.That(pools, Is.EqualTo(1), "Remove of a never-added type must not allocate a pool");
        }

        [Test]
        public void Remove_MissingComponent_FiresNoEvent() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.Pool<Health>();
            var listener = new CountingWorldListener();
            world.AddEventListener(listener);
            world.Remove<Health>(e);
            Assert.That(listener.RemovedCount, Is.EqualTo(0), "Remove of a missing component must be a silent no-op");
        }

        [Test]
        public void Add_Duplicate_Throws() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            Assert.That(() => world.Add(e, new Position()), Throws.InvalidOperationException, "duplicate Add must throw in every build");
        }

        [Test]
        public void OneFrameOnlyEntity_IsDestroyedAtEndOfFrame() {
            var world = new World();
            var systems = new SystemsRunner(world).OneFrame<Damage>();
            systems.Init();
            var e = world.CreateEntity(new Damage());
            systems.Run();
            Assert.That(world.IsAlive(e), Is.False, "entity whose only component is one-frame must be auto-destroyed at end of frame");
        }
    }

    [TestFixture]
    public class CapacityGrowthTests {
        private struct GrowthTag : IComponent { }
        private struct GrowthPad<T> : IComponent { }

        // Same trick as FilterTests: push the process-wide type counter past a
        // mask word so GrowthTag lands in a higher word than Position.
        private static void RegisterPaddingTypes(int count) {
            Type arg = typeof(Velocity);
            for (int i = 0; i < count; i++) {
                arg = typeof(GrowthPad<>).MakeGenericType(arg);
                System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
                    typeof(ComponentType<>).MakeGenericType(arg).TypeHandle);
            }
        }

        [Test]
        public void EntityCapacityGrowth_WithMultipleMaskWords_KeepsComponents() {
            int positionWord = ComponentType<Position>.Index >> 6;
            RegisterPaddingTypes(64);
            var config = WorldConfig.Default();
            config.InitialEntityCapacity = 4;
            var world = new World(config);
            world.Filter().Inc<GrowthTag>().End();
            Assert.That(ComponentType<GrowthTag>.Index >> 6, Is.GreaterThan(positionWord),
                "test setup must place the tag in a higher mask word than Position");

            var entities = new Entity[100];
            for (int i = 0; i < entities.Length; i++) {
                entities[i] = world.CreateEntity(new Position { X = i });
                world.Add(entities[i], new GrowthTag());
            }

            for (int i = 0; i < entities.Length; i++) {
                Assert.That(world.Has<GrowthTag>(entities[i]), Is.True, $"entity {i} must keep its high-word component after growth");
                Assert.That(world.Get<Position>(entities[i]).X, Is.EqualTo((float)i), $"entity {i} must keep its data after growth");
            }
        }

        [Test]
        public void PoolAndFilterSparseGrowth_BeyondInitialCapacity() {
            var config = WorldConfig.Default();
            config.InitialEntityCapacity = 2;
            config.InitialPoolSparseCapacity = 2;
            config.InitialPoolDenseCapacity = 2;
            var world = new World(config);
            var filter = world.Filter().Inc<Position>().End();
            for (int i = 0; i < 50; i++) {
                world.CreateEntity(new Position());
            }
            Assert.That(filter.Count, Is.EqualTo(50), "filter must track every entity across sparse and dense growth");
            Assert.That(world.Pool<Position>().Count, Is.EqualTo(50), "pool must hold every component across growth");
        }

        [Test]
        public void ManyPools_BeyondInitialPoolCount() {
            var config = WorldConfig.Default();
            config.InitialPoolCount = 1;
            var world = new World(config);
            var e = world.CreateEntity(new Position());
            world.Add(e, new Velocity());
            world.Add(e, new Health());
            world.Add(e, new Damage());
            world.Add(e, new Frozen());
            Assert.That(world.GetComponentCount(e), Is.EqualTo(5), "pool registry must grow past its initial capacity");
        }

        [Test]
        public void FreeListGrowth_ManyDestroys() {
            var config = WorldConfig.Default();
            config.InitialEntityCapacity = 8;
            var world = new World(config);
            var entities = new Entity[200];
            for (int i = 0; i < entities.Length; i++) {
                entities[i] = world.CreateEntity(new Position());
            }
            for (int i = 0; i < entities.Length; i++) {
                world.DestroyEntity(entities[i]);
            }
            Assert.That(world.EntityCount, Is.EqualTo(0), "all entities must be destroyed");
            var reborn = world.CreateEntity(new Position());
            Assert.That(reborn.Index, Is.LessThan(entities.Length), "slots must be recycled after the free list grew");
        }
    }
}
