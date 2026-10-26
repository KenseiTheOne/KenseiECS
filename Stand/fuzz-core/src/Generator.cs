using System;
using System.Collections.Generic;
using System.Linq;

namespace KenseiStand.FuzzCore {
    // SplitMix64: deterministic across runtimes and platforms.
    public sealed class Rng {
        private ulong _s;

        public Rng(long seed) {
            _s = (ulong)seed ^ 0x9E3779B97F4A7C15UL;
        }

        public ulong Next() {
            ulong z = _s += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        public int Int(int n) => n <= 1 ? 0 : (int)(Next() % (ulong)n);
        public int Range(int lo, int hiInclusive) => lo + Int(hiInclusive - lo + 1);
        public bool P(double p) => (Next() >> 11) * (1.0 / (1UL << 53)) < p;
        public T Pick<T>(IList<T> list) => list[Int(list.Count)];

        public string Weighted(params (string key, double w)[] items) {
            double total = 0;
            foreach (var it in items) total += it.w;
            double x = (Next() >> 11) * (1.0 / (1UL << 53)) * total;
            foreach (var it in items) {
                if (x < it.w) return it.key;
                x -= it.w;
            }
            return items[items.Length - 1].key;
        }
    }

    public sealed class Generator {
        private readonly Rng _r;
        private readonly bool _debug;
        private readonly bool _crash;
        private readonly Scenario _s;
        private int _nextEntity = 1;
        private int _nextId = 1;
        private int _nextLid = 1;
        private int _nextFilter = 1;
        private int _nextPending = 1;
        private int _nextMarker = 100;
        private int _nextNamed = 1;
        private readonly List<string> _names = new();
        private readonly List<string> _filters = new();
        private readonly Dictionary<string, (string[] inc, string[] exc, string[] any)> _filterSpecs = new();
        private readonly List<string> _pendings = new();
        private readonly string[] _clipSrc = new string[2];   // entity the last top-level clip of a slot copied from

        private static readonly (string, double)[] KindWeights = {
            ("A", 4), ("B", 4), ("C", 3), ("D", 2), ("Tag", 3), ("R", 2), ("K", 2), ("Ev", 2), ("EB", 2), ("LS", 1),
        };

        // Motifs: small blocks built to reach one mechanism. The first ten aim at the known defect families
        // (Exclusions.MotifFamilies), the later ones at documented promises that hold today, so that a regression there
        // is found quickly: re-entrancy, owning groups (a rejected registration owns nothing), and the listener set that a
        // listener changes during a dispatch of any kind (world, filter, typed).
        public static readonly string[] MotifNames = {
            "warmup-destroy", "mutual-remove", "replace-in-slot", "exc-filter", "writeback", "stale-addevent",
            "copy-destroy", "unlisten-continue", "readd", "dying-readd", "iterate",
            "leave-readd", "warmup-companion", "remove-in-onadded", "reentrant-self",
            "group-conflict", "listeners-change",
        };

        private readonly HashSet<string> _exclude;

        private Generator(long seed, bool debug, bool crash, string layout, HashSet<string> exclude) {
            _r = new Rng(seed);
            _debug = debug;
            _crash = crash;
            _exclude = exclude ?? new HashSet<string>();
            _s = new Scenario { Seed = seed, Layout = layout, Uncapped = crash, Origin = "random" };
        }

        public static Scenario Make(long seed, int ops, bool debug, bool crash, string layout, string forceMotif = null, HashSet<string> exclude = null) {
            var g = new Generator(seed, debug, crash, layout, exclude);
            var scn = g.Build(ops, forceMotif);
            if (exclude != null && exclude.Count > 0) {
                Exclusions.Apply(scn, exclude);
                scn.Excluded = exclude.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            }
            return scn;
        }

        private string Kind() => _r.Weighted(KindWeights);
        private int Id() => _nextId++;
        private int Marker() => _nextMarker++;

        private string NewEntityName() => "e" + _nextEntity++;

        private string AnyName() {
            if (_names.Count == 0) return null;
            // Prefer recent entities: they are more likely alive.
            if (_r.P(0.6)) {
                int back = Math.Min(_names.Count, 4);
                return _names[_names.Count - 1 - _r.Int(back)];
            }
            return _r.Pick(_names);
        }

        private Op Create(string t, string name = null) {
            name ??= NewEntityName();
            _names.Add(name);
            return new Op { K = "create", Name = name, T = t, Id = Id(), P = _r.Int(10) };
        }

        private (string[] inc, string[] exc, string[] any) RandomFilterSpec() {
            for (int attempt = 0; attempt < 20; attempt++) {
                var inc = new List<string>();
                var exc = new List<string>();
                var any = new List<string>();
                int nInc = _r.Weighted(("0", 1), ("1", 5), ("2", 3)) == "0" ? 0 : (_r.P(0.6) ? 1 : 2);
                for (int i = 0; i < nInc; i++) inc.Add(Kind());
                if (_r.P(0.45)) exc.Add(Kind());
                if (nInc == 0 || _r.P(0.25)) {
                    any.Add(Kind());
                    any.Add(Kind());
                }
                inc = inc.Distinct().ToList();
                exc = exc.Distinct().ToList();
                any = any.Distinct().ToList();
                if (inc.Count == 0 && any.Count == 0) continue;
                if (exc.Any(x => inc.Contains(x) || any.Contains(x))) continue;
                return (inc.ToArray(), exc.ToArray(), any.ToArray());
            }
            return (new[] { "A" }, Array.Empty<string>(), Array.Empty<string>());
        }

        private Op NewFilter((string[] inc, string[] exc, string[] any) spec) {
            string name = "f" + _nextFilter++;
            _filters.Add(name);
            _filterSpecs[name] = spec;
            return new Op { K = "filter", F = name, Inc = spec.inc, Exc = spec.exc, Any = spec.any };
        }

