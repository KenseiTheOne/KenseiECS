// Runs a case through the model (contract) and the real runner side by side and reports every
// disagreement as a Violation with a rule id. A case stops at the first operation that violates
// anything: later state would only echo the same defect.
using System;
using System.Collections.Generic;
using System.Linq;
using KenseiECS;
using Stand.Runner.Kit;

namespace Stand.Runner {
    /// <summary> What a generated repro checks after the violating step. </summary>
    public sealed class ReproCheck {
        /// <summary> log | rethrow | aggregate | equal | same | throws | nothrow | build </summary>
        public string Kind;
        public string Pattern;
        public int OrigEntryId = -1;
        public string Expr;
        public string Expected;
        public string Note;
    }

    public sealed class Violation {
        public string Rule;
        /// <summary> Where in the runner contract it happened (operation, runner role and depth, kind of system); rule + place is the signature. </summary>
        public string Place;
        /// <summary> The case OpIndex refers to: the case as given, or (2.2.2) the case without its extra slots. </summary>
        public CaseSpec Spec;
        /// <summary> The model stays in step with the runner after this violation (it concerns no state the model tracks), so the case goes on. </summary>
        public bool KeepsModelInStep;
        /// <summary> Index of the violating operation; -1 = while building or right after it. </summary>
        public int OpIndex;
        public string Message;
        public ReproCheck Check;
    }

    public sealed class CaseResult {
        public readonly List<Violation> Violations = new List<Violation>();
        public bool NotApplicable;
        public string NotApplicableReason;
        public int OpsRun;
        /// <summary> 2.2.2: the second registration of a shared runner was rejected as required; the case ran on the tree without the extra slots. </summary>
        public bool SharedRejected;
    }

    public sealed class Ctx {
        public ContractProfile Profile;
        public bool Debug;
        /// <summary> The lifecycle model of the profile (2.1 or 2.2). </summary>
        public Contract Contract => Profile.Base;
        /// <summary> When set, Engine.Run tallies what the cases exercised (for the report's coverage table). </summary>
        public Coverage Coverage;
    }

    /// <summary> What the random cases actually exercised, so a report shows whether the tester reached the interesting paths. </summary>
    public sealed class Coverage {
        public readonly SortedDictionary<string, long> Counts = new SortedDictionary<string, long>();

        public void Add(string key, long n = 1) {
            Counts.TryGetValue(key, out long v);
            Counts[key] = v + n;
        }
    }

    public static class Engine {
        public static CaseResult Run(CaseSpec spec, Ctx ctx) {
            var result = new CaseResult();
            if (ctx.Profile.RejectSharedRunner && spec.HasSharedRunner()) {
                // 2.2.2: the second registration of a runner must throw. Check that on the tree as generated,
                // then run the case on the tree without the extra slots.
                bool rejected = CheckSharedRejection(spec, ctx, result);
                foreach (var v in result.Violations) {
                    v.Spec ??= spec;
                }
                if (!rejected) {
                    return result;
                }
                spec = spec.WithoutExtraSlots();
            }
            RunModel(spec, ctx, result);
            foreach (var v in result.Violations) {
                v.Spec ??= spec;
            }
            return result;
        }

