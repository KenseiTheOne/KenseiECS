using System;
using System.Collections.Generic;
using System.Text;

namespace KenseiStand.FuzzCore {
    // Entity handle as the harness sees it on either side: slot index and generation.
    public readonly struct EH : IEquatable<EH> {
        public readonly int I;
        public readonly int G;

        public EH(int i, int g) {
            I = i;
            G = g;
        }

        public static readonly EH Null = default;
        public bool IsNull => I == 0 && G == 0;
        public bool Equals(EH o) => I == o.I && G == o.G;
        public override bool Equals(object obj) => obj is EH o && Equals(o);
        public override int GetHashCode() => I * 31 + G;
        public override string ToString() => "(" + I + ",g" + G + ")";
    }

    public enum Cb { Added, Removed, Enter, Leave, Created, Destroyed, CompAdded, CompRemoved }

    public sealed class CompSpec {
        public CT T;
        public int Id;
        public int P;
        public bool IsDefault;

        public static CompSpec Of(string t, int id, int p) => new CompSpec { T = TypeMap.Parse(t), Id = id, P = p };
        public static CompSpec Default(CT t) => new CompSpec { T = t, IsDefault = true };
        public override string ToString() => IsDefault ? "default(" + T + ")" : T + "#" + Id + ":" + P;
    }

    // Orders the contract leaves unspecified, taken from the real world and replayed by the model.
    public sealed class Hints {
        public List<int> Visits;                                  // filter iteration order
        public Dictionary<CT, int[]> Dense = new();               // dense order before RunFrame
    }

    public sealed class FirePlan {
        public readonly List<Rule> Rules = new();
        public readonly List<int> RuleIndices = new();
        public int WriteMarker = -1;
        public int Lid;
    }

    // Common part of the real world adapter and of the reference model: names, links, clipboard,
    // the reaction engine that runs scripted nested operations from listener callbacks, and the
    // event log both sides produce.
    public abstract class Side {
        public readonly Scenario Scn;
        public readonly bool Debug;
        // Event log of the current operation and, per line, the reaction it was logged in ("top" for the
        // operation itself, otherwise the kind of callback whose scripted reaction was running).
        public readonly List<string> Log = new();
        public readonly List<string> LogFrame = new();
        // Facts of the current operation (see Mechanism.cs).
        public readonly List<Fact> Facts = new();
        public readonly Dictionary<string, EH> Names = new();
        public readonly Dictionary<EH, EH> Links = new();
        public readonly object[] Clip = new object[4];
        public readonly CT[] ClipType = new CT[4];
        public readonly EH[] ClipSource = new EH[4];   // entity the clip was copied from
        public readonly Dictionary<string, int> PendingIds = new();
        // What the run met where the model does not decide by a promise: "Q-id: text" contract questions, and "D-id: text"
        // behaviour the documents of the tree describe (the driver reports those as documented, with the sentence it finds).
        public readonly SortedSet<string> Questions = new(StringComparer.Ordinal);
        public readonly SortedSet<string> Notes = new(StringComparer.Ordinal);
        public readonly List<Probe> Probes = new();
        public Hints Hints = new();
        public bool SuppressReactions;
        public bool DyingOpsAllowed = true;
        public int MaxDepth = 3;
        public bool Uncapped;
        public int HardDepth = 64;          // runaway guard (model in crash mode)
        public long HardFires = 50000;
        public bool Runaway;
        public bool EnforceHardCap;
        public long EventsTotal;

        protected int Depth;
        private readonly List<string> _frames = new();
        private readonly Dictionary<long, int> _fires = new();
        private long _firesThisOp;
        protected readonly Dictionary<int, ListenerDef> Defs = new();
        public string CurrentOp = "";

        protected Side(Scenario scn, bool debug) {
            Scn = scn;
            Debug = debug;
            foreach (var l in scn.Listeners) {
                Defs[l.Lid] = l;
            }
            Uncapped = scn.Uncapped;
        }

        public string Kind => this is ModelSide ? "model" : "real";