        // ------------------------------------------------------------------ reactions

        private ListenerDef NewListener(string kind, string t = null, string f = null) {
            var def = new ListenerDef { Lid = _nextLid++, Kind = kind, T = t, F = f };
            _s.Listeners.Add(def);
            return def;
        }

        private int MaxFires() => _r.Weighted(("1", 55), ("2", 25), ("3", 20)) switch { "1" => 1, "2" => 2, _ => 3 };

        private Step RandomStep(string listenerKind, string listenerT, bool first) {
            string t = listenerT ?? Kind();
            var menu = new List<(string, double)> {
                ("destroy-self", 14), ("destroy-partner", 6), ("create", 10), ("add-self", 8), ("remove-self", 10),
                ("remove-partner", 6), ("poolRemove-partner", 2), ("add-partner", 3), ("addEvent-self", 3), ("addEvent-partner", 2),
                ("clip-self", 2), ("writeBack-self", 2), ("cbSetClip-self", 1), ("cbDestroy-self", 2), ("cbDestroy-partner", 1),
                ("cbRemove-self", 1), ("unlisten-self", 2), ("listen-other", 1), ("copy-self", 2), ("subscribe-self", 1),
                ("modify-self", 2), ("writeGet-self", 1), ("mark-self", 1),
            };
            if (_debug) {
                menu.Add(("staleAddEvent-partner", 5));
                menu.Add(("staleAdd-partner", 2));
                menu.Add(("staleRemove-partner", 1));
                menu.Add(("staleHas-partner", 1));
            }
            if (first && listenerKind == "typed" && TypeMap.HasPayload(TypeMap.Parse(listenerT))) {
                menu.Add(("write", 10));
            }
            string pick = _r.Weighted(menu.ToArray());
            string other = _r.P(0.5) ? t : Kind();
            switch (pick) {
                case "write": return new Step { A = "write", M = Marker() };
                case "destroy-self": return new Step { A = "destroy", Who = "self" };
                case "destroy-partner": return new Step { A = "destroy", Who = "partner" };
                case "create": {
                    var st = new Step { A = "create", T = _r.P(0.6) ? t : Kind(), Id = Id(), P = _r.Int(10) };
                    if (_r.P(0.3)) {
                        st.Name = "r" + _nextNamed++;
                        _names.Add(st.Name);
                    }
                    return st;
                }
                case "add-self": return new Step { A = "add", Who = "self", T = other, Id = Id(), P = _r.Int(10), G = _r.P(0.8) };
                case "remove-self": return new Step { A = "remove", Who = "self", T = _r.P(0.6) ? t : Kind(), G = _r.P(0.7) };
                case "remove-partner": return new Step { A = "remove", Who = "partner", T = _r.P(0.6) ? t : Kind(), G = _r.P(0.8) };
                case "poolRemove-partner": return new Step { A = "poolRemove", Who = "partner", T = t, G = _r.P(0.8) };
                case "add-partner": return new Step { A = "add", Who = "partner", T = other, Id = Id(), P = _r.Int(10), G = true };
                case "addEvent-self": return new Step { A = "addEvent", Who = "self", P = _r.Int(10) };
                case "addEvent-partner": return new Step { A = "addEvent", Who = "partner", P = _r.Int(10) };
                case "staleAddEvent-partner": return new Step { A = "staleAddEvent", Who = "partner", P = _r.Int(10) };
                case "staleAdd-partner": return new Step { A = "staleAdd", Who = "partner", T = Kind(), Id = Id(), P = _r.Int(10) };
                case "staleRemove-partner": return new Step { A = "staleRemove", Who = "partner", T = Kind() };
                case "staleHas-partner": return new Step { A = "staleHas", Who = "partner", T = Kind() };
                case "clip-self": return new Step { A = "clip", Who = "self", T = _r.P(0.7) ? "EB" : t, Slot = _r.Int(2) };
                case "writeBack-self": return new Step { A = "writeBack", Who = "self", Slot = _r.Int(2) };
                case "cbSetClip-self": return new Step { A = "cbSetClip", Who = "self", Slot = _r.Int(2) };
                case "cbDestroy-self": return new Step { A = "cbDestroy", Who = "self" };
                case "cbDestroy-partner": return new Step { A = "cbDestroy", Who = "partner" };
                case "cbRemove-self": return new Step { A = "cbRemove", Who = "self", T = t };
                case "unlisten-self": return new Step { A = "unlisten", Lid = 0 };
                case "listen-other": return new Step { A = "listen", Lid = 1 + _r.Int(Math.Max(1, _nextLid - 1)) };
                case "copy-self": return new Step { A = "copy", Who = "self" };
                case "subscribe-self": return new Step { A = "subscribe", Who = "self", Id = 1 + _r.Int(3) };
                case "modify-self": return new Step { A = "modify", Who = "self", T = t, M = Marker() };
                case "writeGet-self": return new Step { A = "writeGet", Who = "self", T = t, M = Marker() };
                case "mark-self": return new Step { A = "mark", Who = "self", T = "D" };
            }
            return new Step { A = "destroy", Who = "self" };
        }

        private Rule RandomRule(ListenerDef def) {
            string on;
            switch (def.Kind) {
                case "typed": on = _r.P(0.55) ? "removed" : "added"; break;
                case "filter": on = _r.P(0.5) ? "enter" : "leave"; break;
                default: on = _r.Weighted(("created", 2), ("destroyed", 3), ("compAdded", 2), ("compRemoved", 3)); break;
            }
            var rule = new Rule { On = on, Max = MaxFires() };
            if (def.Kind == "world" && (on == "compAdded" || on == "compRemoved") && _r.P(0.6)) {
                rule.T = Kind();
            }
            int n = _r.Weighted(("1", 5), ("2", 3), ("3", 2)) switch { "1" => 1, "2" => 2, _ => 3 };
            string lt = def.Kind == "typed" ? def.T : rule.T;
            for (int i = 0; i < n; i++) {
                var st = RandomStep(def.Kind, lt, i == 0);
                if (st.A == "write" && i > 0) continue;
                rule.Do.Add(st);
            }
            return rule;
        }

