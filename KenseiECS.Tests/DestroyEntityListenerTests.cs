using System.Collections.Generic;
using System.Runtime.CompilerServices;
using NUnit.Framework;

namespace KenseiECS.Tests {
    // DestroyEntity removes the components one by one: what the listeners of each removal see of
    // the others, and a listener that calls World.Clear in the middle.
    [TestFixture]
    public class DestroyEntityListenerTests {
        // Registered in this order (lowest index first), so DestroyEntity removes DlHealth first.
        private struct DlHealth : IComponent {
        }

        private struct DlPosition : IComponent {
            public int V;
        }

        private struct DlMarker : IComponent {
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int[] RegisterInOrder() {
            return new[] {
                ComponentType<DlHealth>.Index,
                ComponentType<DlPosition>.Index,
                ComponentType<DlMarker>.Index
            };
        }

        [OneTimeSetUp]
        public void RegisterTypes() {
            var indices = RegisterInOrder();
            for (int i = 1; i < indices.Length; i++) {
                Assert.That(indices[i], Is.GreaterThan(indices[i - 1]), "test setup: DestroyEntity must reach DlHealth first");
            }
        }

        // ---- What a listener sees of the components not removed yet

        private sealed class SeeOthersOnRemoved : IComponentListener<DlHealth> {
            public World World;
            public bool WorldHas;
            public bool PoolHas;
            public int Value;
            public bool AddIfMissing;

            public void OnAdded(int entityIndex, ref DlHealth component) {
            }

            public void OnRemoved(int entityIndex, ref DlHealth component) {
                var entity = World.GetEntity(entityIndex);
                WorldHas = World.Has<DlPosition>(entity);
                PoolHas = World.Pool<DlPosition>().Has(entityIndex);
                Value = PoolHas ? World.Pool<DlPosition>().Get(entityIndex).V : -1;
                if (AddIfMissing && !WorldHas) {
                    World.Add(entity, new DlPosition { V = 1 });
                }
            }
        }

        // Up to 2.2.1 DestroyEntity cleared the entity's mask word before the removals, so
        // world.Has said false for a component still in its pool (and still readable with Get).
        [Test]
        public void DestroyEntity_ListenerOfOneComponent_SeesTheOthersWithHas() {
            var world = new World();
            var listener = new SeeOthersOnRemoved { World = world };
            world.Pool<DlHealth>().AddListener(listener);
            var e = world.CreateEntity(new DlHealth());
            world.Add(e, new DlPosition { V = 8 });

            world.DestroyEntity(e);

            Assert.That(listener.PoolHas, Is.True, "test setup: DlPosition is removed after DlHealth");
            Assert.That(listener.WorldHas, Is.True, "world.Has must agree with the pool during DestroyEntity, as during Remove");
            Assert.That(listener.Value, Is.EqualTo(8));
            WorldInvariants.AssertConsistent(world, "after DestroyEntity");
        }

        [Test]
        public void Remove_ListenerOfOneComponent_SeesTheOthersWithHas() {
            var world = new World();
            var listener = new SeeOthersOnRemoved { World = world };
            world.Pool<DlHealth>().AddListener(listener);
            var e = world.CreateEntity(new DlHealth());
            world.Add(e, new DlPosition { V = 7 });

            world.Remove<DlHealth>(e);

            Assert.That(listener.WorldHas, Is.True);
            Assert.That(listener.Value, Is.EqualTo(7));
        }

        // `if (!world.Has<B>(e)) world.Add(e, ...)` threw "already has component" in both builds.
        [Test]
        public void DestroyEntity_ListenerAddsWhatIsMissing_DoesNotThrow() {
            var world = new World();
            world.Pool<DlHealth>().AddListener(new SeeOthersOnRemoved { World = world, AddIfMissing = true });
            var e = world.CreateEntity(new DlHealth());
            world.Add(e, new DlPosition { V = 8 });

            Assert.That(() => world.DestroyEntity(e), Throws.Nothing);

            Assert.That(world.IsAlive(e), Is.False);
            WorldInvariants.AssertConsistent(world, "after DestroyEntity");
        }

        // ---- The dying entity must enter no filter on the way out

        // A component type in another mask word than DlHealth: 64 types are registered in between.
        private struct Gap<T> : IComponent {
        }

        private struct FarPosition : IComponent {
        }

