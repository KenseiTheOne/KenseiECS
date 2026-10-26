using System;
using System.Collections.Generic;
using System.Linq;

namespace KenseiStand.FuzzCore {
    public sealed class RunOptions {
        public bool CrashMode;          // model first (termination guard), then the real world uncapped; no comparison
        public bool Guard = true;       // crash mode: stop a runaway of the real world in-process (rule C-runaway)
        public int GuardDepth = 256;    // crash mode: reaction depth of the real world that counts as a runaway
        public long GuardFires = 200000;
        public bool KeepLogs;           // keep the full logs of every op (repro writer)
        public int StopAfterOp = -1;    // stop after this op index (repro writer)
        public List<OpTrace> Trace;     // filled when KeepLogs
        public List<string> Readings;   // contract questions read the alternative way (ModelSide.Readings)
        public bool CheckReadings = true;   // on a failure at a mirror point, re-run with its alternative reading

        public RunOptions CloneFor(List<string> readings) => new RunOptions {
            CrashMode = CrashMode, Guard = Guard, GuardDepth = GuardDepth, GuardFires = GuardFires,
            StopAfterOp = StopAfterOp, Readings = readings, CheckReadings = false,
            KeepLogs = KeepLogs, Trace = Trace != null ? new List<OpTrace>() : null,
        };
    }

    public sealed class OpTrace {
        public int Index;
        public string Op;
        public string RealOutcome;
        public string ModelOutcome;
        public List<string> RealLog;
        public List<string> ModelLog;
        public List<string> RealSnapshot;
        public List<string> ModelSnapshot;
        public List<string> Internal;
    }

    public static class Executor {
        public static RunResult Run(Scenario scn, RunOptions opt = null) {
            opt ??= new RunOptions();
            TypeMap.Init(scn.Layout);
            if (opt.CrashMode) {
                return RunCrashMode(scn, opt);
            }
            var r = RunCompare(scn, opt);
            if (opt.CheckReadings) {
                r = Explain(scn, opt, r, new List<string>(opt.Readings ?? new List<string>()), 0);
            }
            return r;
        }

        // A failure of a check on the real world alone (callback checks, consistency) is not a matter of reading.
        private static bool Readable(RunResult r) =>
            r.Status == "fail" && r.FindingStatus != "regression" && r.Stage != 0 && r.Stage != 3 && r.Mirrors != null && r.Mirrors.Count > 0;

        // The failure r (found under the readings in `already`) comes after points where the model mirrors contract-silent
        // behaviour of the reference implementation. If the alternative reading of one of those questions (or of all of them)
        // gets the scenario past this operation, the world behaves differently from the reference where the contract is
        // silent: a regression to look at, not a defect. The real world is the same in every replay, so a model that
        // follows it there under a contract-compliant reading shows that no promise is broken. When a reading explains the
        // first difference and something else fails later in the same operation, that later failure is the finding, unless
        // it rests on another mirrored question in turn (two readings at once, depth 2).
        private static RunResult Explain(Scenario scn, RunOptions opt, RunResult r, List<string> already, int depth) {
            if (!Readable(r)) {
                return r;
            }
            var fresh = r.Mirrors.Where(m => !already.Contains(m)).ToList();
            if (fresh.Count == 0) {
                return r;
            }
            var tries = fresh.Select(m => new List<string>(already) { m }).ToList();
            if (fresh.Count > 1) {
                tries.Add(new List<string>(already.Concat(fresh)));
            }
            foreach (var readings in tries) {
                RunResult alt;
                var altOpt = opt.CloneFor(readings);
                try {
                    alt = RunCompare(scn, altOpt);
                } catch (Exception) {
                    alt = null;
                }
                if (alt == null) {
                    continue;
                }
                string why = "the alternative reading of " + string.Join(" and ", readings) + " explains the first difference (" + r.Sig +
                             "): the world differs from the reference implementation where the contract is silent";
                if (alt.Status != "fail" || alt.OpIndex > r.OpIndex) {
                    r.FindingStatus = "regression";
                    r.RegressionReason = why;
                    return r;
                }
                if (alt.OpIndex == r.OpIndex && Later(alt, r)) {
                    // At the original position the mirror point is the same occurrence of the question (its fact is
                    // recorded under either reading); only a later failure can sit at another occurrence of it.
                    if (alt.Pos >= 0 && FirstPos(alt) > FirstPos(r) && alt.MirrorAt != null
                        && alt.MirrorAt.Any(m => m.pos == alt.Pos && readings.Contains(m.question))) {
                        // The later failure is right at another occurrence of the same question: the world follows
                        // neither reading throughout. Still contract-silent territory.
                        r.FindingStatus = "regression";
                        r.RegressionReason = why + "; it follows neither reading of " + string.Join(", ", readings) + " throughout";
                        return r;
                    }
                    // The reading explains the first difference, and something else fails later in the same operation:
                    // that later failure is the finding (under a contract-compliant reading of the silent part).
                    alt.Notes = new List<string>(alt.Notes ?? new List<string>()) { "before it: " + why };
                    alt.Readings = readings;
                    if (opt.Trace != null && altOpt.Trace != null) {
                        // The repro of this failure asserts what the model does under that reading.
                        opt.Trace.Clear();
                        opt.Trace.AddRange(altOpt.Trace);
                    }
                    if (depth < 2) {
                        // The later failure may come right after another mirrored question (Q-stale-world-event, then
                        // Q-created-after-death): read that one the other way too, on top of these readings.
                        return Explain(scn, opt, alt, readings, depth + 1);
                    }
                    return alt;
                }
            }
            return r;
        }

