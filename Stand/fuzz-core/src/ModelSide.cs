using System;
using System.Collections.Generic;
using System.Linq;

namespace KenseiStand.FuzzCore {
    // Reference model of the KenseiECS core: an ECS on dictionaries that follows the documented
    // contract. Order of callbacks, slot reuse (LIFO free list, generation bumped on reuse),
    // the drain order of DestroyEntity and the per-list filter update order follow
    // docs/architecture.md, because the scripted reactions make every order observable.
    //
    // Where the implementation is expected to deviate from the contract, the model does the
    // contract-correct thing (each such point is marked CONTRACT below and records a fact naming the
    // documented promise, see Mechanism.cs). Where the contract is silent, the model mirrors the
    // implementation, records a contract question (QUESTION) and a mirror fact; Readings switches such a
    // point to its alternative reading, so a failure that disappears under it is a behaviour change in
    // contract-silent territory (status "regression"), not a defect.
    public sealed class ModelSide : Side {
        // Contract questions whose alternative reading this run uses (see the QUESTION points).
        public readonly HashSet<string> Readings = new(StringComparer.Ordinal);

        public sealed class MVal {
            public CT T;
            public int Id;
            public int P;
            public Probe Box;
            public List<int> Events;
            public List<int> Foos;
            // An EventBuffer written back from a stale copy: its list went back to the pool before, and what the
            // pool's next renters put into it is not part of the contract. Its content is not compared; who
            // owns the list is (L-* rules on the real world). Up to 2.2.1 only: 2.2.2 documents the outcome.
            public bool Tainted;
            // 2.2.2: the number of the rent under which this value holds its EventBuffer list (EventBuffer XML doc:
            // "Every rent of a pooled list is numbered and the renting buffer keeps the number"). A struct copy keeps it.
            public int Lease;

            // Struct-copy semantics: reference fields are shared with the original.
            public MVal Clone() => (MVal)MemberwiseClone();

            public string Digest() {
                switch (T) {
                    case CT.Tag: return "Tag";
                    case CT.R:
                    case CT.K: return DigestBoxed(T, Id, P, Box);
                    case CT.EB: return Tainted ? "EB(?)" : DigestEvents(Events);
                    case CT.LS: return DigestFoos(Foos);
                    default: return DigestPlain(T, Id, P);
                }
            }
        }

        private sealed class MInst {
            public long Uid;
            public MVal Val;
            public int Version;
            public int Slot;
            // Bumped whenever the component's EventBuffer list goes back to the list pool while the component stays
            // (an overwrite that returns the replaced list): a struct copy taken before shares a list that may belong
            // to another entity now, like a copy of a removed component.
            public int ListEpoch;
        }

        private sealed class MSlot {
            public int Gen = 1;
            public byte State;           // 0 free, 1 alive, 2 dying
            public readonly Dictionary<CT, MInst> Comps = new();
            public readonly HashSet<CT> Mask = new();   // mirrors the component mask (zeroed per word during a drain pass)
            public int DyingAdds;        // adds during the current drain pass (the implementation's component count of a dead entity)
            public int ReAdds;           // adds since the destruction began
            public int Edits;            // listener changes since the destruction began (re-adds and removals the drain did not do)
        }

        private sealed class MFilter {
            public string Name;
            public string Key;
            public CT[] Inc;
            public CT[] Exc;
            public CT[] Any;
            public readonly HashSet<int> Members = new();
            public readonly List<int> Listeners = new();
        }

        private sealed class TypeFilters {
            public readonly List<MFilter> Inc = new();
            public readonly List<MFilter> Exc = new();
            public readonly List<MFilter> Any = new();
        }

        private sealed class RemovalState {
            public MInst Inst;
            public int[] Snapshot;
            public int Next;
            public long Seq;        // start order of the removals in progress
            public int Slot;
            public CT T;
            public int Gen0;        // generation of the slot when the removal started
        }

        private sealed class CbCmd {
            public string Op;
            public string Pending;
            public EH Target;
            public CT T;
            public MVal Payload;
            public MInst SrcInst;     // cbSetClip: the component the clip was copied from
            public int SrcEpoch;      // cbSetClip: its ListEpoch when the clip was copied
            public EH Src;
        }

        private readonly List<MSlot> _slots = new();
        private int _next;
        private readonly List<int> _free = new();
        private long _uid;
        private int _changeVersion;
        private int _tick;
        private bool _suppressWorld;
        private readonly Dictionary<CT, List<int>> _typed = new();
        private readonly List<int> _world = new();
        private readonly List<MFilter> _filters = new();
        private readonly Dictionary<string, MFilter> _filterByName = new();
        private readonly Dictionary<CT, TypeFilters> _tables = new();
        private readonly Dictionary<MInst, RemovalState> _removing = new(ReferenceEqualityComparer.Instance);
        private readonly List<CbCmd> _cb = new();
        private readonly HashSet<int> _activeLids = new();

        // 2.2.2 (EventBuffer XML doc): every pooled list knows whether it is free, which entity holds it, and the number of
        // its current rent; storing a value checks them. The model does not reuse lists: a list that went back to the pool
        // stays free here, which decides every store the same way as "rented again under another number" would.
        private sealed class ListInfo {
            public bool Free;
            public int Holder = -1;
            public int Lease;
        }
        private readonly Dictionary<List<int>, ListInfo> _lists = new(ReferenceEqualityComparer.Instance);
        private int _leases;
        private int _maxWord;
        // Filter listener dispatches so far: an Add's filter update compares it before and after a filter, to know
        // whether user code ran there (2.2.2: only then can the component be gone, and the update stops).
        private long _filterCbRuns;
        // The documents of the tree describe the 2.2.2 contract (see Contract.V222 and the claims since 2.2.2).
        private readonly bool _v222 = Contract.V222;
        private bool _inWarmup;
        private int _warmupTemp = -1;
        private long _warmupNestedFires;
        private long _removalSeq;
        // Filter updates in progress (slot, type, removal or addition), innermost last.
        private readonly List<(int idx, CT t, bool remove)> _filterUpdates = new();
        // Slots freed during the current top-level operation (a reuse of one is a replacement).
        private readonly HashSet<int> _freedThisOp = new();

        // Contract violations the model reproduces deliberately because it must stay in step with the
        // implementation (anomalies); the executor reports one only when the real log matched.
        public readonly List<string> Anomalies = new();

        public ModelSide(Scenario scn, bool debug) : base(scn, debug) {
            foreach (var t in TypeMap.All) {
                _typed[t] = new List<int>();
                int w = TypeMap.Of(t) >> 6;
                if (w > _maxWord) {
                    _maxWord = w;
                }
            }
        }

        private static bool Tracked(CT t) => t == CT.D;
        private static bool HasAutoReset(CT t) => t == CT.R || t == CT.K || t == CT.EB || t == CT.LS;
        private static bool HasAutoCopy(CT t) => t == CT.K || t == CT.EB || t == CT.LS;

        private bool Hooked(CT t) => Tracked(t) || _typed[t].Count > 0 || _owner.ContainsKey(t);

        // Owning groups: their types in registration order, and which group owns a type.
        private readonly List<CT[]> _groups = new();
        private readonly Dictionary<CT, int> _owner = new();

        protected override void OnBeginOp() {
            _freedThisOp.Clear();
        }

        // ------------------------------------------------------------------ setup

        public override void Setup() {
            if (Scn.RecWorld) {
                _world.Add(Recorders.World);
                _activeLids.Add(Recorders.World);
            }
            if (Scn.RecTyped) {
                foreach (var t in TypeMap.All) {
                    _typed[t].Add(Recorders.Typed(t));
                    _activeLids.Add(Recorders.Typed(t));
                }
            }
            // TrackChanges on D's pool takes one change version.
            _changeVersion++;
            if (Scn.Group) {
                RegisterGroup(new[] { CT.A, CT.B });
            }
        }

        // world.Group<...>() (World XML docs, guides/groups.md, architecture.md RegisterGroup): the same types in the same order
        // return the existing group; otherwise a type another group owns, or one listed twice, throws, and nothing is claimed;
        // else the group owns every pool of its types. Its members are the alive entities that have all of them.
        public override string RegisterGroup(CT[] types) {
            foreach (var g in _groups) {
                if (g.SequenceEqual(types)) {
                    return "existing";
                }
            }
            for (int i = 0; i < types.Length; i++) {
                if (_owner.ContainsKey(types[i])) {
                    AddFact("group-rejected", -1, "group-registration");
                    throw new InvalidOperationException("Pool<" + types[i] + "> is already owned by another group");
                }
                for (int j = 0; j < i; j++) {
                    if (types[j] == types[i]) {
                        AddFact("group-rejected", -1, "group-registration");
                        throw new InvalidOperationException("Group lists " + types[i] + " twice");
                    }
                }
            }
            int index = _groups.Count;
            _groups.Add(types.ToArray());
            foreach (var t in types) {
                _owner[t] = index;
            }
            AddFact("group-new", -1, "group-registration");
            return "new";
        }

