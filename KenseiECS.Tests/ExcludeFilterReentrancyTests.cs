using System.Collections.Generic;
using NUnit.Framework;

namespace KenseiECS.Tests {
    // Adding T while a filter listener takes T away again: the entity must stay in the Exc<T>
    // filters it matches (the mirror of FilterReentrancyTests for Remove), and world listeners
    // must not end up believing the entity has T.
    [TestFixture]
    public class ExcludeFilterReentrancyTests {
        private struct XPosition : IComponent {
        }

        private struct XVelocity : IComponent {
        }

        private struct XFrozen : IComponent {
        }

        private struct XStunned : IComponent {
        }

        // Removes T from an entity entering the filter ("immune: drop Frozen at once").
        private sealed class RemoveOnEnter<T> : IFilterListener where T : struct, IComponent {
            public World World;
            public bool Always;
            private bool _done;

            public void OnEntityAdded(Filter filter, int entityIndex) {
                if (_done && !Always) {
                    return;
                }
                _done = true;
                World.Remove<T>(World.GetEntity(entityIndex));
            }

            public void OnEntityRemoved(Filter filter, int entityIndex) {
            }
        }

        // Removes T from an entity leaving the filter.
        private sealed class RemoveOnLeave<T> : IFilterListener where T : struct, IComponent {
            public World World;
            private bool _done;

            public void OnEntityAdded(Filter filter, int entityIndex) {
            }

            public void OnEntityRemoved(Filter filter, int entityIndex) {
                if (_done) {
                    return;
                }
                _done = true;
                var entity = World.GetEntity(entityIndex);
                if (World.IsAlive(entity) && World.Has<T>(entity)) {
                    World.Remove<T>(entity);
                }
            }
        }

        private sealed class AddOnEnter<T> : IFilterListener where T : struct, IComponent {
            public World World;
            private bool _done;

            public void OnEntityAdded(Filter filter, int entityIndex) {
                if (_done) {
                    return;
                }
                _done = true;
                World.Add(World.GetEntity(entityIndex), default(T));
            }

            public void OnEntityRemoved(Filter filter, int entityIndex) {
            }
        }

        private sealed class DestroyAndSpawnOnEnter : IFilterListener {
            public World World;
            public Entity Spawned;
            private bool _done;

            public void OnEntityAdded(Filter filter, int entityIndex) {
                if (_done) {
                    return;
                }
                _done = true;
                World.DestroyEntity(World.GetEntity(entityIndex));
                Spawned = World.CreateEntity(new XPosition());
            }

            public void OnEntityRemoved(Filter filter, int entityIndex) {
            }
        }

        private sealed class EventLog : WorldListenerBase {
            public readonly List<string> Events = new();

            public override void OnComponentAdded(int entityIndex, int typeIndex) =>
                Events.Add("+" + ComponentType.NameOf(typeIndex));

            public override void OnComponentRemoved(int entityIndex, int typeIndex) =>
                Events.Add("-" + ComponentType.NameOf(typeIndex));
        }

        [Test]
        public void Add_IncListenerRemovesComponent_ExcFilterKeepsEntity([Values(false, true)] bool always) {
            var world = new World();
            var frozen = world.Filter().Inc<XFrozen>().End();
            var active = world.Filter().Inc<XPosition>().Exc<XFrozen>().End();
            frozen.AddListener(new RemoveOnEnter<XFrozen> { World = world, Always = always });
            var e = world.CreateEntity(new XPosition());

            world.Add(e, new XFrozen());

            Assert.That(world.Has<XFrozen>(e), Is.False, "the listener took Frozen away again");
            Assert.That(active.Contains(e.Index), Is.True, "an entity without Frozen must stay in Exc<Frozen>");
            WorldInvariants.AssertConsistent(world, "after Add<Frozen>");

            // A one-shot listener lets the second Frozen stay; one that always removes it keeps the entity in.
            world.Add(e, new XFrozen());
            Assert.That(active.Contains(e.Index), Is.EqualTo(always));
            WorldInvariants.AssertConsistent(world, "after the second Add<Frozen>");
        }

        [Test]
        public void Add_ExcListenerRemovesComponentOnLeave_LaterExcFiltersKeepEntity() {
            var world = new World();
            var first = world.Filter().Inc<XPosition>().Exc<XFrozen>().End();
            var second = world.Filter().Inc<XVelocity>().Exc<XFrozen>().End();
            first.AddListener(new RemoveOnLeave<XFrozen> { World = world });
            var e = world.CreateEntity(new XPosition());
            world.Add(e, new XVelocity());

            world.Add(e, new XFrozen());

            Assert.That(world.Has<XFrozen>(e), Is.False);
            Assert.That(first.Contains(e.Index), Is.True);
            Assert.That(second.Contains(e.Index), Is.True, "a later Exc<Frozen> filter must not drop an entity that lost Frozen again");
            WorldInvariants.AssertConsistent(world, "after Add<Frozen>");
        }

