using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using NUnit.Framework;

namespace KenseiECS.Tests {
    // The branches the hot-path optimizations added: the table-free trailing-zero count used under Unity,
    // DestroyEntity's mask words read once and again only after a removal, the _dispatch test inlined
    // into CreateEntity/DestroyEntity, and the free-slot list grown out of ReleaseSlot.
    [TestFixture]
    public class HotPathFastPathTests {
        private struct HpLow : IComponent {
            public int V;
        }

        private struct HpHigh : IComponent {
            public int V;
        }

        private struct HpLate : IComponent {
            public int V;
        }

        private struct HpPad<T> : IComponent {
        }

        // HpLow, then at least 130 padding types, then HpHigh, then 70 more, then HpLate: HpHigh lies
        // two or more mask words above HpLow (with empty words between them) and HpLate one or more above HpHigh.
        private static int _low;
        private static int _high;
        private static int _late;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int RegisterPadding(int count, ref Type seed) {
            int last = -1;
            for (int i = 0; i < count; i++) {
                seed = seed.MakeArrayType();
                var pad = typeof(HpPad<>).MakeGenericType(seed);
                last = (int)typeof(ComponentType<>).MakeGenericType(pad).GetField("Index").GetValue(null);
            }
            return last;
        }

        [OneTimeSetUp]
        public void RegisterTypes() {
            Type seed = typeof(HotPathFastPathTests);
            _low = ComponentType<HpLow>.Index;
            RegisterPadding(130, ref seed);
            _high = ComponentType<HpHigh>.Index;
            RegisterPadding(70, ref seed);
            _late = ComponentType<HpLate>.Index;
            Assert.That(_high >> 6, Is.GreaterThanOrEqualTo((_low >> 6) + 2), "test setup: empty mask words between HpLow and HpHigh");
            Assert.That(_late >> 6, Is.GreaterThan(_high >> 6), "test setup: HpLate in a mask word above HpHigh");
        }

        // ---- Trailing zeros without a table (the Unity build has no BitOperations)

        [Test]
        public void PortableTrailingZeroCount_MatchesBitOperations_ForEverySingleBit() {
            for (int bit = 0; bit < 64; bit++) {
                ulong value = 1UL << bit;
                Assert.That(World.PortableTrailingZeroCount(value), Is.EqualTo(bit), $"1 << {bit}");
                Assert.That(World.PortableTrailingZeroCount(ulong.MaxValue << bit), Is.EqualTo(bit), $"ones from {bit}");
            }
        }

        [Test]
        public void PortableTrailingZeroCount_MatchesBitOperations_ForRandomValues() {
            var random = new Random(12345);
            var bytes = new byte[8];
            for (int i = 0; i < 100_000; i++) {
                random.NextBytes(bytes);
                ulong value = BitConverter.ToUInt64(bytes, 0) >> random.Next(64);
                Assert.That(World.PortableTrailingZeroCount(value),
                    Is.EqualTo(System.Numerics.BitOperations.TrailingZeroCount(value)), $"0x{value:X16}");
            }
        }

        [Test]
        public void PortableTrailingZeroCount_OfZero_Is64LikeBitOperations() {
            Assert.That(World.PortableTrailingZeroCount(0), Is.EqualTo(64));
            Assert.That(System.Numerics.BitOperations.TrailingZeroCount(0UL), Is.EqualTo(64));
        }

        // ---- DestroyEntity over a wide mask

