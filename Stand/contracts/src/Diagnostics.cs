using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using KenseiECS;
using Stand.Contracts.Probe;

namespace Stand.Contracts {
    public sealed class NullCall {
        public string Param;
        public string Code;
        public string Outcome;      // ok / ex:Type
        public string Behavior;     // what got registered or happened, in words
        public bool RegisteredPhase;
        public bool RegisteredRunnable;
        public bool RegisteredSomething;
        public string CallCode;     // the call alone, on a runner named r (for the repro)
    }

    public sealed class ObsoleteEntry {
        public string Member;
        public string Message;
        public bool IsError;
        public List<NullCall> NullCalls = new();
        public string Heuristic;    // verdict of the "phase" heuristic, or null
        public bool Mismatch;
    }

    public sealed class GrowthCheck {
        public string Storage;      // pool dense, filter dense, filter sparse page ...
        public string Source;       // EcsProfiler StorageGrown | World.FirstGrowthAfterReserve
        public bool Available = true;   // false: the API this check needs is not in this version
        public int Instances;       // how many distinct storages of this kind grew
        public List<string> Owners = new();
        public List<string> Events = new();
        public bool Distinct;
        public string Note;
        public string Claim;        // growth/pool-owner | growth/filter-owner: the promise that names the owners
        public string SetupCode;    // C# of the setup and the growth, for the repro (a world named w)
    }

    /// <summary> One probe of a documented promise outside the matrix (FirstGrowthAfterReserve, Filter spans). </summary>
    public sealed class ContractProbe {
        public string Id;           // first-growth/later-growths-keep, span/old-span-after-reserve, ...
        public string Area;         // first-growth | span
        public string Status;       // ok | violated | not-available | inconclusive
        public string Observed;     // the line the repro prints
        public string Expected;     // what the promise requires, in words
        public string Note;
        public string Code;         // C# body of the repro (statements ending in a return of a string)
    }

    public sealed class LeakCheck {
        public string Status;       // leak | ok | inconclusive | n/a
        public bool GroupAlive, GroupPoolAlive, FilterAlive, PlainPoolAlive;
        public int GroupCountAfterDestroy;
        public int FilterCountAfterDestroy;
        public string Note;
    }

    public sealed class DiagResult {
        public bool Debug;
        public List<ObsoleteEntry> Obsolete = new();
        public List<GrowthCheck> Growth = new();
        public string GrowthNote;
        public List<ContractProbe> FirstGrowth = new();
        public List<ContractProbe> Spans = new();
        public List<ContractProbe> V230 = new();   // Diagnostics230: Filter.ReservedCount from the types, World.UnsubscribeLater
        public LeakCheck Leak;
        public List<string> Errors = new();   // a check that threw outside its own probes: the stand could not look
    }

    /// <summary>
    /// Diagnostics and promises outside the matrix: every [Obsolete] text next to what its overload
    /// really does with null; whether StorageGrown owners (EcsProfiler) and FirstGrowthAfterReserve
    /// tell storages of one kind apart; the FirstGrowthAfterReserve contract (later growths keep the
    /// first, Clear keeps both, empty in release); Filter.Entities spans and the enumerator around
    /// Filter.Reserve and World.Reserve; whether World.Destroy lets its groups go (a WeakReference
    /// probe). The worker only observes; the supervisor decides with the docs of the version.
    /// </summary>
    public static class Diagnostics {
        public static DiagResult Run(bool debug) {
            var d = new DiagResult { Debug = debug };
            // Each check on its own: a check that a broken package makes throw must not take the others with it.
            Guard(d, "obsolete", () => d.Obsolete = CheckObsolete());
            Guard(d, "growth", () => CheckGrowth(d, debug));
            Guard(d, "first-growth", () => CheckFirstGrowthContract(d, debug));
            Guard(d, "spans", () => CheckSpans(d));
            Guard(d, "leak", () => d.Leak = CheckLeak());
            Guard(d, "v230", () => Diagnostics230.Run(d));
            return d;
        }

        private static void Guard(DiagResult d, string what, Action check) {
            try {
                check();
            } catch (Exception e) {
                var x = Unwrap(e);
                d.Errors.Add(what + ": " + x.GetType().Name + ": " + x.Message);
            }
        }

        private static Exception Unwrap(Exception e) => e is TargetInvocationException t && t.InnerException != null ? t.InnerException : e;

