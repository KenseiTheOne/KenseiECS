using System;
using System.Runtime.CompilerServices;
using NUnit.Framework;

namespace KenseiECS.Tests {
    // World.Warmup with listeners that destroy, replace or extend its temporary entity.
    [TestFixture]
    public class WarmupListenerTests {
        // Registered in this order (lowest index first), so Warmup adds the trigger before the others.
        private struct WuTrigger : IComponent {
        }

        private struct WuPosition : IComponent {
        }

        private struct WuVelocity : IComponent {
        }

        private struct WuHealth : IComponent {
            public float Value;
        }

        private struct WuMarker : IComponent {
            public int V;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int[] RegisterInOrder() {
            return new[] {
                ComponentType<WuTrigger>.Index,
                ComponentType<WuPosition>.Index,
                ComponentType<WuVelocity>.Index,
                ComponentType<WuHealth>.Index,
                ComponentType<WuMarker>.Index
            };
        }

        [OneTimeSetUp]
        public void RegisterTypes() {
            var indices = RegisterInOrder();
            for (int i = 1; i < indices.Length; i++) {
                Assert.That(indices[i], Is.GreaterThan(indices[i - 1]), "test setup: Warmup must reach the trigger type first");
            }
        }

        private sealed class DestroyOnAdded<T> : IComponentListener<T> where T : struct, IComponent {
            public World World;
            public int Calls;

            public void OnAdded(int entityIndex, ref T component) {
                Calls++;
                World.DestroyEntity(World.GetEntity(entityIndex));
            }

            public void OnRemoved(int entityIndex, ref T component) {
            }
        }

        private sealed class DestroyOnEnter : IFilterListener {
            public World World;
            public int Calls;

            public void OnEntityAdded(Filter filter, int entityIndex) {
                Calls++;
                World.DestroyEntity(World.GetEntity(entityIndex));
            }

            public void OnEntityRemoved(Filter filter, int entityIndex) {
            }
        }

        // Destroys the entity and spawns a replacement, which takes the freed slot.
        private sealed class DestroyAndSpawnOnAdded : IComponentListener<WuTrigger> {
            public World World;
            public Entity Spawned;

            public void OnAdded(int entityIndex, ref WuTrigger component) {
                World.DestroyEntity(World.GetEntity(entityIndex));
                Spawned = World.CreateEntity(new WuMarker { V = 42 });
            }

            public void OnRemoved(int entityIndex, ref WuTrigger component) {
            }
        }

        private sealed class AddHealthOnAdded : IComponentListener<WuTrigger> {
            public World World;

            public void OnAdded(int entityIndex, ref WuTrigger component) {
                var entity = World.GetEntity(entityIndex);
                if (!World.Has<WuHealth>(entity)) {
                    World.Add(entity, new WuHealth { Value = 1 });
                }
            }

            public void OnRemoved(int entityIndex, ref WuTrigger component) {
            }
        }

        private static World MakeWorld(out Filter move) {
            var world = new World();
            world.Pool<WuTrigger>();
            world.Pool<WuPosition>();
            world.Pool<WuVelocity>();
            world.Pool<WuHealth>();
            move = world.Filter().Inc<WuPosition>().Inc<WuVelocity>().End();
            return world;
        }

        private static void AssertWarmupLeftNothing(World world, Filter move) {
            Assert.That(world.EntityCount, Is.EqualTo(0), "no temporary entity may survive Warmup");
            Assert.That(world.Pool<WuPosition>().Count, Is.EqualTo(0), "no component may stay on a freed slot");
            Assert.That(world.Pool<WuVelocity>().Count, Is.EqualTo(0));
            Assert.That(world.Pool<WuHealth>().Count, Is.EqualTo(0));
            Assert.That(move.Count, Is.EqualTo(0), "no filter may keep a freed slot");
            WorldInvariants.AssertConsistent(world, "after Warmup");

            var next = world.CreateEntity(new WuMarker { V = 1 });
            Assert.That(world.GetComponentCount(next), Is.EqualTo(1), "the next entity in the reused slot starts with its own component only");
            Assert.That(world.Has<WuPosition>(next), Is.False);
            Assert.That(world.Has<WuVelocity>(next), Is.False);
            Assert.That(world.Has<WuHealth>(next), Is.False);
            Assert.That(move.Contains(next.Index), Is.False, "the next entity must not inherit filter membership");
            WorldInvariants.AssertConsistent(world, "after the first spawn");
        }

        [Test]
        public void Warmup_TypedListenerDestroysTemporaryEntity_RemainingTypesDoNotLandOnFreedSlot() {
            var world = MakeWorld(out var move);
            var listener = new DestroyOnAdded<WuTrigger> { World = world };
            world.Pool<WuTrigger>().AddListener(listener);

            Assert.That(() => world.Warmup(), Throws.Nothing);

            Assert.That(listener.Calls, Is.EqualTo(1));
            AssertWarmupLeftNothing(world, move);
        }