        // The first log position at which a failure's operation differs from the model: the callback check's or the
        // event log's position, for an outcome difference the first difference of the logs before it (the outcome is
        // checked first, but a log that differs earlier is the earlier symptom), otherwise after every callback.
        private static int FirstPos(RunResult x) {
            if (x.Pos >= 0 && (x.Stage == 0 || x.Stage == 2 || x.Stage == 6)) {
                return x.Pos;
            }
            if (x.Stage == 1 && x.LogDiff >= 0) {
                return x.LogDiff;
            }
            return int.MaxValue;
        }

        private static string LineAt(List<string> log, int k) => log != null && k >= 0 && k < log.Count ? log[k] : null;

        // Whether the failure under an alternative reading (a) lies past the original first difference (b), that is the
        // reading explains it and the real world goes on to fail later. At the same log position it does when the reading
        // changed what the model expects there and another rule fails: the world follows neither reading at that point
        // (an OnEntityCreated the mirrored reading sends, and an exception the world throws instead of either), and the
        // difference under the contract-compliant reading is the finding.
        private static bool Later(RunResult a, RunResult b) {
            int pa = FirstPos(a), pb = FirstPos(b);
            if (pa != pb) {
                return pa > pb;
            }
            if (pa == int.MaxValue) {
                return a.Stage > b.Stage;
            }
            return a.Rule != b.Rule && LineAt(a.ModelLog, pa) != LineAt(b.ModelLog, pb);
        }