        // A probe of a promise: what it saw, or the exception the package threw ("ex:<type>", as a repro prints it).
        private static string Observe(Func<string> probe, out bool threw) {
            try {
                threw = false;
                return probe();
            } catch (Exception e) {
                threw = true;
                return "ex:" + Unwrap(e).GetType().Name;
            }
        }

        // =====================================================================
        // [Obsolete]
        // =====================================================================

        private static readonly Regex PhasePromise = new Regex(@"creates?\s+(a\s+)?(separate\s+)?phase", RegexOptions.IgnoreCase);
        private static readonly string[] NameConditions = { "with a name", "when named", "if named", "with a non-null name", "when a name", "if a name", "only with a name", "given a name" };

        public static List<ObsoleteEntry> CheckObsolete() {
            var list = new List<ObsoleteEntry>();
            foreach (var (name, attr, info) in Catalog.ObsoleteMembers()) {
                var e = new ObsoleteEntry { Member = name, Message = attr.Message, IsError = attr.IsError };
                list.Add(e);
                if (!(info is MethodInfo mi) || mi.IsGenericMethodDefinition || mi.IsStatic) {
                    continue;
                }
                var ps = mi.GetParameters();
                for (int i = 0; i < ps.Length; i++) {
                    if (ps[i].ParameterType.IsValueType || ps[i].IsOut) {
                        continue;
                    }
                    e.NullCalls.Add(CallWithNull(mi, i));
                }
                e.Heuristic = PhaseHeuristic(e, mi, out e.Mismatch);
            }
            return list;
        }

        private static NullCall CallWithNull(MethodInfo mi, int nullSlot) {
            var ps = mi.GetParameters();
            var nc = new NullCall { Param = ps[nullSlot].Name };
            // A fresh, never initialized runner (or a fresh instance of the declaring type), so the call
            // is not answered by "Add after Init".
            var ctx = Scenarios.Build(Scenarios.Fresh, Part.All);
            object target;
            string targetCode;
            if (mi.DeclaringType == typeof(SystemsRunner)) {
                target = new SystemsRunner(ctx.World, ctx.Shared);
                targetCode = "var r = new SystemsRunner(world, shared);";
            } else {
                var t = Catalog.Targets().FirstOrDefault(x => x.Type == mi.DeclaringType);
                target = t != null ? ctx.TargetObject(t) : null;
                targetCode = t != null ? "var r = " + t.Var + ";" : "// no target";
            }
            if (target == null) {
                // A type the stand has no instance of (an [Obsolete] member added on a new type): listed, not called.
                nc.Code = "-";
                nc.Outcome = "not-run";
                nc.Behavior = "not called: the stand builds no instance of " + TypeNames.CSharp(mi.DeclaringType) + " (add a target to Catalog.Targets)";
                return nc;
            }
            var target0 = Catalog.Targets().FirstOrDefault(x => x.Type == mi.DeclaringType) ?? new Target { Name = mi.DeclaringType.Name, Type = mi.DeclaringType, Var = "r" };
            var member = Catalog.Describe(target0, mi, mi);
            var args = new ArgValue[ps.Length];
            for (int i = 0; i < ps.Length; i++) {
                args[i] = i == nullSlot ? new ArgValue { Value = null, Code = "null", IsNull = true } : Args.Normal(member, i, ctx);
            }
            nc.CallCode = "r." + mi.Name + "(" + string.Join(", ", args.Select(a => a.Code)) + ")";
            nc.Code = targetCode + " " + nc.CallCode + ";";
            int before = target is SystemsRunner r0 ? r0.SystemCount : -1;
            var thunk = Invoker.For(mi, out _);
            try {
                thunk(target, args.Select(a => a.Value).ToArray());
                nc.Outcome = "ok";
            } catch (Exception ex) {
                nc.Outcome = "ex:" + ex.GetType().Name;
                nc.Behavior = "throws " + ex.GetType().Name + ": " + ex.Message;
            }
            if (target is SystemsRunner runner && runner.SystemCount > before) {
                var info = runner.GetSystemInfo(runner.SystemCount - 1);
                nc.RegisteredSomething = true;
                nc.RegisteredPhase = info.IsSeparatePhase;
                nc.RegisteredRunnable = info.IsRunnable;
                string kind = info.IsSeparatePhase ? "a phase (IsSeparatePhase=true, outside the parent's Run)"
                    : info.ChildRunner != null ? (info.IsRunnable ? "an unnamed inline group (IsSeparatePhase=false, IsRunnable=true: runs inside the parent's Run)" : "a nested runner outside Run")
                    : info.System == null ? "an entry with System == null" : "a system";
                nc.Behavior = (nc.Outcome == "ok" ? "" : nc.Behavior + "; and ") + "registers " + kind + ", Name=\"" + info.Name + "\"";
            } else if (nc.Behavior == null) {
                nc.Behavior = "returns without registering anything";
            }
            return nc;
        }

