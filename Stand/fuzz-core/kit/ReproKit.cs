// Stand/fuzz-core repro kit: the helpers every generated repro (repro-*.cs) compiles with.
//
// A repro declares its own component types (so its type indices and mask words do not depend on
// other repros in the same process), drives KenseiECS through the public API and uses this kit for
// what the public API does not show: whether a slot is being destroyed, the EventBuffer list pool,
// and the stand's log and snapshot formats, so that what a repro prints can be compared line by
// line with the report.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using KenseiECS;
using NUnit.Framework;

namespace KenseiStand.FuzzCore.ReproKit {
    // Shared counter object referenced by the R and K components of a repro: AutoReset and AutoCopy
    // leave their marks here.
    public sealed class Probe {
        public readonly int Id;
        public readonly int Gen;
        public int Resets;
        public int Copies;
        public readonly List<Probe> Registry;

        public Probe(int id, int gen, List<Probe> registry) {
            Id = id;
            Gen = gen;
            Registry = registry;
            registry?.Add(this);
        }

        public Probe Fork() => new Probe(Id, Gen + 1, Registry);
        public string Digest => "b" + Id + "." + Gen + "r" + Resets + "c" + Copies;
    }

    // Filler component type for mask-word padding: Fill<Fill<...<byte>>> are all distinct types.
    public struct Fill<T> : IComponent where T : struct { }

    public static class Layout {
        private static Type _inner = typeof(byte);

        // Registers the given component types in order (first touch of ComponentType<T>.Index). A null
        // entry starts a new 64-type mask word; the group before the first null is also kept inside one word.
        public static void Register(params Type[] types) {
            var groups = new List<List<Type>> { new List<Type>() };
            foreach (var t in types) {
                if (t == null) {
                    groups.Add(new List<Type>());
                } else {
                    groups[groups.Count - 1].Add(t);
                }
            }
            for (int g = 0; g < groups.Count; g++) {
                int count = ComponentType.Count;
                bool fits = (count % 64) + groups[g].Count <= 64;
                if (g > 0 || !fits) {
                    while (ComponentType.Count % 64 != 0) {
                        TouchFiller();
                    }
                }
                foreach (var t in groups[g]) {
                    Touch(t);
                }
            }
        }

        public static int IndexOf(Type t) => (int)typeof(ComponentType<>).MakeGenericType(t).GetField("Index").GetValue(null);

        private static void Touch(Type t) => IndexOf(t);

        private static void TouchFiller() {
            var t = typeof(Fill<>).MakeGenericType(_inner);
            Touch(t);
            _inner = t;
        }
    }

    // State of one repro run: the world and the stand's bookkeeping (names, links, clip slots, the
    // reaction caps) with the stand's log format ("lid|kind|entity|x|digest").
    public sealed class Ctx {
        public World W;
        public CommandBuffer Cb;
        public SystemsRunner Runner;
        public readonly List<string> Log = new();
        public readonly Dictionary<string, Entity> Names = new();
        public readonly Dictionary<Entity, Entity> Links = new();
        public readonly Dictionary<string, PendingEntity> Pending = new();
        public readonly List<Probe> Probes = new();
        public readonly object[] Clip = new object[4];
        public readonly Type[] ClipType = new Type[4];
        public readonly Entity[] ClipSource = new Entity[4];
        public readonly List<string> Violations = new();
        // Lists of the fresh EventBuffer values handed to pending CommandBuffer commands (the stand's bookkeeping).
        public readonly List<object> PayloadLists = new();
        // 2.2.2 documents the lists of pending EventBuffer values (EventBuffer.Add on a buffer without a list) as lists without a
        // recorded holder, whose ownership is not checked: the repro of a 2.2.2 tree does not check them either.
        public bool CheckPayloadLists = true;