        private int GroupMembers(CT[] types) {
            int n = 0;
            for (int i = 0; i < _next; i++) {
                var s = _slots[i];
                if (s.State == 1 && types.All(t => s.Comps.ContainsKey(t))) {
                    n++;
                }
            }
            return n;
        }

        // ------------------------------------------------------------------ values

        private MVal Make(CompSpec c) {
            if (c.IsDefault) {
                return new MVal { T = c.T };
            }
            var v = new MVal { T = c.T, Id = c.Id, P = c.P };
            switch (c.T) {
                case CT.Tag:
                    v.Id = 0;
                    v.P = 0;
                    break;
                case CT.R:
                case CT.K:
                    v.Box = new Probe(c.Id, 0, Probes);
                    break;
                case CT.EB:
                    v.Id = 0;
                    v.P = 0;
                    Rent(v, -1).Add(c.P);
                    break;
                case CT.LS:
                    v.Id = 0;
                    v.P = 0;
                    v.Foos = new List<int> { c.Id };
                    break;
            }
            return v;
        }

        private void AutoReset(MVal v) {
            switch (v.T) {
                case CT.R:
                case CT.K:
                    if (v.Box != null) {
                        v.Box.Resets++;
                    }
                    v.Box = null;
                    v.P = 0;
                    break;
                case CT.EB:
                    // ListPool.Return clears the list; a copy holding it sees it empty. The model does
                    // not pool lists (CONTRACT: a pooled list has one owner).
                    if (v.Events != null) {
                        ReturnList(v.Events);
                        v.Events = null;
                    }
                    break;
                case CT.LS:
                    v.Foos?.Clear();
                    v.Foos = null;
                    break;
            }
        }

        private void AutoCopy(MVal v) {
            switch (v.T) {
                case CT.K:
                    if (v.Box != null) {
                        v.Box.Copies++;
                        v.Box = v.Box.Fork();
                    }
                    break;
                case CT.EB:
                    if (v.Events != null) {
                        var events = v.Events;
                        Rent(v, -1).AddRange(events);
                    }
                    break;
                case CT.LS:
                    if (v.Foos != null) {
                        v.Foos = new List<int>(v.Foos);
                    }
                    break;
            }
        }

        // A new list for an EventBuffer value, rented under a new number, held by an entity's component or by nobody yet.
        private List<int> Rent(MVal v, int holder) {
            var list = new List<int>();
            v.Events = list;
            v.Lease = ++_leases;
            _lists[list] = new ListInfo { Holder = holder, Lease = v.Lease };
            return list;
        }

        private void ReturnList(List<int> list) {
            list.Clear();
            if (_lists.TryGetValue(list, out var info)) {
                info.Free = true;
                info.Holder = -1;
            }
        }

        // 2.2.2: the list of an EventBuffer value went back to the pool or was rented again since the value took it.
        private bool ListStale(MVal v) =>
            v.T == CT.EB && v.Events != null && _lists.TryGetValue(v.Events, out var info) && (info.Free || info.Lease != v.Lease);

        // PooledList.TryAdopt: claim the list of a value stored for an entity. False for a list that is free, rented again
        // since, or held by another entity: the value is a stale copy, or a copy of another entity's buffer.
        private bool TryAdopt(MVal v, int idx) {
            if (!_lists.TryGetValue(v.Events, out var info)) {
                return true;
            }
            if (info.Free || info.Lease != v.Lease) {
                return false;
            }
            if (info.Holder < 0) {
                info.Holder = idx;
                return true;
            }
            return info.Holder == idx;
        }

        // CONTRACT (2.2.2, EventBuffer XML doc): "a value whose list went back to the pool or belongs to another entity is
        // stored with an empty list of its own instead (an empty buffer, Values not null)".
        private void Adopt(MVal v, int idx) {
            if (!_v222 || v.T != CT.EB || v.Events == null || TryAdopt(v, idx)) {
                return;
            }
            AddFact(ListStale(v) ? "store-stale-copy" : "store-other-entity-list", idx, "eventbuffer-copy-store");
            Rent(v, idx);
        }

        // ------------------------------------------------------------------ slots

        private MSlot SlotAt(int idx) {
            while (_slots.Count <= idx) {
                _slots.Add(new MSlot());
            }
            return _slots[idx];
        }

        private EH Alloc() {
            int idx;
            bool reused = false;
            if (_free.Count > 0) {
                idx = _free[_free.Count - 1];
                _free.RemoveAt(_free.Count - 1);
                reused = _freedThisOp.Contains(idx);
                var s = _slots[idx];
                int g = s.Gen + 1;
                if (g == 0) {
                    g = 1;
                }
                s.Gen = g;
            } else {
                idx = _next;
                SlotAt(idx);
                _next = idx + 1;
            }
            var slot = _slots[idx];
            slot.State = 1;
            slot.DyingAdds = 0;
            slot.ReAdds = 0;
            slot.Edits = 0;
            if (reused) {
                // A slot freed in this operation got a new occupant: an operation still in progress on the
                // old entity must not reach it (the W3 situation).
                AddFact("slot-reused", idx);
            }
            return new EH(idx, slot.Gen);
        }

        public override bool IsAlive(EH h) =>
            h.I >= 0 && h.I < _next && h.I < _slots.Count && _slots[h.I].State == 1 && _slots[h.I].Gen == h.G;

        public override bool IsDying(EH h) =>
            h.I >= 0 && h.I < _slots.Count && _slots[h.I].State == 2 && _slots[h.I].Gen == h.G;

        public override EH GetEntity(int idx) => new EH(idx, idx < _slots.Count ? _slots[idx].Gen : 1);

        private bool Present(int idx, CT t, MInst inst) =>
            idx < _slots.Count && _slots[idx].Comps.TryGetValue(t, out var cur) && ReferenceEquals(cur, inst);

        // KENSEI_DEBUG handle validation. CONTRACT: a handle is accepted when its own entity is alive or
        // being destroyed; a dead or stale handle throws (debug-mode.md, AddEvent XML doc).
        private void ValidateHandle(EH h, string op, CT t) {
            if (!Debug) {
                return;
            }
            if (h.I >= 0 && h.I < _next && h.I < _slots.Count) {
                var s = _slots[h.I];
                if (s.Gen == h.G && (s.State == 1 || s.State == 2)) {
                    return;
                }
            }
            throw new InvalidOperationException(op + "<" + t + "> on dead entity " + h);
        }

        // KENSEI_DEBUG pool int-API guard. CONTRACT: the slot must be alive or dying.
        private void ValidateSlot(int idx, CT t) {
            if (!Debug) {
                return;
            }
            if (idx >= 0 && idx < _next && idx < _slots.Count && (_slots[idx].State == 1 || _slots[idx].State == 2)) {
                return;
            }
            throw new InvalidOperationException("Add<" + t + "> on entity slot " + idx + " that is not alive");
        }

        // ------------------------------------------------------------------ callbacks

        private void TypedCb(int lid, Cb kind, int idx, CT t, MInst inst) {
            var plan = PlanFire(lid, kind, idx, t.ToString(), inst.Val.Digest(), t);
            if (plan != null && plan.WriteMarker >= 0 && TypeMap.HasPayload(t)) {
                inst.Val.P = plan.WriteMarker;
                LogWrite(lid, plan.WriteMarker);
            }
            if (_inWarmup && plan != null) {
                _warmupNestedFires++;
            }
            RunPlan(plan, kind, idx);
        }

        private void FilterCb(int lid, Cb kind, MFilter f, int idx) {
            var plan = PlanFire(lid, kind, idx, f.Name, null, null);
            if (_inWarmup && plan != null) {
                _warmupNestedFires++;
            }
            RunPlan(plan, kind, idx);
        }

        private void WorldCb(int lid, Cb kind, int idx, CT? t) {
            var plan = PlanFire(lid, kind, idx, t.HasValue ? t.Value.ToString() : "", null, t);
            RunPlan(plan, kind, idx);
        }