        // The message promises a phase; for a null name the overload must then register a phase, unless
        // the promise is conditioned on a name ("With a name, ... creates a separate phase").
        private static string PhaseHeuristic(ObsoleteEntry e, MethodInfo mi, out bool mismatch) {
            mismatch = false;
            if (e.Message == null || e.Message.IndexOf("phase", StringComparison.OrdinalIgnoreCase) < 0) {
                return null;
            }
            var nameCall = e.NullCalls.FirstOrDefault(c => c.Param == "name");
            if (nameCall == null) {
                return "the text mentions a phase, but the overload has no name parameter: the heuristic does not apply";
            }
            if (nameCall.Outcome == "not-run") {
                return "the overload was not called: the heuristic does not apply";
            }
            if (!nameCall.RegisteredSomething) {
                // Nothing was registered (the call threw, or registered nothing): neither a phase nor a group to compare with.
                return "with name = null the overload registered nothing (" + nameCall.Behavior + "): the heuristic does not apply";
            }
            string promiseClause = e.Message.Split(';').Select(s => s.Trim()).FirstOrDefault(s => PhasePromise.IsMatch(s));
            if (promiseClause == null) {
                return "the text mentions a phase but does not promise to create one: the heuristic does not apply";
            }
            int at = PhasePromise.Match(promiseClause).Index;
            string before = promiseClause.Substring(0, at).ToLowerInvariant();
            bool conditioned = NameConditions.Any(c => before.Contains(c));
            if (conditioned) {
                return "the phase is promised on condition of a name (\"" + promiseClause + "\"); with name = null: " + nameCall.Behavior + " - no mismatch";
            }
            if (nameCall.RegisteredPhase) {
                return "the text promises a phase unconditionally, and with name = null a phase is registered too - no mismatch";
            }
            mismatch = true;
            return "the text promises a phase unconditionally (\"" + promiseClause + "\"), but with name = null it " + nameCall.Behavior;
        }

        // =====================================================================
        // Growth owners
        // =====================================================================

        private static readonly string[] StorageKinds = { "pool dense", "pool sparse", "filter dense", "filter sparse page", "entity slots", "free-slot list" };

        // The setups, as C# for the repros (the stand runs the same steps through reflection below).
        public const string FilterDenseSetup =
            "var w = new World(new WorldConfig { InitialPoolDenseCapacity = 2 });\n" +
            "var temp = new Entity[64];\n" +
            "for (int i = 0; i < 64; i++) { temp[i] = w.CreateEntity(new GA()); w.Add(temp[i], new GB()); }\n" +
            "for (int i = 0; i < 64; i++) { w.DestroyEntity(temp[i]); }\n" +
            "w.Filter().Inc<GA>().End();\n" +
            "w.Filter().Inc<GB>().End();\n" +
            "w.Reserve(64, 2);\n";
        public const string FilterDenseGrowA = "for (int i = 0; i < 10; i++) { w.CreateEntity(new GA { V = i }); }\n";
        public const string FilterDenseGrowB = "for (int i = 0; i < 10; i++) { w.CreateEntity(new GB { V = i }); }\n";
        private const string PoolDenseSetup =
            "var w = new World(new WorldConfig { InitialPoolDenseCapacity = 2 });\n" +
            "w.Reserve(64, 2);\n" +
            "w.Pool<GA>();\n" +
            "w.Pool<GB>();\n";
        private const string PoolSparseSetup =
            "var w = new World(new WorldConfig { InitialPoolSparseCapacity = 64 });\n" +
            "w.Pool<GA>();\n" +
            "w.Pool<GB>();\n" +
            "w.Reserve(64, 0);\n" +
            "for (int i = 0; i < 100; i++) { w.CreateEntity(new CF { V = i }); }\n";
        private const string FilterSparseSetup =
            "var w = new World();\n" +
            "w.Filter().Inc<GA>().End();\n" +
            "w.Filter().Inc<GB>().End();\n" +
            "w.Pool<GA>();\n" +
            "w.Pool<GB>();\n" +
            "w.Reserve(64, 0);\n" +
            "for (int i = 0; i < 1100; i++) { w.CreateEntity(new CF { V = i }); }\n";
        private const string GrowOneEach = "w.CreateEntity(new GA { V = 1 });\nw.CreateEntity(new GB { V = 1 });\n";