        private ListenerDef RandomListener() {
            string kind = _r.Weighted(("typed", 5), ("filter", _filters.Count > 0 ? 3 : 0), ("world", 2));
            ListenerDef def;
            if (kind == "typed") def = NewListener("typed", t: Kind());
            else if (kind == "filter") def = NewListener("filter", f: _r.Pick(_filters));
            else def = NewListener("world");
            int rules = _r.P(0.75) ? 1 : 2;
            for (int i = 0; i < rules; i++) def.Rules.Add(RandomRule(def));
            return def;
        }

        // ------------------------------------------------------------------ top-level ops

        private Op RandomOp() {
            var menu = new List<(string, double)> {
                ("create", 10), ("destroy", 6), ("add", 14), ("remove", 10), ("poolAdd", 2), ("poolRemove", 2),
                ("writeGet", 3), ("modify", 3), ("mark", 1), ("has", 1), ("addEvent", 5), ("subscribe", 2), ("unsubscribe", 1),
                ("copy", 3), ("clip", 2), ("clipEvent", 1), ("writeBack", 2), ("cbSetClip", 1), ("cbCreate", 2), ("cbAdd", 2),
                ("cbSet", 2), ("cbRemove", 2), ("cbDestroy", 2), ("cbPlay", 4), ("link", 2), ("filter", 1), ("listen", 2),
                ("unlisten", 1), ("warmup", 2), ("clear", 0.5), ("reserve", 0.5), ("frame", 4), ("iterate", _crash ? 0 : 3),
                ("group", 1),
            };
            if (_debug) {
                menu.Add(("stale", 3));
            }
            string k = _r.Weighted(menu.ToArray());
            string e = AnyName();
            switch (k) {
                case "create": return Create(Kind());
                case "destroy": return e == null ? Create(Kind()) : new Op { K = "destroy", E = e };
                case "add": return e == null ? Create(Kind()) : new Op { K = "add", E = e, T = Kind(), Id = Id(), P = _r.Int(10), M = _r.P(0.3) ? Marker() : -1 };
                case "remove": return e == null ? Create(Kind()) : new Op { K = "remove", E = e, T = Kind() };
                case "poolAdd": return e == null ? Create(Kind()) : new Op { K = "poolAdd", E = e, T = Kind(), Id = Id(), P = _r.Int(10) };
                case "poolRemove": return e == null ? Create(Kind()) : new Op { K = "poolRemove", E = e, T = Kind() };
                case "writeGet": return e == null ? Create(Kind()) : new Op { K = "writeGet", E = e, T = Kind(), M = Marker() };
                case "modify": return e == null ? Create(Kind()) : new Op { K = "modify", E = e, T = Kind(), M = Marker() };
                case "mark": return e == null ? Create(Kind()) : new Op { K = "mark", E = e, T = "D" };
                case "has": return e == null ? Create(Kind()) : new Op { K = "has", E = e, T = Kind() };
                case "addEvent": return e == null ? Create("EB") : new Op { K = "addEvent", E = e, P = _r.Int(10) };
                case "subscribe": return e == null ? Create("LS") : new Op { K = "subscribe", E = e, Id = 1 + _r.Int(4) };
                case "unsubscribe": return e == null ? Create("LS") : new Op { K = "unsubscribe", E = e, Id = 1 + _r.Int(4) };
                case "copy": {
                    if (e == null) return Create(Kind());
                    string name = NewEntityName();
                    _names.Add(name);
                    return new Op { K = "copy", E = e, Name = name };
                }
                case "clip": {
                    if (e == null) return Create("EB");
                    int slot = _r.Int(2);
                    _clipSrc[slot] = e;
                    return new Op { K = "clip", E = e, T = _r.P(0.7) ? "EB" : Kind(), Slot = slot };
                }
                case "clipEvent": return new Op { K = "clipEvent", Slot = _r.Int(2), P = _r.Int(10) };
                case "writeBack": {
                    // The documented pattern writes a copy back to its own entity; another target is a plain struct copy.
                    int slot = _r.Int(2);
                    string target = _clipSrc[slot] != null && _r.P(0.85) ? _clipSrc[slot] : e;
                    return target == null ? Create("EB") : new Op { K = "writeBack", E = target, Slot = slot };
                }
                case "cbSetClip": {
                    int slot = _r.Int(2);
                    string target = _clipSrc[slot] != null && _r.P(0.85) ? _clipSrc[slot] : e;
                    return target == null ? Create("EB") : new Op { K = "cbSetClip", E = target, Slot = slot };
                }
                case "cbCreate": {
                    string p = "p" + _nextPending++;
                    _pendings.Add(p);
                    return new Op { K = "cbCreate", Name = p, T = Kind(), Id = Id(), P = _r.Int(10) };
                }
                case "cbAdd": return e == null ? Create(Kind()) : new Op { K = "cbAdd", E = CbTarget(e), T = Kind(), Id = Id(), P = _r.Int(10) };
                case "cbSet": return e == null ? Create(Kind()) : new Op { K = "cbSet", E = CbTarget(e), T = Kind(), Id = Id(), P = _r.Int(10) };
                case "cbRemove": return e == null ? Create(Kind()) : new Op { K = "cbRemove", E = CbTarget(e), T = Kind() };
                case "cbDestroy": return e == null ? Create(Kind()) : new Op { K = "cbDestroy", E = CbTarget(e) };
                case "cbPlay": _pendings.Clear(); return new Op { K = "cbPlay" };
                case "link": {
                    string e2 = AnyName();
                    if (e == null || e2 == null || e == e2) return Create(Kind());
                    return new Op { K = "link", E = e, E2 = e2 };
                }
                case "filter": return NewFilter(RandomFilterSpec());
                case "group": return NewGroup();
                case "listen": {
                    if (_s.Listeners.Count == 0 || _r.P(0.4)) {
                        var def = RandomListener();
                        return new Op { K = "listen", Lid = def.Lid };
                    }
                    return new Op { K = "listen", Lid = _r.Pick(_s.Listeners).Lid };
                }
                case "unlisten":
                    if (_s.Listeners.Count == 0) return Create(Kind());
                    return new Op { K = "unlisten", Lid = _r.Pick(_s.Listeners).Lid };
                case "warmup": return new Op { K = "warmup" };
                case "clear": return new Op { K = "clear" };
                case "reserve": return new Op { K = "reserve", N1 = _r.Range(0, 300), N2 = _r.Range(0, 200) };
                case "frame": return new Op { K = "frame" };
                case "iterate": {
                    if (_filters.Count == 0) return NewFilter(RandomFilterSpec());
                    return new Op { K = "iterate", F = _r.Pick(_filters), Plan = IterPlan() };
                }
                case "stale": {
                    if (e == null) return Create(Kind());
                    string sk = _r.Weighted(("add", 3), ("remove", 2), ("addEvent", 3), ("has", 1));
                    var op = new Op { K = sk, E = e, Stale = true, T = Kind(), Id = Id(), P = _r.Int(10) };
                    return op;
                }
            }
            return Create(Kind());
        }