        // World events are suppressed during Warmup. QUESTION (Q-warmup-nested-world-events): for its whole run, also
        // for what typed and filter listeners do to other entities meanwhile; the docs mention only the temporary
        // entity. Alternative reading: only events about the temporary entity are suppressed.
        private bool WorldSuppressed(int idx) {
            if (!_suppressWorld) {
                return false;
            }
            if (!_inWarmup || idx == _warmupTemp) {
                return true;
            }
            if (_v222) {
                // CONTRACT (2.2.2, guides/world-lifecycle.md): "world event listeners do not see their creation: events are
                // suppressed for the whole call".
                AddFact("warmup-world-suppressed", idx, "warmup-events-suppressed");
                return true;
            }
            AddFact("warmup-world-suppressed", idx, null, "Q-warmup-nested-world-events");
            return !Readings.Contains("Q-warmup-nested-world-events");
        }

        private void DispatchWorld(Cb kind, int idx, CT? t) {
            if (_world.Count == 0 || WorldSuppressed(idx)) {
                return;
            }
            if ((kind == Cb.CompAdded || kind == Cb.CompRemoved) && idx < _slots.Count && _slots[idx].State == 0) {
                // QUESTION (Q-stale-world-event): a filter listener destroyed the entity during this change; the
                // implementation still reports it to world listeners. Alternative reading: no event for a freed slot.
                AddFact("stale-world-event", idx, null, "Q-stale-world-event");
                if (Readings.Contains("Q-stale-world-event")) {
                    return;
                }
            }
            var ls = _world.ToArray();
            foreach (int lid in ls) {
                WorldCb(lid, kind, idx, t);
            }
        }

        // ------------------------------------------------------------------ filters

        private bool Match(MFilter f, int idx) {
            var mask = _slots[idx].Mask;
            foreach (var t in f.Inc) {
                if (!mask.Contains(t)) {
                    return false;
                }
            }
            foreach (var t in f.Exc) {
                if (mask.Contains(t)) {
                    return false;
                }
            }
            if (f.Any.Length == 0) {
                return true;
            }
            foreach (var t in f.Any) {
                if (mask.Contains(t)) {
                    return true;
                }
            }
            return false;
        }

        private void AddMember(MFilter f, int idx, bool removePath) {
            var s = _slots[idx];
            bool edited = s.ReAdds > 0 || s.Edits > 0 || !removePath;
            if (s.State == 2 && edited && Readings.Contains("Q-dying-enter")) {
                // Alternative reading of Q-dying-enter: an entity being destroyed does not enter filters.
                return;
            }
            if (!f.Members.Add(idx)) {
                return;
            }
            if (s.State == 2 && f.Listeners.Count > 0) {
                if (_v222 && !removePath) {
                    // CONTRACT (2.2.2, IFilterListener XML doc, concepts/filters.md): "a component that a listener adds to an
                    // entity whose DestroyEntity is running lets the dying entity (IsAlive false) enter the filters it matches".
                    Questions.Add("D-dying-enter: a listener added a component to an entity whose DestroyEntity is running and the dying entity " +
                                   "entered the filters it matches (OnEntityAdded with IsAlive false)");
                    AddFact("dying-enter-readd", idx, "dying-readd-enters");
                } else if (edited) {
                    // QUESTION (Q-dying-enter): the model lets the dying entity enter, like the implementation.
                    Questions.Add("Q-dying-enter: a listener changed an entity being destroyed (re-added or removed a component) and it enters " +
                                  "a filter (OnEntityAdded while dying; IFilterListener says 'alive on enter', the world-events guide allows modifying the dying entity)");
                    AddFact("dying-enter-readd", idx, null, "Q-dying-enter");
                } else {
                    Anomalies.Add("N-dying-enter|filter " + f.Name + " got OnEntityAdded(" + idx + ") for an entity that is being destroyed " +
                                  "and that no listener changed: IFilterListener promises the entity is alive on enter");
                    AddFact("dying-enter", idx, "filter-listener");
                }
            }
            if (f.Listeners.Count == 0) {
                return;
            }
            _filterCbRuns++;
            var ls = f.Listeners.ToArray();
            foreach (int lid in ls) {
                FilterCb(lid, Cb.Enter, f, idx);
            }
        }

        private void RemoveMember(MFilter f, int idx) {
            if (!f.Members.Remove(idx)) {
                return;
            }
            if (f.Listeners.Count == 0) {
                return;
            }
            if (_slots[idx].State == 0) {
                // QUESTION (Q-stale-filter-event): a listener destroyed the entity during this structural change and the
                // remaining filters still report the leave for the freed slot. Alternative reading: no leave events then.
                AddFact("stale-filter-event", idx, null, "Q-stale-filter-event");
                if (Readings.Contains("Q-stale-filter-event")) {
                    return;
                }
            }
            _filterCbRuns++;
            var ls = f.Listeners.ToArray();
            foreach (int lid in ls) {
                FilterCb(lid, Cb.Leave, f, idx);
            }
        }

        // Returns true when the component was lost to a filter listener (2.2.2: a filter listener removed it again or
        // destroyed the entity during this update), so that no OnComponentAdded follows.
        private bool UpdateFiltersOnAdd(int idx, CT t) {
            if (!_tables.TryGetValue(t, out var tf)) {
                return false;
            }
            var s = _slots[idx];
            int gen0 = s.Gen;
            bool replacedNoted = false, removedNoted = false, ran = false;
            void Replaced() {
                if (!replacedNoted && s.Gen != gen0) {
                    // CONTRACT (entity handles): a listener destroyed the entity and a new one took the slot; the
                    // filter update of the old entity's Add does not act on the new one (membership follows its mask).
                    AddFact("filter-update-replaced", idx, "entity-handles");
                    replacedNoted = true;
                }
            }
            bool StillHas() => s.Gen == gen0 && s.Mask.Contains(t);
            // 2.2.2 (CHANGELOG Fixed, the mirror of the 2.1.1 fix for Remove): after a filter whose listeners ran, the
            // update stops when the entity no longer has T or its slot holds another entity; the nested Remove has
            // already updated the filters. The membership is the same either way, the stop only keeps the old entity's
            // update off the slot.
            bool Stop(long before) {
                if (_filterCbRuns == before) {
                    return false;
                }
                ran = true;
                return _v222 && !StillHas();
            }
            _filterUpdates.Add((idx, t, false));
            try {
                foreach (var f in tf.Inc.ToArray()) {
                    Replaced();
                    if (Match(f, idx)) {
                        long before = _filterCbRuns;
                        AddMember(f, idx, false);
                        if (Stop(before)) {
                            return true;
                        }
                    }
                }
                // CONTRACT (filter definition): an entity that matches the filter is in it. The entity leaves an
                // Exc<T> filter only while it really has T; a listener may have removed T again already.
                foreach (var f in tf.Exc.ToArray()) {
                    Replaced();
                    if (!removedNoted && s.Gen == gen0 && !s.Mask.Contains(t)) {
                        AddFact("removed-in-add-filter-update", idx, "filter-set");
                        removedNoted = true;
                    }
                    long before = _filterCbRuns;
                    if (Match(f, idx)) {
                        AddMember(f, idx, false);
                    } else {
                        RemoveMember(f, idx);
                    }
                    if (Stop(before)) {
                        return true;
                    }
                }
                foreach (var f in tf.Any.ToArray()) {
                    Replaced();
                    if (Match(f, idx)) {
                        long before = _filterCbRuns;
                        AddMember(f, idx, false);
                        if (_filterCbRuns != before) {
                            ran = true;
                        }
                    }
                }
            } finally {
                _filterUpdates.RemoveAt(_filterUpdates.Count - 1);
            }
            return _v222 && ran && !StillHas();
        }