        public T Note<T>(T value) {
            var t = typeof(T);
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(EventBuffer<>)) {
                var list = t.GetField("Values").GetValue(value);
                if (list != null) PayloadLists.Add(list);
            }
            return value;
        }
        public readonly Dictionary<string, Filter> Filters = new();
        public readonly Dictionary<Filter, string> FilterNames = new(ReferenceEqualityComparer.Instance);
        public Type[] Kinds;          // the repro's 10 component types, in the stand's order (A B C D Tag R K Ev EB LS)
        public string[] KindNames = { "A", "B", "C", "D", "Tag", "R", "K", "Ev", "EB", "LS" };
        public int Depth;
        public int MaxDepth = 3;
        public bool Uncapped;
        public int GuardDepth = 256;
        public bool Runaway;
        public bool Debug;
        public bool Suppress;         // reactions off (the stand's OneFrame cleanup and filter iteration)
        public string CurrentOp = "";
        private readonly Dictionary<string, int> _fires = new();

        private static readonly BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly FieldInfo AliveF = typeof(World).GetField("_alive", NP);
        private static readonly FieldInfo NextF = typeof(World).GetField("_nextIndex", NP);
        private static readonly FieldInfo FreeF = typeof(World).GetField("_freeIndices", NP);
        private static readonly FieldInfo FreeCountF = typeof(World).GetField("_freeCount", NP);
        private static readonly FieldInfo GenF = typeof(World).GetField("_generations", NP);
        private static readonly FieldInfo MasksF = typeof(World).GetField("_componentMasks", NP);

        public static string H(Entity e) => "(" + e.Index + ",g" + e.Generation + ")";

        // "alive", "dying" (DestroyEntity in progress) or "free".
        public string SlotState(int idx) {
            var alive = (bool[])AliveF.GetValue(W);
            int next = (int)NextF.GetValue(W);
            if (alive == null || idx < 0 || idx >= next) return "free";
            if (alive[idx]) return "alive";
            var free = (int[])FreeF.GetValue(W);
            int freeCount = (int)FreeCountF.GetValue(W);
            for (int i = 0; i < freeCount; i++) {
                if (free[i] == idx) return "free";
            }
            return "dying";
        }

        public bool IsDying(Entity h) {
            if (h.Equals(Entity.Null)) return false;
            var gens = (int[])GenF.GetValue(W);
            return SlotState(h.Index) == "dying" && h.Index < gens.Length && gens[h.Index] == h.Generation;
        }

        // The stand's "usable" target: alive, or the entity being destroyed (listeners may touch it).
        public bool Usable(Entity h) => !h.Equals(Entity.Null) && (W.IsAlive(h) || IsDying(h));

        public Entity Self(int idx) => idx >= 0 ? W.GetEntity(idx) : Entity.Null;
        public Entity Partner(int idx) => idx >= 0 && Links.TryGetValue(W.GetEntity(idx), out var p) ? p : Entity.Null;
        public Entity Named(string name) => Names.TryGetValue(name, out var h) ? h : Entity.Null;

        // Reaction caps of the stand: a rule fires at most `max` times per run and not deeper than MaxDepth
        // nested reactions; uncapped runs stop at GuardDepth and set Runaway.
        public long GuardFires = 200000;
        private long _uncappedFires;

        public bool Fire(int lid, int rule, int max) {
            if (Uncapped) {
                // The stand's runaway guard: too deep a recursion, or too many reactions (an endless loop
                // such as a drain that a listener refills on every pass).
                if (Depth >= GuardDepth || ++_uncappedFires > GuardFires) {
                    Runaway = true;
                    return false;
                }
                return true;
            }
            string key = lid + "." + rule;
            _fires.TryGetValue(key, out int fired);
            if (Depth >= MaxDepth || (max > 0 && fired >= max)) return false;
            _fires[key] = fired + 1;
            return true;
        }

        // Runs one scripted step and logs its outcome like the stand: "lid#rule.step|action|outcome".
        public void Step(string tag, string action, Func<string> body) {
            string outcome;
            try {
                outcome = body();
            } catch (Exception ex) {
                outcome = "!" + ex.GetType().Name;
            }
            Log.Add(tag + "|" + action + "|" + outcome);
        }