        // world.Group<...>() over 2 to 4 random types: a type that another group owns, or one drawn twice, makes it throw.
        private Op NewGroup() {
            int n = _r.Weighted(("2", 6), ("3", 3), ("4", 1)) switch { "2" => 2, "3" => 3, _ => 4 };
            var types = new string[n];
            for (int i = 0; i < n; i++) {
                types[i] = Kind();
            }
            return new Op { K = "group", Inc = types };
        }

        private string CbTarget(string e) {
            if (_pendings.Count > 0 && _r.P(0.35)) {
                return "p:" + _r.Pick(_pendings);
            }
            return e;
        }

        private string[] IterPlan() {
            int n = _r.Range(1, 3);
            var plan = new string[n];
            for (int i = 0; i < n; i++) {
                string a = _r.Weighted(("none", 2), ("destroy", 3), ("remove", 3), ("add", 3), ("create", 2), ("write", 1));
                switch (a) {
                    case "remove": plan[i] = "remove:" + Kind(); break;
                    case "add": plan[i] = "add:" + Kind() + ":" + Id() + ":" + _r.Int(10); break;
                    case "create": plan[i] = "create:" + Kind() + ":" + Id() + ":" + _r.Int(10); break;
                    case "write": plan[i] = "write:" + Kind() + ":" + Marker(); break;
                    default: plan[i] = a; break;
                }
            }
            return plan;
        }

        // ------------------------------------------------------------------ motifs