        private static void CheckGrowth(DiagResult d, bool debug) {
            var reserve = typeof(World).GetMethod("Reserve", new[] { typeof(int), typeof(int) });
            if (!debug) {
                d.GrowthNote = "KENSEI_DEBUG only: release does not track growth (as documented)";
                return;
            }
            if (reserve == null) {
                d.GrowthNote = "not in this version: no World.Reserve, so no growth-after-reserve signal";
                return;
            }
            var asm = typeof(World).Assembly;
            var profiler = asm.GetType("KenseiECS.EcsProfiler");
            var eventType = asm.GetType("KenseiECS.ProfileEventType");
            object storageGrown = null;
            if (eventType != null && Enum.GetNames(eventType).Contains("StorageGrown")) {
                storageGrown = Enum.Parse(eventType, "StorageGrown");
            }
            var firstGrowth = typeof(World).GetProperty("FirstGrowthAfterReserve");

            if (profiler != null && storageGrown != null) {
                // Two filters whose dense arrays outgrow the reserve; their pools are reserved on their own.
                d.Growth.Add(ProfilerCheck("filter dense", 2, "growth/filter-owner", FilterDenseSetup + "EcsProfiler.Enable(w);\n" + FilterDenseGrowA + FilterDenseGrowB,
                    () => FilterDenseWorld(reserve), w => { GrowA(w); GrowB(w); }, profiler, storageGrown));
                // Two pools (pools are named by their component type).
                d.Growth.Add(ProfilerCheck("pool dense", 2, "growth/pool-owner", PoolDenseSetup + "EcsProfiler.Enable(w);\n" + FilterDenseGrowA + FilterDenseGrowB, () => {
                    var w = new World(new WorldConfig { InitialPoolDenseCapacity = 2 });
                    reserve.Invoke(w, new object[] { 64, 2 });
                    w.Pool<GA>();
                    w.Pool<GB>();
                    return w;
                }, w => { GrowA(w); GrowB(w); }, profiler, storageGrown));
                // Two pools whose sparse arrays grow past the reserved entities.
                d.Growth.Add(ProfilerCheck("pool sparse", 2, "growth/pool-owner", PoolSparseSetup + "EcsProfiler.Enable(w);\n" + GrowOneEach, () => {
                    var w = new World(new WorldConfig { InitialPoolSparseCapacity = 64 });
                    w.Pool<GA>();
                    w.Pool<GB>();
                    reserve.Invoke(w, new object[] { 64, 0 });
                    for (int i = 0; i < 100; i++) {
                        w.CreateEntity(new CF { V = i });
                    }
                    return w;
                }, w => {
                    w.CreateEntity(new GA { V = 1 });
                    w.CreateEntity(new GB { V = 1 });
                }, profiler, storageGrown));
                // Two filters that both get a sparse page past the reserved entities.
                d.Growth.Add(ProfilerCheck("filter sparse page", 2, "growth/filter-owner", FilterSparseSetup + "EcsProfiler.Enable(w);\n" + GrowOneEach, () => {
                    var w = new World();
                    w.Filter().Inc<GA>().End();
                    w.Filter().Inc<GB>().End();
                    w.Pool<GA>();
                    w.Pool<GB>();
                    reserve.Invoke(w, new object[] { 64, 0 });
                    for (int i = 0; i < 1100; i++) {
                        w.CreateEntity(new CF { V = i });
                    }
                    return w;
                }, w => {
                    w.CreateEntity(new GA { V = 1 });
                    w.CreateEntity(new GB { V = 1 });
                }, profiler, storageGrown));
            } else {
                d.GrowthNote = "not in this version: EcsProfiler has no StorageGrown event";
            }

            if (firstGrowth != null) {
                // The first growth of a world where only filter A grows, and of one where only filter B does.
                var check = new GrowthCheck {
                    Storage = "filter dense", Source = "World.FirstGrowthAfterReserve", Instances = 2, Claim = "growth/filter-owner",
                    SetupCode = "var owners = new List<string>();\n" +
                        "{\n" + Indent(FilterDenseSetup + FilterDenseGrowA) + "    owners.Add(w.FirstGrowthAfterReserve.Owner ?? \"null\");\n}\n" +
                        "{\n" + Indent(FilterDenseSetup + FilterDenseGrowB) + "    owners.Add(w.FirstGrowthAfterReserve.Owner ?? \"null\");\n}\n"
                };
                foreach (bool aFirst in new[] { true, false }) {
                    var w = FilterDenseWorld(reserve);
                    if (aFirst) {
                        GrowA(w);
                    } else {
                        GrowB(w);
                    }
                    var g = firstGrowth.GetValue(w);
                    var owner = (string)g.GetType().GetField("Owner")?.GetValue(g);
                    var storage = (string)g.GetType().GetField("Storage")?.GetValue(g);
                    check.Events.Add(g.ToString());
                    check.Owners.Add(owner ?? "null");
                    if (storage != "filter dense") {
                        check.Note = "the first growth was not a filter dense array: " + g;
                    }
                }
                check.Distinct = check.Owners.Distinct().Count() == check.Owners.Count;
                d.Growth.Add(check);
            } else {
                d.Growth.Add(new GrowthCheck {
                    Storage = "filter dense", Source = "World.FirstGrowthAfterReserve", Available = false, Distinct = true,
                    Note = "not in this version: World.FirstGrowthAfterReserve does not exist"
                });
            }
        }

