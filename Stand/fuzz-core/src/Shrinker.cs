using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace KenseiStand.FuzzCore {
    // Delta debugging of a failing scenario, in-process and deterministic: a candidate counts when it
    // fails with the same rule at an operation of the same kind, in the same defect family and with the
    // same status. Keeping the family keeps the mechanism: a new bug cannot shrink into the scenario of a
    // known family that happens to violate the same rule, and a known family's scenario stays one of it.
    // Crash scenarios are shrunk with the in-process runaway guard (C-runaway); the driver confirms the real
    // crash afterwards.
    public static class Shrinker {
        public sealed class Outcome {
            public Scenario Min;
            public RunResult Result;
            public int Runs;
            public bool OutOfTime;
        }

        public static string OpKind(Scenario s, RunResult r) =>
            r.OpIndex >= 0 && r.OpIndex < s.Ops.Count ? s.Ops[r.OpIndex].K : "";

        public static Outcome Shrink(Scenario scn, RunResult target, RunOptions opt, TimeSpan budget) {
            var sw = Stopwatch.StartNew();
            int runs = 0;
            string rule = target.Rule;
            string kind = OpKind(scn, target);
            string family = target.Family;
            string status = target.FindingStatus;
            bool outOfTime = false;

            RunResult Try(Scenario cand) {
                runs++;
                try {
                    return Executor.Run(cand, new RunOptions { CrashMode = opt.CrashMode, Guard = true, GuardDepth = opt.GuardDepth, GuardFires = opt.GuardFires,
                                                               Readings = opt.Readings });
                } catch (Exception ex) {
                    return new RunResult { Status = "harness-error", Detail = ex.ToString() };
                }
            }

            bool Same(Scenario cand, RunResult r) => r.Status == "fail" && r.Rule == rule && OpKind(cand, r) == kind
                                                     && r.Family == family && r.FindingStatus == status;

            bool Budget() {
                if (sw.Elapsed > budget) {
                    outOfTime = true;
                }
                return !outOfTime;
            }

            var best = scn.Clone();
            var bestResult = target;

            // Operations after the failing one never ran.
            if (target.OpIndex >= 0 && target.OpIndex + 1 < best.Ops.Count) {
                var cut = best.WithOps(best.Ops.Take(target.OpIndex + 1).ToList());
                var r = Try(cut);
                if (Same(cut, r)) {
                    best = cut;
                    bestResult = r;
                }
            }

            bool changed = true;
            int rounds = 0;
            while (changed && Budget() && rounds++ < 8) {
                changed = false;

                // 1. ddmin over the operations (complements first, then single operations).
                var ops = best.Ops;
                int n = 2;
                while (ops.Count >= 2 && Budget()) {
                    int chunk = (int)Math.Ceiling(ops.Count / (double)n);
                    bool reduced = false;
                    for (int start = 0; start < ops.Count && Budget(); start += chunk) {
                        var keep = new List<Op>();
                        for (int i = 0; i < ops.Count; i++) {
                            if (i < start || i >= start + chunk) {
                                keep.Add(ops[i]);
                            }
                        }
                        if (keep.Count == 0) {
                            continue;
                        }
                        var cand = best.WithOps(keep);
                        var r = Try(cand);
                        if (Same(cand, r)) {
                            best = cand;
                            bestResult = r;
                            ops = best.Ops;
                            n = Math.Max(n - 1, 2);
                            reduced = true;
                            changed = true;
                            break;
                        }
                    }
                    if (!reduced) {
                        if (n >= ops.Count) {
                            break;
                        }
                        n = Math.Min(ops.Count, n * 2);
                    }
                }

                // 2. Listeners, their rules and steps.
                foreach (var lid in best.Listeners.Select(l => l.Lid).Reverse().ToList()) {
                    if (!Budget()) break;
                    var cand = WithoutListener(best, lid);
                    var r = Try(cand);
                    if (Same(cand, r)) {
                        best = cand;
                        bestResult = r;
                        changed = true;
                    }
                }
                for (int li = 0; li < best.Listeners.Count && Budget(); li++) {
                    for (int ri = best.Listeners[li].Rules.Count - 1; ri >= 0 && Budget(); ri--) {
                        if (best.Listeners[li].Rules.Count > 1) {
                            var cand = best.Clone();
                            cand.Listeners[li].Rules.RemoveAt(ri);
                            var r = Try(cand);
                            if (Same(cand, r)) {
                                best = cand;
                                bestResult = r;
                                changed = true;
                                continue;
                            }
                        }
                        for (int si = best.Listeners[li].Rules[ri].Do.Count - 1; si >= 0 && Budget(); si--) {
                            if (best.Listeners[li].Rules[ri].Do.Count <= 1) {
                                break;
                            }
                            var cand = best.Clone();
                            cand.Listeners[li].Rules[ri].Do.RemoveAt(si);
                            var r = Try(cand);
                            if (Same(cand, r)) {
                                best = cand;
                                bestResult = r;
                                changed = true;
                            }
                        }
                        if (best.Listeners[li].Rules[ri].Max > 1) {
                            var cand = best.Clone();
                            cand.Listeners[li].Rules[ri].Max = 1;
                            var r = Try(cand);
                            if (Same(cand, r)) {
                                best = cand;
                                bestResult = r;
                                changed = true;
                            }
                        }
                        foreach (var step in Enumerable.Range(0, best.Listeners[li].Rules[ri].Do.Count)) {
                            if (!Budget()) break;
                            var st = best.Listeners[li].Rules[ri].Do[step];
                            if (st.G) {
                                var cand = best.Clone();
                                cand.Listeners[li].Rules[ri].Do[step].G = false;
                                var r = Try(cand);
                                if (Same(cand, r)) {
                                    best = cand;
                                    bestResult = r;
                                    changed = true;
                                }
                            }
                            if (st.Name != null) {
                                var cand = best.Clone();
                                cand.Listeners[li].Rules[ri].Do[step].Name = null;
                                var r = Try(cand);
                                if (Same(cand, r)) {
                                    best = cand;
                                    bestResult = r;
                                    changed = true;
                                }
                            }
                        }
                    }
                }

                // 3. Recorders and the owning group.
                foreach (var flag in new[] { "RecTyped", "RecFilters", "RecWorld", "Group" }) {
                    if (!Budget()) break;
                    var cand = best.Clone();
                    bool was = flag switch {
                        "RecTyped" => cand.RecTyped,
                        "RecFilters" => cand.RecFilters,
                        "RecWorld" => cand.RecWorld,
                        _ => cand.Group,
                    };
                    if (!was) continue;
                    switch (flag) {
                        case "RecTyped": cand.RecTyped = false; break;
                        case "RecFilters": cand.RecFilters = false; break;
                        case "RecWorld": cand.RecWorld = false; break;
                        default: cand.Group = false; break;
                    }
                    var r = Try(cand);
                    if (Same(cand, r)) {
                        best = cand;
                        bestResult = r;
                        changed = true;
                    }
                }

                // 4. Operation details: ref markers, iteration plans, filter constraints.
                for (int i = 0; i < best.Ops.Count && Budget(); i++) {
                    var op = best.Ops[i];
                    if (op.M >= 0 && op.K == "add") {
                        var cand = best.Clone();
                        cand.Ops[i].M = -1;
                        var r = Try(cand);
                        if (Same(cand, r)) {
                            best = cand;
                            bestResult = r;
                            changed = true;
                        }
                    }
                    if (op.Plan != null && op.Plan.Length > 1) {
                        for (int p = op.Plan.Length - 1; p >= 0 && best.Ops[i].Plan.Length > 1; p--) {
                            var cand = best.Clone();
                            var plan = cand.Ops[i].Plan.ToList();
                            plan.RemoveAt(p);
                            cand.Ops[i].Plan = plan.ToArray();
                            var r = Try(cand);
                            if (Same(cand, r)) {
                                best = cand;
                                bestResult = r;
                                changed = true;
                            }
                        }
                    }
                    if (op.K == "filter") {
                        foreach (var part in new[] { "Inc", "Exc", "Any" }) {
                            var arr = part == "Inc" ? best.Ops[i].Inc : part == "Exc" ? best.Ops[i].Exc : best.Ops[i].Any;
                            if (arr == null) continue;
                            for (int k = arr.Length - 1; k >= 0; k--) {
                                var cand = best.Clone();
                                var list = (part == "Inc" ? cand.Ops[i].Inc : part == "Exc" ? cand.Ops[i].Exc : cand.Ops[i].Any).ToList();
                                if (k >= list.Count) continue;
                                list.RemoveAt(k);
                                if (part == "Inc") cand.Ops[i].Inc = list.ToArray();
                                else if (part == "Exc") cand.Ops[i].Exc = list.ToArray();
                                else cand.Ops[i].Any = list.ToArray();
                                var r = Try(cand);
                                if (Same(cand, r)) {
                                    best = cand;
                                    bestResult = r;
                                    changed = true;
                                    arr = part == "Inc" ? best.Ops[i].Inc : part == "Exc" ? best.Ops[i].Exc : best.Ops[i].Any;
                                }
                            }
                        }
                    }
                }
            }

            best.Origin = (scn.Origin ?? "random") + " (shrunk " + scn.Ops.Count + "->" + best.Ops.Count + " ops)";
            return new Outcome { Min = best, Result = bestResult, Runs = runs, OutOfTime = outOfTime };
        }

        public static Scenario WithoutListener(Scenario s, int lid) {
            var c = s.Clone();
            c.Listeners.RemoveAll(l => l.Lid == lid);
            c.Ops.RemoveAll(o => (o.K == "listen" || o.K == "unlisten") && o.Lid == lid);
            foreach (var l in c.Listeners) {
                foreach (var r in l.Rules) {
                    r.Do.RemoveAll(st => (st.A == "listen" || st.A == "unlisten") && st.Lid == lid);
                }
            }
            return c;
        }
    }
}