        private void UpdateFiltersOnRemove(int idx, CT t) {
            if (!_tables.TryGetValue(t, out var tf)) {
                return;
            }
            var s = _slots[idx];
            var mask = s.Mask;
            int gen0 = s.Gen;
            bool replacedNoted = false;
            void Replaced() {
                if (!replacedNoted && s.Gen != gen0) {
                    AddFact("filter-update-replaced", idx, "entity-handles");
                    replacedNoted = true;
                }
            }
            _filterUpdates.Add((idx, t, true));
            try {
                var inc = tf.Inc.ToArray();
                for (int i = 0; i < inc.Length; i++) {
                    Replaced();
                    var f = inc[i];
                    bool notifies = f.Listeners.Count > 0;
                    RemoveMember(f, idx);
                    if (notifies && mask.Contains(t)) {
                        // CONTRACT (filter definition, CHANGELOG 2.1.1): a filter listener added the component back; that
                        // Add put the entity into every matching filter, so the remaining Inc filters keep it.
                        if (i + 1 < inc.Length) {
                            AddFact("filter-readd-stop", idx, "filter-set");
                        }
                        break;
                    }
                }
                if (_v222 && s.State == 2) {
                    // CONTRACT (2.2.2, CHANGELOG Fixed: "a dying entity leaves filters without entering any"): the
                    // removals of an entity being destroyed take it out of its Inc and Any filters (an Any filter at the
                    // first removal of one of its types) and put it into no Exc filter, although the components not
                    // removed yet are still in its mask.
                    bool noted = false;
                    foreach (var f in tf.Exc.ToArray()) {
                        Replaced();
                        if (!noted && !f.Members.Contains(idx) && Match(f, idx)) {
                            AddFact("dying-no-enter", idx, "dying-leaves-filters");
                            noted = true;
                        }
                    }
                    foreach (var f in tf.Any.ToArray()) {
                        Replaced();
                        if (!noted && f.Members.Contains(idx) && Match(f, idx)) {
                            AddFact("dying-leaves-any", idx, "dying-leaves-filters");
                            noted = true;
                        }
                        RemoveMember(f, idx);
                    }
                    return;
                }
                foreach (var f in tf.Exc.ToArray()) {
                    Replaced();
                    if (Match(f, idx)) {
                        AddMember(f, idx, true);
                    }
                }
                foreach (var f in tf.Any.ToArray()) {
                    Replaced();
                    if (Match(f, idx)) {
                        AddMember(f, idx, true);
                    } else {
                        RemoveMember(f, idx);
                    }
                }
            } finally {
                _filterUpdates.RemoveAt(_filterUpdates.Count - 1);
            }
        }

        // ------------------------------------------------------------------ add / remove

        // Pool-level Add (ComponentPool<T>.Add(int, T)); world.Add validates the handle first.
        private MInst PoolAddCore(int idx, MVal val, int marker) {
            var t = val.T;
            ValidateSlot(idx, t);
            var s = SlotAt(idx);
            if (s.Comps.ContainsKey(t)) {
                throw new InvalidOperationException("Entity " + idx + " already has component " + t);
            }
            // Where this Add happens: during the OnRemoved dispatch of the same type's component on this slot (W3c),
            // during the filter update of its removal, or on an entity being destroyed.
            foreach (var rs in _removing.Values) {
                if (rs.Slot == idx && rs.T == t && rs.Gen0 == s.Gen && !Present(idx, t, rs.Inst)) {
                    AddFact("readd-in-removed", idx, "pool-remove-reentrant", decision: false);
                    break;
                }
            }
            foreach (var fu in _filterUpdates) {
                if (fu.remove && fu.idx == idx && fu.t == t) {
                    AddFact("readd-in-filter-update", idx);
                    break;
                }
            }
            if (s.State == 2) {
                AddFact("dying-readd", idx);
            }
            int gen0 = s.Gen;
            bool hooked = Hooked(t);
            Adopt(val, idx);
            var inst = new MInst { Uid = ++_uid, Val = val, Slot = idx };
            s.Comps[t] = inst;
            if (Tracked(t)) {
                inst.Version = ++_changeVersion;
            }
            if (s.State == 2) {
                s.DyingAdds++;
                s.ReAdds++;
            }
            s.Mask.Add(t);
            if (UpdateFiltersOnAdd(idx, t)) {
                // CONTRACT (2.2.2, CHANGELOG Changed, world-events.md): a filter listener removed the component again
                // (or destroyed the entity); its OnComponentRemoved went out, no OnComponentAdded follows it.
                AddFact("add-lost-world", idx, "world-add-lost");
            } else {
                DispatchWorld(Cb.CompAdded, idx, t);
            }
            if (hooked) {
                var ls = _typed[t].ToArray();
                foreach (int lid in ls) {
                    // CONTRACT (2.1.1 changelog, ComponentPool.Add XML doc): once the component is gone, the
                    // remaining typed listeners get no OnAdded. "Gone" means this component; another one
                    // of the same type on the same slot (a re-add, or a new entity in the slot) is not it.
                    if (!Present(idx, t, inst)) {
                        // The slot holds a component of this type again: a new entity's (replaced) or the entity's own
                        // re-added one (readded); an implementation that looks the slot up goes on with it. Otherwise
                        // the component is simply gone.
                        if (!s.Comps.ContainsKey(t)) {
                            AddFact("add-gone", idx, "pool-add-gone");
                        } else if (s.Gen != gen0) {
                            AddFact("add-replaced", idx, "entity-handles");
                        } else {
                            AddFact("add-readded", idx, "pool-add-gone");
                        }
                        break;
                    }
                    TypedCb(lid, Cb.Added, idx, t, inst);
                }
            }
            if (marker >= 0 && TypeMap.HasPayload(t)) {
                // CONTRACT: the returned ref points at this component; if it was removed before Add
                // returned, writes go to a scratch value and are discarded.
                if (Present(idx, t, inst)) {
                    inst.Val.P = marker;
                } else if (!s.Comps.ContainsKey(t)) {
                    AddFact("ref-scratch", idx, "pool-add-gone");
                } else if (s.Gen != gen0) {
                    AddFact("ref-replaced", idx, "entity-handles");
                } else if (_v222) {
                    // CONTRACT (2.2.2, ComponentPool.Add XML doc): a component a listener removed and added to the same
                    // entity again is what the ref points at; writes through it land in that new component.
                    s.Comps[t].Val.P = marker;
                    AddFact("ref-readded", idx, "pool-add-readd-ref");
                } else {
                    AddFact("ref-readded", idx, "pool-add-gone");
                }
            }
            return inst;
        }

        // Pool-level Remove (ComponentPool<T>.Remove(int)). No-op when absent. fromDrain: DestroyEntity's own drain.
        private void PoolRemoveCore(int idx, CT t, bool fromDrain = false) {
            if (idx >= _slots.Count) {
                return;
            }
            var s = _slots[idx];
            if (!s.Comps.TryGetValue(t, out var inst)) {
                return;
            }
            if (!fromDrain && s.State == 2) {
                s.Edits++;
            }
            if (_removing.TryGetValue(inst, out var st)) {
                // CONTRACT (ComponentPool.Remove XML doc): a listener that removes the same component again
                // continues the notification from the next listener of the dispatch in progress (the array it
                // captured), so every listener gets exactly one OnRemoved.
                bool interleaved = false;
                foreach (var other in _removing.Values) {
                    if (!ReferenceEquals(other, st) && other.T == t && other.Seq > st.Seq) {
                        interleaved = true;
                        break;
                    }
                }
                // Interleaved: a removal of the same type on another entity started inside this one's dispatch.
                if (!SameListeners(st.Snapshot, _typed[t])) {
                    AddFact("reentrant-remove-listeners-changed", idx, "listener-snapshot", decision: false);
                }
                AddFact(interleaved ? "reentrant-remove-interleaved" : "reentrant-remove", idx, "pool-remove-reentrant");
                RunRemoveListeners(idx, t, st);
                if (!Present(idx, t, inst)) {
                    return;
                }
            } else if (Hooked(t) && _typed[t].Count > 0) {
                // A removal of this type on this slot is already in progress for another component: the entity
                // was replaced in the slot, or the component was re-added. CONTRACT: this removal is a removal of its
                // own component, with its own OnRemoved dispatch (the implementation keys its progress by slot).
                foreach (var other in _removing.Values) {
                    if (other.Slot == idx && other.T == t && !ReferenceEquals(other.Inst, inst)) {
                        if (other.Gen0 != s.Gen) {
                            AddFact("nested-removal-replaced", idx, "entity-handles");
                        } else {
                            AddFact("nested-removal-readded", idx, "pool-remove-reentrant");
                        }
                        break;
                    }
                }
                st = new RemovalState { Inst = inst, Snapshot = _typed[t].ToArray(), Next = 0, Seq = ++_removalSeq, Slot = idx, T = t, Gen0 = s.Gen };
                _removing[inst] = st;
                try {
                    RunRemoveListeners(idx, t, st);
                } finally {
                    _removing.Remove(inst);
                }
                // A nested Remove of this component finished the work.
                if (!Present(idx, t, inst)) {
                    NoteRemovalGone(idx, t, st, "remove");
                    return;
                }
            }
            PerformRemoval(idx, t, inst);
        }