        private static void RunModel(CaseSpec spec, Ctx ctx, CaseResult result) {
            var model = new Model(spec, ctx);
            var exec = new Exec(spec);
            exec.Build();
            if (exec.BuildError != null) {
                var entry = exec.BuildErrorEntry;
                if (entry != null && (entry.Kind == EntryKind.Group || entry.Kind == EntryKind.Phase) && spec.Appearances(entry.RunnerId) > 1) {
                    // Up to 2.2.1 a version may reject registering one runner twice; nothing documents either way.
                    result.NotApplicable = true;
                    result.NotApplicableReason = "registering runner r" + entry.RunnerId + " a second time threw " + Journal.Describe(exec.BuildError);
                    return;
                }
                result.Violations.Add(new Violation {
                    Rule = Rules.BuildThrew,
                    Place = "build:" + EntryKindName(entry),
                    OpIndex = -1,
                    Message = "registration of " + (entry != null ? entry.Label : "the tree") + " threw " + Journal.Describe(exec.BuildError),
                    Check = new ReproCheck { Kind = "build" }
                });
                return;
            }
            var probe = new Checker(model, exec, ctx, result);
            probe.StartState();
            probe.Snapshot(-1, buildTime: true, null);
            if (result.Violations.Any(v => !v.KeepsModelInStep)) {
                return;
            }
            var cov = ctx.Coverage;
            if (cov != null) {
                cov.Add("cases");
                cov.Add("tree entries", spec.AllEntries().Count());
                cov.Add("runners", spec.Runners.Count);
                if (spec.Runners.Any(r => spec.Appearances(r.Id) > 1)) {
                    cov.Add("cases with a runner in 2+ slots");
                }
                if (result.SharedRejected) {
                    cov.Add("cases whose second registration of a runner was rejected (2.2.2)");
                }
                if (spec.AllEntries().Any(x => x.Entry.Kind == EntryKind.Phase)) {
                    cov.Add("cases with phases");
                }
                if (spec.Runners.Any(r => r.OneFrames.Any(o => o.CleanWhenDisabled))) {
                    cov.Add("cases with cleanWhenDisabled");
                }
            }
            for (int i = 0; i < spec.Ops.Count; i++) {
                var op = spec.Ops[i];
                var expected = model.Apply(op);
                var actual = exec.Apply(op, expected);
                result.OpsRun = i + 1;
                if (cov != null) {
                    cov.Add("op " + op.Kind + (op.Kind == OpKind.Frame && expected.FrameSkipped ? " (skipped: not initialized)" : ""));
                    cov.Add("log tokens checked", expected.Log.Count);
                    if (expected.InitFailEntry >= 0) {
                        cov.Add("Init failures");
                        if (actual.DestroyErrors.Count > 0) {
                            cov.Add("Init failures with throwing rollback Destroy");
                        }
                        if (expected.Log.Any(t => t.Text.StartsWith("destroy:"))) {
                            cov.Add("Init failures that rolled something back");
                        }
                    }
                    if (expected.Forbidden.Count > 0) {
                        cov.Add("rollbacks passing an Init-less system");
                    }
                    if (expected.Log.Any(t => t.Optional)) {
                        cov.Add("rollbacks through a completed nested runner with an Init-less system");
                    }
                    if (expected.RootRunFailEntry >= 0 || expected.PhaseRunFailEntry.Count > 0) {
                        cov.Add("frames with a throwing Run");
                    }
                    if (expected.ExpectInvalidOperation) {
                        cov.Add("KENSEI_DEBUG throws expected");
                    }
                    cov.Add("observer checks", expected.SeeReasons.Count);
                    foreach (var reasons in expected.SeeReasons.Values) {
                        foreach (var reason in reasons) {
                            if (reason != null && reason.StartsWith("skip:")) {
                                cov.Add("observer counts after a skip cleanup");
                                break;
                            }
                        }
                    }
                }
                int before = result.Violations.Count;
                probe.CompareOp(i, op, expected, actual);
                probe.Snapshot(i, buildTime: false, op);
                if (result.Violations.Skip(before).Any(v => !v.KeepsModelInStep)) {
                    break;
                }
            }
        }

        public static string EntryKindName(EntrySpec e) {
            if (e == null) {
                return "tree";
            }
            switch (e.Kind) {
                case EntryKind.Leaf: return Probes.ClassName(e.Caps);
                case EntryKind.Group: return e.Name != null ? "named-group" : "group";
                case EntryKind.Phase: return "phase";
                default: return "DelHere";
            }
        }

        // 2.2.2: "registering one runner in two slots is forbidden in every build". Builds the tree as generated:
        // the first second registration must throw, and (a question) must leave the parent as it was.
        private static bool CheckSharedRejection(CaseSpec spec, Ctx ctx, CaseResult result) {
            var first = spec.FirstRegistrations();
            var firstExtra = spec.NestedRegistrationOrder().FirstOrDefault(e => first.TryGetValue(e.RunnerId, out var f) && !ReferenceEquals(f, e));
            if (firstExtra == null) {
                return true;
            }
            var exec = new Exec(spec);
            exec.Build();
            if (exec.BuildError == null) {
                var owner = spec.FindEntry(firstExtra.Id).Owner;
                var firstOwner = spec.FindEntry(first[firstExtra.RunnerId].Id).Owner;
                string place = "build:" + (firstExtra.Kind == EntryKind.Phase ? "phase" : "group") + "-after-" + (first[firstExtra.RunnerId].Kind == EntryKind.Phase ? "phase" : "group") +
                               (owner.Id == firstOwner.Id ? ":same-parent" : ":other-parent");
                result.Violations.Add(new Violation {
                    Rule = Rules.SharedRunnerAccepted,
                    Place = place,
                    OpIndex = -1,
                    Message = "runner r" + firstExtra.RunnerId + " was registered a second time (in r" + owner.Id + ", " + EntryKindName(firstExtra) + ") without an exception",
                    Check = new ReproCheck { Kind = "reject", Note = "r" + firstExtra.RunnerId }
                });
                return false;
            }
            var thrownAt = exec.BuildErrorEntry;
            bool isExtra = thrownAt != null && (thrownAt.Kind == EntryKind.Group || thrownAt.Kind == EntryKind.Phase) &&
                           first.TryGetValue(thrownAt.RunnerId, out var firstOfIt) && !ReferenceEquals(firstOfIt, thrownAt);
            if (!isExtra) {
                result.Violations.Add(new Violation {
                    Rule = Rules.BuildThrew,
                    Place = "build:" + EntryKindName(thrownAt),
                    OpIndex = -1,
                    Message = "registration of " + (thrownAt != null ? thrownAt.Label : "the tree") + " threw " + Journal.Describe(exec.BuildError),
                    Check = new ReproCheck { Kind = "build" }
                });
                return false;
            }
            result.SharedRejected = true;
            var parent = spec.FindEntry(thrownAt.Id).Owner;
            int before = parent.Entries.IndexOf(thrownAt);
            int after = exec.Runners[parent.Id].SystemCount;
            if (after != before) {
                result.Violations.Add(new Violation {
                    Rule = Rules.SharedRunnerRejectState,
                    Place = "build:" + (thrownAt.Kind == EntryKind.Phase ? "phase" : "group"),
                    KeepsModelInStep = true,
                    OpIndex = -1,
                    Message = "the rejected second registration of r" + thrownAt.RunnerId + " (" + Journal.Describe(exec.BuildError) + ") left r" + parent.Id + ".SystemCount at " + after +
                              ", " + before + " before the call",
                    Check = new ReproCheck { Kind = "reject-state", Expr = "r" + parent.Id + ".SystemCount", Expected = before.ToString() }
                });
            }
            return true;
        }