        private List<Op> Motif(string name) {
            var ops = new List<Op>();
            string t = _r.Pick(new[] { "A", "B", "C", "R", "K", "EB", "Tag", "D" });
            switch (name) {
                case "warmup-destroy": {
                    ListenerDef def;
                    if (_r.P(0.6)) {
                        def = NewListener("typed", t: t);
                        def.Rules.Add(new Rule { On = "added", Max = 1, Do = { new Step { A = "destroy", Who = "self" } } });
                    } else {
                        var f = NewFilter((new[] { t }, Array.Empty<string>(), Array.Empty<string>()));
                        ops.Add(f);
                        def = NewListener("filter", f: f.F);
                        def.Rules.Add(new Rule { On = "enter", Max = 1, Do = { new Step { A = "destroy", Who = "self" } } });
                    }
                    if (_r.P(0.3)) {
                        def.Rules[0].Do.Add(new Step { A = "create", T = Kind(), Id = Id(), P = _r.Int(10) });
                    }
                    ops.Add(new Op { K = "listen", Lid = def.Lid });
                    ops.Add(new Op { K = "warmup" });
                    ops.Add(Create(Kind()));
                    break;
                }
                case "mutual-remove": {
                    var def = NewListener("typed", t: t);
                    var step = _r.P(0.5)
                        ? new Step { A = "remove", Who = "partner", T = t, G = true }
                        : new Step { A = "destroy", Who = "partner" };
                    def.Rules.Add(new Rule { On = "removed", Max = _r.Pick(new[] { 1, 2, 3 }), Do = { step } });
                    var a = Create(_r.P(0.5) ? t : "Tag");
                    var b = Create(_r.P(0.5) ? t : "Tag");
                    ops.Add(a);
                    ops.Add(b);
                    if (a.T != t) ops.Add(new Op { K = "add", E = a.Name, T = t, Id = Id() });
                    if (b.T != t) ops.Add(new Op { K = "add", E = b.Name, T = t, Id = Id() });
                    ops.Add(new Op { K = "link", E = a.Name, E2 = b.Name });
                    ops.Add(new Op { K = "listen", Lid = def.Lid });
                    if (_r.P(0.5)) {
                        var extra = NewListener("typed", t: t);
                        extra.Rules.Add(new Rule { On = "added", Max = 1, Do = { new Step { A = "mark", Who = "self", T = "D" } } });
                        ops.Add(new Op { K = "listen", Lid = extra.Lid });
                    }
                    ops.Add(_r.P(0.6) ? new Op { K = "remove", E = a.Name, T = t } : new Op { K = "destroy", E = a.Name });
                    break;
                }
                case "replace-in-slot": {
                    var def = NewListener("typed", t: t);
                    bool onAdd = _r.P(0.4);
                    string named = "r" + _nextNamed++;
                    _names.Add(named);
                    def.Rules.Add(new Rule {
                        On = onAdd ? "added" : "removed", Max = 1,
                        Do = { new Step { A = "destroy", Who = "self" }, new Step { A = "create", T = _r.P(0.7) ? t : Kind(), Id = Id(), P = _r.Int(10), Name = named } }
                    });
                    if (_r.P(0.5)) {
                        var second = NewListener("typed", t: t);
                        second.Rules.Add(new Rule { On = onAdd ? "added" : "removed", Max = 2, Do = { new Step { A = "addEvent", Who = "self", P = 1 } } });
                        ops.Add(new Op { K = "listen", Lid = def.Lid });
                        ops.Add(new Op { K = "listen", Lid = second.Lid });
                    } else {
                        ops.Add(new Op { K = "listen", Lid = def.Lid });
                    }
                    var e = Create(onAdd || _r.P(0.5) ? "Tag" : t);
                    ops.Insert(0, e);
                    if (onAdd) {
                        ops.Add(new Op { K = "add", E = e.Name, T = t, Id = Id(), M = Marker() });
                    } else {
                        if (e.T != t) ops.Insert(1, new Op { K = "add", E = e.Name, T = t, Id = Id() });
                        ops.Add(_r.P(0.7) ? new Op { K = "remove", E = e.Name, T = t } : new Op { K = "destroy", E = e.Name });
                    }
                    ops.Add(new Op { K = "has", E = named, T = t });
                    break;
                }
                case "exc-filter": {
                    string x = _r.Pick(new[] { "A", "B", "C", "Tag", "R" });
                    string y = _r.Pick(new[] { "D", "K", "EB", "Ev" });
                    var f1 = NewFilter((new[] { x }, Array.Empty<string>(), Array.Empty<string>()));
                    var f2 = NewFilter((new[] { y }, new[] { x }, Array.Empty<string>()));
                    ops.Add(f1);
                    ops.Add(f2);
                    var def = NewListener("filter", f: f1.F);
                    def.Rules.Add(new Rule { On = "enter", Max = 1, Do = { new Step { A = "remove", Who = "self", T = x, G = true } } });
                    if (_r.P(0.5)) {
                        var l2 = NewListener("filter", f: f2.F);
                        l2.Rules.Add(new Rule { On = "leave", Max = 1, Do = { new Step { A = "addEvent", Who = "self", P = 2 } } });
                        ops.Add(new Op { K = "listen", Lid = l2.Lid });
                    }
                    var e = Create(y);
                    ops.Add(e);
                    if (_r.P(0.4)) ops.Add(new Op { K = "add", E = e.Name, T = "Tag", Id = Id() });
                    ops.Add(new Op { K = "listen", Lid = def.Lid });
                    ops.Add(new Op { K = "add", E = e.Name, T = x, Id = Id() });
                    break;
                }
                case "writeback": {
                    var e = _r.P(0.5) ? Create("EB") : Create("A");
                    ops.Add(e);
                    if (e.T != "EB" || _r.P(0.5)) ops.Add(new Op { K = "addEvent", E = e.Name, P = _r.Int(10) });
                    ops.Add(new Op { K = "clip", E = e.Name, T = "EB", Slot = 0 });
                    if (_r.P(0.4)) ops.Add(new Op { K = "clipEvent", Slot = 0, P = _r.Int(10) });
                    if (e.T == "EB" && _r.P(0.3)) ops.Add(new Op { K = "add", E = e.Name, T = "Tag", Id = Id() });
                    ops.Add(_r.P(0.5) ? new Op { K = "remove", E = e.Name, T = "EB" } : new Op { K = "frame" });
                    if (_r.P(0.5)) {
                        ops.Add(new Op { K = "writeBack", E = e.Name, Slot = 0 });
                    } else {
                        ops.Add(new Op { K = "cbSetClip", E = e.Name, Slot = 0 });
                        ops.Add(new Op { K = "cbPlay" });
                    }
                    var f = Create("A");
                    ops.Add(f);
                    ops.Add(new Op { K = "addEvent", E = f.Name, P = _r.Int(10) });
                    if (_r.P(0.5)) ops.Add(new Op { K = "addEvent", E = e.Name, P = _r.Int(10) });
                    break;
                }
                case "stale-addevent": {
                    var a = Create(_r.P(0.5) ? t : "A");
                    var b = Create("A");
                    ops.Add(a);
                    ops.Add(b);
                    ops.Add(new Op { K = "link", E = a.Name, E2 = b.Name });
                    ops.Add(new Op { K = "destroy", E = b.Name });
                    ListenerDef def;
                    string act = _r.Weighted(("staleAddEvent", 5), ("staleAdd", 2), ("staleRemove", 1));
                    var step = new Step { A = act, Who = "partner", T = Kind(), Id = Id(), P = _r.Int(10) };
                    if (_r.P(0.5)) {
                        def = NewListener("world");
                        def.Rules.Add(new Rule { On = "destroyed", Max = 1, Do = { step } });
                    } else {
                        def = NewListener("typed", t: a.T);
                        def.Rules.Add(new Rule { On = "removed", Max = 1, Do = { step } });
                    }
                    ops.Add(new Op { K = "listen", Lid = def.Lid });
                    ops.Add(new Op { K = "destroy", E = a.Name });
                    ops.Add(Create("A"));
                    ops.Add(Create("B"));
                    break;
                }
                case "copy-destroy": {
                    var e = Create(t);
                    ops.Add(e);
                    string u = Kind();
                    if (u != t) ops.Add(new Op { K = "add", E = e.Name, T = u, Id = Id() });
                    var def = NewListener("typed", t: _r.P(0.5) ? t : u);
                    def.Rules.Add(new Rule { On = "added", Max = 1, Do = { new Step { A = "destroy", Who = "self" } } });
                    if (_r.P(0.4)) def.Rules[0].Do.Add(new Step { A = "create", T = Kind(), Id = Id() });
                    ops.Add(new Op { K = "listen", Lid = def.Lid });
                    string copyName = NewEntityName();
                    _names.Add(copyName);
                    ops.Add(new Op { K = "copy", E = e.Name, Name = copyName });
                    ops.Add(Create(Kind()));
                    break;
                }
                case "unlisten-continue": {
                    var e = Create("Tag");
                    ops.Add(e);
                    ops.Add(new Op { K = "add", E = e.Name, T = t, Id = Id() });
                    var l0 = NewListener("typed", t: t);
                    l0.Rules.Add(new Rule { On = "removed", Max = 1, Do = { new Step { A = "unlisten", Lid = 0 }, new Step { A = "destroy", Who = "self" } } });
                    var l1 = NewListener("typed", t: t);
                    l1.Rules.Add(new Rule { On = "removed", Max = 1, Do = { new Step { A = "addEvent", Who = "self", P = 3 } } });
                    ops.Add(new Op { K = "listen", Lid = l0.Lid });
                    ops.Add(new Op { K = "listen", Lid = l1.Lid });
                    ops.Add(new Op { K = "remove", E = e.Name, T = t });
                    break;
                }
                case "readd": {
                    var e = Create("Tag");
                    ops.Add(e);
                    ops.Add(new Op { K = "add", E = e.Name, T = t, Id = Id() });
                    var l0 = NewListener("typed", t: t);
                    var rule = new Rule { On = "removed", Max = 1, Do = { new Step { A = "remove", Who = "self", T = t }, new Step { A = "add", Who = "self", T = t, Id = Id(), G = true } } };
                    if (_r.P(0.5)) rule.Do.Add(new Step { A = "remove", Who = "self", T = t });
                    l0.Rules.Add(rule);
                    ops.Add(new Op { K = "listen", Lid = l0.Lid });
                    if (_r.P(0.5)) {
                        var l1 = NewListener("typed", t: t);
                        l1.Rules.Add(new Rule { On = "removed", Max = 2, Do = { new Step { A = "addEvent", Who = "self", P = 4 } } });
                        ops.Add(new Op { K = "listen", Lid = l1.Lid });
                    }
                    ops.Add(new Op { K = "remove", E = e.Name, T = t });
                    break;
                }
                case "dying-readd": {
                    var f = NewFilter(RandomFilterSpec());
                    ops.Add(f);
                    var e = Create(t);
                    ops.Add(e);
                    ops.Add(new Op { K = "add", E = e.Name, T = Kind(), Id = Id() });
                    var def = NewListener("typed", t: t);
                    def.Rules.Add(new Rule { On = "removed", Max = 1, Do = { new Step { A = "add", Who = "self", T = Kind(), Id = Id(), G = true } } });
                    ops.Add(new Op { K = "listen", Lid = def.Lid });
                    ops.Add(new Op { K = "destroy", E = e.Name });
                    break;
                }
                case "iterate": {
                    var f = NewFilter(RandomFilterSpec());
                    ops.Add(f);
                    for (int i = 0; i < _r.Range(3, 7); i++) {
                        var e = Create(_r.Pick(f.Inc.Length > 0 ? f.Inc : f.Any));
                        ops.Add(e);
                        if (_r.P(0.5)) ops.Add(new Op { K = "add", E = e.Name, T = Kind(), Id = Id() });
                    }
                    if (!_crash) {
                        ops.Add(new Op { K = "iterate", F = f.F, Plan = IterPlan() });
                    }
                    break;
                }
                case "leave-readd": {
                    // IFilterListener.OnEntityRemoved adds the removed component back: the entity must stay in the
                    // remaining Inc filters of that type (CHANGELOG 2.1.1, filter definition).
                    string x = _r.Pick(new[] { "A", "B", "C", "D", "R", "K", "Tag", "Ev" });
                    string y = _r.Pick(new[] { "A", "B", "C", "D", "Tag", "LS" }.Where(k => k != x).ToArray());
                    var f1 = NewFilter((new[] { x }, Array.Empty<string>(), Array.Empty<string>()));
                    var f2 = NewFilter((new[] { x, y }, Array.Empty<string>(), Array.Empty<string>()));
                    ops.Add(f1);
                    ops.Add(f2);
                    var def = NewListener("filter", f: f1.F);
                    def.Rules.Add(new Rule { On = "leave", Max = _r.Pick(new[] { 1, 2 }), Do = { new Step { A = "add", Who = "self", T = x, Id = Id(), P = _r.Int(10), G = true } } });
                    if (_r.P(0.4)) {
                        var l2 = NewListener("filter", f: f2.F);
                        l2.Rules.Add(new Rule { On = "leave", Max = 1, Do = { new Step { A = "mark", Who = "self", T = "D" } } });
                        ops.Add(new Op { K = "listen", Lid = l2.Lid });
                    }
                    var e = Create(y);
                    ops.Add(e);
                    ops.Add(new Op { K = "add", E = e.Name, T = x, Id = Id() });
                    ops.Add(new Op { K = "listen", Lid = def.Lid });
                    string how = _r.Weighted(("remove", 6), ("poolRemove", 1), ("cb", 2), ("frame", x == "Ev" ? 3 : 0));
                    switch (how) {
                        case "poolRemove": ops.Add(new Op { K = "poolRemove", E = e.Name, T = x }); break;
                        case "cb": ops.Add(new Op { K = "cbRemove", E = e.Name, T = x }); ops.Add(new Op { K = "cbPlay" }); break;
                        case "frame": ops.Add(new Op { K = "frame" }); break;
                        default: ops.Add(new Op { K = "remove", E = e.Name, T = x }); break;
                    }
                    break;
                }
                case "warmup-companion": {
                    // An OnAdded of Warmup's temporary entity adds a component of a later pool, so Warmup meets a type the
                    // entity already has (contract question Q-warmup-already-has).
                    var order = TypeMap.ByTypeIndex;
                    int i0 = _r.Int(order.Length - 1);
                    string first = order[i0].ToString();
                    string later = order[i0 + 1 + _r.Int(order.Length - i0 - 1)].ToString();
                    var def = NewListener("typed", t: first);
                    def.Rules.Add(new Rule { On = "added", Max = 1, Do = { new Step { A = "add", Who = "self", T = later, Id = Id(), P = _r.Int(10), G = _r.P(0.5) } } });
                    ops.Add(new Op { K = "listen", Lid = def.Lid });
                    ops.Add(new Op { K = "warmup" });
                    ops.Add(Create(Kind()));
                    break;
                }
                case "remove-in-onadded": {
                    // A typed OnAdded removes the component again: the remaining OnAdded listeners get nothing and the ref
                    // that Add returns points at a scratch value (ComponentPool.Add XML doc, 2.1.1).
                    string tt = TypeMap.HasPayload(TypeMap.Parse(t)) ? t : "A";
                    var e = Create(tt == "B" ? "Tag" : _r.Pick(new[] { "Tag", "B", "LS" }));
                    ops.Add(e);
                    var l0 = NewListener("typed", t: tt);
                    l0.Rules.Add(new Rule { On = "added", Max = 1, Do = { new Step { A = "remove", Who = "self", T = tt, G = true } } });
                    var l1 = NewListener("typed", t: tt);
                    l1.Rules.Add(new Rule { On = "added", Max = 2, Do = { new Step { A = "write", M = Marker() } } });
                    if (_r.P(0.5)) {
                        ops.Add(new Op { K = "listen", Lid = l0.Lid });
                        ops.Add(new Op { K = "listen", Lid = l1.Lid });
                    } else {
                        ops.Add(new Op { K = "listen", Lid = l1.Lid });
                        ops.Add(new Op { K = "listen", Lid = l0.Lid });
                    }
                    ops.Add(new Op { K = "add", E = e.Name, T = tt, Id = Id(), M = Marker() });
                    break;
                }
                case "group-conflict": {
                    // A group over a type another group owns (or that lists a type twice) throws, and the rejected group
                    // owns nothing (architecture.md, RegisterGroup checks before it claims; guides/groups.md: a pool can be
                    // owned by one group): a later group over the freed type registers, and the existing group stays
                    // aligned while its members also get the freed type.
                    var kinds = new List<string> { "A", "B", "C", "D", "K", "Ev", "R" };
                    string a = _r.Pick(kinds);
                    kinds.Remove(a);
                    string b = _r.Pick(kinds);
                    kinds.Remove(b);
                    string c = _r.Pick(kinds);
                    kinds.Remove(c);
                    string d = _r.Pick(kinds);
                    ops.Add(new Op { K = "group", Inc = new[] { a, b } });
                    var e1 = Create(a);
                    var e2 = Create(a);
                    ops.Add(e1);
                    ops.Add(new Op { K = "add", E = e1.Name, T = b, Id = Id(), P = _r.Int(10) });
                    ops.Add(e2);
                    ops.Add(new Op { K = "add", E = e2.Name, T = b, Id = Id(), P = _r.Int(10) });
                    string how = _r.Weighted(("owned", 5), ("twice", 2), ("owned3", 2));
                    ops.Add(how == "owned" ? new Op { K = "group", Inc = _r.P(0.5) ? new[] { c, b } : new[] { b, c } }
                        : how == "twice" ? new Op { K = "group", Inc = new[] { c, c } }
                        : new Op { K = "group", Inc = new[] { c, d, a } });
                    ops.Add(new Op { K = "add", E = (_r.P(0.5) ? e2 : e1).Name, T = c, Id = Id(), P = _r.Int(10) });
                    if (_r.P(0.5)) {
                        ops.Add(new Op { K = "add", E = (_r.P(0.5) ? e1 : e2).Name, T = d, Id = Id(), P = _r.Int(10) });
                    }
                    ops.Add(new Op { K = "group", Inc = _r.P(0.5) ? new[] { c, d } : new[] { d, c } });
                    if (_r.P(0.5)) {
                        ops.Add(new Op { K = "remove", E = e1.Name, T = _r.P(0.5) ? b : c });
                    }
                    break;
                }
                case "listeners-change": {
                    // A listener changes the listener set during a dispatch: it unsubscribes itself or the next listener and
                    // subscribes another one. The dispatch goes on over the array it captured at its start (architecture.md,
                    // event ordering; world-events.md: "Listeners may add or remove listeners during dispatch; the dispatch
                    // continues over the set captured at its start"): the next listener still gets this event, the new one
                    // only the next. For world, filter and typed listeners alike.
                    string kind = _r.Weighted(("world", 4), ("filter", 3), ("typed", 3));
                    string tt = _r.Pick(new[] { "A", "B", "C", "D", "Tag", "R" });
                    string fname = null;
                    if (kind == "filter") {
                        var f = NewFilter((new[] { tt }, Array.Empty<string>(), Array.Empty<string>()));
                        ops.Add(f);
                        fname = f.F;
                    }
                    ListenerDef Mk() => kind == "typed" ? NewListener("typed", t: tt) : kind == "filter" ? NewListener("filter", f: fname) : NewListener("world");
                    string on = kind == "typed" ? (_r.P(0.5) ? "added" : "removed")
                        : kind == "filter" ? (_r.P(0.5) ? "enter" : "leave")
                        : _r.Weighted(("compAdded", 4), ("compRemoved", 3), ("created", 2), ("destroyed", 2));
                    var l0 = Mk();
                    var l1 = Mk();
                    var l2 = Mk();
                    var change = new Rule { On = on, Max = _r.Pick(new[] { 1, 2 }), T = kind == "world" && on.StartsWith("comp") && _r.P(0.5) ? tt : null };
                    change.Do.Add(new Step { A = "unlisten", Lid = _r.P(0.5) ? 0 : l1.Lid });
                    change.Do.Add(new Step { A = "listen", Lid = l2.Lid });
                    if (_r.P(0.3)) {
                        change.Do.Add(new Step { A = "listen", Lid = _r.P(0.5) ? 0 : l1.Lid });
                    }
                    l0.Rules.Add(change);
                    // l1 and l2 only need to be called to show in the log; a light reaction makes their calls visible to typed recorders too.
                    l1.Rules.Add(new Rule { On = on, Max = 2, Do = { new Step { A = "mark", Who = "self", T = "D" } } });
                    l2.Rules.Add(new Rule { On = on, Max = 2 });
                    if (_r.P(0.5)) {
                        ops.Add(new Op { K = "listen", Lid = l0.Lid });
                        ops.Add(new Op { K = "listen", Lid = l1.Lid });
                    } else {
                        ops.Add(new Op { K = "listen", Lid = l1.Lid });
                        ops.Add(new Op { K = "listen", Lid = l0.Lid });
                    }
                    var e = Create("Tag");
                    ops.Add(e);
                    ops.Add(new Op { K = "add", E = e.Name, T = tt, Id = Id(), P = _r.Int(10) });
                    ops.Add(new Op { K = "remove", E = e.Name, T = tt });
                    var e2 = Create(tt);
                    ops.Add(e2);
                    ops.Add(new Op { K = "destroy", E = e2.Name });
                    if (_r.P(0.5)) {
                        ops.Add(Create(tt));
                    }
                    break;
                }
                case "reentrant-self": {
                    // OnRemoved destroys its own entity: the nested Remove finishes the notification and the removal, and
                    // every listener still gets exactly one OnRemoved (ComponentPool.Remove XML doc, 2.1.1).
                    string tt = t == "Tag" ? "A" : t;
                    var e = Create("Tag");
                    ops.Add(e);
                    ops.Add(new Op { K = "add", E = e.Name, T = tt, Id = Id() });
                    var l0 = NewListener("typed", t: tt);
                    l0.Rules.Add(new Rule { On = "removed", Max = 1, Do = { _r.P(0.6) ? new Step { A = "destroy", Who = "self" } : new Step { A = "remove", Who = "self", T = "Tag", G = true } } });
                    var l1 = NewListener("typed", t: tt);
                    l1.Rules.Add(new Rule { On = "removed", Max = 1, Do = { new Step { A = "modify", Who = "self", T = tt, M = Marker() } } });
                    if (_r.P(0.5)) {
                        ops.Add(new Op { K = "listen", Lid = l1.Lid });
                    }
                    ops.Add(new Op { K = "listen", Lid = l0.Lid });
                    if (ops.Count < 5 || _r.P(0.5)) {
                        ops.Add(new Op { K = "listen", Lid = l1.Lid });
                    }
                    ops.Add(_r.P(0.6) ? new Op { K = "remove", E = e.Name, T = tt } : new Op { K = "destroy", E = e.Name });
                    break;
                }
            }
            return ops;
        }