        private static string Indent(string code) => string.Concat(code.Split('\n').Where(l => l.Length > 0).Select(l => "    " + l + "\n"));

        // The pools grow to 64 on temporary entities, then come two empty filters (dense 2) and the reserve:
        // the first array to outgrow a reserve is a filter's, in every version. The pools are not reserved
        // with Pool.Reserve, because from 2.3.0 that reserves the filters of their types as well.
        private static World FilterDenseWorld(MethodInfo reserve) {
            var w = new World(new WorldConfig { InitialPoolDenseCapacity = 2 });
            var temp = new Entity[64];
            for (int i = 0; i < 64; i++) {
                temp[i] = w.CreateEntity(new GA());
                w.Add(temp[i], new GB());
            }
            for (int i = 0; i < 64; i++) {
                w.DestroyEntity(temp[i]);
            }
            w.Filter().Inc<GA>().End();
            w.Filter().Inc<GB>().End();
            reserve.Invoke(w, new object[] { 64, 2 });
            return w;
        }

        private static void GrowA(World w) {
            for (int i = 0; i < 10; i++) {
                w.CreateEntity(new GA { V = i });
            }
        }

        private static void GrowB(World w) {
            for (int i = 0; i < 10; i++) {
                w.CreateEntity(new GB { V = i });
            }
        }

        private static GrowthCheck ProfilerCheck(string storage, int instances, string claim, string setupCode, Func<World> setup, Action<World> grow, Type profiler, object storageGrown) {
            var check = new GrowthCheck { Storage = storage, Source = "EcsProfiler StorageGrown", Instances = instances, Claim = claim, SetupCode = setupCode };
            var enable = profiler.GetMethod("Enable");
            var disable = profiler.GetMethod("Disable");
            var byType = profiler.GetMethod("GetEventsByType");
            var w = setup();
            enable.Invoke(null, new object[] { w });
            try {
                grow(w);
                var events = (System.Collections.IList)byType.Invoke(null, new[] { storageGrown });
                foreach (var ev in events) {
                    var text = (string)ev.GetType().GetField("ComponentType").GetValue(ev);
                    string kind = StorageKinds.Where(k => text.Contains(" " + k + " ")).OrderByDescending(k => k.Length).FirstOrDefault();
                    if (kind != storage) {
                        continue;
                    }
                    check.Events.Add(text);
                    check.Owners.Add(text.Substring(0, text.IndexOf(" " + kind + " ", StringComparison.Ordinal)));
                }
            } finally {
                disable.Invoke(null, null);
            }
            var distinct = check.Owners.Distinct().ToList();
            check.Owners = distinct;
            if (check.Events.Count == 0) {
                check.Note = "no " + storage + " events: the check did not trigger";
                check.Distinct = true;
            } else {
                check.Distinct = distinct.Count >= instances;
            }
            return check;
        }

        // =====================================================================
        // The FirstGrowthAfterReserve contract
        // =====================================================================