        public void BeginOp(string text) {
            CurrentOp = text;
            _firesThisOp = 0;
            MaxDepthThisOp = 0;
            Facts.Clear();
            OnBeginOp();
        }

        protected virtual void OnBeginOp() {
        }

        public string CurrentFrame => _frames.Count == 0 ? "top" : _frames[_frames.Count - 1];

        public void AddLog(string line) {
            Log.Add(line);
            LogFrame.Add(CurrentFrame);
        }

        public void ClearLog() {
            Log.Clear();
            LogFrame.Clear();
        }

        private long _factSeq;

        // decision: null means "a decision when it names a claim or a mirrored question".
        public void AddFact(string key, int slot, string claim = null, string mirror = null, int pos = -1, bool? decision = null) {
            Facts.Add(new Fact(key, slot, pos >= 0 ? pos : Log.Count, claim, mirror, this is RealSide, ++_factSeq,
                               decision ?? (claim != null || mirror != null)));
        }

        // A call with a dead or stale handle (KENSEI_DEBUG): what the handle points at before the call, and
        // the note that the call returned instead of throwing. Only the real world can accept such a call.
        public virtual string StaleState(EH h) => "?";

        public virtual void StaleAccepted(EH h, string state, int pos, string action) {
        }

        public static string FrameName(Cb k) {
            switch (k) {
                case Cb.Added: return "typed-added";
                case Cb.Removed: return "typed-removed";
                case Cb.Enter: return "filter-enter";
                case Cb.Leave: return "filter-leave";
                case Cb.Created: return "world-created";
                case Cb.Destroyed: return "world-destroyed";
                case Cb.CompAdded: return "world-compAdded";
                case Cb.CompRemoved: return "world-compRemoved";
            }
            return "?";
        }

        // Deepest reaction nesting and number of reactions in the current top-level operation.
        public int MaxDepthThisOp { get; private set; }
        public long FiresThisOp => _firesThisOp;

        // ------------------------------------------------------------------ primitives

        public abstract void Setup();
        public abstract EH CreateEntity(CompSpec c);
        public abstract void DestroyEntity(EH h);
        public abstract EH CopyEntity(EH h);
        public abstract bool IsAlive(EH h);
        public abstract bool IsDying(EH h);
        public abstract EH GetEntity(int idx);
        public abstract bool WorldHas(EH h, CT t);
        public abstract bool PoolHas(int idx, CT t);
        public abstract void Add(EH h, CompSpec c, int marker);
        public abstract void Remove(EH h, CT t);
        public abstract void PoolAdd(int idx, CompSpec c);
        public abstract void PoolRemove(int idx, CT t);
        public abstract void WriteGet(EH h, CT t, int marker);
        public abstract void Modify(int idx, CT t, int marker);
        public abstract void MarkChanged(int idx, CT t);
        public abstract void AddEvent(EH h, int v);
        public abstract void Subscribe(EH h, int fooId);
        public abstract void Unsubscribe(EH h, int fooId);
        public abstract void CopyClip(EH h, CT t, int slot);
        public abstract void ClipAddEvent(int slot, int v);
        public abstract void WriteBackAdd(EH h, int slot);
        public abstract void CbSetClip(string pending, EH h, int slot);
        public abstract void CbCreate(string pending, CompSpec c);
        public abstract void CbAdd(string pending, EH h, CompSpec c);
        public abstract void CbSet(string pending, EH h, CompSpec c);
        public abstract void CbRemove(string pending, EH h, CT t);
        public abstract void CbDestroy(string pending, EH h);
        public abstract void CbPlayback();
        public abstract void Clear();
        public abstract void Warmup();
        public abstract bool Reserve(int entities, int components);
        public abstract void CreateFilter(string name, CT[] inc, CT[] exc, CT[] any);
        // world.Group<T1, ...>() over 2 to 4 types: "new" (registered) or "existing" (the same types in the same order);
        // throws when a type is owned by another group or listed twice.
        public abstract string RegisterGroup(CT[] types);
        public abstract bool HasFilter(string name);
        public abstract void IterateFilter(string name, string[] plan);
        public abstract void RunFrame();
        public abstract bool Listen(int lid);
        public abstract bool Unlisten(int lid);
        public abstract List<string> Snapshot();

