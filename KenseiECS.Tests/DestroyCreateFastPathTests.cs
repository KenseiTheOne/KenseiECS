using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using NUnit.Framework;

namespace KenseiECS.Tests {
    // The shortcuts of entity creation and destruction: the drain that stops once every component it
    // started with is gone (and reads no word for an auto-destroy), the word scan that re-reads the
    // mask arrays only after user code ran, the removal that keeps the count and the alive flag in
    // registers while no filter listener runs, and a new entity that relies on its free slot's zero
    // count. Each is checked on the branch it takes and on the one that falls back to the full walk.
    [TestFixture]
    public class DestroyCreateFastPathTests {
        // Registered with padding between them, so each lands in a mask word of its own, in this order.
        private struct WA : IComponent {
            public int V;
        }

        private struct WB : IComponent {
            public int V;
        }

        private struct WC : IComponent {
            public int V;
        }

        private struct WD : IComponent {
            public int V;
        }

        // Registered last: a world that creates its pool from a listener grows its mask words.
        private struct WLate : IComponent {
            public int V;
        }

        private struct Pad<T> : IComponent {
        }

        private struct SeedA {
        }

        private struct SeedB {
        }

        private struct SeedC {
        }

        private struct SeedD {
        }

        private static int _a, _b, _c, _d, _late;

        private static int Word(int typeIndex) => typeIndex >> 6;