        // The removal of st.Inst stopped because that component is gone: CONTRACT, the rest of this removal
        // does not act on what is on the slot now (a new entity in the slot, or a component re-added to it).
        private void NoteRemovalGone(int idx, CT t, RemovalState st, string stage) {
            var s = _slots[idx];
            if (s.Gen != st.Gen0) {
                if (s.Comps.ContainsKey(t)) {
                    AddFact(stage + "-replaced", idx, "entity-handles");
                }
            } else if (s.Comps.ContainsKey(t)) {
                AddFact(stage + "-readded", idx, "pool-remove-reentrant");
            }
        }

        private static bool SameListeners(int[] snapshot, List<int> now) {
            if (snapshot.Length != now.Count) {
                return false;
            }
            for (int i = 0; i < snapshot.Length; i++) {
                if (snapshot[i] != now[i]) {
                    return false;
                }
            }
            return true;
        }

        private void RunRemoveListeners(int idx, CT t, RemovalState st) {
            while (st.Next < st.Snapshot.Length) {
                int lid = st.Snapshot[st.Next++];
                TypedCb(lid, Cb.Removed, idx, t, st.Inst);
                if (!Present(idx, t, st.Inst)) {
                    // Also when every listener of the snapshot has had its call (a nested dispatch went on): the
                    // implementation's own loop may still go on with what is on the slot now.
                    NoteRemovalGone(idx, t, st, "dispatch");
                    return;
                }
            }
        }

        private void PerformRemoval(int idx, CT t, MInst inst) {
            var s = _slots[idx];
            int gen0 = s.Gen;
            AutoReset(inst.Val);
            s.Comps.Remove(t);
            s.Mask.Remove(t);
            UpdateFiltersOnRemove(idx, t);
            if (_v222 && (s.Gen != gen0 || s.Mask.Contains(t))) {
                // CONTRACT (2.2.2, CHANGELOG Changed, world-events.md): a filter listener added the component back (its
                // OnComponentAdded went out) or destroyed the entity and a new one took the slot (its events went out):
                // no OnComponentRemoved for the slot follows.
                AddFact("remove-kept-world", idx, "world-remove-kept");
            } else {
                DispatchWorld(Cb.CompRemoved, idx, t);
            }
            if (s.State == 1 && s.Comps.Count == 0) {
                DestroyInternal(idx);
            }
        }

        private void DestroyInternal(int idx) {
            var s = _slots[idx];
            s.State = 2;
            s.ReAdds = 0;
            s.Edits = 0;
            try {
                DispatchWorld(Cb.Destroyed, idx, null);
            } finally {
                try {
                    int passes = 0;
                    do {
                        if (Debug && ++passes > 1000) {
                            throw new InvalidOperationException("DestroyEntity(" + idx + ") cannot finish");
                        }
                        s.DyingAdds = 0;
                        for (int w = 0; w <= _maxWord; w++) {
                            var snap = new List<CT>();
                            foreach (var t in TypeMap.ByTypeIndex) {
                                if ((TypeMap.Of(t) >> 6) == w && s.Mask.Contains(t)) {
                                    snap.Add(t);
                                }
                            }
                            if (snap.Count == 0) {
                                continue;
                            }
                            if (!_v222) {
                                // Up to 2.2.1 the drain zeroes the mask word before it removes its components (world.Has
                                // is false for them meanwhile, question Q-has-during-drain). 2.2.2 (CHANGELOG Fixed,
                                // DestroyEntity XML doc): each removal clears its own bit, the others stay visible.
                                foreach (var t in snap) {
                                    s.Mask.Remove(t);
                                }
                            }
                            foreach (var t in snap) {
                                PoolRemoveCore(idx, t, fromDrain: true);
                            }
                        }
                    } while (s.DyingAdds != 0);
                } finally {
                    s.State = 0;
                    s.Mask.Clear();
                    s.DyingAdds = 0;
                    _free.Add(idx);
                    _freedThisOp.Add(idx);
                }
            }
        }

        private void DispatchCreated(EH h) {
            if (_world.Count == 0 || WorldSuppressed(h.I)) {
                return;
            }
            if (!IsAlive(h) && _v222) {
                // CONTRACT (2.2.2, CreateEntity / CopyEntity XML docs, world-events.md): an entity a listener destroyed
                // while it was being made gets no OnEntityCreated (and the slot's new entity, if any, got its own).
                AddFact("created-dead", h.I, "created-alive-only");
                return;
            }
            if (!IsAlive(h)) {
                // QUESTION: the entity died during its own creation (a listener destroyed it, and maybe a new entity
                // already took the slot and got its own OnEntityCreated). The docs do not say whether OnEntityCreated
                // still fires for the dead one; the implementation sends it for the slot index, and so does the model.
                // Two entities were created, two events are sent: that is not a duplicate notification.
                Questions.Add("Q-created-after-death: OnEntityCreated fires for an entity that a listener destroyed during its own " +
                              "CreateEntity/CopyEntity, also when another entity already took the slot (the contract does not say whether it should)");
                AddFact("created-after-death", h.I, null, "Q-created-after-death");
                if (Readings.Contains("Q-created-after-death")) {
                    return;
                }
            }
            DispatchWorld(Cb.Created, h.I, null);
        }

        // ------------------------------------------------------------------ public primitives

        public override EH CreateEntity(CompSpec c) => CreateEntityVal(Make(c));

        private EH CreateEntityVal(MVal v) {
            if (Debug && _slots == null) {
                throw new ObjectDisposedException("World");
            }
            var h = Alloc();
            PoolAddCore(h.I, v, -1);
            DispatchCreated(h);
            return h;
        }

        public override void DestroyEntity(EH h) {
            if (!IsAlive(h)) {
                return;
            }
            DestroyInternal(h.I);
        }

        public override EH CopyEntity(EH src) {
            if (!IsAlive(src)) {
                // Documented outcome (debug-mode.md): Entity.Null in release, a throw under KENSEI_DEBUG; nothing else.
                AddFact("copy-src-dead", src.I, "copy-dead", decision: false);
                if (Debug) {
                    throw new InvalidOperationException("CopyEntity on dead entity " + src);
                }
                return EH.Null;
            }
            if (_slots[src.I].Comps.Count == 0) {
                if (_v222) {
                    // CONTRACT (2.2.2, World.CopyEntity XML doc, CHANGELOG Fixed): "A source with no components left (inside the
                    // removal of its last component ...) is not copied: CopyEntity returns Entity.Null and creates nothing, in
                    // every build."
                    AddFact("copy-src-empty-null", src.I, "copy-empty-null");
                    return EH.Null;
                }
                // The source is alive without components: CopyEntity from inside the removal of its last component.
                AddFact("copy-src-empty", src.I, "alive-iff-component", decision: false);
            }
            bool srcChangedNoted = false;
            var dst = Alloc();
            for (int w = 0; w <= _maxWord; w++) {
                var ss = _slots[src.I];
                var snap = new List<CT>();
                foreach (var t in TypeMap.ByTypeIndex) {
                    if ((TypeMap.Of(t) >> 6) == w && ss.Mask.Contains(t)) {
                        snap.Add(t);
                    }
                }
                foreach (var t in snap) {
                    // CONTRACT: CopyEntity puts components on the copy only; once a listener destroyed the
                    // copy, no component goes to its freed slot (or to whoever took the slot).
                    if (!IsAlive(dst)) {
                        Notes.Add("model: CopyEntity stopped, the copy " + dst + " died during the copy");
                        AddFact("copy-dst-died", dst.I, "copy-entity");
                        goto done;
                    }
                    ss = _slots[src.I];
                    // CONTRACT: components are copied from the source entity, not from a new occupant of its slot.
                    if (ss.Gen != src.G || ss.State == 0) {
                        if (!srcChangedNoted) {
                            AddFact("copy-src-gone", src.I, "copy-entity");
                            srcChangedNoted = true;
                        }
                        continue;
                    }
                    if (!ss.Comps.TryGetValue(t, out var sinst)) {
                        continue;
                    }
                    var val = sinst.Val.Clone();
                    AutoCopy(val);
                    var ds = _slots[dst.I];
                    if (ds.Comps.TryGetValue(t, out var dinst)) {
                        AddFact("copy-overwrite", dst.I, "copy-overwrite");
                        if (Tracked(t)) {
                            dinst.Version = ++_changeVersion;
                        }
                        if (HasAutoCopy(t) && HasAutoReset(t)) {
                            if (t == CT.EB && dinst.Val.Events != null) {
                                ListReturned(dinst, "copy-overwrite", "copy-overwrite");
                            }
                            AutoReset(dinst.Val);
                        }
                        Adopt(val, dst.I);
                        dinst.Val = val;
                    } else {
                        PoolAddCore(dst.I, val, -1);
                    }
                }
            }
            done:
            DispatchCreated(dst);
            return dst;
        }