        private struct W0 { } private struct W1 { } private struct W2 { } private struct W3 { }
        private struct W4 { } private struct W5 { } private struct W6 { } private struct W7 { }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int RegisterFarType() {
            _ = ComponentType<DlHealth>.Index;
            RegisterGaps<W0>(); RegisterGaps<W1>(); RegisterGaps<W2>(); RegisterGaps<W3>();
            RegisterGaps<W4>(); RegisterGaps<W5>(); RegisterGaps<W6>(); RegisterGaps<W7>();
            return ComponentType<FarPosition>.Index;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RegisterGaps<T>() {
            _ = ComponentType<Gap<(T, byte)>>.Index; _ = ComponentType<Gap<(T, sbyte)>>.Index;
            _ = ComponentType<Gap<(T, short)>>.Index; _ = ComponentType<Gap<(T, ushort)>>.Index;
            _ = ComponentType<Gap<(T, int)>>.Index; _ = ComponentType<Gap<(T, uint)>>.Index;
            _ = ComponentType<Gap<(T, long)>>.Index; _ = ComponentType<Gap<(T, ulong)>>.Index;
        }

        private sealed class EnterLog : IFilterListener {
            public int Entered;

            public void OnEntityAdded(Filter filter, int entityIndex) => Entered++;

            public void OnEntityRemoved(Filter filter, int entityIndex) {
            }
        }

        // The mask is no longer cleared ahead of the removals, so the remaining components of the
        // dying entity must not let it into an Exc filter (DlPosition is in DlHealth's mask word).
        [Test]
        public void DestroyEntity_ComponentsInOneMaskWord_DyingEntityEntersNoExcFilter() {
            var world = new World();
            var log = new EnterLog();
            var filter = world.Filter().Inc<DlPosition>().Exc<DlHealth>().End();
            filter.AddListener(log);
            var e = world.CreateEntity(new DlHealth());
            world.Add(e, new DlPosition());

            world.DestroyEntity(e);

            Assert.That(log.Entered, Is.EqualTo(0), "a dying entity must not enter a filter");
            Assert.That(filter.Count, Is.EqualTo(0));
            WorldInvariants.AssertConsistent(world, "after DestroyEntity");
        }

        // Up to 2.2.1 only the mask word being drained was cleared: removing DlHealth put the dying
        // entity into Inc<FarPosition>.Exc<DlHealth> (its listener saw it enter) until FarPosition went.
        [Test]
        public void DestroyEntity_ComponentsInTwoMaskWords_DyingEntityEntersNoExcFilter() {
            int far = RegisterFarType();
            Assume.That(far >> 6, Is.Not.EqualTo(ComponentType<DlHealth>.Index >> 6), "test setup: FarPosition in another mask word");
            var world = new World();
            var log = new EnterLog();
            var filter = world.Filter().Inc<FarPosition>().Exc<DlHealth>().End();
            filter.AddListener(log);
            var e = world.CreateEntity(new DlHealth());
            world.Add(e, new FarPosition());

            world.DestroyEntity(e);

            Assert.That(log.Entered, Is.EqualTo(0), "a dying entity must not enter a filter");
            Assert.That(filter.Count, Is.EqualTo(0));
            WorldInvariants.AssertConsistent(world, "after DestroyEntity");
        }

        private sealed class AddMarkerOnRemoved : IComponentListener<DlHealth> {
            public World World;
            private bool _done;

            public void OnAdded(int entityIndex, ref DlHealth component) {
            }

            public void OnRemoved(int entityIndex, ref DlHealth component) {
                if (_done) {
                    return;
                }
                _done = true;
                World.Add(World.GetEntity(entityIndex), new DlMarker());
            }
        }

        private sealed class AliveOnEnter : IFilterListener {
            public World World;
            public readonly List<bool> Entered = new();
            public int Left;

            public void OnEntityAdded(Filter filter, int entityIndex) =>
                Entered.Add(World.IsAlive(World.GetEntity(entityIndex)));

            public void OnEntityRemoved(Filter filter, int entityIndex) =>
                Left++;
        }

        // Documented (filters page, IFilterListener): a component that a listener adds to the dying
        // entity lets it enter the filters it then matches, with the components not removed yet, and
        // OnEntityAdded sees it dead; the destroy takes it out again.
        [Test]
        public void DestroyEntity_ListenerReAddsAComponent_DyingEntityEntersAndLeavesItsFilters() {
            var world = new World();
            var log = new AliveOnEnter { World = world };
            var filter = world.Filter().Inc<DlPosition>().Inc<DlMarker>().End();
            filter.AddListener(log);
            world.Pool<DlHealth>().AddListener(new AddMarkerOnRemoved { World = world });
            var e = world.CreateEntity(new DlHealth());
            world.Add(e, new DlPosition());

            world.DestroyEntity(e);

            Assert.That(log.Entered, Is.EqualTo(new[] { false }), "the dying entity enters with DlPosition, which is not removed yet");
            Assert.That(log.Left, Is.EqualTo(1));
            Assert.That(filter.Count, Is.EqualTo(0));
            WorldInvariants.AssertConsistent(world, "after DestroyEntity");
        }