        private sealed class Checker {
            private readonly Model _m;
            private readonly Exec _x;
            private readonly Ctx _ctx;
            private readonly CaseResult _r;
            private readonly HashSet<string> _seenRules = new HashSet<string>();

            public Checker(Model m, Exec x, Ctx ctx, CaseResult r) {
                _m = m;
                _x = x;
                _ctx = ctx;
                _r = r;
            }

            private bool V22 => _ctx.Contract == Contract.V22;

            // Violations after which the model still matches the runner's state: an extra Destroy of an
            // Init-less system (the model keeps no state for it), the shape of an exception, a lookup result.
            private static readonly HashSet<string> InStep = new HashSet<string> {
                Rules.RollbackDestroysNoInit, Rules.RollbackAggregate, Rules.RollbackRethrow, Rules.InitException21,
                Rules.ConsistIndexOfName, Rules.ConsistIndexOfSystem, Rules.ReleaseResult, Rules.InitArgs, Rules.SharedProperty,
                Rules.SharedPropertyTwoSlots, Rules.GroupStartState, Rules.SharedRunnerRejectState
            };

            private void Add(string rule, int op, string message, ReproCheck check, string place) {
                // One violation per rule per case is enough.
                if (!_seenRules.Add(rule)) {
                    return;
                }
                _r.Violations.Add(new Violation { Rule = rule, Place = place, OpIndex = op, Message = message, Check = check, KeepsModelInStep = InStep.Contains(rule) });
            }

            /// <summary> "group:InitDestroy" (role of its runner, kind of system) for the system behind a log token such as "destroy:s3". </summary>
            private string WhereOfLabel(string label) {
                if (label == null || label.Length < 2 || label[0] != 's' || !int.TryParse(label.Substring(1), out int id) || !_m.Entries.TryGetValue(id, out var e)) {
                    return "?";
                }
                return e.Owner.Where + ":" + (e.IsLeaf ? Probes.ClassName(e.Spec.Caps) : e.TypeName);
            }

            private static string LabelOf(string token) {
                if (token == null) {
                    return null;
                }
                var parts = token.Split(':');
                return parts.Length >= 2 ? parts[1] : null;
            }

            private string WhereOfEntry(int entryId) {
                return _m.Entries.TryGetValue(entryId, out var e) ? e.Owner.Where : "?";
            }

            private static string After(OpSpec op) {
                return op == null ? "after-build" : "after-" + OpName(op);
            }

            /// <summary> The operation for a place: Warmup runs the same Init path as Init. </summary>
            private static string OpName(OpSpec op) {
                return op.Kind == OpKind.Warmup ? "Init" : op.Kind.ToString();
            }

            /// <summary>
            /// Place of a rule about inherited state (World, SharedData) for the system of an entry: the role of its runner,
            /// "+shared" when that runner sits in several slots (it inherits again at each), and the depth bucket d0/d1/d2+
            /// (a runner can only miss what its parent inherits later from depth 2 on).
            /// </summary>
            private string InheritedPlace(int entryId) {
                if (!_m.Entries.TryGetValue(entryId, out var e)) {
                    return "?";
                }
                return e.Owner.Where + (_m.IsShared(e.Owner) ? "+shared" : "") + ":" + (e.Owner.Depth <= 1 ? "d" + e.Owner.Depth : "d2+");
            }

            private string KindOf(MEntry e) {
                string k = e.IsLeaf ? Probes.ClassName(e.Spec.Caps) : e.IsPhase ? "phase" : e.IsGroup ? "group" : "DelHere";
                return k + (e.Child != null && _m.IsShared(e.Child) ? "+shared" : "");
            }