        public override bool WorldHas(EH h, CT t) {
            ValidateHandle(h, "Has", t);
            if (h.I >= _slots.Count) {
                return false;
            }
            var s = _slots[h.I];
            bool has = s.Mask.Contains(t);
            if (_v222 && s.State == 2 && has) {
                // CONTRACT (2.2.2, DestroyEntity XML doc): a listener of a removal sees the components not removed yet.
                AddFact("has-during-drain", h.I, "drain-has");
            }
            if (s.State == 2 && !has && s.Comps.ContainsKey(t)) {
                // QUESTION: the model answers from the mask, like the implementation. Alternative reading: from the pool.
                Questions.Add("Q-has-during-drain: world.Has<T> is false for a component of an entity being destroyed that is still " +
                              "in its pool (the drain zeroes the mask word before removing; components are 'still readable' per docs)");
                AddFact("has-during-drain", h.I, null, "Q-has-during-drain");
                if (Readings.Contains("Q-has-during-drain")) {
                    return true;
                }
            }
            return has;
        }

        public override bool PoolHas(int idx, CT t) => idx >= 0 && idx < _slots.Count && _slots[idx].Comps.ContainsKey(t);

        public override void Add(EH h, CompSpec c, int marker) {
            // The real call evaluates its argument (the component, with its Probe) before World.Add validates.
            var v = Make(c);
            ValidateHandle(h, "Add", c.T);
            PoolAddCore(h.I, v, marker);
        }

        public override void Remove(EH h, CT t) {
            ValidateHandle(h, "Remove", t);
            PoolRemoveCore(h.I, t);
        }

        public override void PoolAdd(int idx, CompSpec c) => PoolAddCore(idx, Make(c), -1);

        public override void PoolRemove(int idx, CT t) => PoolRemoveCore(idx, t);

        private MInst Existing(int idx, CT t) {
            if (idx < _slots.Count && _slots[idx].Comps.TryGetValue(t, out var inst)) {
                return inst;
            }
            if (Debug) {
                throw new InvalidOperationException("Get<" + t + "> on entity " + idx + " without this component");
            }
            throw new InvalidOperationException("model: release read of a missing component (undefined)");
        }

        public override void WriteGet(EH h, CT t, int marker) {
            ValidateHandle(h, "Get", t);
            Existing(h.I, t).Val.P = marker;
        }

        public override void Modify(int idx, CT t, int marker) {
            var inst = Existing(idx, t);
            if (Tracked(t)) {
                inst.Version = ++_changeVersion;
            }
            inst.Val.P = marker;
        }

        public override void MarkChanged(int idx, CT t) {
            var inst = Existing(idx, t);
            if (Tracked(t)) {
                inst.Version = ++_changeVersion;
            }
        }

        public override void AddEvent(EH h, int v) {
            ValidateHandle(h, "AddEvent", CT.EB);
            if (h.I < _slots.Count && _slots[h.I].Comps.TryGetValue(CT.EB, out var inst)) {
                if (inst.Val.Events == null) {
                    // world.AddEvent records the entity as the holder of a list it rents for a buffer without one.
                    Rent(inst.Val, h.I);
                }
                inst.Val.Events.Add(v);
                return;
            }
            var b = new MVal { T = CT.EB };
            Rent(b, -1).Add(v);
            ValidateHandle(h, "Add", CT.EB);
            PoolAddCore(h.I, b, -1);
        }

        public override void Subscribe(EH h, int fooId) {
            if (Debug && !IsAlive(h)) {
                throw new InvalidOperationException("Subscribe: entity " + h + " is not alive");
            }
            if (PoolHas(h.I, CT.LS)) {
                var v = _slots[h.I].Comps[CT.LS].Val;
                v.Foos ??= new List<int>();
                v.Foos.Add(fooId);
                return;
            }
            PoolAddCore(h.I, new MVal { T = CT.LS, Foos = new List<int> { fooId } }, -1);
        }

        public override void Unsubscribe(EH h, int fooId) {
            if (Debug && !IsAlive(h)) {
                throw new InvalidOperationException("Unsubscribe: entity " + h + " is not alive");
            }
            if (!PoolHas(h.I, CT.LS)) {
                return;
            }
            _slots[h.I].Comps[CT.LS].Val.Foos?.Remove(fooId);
        }

        private readonly MInst[] _clipInst = new MInst[4];
        private readonly int[] _clipEpoch = new int[4];

        public override void CopyClip(EH h, CT t, int slot) {
            ValidateHandle(h, "Get", t);
            var inst = Existing(h.I, t);
            Clip[slot] = inst.Val.Clone();
            ClipType[slot] = t;
            ClipSource[slot] = h;
            _clipInst[slot] = inst;
            _clipEpoch[slot] = inst.ListEpoch;
        }

        // The clip's component was removed since it was copied, or its list went back to the pool while the component
        // stayed (an overwrite returned it): the copy holds a list that may belong to another entity now.
        public bool ClipStale(int slot) {
            var inst = _clipInst[slot];
            if (Clip[slot] == null || inst == null) {
                return false;
            }
            if (_v222) {
                // 2.2.2: the list the copy holds went back to the pool (or was rented again since).
                return ListStale((MVal)Clip[slot]);
            }
            return InstStale(ClipSource[slot], inst, _clipEpoch[slot]);
        }

        // An overwrite of an EventBuffer that returns the replaced list to the pool: every copy taken before shares
        // that list and is stale from now on. CopyEntity's overwrite runs AutoReset on the old value (every version);
        // CommandBuffer.Set returns the replaced list when the new value brings a list of its own (2.2.2, CHANGELOG
        // Fixed: "it used to be dropped"; a dropped list stays with the copies and nobody else gets it).
        private void ListReturned(MInst inst, string why, string claim) {
            inst.ListEpoch++;
            AddFact("list-returned:" + why, inst.Slot, claim, decision: false);
        }

        public override void ClipAddEvent(int slot, int v) {
            var m = (MVal)Clip[slot];
            if (m.Events == null) {
                // EventBuffer.Add on a copy without a list rents one without a holder (the entity's buffer keeps none).
                Rent(m, -1);
            }
            m.Events.Add(v);
        }

        public override void WriteBackAdd(EH h, int slot) {
            var v = ((MVal)Clip[slot]).Clone();
            if (v.T == CT.EB && ClipStale(slot)) {
                // A copy of an EventBuffer written back after its component was removed (its list went back to the pool).
                // 2.2.2 documents the outcome (an empty list of its own, PoolAddCore); up to 2.2.1 its content is unknown.
                v.Tainted = !_v222;
                AddFact("writeback-stale", h.I, "eventbuffer-lists", decision: false);
            } else if (v.T == CT.EB && !ClipSource[slot].Equals(h)) {
                // 2.2.2: a copy of another entity's buffer stored for this one.
                AddFact("cross-copy-store", h.I, "eventbuffer-copy-store", decision: false);
            }
            ValidateHandle(h, "Add", v.T);
            PoolAddCore(h.I, v, -1);
        }

        // ------------------------------------------------------------------ command buffer

        public override void CbSetClip(string pending, EH h, int slot) {
            var v = ((MVal)Clip[slot]).Clone();
            _cb.Add(new CbCmd { Op = "set", Pending = pending, Target = h, T = v.T, Payload = v, SrcInst = _clipInst[slot], SrcEpoch = _clipEpoch[slot],
                                Src = ClipSource[slot] });
        }