        // ---- World.Clear from a listener inside DestroyEntity

        private sealed class ClearOnDestroyed : WorldListenerBase {
            public World World;
            public bool Armed = true;

            public override void OnEntityDestroyed(int entityIndex) {
                if (Armed) {
                    Armed = false;
                    World.Clear();
                }
            }
        }

        private sealed class ClearOnRemoved : IComponentListener<DlHealth> {
            public World World;
            public bool Armed = true;
            public bool SpawnAfter;
            public Entity Spawned;

            public void OnAdded(int entityIndex, ref DlHealth component) {
            }

            public void OnRemoved(int entityIndex, ref DlHealth component) {
                if (!Armed) {
                    return;
                }
                Armed = false;
                World.Clear();
                if (SpawnAfter) {
                    // Takes slot 0 again: Clear started the slots over.
                    Spawned = World.CreateEntity(new DlPosition { V = 42 });
                }
            }
        }

        private static void AssertUsableAfterClear(World world) {
            Assert.That(world.EntityCount, Is.EqualTo(0), "Clear removed every entity");
            WorldInvariants.AssertConsistent(world, "after the destroy that Clear interrupted");
            var a = world.CreateEntity(new DlMarker());
            var b = world.CreateEntity(new DlMarker());
            Assert.That(world.IsAlive(a), Is.True, "a new entity must be alive");
            Assert.That(world.IsAlive(b), Is.True, "a new entity must be alive");
            Assert.That(world.EntityCount, Is.EqualTo(2));
            WorldInvariants.AssertConsistent(world, "after creating two entities");
        }

        // Up to 2.2.1 the destroy went on after Clear and released the slot again: EntityCount -1,
        // a free slot past the end, and the next entity created there was not alive.
        [Test]
        public void DestroyEntity_ClearFromOnEntityDestroyed_WorldStaysUsable() {
            var world = new World();
            world.CreateEntity(new DlMarker());
            var x = world.CreateEntity(new DlHealth());
            world.Add(x, new DlPosition());
            world.AddEventListener(new ClearOnDestroyed { World = world });

            world.DestroyEntity(x);

            AssertUsableAfterClear(world);
        }

        [Test]
        public void DestroyEntity_ClearFromOnRemoved_WorldStaysUsable() {
            var world = new World();
            world.CreateEntity(new DlMarker());
            var x = world.CreateEntity(new DlHealth());
            world.Add(x, new DlPosition());
            world.Pool<DlHealth>().AddListener(new ClearOnRemoved { World = world });

            world.DestroyEntity(x);

            AssertUsableAfterClear(world);
        }

        // The listener creates an entity after Clear, in the slot being destroyed: the destroy must
        // not remove its component (and destroy it with the last one).
        [Test]
        public void DestroyEntity_ClearFromOnRemovedThenSpawnInTheSlot_SpawnedEntityKeepsItsComponent() {
            var world = new World();
            var x = world.CreateEntity(new DlHealth());
            world.Add(x, new DlPosition());
            var listener = new ClearOnRemoved { World = world, SpawnAfter = true };
            world.Pool<DlHealth>().AddListener(listener);

            world.DestroyEntity(x);

            var spawned = listener.Spawned;
            Assert.That(spawned.Index, Is.EqualTo(x.Index), "test setup: the spawned entity takes the slot");
            Assert.That(world.IsAlive(spawned), Is.True);
            Assert.That(world.Has<DlPosition>(spawned), Is.True);
            Assert.That(world.Get<DlPosition>(spawned).V, Is.EqualTo(42));
            Assert.That(world.EntityCount, Is.EqualTo(1));
            WorldInvariants.AssertConsistent(world, "after DestroyEntity");
            var next = world.CreateEntity(new DlMarker());
            Assert.That(next.Index, Is.Not.EqualTo(spawned.Index), "the spawned entity's slot is not free");
            WorldInvariants.AssertConsistent(world, "after the next CreateEntity");
        }

        // The last component's Remove destroys the entity: a Clear in that destroy ends it too.
        [Test]
        public void RemoveLastComponent_ClearFromOnEntityDestroyed_WorldStaysUsable() {
            var world = new World();
            world.CreateEntity(new DlMarker());
            var x = world.CreateEntity(new DlHealth());
            world.AddEventListener(new ClearOnDestroyed { World = world });

            world.Remove<DlHealth>(x);

            AssertUsableAfterClear(world);
        }
    }
}
