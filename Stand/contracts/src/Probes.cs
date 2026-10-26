using System;
using System.Collections.Generic;
using System.IO;
using KenseiECS;
using Stand.Contracts.Probe;

namespace Stand.Contracts {
    /// <summary> One safe query: run after the call, and on a fresh instance of the same state. </summary>
    public sealed class ProbeDef {
        public string Name;
        public string Code;      // a C# expression, or a block "{ ...; return x; }"
        public Func<object> Run;
        public bool Extended;    // creates storage: not one of the promised safe queries
        public bool Mutating;    // changes the state: a repro must run it to reach the probes after it

        /// <summary> The probe as the body of a Func&lt;object&gt; lambda in a repro. </summary>
        public static string Lambda(string code) {
            if (code.StartsWith("{", StringComparison.Ordinal)) {
                return "() => " + code;
            }
            if (code.StartsWith("world.DestroyEntity", StringComparison.Ordinal)) {
                return "() => { " + code + "; return \"void\"; }";
            }
            return "() => " + code;
        }
    }

    /// <summary>
    /// The state probe. After every call these queries are compared with a fresh object in the same
    /// state (the scenario rebuilt without the call): IsAlive, EntityCount, IsDestroyed, TryGetEntity,
    /// Has and DestroyEntity on a stale handle, the number of alive entities without a component,
    /// Filter.Count and the counts of the other objects of the scenario. Two extended probes then create
    /// a pool and a filter of a type nothing touched before. The policy decides what a difference means
    /// (Policy.JudgeState): a defect only where a document of the version promises the answer.
    /// </summary>
    public static class Probes {
        /// <summary> Name suffix of the invariant probe: alive entities that have no component. </summary>
        public const string AliveWithoutComponents = "alive entities without components";

        /// <summary> How many alive entities of the world have no component (the package keeps it at 0). </summary>
        public static int CountAliveWithoutComponents(World w) {
            int n = 0;
            foreach (var e in w.AliveEntities) {
                if (w.GetComponentCount(e) == 0) {
                    n++;
                }
            }
            return n;
        }

        public const string AliveWithoutComponentsCode = "{ int an = 0; foreach (var ae in world.AliveEntities) { if (world.GetComponentCount(ae) == 0) { an++; } } return an; }";