            // 2.2.1: "The slot starts as the group is at registration". A group switched off before AddGroup that
            // comes out of the registration enabled (group and slot agreeing) breaks that promise only; the model
            // takes the observed state over so that the case goes on.
            public void StartState() {
                if (!V22) {
                    return;
                }
                foreach (var mr in _m.Runners.Values) {
                    if (mr.IsRoot || !mr.Spec.StartDisabled || mr.Enabled) {
                        continue;
                    }
                    // The promise is about groups: a runner registered as a phase only keeps its own flag (checked as usual).
                    bool asGroup = _x.Spec.AllEntries().Any(p => p.Entry.RunnerId == mr.Id && p.Entry.Kind == EntryKind.Group);
                    var runner = _x.Runners[mr.Id];
                    if (!asGroup || !runner.IsEnabled) {
                        continue;
                    }
                    string rv = "r" + mr.Id;
                    Add(Rules.GroupStartState, -1, rv + ".SetActive(false) was called before its registration as a group, yet afterwards " + rv + ".IsEnabled is true",
                        Eq(rv + ".IsEnabled", false), "registration:" + mr.Where);
                    _m.AdoptEnabled(mr, true);
                }
            }

            // ------------------------------------------------------------- per-operation outcome

            public void CompareOp(int i, OpSpec op, Expectation exp, Actual act) {
                CompareLog(i, op, exp, act);
                CompareExceptions(i, op, exp, act);
                if (op.Kind == OpKind.Init || op.Kind == OpKind.Warmup) {
                    CompareInitArgs(i, act);
                }
                if (act.Tick != exp.Tick) {
                    Add(Rules.Tick, i, "world.Tick is " + act.Tick + ", the contract gives " + exp.Tick,
                        new ReproCheck { Kind = "equal", Expr = "world.Tick", Expected = exp.Tick.ToString() }, OpName(op) + ":" + (_x.PhaseOrder.Count > 0 ? "with-phases" : "root-only"));
                }
            }

            // Every system initialized in this step got the root's World and SharedData (no runner of the stand
            // is constructed with its own SharedData, so every nested runner inherits the root's: "every system
            // gets the same shared"), and the Shared property of the runner it sits in is that same object
            // (XML Shared: "Shared data passed to systems in Init").
            private void CompareInitArgs(int i, Actual act) {
                var shared = _x.Root.Shared;
                foreach (var kv in _x.Probes) {
                    var probe = kv.Value;
                    if (!act.Log.Contains("init:" + probe.Label)) {
                        continue;
                    }
                    bool worldOk = ReferenceEquals(probe.InitWorld, _x.World);
                    bool sharedOk = ReferenceEquals(probe.InitShared, shared);
                    if (!worldOk || !sharedOk) {
                        Add(Rules.InitArgs, i, probe.Label + ".Init got " + (worldOk ? "" : "another World ") + (sharedOk ? "" : (worldOk ? "" : "and ") + "another SharedData") +
                                               " than the root's",
                            new ReproCheck {
                                Kind = "equal", Expected = "true",
                                Expr = "ReferenceEquals(" + probe.Label + ".InitWorld, world) && ReferenceEquals(" + probe.Label + ".InitShared, r0.Shared)"
                            }, "Init:" + InheritedPlace(kv.Key));
                    }
                    var owner = _x.Spec.FindEntry(kv.Key).Owner;
                    var runner = owner != null ? _x.Runners[owner.Id] : null;
                    if (runner != null && probe.InitShared != null && !ReferenceEquals(runner.Shared, probe.InitShared)) {
                        string rv = "r" + owner.Id;
                        string place = "Init:" + InheritedPlace(kv.Key);
                        // A runner in two slots ("+shared") is a setup 2.2.0/2.2.1 do not describe and 2.2.2 forbids: its own rule.
                        Add(place.Contains("+shared") ? Rules.SharedPropertyTwoSlots : Rules.SharedProperty, i,
                            rv + ".Shared is not the SharedData its system " + probe.Label + " got in Init" +
                            (ReferenceEquals(probe.InitShared, shared) ? " (the system got the root's, " + rv + ".Shared is another object)" : ""),
                            Eq("ReferenceEquals(" + rv + ".Shared, " + probe.Label + ".InitShared)", true), place);
                    }
                }
            }

            private void CompareLog(int i, OpSpec op, Expectation exp, Actual act) {
                var filtered = new List<string>();
                foreach (var token in act.Log) {
                    if (exp.Forbidden.TryGetValue(token, out var forbidden)) {
                        Add(forbidden.Rule, i, "the step logged " + token + ", which the contract rules out; full log: [" + string.Join(" ", act.Log) + "]",
                            new ReproCheck { Kind = "log", Pattern = LogPattern.Format(exp.Log), Note = token + " must not appear" }, forbidden.Place);
                        continue;
                    }
                    filtered.Add(token);
                }
                var mismatch = LogPattern.Compare(exp.Log, filtered);
                if (mismatch == null) {
                    return;
                }
                string rule2 = ClassifyLog(op, exp, filtered, mismatch);
                string at = mismatch.Actual ?? mismatch.Expected?.Text;
                string tokenKind = at != null && at.Contains(':') ? at.Substring(0, at.IndexOf(':')) : "end";
                Add(rule2, i,
                    "log mismatch at expected #" + mismatch.ExpectedIndex + " (" + (mismatch.Expected?.ToString() ?? "end") + ") / actual #" +
                    mismatch.ActualIndex + " (" + (mismatch.Actual ?? "end") + "); expected [" + LogPattern.Format(exp.Log) + "], actual [" + string.Join(" ", act.Log) + "]",
                    new ReproCheck { Kind = "log", Pattern = LogPattern.Format(exp.Log) }, OpName(op) + ":" + tokenKind + "@" + WhereOfLabel(LabelOf(at)));
            }