        private static RunResult RunCompare(Scenario scn, RunOptions opt) {
            var real = new RealSide(scn);
            var model = new ModelSide(scn, Build.Debug);
            if (opt.Readings != null) {
                model.Readings.UnionWith(opt.Readings);
            }
            model.DyingOpsAllowed = real.DyingOpsAllowed;
            var result = new RunResult { Status = "pass" };
            real.Setup();
            model.Setup();
            var notes = new List<string>();

            var first = CompareSnapshots(real.Snapshot(), model.Snapshot());
            if (first != null) {
                return Fail(result, -1, "setup", first.Value.cls, first.Value.detail, null, null, model, notes, real, -1, "state", 5);
            }

            for (int i = 0; i < scn.Ops.Count; i++) {
                var op = scn.Ops[i];
                string text = i + ": " + op;
                real.ClearLog();
                model.ClearLog();
                real.Hints = new Hints();
                real.BeginOp(text);
                real.ResetOpTracking();
                model.BeginOp(text);
                model.WarmupTempDiedAt = -1;
                if (op.K == "clipEvent") {
                    op = op.Clone();
                    op.Stale = model.ClipStale(op.Slot);
                }
                string ro = Exec(real, op);
                model.Hints = real.Hints;
                string mo = Exec(model, op);
                foreach (var q in real.Questions) {
                    model.Questions.Add(q);
                }
                result.OpsRun = i + 1;
                result.Events = real.EventsTotal;

                OpTrace trace = null;
                if (opt.KeepLogs) {
                    trace = new OpTrace {
                        Index = i, Op = op.ToString(), RealOutcome = ro, ModelOutcome = mo,
                        RealLog = new List<string>(real.Log), ModelLog = new List<string>(model.Log),
                    };
                    opt.Trace?.Add(trace);
                }

                // 0. What a callback promised at the moment it was called (real side, independent of the model),
                //    when it is the earliest symptom of this operation.
                MaskTainted(real.Log, model.Log);
                int k0 = FirstLogDifference(real.Log, model.Log);
                if (real.CbViolations.Count > 0 && (k0 < 0 || real.CbViolations[0].at <= k0)) {
                    var v = real.CbViolations[0];
                    var more = real.CbViolations.Skip(1).Take(5).Select(x => x.rule + ": " + x.detail);
                    var r = Fail(result, i, text, v.rule, v.detail + (real.CbViolations.Count > 1 ? "\n" + string.Join("\n", more) : "") +
                        Context(real.Log, model.Log, Math.Min(v.at, Math.Max(real.Log.Count - 1, 0))), real.Log, model.Log, model, notes,
                        real, v.at, FrameAt(real, model, v.at), 0);
                    FillTrace(trace, real, model, notes);
                    return r;
                }
                real.CbViolations.Clear();

                // 1. Outcome of the top-level operation (exception or not).
                if (Norm(ro) != Norm(mo)) {
                    string cls = OutcomeClass(ro, mo);
                    var r = Fail(result, i, text, cls,
                        "operation outcome: real " + ro + ", model (contract) " + mo, real.Log, model.Log, model, notes, real, -1, "top", 1);
                    // The logs may differ before the outcome does: that position decides whether an alternative reading
                    // explains the failure (Executor.Later).
                    r.LogDiff = k0;
                    FillTrace(trace, real, model, notes);
                    return r;
                }

                // 2. Event log: callbacks, writes, nested operation outcomes, iteration visits.
                MaskTainted(real.Log, model.Log);
                int k = FirstLogDifference(real.Log, model.Log);
                if (k >= 0 && op.K == "warmup" && model.WarmupTempDiedAt >= 0 && k >= model.WarmupTempDiedAt
                    && k < real.Log.Count && real.CbInfo.TryGetValue(k, out var info)
                    && info.state == "alive" && !real.CreatedThisOp.Contains(info.handle)) {
                    // After a listener destroyed Warmup's temporary entity the model stops; the contract does not
                    // say what Warmup does then. A notification about an alive entity that no listener created
                    // (a fresh temporary entity) is a legitimate reading of it, not a finding.
                    model.Questions.Add("Q-warmup-temp-destroyed: what Warmup does after a listener destroyed its temporary entity " +
                                        "(stop, or continue with a new one) is not documented; the model stops");
                    result.Status = "discard";
                    result.OpIndex = i;
                    result.OpText = text;
                    result.Detail = "warmup continued with an alive entity after its temporary entity was destroyed: " + real.Log[k];
                    result.Questions = model.Questions.ToList();
                    result.Notes = notes;
                    return result;
                }
                (string rule, string detail, int at, string ctx)? reorder = null;
                if (k >= 0) {
                    var (cls, detail) = ClassifyLog(real.Log, model.Log, k);
                    if (cls.StartsWith("N-order:") && k < real.Log.Count && k < model.Log.Count && SameMultiset(real.Log, model.Log)
                        && Category(real.Log[k]) == Category(model.Log[k])) {
                        // The same notifications, only in another order within one kind of callback (two filters, two
                        // listeners of a pool, the drain order of components). architecture.md describes that order, the
                        // public docs do not promise it: a behaviour change, unless the state differs too (checked below).
                        reorder = ("N-reorder:" + cls.Substring("N-order:".Length), "the same notifications in another order: " + detail, k, FrameAt(real, model, k));
                    } else {
                        var r = Fail(result, i, text, cls, detail, real.Log, model.Log, model, notes, real, k, FrameAt(real, model, k), 2);
                        FillTrace(trace, real, model, notes);
                        return r;
                    }
                }

                // 3. Internal consistency of the real world (independent of the model).
                var opNotes = new List<string>();
                var errs = real.CheckInternal(opNotes);
                foreach (var n in opNotes) {
                    if (!notes.Contains(n)) {
                        notes.Add(n);
                    }
                }
                if (trace != null) {
                    trace.Internal = errs;
                }
                if (errs.Count > 0) {
                    string rule = errs[0].Split('|')[0];
                    var r = Fail(result, i, text, rule, string.Join("\n", errs.Take(8)), real.Log, model.Log, model, notes, real, -1, "state", 3);
                    FillTrace(trace, real, model, notes);
                    return r;
                }

                // 4. Contract violations the model reproduced on purpose (confirmed by the matching logs).
                if (model.Anomalies.Count > 0) {
                    var a = model.Anomalies[0].Split('|');
                    int at = model.Facts.Where(f => f.Key == "dying-enter").Select(f => f.Pos).DefaultIfEmpty(-1).First();
                    var r = Fail(result, i, text, a[0], a.Length > 1 ? a[1] : a[0], real.Log, model.Log, model, notes, real, -1,
                        at >= 0 ? FrameAt(real, model, at) : "top", 4);
                    FillTrace(trace, real, model, notes);
                    return r;
                }

                // 5. World state against the model.
                var rs = real.Snapshot();
                var ms = model.Snapshot();
                MaskTaintedSnapshot(rs, ms);
                if (trace != null) {
                    trace.RealSnapshot = rs;
                    trace.ModelSnapshot = ms;
                }
                var diff = CompareSnapshots(rs, ms);
                if (diff != null) {
                    var r = Fail(result, i, text, diff.Value.cls, diff.Value.detail, real.Log, model.Log, model, notes, real, -1, "state", 5);
                    return r;
                }
                if (reorder != null) {
                    var ro2 = reorder.Value;
                    var r = Fail(result, i, text, ro2.rule, ro2.detail, real.Log, model.Log, model, notes, real, ro2.at, ro2.ctx, 6);
                    r.FindingStatus = "regression";
                    r.RegressionReason = "the same notifications arrive in another order within one kind of callback (the order is described in architecture.md, not promised by the public docs)";
                    return r;
                }
                if (opt.StopAfterOp >= 0 && i >= opt.StopAfterOp) {
                    break;
                }
            }
            result.Questions = model.Questions.ToList();
            result.Notes = notes;
            return result;
        }

        private static void FillTrace(OpTrace trace, RealSide real, ModelSide model, List<string> notes) {
            if (trace == null) {
                return;
            }
            try {
                trace.RealSnapshot = real.Snapshot();
            } catch (Exception ex) {
                trace.RealSnapshot = new List<string> { "snapshot threw " + ex.GetType().Name };
            }
            trace.ModelSnapshot = model.Snapshot();
            try {
                trace.Internal ??= real.CheckInternal(null);
            } catch (Exception ex) {
                trace.Internal = new List<string> { "check threw " + ex.GetType().Name };
            }
        }