        public static List<ProbeDef> Define(Ctx c) {
            var list = new List<ProbeDef>();
            var w = c.World;
            if (w != null) {
                list.Add(P("IsDestroyed", "world.IsDestroyed", () => w.IsDestroyed));
                list.Add(P("EntityCount", "world.EntityCount", () => w.EntityCount));
                list.Add(P("IsAlive(Entity.Null)", "world.IsAlive(Entity.Null)", () => w.IsAlive(Entity.Null)));
                if (c.HasEntities) {
                    var e0 = c.E0;
                    var e1 = c.E1;
                    var e2 = c.E2;
                    var e3 = c.E3;
                    list.Add(P("IsAlive(e0)", "world.IsAlive(e0)", () => w.IsAlive(e0)));
                    list.Add(P("IsAlive(e1)", "world.IsAlive(e1)", () => w.IsAlive(e1)));
                    list.Add(P("IsAlive(e2)", "world.IsAlive(e2)", () => w.IsAlive(e2)));
                    list.Add(P("IsAlive(e3)", "world.IsAlive(e3)", () => w.IsAlive(e3)));
                }
                if (c.HasStale) {
                    var stale = c.Stale;
                    list.Add(P("IsAlive(stale)", "world.IsAlive(stale)", () => w.IsAlive(stale)));
                }
                if (c.HasReused) {
                    var reused = c.Reused;
                    list.Add(P("IsAlive(reused)", "world.IsAlive(reused)", () => w.IsAlive(reused)));
                }
                list.Add(P("TryGetEntity(0)", "world.TryGetEntity(0, out var t0) + \":\" + t0", () => TryGet(w, 0)));
                list.Add(P("TryGetEntity(1)", "world.TryGetEntity(1, out var t1) + \":\" + t1", () => TryGet(w, 1)));
                if (c.HasStale) {
                    int si = c.Stale.Index;
                    list.Add(P("TryGetEntity(stale.Index)", "world.TryGetEntity(stale.Index, out var ts) + \":\" + ts", () => TryGet(w, si)));
                }
                var handle = c.HasStale ? c.Stale : c.HasEntities ? c.E1 : Entity.Null;
                string handleCode = c.HasStale ? "stale" : c.HasEntities ? "e1" : "Entity.Null";
                list.Add(P("Has<CA>(" + handleCode + ")", "world.Has<CA>(" + handleCode + ")", () => w.Has<CA>(handle)));
                // The invariant the docs state: an entity is alive if and only if it has a component.
                list.Add(P(AliveWithoutComponents, AliveWithoutComponentsCode, () => CountAliveWithoutComponents(w)));
                list.Add(P("FilterCount", "world.FilterCount", () => w.FilterCount));
                list.Add(P("GroupCount", "world.GroupCount", () => w.GroupCount));
                if (c.FA != null) {
                    var fa = c.FA;
                    var fb = c.FAnotB;
                    list.Add(P("fA.Count", "fA.Count", () => fa.Count));
                    list.Add(P("fAnotB.Count", "fAnotB.Count", () => fb.Count));
                }
                if (c.GroupCD != null) {
                    var g = c.GroupCD;
                    list.Add(P("groupCD.Count", "groupCD.Count", () => g.Count));
                }
                if (c.PoolA != null) {
                    var pa = c.PoolA;
                    list.Add(P("poolA.Count", "poolA.Count", () => pa.Count));
                }
            }
            if (c.Runner != null) {
                var r = c.Runner;
                var s = c.Sys;
                list.Add(P("runner.SystemCount", "runner.SystemCount", () => r.SystemCount));
                list.Add(P("runner.IsInitialized", "runner.IsInitialized", () => r.IsInitialized));
                list.Add(P("runner.IsEnabled", "runner.IsEnabled", () => r.IsEnabled));
                list.Add(P("sys.Inits/Runs/Destroys", "sys.Inits + \"/\" + sys.Runs + \"/\" + sys.Destroys", () => s.Inits + "/" + s.Runs + "/" + s.Destroys));
            }
            if (c.Buffer != null) {
                var b = c.Buffer;
                list.Add(P("buffer.Count", "buffer.Count", () => b.Count));
            }
            if (c.ListenersBox != null) {
                var box = c.ListenersBox;
                list.Add(P("listeners.Count", "listeners.Count", () => ((Listeners<IProbeListener>)box).Count));
            }
            if (c.EventsBox != null) {
                var box = c.EventsBox;
                list.Add(P("events.Count", "events.Count", () => ((EventBuffer<Ev>)box).Count));
            }
            if (c.Shared != null) {
                var sh = c.Shared;
                list.Add(P("shared.TryGet<ProbeConfig>()", "shared.TryGet<ProbeConfig>(out var c0) + \":\" + c0?.V", () => {
                    bool ok = sh.TryGet<ProbeConfig>(out var x);
                    return ok + ":" + x?.V;
                }));
            }

            // Mutating probes last, in the same order on both sides.
            if (w != null) {
                var handle = c.HasStale ? c.Stale : Entity.Null;
                string handleCode = c.HasStale ? "stale" : "Entity.Null";
                var destroy = P("DestroyEntity(" + handleCode + ")", "world.DestroyEntity(" + handleCode + ")", () => {
                    w.DestroyEntity(handle);
                    return "void";
                });
                destroy.Mutating = true;
                list.Add(destroy);
                list.Add(new ProbeDef {
                    Name = "ext:Pool<ProbeFresh>().Count", Code = "world.Pool<ProbeFresh>().Count", Extended = true, Mutating = true,
                    Run = () => w.Pool<ProbeFresh>().Count
                });
                list.Add(new ProbeDef {
                    Name = "ext:Filter().Inc<ProbeFresh>().End().Count", Code = "world.Filter().Inc<ProbeFresh>().End().Count", Extended = true, Mutating = true,
                    Run = () => w.Filter().Inc<ProbeFresh>().End().Count
                });
            }
            return list;
        }