        // EventBufferListOwner.Replace (2.2.2): the value about to replace the entity's buffer through CommandBuffer.Set.
        // A copy of the entity's own buffer (the same list) needs nothing; a stale copy, or one whose list another entity
        // holds, leaves the entity's buffer as it is (CommandBuffer.Set XML doc: "Set then stores an empty buffer, or keeps
        // the entity's current one, instead of a list that went back to the pool"); a value with a list of its own
        // returns the entity's list to the pool (CHANGELOG 2.2.2, Fixed), and so does a value without a list (QUESTION
        // Q-cb-set-listless-overwrite: the documents name only a buffer that brings its own list; the model mirrors it).
        private MVal Replace(MInst inst, MVal value, int idx) {
            var current = inst.Val;
            if (ReferenceEquals(value.Events, current.Events)) {
                return value.Lease != current.Lease ? current : value;
            }
            if (value.Events != null && !TryAdopt(value, idx)) {
                AddFact("cb-set-kept", idx, "eventbuffer-copy-store");
                return current;
            }
            if (current.Events != null) {
                if (value.Events == null) {
                    Questions.Add("Q-cb-set-listless-overwrite: CommandBuffer.Set with an EventBuffer without a list over a buffer that has one " +
                                  "returns that list to the pool (the documents name only a buffer that brings its own list)");
                    AddFact("cb-set-listless", idx, null, "Q-cb-set-listless-overwrite");
                    if (Readings.Contains("Q-cb-set-listless-overwrite")) {
                        // Alternative reading: the list is dropped, as up to 2.2.1 (held by nobody, not free).
                        if (_lists.TryGetValue(current.Events, out var dropped)) {
                            dropped.Holder = -1;
                        }
                        return value;
                    }
                    ListReturned(inst, "cb-set", null);
                } else {
                    ListReturned(inst, "cb-set", "cb-set-returns-list");
                }
                ReturnList(current.Events);
            }
            return value;
        }

        private bool InstStale(EH src, MInst inst, int epoch) =>
            inst != null && (!(src.I < _slots.Count && _slots[src.I].Gen == src.G && Present(src.I, inst.Val.T, inst)) || inst.ListEpoch != epoch);

        public override void CbCreate(string pending, CompSpec c) {
            PendingIds[pending] = PendingIds.Count;
            _cb.Add(new CbCmd { Op = "create", Pending = pending, T = c.T, Payload = Make(c) });
        }

        public override void CbAdd(string pending, EH h, CompSpec c) =>
            _cb.Add(new CbCmd { Op = "add", Pending = pending, Target = h, T = c.T, Payload = Make(c) });

        public override void CbSet(string pending, EH h, CompSpec c) =>
            _cb.Add(new CbCmd { Op = "set", Pending = pending, Target = h, T = c.T, Payload = Make(c) });

        public override void CbRemove(string pending, EH h, CT t) =>
            _cb.Add(new CbCmd { Op = "remove", Pending = pending, Target = h, T = t });

        public override void CbDestroy(string pending, EH h) =>
            _cb.Add(new CbCmd { Op = "destroy", Pending = pending, Target = h });

        public override void CbPlayback() {
            var resolved = new Dictionary<string, EH>();
            try {
                for (int i = 0; i < _cb.Count; i++) {
                    var cmd = _cb[i];
                    if (cmd.Op == "create") {
                        resolved[cmd.Pending] = CreateEntityVal(cmd.Payload.Clone());
                        continue;
                    }
                    EH target = cmd.Pending != null
                        ? (resolved.TryGetValue(cmd.Pending, out var r) ? r : EH.Null)
                        : cmd.Target;
                    if (!IsAlive(target)) {
                        // CONTRACT (CommandBuffer XML doc): a command whose entity is dead at Playback time is skipped.
                        AddFact("cb-skip-dead", target.I, "cb-dead-skipped");
                        continue;
                    }
                    switch (cmd.Op) {
                        case "add":
                            PoolAddCore(target.I, cmd.Payload.Clone(), -1);
                            break;
                        case "set": {
                            var payload = cmd.Payload.Clone();
                            bool stale = payload.T == CT.EB && (_v222 ? ListStale(payload) : InstStale(cmd.Src, cmd.SrcInst, cmd.SrcEpoch));
                            if (stale) {
                                payload.Tainted = !_v222;
                                AddFact("writeback-stale", target.I, "cb-set", decision: false);
                            } else if (_v222 && payload.T == CT.EB && cmd.Src.Equals(target) == false && cmd.SrcInst != null) {
                                AddFact("cross-copy-store", target.I, "eventbuffer-copy-store", decision: false);
                            }
                            if (PoolHas(target.I, cmd.T)) {
                                AddFact("cb-set-overwrite", target.I, "cb-set");
                                var inst = _slots[target.I].Comps[cmd.T];
                                if (Tracked(cmd.T)) {
                                    inst.Version = ++_changeVersion;
                                }
                                if (_v222 && cmd.T == CT.EB) {
                                    payload = Replace(inst, payload, target.I);
                                }
                                inst.Val = payload;
                            } else {
                                PoolAddCore(target.I, payload, -1);
                            }
                            break;
                        }
                        case "remove":
                            PoolRemoveCore(target.I, cmd.T);
                            break;
                        case "destroy":
                            DestroyEntity(target);
                            break;
                    }
                }
            } finally {
                _cb.Clear();
                PendingIds.Clear();
            }
        }

        // ------------------------------------------------------------------ world operations

        public override void Clear() {
            for (int i = 0; i < _next; i++) {
                var s = _slots[i];
                int g = s.Gen + 1;
                if (g == 0) {
                    g = 1;
                }
                s.Gen = g;
            }
            // Pools are cleared type by type, each AutoReset on its components; no listener sees it.
            foreach (var t in TypeMap.ByTypeIndex) {
                for (int i = 0; i < _slots.Count; i++) {
                    if (_slots[i].Comps.TryGetValue(t, out var inst)) {
                        AutoReset(inst.Val);
                    }
                }
            }
            for (int i = 0; i < _slots.Count; i++) {
                var s = _slots[i];
                s.Comps.Clear();
                s.Mask.Clear();
                s.State = 0;
                s.DyingAdds = 0;
                s.ReAdds = 0;
            }
            _free.Clear();
            _next = 0;
            _tick = 0;
            foreach (var f in _filters) {
                f.Members.Clear();
            }
        }

        // Log index at which the model's Warmup found its temporary entity destroyed by a listener (-1: not in this op).
        public int WarmupTempDiedAt = -1;

        public override void Warmup() {
            WarmupTempDiedAt = -1;
            _suppressWorld = true;
            _inWarmup = true;
            _warmupNestedFires = 0;
            EH temp = EH.Null;
            try {
                temp = Alloc();
                _warmupTemp = temp.I;
                foreach (var t in TypeMap.ByTypeIndex) {
                    if (_v222) {
                        // CONTRACT (2.2.2, World.Warmup XML doc): before each type the temporary entity is checked by its
                        // handle. Once a listener destroyed it, the remaining types go to a new temporary entity (never to
                        // the freed slot or to an entity the listener created in it); a type a listener already added to the
                        // temporary entity is skipped.
                        if (!IsAlive(temp)) {
                            Notes.Add("model: Warmup's temporary entity " + temp + " was destroyed by a listener; the remaining types go to a new one");
                            AddFact("warmup-temp-died", temp.I, "warmup-renew");
                            temp = Alloc();
                            _warmupTemp = temp.I;
                        } else if (SlotAt(temp.I).Comps.ContainsKey(t)) {
                            AddFact("warmup-skip-has", temp.I, "warmup-renew");
                            continue;
                        }
                        PoolAddCore(temp.I, new MVal { T = t }, -1);
                        continue;
                    }
                    // CONTRACT (World.Warmup XML doc): the temporary entity is created, gets a default of every
                    // type and is destroyed; existing entities are not touched. Once a listener destroyed it, the
                    // remaining types go nowhere: not to the freed slot, not to a new occupant of the slot.
                    if (!IsAlive(temp)) {
                        Notes.Add("model: Warmup's temporary entity " + temp + " was destroyed by a listener; the remaining types are not added");
                        AddFact("warmup-temp-died", temp.I, "warmup");
                        WarmupTempDiedAt = Log.Count;
                        break;
                    }
                    if (SlotAt(temp.I).Comps.ContainsKey(t)) {
                        // QUESTION (Q-warmup-already-has): a listener already added this type to the temporary entity (an
                        // OnAdded of an earlier type added a later one). The implementation's Add then throws "already has";
                        // the model does the same. Alternative reading: Warmup skips a type the entity already has.
                        Questions.Add("Q-warmup-already-has: World.Warmup throws InvalidOperationException (\"already has\") when a listener's " +
                                      "OnAdded added a component of a later pool to the temporary entity; the XML doc promises a default of " +
                                      "every type and does not cover a type the entity already has");
                        AddFact("warmup-already-has", temp.I, null, "Q-warmup-already-has");
                        if (Readings.Contains("Q-warmup-already-has")) {
                            continue;
                        }
                    }
                    PoolAddCore(temp.I, new MVal { T = t }, -1);
                }
            } finally {
                try {
                    DestroyEntity(temp);
                } finally {
                    _suppressWorld = false;
                    _inWarmup = false;
                    _warmupTemp = -1;
                }
            }
            if (_warmupNestedFires > 0 && _world.Count > 0) {
                if (_v222) {
                    Questions.Add("D-warmup-nested-world-events: world listeners do not see the world changes that typed and filter listeners " +
                                   "make during Warmup: events are suppressed for the whole call");
                } else {
                    Questions.Add("Q-warmup-nested-world-events: world listeners miss the world changes that typed/filter listeners make " +
                                  "during Warmup (events are suppressed for the whole Warmup, the docs mention only the temporary entity)");
                }
            }
        }