        // Crash mode: the model runs the whole scenario first with a runaway guard; if it terminates,
        // the real world runs it with uncapped reactions. A process crash (stack overflow) or a hang is
        // what this mode looks for; the driver sees it from outside.
        // With opt.Guard the real world's reactions stop at GuardDepth nested callbacks (or GuardFires per
        // op) and the run fails with C-runaway, in-process: that is how crashes are shrunk. Without the
        // guard nothing stops the recursion, and a stack overflow kills the process (the driver confirms
        // crashes that way).
        private static RunResult RunCrashMode(Scenario scn, RunOptions opt) {
            var result = new RunResult { Status = "pass" };
            var model = new ModelSide(scn, Build.Debug) { EnforceHardCap = true };
            model.DyingOpsAllowed = Internals.CanDetectDying;
            model.Setup();
            var modelDepth = new int[scn.Ops.Count];
            var modelFires = new long[scn.Ops.Count];
            var modelFacts = new List<Fact>[scn.Ops.Count];
            for (int i = 0; i < scn.Ops.Count; i++) {
                model.BeginOp(i + ": " + scn.Ops[i]);
                model.ClearLog();
                Exec(model, scn.Ops[i]);
                modelDepth[i] = model.MaxDepthThisOp;
                modelFires[i] = model.FiresThisOp;
                modelFacts[i] = new List<Fact>(model.Facts);
                if (model.Runaway) {
                    result.Status = "discard";
                    result.Detail = "model runaway at op " + i + ": the scripted reactions do not terminate under the contract";
                    return result;
                }
            }
            var real = new RealSide(scn);
            if (opt.Guard) {
                real.EnforceHardCap = true;
                real.HardDepth = opt.GuardDepth;
                real.HardFires = opt.GuardFires;
            } else {
                Console.Out.Flush();
                Console.Error.WriteLine("crash-mode: model terminated, running the real world uncapped");
                Console.Error.Flush();
            }
            real.Setup();
            for (int i = 0; i < scn.Ops.Count; i++) {
                string text = i + ": " + scn.Ops[i];
                real.BeginOp(text);
                real.ClearLog();
                if (!opt.Guard) {
                    Console.Error.WriteLine("crash-mode: op " + text);
                    Console.Error.Flush();
                }
                string ro = Exec(real, scn.Ops[i]);
                result.OpsRun = i + 1;
                result.Events = real.EventsTotal;
                if (opt.KeepLogs) {
                    opt.Trace?.Add(new OpTrace { Index = i, Op = scn.Ops[i].ToString(), RealOutcome = ro, RealLog = new List<string>(real.Log) });
                }
                if (real.Runaway) {
                    result.Status = "fail";
                    result.OpIndex = i;
                    result.OpText = text;
                    result.Rule = "C-runaway";
                    result.Class = Classifier.ClassOf(result.Rule);
                    string opKind = scn.Ops[i].K;
                    result.Sig = result.Rule + "@" + opKind;
                    result.Detail = "the real world's listener recursion did not end: it passed " + opt.GuardDepth + " nested callbacks or " +
                                    opt.GuardFires + " callbacks in this operation, while the reference model finished it with depth " +
                                    modelDepth[i] + " and " + modelFires[i] + " reactions (uncapped reactions; without the guard the process dies with a stack overflow)";
                    result.Questions = model.Questions.ToList();
                    result.Notes = new List<string>();
                    Attribute(result, opKind, "top", modelFacts[i], 1);
                    return result;
                }
            }
            return result;
        }

        // window: the log position of the first difference (facts recorded after it are not part of the mechanism),
        // or -1 for a difference found after the operation (outcome, state): every fact of the operation counts.
        private static RunResult Fail(RunResult r, int opIndex, string opText, string cls, string detail,
            List<string> realLog, List<string> modelLog, ModelSide model, List<string> notes, RealSide real, int window, string ctx, int stage) {
            r.Status = "fail";
            r.OpIndex = opIndex;
            r.OpText = opText;
            r.Class = Classifier.ClassOf(cls);
            r.Rule = cls;
            string opKind = opText;
            int colon = opText.IndexOf(": ", StringComparison.Ordinal);
            if (colon >= 0) {
                opKind = opText.Substring(colon + 2).Split(' ')[0];
            }
            r.Detail = detail;
            r.Stage = stage;
            r.Pos = window;
            r.RealLog = realLog == null ? null : new List<string>(realLog);
            r.ModelLog = modelLog == null ? null : new List<string>(modelLog);
            r.Questions = model.Questions.ToList();
            r.Notes = notes;
            var facts = Window(model.Facts, window);
            if (real != null) {
                facts.AddRange(Window(real.Facts, window));
            }
            Attribute(r, opKind, ctx, facts, stage, KindsAt(realLog, modelLog, window));
            return r;
        }

        // The callback kinds of the real and the model line at log position k (none for a check after the operation).
        private static List<string> KindsAt(List<string> real, List<string> model, int k) {
            var kinds = new List<string>();
            if (k < 0) {
                return kinds;
            }
            foreach (var log in new[] { real, model }) {
                if (log != null && k < log.Count && IsCallback(log[k])) {
                    kinds.Add(CallbackKind(log[k]));
                }
            }
            return kinds;
        }

        // The facts that came before the first difference at log position k (recorded before line k, or right at k: after
        // line k - 1 with no line in between). Of the families they show, the classifier takes the earliest one.
        private static List<Fact> Window(List<Fact> all, int k) =>
            k < 0 ? new List<Fact>(all) : all.Where(f => f.Pos <= k).ToList();