        // ------------------------------------------------------------------ helpers

        public bool Usable(EH h) => !h.IsNull && (IsAlive(h) || (DyingOpsAllowed && IsDying(h)));

        public static string FilterName(string f) => f;

        public static string Ch(Cb k) {
            switch (k) {
                case Cb.Added: return "+";
                case Cb.Removed: return "-";
                case Cb.Enter: return ">";
                case Cb.Leave: return "<";
                case Cb.Created: return "C";
                case Cb.Destroyed: return "D";
                case Cb.CompAdded: return "a";
                case Cb.CompRemoved: return "r";
            }
            return "?";
        }

        private static string RuleOn(Cb k) {
            switch (k) {
                case Cb.Added: return "added";
                case Cb.Removed: return "removed";
                case Cb.Enter: return "enter";
                case Cb.Leave: return "leave";
                case Cb.Created: return "created";
                case Cb.Destroyed: return "destroyed";
                case Cb.CompAdded: return "compAdded";
                case Cb.CompRemoved: return "compRemoved";
            }
            return "?";
        }

        // Logs the callback and decides which rules fire. The caller applies the plan's write
        // through the callback's ref (typed callbacks), then calls RunPlan.
        protected FirePlan PlanFire(int lid, Cb kind, int idx, string x, string digest, CT? compType) {
            EventsTotal++;
            AddLog(lid + "|" + Ch(kind) + "|" + idx + "|" + x + (digest != null ? "|" + digest : ""));
            if (SuppressReactions || !Defs.TryGetValue(lid, out var def)) {
                return null;
            }
            FirePlan plan = null;
            string on = RuleOn(kind);
            for (int r = 0; r < def.Rules.Count; r++) {
                var rule = def.Rules[r];
                if (rule.On != on) {
                    continue;
                }
                if (rule.T != null && compType.HasValue && rule.T != compType.Value.ToString()) {
                    continue;
                }
                long key = (long)lid * 1000 + r;
                _fires.TryGetValue(key, out int fired);
                if (Uncapped) {
                    if (EnforceHardCap && (Depth >= HardDepth || _firesThisOp >= HardFires)) {
                        Runaway = true;
                        continue;
                    }
                } else {
                    if (Depth >= MaxDepth || (rule.Max > 0 && fired >= rule.Max)) {
                        continue;
                    }
                }
                _fires[key] = fired + 1;
                _firesThisOp++;
                plan ??= new FirePlan { Lid = lid };
                plan.Rules.Add(rule);
                plan.RuleIndices.Add(r);
                if (plan.WriteMarker < 0 && rule.Do.Count > 0 && rule.Do[0].A == "write"
                    && (kind == Cb.Added || kind == Cb.Removed)) {
                    plan.WriteMarker = rule.Do[0].M;
                }
            }
            return plan;
        }

        protected void LogWrite(int lid, int marker) {
            AddLog(lid + "|w|" + marker);
        }

        protected void RunPlan(FirePlan plan, Cb kind, int idx) {
            if (plan == null) {
                return;
            }
            for (int r = 0; r < plan.Rules.Count; r++) {
                var rule = plan.Rules[r];
                Depth++;
                if (Depth > MaxDepthThisOp) {
                    MaxDepthThisOp = Depth;
                }
                _frames.Add(FrameName(kind));
                try {
                    EH last = EH.Null;
                    for (int s = 0; s < rule.Do.Count; s++) {
                        var step = rule.Do[s];
                        if (step.A == "write") {
                            continue;
                        }
                        string outcome;
                        try {
                            outcome = DoStep(step, plan.Lid, kind, idx, ref last);
                        } catch (Exception ex) {
                            outcome = "!" + ex.GetType().Name;
                        }
                        AddLog(plan.Lid + "#" + plan.RuleIndices[r] + "." + s + "|" + step.A + "|" + outcome);
                    }
                } finally {
                    _frames.RemoveAt(_frames.Count - 1);
                    Depth--;
                }
            }
        }