        [Test]
        public void Add_IncListenerDestroysAndSpawnsInSameSlot_NewEntityKeepsItsExcFilter() {
            var world = new World();
            var frozen = world.Filter().Inc<XFrozen>().End();
            var active = world.Filter().Inc<XPosition>().Exc<XFrozen>().End();
            var listener = new DestroyAndSpawnOnEnter { World = world };
            frozen.AddListener(listener);
            var e = world.CreateEntity(new XPosition());

            world.Add(e, new XFrozen());

            var spawned = listener.Spawned;
            Assert.That(spawned.Index, Is.EqualTo(e.Index), "test setup: the new entity takes the freed slot");
            Assert.That(world.Has<XFrozen>(spawned), Is.False);
            Assert.That(active.Contains(spawned.Index), Is.True, "the new entity in the slot must stay in Exc<Frozen>");
            WorldInvariants.AssertConsistent(world, "after Add<Frozen>");
        }

        [Test]
        public void Add_NestedAddRolledBackByListener_ExcFilterOfNestedTypeKeepsEntity() {
            var world = new World();
            var frozen = world.Filter().Inc<XFrozen>().End();
            var stunned = world.Filter().Inc<XStunned>().End();
            var notStunned = world.Filter().Inc<XPosition>().Exc<XStunned>().End();
            frozen.AddListener(new AddOnEnter<XStunned> { World = world });
            stunned.AddListener(new RemoveOnEnter<XStunned> { World = world });
            var e = world.CreateEntity(new XPosition());

            world.Add(e, new XFrozen());

            Assert.That(world.Has<XStunned>(e), Is.False, "the nested Add<Stunned> was rolled back");
            Assert.That(notStunned.Contains(e.Index), Is.True, "Exc<Stunned> must keep the entity");
            WorldInvariants.AssertConsistent(world, "after Add<Frozen>");
        }

        [Test]
        public void Add_IncListenerAddsOrRemovesOtherComponent_FiltersStayExact([Values(false, true)] bool addOther) {
            var world = new World();
            var frozen = world.Filter().Inc<XFrozen>().End();
            world.Filter().Inc<XPosition>().Exc<XFrozen>().End();
            world.Filter().Inc<XPosition>().Exc<XStunned>().End();
            world.Filter().Inc<XFrozen>().Exc<XStunned>().End();
            world.Filter().Any<XFrozen>().Any<XStunned>().End();
            var e = world.CreateEntity(new XPosition());
            if (addOther) {
                frozen.AddListener(new AddOnEnter<XStunned> { World = world });
            } else {
                world.Add(e, new XStunned());
                frozen.AddListener(new RemoveOnEnter<XStunned> { World = world });
            }

            world.Add(e, new XFrozen());

            Assert.That(world.Has<XFrozen>(e), Is.True);
            Assert.That(world.Has<XStunned>(e), Is.EqualTo(addOther));
            WorldInvariants.AssertConsistent(world, "after Add<Frozen>");
        }

        [Test]
        public void Add_FilterListenerRemovesComponent_WorldListenersDoNotSeeItAdded() {
            var world = new World();
            var frozen = world.Filter().Inc<XFrozen>().End();
            frozen.AddListener(new RemoveOnEnter<XFrozen> { World = world });
            var e = world.CreateEntity(new XPosition());
            var log = new EventLog();
            world.AddEventListener(log);

            world.Add(e, new XFrozen());

            Assert.That(log.Events, Is.EqualTo(new[] { "-" + nameof(XFrozen) }),
                "the removal is reported; an Added after it would leave listeners thinking the entity has Frozen");
        }

        [Test]
        public void Add_NoFilterListener_WorldListenersSeeAdd() {
            var world = new World();
            world.Filter().Inc<XFrozen>().End();
            world.Filter().Inc<XPosition>().Exc<XFrozen>().End();
            var e = world.CreateEntity(new XPosition());
            var log = new EventLog();
            world.AddEventListener(log);

            world.Add(e, new XFrozen());

            Assert.That(log.Events, Is.EqualTo(new[] { "+" + nameof(XFrozen) }));
        }

        private sealed class RemoveAndAddBackOnEnter : IFilterListener {
            public World World;
            private bool _done;

            public void OnEntityAdded(Filter filter, int entityIndex) {
                if (_done) {
                    return;
                }
                _done = true;
                var entity = World.GetEntity(entityIndex);
                World.Remove<XStunned>(entity);
                World.Add(entity, new XStunned());
            }

            public void OnEntityRemoved(Filter filter, int entityIndex) {
            }
        }

        private sealed class CountAdded : IComponentListener<XStunned> {
            public int Added;

            public void OnAdded(int entityIndex, ref XStunned component) =>
                Added++;

            public void OnRemoved(int entityIndex, ref XStunned component) {
            }
        }

        // Documented (world events guide, architecture): the outer Add checks the mask bit, which the
        // re-add set again, so world listeners hear of the removal and of two additions; typed
        // listeners get one OnAdded, from the nested Add. As in 2.2.1.
        [Test]
        public void Add_FilterListenerRemovesAndAddsBack_WorldListenersGetTwoAdditions() {
            var world = new World();
            world.Filter().Inc<XStunned>().End().AddListener(new RemoveAndAddBackOnEnter { World = world });
            var typed = new CountAdded();
            world.Pool<XStunned>().AddListener(typed);
            var e = world.CreateEntity(new XPosition());
            var log = new EventLog();
            world.AddEventListener(log);

            world.Add(e, new XStunned());

            Assert.That(world.Has<XStunned>(e), Is.True);
            Assert.That(log.Events, Is.EqualTo(new[] { "-" + nameof(XStunned), "+" + nameof(XStunned), "+" + nameof(XStunned) }));
            Assert.That(typed.Added, Is.EqualTo(1));
        }
    }
}
