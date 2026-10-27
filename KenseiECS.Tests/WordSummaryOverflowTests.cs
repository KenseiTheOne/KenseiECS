using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using NUnit.Framework;

namespace KenseiECS.Tests {
    // The summary of the words in use has a bit per mask word up to 62; bit 63 stands for every word
    // from 63 on, which DestroyEntity and CopyEntity then read one by one. These types sit past 4032
    // component types, around that boundary.
    [TestFixture]
    public class WordSummaryOverflowTests {
        private struct OLow : IComponent {
            public int V;
        }

        // Word 63, the first word of bit 63.
        private struct O63 : IComponent {
        }

        // Word 64.
        private struct O64 : IComponent {
            public int V;
        }

        // Word 66: word 65 stays empty.
        private struct O66 : IComponent {
            public int V;
        }

        private struct Pad<T1, T2, T3> : IComponent {
        }

        private struct Base<T> {
        }

        private struct Seed {
        }

        private static int _low, _o63, _o64, _o66;

        private static Type[] Bases(int count) {
            var bases = new Type[count];
            Type t = typeof(Seed);
            for (int i = 0; i < count; i++) {
                bases[i] = t;
                t = typeof(Base<>).MakeGenericType(t);
            }
            return bases;
        }

        private static int _next;

        // Registers fresh component types until the next one registered gets index target.
        private static void FillTo(Type[] bases, int target) {
            int n = bases.Length;
            while (ComponentType.Count < target) {
                int i = _next++;
                var pad = typeof(Pad<,,>).MakeGenericType(bases[i % n], bases[i / n % n], bases[i / (n * n) % n]);
                RuntimeHelpers.RunClassConstructor(typeof(ComponentType<>).MakeGenericType(pad).TypeHandle);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RegisterTypes() {
            _low = ComponentType<OLow>.Index;
            var bases = Bases(18);
            FillTo(bases, 63 * 64 + 5);
            _o63 = ComponentType<O63>.Index;
            FillTo(bases, 64 * 64 + 9);
            _o64 = ComponentType<O64>.Index;
            FillTo(bases, 66 * 64 + 1);
            _o66 = ComponentType<O66>.Index;
        }

        [OneTimeSetUp]
        public void Setup() {
            RegisterTypes();
            Assert.That(_low >> 6, Is.LessThan(63), "test setup: OLow is past word 62");
            Assert.That(_o63 >> 6, Is.EqualTo(63));
            Assert.That(_o64 >> 6, Is.EqualTo(64));
            Assert.That(_o66 >> 6, Is.EqualTo(66));
        }

        private sealed class Log<T> : IComponentListener<T> where T : struct, IComponent {
            public List<string> Events;
            public string Name;
            public Action<int> OnRemove;

            public void OnAdded(int entityIndex, ref T component) {
                Events.Add("+" + Name);
            }

            public void OnRemoved(int entityIndex, ref T component) {
                Events.Add("-" + Name);
                OnRemove?.Invoke(entityIndex);
            }
        }

        private static World WorldWithLogs(List<string> events, out Log<O63> o63) {
            var world = new World();
            world.Pool<OLow>().AddListener(new Log<OLow> { Events = events, Name = "Low" });
            o63 = new Log<O63> { Events = events, Name = "63" };
            world.Pool<O63>().AddListener(o63);
            world.Pool<O64>().AddListener(new Log<O64> { Events = events, Name = "64" });
            world.Pool<O66>().AddListener(new Log<O66> { Events = events, Name = "66" });
            return world;
        }

        [Test]
        public void Destroy_ComponentsPastWord62_RemovedInTypeOrder() {
            var events = new List<string>();
            var world = WorldWithLogs(events, out _);
            var e = world.CreateEntity(new O66());
            world.Add(e, new O64());
            world.Add(e, new OLow());
            world.Add(e, new O63());
            Assert.That(world._usedWords[e.Index] >> 63, Is.EqualTo(1UL), "the words from 63 on share bit 63");
            events.Clear();

            world.DestroyEntity(e);

            Assert.That(events, Is.EqualTo(new[] { "-Low", "-63", "-64", "-66" }));
            Assert.That(world.EntityCount, Is.EqualTo(0));
            WorldInvariants.AssertConsistent(world, "after destroying an entity with components past word 62");
        }

        // Only word 66 is set: the drain walks words 63 to 66 one by one and removes it.
        [Test]
        public void Destroy_OnlyTheLastWord_Removed() {
            var events = new List<string>();
            var world = WorldWithLogs(events, out _);
            var e = world.CreateEntity(new O66 { V = 6 });
            var other = world.CreateEntity(new O64());
            events.Clear();

            world.DestroyEntity(e);

            Assert.That(events, Is.EqualTo(new[] { "-66" }));
            Assert.That(world.Pool<O66>().Count, Is.EqualTo(0));
            Assert.That(world.IsAlive(other), Is.True);
            WorldInvariants.AssertConsistent(world, "after destroying an entity with its only component in word 66");
        }

        // Added mid-pass in a later word of bit 63: removed in the same pass.
        [Test]
        public void Destroy_ListenerAddsInLaterOverflowWord_RemovedInSamePass() {
            var events = new List<string>();
            var world = WorldWithLogs(events, out var o63);
            o63.OnRemove = idx => world.Add(world.GetEntity(idx), new O64());
            var e = world.CreateEntity(new O63());
            world.Add(e, new O66());
            events.Clear();

            world.DestroyEntity(e);

            Assert.That(events, Is.EqualTo(new[] { "-63", "+64", "-64", "-66" }));
            Assert.That(world.EntityCount, Is.EqualTo(0));
            WorldInvariants.AssertConsistent(world, "after an add in an overflow word mid-destroy");
        }

        [Test]
        public void Copy_ComponentsPastWord62_AllCopied() {
            var events = new List<string>();
            var world = WorldWithLogs(events, out _);
            var src = world.CreateEntity(new O66 { V = 6 });
            world.Add(src, new OLow { V = 1 });
            world.Add(src, new O64 { V = 4 });
            events.Clear();

            var copy = world.CopyEntity(src);

            Assert.That(events, Is.EqualTo(new[] { "+Low", "+64", "+66" }));
            Assert.That(world.Get<OLow>(copy).V, Is.EqualTo(1));
            Assert.That(world.Get<O64>(copy).V, Is.EqualTo(4));
            Assert.That(world.Get<O66>(copy).V, Is.EqualTo(6));
            Assert.That(world.Has<O63>(copy), Is.False);
            WorldInvariants.AssertConsistent(world, "after copying an entity with components past word 62");
        }
        // ---- After an EnsureMaskWords that ran out of memory

        // The state an OutOfMemoryException leaves while EnsureMaskWords allocates the second of two new
        // words: the outer array is resized already, its last word is still null, and the word count is
        // the old one. The scans of DestroyEntity and CopyEntity stop at the word count, as the other
        // loops over the mask do, and never read the null word (on IL2CPP, with null checks off, that
        // read would crash the process).
        private static void SimulatePartialGrowth(World world) {
            var masks = world._componentMasks;
            int capacity = masks[0].Length;
            Array.Resize(ref masks, masks.Length + 2);
            masks[masks.Length - 2] = new ulong[capacity];
            world._componentMasks = masks;
        }

        private sealed class RemoveLowOnDestroy : WorldListenerBase {
            public World World;

            public override void OnEntityDestroyed(int entityIndex) {
                World.Pool<OLow>().Remove(entityIndex);
            }
        }

        [Test]
        public void Copy_AfterPartialMaskGrowth_CopiesEverything() {
            var world = new World();
            var src = world.CreateEntity(new O66 { V = 6 });
            world.Add(src, new OLow { V = 1 });
            SimulatePartialGrowth(world);

            var copy = world.CopyEntity(src);

            Assert.That(world.Get<OLow>(copy).V, Is.EqualTo(1));
            Assert.That(world.Get<O66>(copy).V, Is.EqualTo(6));
            Assert.That(world.GetComponentCount(copy), Is.EqualTo(2));
        }

        // The listener removes OLow before the drain, so the drain cannot stop at the count and reads on
        // past word 66, up to the word count.
        [Test]
        public void Destroy_AfterPartialMaskGrowth_RemovesEverything() {
            var world = new World();
            var e = world.CreateEntity(new O66 { V = 6 });
            world.Add(e, new OLow { V = 1 });
            world.AddEventListener(new RemoveLowOnDestroy { World = world });
            SimulatePartialGrowth(world);

            world.DestroyEntity(e);

            Assert.That(world.IsAlive(e), Is.False);
            Assert.That(world.Pool<O66>().Has(e.Index), Is.False);
            Assert.That(world.Pool<OLow>().Has(e.Index), Is.False);
        }
    }
}