        [Test]
        public void DestroyEntity_ComponentsAcrossEmptyMaskWords_AllRemovedAndSlotClean() {
            var world = new World();
            var filter = world.Filter().Inc<HpLow>().Inc<HpHigh>().End();
            var lowOnly = world.Filter().Inc<HpLow>().Exc<HpHigh>().End();
            var entity = world.CreateEntity(new HpLow { V = 1 });
            world.Add(entity, new HpHigh { V = 2 });
            var other = world.CreateEntity(new HpHigh { V = 3 });
            Assert.That(filter.Count, Is.EqualTo(1));

            world.DestroyEntity(entity);

            Assert.That(world.IsAlive(entity), Is.False);
            Assert.That(world.Pool<HpLow>().Count, Is.EqualTo(0));
            Assert.That(world.Pool<HpHigh>().Count, Is.EqualTo(1));
            Assert.That(filter.Count, Is.EqualTo(0));
            Assert.That(lowOnly.Count, Is.EqualTo(0));
            Assert.That(world.Get<HpHigh>(other).V, Is.EqualTo(3));

            // The freed slot comes back with an empty mask in every word.
            var reused = world.CreateEntity(new HpLate { V = 4 });
            Assert.That(reused.Index, Is.EqualTo(entity.Index));
            Assert.That(world.Has<HpLow>(reused), Is.False);
            Assert.That(world.Has<HpHigh>(reused), Is.False);
            Assert.That(world.GetComponentCount(reused), Is.EqualTo(1));
            WorldInvariants.AssertConsistent(world, "after");
        }

        // A listener of a removal adds a type whose pool the world creates right then, in a mask word it
        // did not have: the mask arrays are replaced in the middle of the drain, which must read the new ones.
        private sealed class AddLateOnRemoved : IComponentListener<HpLow> {
            public World World;
            public ulong[][] MasksBefore;
            public bool MasksReplaced;

            public void OnAdded(int entityIndex, ref HpLow component) {
            }

            public void OnRemoved(int entityIndex, ref HpLow component) {
                MasksBefore = World._componentMasks;
                World.Add(World.GetEntity(entityIndex), new HpLate { V = 7 });
                MasksReplaced = !ReferenceEquals(MasksBefore, World._componentMasks);
            }
        }

        private sealed class CountLateRemovals : IComponentListener<HpLate> {
            public int Added;
            public int Removed;

            public void OnAdded(int entityIndex, ref HpLate component) {
                Added++;
            }

            public void OnRemoved(int entityIndex, ref HpLate component) {
                Removed++;
            }
        }

        [Test]
        public void DestroyEntity_ListenerAddsTypeInNewMaskWord_ComponentIsRemovedToo() {
            var world = new World();
            var entity = world.CreateEntity(new HpLow { V = 1 });
            var addLate = new AddLateOnRemoved { World = world };
            world.Pool<HpLow>().AddListener(addLate);

            world.DestroyEntity(entity);

            Assert.That(addLate.MasksReplaced, Is.True, "test setup: the late pool must add a mask word");
            Assert.That(world.IsAlive(entity), Is.False);
            Assert.That(world.Pool<HpLow>().Count, Is.EqualTo(0));
            Assert.That(world.Pool<HpLate>().Count, Is.EqualTo(0), "the component added during the destroy is removed");
            Assert.That(world.EntityCount, Is.EqualTo(0));
            WorldInvariants.AssertConsistent(world, "after");
        }

        [Test]
        public void DestroyEntity_ListenerAddsTypeInNewMaskWord_ItsListenersSeeOneRemoval() {
            var world = new World();
            var lateListener = new CountLateRemovals();
            // The late pool must not exist yet; its listener is attached from the removal listener.
            var entity = world.CreateEntity(new HpLow { V = 1 });
            world.Pool<HpLow>().AddListener(new AttachAndAdd { World = world, Listener = lateListener });

            world.DestroyEntity(entity);

            Assert.That(lateListener.Added, Is.EqualTo(1));
            Assert.That(lateListener.Removed, Is.EqualTo(1));
            Assert.That(world.Pool<HpLate>().Count, Is.EqualTo(0));
            WorldInvariants.AssertConsistent(world, "after");
        }

        private sealed class AttachAndAdd : IComponentListener<HpLow> {
            public World World;
            public CountLateRemovals Listener;

            public void OnAdded(int entityIndex, ref HpLow component) {
            }

            public void OnRemoved(int entityIndex, ref HpLow component) {
                var pool = World.Pool<HpLate>();
                pool.AddListener(Listener);
                pool.Add(entityIndex, new HpLate { V = 1 });
            }
        }

        // ---- The _dispatch test inlined into CreateEntity and DestroyEntity