            private string ClassifyLog(OpSpec op, Expectation exp, List<string> actual, LogMismatch m) {
                string a = m.Actual;
                string p = m.Expected?.Text;
                if (a != null && p != null && a.StartsWith("see:") && p.StartsWith("see:") &&
                    a.Substring(0, a.LastIndexOf(':')) == p.Substring(0, p.LastIndexOf(':'))) {
                    return ClassifyOneFrame(exp, m.ExpectedIndex, p, a);
                }
                string token = a ?? p;
                bool expectedLater = a != null && exp.Log.Skip(m.ExpectedIndex).Any(t => LogPattern.Matches(t.Text, a));
                bool actualLater = p != null && actual.Skip(m.ActualIndex).Any(t => LogPattern.Matches(p, t));
                switch (op.Kind) {
                    case OpKind.Init:
                    case OpKind.Warmup:
                        if ((p != null && p.StartsWith("init:")) || (a != null && a.StartsWith("init:"))) {
                            return Rules.InitSequence;
                        }
                        if (expectedLater || actualLater) {
                            return Rules.RollbackOrder;
                        }
                        if (p != null && p.StartsWith("destroy:")) {
                            return Rules.RollbackMissingDestroy;
                        }
                        return Rules.RollbackExtraDestroy;
                    case OpKind.Destroy:
                        if (exp.Log.Count == 0 && a != null) {
                            return Rules.DestroyNotNoop;
                        }
                        return Rules.DestroySequence;
                    case OpKind.Frame:
                        if (expectedLater || actualLater) {
                            return Rules.RunOrder;
                        }
                        if (a != null && (a.StartsWith("run:") || a.StartsWith("see:"))) {
                            return Rules.RunDisabledRan;
                        }
                        return Rules.RunEnabledSkipped;
                    default:
                        return Rules.UnexpectedCall;
                }
            }

            private string ClassifyOneFrame(Expectation exp, int expectedIndex, string p, string a) {
                var pc = p.Substring(p.LastIndexOf(':') + 1).Split(',');
                var ac = a.Substring(a.LastIndexOf(':') + 1).Split(',');
                exp.SeeReasons.TryGetValue(expectedIndex, out var reasons);
                for (int t = 0; t < pc.Length && t < ac.Length; t++) {
                    if (pc[t] == "?" || pc[t] == ac[t]) {
                        continue;
                    }
                    int pv = int.Parse(pc[t]);
                    int av = int.Parse(ac[t]);
                    if (av > pv) {
                        string kind = reasons != null && reasons[t] != null ? reasons[t].Split(':')[0] : "unknown";
                        return Rules.OneFramePrefix + "NOT-REMOVED-" + kind.ToUpperInvariant();
                    }
                    return Rules.OneFramePrefix + "REMOVED-TOO-EARLY";
                }
                return Rules.OneFramePrefix + "COUNT";
            }