        [Test]
        public void Warmup_FilterListenerDestroysTemporaryEntity_RemainingTypesDoNotLandOnFreedSlot() {
            var world = MakeWorld(out var move);
            var listener = new DestroyOnEnter { World = world };
            world.Filter().Inc<WuTrigger>().End().AddListener(listener);

            Assert.That(() => world.Warmup(), Throws.Nothing);

            Assert.That(listener.Calls, Is.EqualTo(1));
            AssertWarmupLeftNothing(world, move);
        }

        [Test]
        public void Warmup_ListenersDestroyEveryTemporaryEntity_EachTypeGetsAFreshOne() {
            var world = MakeWorld(out var move);
            var trigger = new DestroyOnAdded<WuTrigger> { World = world };
            var position = new DestroyOnAdded<WuPosition> { World = world };
            var health = new DestroyOnAdded<WuHealth> { World = world };
            world.Pool<WuTrigger>().AddListener(trigger);
            world.Pool<WuPosition>().AddListener(position);
            world.Pool<WuHealth>().AddListener(health);

            world.Warmup();

            Assert.That(trigger.Calls + position.Calls + health.Calls, Is.EqualTo(3), "every type is still warmed up once");
            AssertWarmupLeftNothing(world, move);
        }

        [Test]
        public void Warmup_ListenerDestroysAndSpawns_SpawnedEntityGetsNoWarmupComponents() {
            var world = MakeWorld(out var move);
            var listener = new DestroyAndSpawnOnAdded { World = world };
            world.Pool<WuTrigger>().AddListener(listener);

            world.Warmup();

            var spawned = listener.Spawned;
            Assert.That(world.IsAlive(spawned), Is.True, "Warmup must not destroy an entity a listener created");
            Assert.That(world.GetComponentCount(spawned), Is.EqualTo(1), "the spawned entity keeps only its own component");
            Assert.That(world.Get<WuMarker>(spawned).V, Is.EqualTo(42));
            Assert.That(world.Has<WuPosition>(spawned), Is.False, "Warmup must not continue on an entity that took the temporary slot");
            Assert.That(world.Has<WuVelocity>(spawned), Is.False);
            Assert.That(world.Has<WuHealth>(spawned), Is.False);
            Assert.That(move.Contains(spawned.Index), Is.False);
            Assert.That(world.EntityCount, Is.EqualTo(1), "only the spawned entity remains");
            WorldInvariants.AssertConsistent(world, "after Warmup");
        }

        [Test]
        public void Warmup_ListenerAlreadyAddedALaterType_IsSkipped() {
            var world = MakeWorld(out var move);
            world.Pool<WuTrigger>().AddListener(new AddHealthOnAdded { World = world });

            Assert.That(() => world.Warmup(), Throws.Nothing, "a type a listener already added must not be added twice");

            AssertWarmupLeftNothing(world, move);
        }

        [Test]
        public void Warmup_SingletonCreatedBefore_StaysTheOnlyOne() {
            var world = MakeWorld(out _);
            world.Pool<WuTrigger>().AddListener(new DestroyOnAdded<WuTrigger> { World = world });
            var config = world.CreateEntity(new WuHealth { Value = 7 });

            world.Warmup();

            Assert.That(world.HasSingleton<WuHealth>(), Is.True);
            Assert.That(world.GetSingleton<WuHealth>().Value, Is.EqualTo(7));
            Assert.That(world.GetSingletonEntity<WuHealth>(), Is.EqualTo(config));
            WorldInvariants.AssertConsistent(world, "after Warmup");
        }

        // "Hp <= 0 dies at once", subscribed in Init: SystemsRunner.Warmup (EcsBootstrap's default)
        // hands it the default Health of the temporary entity.
        private sealed class DeathSystem : IInitSystem, IComponentListener<WuHealth> {
            private World _world;
            public Filter Alive;

            public void Init(World world, SharedData shared) {
                _world = world;
                world.Pool<WuHealth>().AddListener(this);
                world.Pool<WuMarker>();
                Alive = world.Filter().Inc<WuHealth>().Exc<WuMarker>().End();
            }

            public void OnAdded(int entityIndex, ref WuHealth component) {
                if (component.Value <= 0) {
                    _world.DestroyEntity(_world.GetEntity(entityIndex));
                }
            }

            public void OnRemoved(int entityIndex, ref WuHealth component) {
            }
        }

        [Test]
        public void RunnerWarmup_InitListenerKillsDefaultHealth_FirstSpawnIsClean() {
            var world = new World();
            var death = new DeathSystem();
            var systems = new SystemsRunner(world).Add(death);

            systems.Warmup();

            var player = world.CreateEntity(new WuHealth { Value = 100 });
            Assert.That(world.Has<WuMarker>(player), Is.False, "the player must not inherit a type Warmup added after Health");
            Assert.That(world.GetComponentCount(player), Is.EqualTo(1));
            Assert.That(death.Alive.Contains(player.Index), Is.True);
            WorldInvariants.AssertConsistent(world, "after the first spawn");
        }
    }
}