        public override bool Reserve(int entities, int components) => Compat.HasReserve;

        public override bool HasFilter(string name) => _filterByName.ContainsKey(name);

        public override void CreateFilter(string name, CT[] inc, CT[] exc, CT[] any) {
            var incL = inc.Distinct().ToList();
            var excL = exc.Distinct().ToList();
            var anyL = any.Distinct().ToList();
            if (incL.Count == 0 && anyL.Count == 0) {
                throw new InvalidOperationException("Filter requires at least one Inc<T> or Any<T> constraint");
            }
            foreach (var t in excL) {
                if (incL.Contains(t) || anyL.Contains(t)) {
                    throw new InvalidOperationException("Filter has the same component type in Exc and Inc/Any");
                }
            }
            anyL.RemoveAll(t => incL.Contains(t));
            Comparison<CT> byIndex = (x, y) => TypeMap.Of(x).CompareTo(TypeMap.Of(y));
            incL.Sort(byIndex);
            excL.Sort(byIndex);
            anyL.Sort(byIndex);
            string key = string.Join(",", incL) + "|" + string.Join(",", excL) + "|" + string.Join(",", anyL);
            foreach (var existing in _filters) {
                if (existing.Key == key) {
                    _filterByName[name] = existing;
                    return;
                }
            }
            var f = new MFilter { Name = name, Key = key, Inc = incL.ToArray(), Exc = excL.ToArray(), Any = anyL.ToArray() };
            _filters.Add(f);
            _filterByName[name] = f;
            foreach (var t in f.Inc) {
                TablesFor(t).Inc.Add(f);
            }
            foreach (var t in f.Exc) {
                TablesFor(t).Exc.Add(f);
            }
            foreach (var t in f.Any) {
                TablesFor(t).Any.Add(f);
            }
            for (int i = 0; i < _next; i++) {
                if (_slots[i].State == 1 && Match(f, i)) {
                    f.Members.Add(i);
                }
            }
            if (Scn.RecFilters) {
                int lid = Recorders.Filter(_filters.Count - 1);
                f.Listeners.Add(lid);
                _activeLids.Add(lid);
            }
        }

        private TypeFilters TablesFor(CT t) {
            if (!_tables.TryGetValue(t, out var tf)) {
                tf = new TypeFilters();
                _tables[t] = tf;
            }
            return tf;
        }

        public override void IterateFilter(string name, string[] plan) {
            var f = _filterByName[name];
            var start = new HashSet<int>(f.Members);
            List<int> visits = Hints.Visits;
            if (visits == null) {
                visits = start.OrderByDescending(x => x).ToList();
            }
            SuppressReactions = true;
            try {
                var seen = new HashSet<int>();
                int k = 0;
                foreach (int idx in visits) {
                    if (!start.Contains(idx) || !seen.Add(idx)) {
                        AddLog("visit|" + idx + "|unexpected");
                        continue;
                    }
                    AddLog("visit|" + idx);
                    string action = plan == null || plan.Length == 0 ? "none" : plan[k % plan.Length];
                    k++;
                    AddLog("it|" + action + "|" + IterAction(action, idx));
                }
                var missing = start.Where(x => !seen.Contains(x)).OrderBy(x => x).ToList();
                foreach (int idx in missing) {
                    AddLog("visit-missing|" + idx);
                }
            } finally {
                SuppressReactions = false;
            }
        }

        public override void RunFrame() {
            _tick++;
            SuppressReactions = true;
            try {
                foreach (var t in new[] { CT.Ev, CT.EB }) {
                    int[] order;
                    if (!Hints.Dense.TryGetValue(t, out order)) {
                        order = Enumerable.Range(0, _slots.Count).Where(i => _slots[i].Comps.ContainsKey(t)).ToArray();
                    }
                    for (int i = order.Length - 1; i >= 0; i--) {
                        PoolRemoveCore(order[i], t);
                    }
                }
            } finally {
                SuppressReactions = false;
            }
        }

        // ------------------------------------------------------------------ listeners

        public override bool Listen(int lid) {
            var def = Scn.FindListener(lid);
            if (def == null || _activeLids.Contains(lid)) {
                return false;
            }
            if (Depth > 0) {
                // A listener changes the listener set from inside a dispatch (listener-snapshot: the dispatch in progress goes
                // on over the array it captured, the new listener gets the next event).
                AddFact("listeners-changed:" + def.Kind, -1, "listener-snapshot", decision: false);
            }
            switch (def.Kind) {
                case "typed":
                    _typed[TypeMap.Parse(def.T)].Add(lid);
                    break;
                case "filter":
                    if (!_filterByName.TryGetValue(def.F, out var f)) {
                        return false;
                    }
                    f.Listeners.Add(lid);
                    break;
                case "world":
                    _world.Add(lid);
                    break;
                default:
                    return false;
            }
            _activeLids.Add(lid);
            return true;
        }

        public override bool Unlisten(int lid) {
            var def = Scn.FindListener(lid);
            if (def == null || !_activeLids.Contains(lid)) {
                return false;
            }
            if (Depth > 0) {
                AddFact("listeners-changed:" + def.Kind, -1, "listener-snapshot", decision: false);
            }
            switch (def.Kind) {
                case "typed":
                    _typed[TypeMap.Parse(def.T)].Remove(lid);
                    break;
                case "filter":
                    if (_filterByName.TryGetValue(def.F, out var f)) {
                        f.Listeners.Remove(lid);
                    }
                    break;
                case "world":
                    _world.Remove(lid);
                    break;
            }
            _activeLids.Remove(lid);
            return true;
        }

        // ------------------------------------------------------------------ snapshot

        public override List<string> Snapshot() {
            var lines = new List<string>();
            int alive = 0;
            for (int i = 0; i < _next; i++) {
                if (_slots[i].State == 1) {
                    alive++;
                }
            }
            lines.Add("tick=" + _tick + " count=" + alive + " cv=" + _changeVersion);
            for (int i = 0; i < _next; i++) {
                var s = _slots[i];
                if (s.State != 1) {
                    continue;
                }
                var parts = new List<string>();
                foreach (var t in TypeMap.All) {
                    if (s.Comps.TryGetValue(t, out var inst)) {
                        parts.Add(inst.Val.Digest());
                    }
                }
                lines.Add("e" + i + "g" + s.Gen + ": " + string.Join(" ", parts));
            }
            foreach (var t in TypeMap.All) {
                int n = 0;
                foreach (var s in _slots) {
                    if (s.Comps.ContainsKey(t)) {
                        n++;
                    }
                }
                lines.Add("pool " + t + " n=" + n);
            }
            foreach (var name in _filterByName.Keys.OrderBy(x => x, StringComparer.Ordinal)) {
                var f = _filterByName[name];
                lines.Add("filter " + name + " " + Members(f.Members) + " n=" + f.Members.Count);
            }
            for (int i = 0; i < _next; i++) {
                var s = _slots[i];
                if (s.State == 1 && s.Comps.TryGetValue(CT.D, out var inst)) {
                    lines.Add("ver e" + i + "=" + inst.Version);
                }
            }
            for (int g = 0; g < _groups.Count; g++) {
                lines.Add("group " + g + " <" + string.Join(",", _groups[g]) + "> n=" + GroupMembers(_groups[g]));
            }
            lines.Add(ProbeTotals());
            return lines;
        }

    }

    // Listener ids of the always-on recorders (they log, they never react).
    public static class Recorders {
        public const int World = 3000;
        public static int Typed(CT t) => 1000 + (int)t;
        public static int Filter(int ordinal) => 2000 + ordinal;
        public static bool IsRecorder(int lid) => lid >= 1000;
    }

    // Capabilities of the tree under test that differ between versions (looked up by reflection).
    public static class Compat {
        public static readonly System.Reflection.MethodInfo ReserveMethod =
            typeof(KenseiECS.World).GetMethod("Reserve", new[] { typeof(int), typeof(int) });

        public static bool HasReserve => ReserveMethod != null;
    }
}
