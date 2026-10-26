using System.Collections.Generic;
using NUnit.Framework;

namespace KenseiECS.Tests {
    // Removing T while a filter listener adds T back (or replaces the entity): world listeners must
    // not end up believing the entity lacks T, or hear of a removal from the entity that took the
    // slot. The mirror of the Add side in ExcludeFilterReentrancyTests.
    [TestFixture]
    public class RemoveWorldEventsTests {
        private struct RwBase : IComponent {
        }

        private struct RwImmune : IComponent {
            public int V;
        }

        private struct RwOther : IComponent {
        }

        // Adds T back to an entity once ("immune: keep it").
        private sealed class AddBack<T> : IFilterListener where T : struct, IComponent {
            public World World;
            public bool OnEnter;
            private bool _done;

            public void OnEntityAdded(Filter filter, int entityIndex) {
                if (OnEnter) {
                    Once(entityIndex);
                }
            }

            public void OnEntityRemoved(Filter filter, int entityIndex) {
                if (!OnEnter) {
                    Once(entityIndex);
                }
            }

            private void Once(int entityIndex) {
                if (_done) {
                    return;
                }
                _done = true;
                World.Add(World.GetEntity(entityIndex), default(T));
            }
        }

        // Destroys the entity leaving the filter and spawns one, which takes the freed slot.
        private sealed class ReplaceOnLeave : IFilterListener {
            public World World;
            public Entity Spawned;

            public void OnEntityAdded(Filter filter, int entityIndex) {
            }

            public void OnEntityRemoved(Filter filter, int entityIndex) {
                if (Spawned != Entity.Null) {
                    return;
                }
                World.DestroyEntity(World.GetEntity(entityIndex));
                Spawned = World.CreateEntity(new RwOther());
            }
        }

        // What a world listener can know from the events alone: the components of each entity,
        // keyed by the handle the slot holds when the event arrives. A removal of a component the
        // model does not have is an error.
        private sealed class Model : WorldListenerBase {
            public World World;
            public readonly HashSet<(Entity, int)> Has = new();
            public readonly List<string> Errors = new();

            public override void OnComponentAdded(int entityIndex, int typeIndex) {
                Has.Add((World.GetEntity(entityIndex), typeIndex));
            }

            public override void OnComponentRemoved(int entityIndex, int typeIndex) {
                if (!Has.Remove((World.GetEntity(entityIndex), typeIndex))) {
                    Errors.Add($"Removed({entityIndex}, {ComponentType.NameOf(typeIndex)}) for a component the entity in the slot never got");
                }
            }

            public bool Believes<T>(Entity entity) where T : struct, IComponent =>
                Has.Contains((entity, ComponentType<T>.Index));
        }

        private static (World world, Entity e, Model model) Setup() {
            var world = new World();
            var model = new Model { World = world };
            world.AddEventListener(model);
            var e = world.CreateEntity(new RwBase());
            world.Add(e, new RwImmune { V = 1 });
            return (world, e, model);
        }

        // Up to 2.2.1 the outer Remove reported the removal after the nested Add: events
        // [Added, Removed] while the entity has the component.
        [Test]
        public void Remove_IncListenerAddsComponentBack_WorldListenersKnowItIsThere() {
            var (world, e, model) = Setup();
            world.Filter().Inc<RwImmune>().End().AddListener(new AddBack<RwImmune> { World = world });

            world.Remove<RwImmune>(e);

            Assert.That(world.Has<RwImmune>(e), Is.True, "test setup: the listener added it back");
            Assert.That(model.Believes<RwImmune>(e), Is.True, "world listeners must not believe the component is gone");
            Assert.That(model.Errors, Is.Empty);
            WorldInvariants.AssertConsistent(world, "after Remove");
        }

        [Test]
        public void Remove_ExcListenerAddsComponentBack_WorldListenersKnowItIsThere() {
            var (world, e, model) = Setup();
            world.Filter().Inc<RwBase>().Exc<RwImmune>().End().AddListener(new AddBack<RwImmune> { World = world, OnEnter = true });

            world.Remove<RwImmune>(e);

            Assert.That(world.Has<RwImmune>(e), Is.True, "test setup: the listener added it back");
            Assert.That(model.Believes<RwImmune>(e), Is.True, "world listeners must not believe the component is gone");
            Assert.That(model.Errors, Is.Empty);
            WorldInvariants.AssertConsistent(world, "after Remove");
        }

        // The entity that took the slot never had the component: up to 2.2.1 its removal arrived
        // after the new entity's own events.
        [Test]
        public void Remove_ListenerReplacesEntity_NoRemovalReportedForTheNewEntity() {
            var (world, e, model) = Setup();
            var listener = new ReplaceOnLeave { World = world };
            world.Filter().Inc<RwImmune>().End().AddListener(listener);

            world.Remove<RwImmune>(e);

            Assert.That(listener.Spawned.Index, Is.EqualTo(e.Index), "test setup: the spawned entity takes the slot");
            Assert.That(model.Errors, Is.Empty);
            Assert.That(model.Believes<RwOther>(listener.Spawned), Is.True);
            Assert.That(model.Believes<RwImmune>(listener.Spawned), Is.False);
            WorldInvariants.AssertConsistent(world, "after Remove");
        }

        [Test]
        public void Remove_NoFilterListener_WorldListenersSeeRemove() {
            var (world, e, model) = Setup();
            world.Filter().Inc<RwImmune>().End();
            world.Filter().Inc<RwBase>().Exc<RwImmune>().End();

            world.Remove<RwImmune>(e);

            Assert.That(model.Believes<RwImmune>(e), Is.False);
            Assert.That(model.Believes<RwBase>(e), Is.True);
            Assert.That(model.Errors, Is.Empty);
        }

        // A listener that destroys the entity (no new one in the slot): the removal is still
        // reported, after the destroy's own events, as in any DestroyEntity.
        [Test]
        public void Remove_ListenerDestroysEntity_RemovalStillReported() {
            var (world, e, model) = Setup();
            world.Filter().Inc<RwImmune>().End().AddListener(new DestroyOnLeave { World = world });

            world.Remove<RwImmune>(e);

            Assert.That(world.IsAlive(e), Is.False);
            Assert.That(model.Has, Is.Empty, "every component of the destroyed entity was reported removed");
            Assert.That(model.Errors, Is.Empty);
        }

        private sealed class DestroyOnLeave : IFilterListener {
            public World World;

            public void OnEntityAdded(Filter filter, int entityIndex) {
            }

            public void OnEntityRemoved(Filter filter, int entityIndex) {
                World.DestroyEntity(World.GetEntity(entityIndex));
            }
        }
    }
}