            private void CompareExceptions(int i, OpSpec op, Expectation exp, Actual act) {
                switch (op.Kind) {
                    case OpKind.Init:
                    case OpKind.Warmup: {
                        if (exp.InitFailEntry < 0) {
                            if (act.Thrown != null) {
                                Add(Rules.UnexpectedPrefix + op.Kind.ToString().ToUpperInvariant(), i, op.Kind + " threw " + Journal.Describe(act.Thrown),
                                    new ReproCheck { Kind = "nothrow" }, OpName(op) + ":no-failure");
                            }
                            return;
                        }
                        var origin = _x.Probes[exp.InitFailEntry];
                        var original = origin.InitError;
                        string failPlace = OpName(op) + ":failure@" + WhereOfEntry(exp.InitFailEntry);
                        if (act.Thrown == null) {
                            Add(Rules.InitSwallowed, i, origin.Label + ".Init threw, but " + op.Kind + " returned normally",
                                new ReproCheck { Kind = "rethrow", OrigEntryId = exp.InitFailEntry }, failPlace);
                            return;
                        }
                        if (!V22) {
                            if (!ReferenceEquals(act.Thrown, original)) {
                                Add(Rules.InitException21, i, "expected the original exception, got " + Journal.Structure(act.Thrown),
                                    new ReproCheck { Kind = "rethrow", OrigEntryId = exp.InitFailEntry }, failPlace);
                            }
                            return;
                        }
                        if (act.DestroyErrors.Count == 0) {
                            if (!ReferenceEquals(act.Thrown, original)) {
                                Add(Rules.RollbackRethrow, i, "no Destroy threw during the rollback, yet " + op.Kind + " threw " + Journal.Structure(act.Thrown) +
                                                               " instead of the original " + Journal.Describe(original),
                                    new ReproCheck { Kind = "rethrow", OrigEntryId = exp.InitFailEntry }, failPlace);
                            }
                            return;
                        }
                        string problem = _x.J.ExpectAggregate(act.Thrown, original);
                        if (problem != null) {
                            Add(Rules.RollbackAggregate, i, problem,
                                new ReproCheck { Kind = "aggregate", OrigEntryId = exp.InitFailEntry }, failPlace);
                        }
                        return;
                    }
                    case OpKind.Frame: {
                        if (exp.FrameSkipped) {
                            return;
                        }
                        CompareRunThrow(i, "root.Run()", "root", exp.RootRunFailEntry, act.RootThrown);
                        foreach (var id in _x.PhaseOrder) {
                            exp.PhaseRunFailEntry.TryGetValue(id, out int fail);
                            act.PhaseThrown.TryGetValue(id, out var thrown);
                            CompareRunThrow(i, "r" + id + ".Run()", _m.Runners[id].Where, exp.PhaseRunFailEntry.ContainsKey(id) ? fail : -1, thrown);
                        }
                        return;
                    }
                    case OpKind.SetActiveIndex:
                    case OpKind.SetActiveName:
                    case OpKind.SetActiveSelf:
                    case OpKind.UnknownName: {
                        if (exp.ExceptionUnspecified) {
                            return;
                        }
                        string target = TargetOf(op);
                        if (exp.ExpectInvalidOperation) {
                            if (!(act.Thrown is InvalidOperationException)) {
                                Add(Rules.DebugCheckPrefix + exp.InvalidOperationRule.ToUpperInvariant(), i,
                                    "under KENSEI_DEBUG the call must throw InvalidOperationException, got " + Journal.Describe(act.Thrown),
                                    new ReproCheck { Kind = "throws" }, op.Kind + ":" + target);
                            }
                            return;
                        }
                        if (act.Thrown != null) {
                            Add(Rules.UnexpectedPrefix + op.Kind.ToString().ToUpperInvariant(), i, op.Kind + " threw " + Journal.Describe(act.Thrown),
                                new ReproCheck { Kind = "nothrow" }, op.Kind + ":" + target);
                            return;
                        }
                        if (exp.CheckReleaseResult && op.Kind == OpKind.UnknownName) {
                            if (op.Type == 1 && !(act.Result is bool b && b == false)) {
                                Add(Rules.ReleaseResult, i, "IsActive(unknown) returned " + act.Result, new ReproCheck {
                                    Kind = "equal", Expr = "r" + op.RunnerId + ".IsActive(\"" + Exec.UnknownName + "\")", Expected = "false"
                                }, "IsActive:" + target);
                            }
                            if (op.Type == 2 && act.Result != null) {
                                Add(Rules.ReleaseResult, i, "GetRunner(unknown) returned a runner", new ReproCheck {
                                    Kind = "equal", Expr = "r" + op.RunnerId + ".GetRunner(\"" + Exec.UnknownName + "\") == null", Expected = "true"
                                }, "GetRunner:" + target);
                            }
                        }
                        return;
                    }
                    case OpKind.Destroy:
                        if (act.Thrown != null) {
                            Add(Rules.UnexpectedPrefix + "DESTROY", i, "Destroy threw " + Journal.Describe(act.Thrown), new ReproCheck { Kind = "nothrow" }, "Destroy");
                        }
                        return;
                }
            }

            private void CompareRunThrow(int i, string what, string where, int failEntry, Exception thrown) {
                if (failEntry < 0) {
                    if (thrown != null) {
                        Add(Rules.UnexpectedPrefix + "RUN", i, what + " threw " + Journal.Describe(thrown), new ReproCheck { Kind = "nothrow", Note = what }, "Frame:run@" + where);
                    }
                    return;
                }
                var original = _x.Probes[failEntry].RunError;
                if (!ReferenceEquals(thrown, original)) {
                    Add(Rules.RunException, i, what + ": expected the exception of " + _x.Probes[failEntry].Label + ".Run to propagate as is, got " + Journal.Structure(thrown),
                        new ReproCheck { Kind = "same-run", Note = what, OrigEntryId = failEntry }, "Frame:run@" + where + ":failure@" + WhereOfEntry(failEntry));
                }
            }

            /// <summary> What a SetActive/IsActive/GetRunner step addresses: the kind of system, or "unknown-name". </summary>
            private string TargetOf(OpSpec op) {
                switch (op.Kind) {
                    case OpKind.SetActiveIndex:
                    case OpKind.SetActiveName:
                        return _m.Entries.TryGetValue(op.EntryId, out var e) ? KindOf(e) : "?";
                    case OpKind.SetActiveSelf:
                        return _m.Runners.TryGetValue(op.RunnerId, out var r) ? r.Where : "?";
                    default:
                        return "unknown-name";
                }
            }

            // ------------------------------------------------------------- introspection