        private sealed class RecordingWorldListener : WorldListenerBase {
            public readonly List<string> Events = new List<string>();

            public override void OnEntityCreated(int entityIndex) {
                Events.Add("created " + entityIndex);
            }

            public override void OnEntityDestroyed(int entityIndex) {
                Events.Add("destroyed " + entityIndex);
            }
        }

        // A typed listener subscribes the first world listener while CreateEntity adds the component: the
        // dispatch is decided after the add, so the new listener gets OnEntityCreated for this entity.
        private sealed class SubscribeOnAdded : IComponentListener<HpLow> {
            public World World;
            public IWorldEventListener Listener;

            public void OnAdded(int entityIndex, ref HpLow component) {
                if (Listener != null) {
                    World.AddEventListener(Listener);
                    Listener = null;
                }
            }

            public void OnRemoved(int entityIndex, ref HpLow component) {
            }
        }

        [Test]
        public void CreateEntity_WorldListenerSubscribedDuringTheAdd_GetsOnEntityCreated() {
            var world = new World();
            var recorder = new RecordingWorldListener();
            world.Pool<HpLow>().AddListener(new SubscribeOnAdded { World = world, Listener = recorder });

            var entity = world.CreateEntity(new HpLow());

            Assert.That(recorder.Events, Is.EqualTo(new[] { "created " + entity.Index }));
        }

        [Test]
        public void CreateAndDestroy_WithoutListeners_NoDispatch_WithListenerEveryEvent() {
            var world = new World();
            var silent = world.CreateEntity(new HpLow());
            world.DestroyEntity(silent);

            var recorder = new RecordingWorldListener();
            world.AddEventListener(recorder);
            var entity = world.CreateEntity(new HpLow());
            world.DestroyEntity(entity);
            world.RemoveEventListener(recorder);
            var after = world.CreateEntity(new HpLow());
            world.DestroyEntity(after);

            Assert.That(recorder.Events, Is.EqualTo(new[] { "created " + entity.Index, "destroyed " + entity.Index }));
        }

        private sealed class DestroyOnAdded : IComponentListener<HpLow> {
            public World World;

            public void OnAdded(int entityIndex, ref HpLow component) {
                World.DestroyEntity(World.GetEntity(entityIndex));
            }

            public void OnRemoved(int entityIndex, ref HpLow component) {
            }
        }

        [Test]
        public void CreateEntity_DestroyedByItsComponentListener_NoOnEntityCreated() {
            var world = new World();
            var recorder = new RecordingWorldListener();
            world.AddEventListener(recorder);
            world.Pool<HpLow>().AddListener(new DestroyOnAdded { World = world });

            var entity = world.CreateEntity(new HpLow());

            Assert.That(world.IsAlive(entity), Is.False);
            Assert.That(recorder.Events, Is.EqualTo(new[] { "destroyed " + entity.Index }));
        }

        // ---- The free-slot list grown out of ReleaseSlot

        [Test]
        public void DestroyEntity_PastFreeListCapacity_GrowsAndReusesSlotsLastFreedFirst() {
            // InitialEntityCapacity 4 gives a free-slot list of 16: destroying 40 grows it twice.
            var world = new World(new WorldConfig { InitialEntityCapacity = 4 });
            var entities = new Entity[40];
            for (int i = 0; i < entities.Length; i++) {
                entities[i] = world.CreateEntity(new HpLow { V = i });
            }
            for (int i = 0; i < entities.Length; i++) {
                world.DestroyEntity(entities[i]);
            }
            Assert.That(world.EntityCount, Is.EqualTo(0));

            for (int i = entities.Length - 1; i >= 0; i--) {
                var reused = world.CreateEntity(new HpLow { V = i });
                Assert.That(reused.Index, Is.EqualTo(entities[i].Index), "freed slots come back last freed first");
                Assert.That(reused.Generation, Is.EqualTo(entities[i].Generation + 1));
                Assert.That(world.IsAlive(entities[i]), Is.False);
            }
            Assert.That(world.EntityCount, Is.EqualTo(entities.Length));
            WorldInvariants.AssertConsistent(world, "after");
        }
    }
}