        public EH Resolve(string who, int idx, EH last) {
            switch (who) {
                case null:
                case "self":
                    return idx >= 0 ? GetEntity(idx) : EH.Null;
                case "partner": {
                    if (idx < 0) {
                        return EH.Null;
                    }
                    var self = GetEntity(idx);
                    return Links.TryGetValue(self, out var p) ? p : EH.Null;
                }
                case "last":
                    return last;
                default:
                    return Names.TryGetValue(who, out var h) ? h : EH.Null;
            }
        }

        private CompSpec SpecOf(Step s) => new CompSpec { T = TypeMap.Parse(s.T), Id = s.Id, P = s.P };

        // One nested operation. Returns a short outcome for the log; exceptions are caught by the caller.
        protected virtual string DoStep(Step s, int lid, Cb kind, int idx, ref EH last) {
            switch (s.A) {
                case "destroy": {
                    var h = Resolve(s.Who, idx, last);
                    if (!Usable(h)) return "skip";
                    DestroyEntity(h);
                    return "ok";
                }
                case "create": {
                    var h = CreateEntity(SpecOf(s));
                    last = h;
                    if (s.Name != null) {
                        Names[s.Name] = h;
                    }
                    return "ok" + h;
                }
                case "add": {
                    var h = Resolve(s.Who, idx, last);
                    if (!Usable(h)) return "skip";
                    var t = TypeMap.Parse(s.T);
                    if (s.G && WorldHas(h, t)) return "has";
                    Add(h, SpecOf(s), s.M);
                    return "ok";
                }
                case "remove": {
                    var h = Resolve(s.Who, idx, last);
                    if (!Usable(h)) return "skip";
                    var t = TypeMap.Parse(s.T);
                    if (s.G && !WorldHas(h, t)) return "absent";
                    Remove(h, t);
                    return "ok";
                }
                case "poolAdd": {
                    var h = Resolve(s.Who, idx, last);
                    if (!Usable(h)) return "skip";
                    var t = TypeMap.Parse(s.T);
                    if (PoolHas(h.I, t)) return "has";
                    PoolAdd(h.I, SpecOf(s));
                    return "ok";
                }
                case "poolRemove": {
                    var h = Resolve(s.Who, idx, last);
                    if (!Usable(h)) return "skip";
                    var t = TypeMap.Parse(s.T);
                    if (s.G && !PoolHas(h.I, t)) return "absent";
                    PoolRemove(h.I, t);
                    return "ok";
                }
                case "addEvent": {
                    var h = Resolve(s.Who, idx, last);
                    if (!Usable(h)) return "skip";
                    AddEvent(h, s.P);
                    return "ok";
                }
                case "staleAddEvent":
                case "staleAdd":
                case "staleRemove":
                case "staleHas": {
                    // KENSEI_DEBUG only: a dead or stale handle must throw. Live (or dying) targets are not stale.
                    if (!Debug) return "skip";
                    var h = Resolve(s.Who, idx, last);
                    if (h.IsNull) return "skip";
                    if (Usable(h)) return "live";
                    var t = s.T != null ? TypeMap.Parse(s.T) : CT.A;
                    int pos = Log.Count;
                    string state = StaleState(h);
                    string result = "ok";
                    switch (s.A) {
                        case "staleAddEvent": AddEvent(h, s.P); break;
                        case "staleAdd": Add(h, SpecOf(s), -1); break;
                        case "staleRemove": Remove(h, t); break;
                        case "staleHas": result = "ok:" + WorldHas(h, t); break;
                    }
                    StaleAccepted(h, state, pos, s.A);
                    return result;
                }
                case "clip": {
                    var h = Resolve(s.Who, idx, last);
                    if (!Usable(h)) return "skip";
                    var t = TypeMap.Parse(s.T);
                    if (!PoolHas(h.I, t)) return "absent";
                    CopyClip(h, t, s.Slot);
                    return "ok";
                }
                case "clipEvent": {
                    if (Clip[s.Slot] == null || ClipType[s.Slot] != CT.EB) return "empty";
                    ClipAddEvent(s.Slot, s.P);
                    return "ok";
                }
                case "writeBack": {
                    var h = Resolve(s.Who, idx, last);
                    if (!Usable(h)) return "skip";
                    if (Clip[s.Slot] == null) return "empty";
                    if (!ClipSource[s.Slot].Equals(h) && !CrossCopyRuns(s.Slot)) return CrossCopy();
                    if (PoolHas(h.I, ClipType[s.Slot])) return "has";
                    WriteBackAdd(h, s.Slot);
                    return "ok";
                }
                case "cbSetClip": {
                    var h = Resolve(s.Who, idx, last);
                    if (h.IsNull) return "skip";
                    if (Clip[s.Slot] == null) return "empty";
                    if (!ClipSource[s.Slot].Equals(h) && !CrossCopyRuns(s.Slot)) return CrossCopy();
                    CbSetClip(null, h, s.Slot);
                    return "ok";
                }
                case "cbDestroy": {
                    var h = Resolve(s.Who, idx, last);
                    if (h.IsNull) return "skip";
                    CbDestroy(null, h);
                    return "ok";
                }
                case "cbRemove": {
                    var h = Resolve(s.Who, idx, last);
                    if (h.IsNull) return "skip";
                    CbRemove(null, h, TypeMap.Parse(s.T));
                    return "ok";
                }
                case "cbAdd": {
                    var h = Resolve(s.Who, idx, last);
                    if (h.IsNull) return "skip";
                    CbAdd(null, h, SpecOf(s));
                    return "ok";
                }
                case "cbSet": {
                    var h = Resolve(s.Who, idx, last);
                    if (h.IsNull) return "skip";
                    CbSet(null, h, SpecOf(s));
                    return "ok";
                }
                case "unlisten":
                    return Unlisten(s.Lid == 0 ? lid : s.Lid) ? "ok" : "no";
                case "listen":
                    return Listen(s.Lid == 0 ? lid : s.Lid) ? "ok" : "no";
                case "copy": {
                    var h = Resolve(s.Who, idx, last);
                    if (h.IsNull || !IsAlive(h)) return "skip";
                    last = CopyEntity(h);
                    if (s.Name != null) {
                        Names[s.Name] = last;
                    }
                    return "ok" + last;
                }
                case "subscribe": {
                    var h = Resolve(s.Who, idx, last);
                    if (h.IsNull || !IsAlive(h)) return "skip";
                    Subscribe(h, s.Id);
                    return "ok";
                }
                case "unsubscribe": {
                    var h = Resolve(s.Who, idx, last);
                    if (h.IsNull || !IsAlive(h)) return "skip";
                    Unsubscribe(h, s.Id);
                    return "ok";
                }
                case "modify": {
                    var h = Resolve(s.Who, idx, last);
                    if (!Usable(h)) return "skip";
                    var t = TypeMap.Parse(s.T);
                    if (!PoolHas(h.I, t) || !TypeMap.HasPayload(t)) return "absent";
                    Modify(h.I, t, s.M);
                    return "ok";
                }
                case "writeGet": {
                    var h = Resolve(s.Who, idx, last);
                    if (!Usable(h)) return "skip";
                    var t = TypeMap.Parse(s.T);
                    if (!PoolHas(h.I, t) || !TypeMap.HasPayload(t)) return "absent";
                    WriteGet(h, t, s.M);
                    return "ok";
                }
                case "mark": {
                    var h = Resolve(s.Who, idx, last);
                    if (!Usable(h)) return "skip";
                    var t = TypeMap.Parse(s.T);
                    if (!PoolHas(h.I, t)) return "absent";
                    MarkChanged(h.I, t);
                    return "ok";
                }
                case "link": {
                    var a = Resolve(s.Who, idx, last);
                    var b = Resolve(s.Name, idx, last);
                    if (a.IsNull || b.IsNull) return "skip";
                    Links[a] = b;
                    Links[b] = a;
                    return "ok";
                }
            }
            return "unknown";
        }