        /// <summary>
        /// Ordinary use of every object of the scenario, after a call that returned normally although
        /// it was given null: create, change and destroy an entity, walk a filter, play back the
        /// buffer, run the runner, invoke the listeners, read the shared data, save a snapshot. The
        /// same steps run on a fresh state built the same way. Only whether a step throws is
        /// compared, never its value — the call may legitimately change counts — so a step that
        /// throws only after the call means the null was accepted and broke a later, valid operation.
        /// </summary>
        public static List<ProbeDef> Smoke(Ctx c) {
            var list = new List<ProbeDef>();
            var w = c.World;
            bool live = w != null && !w.IsDestroyed;
            if (live) {
                list.Add(S("smoke:world", "{ var se = world.CreateEntity(new ProbeFresh { V = 1 }); world.Add(se, new CA { V = 2 }); world.Remove<CA>(se); world.DestroyEntity(se); return \"ok\"; }", () => {
                    var se = w.CreateEntity(new ProbeFresh { V = 1 });
                    w.Add(se, new CA { V = 2 });
                    w.Remove<CA>(se);
                    w.DestroyEntity(se);
                    return "ok";
                }));
            }
            if (live && c.FA != null) {
                var fa = c.FA;
                list.Add(S("smoke:filter", "{ int sk = 0; foreach (var i in fA) { sk++; } return \"ok\"; }", () => {
                    int sk = 0;
                    foreach (var i in fa) {
                        sk++;
                    }
                    return "ok";
                }));
            }
            if (live && c.Buffer != null) {
                var b = c.Buffer;
                list.Add(S("smoke:buffer", "{ buffer.Playback(world); return \"ok\"; }", () => {
                    b.Playback(w);
                    return "ok";
                }));
            }
            if (live && c.Runner != null && c.Runner.IsInitialized) {
                var r = c.Runner;
                list.Add(S("smoke:runner", "{ runner.Run(); return \"ok\"; }", () => {
                    r.Run();
                    return "ok";
                }));
            }
            if (c.ListenersBox != null) {
                var box = c.ListenersBox;
                // Through Values, which every version has (Invoke appeared in 2.2.0).
                list.Add(S("smoke:listeners", "{ if (listeners.Values != null) { foreach (var l in listeners.Values) { l.Ping(1); } } return \"ok\"; }", () => {
                    var values = ((Listeners<IProbeListener>)box).Values;
                    if (values != null) {
                        foreach (var l in values) {
                            l.Ping(1);
                        }
                    }
                    return "ok";
                }));
            }
            if (c.EventsBox != null) {
                var box = c.EventsBox;
                list.Add(S("smoke:events", "{ var ev = new EventBuffer<Ev>(); ev.Add(new Ev { V = 1 }); return \"ok\"; }", () => {
                    var ev = new EventBuffer<Ev>();
                    ev.Add(new Ev { V = 1 });
                    return "ok";
                }));
            }
            if (c.Shared != null) {
                var sh = c.Shared;
                list.Add(S("smoke:shared", "{ var sv = shared.Get<ProbeConfig>().V; return \"ok\"; }", () => {
                    var sv = sh.Get<ProbeConfig>().V;
                    return "ok";
                }));
            }
            if (live && c.Serializer != null) {
                var s = c.Serializer;
                list.Add(S("smoke:serializer", "{ serializer.Save(world, new MemoryStream()); return \"ok\"; }", () => {
                    s.Save(w, new MemoryStream());
                    return "ok";
                }));
            }
            return list;
        }