        // Mechanism of a failure: its facts, the claims it rests on, its family, its status and its signature
        // (rule @ operation [reaction it happened in] {facts}). The callback checks (stage 0) and the consistency
        // checks (stage 3) look at the real world alone, so they rest on their rule's claims only; a difference from the
        // model also rests on the claims behind the model's decisions before it.
        private static void Attribute(RunResult r, string opKind, string ctx, List<Fact> facts, int stage, List<string> atFirst = null) {
            var keys = new SortedSet<string>(facts.Where(f => f.Mirror == null).Select(f => f.Key), StringComparer.Ordinal);
            var mirrors = facts.Where(f => f.Mirror != null).Select(f => f.Mirror).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            var claims = Classifier.BaseClaims(r.Rule, opKind, r.Rule);
            if (stage != 0 && stage != 3) {
                foreach (var f in facts) {
                    if (f.Claim != null && !claims.Contains(f.Claim)) {
                        claims.Add(f.Claim);
                    }
                }
            }
            r.Facts = keys.ToList();
            r.Mirrors = mirrors;
            r.MirrorAt = facts.Where(f => f.Mirror != null).Select(f => (f.Mirror, f.Pos)).ToList();
            r.Claims = claims;
            r.Ctx = ctx;
            r.Family = Classifier.FamilyOf(r.Rule, facts, atFirst);
            r.FindingStatus = Classifier.StatusOf(claims, out var inactive);
            r.InactiveClaims = inactive;
            var sigFacts = Classifier.SignatureFacts(r.Family, keys);
            r.Sig = r.Rule + "@" + opKind + "[" + ctx + "]" + (sigFacts.Count > 0 ? "{" + string.Join(",", sigFacts) + "}" : "");
        }

        private static string FrameAt(RealSide real, ModelSide model, int k) {
            if (k >= 0 && k < real.LogFrame.Count) return real.LogFrame[k];
            if (k >= 0 && k < model.LogFrame.Count) return model.LogFrame[k];
            return real.CurrentFrame;
        }

        private static bool SameMultiset(List<string> a, List<string> b) {
            if (a.Count != b.Count) {
                return false;
            }
            var x = a.OrderBy(l => l, StringComparer.Ordinal).ToList();
            var y = b.OrderBy(l => l, StringComparer.Ordinal).ToList();
            for (int i = 0; i < x.Count; i++) {
                if (x[i] != y[i]) {
                    return false;
                }
            }
            return true;
        }

        // Kind of a log line: the callback kind, a nested step, a write through a ref, or an iteration line.
        private static string Category(string line) {
            if (IsCallback(line)) return "cb" + line.Split('|')[1];
            if (line.Contains("|w|")) return "write";
            if (line.StartsWith("visit") || line.StartsWith("it|")) return "iter";
            if (line.Split('|')[0].Contains('#')) return "step";
            return "other";
        }

        private static string Norm(string outcome) {
            if (outcome == null) {
                return "";
            }
            // "ok(3,g2)" carries a handle; handles are compared by the log and the snapshot.
            return outcome;
        }

        private static string OutcomeClass(string real, string model) {
            bool rt = real.StartsWith("!");
            bool mt = model.StartsWith("!");
            if (mt && !rt) return "X-missing-throw";
            if (rt && !mt) return "X-unexpected-throw";
            if (rt && mt) return "X-other-throw";
            if (real.StartsWith("ok(") || model.StartsWith("ok(")) return "S-handle";
            return "X-outcome";
        }

        // The model marks the content of an EventBuffer written back from a stale copy as unknown ("EB(?)");
        // the same position of the real log or snapshot is masked the same way.
        public static void MaskTainted(List<string> real, List<string> model) {
            int n = Math.Min(real.Count, model.Count);
            for (int i = 0; i < n; i++) {
                string m = model[i];
                if (!m.EndsWith("|EB(?)", StringComparison.Ordinal)) {
                    continue;
                }
                string r = real[i];
                int cut = r.LastIndexOf('|');
                if (cut > 0 && m.StartsWith(r.Substring(0, cut + 1), StringComparison.Ordinal)) {
                    real[i] = r.Substring(0, cut + 1) + "EB(?)";
                }
            }
        }

        public static void MaskTaintedSnapshot(List<string> real, List<string> model) {
            var tainted = new HashSet<string>();
            foreach (var l in model) {
                if (l.StartsWith("e") && l.Contains("EB(?)")) {
                    tainted.Add(l.Substring(0, l.IndexOf(':')));
                }
            }
            if (tainted.Count == 0) {
                return;
            }
            for (int i = 0; i < real.Count; i++) {
                var l = real[i];
                int c = l.IndexOf(':');
                if (!l.StartsWith("e") || c < 0 || !tainted.Contains(l.Substring(0, c))) {
                    continue;
                }
                var parts = l.Substring(c + 1).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                for (int p = 0; p < parts.Length; p++) {
                    if (parts[p].StartsWith("EB[") || parts[p] == "EB()") {
                        parts[p] = "EB(?)";
                    }
                }
                real[i] = l.Substring(0, c) + ": " + string.Join(" ", parts);
            }
        }

        private static int FirstLogDifference(List<string> a, List<string> b) {
            int n = Math.Min(a.Count, b.Count);
            for (int i = 0; i < n; i++) {
                if (a[i] != b[i]) {
                    return i;
                }
            }
            return a.Count == b.Count ? -1 : n;
        }

        public static bool IsCallback(string line) {
            var p = line.Split('|');
            return p.Length >= 4 && p[1].Length == 1 && "+-<>CDar".IndexOf(p[1][0]) >= 0 && !p[0].Contains('#');
        }

        private static string CallbackKind(string line) {
            var p = line.Split('|');
            switch (p[1]) {
                case "+": return "typed-added";
                case "-": return "typed-removed";
                case ">": return "filter-enter";
                case "<": return "filter-leave";
                case "C": return "world-created";
                case "D": return "world-destroyed";
                case "a": return "world-compAdded";
                case "r": return "world-compRemoved";
            }
            return "callback";
        }

        private static string Key(string line) {
            // callback identity without the payload digest: lid|kind|idx|x
            var p = line.Split('|');
            return p.Length >= 4 ? p[0] + "|" + p[1] + "|" + p[2] + "|" + p[3] : line;
        }