        // One top-level operation, like the stand: an exception is its outcome, not the end of the run.
        public string Do(string op, Action a) {
            CurrentOp = op;
            try {
                a();
                return "ok";
            } catch (Exception ex) {
                return "!" + ex.GetType().Name;
            }
        }

        public static string Outcome(Action a) {
            try {
                a();
                return "ok";
            } catch (Exception ex) {
                return "!" + ex.GetType().Name;
            }
        }

        public void Callback(string line, int idx, bool recorder, string kind, Filter filter = null) {
            if (recorder) {
                CheckCallback(kind, idx, filter);
            } else if (kind == ">" && SlotState(idx) == "dying") {
                // the stand checks this for every filter listener, not only the recorders
                Violations.Add("N-dying-enter|filter OnEntityAdded(" + filter + ", " + idx + ") for an entity that is being destroyed (" + CurrentOp + ")");
            }
            Log.Add(line);
        }

        // The stand's immediate checks, done by the recorders (first listener of every pool, filter and world).
        private void CheckCallback(string kind, int idx, Filter filter) {
            string state = SlotState(idx);
            switch (kind) {
                case "+":
                case "-":
                    if (state == "free") Violations.Add("R-cb-free-slot|typed " + (kind == "+" ? "OnAdded" : "OnRemoved") + "(" + idx + ") for a slot that holds no entity (" + CurrentOp + ")");
                    break;
                case ">":
                case "<":
                    if (kind == ">" && state == "dying") {
                        Violations.Add("N-dying-enter|filter OnEntityAdded(" + filter + ", " + idx + ") for an entity that is being destroyed (" + CurrentOp + ")");
                    }
                    if (state == "free") {
                        bool empty = Kinds.All(t => !PoolHas(t, idx));
                        if (!(kind == "<" && empty)) {   // a late leave on an empty slot is a contract question in the stand
                            Violations.Add("R-cb-free-slot|filter " + (kind == ">" ? "OnEntityAdded" : "OnEntityRemoved") + "(" + idx + ") for a slot that holds no entity (" + CurrentOp + ")");
                        }
                    } else if (state == "alive") {
                        bool m = Matches(filter, idx);
                        if (kind == ">" && !m) Violations.Add("R-filter-event|OnEntityAdded(" + filter + ", " + idx + ") but the entity does not match (" + CurrentOp + ")");
                        if (kind == "<" && m) Violations.Add("R-filter-event|OnEntityRemoved(" + filter + ", " + idx + ") but the entity still matches (" + CurrentOp + ")");
                    }
                    break;
                case "D":
                    if (state != "dying") Violations.Add("R-cb-destroyed|OnEntityDestroyed(" + idx + ") while the slot is " + state + " (" + CurrentOp + ")");
                    break;
            }
        }

        public bool PoolHas(Type t, int idx) {
            var pool = W.GetPool(Layout.IndexOf(t));
            return pool != null && pool.Has(idx);
        }

        public bool Matches(Filter f, int idx) {
            foreach (int ti in f.IncludedTypes) {
                var p = W.GetPool(ti);
                if (p == null || !p.Has(idx)) return false;
            }
            foreach (int ti in f.ExcludedTypes) {
                var p = W.GetPool(ti);
                if (p != null && p.Has(idx)) return false;
            }
            if (f.AnyTypes.Length == 0) return true;
            foreach (int ti in f.AnyTypes) {
                var p = W.GetPool(ti);
                if (p != null && p.Has(idx)) return true;
            }
            return false;
        }

        public string KindOf(int typeIndex) {
            for (int i = 0; i < Kinds.Length; i++) {
                if (Layout.IndexOf(Kinds[i]) == typeIndex) return KindNames[i];
            }
            return "t" + typeIndex;
        }

        // ------------------------------------------------------------------ digests (the stand's format)