        // ------------------------------------------------------------------ assembly

        private Scenario Build(int opCount, string forceMotif) {
            _s.RecTyped = _r.P(0.75);
            _s.RecFilters = _r.P(0.75);
            _s.RecWorld = _r.P(0.6);
            _s.Group = _r.P(0.25);

            var ops = new List<Op>();
            int nf = _r.Range(0, 3);
            for (int i = 0; i < nf; i++) {
                ops.Add(NewFilter(RandomFilterSpec()));
            }
            int nl = _r.Range(0, 4);
            for (int i = 0; i < nl; i++) {
                var def = RandomListener();
                if (_r.P(0.85)) {
                    ops.Add(new Op { K = "listen", Lid = def.Lid });
                }
            }
            int ne = _r.Range(2, 7);
            for (int i = 0; i < ne; i++) {
                var c = Create(Kind());
                ops.Add(c);
                if (_r.P(0.5)) {
                    ops.Add(new Op { K = "add", E = c.Name, T = Kind(), Id = Id(), P = _r.Int(10) });
                }
            }
            if (_names.Count >= 2) {
                for (int i = 0; i < _r.Range(0, 2); i++) {
                    string a = _r.Pick(_names), b = _r.Pick(_names);
                    if (a != b) ops.Add(new Op { K = "link", E = a, E2 = b });
                }
            }

            var main = new List<Op>();
            for (int i = 0; i < opCount; i++) {
                main.Add(RandomOp());
            }

            var motifs = new List<string>();
            if (forceMotif != null) {
                motifs.Add(forceMotif);
            } else {
                string roll = _r.Weighted(("0", 45), ("1", 40), ("2", 15));
                int n = roll == "0" ? 0 : roll == "1" ? 1 : 2;
                for (int i = 0; i < n; i++) {
                    string m = _r.Pick(MotifNames);
                    if (m == "stale-addevent" && !_debug) m = "writeback";
                    if (m == "iterate" && _crash) m = "readd";
                    motifs.Add(m);
                }
            }
            // A motif built for an excluded family is left out (its block is still generated, so the random stream
            // and the rest of the scenario stay the same).
            foreach (var m in motifs.ToList()) {
                int listenersBefore = _s.Listeners.Count;
                var block = Motif(m);
                int at = _r.Int(main.Count + 1);
                if (Exclusions.MotifHits(m, _exclude)) {
                    _s.Listeners.RemoveRange(listenersBefore, _s.Listeners.Count - listenersBefore);
                    motifs.Remove(m);
                    continue;
                }
                main.InsertRange(at, block);
            }
            if (motifs.Count > 0) {
                _s.Origin = "motif:" + string.Join("+", motifs);
            }
            ops.AddRange(main);
            _s.Ops = ops;
            return _s;
        }
    }
}