        private static void CheckFirstGrowthContract(DiagResult d, bool debug) {
            var firstGrowth = typeof(World).GetProperty("FirstGrowthAfterReserve");
            var growths = typeof(World).GetProperty("GrowthsAfterReserve");
            var reserve = typeof(World).GetMethod("Reserve", new[] { typeof(int), typeof(int) });
            if (firstGrowth == null || growths == null || reserve == null) {
                d.FirstGrowth.Add(new ContractProbe {
                    Id = "first-growth/api", Area = "first-growth", Status = "not-available",
                    Note = "World.FirstGrowthAfterReserve is not in this version: the first growth after Reserve is visible only through EcsProfiler"
                });
                return;
            }
            string G(World w) => firstGrowth.GetValue(w).ToString();
            int N(World w) => (int)growths.GetValue(w);
            bool Empty(World w) {
                var g = firstGrowth.GetValue(w);
                return g.GetType().GetProperty("IsEmpty") is PropertyInfo p ? (bool)p.GetValue(g) : g.ToString() == "none";
            }

            if (debug) {
                // Filter A outgrows the reserve first, then filter B: the first growth stays A's.
                string afterA = null, afterB = null;
                int countA = 0, countB = 0;
                World w = null;
                string observed = Observe(() => {
                    w = FilterDenseWorld(reserve);
                    GrowA(w);
                    afterA = G(w);
                    countA = N(w);
                    GrowB(w);
                    afterB = G(w);
                    countB = N(w);
                    return "afterA=" + afterA + " | afterB=" + afterB + " | count=" + countA + "->" + countB;
                }, out bool threw);
                var keep = new ContractProbe {
                    Id = "first-growth/later-growths-keep", Area = "first-growth",
                    Expected = "FirstGrowthAfterReserve after filter B grew is still the growth of filter A, and GrowthsAfterReserve went up",
                    Observed = observed,
                    Code = FilterDenseSetup + FilterDenseGrowA + "var afterA = w.FirstGrowthAfterReserve.ToString();\nint countA = w.GrowthsAfterReserve;\n" + FilterDenseGrowB
                        + "return \"afterA=\" + afterA + \" | afterB=\" + w.FirstGrowthAfterReserve + \" | count=\" + countA + \"->\" + w.GrowthsAfterReserve;\n"
                };
                if (threw) {
                    keep.Status = "violated";
                } else if (afterA == "none" || countA == 0) {
                    keep.Status = "inconclusive";
                    keep.Note = "filter A did not outgrow the reserve: the probe did not trigger";
                } else {
                    keep.Status = afterB == afterA && countB > countA ? "ok" : "violated";
                }
                d.FirstGrowth.Add(keep);

                // Clear resets neither the first growth nor the count.
                string afterClear = null;
                int countClear = 0;
                string observed2 = threw ? null : Observe(() => {
                    w.Clear();
                    afterClear = G(w);
                    countClear = N(w);
                    return "before=" + afterB + " count " + countB + " | afterClear=" + afterClear + " count " + countClear;
                }, out threw);
                var clear = new ContractProbe {
                    Id = "first-growth/clear-keeps", Area = "first-growth",
                    Expected = "after Clear, FirstGrowthAfterReserve and GrowthsAfterReserve are what they were",
                    Observed = observed2,
                    Code = FilterDenseSetup + FilterDenseGrowA + FilterDenseGrowB + "var before = w.FirstGrowthAfterReserve.ToString();\nint count = w.GrowthsAfterReserve;\nw.Clear();\n"
                        + "return \"before=\" + before + \" count \" + count + \" | afterClear=\" + w.FirstGrowthAfterReserve + \" count \" + w.GrowthsAfterReserve;\n"
                };
                if (observed2 == null) {
                    clear.Status = "inconclusive";
                    clear.Note = "the growth probe before it threw";
                } else if (threw) {
                    clear.Status = "violated";
                } else if (afterB == "none") {
                    clear.Status = "inconclusive";
                    clear.Note = "nothing outgrew the reserve: the probe did not trigger";
                } else {
                    clear.Status = afterClear == afterB && countClear == countB ? "ok" : "violated";
                }
                d.FirstGrowth.Add(clear);
            } else {
                // Without KENSEI_DEBUG nothing is tracked: always empty, the count always 0.
                bool empty = false;
                int count = -1;
                string observed = Observe(() => {
                    var w = FilterDenseWorld(reserve);
                    GrowA(w);
                    GrowB(w);
                    empty = Empty(w);
                    count = N(w);
                    return "IsEmpty=" + (empty ? "True" : "False") + " | count=" + count;
                }, out bool threw);
                d.FirstGrowth.Add(new ContractProbe {
                    Id = "first-growth/release-empty", Area = "first-growth",
                    Expected = "without KENSEI_DEBUG FirstGrowthAfterReserve is empty and GrowthsAfterReserve is 0, even after the reserve was outgrown",
                    Observed = observed,
                    Code = FilterDenseSetup + FilterDenseGrowA + FilterDenseGrowB
                        + "return \"IsEmpty=\" + (w.FirstGrowthAfterReserve.IsEmpty ? \"True\" : \"False\") + \" | count=\" + w.GrowthsAfterReserve;\n",
                    Status = !threw && empty && count == 0 ? "ok" : "violated"
                });
            }
        }