        public static string Digest(object c) {
            var t = c.GetType();
            string name = t.Name;
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(EventBuffer<>)) {
                var values = (IList)t.GetField("Values").GetValue(c);
                if (values == null) return "EB()";
                var vs = new List<string>();
                foreach (var h in values) vs.Add(h.GetType().GetField("V").GetValue(h).ToString());
                return "EB[" + string.Join(",", vs) + "]";
            }
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Listeners<>)) {
                var values = (IList)t.GetField("Values").GetValue(c);
                if (values == null) return "LS()";
                var ids = new List<string>();
                foreach (var f in values) ids.Add(f.GetType().GetProperty("Id").GetValue(f).ToString());
                return "LS[" + string.Join(",", ids) + "]";
            }
            if (name == "Tag") return "Tag";
            int id = (int)t.GetField("Id").GetValue(c);
            int p = (int)t.GetField("P").GetValue(c);
            var box = t.GetField("Box");
            if (box != null) {
                var probe = (Probe)box.GetValue(c);
                return name + "#" + id + ":" + p + ":" + (probe == null ? "nobox" : probe.Digest);
            }
            return name + "#" + id + ":" + p;
        }

        private object GetRef(Type t, int idx) {
            var pool = typeof(World).GetMethod("Pool").MakeGenericMethod(t).Invoke(W, null);
            return pool.GetType().GetMethod("Get").Invoke(pool, new object[] { idx });
        }

        // The stand's snapshot lines: world counters, every alive entity with its components, pool
        // counts, filter members, change versions of D, AutoReset/AutoCopy totals.
        public List<string> Snapshot() {
            var lines = new List<string> { "tick=" + W.Tick + " count=" + W.EntityCount + " cv=" + W.ChangeVersion };
            foreach (var e in W.AliveEntities) {
                var parts = new List<string>();
                foreach (var t in Kinds) {
                    if (PoolHas(t, e.Index)) parts.Add(Digest(GetRef(t, e.Index)));
                }
                lines.Add("e" + e.Index + "g" + e.Generation + ": " + string.Join(" ", parts));
            }
            for (int i = 0; i < Kinds.Length; i++) {
                var pool = W.GetPool(Layout.IndexOf(Kinds[i]));
                lines.Add("pool " + KindNames[i] + " n=" + (pool?.Count ?? 0));
            }
            foreach (var name in Filters.Keys.OrderBy(x => x, StringComparer.Ordinal)) {
                var f = Filters[name];
                var m = f.Entities.ToArray().OrderBy(x => x);
                lines.Add("filter " + name + " [" + string.Join(",", m) + "] n=" + f.Count);
            }
            var dPool = typeof(World).GetMethod("Pool").MakeGenericMethod(Kinds[3]).Invoke(W, null);
            foreach (var e in W.AliveEntities) {
                if (PoolHas(Kinds[3], e.Index)) {
                    lines.Add("ver e" + e.Index + "=" + dPool.GetType().GetMethod("ChangedVersion").Invoke(dPool, new object[] { e.Index }));
                }
            }
            for (int g = 0; g < W.GroupCount; g++) {
                var grp = W.GetGroup(g);
                var names = new List<string>();
                for (int k = 0; k < grp.PoolCount; k++) {
                    int i = Array.IndexOf(Kinds, grp.GetPool(k).ComponentType);
                    names.Add(i >= 0 ? KindNames[i] : "?");
                }
                lines.Add("group " + g + " <" + string.Join(",", names) + "> n=" + grp.Count);
            }
            int resets = Probes.Sum(p => p.Resets), copies = Probes.Sum(p => p.Copies);
            lines.Add("probes n=" + Probes.Count + " resets=" + resets + " copies=" + copies);
            return lines;
        }

        // ------------------------------------------------------------------ the stand's invariants

        // Dead slots hold nothing, pool counts and component counts agree, filters equal their predicate,
        // and a list of an EventBuffer/Listeners has one owner and is not in the free pool at the same time.
        public List<string> Invariants() {
            var errs = new List<string>();
            var alive = new List<Entity>();
            foreach (var e in W.AliveEntities) alive.Add(e);
            var aliveSet = new HashSet<int>(alive.Select(e => e.Index));
            int next = (int)NextF.GetValue(W);
            for (int k = 0; k < Kinds.Length; k++) {
                var pool = W.GetPool(Layout.IndexOf(Kinds[k]));
                if (pool == null) continue;
                int aliveWith = 0;
                var masks = (ulong[][])MasksF.GetValue(W);
                int ti = Layout.IndexOf(Kinds[k]);
                var worldHas = typeof(World).GetMethod("Has").MakeGenericMethod(Kinds[k]);
                for (int idx = 0; idx < next; idx++) {
                    bool has = pool.Has(idx);
                    if (aliveSet.Contains(idx)) {
                        if (has) aliveWith++;
                        bool wh = (bool)worldHas.Invoke(W, new object[] { W.GetEntity(idx) });
                        if (wh != has) errs.Add("R-has-mismatch|entity " + idx + ": world.Has<" + KindNames[k] + ">=" + wh + " but pool.Has=" + has);
                    } else {
                        if (has) errs.Add("R-dead-slot|dead slot " + idx + " still holds " + KindNames[k]);
                        int wd = ti >> 6;
                        if (wd < masks.Length && idx < masks[wd].Length && (masks[wd][idx] & (1UL << (ti & 63))) != 0) {
                            errs.Add("R-dead-mask|dead slot " + idx + " still has the mask bit of " + KindNames[k]);
                        }
                    }
                }
                if (pool.Count != aliveWith) errs.Add("R-pool-count|pool " + KindNames[k] + " Count=" + pool.Count + " but " + aliveWith + " alive entities have it");
            }
            foreach (var e in alive) {
                int n = Kinds.Count(t => PoolHas(t, e.Index));
                if (n == 0) errs.Add("R-alive-empty|entity " + H(e) + " is alive with no components");
                if (W.GetComponentCount(e) != n) errs.Add("R-count|entity " + H(e) + ": GetComponentCount=" + W.GetComponentCount(e) + " but it has " + n);
            }
            foreach (var kv in FilterNames) {
                var f = kv.Key;
                var expected = new HashSet<int>(alive.Where(e => Matches(f, e.Index)).Select(e => e.Index));
                var actual = new HashSet<int>(f.Entities.ToArray());
                if (!actual.SetEquals(expected) || f.Count != expected.Count) {
                    errs.Add("R-filter|filter " + kv.Value + " (" + f + ") has [" + string.Join(",", actual.OrderBy(x => x)) + "], the predicate gives [" +
                             string.Join(",", expected.OrderBy(x => x)) + "]");
                }
            }
            // Every owning group: exact membership and aligned dense arrays.
            for (int gi = 0; gi < W.GroupCount; gi++) {
                var g = W.GetGroup(gi);
                var types = new List<Type>();
                for (int k = 0; k < g.PoolCount; k++) types.Add(g.GetPool(k).ComponentType);
                int expectedMembers = alive.Count(e => types.All(t => PoolHas(t, e.Index)));
                if (g.Count != expectedMembers) errs.Add("R-group|group " + gi + " Count=" + g.Count + " but " + expectedMembers + " entities have all its types");
                var ents = g.Entities.ToArray();
                for (int i = 0; i < ents.Length; i++) {
                    for (int k = 0; k < g.PoolCount; k++) {
                        if (g.GetPool(k).RawEntities[i] != ents[i]) errs.Add("R-group|group " + gi + " pool " + k + " dense[" + i + "]=" + g.GetPool(k).RawEntities[i] + " != member " + ents[i]);
                    }
                }
            }
            var owners = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
            foreach (int k in new[] { 8, 9 }) {
                foreach (var e in alive) {
                    if (!PoolHas(Kinds[k], e.Index)) continue;
                    var list = Kinds[k].GetField("Values").GetValue(GetRef(Kinds[k], e.Index));
                    if (list == null) continue;
                    if (owners.TryGetValue(list, out var other)) errs.Add("L-shared-list|" + KindNames[k] + " of " + H(e) + " and " + other + " hold the same List");
                    else owners[list] = KindNames[k] + " of " + H(e);
                }
            }
            var free = FreeLists(Kinds[8]);
            var freeSet = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (var l in free) {
                if (!freeSet.Add(l)) {
                    errs.Add("L-free-dup|the EventBuffer list pool holds the same List twice");
                    break;
                }
            }
            foreach (var kv in owners) {
                if (freeSet.Contains(kv.Key)) errs.Add("L-owned-and-free|" + kv.Value + " holds a List that is also in the free list pool");
            }
            foreach (var list in CheckPayloadLists ? PayloadLists : new List<object>()) {
                if (owners.TryGetValue(list, out var other)) errs.Add("L-shared-list|the EventBuffer of a pending CommandBuffer command and " + other + " hold the same List");
                else if (freeSet.Contains(list)) errs.Add("L-owned-and-free|the EventBuffer of a pending CommandBuffer command holds a List that is also in the free list pool");
            }
            return errs;
        }

        // Free lists of the static EventBuffer<T> list pool (KenseiECS.ListPool<T>._free) for an EventBuffer<T> type.
        public static List<object> FreeLists(Type eventBufferType) {
            var hit = eventBufferType.GetGenericArguments()[0];
            var poolType = typeof(World).Assembly.GetType("KenseiECS.ListPool`1")?.MakeGenericType(hit);
            var result = new List<object>();
            if (poolType?.GetField("_free", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) is IEnumerable e) {
                foreach (var x in e) result.Add(x);
            }
            return result;
        }

        // ------------------------------------------------------------------ assertions

        public static void AssertLog(List<string> expected, List<string> actual, string what) {
            int n = Math.Min(expected.Count, actual.Count);
            int k = -1;
            for (int i = 0; i < n; i++) {
                if (!SameLine(expected[i], actual[i])) {
                    k = i;
                    break;
                }
            }
            if (k < 0 && expected.Count == actual.Count) return;
            if (k < 0) k = n;
            var sb = new StringBuilder(what + "\nfirst difference at line " + k + ":\n  expected (contract): " +
                                       (k < expected.Count ? expected[k] : "(end)") + "\n  actual             : " + (k < actual.Count ? actual[k] : "(end)"));
            sb.Append("\nexpected:\n    ").Append(string.Join("\n    ", expected));
            sb.Append("\nactual:\n    ").Append(string.Join("\n    ", actual));
            Assert.Fail(sb.ToString());
        }

        // "EB(?)" in an expected line: an EventBuffer written back from a stale copy, whose content the contract does not fix.
        private static bool SameLine(string expected, string actual) {
            if (expected == actual) return true;
            if (!expected.EndsWith("|EB(?)", StringComparison.Ordinal)) return false;
            int cut = expected.LastIndexOf('|');
            return actual.StartsWith(expected.Substring(0, cut + 1), StringComparison.Ordinal);
        }

        public static void AssertLines(List<string> expectedLines, List<string> actual, string what) {
            var missing = expectedLines.Where(l => !actual.Contains(l)).ToList();
            if (missing.Count == 0) return;
            Assert.Fail(what + "\nexpected (contract):\n    " + string.Join("\n    ", missing) + "\nactual state:\n    " + string.Join("\n    ", actual));
        }

        public static void AssertNoRule(List<string> errs, string rule, string what) {
            var hits = errs.Where(x => x.StartsWith(rule + "|", StringComparison.Ordinal)).ToList();
            if (hits.Count == 0) return;
            Assert.Fail(what + "\n    " + string.Join("\n    ", hits));
        }
    }
}