        // Registers count fresh component types, so the next type registered gets an index at least
        // count higher.
        private static void Burn(Type seed, int count) {
            Type arg = typeof(Pad<>).MakeGenericType(seed);
            for (int i = 0; i < count; i++) {
                RuntimeHelpers.RunClassConstructor(typeof(ComponentType<>).MakeGenericType(arg).TypeHandle);
                arg = typeof(Pad<>).MakeGenericType(arg);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RegisterTypes() {
            _a = ComponentType<WA>.Index;
            Burn(typeof(SeedA), 64);
            _b = ComponentType<WB>.Index;
            Burn(typeof(SeedB), 64);
            _c = ComponentType<WC>.Index;
            Burn(typeof(SeedC), 192);
            _d = ComponentType<WD>.Index;
            Burn(typeof(SeedD), 64);
            _late = ComponentType<WLate>.Index;
        }

        [OneTimeSetUp]
        public void Setup() {
            RegisterTypes();
            Assert.That(Word(_a), Is.LessThan(Word(_b)), "test setup: WA and WB share a word");
            Assert.That(Word(_b), Is.LessThan(Word(_c)), "test setup: WB and WC share a word");
            Assert.That(Word(_c) + 2, Is.LessThan(Word(_d)), "test setup: no empty words between WC and WD");
            Assert.That(Word(_d), Is.LessThan(Word(_late)), "test setup: WD and WLate share a word");
        }

        // ---- Listeners

        // Logs every typed OnRemoved / OnAdded in one shared list and runs an optional action.
        private sealed class Log<T> : IComponentListener<T> where T : struct, IComponent {
            public List<string> Events;
            public string Name;
            public Action<int> OnRemove;
            public Action<int> OnAdd;

            public void OnAdded(int entityIndex, ref T component) {
                Events?.Add("+" + Name);
                OnAdd?.Invoke(entityIndex);
            }

            public void OnRemoved(int entityIndex, ref T component) {
                Events?.Add("-" + Name);
                OnRemove?.Invoke(entityIndex);
            }
        }

        private sealed class DestroyedAction : WorldListenerBase {
            public Action<int> Action;

            public override void OnEntityDestroyed(int entityIndex) {
                Action(entityIndex);
            }
        }

        private sealed class FilterAction : IFilterListener {
            public Action<int> Removed;

            public void OnEntityAdded(Filter filter, int entityIndex) {
            }

            public void OnEntityRemoved(Filter filter, int entityIndex) {
                Removed?.Invoke(entityIndex);
            }
        }

        // A world with every padded type's pool (mask words up to WD's) and a removal log on each.
        private static World WideWorld(List<string> events, out Log<WA> a, out Log<WB> b, out Log<WC> c, out Log<WD> d) {
            var world = new World();
            a = new Log<WA> { Events = events, Name = "A" };
            b = new Log<WB> { Events = events, Name = "B" };
            c = new Log<WC> { Events = events, Name = "C" };
            d = new Log<WD> { Events = events, Name = "D" };
            world.Pool<WA>().AddListener(a);
            world.Pool<WB>().AddListener(b);
            world.Pool<WC>().AddListener(c);
            world.Pool<WD>().AddListener(d);
            return world;
        }

        private static List<string> Removals(List<string> events) {
            return events.FindAll(s => s[0] == '-');
        }

        // ---- The drain stops once every component it started with is gone

        [Test]
        public void Destroy_WideWorld_ComponentsInLowWords_AllRemovedAndFiltersLeft() {
            var events = new List<string>();
            var world = WideWorld(events, out _, out _, out _, out _);
            var both = world.Filter().Inc<WA>().Inc<WB>().End();
            var notD = world.Filter().Inc<WA>().Exc<WD>().End();
            var anyCD = world.Filter().Any<WB>().Any<WD>().End();
            var e = world.CreateEntity(new WA { V = 1 });
            world.Add(e, new WB { V = 2 });
            var other = world.CreateEntity(new WD { V = 4 });
            world.Add(other, new WA { V = 5 });
            Assert.That(world._componentMasks.Length, Is.GreaterThan(Word(_d)), "test setup: WD's word is not the last one");

            world.DestroyEntity(e);

            Assert.That(world.IsAlive(e), Is.False);
            Assert.That(world.Pool<WA>().Has(e.Index) || world.Pool<WB>().Has(e.Index), Is.False);
            Assert.That(both.Count, Is.EqualTo(0));
            Assert.That(notD.Count, Is.EqualTo(0));
            Assert.That(anyCD.Count, Is.EqualTo(1), "the other entity stays in the Any filter");
            Assert.That(Removals(events), Is.EqualTo(new[] { "-A", "-B" }));
            Assert.That(world.EntityCount, Is.EqualTo(1));
            Assert.That(world.Get<WA>(other).V, Is.EqualTo(5), "the other entity keeps its components");
            WorldInvariants.AssertConsistent(world, "after destroying an entity with components in low words only");
        }

        [Test]
        public void Destroy_ComponentsInEveryWord_RemovedInTypeOrder() {
            var events = new List<string>();
            var world = WideWorld(events, out _, out _, out _, out _);
            var e = world.CreateEntity(new WD());
            world.Add(e, new WB());
            world.Add(e, new WA());
            world.Add(e, new WC());
            events.Clear();

            world.DestroyEntity(e);

            Assert.That(events, Is.EqualTo(new[] { "-A", "-B", "-C", "-D" }));
            WorldInvariants.AssertConsistent(world, "after destroying an entity with a component in every word");
        }

        // A component added to the dying entity in a word the pass has not reached yet: the count shows
        // the Add, so the pass does not stop when it has met as many bits as it started with, and removes
        // the component in the same pass, before the components of later words.
        [Test]
        public void Destroy_ListenerAddsInLaterWord_RemovedInSamePass() {
            var events = new List<string>();
            var world = WideWorld(events, out var a, out _, out _, out _);
            a.OnRemove = idx => world.Add(world.GetEntity(idx), new WC { V = 3 });
            var e = world.CreateEntity(new WA());
            world.Add(e, new WD());
            events.Clear();

            world.DestroyEntity(e);

            Assert.That(events, Is.EqualTo(new[] { "-A", "+C", "-C", "-D" }));
            Assert.That(world.Pool<WC>().Has(e.Index), Is.False);
            Assert.That(world.Pool<WD>().Has(e.Index), Is.False, "the pass must not stop after the second bit it met");
            Assert.That(world.EntityCount, Is.EqualTo(0));
            WorldInvariants.AssertConsistent(world, "after a component was added in a later word mid-destroy");
        }

        // Added in a word the pass has passed: the next pass removes it and stops after it.
        [Test]
        public void Destroy_ListenerAddsInEarlierWord_RemovedInNextPass() {
            var events = new List<string>();
            var world = WideWorld(events, out _, out _, out _, out var d);
            bool once = false;
            d.OnRemove = idx => {
                if (!once) {
                    once = true;
                    world.Add(world.GetEntity(idx), new WB { V = 2 });
                }
            };
            var e = world.CreateEntity(new WA());
            world.Add(e, new WD());
            events.Clear();

            world.DestroyEntity(e);

            Assert.That(events, Is.EqualTo(new[] { "-A", "-D", "+B", "-B" }));
            Assert.That(world.Pool<WB>().Has(e.Index), Is.False);
            Assert.That(world.EntityCount, Is.EqualTo(0));
            var reused = world.CreateEntity(new WC());
            Assert.That(reused.Index, Is.EqualTo(e.Index), "test setup: the slot is reused");
            Assert.That(world.GetComponentCount(reused), Is.EqualTo(1), "the released slot's count is zero");
            WorldInvariants.AssertConsistent(world, "after a component was added in an earlier word mid-destroy");
        }

        // The listener of the first removal adds a component and removes it again: the count still shows
        // the Add, so the pass reads every word, and nothing is left behind.
        [Test]
        public void Destroy_ListenerAddsAndRemovesAgain_EveryWordStillRead() {
            var events = new List<string>();
            var world = WideWorld(events, out var a, out _, out _, out _);
            a.OnRemove = idx => {
                var entity = world.GetEntity(idx);
                world.Add(entity, new WB());
                world.Remove<WB>(entity);
            };
            var e = world.CreateEntity(new WA());
            world.Add(e, new WC());
            world.Add(e, new WD());
            events.Clear();

            world.DestroyEntity(e);

            Assert.That(Removals(events), Is.EqualTo(new[] { "-A", "-B", "-C", "-D" }));
            Assert.That(world.EntityCount, Is.EqualTo(0));
            WorldInvariants.AssertConsistent(world, "after an add and remove mid-destroy");
        }

        // A world listener removes a component of the dying entity before the drain: the count is now
        // above the bits, the pass never meets as many bits as it started with and reads every word.
        [Test]
        public void Destroy_WorldListenerRemovesComponentFirst_TheRestAreRemoved() {
            var events = new List<string>();
            var world = WideWorld(events, out _, out _, out _, out _);
            world.AddEventListener(new DestroyedAction { Action = idx => world.Remove<WA>(world.GetEntity(idx)) });
            var filter = world.Filter().Inc<WC>().Inc<WD>().End();
            var e = world.CreateEntity(new WA());
            world.Add(e, new WC());
            world.Add(e, new WD());
            events.Clear();

            world.DestroyEntity(e);

            Assert.That(events, Is.EqualTo(new[] { "-A", "-C", "-D" }));
            Assert.That(filter.Count, Is.EqualTo(0));
            Assert.That(world.EntityCount, Is.EqualTo(0));
            WorldInvariants.AssertConsistent(world, "after a world listener removed a component before the drain");
        }

        // The listener of the first removal creates the pool of a type past the world's last mask word
        // and adds it to the dying entity: the scan re-reads the grown mask arrays and removes it in the
        // same pass, after the components of the words before it. The listener of the second removal
        // adds WA back, which only the next pass removes: after WLate, not before it.
        [Test]
        public void Destroy_ListenerGrowsMaskWords_NewWordRemovedInSamePass() {
            var events = new List<string>();
            var world = new World();
            var a = new Log<WA> { Events = events, Name = "A" };
            var b = new Log<WB> { Events = events, Name = "B" };
            world.Pool<WA>().AddListener(a);
            world.Pool<WB>().AddListener(b);
            var e = world.CreateEntity(new WA());
            world.Add(e, new WB());
            int wordsBefore = world._componentMasks.Length;
            Assert.That(wordsBefore, Is.LessThanOrEqualTo(Word(_late)), "test setup: WLate's word already exists");
            bool once = false;
            a.OnRemove = idx => {
                if (!once) {
                    once = true;
                    world.Pool<WLate>().AddListener(new Log<WLate> { Events = events, Name = "Late" });
                    world.Add(world.GetEntity(idx), new WLate());
                }
            };
            b.OnRemove = idx => world.Add(world.GetEntity(idx), new WA());
            events.Clear();

            world.DestroyEntity(e);

            Assert.That(world._componentMasks.Length, Is.GreaterThan(wordsBefore), "test setup: the mask words did not grow");
            Assert.That(events, Is.EqualTo(new[] { "-A", "+Late", "-B", "+A", "-Late", "-A" }));
            Assert.That(world.Pool<WLate>().Has(e.Index), Is.False);
            Assert.That(world.EntityCount, Is.EqualTo(0));
            WorldInvariants.AssertConsistent(world, "after the mask words grew mid-destroy");
        }

        [Test]
        public void Destroy_ListenerClearsWorldMidPass_StopsAndNewEntityKeepsSlot() {
            var events = new List<string>();
            var world = WideWorld(events, out var a, out _, out _, out _);
            Entity created = default;
            a.OnRemove = idx => {
                world.Clear();
                created = world.CreateEntity(new WD { V = 9 });
            };
            var e = world.CreateEntity(new WA());
            world.Add(e, new WC());
            events.Clear();

            world.DestroyEntity(e);

            Assert.That(created.Index, Is.EqualTo(e.Index), "test setup: the new entity took the slot");
            Assert.That(world.IsAlive(created), Is.True);
            Assert.That(world.Get<WD>(created).V, Is.EqualTo(9));
            Assert.That(world.GetComponentCount(created), Is.EqualTo(1));
            Assert.That(world.EntityCount, Is.EqualTo(1));
            Assert.That(Removals(events), Is.EqualTo(new[] { "-A" }), "nothing is removed after the Clear");
            WorldInvariants.AssertConsistent(world, "after a Clear mid-destroy");
        }

        // ---- Auto-destroy reads no mask word

        [Test]
        public void AutoDestroy_WideWorld_SlotReleasedAndReusedClean() {
            var events = new List<string>();
            var world = WideWorld(events, out _, out _, out _, out _);
            var filter = world.Filter().Inc<WD>().End();
            var e = world.CreateEntity(new WD());

            world.Remove<WD>(e);

            Assert.That(world.IsAlive(e), Is.False);
            Assert.That(world.EntityCount, Is.EqualTo(0));
            Assert.That(filter.Count, Is.EqualTo(0));
            var reused = world.CreateEntity(new WA());
            Assert.That(reused.Index, Is.EqualTo(e.Index), "test setup: the slot is reused");
            Assert.That(world.GetComponentCount(reused), Is.EqualTo(1));
            WorldInvariants.AssertConsistent(world, "after an auto-destroy in a wide world");
        }

        // The count of an auto-destroyed entity is 0, unless OnEntityDestroyed added a component: then the
        // drain must walk the words and remove it.
        [Test]
        public void AutoDestroy_DestroyedListenerAddsComponent_ItIsRemoved() {
            var events = new List<string>();
            var world = WideWorld(events, out _, out _, out _, out _);
            world.AddEventListener(new DestroyedAction { Action = idx => world.Add(world.GetEntity(idx), new WC { V = 3 }) });
            var e = world.CreateEntity(new WA());
            events.Clear();

            world.Remove<WA>(e);

            Assert.That(events, Is.EqualTo(new[] { "-A", "+C", "-C" }));
            Assert.That(world.Pool<WC>().Has(e.Index), Is.False);
            Assert.That(world.EntityCount, Is.EqualTo(0));
            WorldInvariants.AssertConsistent(world, "after OnEntityDestroyed added a component to an auto-destroyed entity");
        }

        // ---- Remove keeps the count in a register while no filter listener runs

        [Test]
        public void RemoveLast_NoFilter_Destroys() {
            var world = new World();
            var e = world.CreateEntity(new WB());
            world.Remove<WB>(e);
            Assert.That(world.IsAlive(e), Is.False);
            Assert.That(world.EntityCount, Is.EqualTo(0));
        }

        [Test]
        public void RemoveLast_FilterWithoutListener_DestroysAndLeavesFilter() {
            var world = new World();
            var inc = world.Filter().Inc<WB>().End();
            var exc = world.Filter().Inc<WA>().Exc<WB>().End();
            var e = world.CreateEntity(new WB());
            var other = world.CreateEntity(new WA());
            world.Add(other, new WB());

            world.Remove<WB>(e);
            world.Remove<WB>(other);

            Assert.That(world.IsAlive(e), Is.False);
            Assert.That(world.IsAlive(other), Is.True, "an entity with components left is not destroyed");
            Assert.That(inc.Count, Is.EqualTo(0));
            Assert.That(exc.Count, Is.EqualTo(1));
            WorldInvariants.AssertConsistent(world, "after removals without filter listeners");
        }

        // A filter listener of the last removal adds another component: the count is read again after it,
        // so the entity is not destroyed.
        [Test]
        public void RemoveLast_FilterListenerAddsComponent_EntitySurvives() {
            var world = new World();
            var filter = world.Filter().Inc<WB>().End();
            Entity e = default;
            filter.AddListener(new FilterAction { Removed = idx => world.Add(e, new WC { V = 7 }) });
            e = world.CreateEntity(new WB());

            world.Remove<WB>(e);

            Assert.That(world.IsAlive(e), Is.True);
            Assert.That(world.Get<WC>(e).V, Is.EqualTo(7));
            Assert.That(world.GetComponentCount(e), Is.EqualTo(1));
            WorldInvariants.AssertConsistent(world, "after a filter listener re-filled the entity");
        }

        // A filter listener of a removal (not the last one) removes the other component: the nested
        // removal destroys the entity, and the outer one, reading the flag again, does not destroy twice.
        [Test]
        public void Remove_FilterListenerRemovesTheOther_DestroyedOnce() {
            var world = new World();
            var filter = world.Filter().Inc<WB>().End();
            Entity e = default;
            filter.AddListener(new FilterAction { Removed = idx => world.Remove<WC>(e) });
            e = world.CreateEntity(new WB());
            world.Add(e, new WC());
            var other = world.CreateEntity(new WA());

            world.Remove<WB>(e);

            Assert.That(world.IsAlive(e), Is.False);
            Assert.That(world.IsAlive(other), Is.True);
            Assert.That(world.EntityCount, Is.EqualTo(1));
            WorldInvariants.AssertConsistent(world, "after a filter listener removed the last other component");
            var reused = world.CreateEntity(new WD());
            Assert.That(reused.Index, Is.EqualTo(e.Index), "test setup: the slot is reused");
            Assert.That(world.GetComponentCount(reused), Is.EqualTo(1));
        }

        // ---- A new entity relies on its free slot's zero count

        // The listener adds a component to the dying entity (the count is 1 again) and throws: the
        // interrupted destroy zeroes the count when it releases the slot, and the next entity there
        // starts from it.
        [Test]
        public void Create_InSlotOfDestroyThatThrew_CountIsOne() {
            var world = new World();
            var a = new Log<WA>();
            world.Pool<WA>().AddListener(a);
            var e = world.CreateEntity(new WA());
            world.Add(e, new WD());
            a.OnRemove = idx => {
                world.Add(world.GetEntity(idx), new WB());
                throw new InvalidOperationException("listener failed");
            };
            Assert.That(() => world.DestroyEntity(e), Throws.InvalidOperationException);
            a.OnRemove = null;

            var reused = world.CreateEntity(new WC());

            Assert.That(reused.Index, Is.EqualTo(e.Index), "test setup: the slot is reused");
            Assert.That(world.GetComponentCount(reused), Is.EqualTo(1));
            Assert.That(world.Has<WB>(reused), Is.False, "the released slot's mask is zero");
            world.Remove<WC>(reused);
            Assert.That(world.IsAlive(reused), Is.False, "the count reached 0 on the last removal");
        }

        [Test]
        public void Create_AfterDestroyAndClear_CountIsOne() {
            var world = new World();
            var a = world.CreateEntity(new WA());
            world.Add(a, new WB());
            world.Add(a, new WC());
            var b = world.CreateEntity(new WA());
            world.DestroyEntity(a);
            world.Clear();
            var first = world.CreateEntity(new WD());
            var second = world.CreateEntity(new WD());
            Assert.That(world.GetComponentCount(first), Is.EqualTo(1));
            Assert.That(world.GetComponentCount(second), Is.EqualTo(1));
            Assert.That(b.Index, Is.Not.Negative);
            WorldInvariants.AssertConsistent(world, "after Clear and new entities");
        }

        // ---- The summary of the words in use

        // Remove empties WC's word but keeps its bit: the drain reads the word, finds it zero and goes on.
        [Test]
        public void Destroy_WordEmptiedByRemove_StillDrainsTheRest() {
            var events = new List<string>();
            var world = WideWorld(events, out _, out _, out _, out _);
            var e = world.CreateEntity(new WA());
            world.Add(e, new WC());
            world.Add(e, new WD());
            world.Remove<WC>(e);
            Assert.That((world._usedWords[e.Index] >> Word(_c)) & 1, Is.EqualTo(1UL), "test setup: the emptied word keeps its bit");
            events.Clear();

            world.DestroyEntity(e);

            Assert.That(events, Is.EqualTo(new[] { "-A", "-D" }));
            Assert.That(world._usedWords[e.Index], Is.EqualTo(0UL), "the released slot's summary is reset");
            WorldInvariants.AssertConsistent(world, "after destroying an entity with an emptied word");
        }

        // Growing the slot arrays carries the summary over: the entity's high words are still drained.
        [Test]
        public void Destroy_AfterSlotStorageGrew_HighWordsStillDrained() {
            var events = new List<string>();
            var world = WideWorld(events, out _, out _, out _, out _);
            var e = world.CreateEntity(new WD { V = 4 });
            world.Add(e, new WB());
            int capacity = world._generations.Length;
            for (int i = 0; i <= capacity; i++) {
                world.CreateEntity(new WA());
            }
            Assert.That(world._generations.Length, Is.GreaterThan(capacity), "test setup: the slot arrays did not grow");
            Assert.That(world._usedWords.Length, Is.EqualTo(world._generations.Length));
            events.Clear();

            world.DestroyEntity(e);

            Assert.That(events, Is.EqualTo(new[] { "-B", "-D" }));
            Assert.That(world.Pool<WD>().Count, Is.EqualTo(0));
            WorldInvariants.AssertConsistent(world, "after destroying an entity once the slot arrays grew");
        }

        [Test]
        public void Clear_ResetsTheSummary() {
            var world = WideWorld(null, out _, out _, out _, out _);
            var e = world.CreateEntity(new WD());
            world.Add(e, new WB());
            Assert.That(world._usedWords[e.Index], Is.Not.EqualTo(0UL), "test setup");

            world.Clear();

            Assert.That(world._usedWords[e.Index], Is.EqualTo(0UL));
            var reused = world.CreateEntity(new WA());
            world.DestroyEntity(reused);
            WorldInvariants.AssertConsistent(world, "after Clear");
        }

        // A world of one mask word has no summary. The listener of the first removal creates the pool of
        // a type in a higher word (the summary comes with the second word) and adds it to the dying
        // entity: the drain finds its word through the new summary and removes it.
        [Test]
        public void Destroy_WorldGetsItsSecondWordMidDrain_NewWordDrained() {
            Type low = null;
            int lowIndex = -1;
            for (int i = 0; i < 64 && i < ComponentType.Count; i++) {
                var t = ComponentType.TypeOf(i);
                bool hooks = false;
                foreach (var it in t.GetInterfaces()) {
                    hooks |= it.IsGenericType && (it.GetGenericTypeDefinition() == typeof(IAutoReset<>)
                        || it.GetGenericTypeDefinition() == typeof(IAutoCopy<>));
                }
                if (!hooks && t != typeof(WD)) {
                    low = t;
                    lowIndex = i;
                    break;
                }
            }
            Assume.That(low, Is.Not.Null, "no plain component type in the first mask word");

            var events = new List<string>();
            var world = new World();
            var entity = (Entity)typeof(World).GetMethod(nameof(World.CreateEntity)).MakeGenericMethod(low)
                .Invoke(world, new[] { Activator.CreateInstance(low) });
            Assert.That(world._componentMasks.Length, Is.EqualTo(1), "test setup: one mask word");
            Assert.That(world._usedWords, Is.Null, "a world of one mask word has no summary");
            var listener = Activator.CreateInstance(typeof(Log<>).MakeGenericType(low));
            listener.GetType().GetField(nameof(Log<WA>.Events)).SetValue(listener, events);
            listener.GetType().GetField(nameof(Log<WA>.Name)).SetValue(listener, "Low");
            Action<int> addD = idx => world.Add(world.GetEntity(idx), new WD { V = 4 });
            listener.GetType().GetField(nameof(Log<WA>.OnRemove)).SetValue(listener, addD);
            var pool = world.GetPool(lowIndex);
            pool.GetType().GetMethod("AddListener").Invoke(pool, new[] { listener });

            world.DestroyEntity(entity);

            Assert.That(world._usedWords, Is.Not.Null, "test setup: the world did not get a second word");
            Assert.That(events, Is.EqualTo(new[] { "-Low" }));
            Assert.That(world.Pool<WD>().Has(entity.Index), Is.False, "the component in the new word is removed");
            Assert.That(world.EntityCount, Is.EqualTo(0));
            WorldInvariants.AssertConsistent(world, "after the world got its second word mid-destroy");
        }

        // ---- CopyEntity scans the words the same way

        [Test]
        public void Copy_ComponentsInEveryWord_AllCopiedInTypeOrder() {
            var events = new List<string>();
            var world = WideWorld(events, out _, out _, out _, out _);
            var src = world.CreateEntity(new WD { V = 4 });
            world.Add(src, new WA { V = 1 });
            world.Add(src, new WC { V = 3 });
            events.Clear();

            var copy = world.CopyEntity(src);

            Assert.That(events, Is.EqualTo(new[] { "+A", "+C", "+D" }));
            Assert.That(world.Get<WA>(copy).V, Is.EqualTo(1));
            Assert.That(world.Get<WC>(copy).V, Is.EqualTo(3));
            Assert.That(world.Get<WD>(copy).V, Is.EqualTo(4));
            Assert.That(world.Has<WB>(copy), Is.False);
            Assert.That(world.GetComponentCount(copy), Is.EqualTo(3));
            WorldInvariants.AssertConsistent(world, "after copying an entity with components in several words");
        }

        // A listener of the first copied component creates the pool of a type past the last mask word and
        // adds it to the source: the scan re-reads the grown mask arrays and copies it too.
        [Test]
        public void Copy_ListenerGrowsMaskWords_NewComponentCopied() {
            var world = new World();
            var a = new Log<WA> { Name = "A" };
            world.Pool<WA>().AddListener(a);
            var src = world.CreateEntity(new WA { V = 1 });
            world.Add(src, new WB { V = 2 });
            int wordsBefore = world._componentMasks.Length;
            Assert.That(wordsBefore, Is.LessThanOrEqualTo(Word(_late)), "test setup: WLate's word already exists");
            bool once = false;
            a.OnAdd = idx => {
                if (!once) {
                    once = true;
                    world.Add(src, new WLate { V = 5 });
                }
            };

            var copy = world.CopyEntity(src);

            Assert.That(world._componentMasks.Length, Is.GreaterThan(wordsBefore), "test setup: the mask words did not grow");
            Assert.That(world.Get<WB>(copy).V, Is.EqualTo(2));
            Assert.That(world.Has<WLate>(copy), Is.True, "a component added to the source in a later word is copied");
            Assert.That(world.Get<WLate>(copy).V, Is.EqualTo(5));
            WorldInvariants.AssertConsistent(world, "after the mask words grew mid-copy");
        }
        // ---- After an interrupted destroy: orphans

        private sealed class ThrowOnce<T> : IComponentListener<T> where T : struct, IComponent {
            public bool Armed;

            public void OnAdded(int entityIndex, ref T component) {
            }

            public void OnRemoved(int entityIndex, ref T component) {
                if (Armed) {
                    Armed = false;
                    throw new InvalidOperationException("listener failed");
                }
            }
        }

        private static bool Orphans(World world) {
            return (bool)typeof(World)
                .GetField("_orphans", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .GetValue(world);
        }

        // A listener throws inside DestroyEntity: WA and WB stay orphaned in their pools (documented). The
        // next entity takes the slot, and removing the WB orphan lowers its count to 0 although it has a
        // component: it is auto-destroyed, and the drain must still find that component (the count no
        // longer bounds the bits), in a low word and in a high one. Nothing passes to the entity after it.
        [Test]
        public void Destroy_AfterOrphanLoweredTheCount_StillRemovesEveryComponent([Values] bool highWord) {
            var world = new World();
            var thrower = new ThrowOnce<WA>();
            world.Pool<WA>().AddListener(thrower);
            var cFilter = world.Filter().Inc<WC>().End();
            var dFilter = world.Filter().Inc<WD>().End();
            var e1 = world.CreateEntity(new WA { V = 1 });
            world.Add(e1, new WB { V = 2 });
            Assert.That(Orphans(world), Is.False);

            thrower.Armed = true;
            Assert.That(() => world.DestroyEntity(e1), Throws.InvalidOperationException);
            Assert.That(world.Pool<WB>().Has(e1.Index), Is.True, "test setup: WB is not orphaned");
            Assert.That(Orphans(world), Is.True, "the released slot had bits set");

            var e2 = highWord ? world.CreateEntity(new WD { V = 4 }) : world.CreateEntity(new WC { V = 3 });
            Assert.That(e2.Index, Is.EqualTo(e1.Index), "test setup: the slot is reused");
            world.Pool<WB>().Remove(e2.Index);

            Assert.That(world.IsAlive(e2), Is.False, "the orphan's removal took the count to 0");
            Assert.That(world.Pool<WC>().Has(e2.Index), Is.False);
            Assert.That(world.Pool<WD>().Has(e2.Index), Is.False);
            Assert.That(cFilter.Count + dFilter.Count, Is.EqualTo(0));
            for (int w = 0; w < world._componentMasks.Length; w++) {
                Assert.That(world._componentMasks[w][e2.Index], Is.EqualTo(0UL), "mask word " + w);
            }

            var e3 = world.CreateEntity(new WB { V = 5 });
            Assert.That(e3.Index, Is.EqualTo(e1.Index), "test setup: the slot is reused again");
            Assert.That(world.Has<WC>(e3) || world.Has<WD>(e3), Is.False, "e3 inherited a component of e2");
            Assert.That(world.GetComponentCount(e3), Is.EqualTo(1));
            world.DestroyEntity(e3);
            Assert.That(world.Pool<WB>().Has(e1.Index), Is.False);

            world.Clear();
            Assert.That(Orphans(world), Is.False, "Clear empties the pools");
            WorldInvariants.AssertConsistent(world, "after Clear");
        }

        // A destroy interrupted after the last removal leaves nothing behind: no orphan is recorded.
        [Test]
        public void Destroy_ThrowAfterEveryRemoval_RecordsNoOrphan() {
            var world = new World();
            var a = new Log<WA>();
            world.Pool<WA>().AddListener(a);
            var e = world.CreateEntity(new WB());
            world.Add(e, new WA());
            world.AddEventListener(new DestroyedAction { Action = _ => throw new InvalidOperationException("listener failed") });

            Assert.That(() => world.DestroyEntity(e), Throws.InvalidOperationException);

            Assert.That(world.Pool<WA>().Has(e.Index) || world.Pool<WB>().Has(e.Index), Is.False,
                "the drain runs in a finally after the throwing OnEntityDestroyed");
            Assert.That(Orphans(world), Is.False);
            WorldInvariants.AssertConsistent(world, "after a destroy whose OnEntityDestroyed threw");
        }
    }
}