        // =====================================================================
        // Filter.Entities and the enumerator around Reserve
        // =====================================================================

        private const string SpanSetup =
            "var w = new World(new WorldConfig { InitialPoolDenseCapacity = 4 });\n" +
            "var f = w.Filter().Inc<CA>().End();\n" +
            "for (int i = 0; i < 3; i++) { w.CreateEntity(new CA { V = i }); }\n";

        private static string Sorted(ReadOnlySpan<int> span) {
            var a = span.ToArray();
            Array.Sort(a);
            return "[" + string.Join(",", a) + "]";
        }

        private static string Sorted(List<int> list) {
            var a = list.ToArray();
            Array.Sort(a);
            return "[" + string.Join(",", a) + "]";
        }

        private static (World, Filter) SpanWorld() {
            var w = new World(new WorldConfig { InitialPoolDenseCapacity = 4 });
            var f = w.Filter().Inc<CA>().End();
            for (int i = 0; i < 3; i++) {
                w.CreateEntity(new CA { V = i });
            }
            return (w, f);
        }

        private static void CheckSpans(DiagResult d) {
            var filterReserve = typeof(Filter).GetMethod("Reserve", new[] { typeof(int) });
            var worldReserve = typeof(World).GetMethod("Reserve", new[] { typeof(int), typeof(int) });
            var capacity = typeof(Filter).GetProperty("DenseCapacity");
            if (filterReserve == null) {
                d.Spans.Add(new ContractProbe { Id = "span/api", Area = "span", Status = "not-available", Note = "Filter.Reserve is not in this version" });
                return;
            }

            // 1. A span taken before Filter.Reserve, read after it; and the filter's own span after it.
            {
                string before = null, oldAfter = null, current = null;
                string observed = Observe(() => {
                    var (w, f) = SpanWorld();
                    ReadOnlySpan<int> span = f.Entities;
                    before = Sorted(span);
                    filterReserve.Invoke(f, new object[] { 64 });
                    oldAfter = Sorted(span);
                    current = Sorted(f.Entities);
                    return "before=" + before + " | oldSpanAfterReserve=" + oldAfter + " | current=" + current;
                }, out bool threw);
                const string code = SpanSetup + "ReadOnlySpan<int> span = f.Entities;\nvar before = Sorted(span);\nf.Reserve(64);\n"
                    + "return \"before=\" + before + \" | oldSpanAfterReserve=\" + Sorted(span) + \" | current=\" + Sorted(f.Entities);\n";
                d.Spans.Add(new ContractProbe {
                    Id = "span/old-span-after-reserve", Area = "span", Observed = observed, Code = code,
                    Expected = "a span from Filter.Entities taken before Filter.Reserve still lists the matching entities (Reserve is no structural change)",
                    Status = !threw && oldAfter == before ? "ok" : "violated"
                });
                d.Spans.Add(new ContractProbe {
                    Id = "span/entities-after-reserve", Area = "span", Observed = observed, Code = code,
                    Expected = "after Filter.Reserve, Filter.Entities lists exactly the matching entities",
                    Status = !threw && current == before ? "ok" : "violated"
                });
            }

            // 2. The same around World.Reserve, which grows the dense arrays of every filter too.
            if (worldReserve != null) {
                string before = null, oldAfter = null, current = null;
                string observed = Observe(() => {
                    var (w, f) = SpanWorld();
                    ReadOnlySpan<int> span = f.Entities;
                    before = Sorted(span);
                    worldReserve.Invoke(w, new object[] { 16, 64 });
                    oldAfter = Sorted(span);
                    current = Sorted(f.Entities);
                    return "before=" + before + " | oldSpanAfterReserve=" + oldAfter + " | current=" + current;
                }, out bool threw);
                d.Spans.Add(new ContractProbe {
                    Id = "span/old-span-after-world-reserve", Area = "span",
                    Expected = "a span from Filter.Entities taken before World.Reserve still lists the matching entities (Reserve is no structural change)",
                    Observed = observed,
                    Code = SpanSetup + "ReadOnlySpan<int> span = f.Entities;\nvar before = Sorted(span);\nw.Reserve(16, 64);\n"
                        + "return \"before=\" + before + \" | oldSpanAfterReserve=\" + Sorted(span) + \" | current=\" + Sorted(f.Entities);\n",
                    Status = !threw && oldAfter == before && current == before ? "ok" : "violated"
                });
            }

            // 3. Filter.Reserve during a foreach: every matching entity is visited exactly once.
            {
                string expected = null, got = null;
                string observed = Observe(() => {
                    var (w, f) = SpanWorld();
                    expected = Sorted(f.Entities);
                    var seen = new List<int>();
                    int steps = 0;
                    foreach (var e in f) {
                        seen.Add(e);
                        if (++steps == 1) {
                            filterReserve.Invoke(f, new object[] { 256 });
                        }
                        if (steps > 64) {
                            break;
                        }
                    }
                    got = Sorted(seen);
                    return "expected=" + expected + " | visited=" + got;
                }, out bool threw);
                d.Spans.Add(new ContractProbe {
                    Id = "span/enumerator-during-reserve", Area = "span",
                    Expected = "Filter.Reserve during a foreach: the enumerator switches to the new array and visits every matching entity exactly once",
                    Observed = observed,
                    Code = SpanSetup + "var expected = Sorted(f.Entities);\nvar seen = new List<int>();\nint steps = 0;\n"
                        + "foreach (var e in f) { seen.Add(e); if (++steps == 1) { f.Reserve(256); } if (steps > 64) { break; } }\n"
                        + "return \"expected=\" + expected + \" | visited=\" + Sorted(seen);\n",
                    Status = !threw && got == expected ? "ok" : "violated"
                });
            }

            // 4. Room after Reserve(count), and Reserve never shrinks.
            if (capacity != null) {
                int afterBig = -1, afterSmall = -1;
                string observed = Observe(() => {
                    var (w, f) = SpanWorld();
                    filterReserve.Invoke(f, new object[] { 100 });
                    afterBig = (int)capacity.GetValue(f);
                    filterReserve.Invoke(f, new object[] { 2 });
                    afterSmall = (int)capacity.GetValue(f);
                    return "afterReserve100=" + afterBig + " | afterReserve2=" + afterSmall;
                }, out bool threw);
                d.Spans.Add(new ContractProbe {
                    Id = "span/reserve-capacity", Area = "span",
                    Expected = "after Filter.Reserve(100) DenseCapacity >= 100, and a later Reserve(2) does not shrink it",
                    Observed = observed,
                    Code = SpanSetup + "f.Reserve(100);\nint big = f.DenseCapacity;\nf.Reserve(2);\nreturn \"afterReserve100=\" + big + \" | afterReserve2=\" + f.DenseCapacity;\n",
                    Status = !threw && afterBig >= 100 && afterSmall == afterBig ? "ok" : "violated"
                });
            }
        }