        // A struct copy of a component written to another entity shares its reference fields (an
        // EventBuffer list) by plain C# value semantics; the docs promise nothing for it, so it is not run.
        // 2.2.2 documents it for EventBuffer (claim eventbuffer-copy-store): a value whose list another entity holds is stored
        // with an empty list of its own, or leaves the entity's buffer as it is; such a copy runs (CrossCopyRuns).
        public string CrossCopy() {
            Questions.Add("Q-cross-entity-copy: a copy of a component written to another entity shares its references (" +
                          (Contract.V222 ? "a Listeners list, a boxed object; 2.2.2 documents EventBuffer copies, which run" : "an EventBuffer or Listeners list") +
                          "): plain struct copy, only CopyEntity promises own copies - not run, the contract does not cover it");
            return "cross";
        }

        public bool CrossCopyRuns(int slot) => ClipType[slot] == CT.EB && Contract.V222 && Contract.Active("eventbuffer-copy-store");

        // ------------------------------------------------------------------ filter iteration actions

        // Changes allowed inside foreach over a filter (concepts/filters.md "Iteration contract"):
        // destroy the current entity, add or remove components on it, create new entities.
        protected string IterAction(string action, int idx) {
            var parts = action.Split(':');
            var cur = GetEntity(idx);
            try {
                switch (parts[0]) {
                    case "none":
                        return "ok";
                    case "destroy":
                        if (!IsAlive(cur)) return "dead";
                        DestroyEntity(cur);
                        return "ok";
                    case "remove": {
                        var t = TypeMap.Parse(parts[1]);
                        if (!IsAlive(cur) || !WorldHas(cur, t)) return "absent";
                        Remove(cur, t);
                        return "ok";
                    }
                    case "add": {
                        var t = TypeMap.Parse(parts[1]);
                        if (!IsAlive(cur) || WorldHas(cur, t)) return "has";
                        Add(cur, CompSpec.Of(parts[1], int.Parse(parts[2]), int.Parse(parts[3])), -1);
                        return "ok";
                    }
                    case "create":
                        return "ok" + CreateEntity(CompSpec.Of(parts[1], int.Parse(parts[2]), int.Parse(parts[3])));
                    case "write": {
                        var t = TypeMap.Parse(parts[1]);
                        if (!IsAlive(cur) || !PoolHas(idx, t) || !TypeMap.HasPayload(t)) return "absent";
                        WriteGet(cur, t, int.Parse(parts[2]));
                        return "ok";
                    }
                }
                return "unknown";
            } catch (Exception ex) {
                return "!" + ex.GetType().Name;
            }
        }