            public void Snapshot(int i, bool buildTime, OpSpec op) {
                foreach (var mr in _m.Runners.Values) {
                    var runner = _x.Runners[mr.Id];
                    string rv = "r" + mr.Id;
                    bool shared = _m.IsShared(mr);
                    if (runner.IsInitialized != mr.Initialized) {
                        Add(Rules.InitState, i, rv + ".IsInitialized is " + runner.IsInitialized + ", the contract gives " + mr.Initialized,
                            Eq(rv + ".IsInitialized", mr.Initialized), "IsInitialized@" + mr.Where + ":" + After(op));
                    }
                    if (runner.IsEnabled != mr.Enabled) {
                        Add(shared ? Rules.SharedSlots : Rules.ConsistChildEnabled, i,
                            rv + ".IsEnabled is " + runner.IsEnabled + ", the contract gives " + mr.Enabled + (shared ? " (runner sits in " + mr.Appearances + " slots)" : ""),
                            Eq(rv + ".IsEnabled", mr.Enabled), "IsEnabled@" + mr.Where + ":" + After(op));
                    }
                    if (buildTime && !ReferenceEquals(runner.World, _x.World)) {
                        Add(Rules.InfoStatic, i, rv + ".World is not the world it was constructed with", Eq("ReferenceEquals(" + rv + ".World, world)", true), "World@" + mr.Where);
                    }
                    if (runner.SystemCount != mr.Entries.Count) {
                        Add(Rules.InfoStatic, i, rv + ".SystemCount is " + runner.SystemCount + ", " + mr.Entries.Count + " registered",
                            Eq(rv + ".SystemCount", mr.Entries.Count), "SystemCount@" + mr.Where);
                        continue;
                    }
                    for (int k = 0; k < mr.Entries.Count; k++) {
                        CheckEntry(i, buildTime, mr, runner, k, op);
                    }
                }
            }