        // Classifies the first difference between the two logs of one operation.
        public static (string cls, string detail) ClassifyLog(List<string> real, List<string> model, int k) {
            string r = k < real.Count ? real[k] : null;
            string m = k < model.Count ? model[k] : null;
            string ctx = Context(real, model, k);
            if (r != null && m != null && IsCallback(r) && IsCallback(m) && Key(r) == Key(m)) {
                return ("N-payload:" + CallbackKind(r), "the callback is the same but carries another component value: real " + r + ", model " + m + ctx);
            }
            if (r != null && r.Contains("#") && m != null && m.Contains("#")) {
                var rp = r.Split('|');
                var mp = m.Split('|');
                if (rp.Length >= 3 && mp.Length >= 3 && rp[0] == mp[0] && rp[1] == mp[1]) {
                    bool mThrows = mp[2].StartsWith("!");
                    bool rThrows = rp[2].StartsWith("!");
                    string cls = mThrows && !rThrows ? "X-nested-missing-throw:" + rp[1]
                        : rThrows && !mThrows ? "X-nested-unexpected-throw:" + rp[1]
                        : "X-nested-outcome:" + rp[1];
                    return (cls, "nested operation " + rp[1] + ": real " + rp[2] + ", model (contract) " + mp[2] + ctx);
                }
            }
            if (r != null && r.StartsWith("visit") || m != null && m.StartsWith("visit")) {
                return ("I-iteration", "filter iteration: real " + (r ?? "(end)") + ", model " + (m ?? "(end)") + ctx);
            }
            if (r != null && IsCallback(r)) {
                // Does the model have this callback later? Then it is missing on the model's side at k.
                bool laterInModel = model.Skip(k).Contains(r);
                if (!laterInModel) {
                    return ("N-extra:" + CallbackKind(r), "the real world sends a notification the contract does not expect: " + r + ctx);
                }
            }
            if (m != null && IsCallback(m)) {
                bool laterInReal = real.Skip(k).Contains(m);
                if (!laterInReal) {
                    return ("N-missing:" + CallbackKind(m), "the real world does not send an expected notification: " + m + ctx);
                }
                return ("N-order:" + CallbackKind(m), "notification order differs: real " + (r ?? "(end)") + ", model " + m + ctx);
            }
            if (r != null && r.Contains("|w|") || m != null && m.Contains("|w|")) {
                return ("N-diverge:ref-write", "write through the callback ref differs: real " + (r ?? "(end)") + ", model " + (m ?? "(end)") + ctx);
            }
            return ("N-diverge", "logs differ: real " + (r ?? "(end)") + ", model " + (m ?? "(end)") + ctx);
        }

        private static string Context(List<string> real, List<string> model, int k) {
            int from = Math.Max(0, k - 6);
            var sb = new System.Text.StringBuilder();
            sb.Append("\n  real : ");
            for (int i = from; i < Math.Min(real.Count, k + 4); i++) {
                sb.Append(i == k ? ">>" : "").Append(real[i]).Append("  ");
            }
            sb.Append("\n  model: ");
            for (int i = from; i < Math.Min(model.Count, k + 4); i++) {
                sb.Append(i == k ? ">>" : "").Append(model[i]).Append("  ");
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ snapshots

        private static Dictionary<string, string> Parse(List<string> lines) {
            var d = new Dictionary<string, string>();
            foreach (var l in lines) {
                string key;
                if (l.StartsWith("e")) {
                    int c = l.IndexOf(':');
                    key = "entity " + l.Substring(0, c);
                    d[key] = l.Substring(c + 1).Trim();
                    continue;
                }
                if (l.StartsWith("pool ") || l.StartsWith("filter ") || l.StartsWith("ver ") || l.StartsWith("group ")) {
                    int sp = l.IndexOf(' ', l.IndexOf(' ') + 1);
                    if (l.StartsWith("ver ")) {
                        sp = l.IndexOf('=');
                    }
                    key = sp > 0 ? l.Substring(0, sp) : l;
                    d[key] = sp > 0 ? l.Substring(sp + 1) : "";
                    continue;
                }
                if (l.StartsWith("tick")) {
                    d["world"] = l;
                    continue;
                }
                if (l.StartsWith("probes")) {
                    d["probes"] = l;
                    continue;
                }
                d[l] = "";
            }
            return d;
        }

        public static (string cls, string detail)? CompareSnapshots(List<string> real, List<string> model) {
            var r = Parse(real);
            var m = Parse(model);
            var keys = new SortedSet<string>(r.Keys.Concat(m.Keys), StringComparer.Ordinal);
            var diffs = new List<(string cls, string text)>();
            foreach (var key in keys) {
                r.TryGetValue(key, out var rv);
                m.TryGetValue(key, out var mv);
                if (rv == mv) {
                    continue;
                }
                string cls;
                if (key.StartsWith("entity ")) {
                    if (rv == null) cls = "S-alive:missing-entity";
                    else if (mv == null) cls = "S-alive:extra-entity";
                    else if (ComponentTypes(rv) != ComponentTypes(mv)) cls = "S-components";
                    else cls = "S-value";
                } else if (key.StartsWith("pool ")) {
                    cls = "S-pool-count";
                } else if (key.StartsWith("filter ")) {
                    cls = "S-filter";
                } else if (key.StartsWith("group ")) {
                    cls = "S-group";
                } else if (key.StartsWith("ver ")) {
                    cls = "S-version";
                } else if (key == "probes") {
                    cls = "S-autoreset";
                } else if (key == "world") {
                    cls = "S-world";
                } else {
                    cls = "S-other";
                }
                diffs.Add((cls, key + ": real " + (rv ?? "(none)") + " | model " + (mv ?? "(none)")));
            }
            if (diffs.Count == 0) {
                return null;
            }
            // Report the most specific difference first: entity state, then filters, then counters.
            int Rank(string c) => c.StartsWith("S-alive") ? 0 : c == "S-components" ? 1 : c == "S-value" ? 2 : c == "S-filter" ? 3
                : c == "S-group" ? 4 : c == "S-version" ? 5 : c == "S-autoreset" ? 6 : c == "S-pool-count" ? 7 : 8;
            diffs.Sort((a, b) => Rank(a.cls).CompareTo(Rank(b.cls)));
            return (diffs[0].cls, string.Join("\n", diffs.Take(10).Select(x => x.text)));
        }

        private static string ComponentTypes(string comps) {
            var parts = comps.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", parts.Select(p => {
                int h = p.IndexOfAny(new[] { '#', '[', '(' });
                return h > 0 ? p.Substring(0, h) : p;
            }));
        }