        // ------------------------------------------------------------------ digests

        public static string DigestPlain(CT t, int id, int p) => t + "#" + id + ":" + p;

        public static string DigestBoxed(CT t, int id, int p, Probe box) =>
            t + "#" + id + ":" + p + ":" + (box == null ? "nobox" : box.Digest);

        public static string DigestEvents(List<int> values) {
            if (values == null) {
                return "EB()";
            }
            var sb = new StringBuilder("EB[");
            for (int i = 0; i < values.Count; i++) {
                if (i > 0) sb.Append(',');
                sb.Append(values[i]);
            }
            return sb.Append(']').ToString();
        }

        public static string DigestFoos(IList<int> ids) {
            if (ids == null) {
                return "LS()";
            }
            var sb = new StringBuilder("LS[");
            for (int i = 0; i < ids.Count; i++) {
                if (i > 0) sb.Append(',');
                sb.Append(ids[i]);
            }
            return sb.Append(']').ToString();
        }

        public string ProbeTotals() {
            int resets = 0, copies = 0;
            foreach (var p in Probes) {
                resets += p.Resets;
                copies += p.Copies;
            }
            return "probes n=" + Probes.Count + " resets=" + resets + " copies=" + copies;
        }

        public static string Members(IEnumerable<int> set) {
            var list = new List<int>(set);
            list.Sort();
            return "[" + string.Join(",", list) + "]";
        }
    }
}