            private void CheckEntry(int i, bool buildTime, MRunner mr, SystemsRunner runner, int k, OpSpec op) {
                var e = mr.Entries[k];
                string rv = "r" + mr.Id;
                var info = runner.GetSystemInfo(k);
                string at = rv + ".GetSystemInfo(" + k + ")";
                bool sharedChild = _m.IsShared(e.Child);

                string kind = KindOf(e);
                if (e.IsDelHere ? info.System == null : !ReferenceEquals(info.System, _x.Systems[e.Spec.Id])) {
                    Add(Rules.InfoStatic, i, at + ".System is not the registered instance", Eq(at + ".System == " + VarOf(e), true), "SystemInfo.System@" + kind);
                }
                if (info.Name != e.InfoName) {
                    Add(Rules.InfoStatic, i, at + ".Name is \"" + info.Name + "\", expected \"" + e.InfoName + "\"", EqS(at + ".Name", e.InfoName), "SystemInfo.Name@" + kind);
                }
                if (info.IsRunnable != e.Runnable) {
                    Add(Rules.InfoStatic, i, at + ".IsRunnable is " + info.IsRunnable + ", expected " + e.Runnable, Eq(at + ".IsRunnable", e.Runnable), "SystemInfo.IsRunnable@" + kind);
                }
                bool enabled = _m.InfoEnabled(e);
                if (info.IsEnabled != enabled) {
                    Add(sharedChild ? Rules.SharedSlots : Rules.ConsistInfoEnabled, i,
                        at + ".IsEnabled is " + info.IsEnabled + ", the contract gives " + enabled +
                        (e.Child != null ? " (r" + e.Child.Id + ".IsEnabled is " + _x.Runners[e.Child.Id].IsEnabled + (sharedChild ? ", runner sits in " + e.Child.Appearances + " slots" : "") + ")" : ""),
                        Eq(at + ".IsEnabled", enabled), "SystemInfo.IsEnabled@" + kind + ":" + After(op));
                }
                var expectedChild = e.Child != null ? _x.Runners[e.Child.Id] : null;
                if (!ReferenceEquals(info.ChildRunner, expectedChild)) {
                    Add(Rules.InfoStatic, i, at + ".ChildRunner is not the nested runner", Eq(at + ".ChildRunner == " + (e.Child != null ? "r" + e.Child.Id : "null"), true), "SystemInfo.ChildRunner@" + kind);
                }
                if (info.IsSeparatePhase != e.IsPhase) {
                    Add(Rules.InfoStatic, i, at + ".IsSeparatePhase is " + info.IsSeparatePhase + ", expected " + e.IsPhase, Eq(at + ".IsSeparatePhase", e.IsPhase), "SystemInfo.IsSeparatePhase@" + kind);
                }

                string name = e.IsDelHere ? null : e.Spec.Name;
                if (name != null) {
                    if (e.Runnable || e.IsPhase) {
                        bool active = false;
                        var thrown = Journal.Catch(() => active = runner.IsActive(name));
                        if (thrown != null) {
                            Add(Rules.UnexpectedPrefix + "ISACTIVE", i, rv + ".IsActive(\"" + name + "\") threw " + Journal.Describe(thrown),
                                new ReproCheck { Kind = "nothrow", Expr = rv + ".IsActive(\"" + name + "\")" }, "IsActive@" + kind);
                        } else if (active != enabled) {
                            Add(sharedChild ? Rules.SharedSlots : Rules.ConsistIsActive, i,
                                rv + ".IsActive(\"" + name + "\") is " + active + ", the contract gives " + enabled,
                                Eq(rv + ".IsActive(\"" + name + "\")", enabled), "IsActive@" + kind + ":" + After(op));
                        }
                    } else if (buildTime && V22) {
                        // Named Init/Destroy-only system: IsActive throws under KENSEI_DEBUG, returns false in release (2.2 XML IsActive).
                        bool active = true;
                        var thrown = Journal.Catch(() => active = runner.IsActive(name));
                        if (_ctx.Debug && !(thrown is InvalidOperationException)) {
                            Add(Rules.DebugCheckPrefix + "ISACTIVE-NOT-RUNNABLE", i, rv + ".IsActive(\"" + name + "\") on an Init/Destroy-only system must throw under KENSEI_DEBUG, got " + Journal.Describe(thrown),
                                new ReproCheck { Kind = "throws", Expr = rv + ".IsActive(\"" + name + "\")" }, "IsActive@" + kind);
                        } else if (!_ctx.Debug && (thrown != null || active)) {
                            Add(Rules.ConsistIsActive, i, rv + ".IsActive(\"" + name + "\") on an Init/Destroy-only system must return false in release, got " + (thrown != null ? Journal.Describe(thrown) : "true"),
                                Eq(rv + ".IsActive(\"" + name + "\")", false), "IsActive@" + kind);
                        }
                    }
                    if (e.Child != null) {
                        SystemsRunner got = null;
                        var thrown = Journal.Catch(() => got = runner.GetRunner(name));
                        if (thrown != null || !ReferenceEquals(got, expectedChild)) {
                            Add(Rules.ConsistGetRunner, i, rv + ".GetRunner(\"" + name + "\") " + (thrown != null ? "threw " + Journal.Describe(thrown) : "returned another runner"),
                                Eq(rv + ".GetRunner(\"" + name + "\") == r" + e.Child.Id, true), "GetRunner@" + kind);
                        }
                    }
                }
                if (V22 && Api.HasIndexOf) {
                    // IndexOf(name): an explicit name resolves to its entry; an unnamed entry's SystemInfo.Name
                    // falls back to the first unnamed entry with that name (no explicit name equals a type name here).
                    // DelHere names ("DelHere<T>") are generated by the runner: whether they count as registration
                    // names is not documented (contract question Q-DELHERE-NAME), so they are not checked.
                    // A runner in several slots: SetActive(name) resolves the name to the runner, not to a slot,
                    // so any slot of that runner is accepted (contract question Q-SHARED-INDEXOF).
                    string lookup = name ?? e.InfoName;
                    int expectedIndex = name != null ? k : mr.Entries.FindIndex(x => !x.IsDelHere && x.Spec.Name == null && x.InfoName == lookup);
                    int got = -2;
                    var thrown = e.IsDelHere ? null : Journal.Catch(() => got = Api.IndexOfNameFn(runner, lookup));
                    bool acceptable = got == expectedIndex ||
                                      (name != null && sharedChild && got >= 0 && got < mr.Entries.Count && ReferenceEquals(mr.Entries[got].Child, e.Child));
                    if (!e.IsDelHere && (thrown != null || !acceptable)) {
                        Add(Rules.ConsistIndexOfName, i, rv + ".IndexOf(\"" + lookup + "\") " + (thrown != null ? "threw " + Journal.Describe(thrown) : "is " + got) + ", expected " + expectedIndex,
                            Eq(rv + ".IndexOf(\"" + lookup + "\")", expectedIndex), "IndexOf(" + (name != null ? "name" : "type-name") + ")@" + kind);
                    }
                    if (!e.IsDelHere) {
                        var instance = _x.Systems[e.Spec.Id];
                        var slots = new List<int>();
                        for (int q = 0; q < mr.Entries.Count; q++) {
                            if (!mr.Entries[q].IsDelHere && ReferenceEquals(_x.Systems[mr.Entries[q].Spec.Id], instance)) {
                                slots.Add(q);
                            }
                        }
                        int byRef = Api.IndexOfSystemFn(runner, instance);
                        if (!slots.Contains(byRef)) {
                            Add(Rules.ConsistIndexOfSystem, i, rv + ".IndexOf(" + VarOf(e) + ") is " + byRef + ", registered at " + string.Join("/", slots),
                                Eq(rv + ".IndexOf(" + VarOf(e) + ")", slots[0]), "IndexOf(system)@" + kind);
                        }
                    }
                }
            }

            private static string VarOf(MEntry e) {
                return e.Child != null ? "r" + e.Child.Id : e.Label;
            }

            private static ReproCheck Eq(string expr, bool expected) {
                return new ReproCheck { Kind = "equal", Expr = expr, Expected = expected ? "true" : "false" };
            }

            private static ReproCheck Eq(string expr, int expected) {
                return new ReproCheck { Kind = "equal", Expr = expr, Expected = expected.ToString() };
            }

            private static ReproCheck EqS(string expr, string expected) {
                return new ReproCheck { Kind = "equal", Expr = expr, Expected = "\"" + expected + "\"" };
            }
        }
    }
}