        private static ProbeDef S(string name, string code, Func<object> run) {
            return new ProbeDef { Name = name, Code = code, Run = run, Mutating = true };
        }

        /// <summary> Capture a list of probes defined elsewhere (argument probes, the smoke steps). </summary>
        public static List<KeyValuePair<ProbeDef, string>> CaptureAll(List<ProbeDef> defs) {
            var values = new List<KeyValuePair<ProbeDef, string>>(defs.Count);
            foreach (var d in defs) {
                values.Add(new KeyValuePair<ProbeDef, string>(d, Eval(d.Run)));
            }
            return values;
        }

        /// <summary> Smoke comparison: only ok vs the exception type matters. </summary>
        public static List<ProbeDiff> CompareThrows(List<KeyValuePair<ProbeDef, string>> actual, List<KeyValuePair<ProbeDef, string>> expected) {
            // By name: a step exists on one side only when the call changed what the step needs (it
            // de-initialized the runner, say); such a step is not compared.
            var diffs = new List<ProbeDiff>();
            var byName = new Dictionary<string, string>();
            foreach (var e in expected) {
                byName[e.Key.Name] = e.Value;
            }
            foreach (var a in actual) {
                if (!byName.TryGetValue(a.Key.Name, out var e)) {
                    continue;
                }
                if (Kind(a.Value) != Kind(e)) {
                    diffs.Add(new ProbeDiff { Probe = a.Key.Name, Actual = a.Value, Expected = e, Extended = false });
                }
            }
            return diffs;
        }

        private static string Kind(string v) => v.StartsWith("ok:", StringComparison.Ordinal) ? "ok" : v;

        private static ProbeDef P(string name, string code, Func<object> run) {
            return new ProbeDef { Name = name, Code = code, Run = run };
        }

        /// <summary> A probe of the world and its pools, filters and groups (not of a runner, buffer, listener list or the shared data). </summary>
        public static bool IsWorldProbe(string name) {
            foreach (var prefix in new[] { "runner.", "sys.", "buffer.", "listeners.", "events.", "shared.", "arg:", "smoke:", "ext:" }) {
                if (name.StartsWith(prefix, StringComparison.Ordinal)) {
                    return false;
                }
            }
            return true;
        }

        private static string TryGet(World w, int index) {
            bool ok = w.TryGetEntity(index, out var e);
            return ok + ":" + e;
        }

        public static List<KeyValuePair<ProbeDef, string>> Capture(Ctx c) {
            var defs = Define(c);
            var values = new List<KeyValuePair<ProbeDef, string>>(defs.Count);
            foreach (var d in defs) {
                values.Add(new KeyValuePair<ProbeDef, string>(d, Eval(d.Run)));
            }
            return values;
        }

        public static string Eval(Func<object> f) {
            try {
                return "ok:" + Fmt.Value(f());
            } catch (Exception e) {
                return "ex:" + e.GetType().Name;
            }
        }

        public static List<ProbeDiff> Compare(List<KeyValuePair<ProbeDef, string>> actual, List<KeyValuePair<ProbeDef, string>> expected) {
            var diffs = new List<ProbeDiff>();
            int n = Math.Min(actual.Count, expected.Count);
            for (int i = 0; i < n; i++) {
                if (actual[i].Key.Name != expected[i].Key.Name) {
                    throw new InvalidOperationException("probe lists differ: " + actual[i].Key.Name + " vs " + expected[i].Key.Name);
                }
                if (actual[i].Value != expected[i].Value) {
                    diffs.Add(new ProbeDiff {
                        Probe = actual[i].Key.Name, Actual = actual[i].Value, Expected = expected[i].Value, Extended = actual[i].Key.Extended
                    });
                }
            }
            return diffs;
        }
    }
}