        // ------------------------------------------------------------------ top-level operations

        private static CT[] Kinds(string[] names) => names == null ? Array.Empty<CT>() : names.Select(TypeMap.Parse).ToArray();

        private static bool Target(Side s, string name, out EH h) {
            h = EH.Null;
            return name != null && s.Names.TryGetValue(name, out h);
        }

        // Release: operations on a dead handle are undefined, so they are skipped (on both sides alike).
        // KENSEI_DEBUG: an op marked Stale runs on the dead handle and must throw.
        private static bool LiveOrStale(Side s, EH h, Op op) => s.IsAlive(h) || (s.Debug && op.Stale);

        public static string Exec(Side s, Op op) {
            try {
                return ExecCore(s, op);
            } catch (Exception ex) {
                return "!" + ex.GetType().Name;
            }
        }

        private static string ExecCore(Side s, Op op) {
            EH h;
            switch (op.K) {
                case "create": {
                    var e = s.CreateEntity(CompSpec.Of(op.T, op.Id, op.P));
                    if (op.Name != null) s.Names[op.Name] = e;
                    return "ok" + e;
                }
                case "destroy":
                    if (!Target(s, op.E, out h)) return "skip";
                    s.DestroyEntity(h);
                    return "ok";
                case "copy": {
                    // CopyEntity on a dead source is defined in both builds: Entity.Null in release, a throw under KENSEI_DEBUG.
                    if (!Target(s, op.E, out h)) return "skip";
                    var c = s.CopyEntity(h);
                    if (op.Name != null && !c.IsNull) s.Names[op.Name] = c;
                    return "ok" + c;
                }
                case "add": {
                    if (!Target(s, op.E, out h) || !LiveOrStale(s, h, op)) return "skip";
                    var hh = h;
                    return Stale(s, op, h, () => { s.Add(hh, CompSpec.Of(op.T, op.Id, op.P), op.M); return "ok"; });
                }
                case "remove": {
                    if (!Target(s, op.E, out h) || !LiveOrStale(s, h, op)) return "skip";
                    var hh = h;
                    return Stale(s, op, h, () => { s.Remove(hh, TypeMap.Parse(op.T)); return "ok"; });
                }
                case "has": {
                    if (!Target(s, op.E, out h) || !LiveOrStale(s, h, op)) return "skip";
                    var hh = h;
                    return Stale(s, op, h, () => "ok:" + s.WorldHas(hh, TypeMap.Parse(op.T)));
                }
                case "poolAdd":
                    if (!Target(s, op.E, out h) || !s.IsAlive(h)) return "skip";
                    s.PoolAdd(h.I, CompSpec.Of(op.T, op.Id, op.P));
                    return "ok";
                case "poolRemove":
                    if (!Target(s, op.E, out h) || !s.IsAlive(h)) return "skip";
                    s.PoolRemove(h.I, TypeMap.Parse(op.T));
                    return "ok";
                case "writeGet": {
                    if (!Target(s, op.E, out h) || !s.IsAlive(h)) return "skip";
                    var t = TypeMap.Parse(op.T);
                    if (!s.PoolHas(h.I, t) || !TypeMap.HasPayload(t)) return "absent";
                    s.WriteGet(h, t, op.M);
                    return "ok";
                }
                case "modify": {
                    if (!Target(s, op.E, out h) || !s.IsAlive(h)) return "skip";
                    var t = TypeMap.Parse(op.T);
                    if (!s.PoolHas(h.I, t) || !TypeMap.HasPayload(t)) return "absent";
                    s.Modify(h.I, t, op.M);
                    return "ok";
                }
                case "mark": {
                    if (!Target(s, op.E, out h) || !s.IsAlive(h)) return "skip";
                    var t = TypeMap.Parse(op.T);
                    if (!s.PoolHas(h.I, t)) return "absent";
                    s.MarkChanged(h.I, t);
                    return "ok";
                }
                case "addEvent": {
                    if (!Target(s, op.E, out h) || !LiveOrStale(s, h, op)) return "skip";
                    var hh = h;
                    return Stale(s, op, h, () => { s.AddEvent(hh, op.P); return "ok"; });
                }
                case "subscribe": {
                    if (!Target(s, op.E, out h) || !LiveOrStale(s, h, op)) return "skip";
                    var hh = h;
                    return Stale(s, op, h, () => { s.Subscribe(hh, op.Id); return "ok"; });
                }
                case "unsubscribe": {
                    if (!Target(s, op.E, out h) || !LiveOrStale(s, h, op)) return "skip";
                    var hh = h;
                    return Stale(s, op, h, () => { s.Unsubscribe(hh, op.Id); return "ok"; });
                }
                case "clip": {
                    if (!Target(s, op.E, out h) || !s.IsAlive(h)) return "skip";
                    var t = TypeMap.Parse(op.T);
                    if (!s.PoolHas(h.I, t)) return "absent";
                    s.CopyClip(h, t, op.Slot);
                    return "ok";
                }
                case "clipEvent":
                    if (s.Clip[op.Slot] == null || s.ClipType[op.Slot] != CT.EB) return "empty";
                    if (op.Stale) {
                        // Decided by the model before either side ran (see Run): the copied component was
                        // removed since, and its list went back to the pool. 2.2.2 documents it as misuse (EventBuffer XML
                        // doc: the copy is stale, "so do not use it any more"); either way it does not run.
                        if (Contract.V222 && Contract.Active("eventbuffer-copy-store")) {
                            s.Questions.Add("D-stale-copy: an EventBuffer copy whose component was removed is stale: its list went back to the pool " +
                                            "and may belong to another entity's buffer, so it must not be used any more (not run)");
                        } else {
                            s.Questions.Add("Q-stale-copy: an EventBuffer copy is modified after its component was removed (its list was returned " +
                                            "to the pool and may belong to another entity now) - not run, the contract does not cover it");
                        }
                        return "stale";
                    }
                    s.ClipAddEvent(op.Slot, op.P);
                    return "ok";
                case "writeBack":
                    if (!Target(s, op.E, out h) || !s.IsAlive(h)) return "skip";
                    if (s.Clip[op.Slot] == null) return "empty";
                    if (!s.ClipSource[op.Slot].Equals(h) && !s.CrossCopyRuns(op.Slot)) return s.CrossCopy();
                    if (s.PoolHas(h.I, s.ClipType[op.Slot])) return "has";
                    s.WriteBackAdd(h, op.Slot);
                    return "ok";
                case "cbSetClip": {
                    if (s.Clip[op.Slot] == null) return "empty";
                    if (!CbTarget(s, op.E, out var pending, out h)) return "skip";
                    if ((pending != null || !s.ClipSource[op.Slot].Equals(h)) && !s.CrossCopyRuns(op.Slot)) return s.CrossCopy();
                    s.CbSetClip(pending, h, op.Slot);
                    return "ok";
                }
                case "cbCreate":
                    s.CbCreate(op.Name, CompSpec.Of(op.T, op.Id, op.P));
                    return "ok";
                case "cbAdd": {
                    if (!CbTarget(s, op.E, out var pending, out h)) return "skip";
                    s.CbAdd(pending, h, CompSpec.Of(op.T, op.Id, op.P));
                    return "ok";
                }
                case "cbSet": {
                    if (!CbTarget(s, op.E, out var pending, out h)) return "skip";
                    s.CbSet(pending, h, CompSpec.Of(op.T, op.Id, op.P));
                    return "ok";
                }
                case "cbRemove": {
                    if (!CbTarget(s, op.E, out var pending, out h)) return "skip";
                    s.CbRemove(pending, h, TypeMap.Parse(op.T));
                    return "ok";
                }
                case "cbDestroy": {
                    if (!CbTarget(s, op.E, out var pending, out h)) return "skip";
                    s.CbDestroy(pending, h);
                    return "ok";
                }
                case "cbPlay":
                    s.CbPlayback();
                    return "ok";
                case "link": {
                    if (!Target(s, op.E, out h) || !Target(s, op.E2, out var h2)) return "skip";
                    s.Links[h] = h2;
                    s.Links[h2] = h;
                    return "ok";
                }
                case "filter":
                    s.CreateFilter(op.F, Kinds(op.Inc), Kinds(op.Exc), Kinds(op.Any));
                    return "ok";
                case "group":
                    return "ok:" + s.RegisterGroup(Kinds(op.Inc));
                case "listen":
                    return s.Listen(op.Lid) ? "ok" : "no";
                case "unlisten":
                    return s.Unlisten(op.Lid) ? "ok" : "no";
                case "warmup":
                    s.Warmup();
                    return "ok";
                case "clear":
                    s.Clear();
                    return "ok";
                case "reserve":
                    return s.Reserve(op.N1, op.N2) ? "ok" : "unsupported";
                case "frame":
                    s.RunFrame();
                    return "ok";
                case "iterate":
                    if (!s.HasFilter(op.F)) return "skip";
                    s.IterateFilter(op.F, op.Plan);
                    return "ok";
            }
            return "unknown-op";
        }

        // A top-level operation on a dead handle on purpose (KENSEI_DEBUG): note what the handle pointed at and,
        // when the call returns instead of throwing, that the world accepted it.
        private static string Stale(Side s, Op op, EH h, Func<string> call) {
            if (!(op.Stale && s.Debug && !s.IsAlive(h))) {
                return call();
            }
            int pos = s.Log.Count;
            string state = s.StaleState(h);
            string result = call();
            s.StaleAccepted(h, state, pos, op.K);
            return result;
        }

        private static bool CbTarget(Side s, string name, out string pending, out EH h) {
            pending = null;
            h = EH.Null;
            if (name == null) {
                return false;
            }
            if (name.StartsWith("p:")) {
                pending = name.Substring(2);
                return s.PendingIds.ContainsKey(pending);
            }
            return s.Names.TryGetValue(name, out h);
        }
    }
}