        // =====================================================================
        // World.Destroy and its groups
        // =====================================================================

        private static LeakCheck CheckLeak() {
            var l = new LeakCheck();
            var (world, group, groupPool, filter, plainPool) = DestroyedWorldWithRefs();
            for (int i = 0; i < 3; i++) {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
            }
            l.GroupAlive = group.IsAlive;
            l.GroupPoolAlive = groupPool.IsAlive;
            l.FilterAlive = filter.IsAlive;
            l.PlainPoolAlive = plainPool.IsAlive;
            l.GroupCountAfterDestroy = world.GroupCount;
            l.FilterCountAfterDestroy = world.FilterCount;
            if (l.FilterAlive || l.PlainPoolAlive) {
                l.Status = "inconclusive";
                l.Note = "the control failed: a filter or a pool without a group survived GC although Destroy lets them go";
            } else if (l.GroupAlive || l.GroupPoolAlive) {
                l.Status = "leak";
                l.Note = "after Destroy the world (while referenced) keeps its groups and through them their pools with data; FilterCount=" + l.FilterCountAfterDestroy + ", GroupCount=" + l.GroupCountAfterDestroy;
            } else {
                l.Status = "ok";
                l.Note = "groups and pools collected; GroupCount=" + l.GroupCountAfterDestroy;
            }
            GC.KeepAlive(world);
            return l;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static (World, WeakReference, WeakReference, WeakReference, WeakReference) DestroyedWorldWithRefs() {
            var w = new World();
            var e = w.CreateEntity(new CC { V = 1 });
            w.Add(e, new CD { V = 2 });
            w.CreateEntity(new CA { V = 3 });
            var g = w.Group<CC, CD>();
            var f = w.Filter().Inc<CA>().End();
            var poolA = w.Pool<CA>();
            var poolC = w.Pool<CC>();
            w.Destroy();
            return (w, new WeakReference(g), new WeakReference(poolC), new WeakReference(f), new WeakReference(poolA));
        }
    }
}
