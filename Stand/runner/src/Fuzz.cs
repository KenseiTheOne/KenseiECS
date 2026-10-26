// Random runner trees and operation scripts, and a greedy shrinker that reduces a violating case
// to a small one that still breaks the same rule.
using System;
using System.Collections.Generic;
using System.Linq;
using Stand.Runner.Kit;

namespace Stand.Runner {
    public sealed class GenParams {
        public int MaxEntries = 5;
        public int MaxDepth = 3;
        public int MaxTotalEntries = 16;
        public int MaxOps = 14;

        public override string ToString() {
            return "maxEntries=" + MaxEntries + ", maxDepth=" + MaxDepth + ", maxTotalEntries=" + MaxTotalEntries + ", maxOps=" + MaxOps;
        }
    }

    public static class Gen {
        private static readonly (int Caps, int Weight)[] CapsWeights = {
            (Probes.I | Probes.R | Probes.D, 22), (Probes.I | Probes.R, 12), (Probes.R, 14), (Probes.R | Probes.D, 10),
            (Probes.I | Probes.D, 14), (Probes.D, 14), (Probes.I, 8)
        };

        public static CaseSpec Random(int seed, GenParams p, Ctx ctx) {
            var rnd = new Random(seed);
            bool v22 = ctx.Contract == Contract.V22;
            var spec = new CaseSpec { Origin = "random", Seed = seed };
            int nextEntry = 1;
            int nextRunner = 0;
            int total = 0;
            var finished = new List<RunnerSpec>();
            var registeredAsPhase = new HashSet<int>();

            RunnerSpec NewRunner(int depth) {
                var r = new RunnerSpec { Id = nextRunner++ };
                spec.Runners.Add(r);
                int n = rnd.Next(1, p.MaxEntries + 1);
                for (int k = 0; k < n && total < p.MaxTotalEntries; k++) {
                    total++;
                    double roll = rnd.NextDouble();
                    int id = nextEntry++;
                    if (depth < p.MaxDepth && roll < 0.22) {
                        bool phase = rnd.NextDouble() < 0.3;
                        var child = NewRunner(depth + 1);
                        var e = new EntrySpec { Id = id, RunnerId = child.Id };
                        if (phase) {
                            e.Kind = EntryKind.Phase;
                            e.Name = "p" + id;
                            e.Api = v22 ? rnd.Next(3) : rnd.Next(1, 3);
                            registeredAsPhase.Add(child.Id);
                        } else {
                            e.Kind = EntryKind.Group;
                            e.Name = v22 && Api.HasAddGroup && rnd.NextDouble() < 0.5 ? "g" + id : null;
                            e.Api = rnd.Next(2);
                        }
                        if (v22 && Api.HasSetActiveSelf && rnd.NextDouble() < 0.12) {
                            child.StartDisabled = true;
                        }
                        r.Entries.Add(e);
                        continue;
                    }
                    if (roll < 0.27) {
                        // Another slot for a runner that is already complete (never an ancestor: no cycles).
                        var candidates = finished.Where(c => c.Id != 0 && (v22 || !registeredAsPhase.Contains(c.Id))).ToList();
                        if (candidates.Count > 0) {
                            var target = candidates[rnd.Next(candidates.Count)];
                            var e = new EntrySpec { Id = id, RunnerId = target.Id, Kind = EntryKind.Group, Api = rnd.Next(2) };
                            if (v22 && rnd.NextDouble() < 0.2) {
                                e.Kind = EntryKind.Phase;
                                e.Name = "p" + id;
                                e.Api = rnd.Next(3);
                                registeredAsPhase.Add(target.Id);
                            } else if (v22 && Api.HasAddGroup && rnd.NextDouble() < 0.5) {
                                e.Name = "g" + id;
                            }
                            r.Entries.Add(e);
                            continue;
                        }
                    }
                    if (roll < 0.32) {
                        r.Entries.Add(new EntrySpec { Id = id, Kind = EntryKind.DelHere, DelType = rnd.Next(Events.TypeCount) });
                        continue;
                    }
                    r.Entries.Add(Leaf(rnd, id));
                }
                if (rnd.NextDouble() < 0.35) {
                    int count = rnd.Next(1, 3);
                    for (int q = 0; q < count; q++) {
                        r.OneFrames.Add(new OneFrameSpec {
                            Type = rnd.Next(Events.TypeCount),
                            CleanWhenDisabled = v22 && Api.HasCleanWhenDisabled && rnd.NextDouble() < 0.5,
                            AfterAdd = rnd.NextDouble() < 0.3
                        });
                    }
                }
                finished.Add(r);
                return r;
            }

            NewRunner(0);
            if (v22 && Api.HasSetActiveSelf && rnd.NextDouble() < 0.04) {
                spec.Root.StartDisabled = true;
            }

            var initLeaves = spec.AllEntries().Where(x => x.Entry.Kind == EntryKind.Leaf && (x.Entry.Caps & Probes.I) != 0).Select(x => x.Entry).ToList();
            if (initLeaves.Count > 0 && rnd.NextDouble() < 0.55) {
                initLeaves[rnd.Next(initLeaves.Count)].FailInit = rnd.NextDouble() < 0.2 ? 2 : 1;
                if (rnd.NextDouble() < 0.15) {
                    initLeaves[rnd.Next(initLeaves.Count)].FailInit = 1;
                }
            }

            // The script follows the contract model, so that most frames run on an initialized root:
            // an uninitialized root (failed Init, after Destroy) usually gets Init next.
            var model = new Model(spec, ctx);
            void Push(OpSpec op) {
                spec.Ops.Add(op);
                model.Apply(op);
            }
            Push(new OpSpec { Kind = rnd.NextDouble() < 0.2 ? OpKind.Warmup : OpKind.Init });
            int ops = rnd.Next(3, p.MaxOps + 1);
            for (int k = 0; k < ops; k++) {
                if (!model.Root.Initialized && rnd.NextDouble() < 0.7) {
                    Push(new OpSpec { Kind = rnd.NextDouble() < 0.15 ? OpKind.Warmup : OpKind.Init });
                    continue;
                }
                var op = RandomOp(rnd, spec, v22);
                if (op != null) {
                    Push(op);
                }
            }
            spec.Ops.Add(new OpSpec { Kind = OpKind.Destroy });
            spec.Ops.Add(new OpSpec { Kind = OpKind.Destroy });
            if (rnd.NextDouble() < 0.3) {
                spec.Ops.Add(new OpSpec { Kind = OpKind.Init });
                spec.Ops.Add(new OpSpec { Kind = OpKind.Frame });
                spec.Ops.Add(new OpSpec { Kind = OpKind.Destroy });
            }
            return spec;
        }

        private static EntrySpec Leaf(Random rnd, int id) {
            int roll = rnd.Next(CapsWeights.Sum(c => c.Weight));
            int caps = Probes.I | Probes.R | Probes.D;
            foreach (var (c, w) in CapsWeights) {
                if (roll < w) {
                    caps = c;
                    break;
                }
                roll -= w;
            }
            var e = new EntrySpec { Id = id, Kind = EntryKind.Leaf, Caps = caps };
            if (rnd.NextDouble() < 0.5) {
                e.Name = "n" + id;
            }
            if ((caps & Probes.D) != 0 && rnd.NextDouble() < 0.25) {
                e.ThrowOnDestroy = true;
            }
            if ((caps & Probes.R) != 0) {
                if (rnd.NextDouble() < 0.04) {
                    e.FailRun = 1;
                }
                if (rnd.NextDouble() < 0.3) {
                    e.Produce = rnd.Next(Events.TypeCount);
                }
                if (rnd.NextDouble() < 0.35) {
                    e.Observe = true;
                }
            }
            return e;
        }

        private static OpSpec RandomOp(Random rnd, CaseSpec spec, bool v22) {
            var weights = new List<(OpKind Kind, int Weight)> {
                (OpKind.Frame, 34), (OpKind.SetActiveIndex, 14), (OpKind.SetActiveName, 9), (OpKind.Emit, 6),
                (OpKind.Init, 4), (OpKind.Warmup, 1), (OpKind.Destroy, 4), (OpKind.UnknownName, 2)
            };
            if (v22 && Api.HasSetActiveSelf) {
                weights.Add((OpKind.SetActiveSelf, 9));
            }
            int roll = rnd.Next(weights.Sum(w => w.Weight));
            var kind = OpKind.Frame;
            foreach (var (k, w) in weights) {
                if (roll < w) {
                    kind = k;
                    break;
                }
                roll -= w;
            }
            var runners = spec.Reachable();
            switch (kind) {
                case OpKind.SetActiveIndex: {
                    var targets = spec.AllEntries().Where(x => v22 || Toggleable21(x.Entry)).ToList();
                    if (targets.Count == 0) {
                        return null;
                    }
                    var (owner, entry) = targets[rnd.Next(targets.Count)];
                    return new OpSpec { Kind = kind, RunnerId = owner.Id, EntryId = entry.Id, Value = rnd.NextDouble() < 0.5 };
                }
                case OpKind.SetActiveName: {
                    var targets = spec.AllEntries().Where(x => x.Entry.Name != null && x.Entry.Kind != EntryKind.DelHere && (v22 || Toggleable21(x.Entry))).ToList();
                    if (targets.Count == 0) {
                        return null;
                    }
                    var (owner, entry) = targets[rnd.Next(targets.Count)];
                    return new OpSpec { Kind = kind, RunnerId = owner.Id, EntryId = entry.Id, Value = rnd.NextDouble() < 0.5 };
                }
                case OpKind.SetActiveSelf:
                    return new OpSpec { Kind = kind, RunnerId = runners[rnd.Next(runners.Count)].Id, Value = rnd.NextDouble() < 0.5 };
                case OpKind.Emit:
                    return new OpSpec { Kind = kind, Type = rnd.Next(Events.TypeCount) };
                case OpKind.UnknownName:
                    return new OpSpec { Kind = kind, RunnerId = runners[rnd.Next(runners.Count)].Id, Type = rnd.Next(3), Value = rnd.NextDouble() < 0.5 };
                default:
                    return new OpSpec { Kind = kind };
            }
        }

        /// <summary> 2.1 documents toggling run systems, unnamed groups (their slot) and named runners (phases). </summary>
        private static bool Toggleable21(EntrySpec e) {
            return e.Kind == EntryKind.Group || e.Kind == EntryKind.Phase || e.Kind == EntryKind.DelHere ||
                   (e.Kind == EntryKind.Leaf && (e.Caps & Probes.R) != 0);
        }
    }

    public static class Shrinker {
        /// <summary>
        /// Smallest case found that still violates <paramref name="rule"/> at <paramref name="place"/> (the same
        /// signature; any place when it is null), renumbered for reading. A step never adds a violation of another
        /// rule, and an isolation pass tries to drop the other rules the case breaks too (a repro of one defect
        /// should not lean on another one, which may be fixed first).
        /// </summary>
        public static CaseSpec Shrink(CaseSpec original, string rule, string place, Ctx ctx, int budget = 4000, int secondBudget = 2000) {
            var best = original.Clone();
            var first = Engine.Run(best, ctx).Violations.FirstOrDefault(v => Is(v, rule, place));
            if (first == null) {
                return best;
            }
            if (first.OpIndex < 0) {
                best.Ops.Clear();
            } else if (first.OpIndex + 1 < best.Ops.Count) {
                best.Ops.RemoveRange(first.OpIndex + 1, best.Ops.Count - first.OpIndex - 1);
            }
            var others = Others(best, rule, place, ctx) ?? new HashSet<string>();
            best = Reduce(best, rule, place, ctx, ref budget, ref others, Mutations, strict: false);
            if (others.Count > 0) {
                // Own budgets: a large case can use up the first one, and the isolation matters more than the last few entries.
                // Not strict: two Init-less systems may need an Init each before the other rule goes away; every isolation
                // mutation applies at most once per entry or runner, so the pass ends.
                int isolationBudget = secondBudget;
                best = Reduce(best, rule, place, ctx, ref isolationBudget, ref others, IsolationMutations, strict: false);
                int finalBudget = secondBudget;
                best = Reduce(best, rule, place, ctx, ref finalBudget, ref others, Mutations, strict: false);
            }
            var renumbered = Renumber(best);
            return Fails(renumbered, rule, place, ctx) ? renumbered : best;
        }

        private static bool Is(Violation v, string rule, string place) {
            return v.Rule == rule && (place == null || v.Place == place);
        }

        // Greedy loop: apply the first mutation whose result still violates the rule and whose other violated
        // rules are a subset of the current ones (strict: a proper subset), until none applies.
        private static CaseSpec Reduce(CaseSpec best, string rule, string place, Ctx ctx, ref int budget, ref HashSet<string> others,
                                       Func<CaseSpec, IEnumerable<Func<CaseSpec, bool>>> mutations, bool strict) {
            bool progress = true;
            while (progress && budget > 0) {
                progress = false;
                foreach (var mutate in mutations(best)) {
                    if (--budget <= 0) {
                        break;
                    }
                    var candidate = best.Clone();
                    if (!mutate(candidate)) {
                        continue;
                    }
                    candidate.Normalize();
                    var candidateOthers = Others(candidate, rule, place, ctx);
                    if (candidateOthers == null || !candidateOthers.IsSubsetOf(others) || (strict && candidateOthers.Count == others.Count)) {
                        continue;
                    }
                    best = candidate;
                    others = candidateOthers;
                    progress = true;
                    break;
                }
            }
            return best;
        }

        /// <summary> The other rules a case violates, or null when it does not violate <paramref name="rule"/> at <paramref name="place"/>. </summary>
        private static HashSet<string> Others(CaseSpec spec, string rule, string place, Ctx ctx) {
            var result = Engine.Run(spec, ctx);
            if (result.NotApplicable || !result.Violations.Any(v => Is(v, rule, place))) {
                return null;
            }
            return new HashSet<string>(result.Violations.Select(v => v.Rule).Where(r => r != rule));
        }

        public static bool Fails(CaseSpec spec, string rule, string place, Ctx ctx) {
            var result = Engine.Run(spec, ctx);
            return !result.NotApplicable && result.Violations.Any(v => Is(v, rule, place));
        }

        // Changes that make a case less entangled rather than smaller: give an Init-less system an Init
        // (it then belongs in a rollback), drop a OneFrame or an observer that ties in the cleanup rules.
        private static IEnumerable<Func<CaseSpec, bool>> IsolationMutations(CaseSpec s) {
            foreach (var id in s.AllEntries().Select(x => x.Entry.Id).ToList()) {
                int eid = id;
                yield return c => Edit(c, eid, e => e.Kind == EntryKind.Leaf && (e.Caps & Probes.I) == 0, e => e.Caps |= Probes.I);
                yield return c => Edit(c, eid, e => e.Observe, e => e.Observe = false);
                yield return c => Edit(c, eid, e => e.Produce >= 0, e => e.Produce = -1);
            }
            foreach (var r in s.Runners) {
                int rid = r.Id;
                yield return c => {
                    var rr = c.RunnerOrNull(rid);
                    if (rr == null || rr.OneFrames.Count == 0) {
                        return false;
                    }
                    rr.OneFrames.Clear();
                    return true;
                };
            }
        }

        // Each mutation edits a clone and says whether it applied.
        private static IEnumerable<Func<CaseSpec, bool>> Mutations(CaseSpec s) {
            for (int i = s.Ops.Count - 1; i >= 0; i--) {
                int at = i;
                yield return c => {
                    if (c.Ops.Count <= 1) {
                        return false;
                    }
                    c.Ops.RemoveAt(at);
                    return true;
                };
            }
            var entries = s.AllEntries().Select(x => x.Entry.Id).ToList();
            for (int i = entries.Count - 1; i >= 0; i--) {
                int id = entries[i];
                yield return c => {
                    var (owner, entry) = c.FindEntry(id);
                    if (owner == null || (owner.Id == 0 && owner.Entries.Count == 1)) {
                        return false;
                    }
                    owner.Entries.Remove(entry);
                    return true;
                };
                // Replace a nested runner by its entries (flatten one level).
                yield return c => {
                    var (owner, entry) = c.FindEntry(id);
                    if (owner == null || (entry.Kind != EntryKind.Group && entry.Kind != EntryKind.Phase) || c.Appearances(entry.RunnerId) > 1) {
                        return false;
                    }
                    var child = c.Runner(entry.RunnerId);
                    int at = owner.Entries.IndexOf(entry);
                    owner.Entries.RemoveAt(at);
                    owner.Entries.InsertRange(at, child.Entries);
                    owner.OneFrames.AddRange(child.OneFrames);
                    child.Entries = new List<EntrySpec>();
                    foreach (var op in c.Ops) {
                        if (op.RunnerId == child.Id) {
                            op.RunnerId = owner.Id;
                        }
                    }
                    return true;
                };
            }
            foreach (var r in s.Runners) {
                int rid = r.Id;
                for (int q = r.OneFrames.Count - 1; q >= 0; q--) {
                    int at = q;
                    yield return c => {
                        var rr = c.RunnerOrNull(rid);
                        if (rr == null || at >= rr.OneFrames.Count) {
                            return false;
                        }
                        rr.OneFrames.RemoveAt(at);
                        return true;
                    };
                }
                yield return c => Flip(c.RunnerOrNull(rid), x => x.StartDisabled, x => x.StartDisabled = false);
            }
            foreach (var id in entries) {
                int eid = id;
                yield return c => Edit(c, eid, e => e.FailInit > 0, e => e.FailInit = 0);
                yield return c => Edit(c, eid, e => e.FailInit > 1, e => e.FailInit = 1);
                yield return c => Edit(c, eid, e => e.ThrowOnDestroy, e => e.ThrowOnDestroy = false);
                yield return c => Edit(c, eid, e => e.FailRun > 0, e => e.FailRun = 0);
                yield return c => Edit(c, eid, e => e.Produce >= 0, e => e.Produce = -1);
                yield return c => Edit(c, eid, e => e.Observe, e => e.Observe = false);
                yield return c => Edit(c, eid, e => e.Name != null && e.Kind != EntryKind.Phase, e => e.Name = null);
                yield return c => Edit(c, eid, e => e.Api != 0, e => e.Api = 0);
                foreach (int bit in new[] { Probes.R, Probes.I, Probes.D }) {
                    int b = bit;
                    yield return c => Edit(c, eid, e => e.Kind == EntryKind.Leaf && (e.Caps & b) != 0 && (e.Caps & ~b) != 0, e => e.Caps &= ~b);
                }
            }
            for (int i = 0; i < s.Ops.Count; i++) {
                int at = i;
                yield return c => {
                    if (c.Ops[at].Kind != OpKind.Warmup) {
                        return false;
                    }
                    c.Ops[at].Kind = OpKind.Init;
                    return true;
                };
            }
        }

        private static bool Edit(CaseSpec c, int entryId, Func<EntrySpec, bool> applies, Action<EntrySpec> change) {
            var (_, entry) = c.FindEntry(entryId);
            if (entry == null || !applies(entry)) {
                return false;
            }
            change(entry);
            return true;
        }

        private static bool Flip(RunnerSpec r, Func<RunnerSpec, bool> applies, Action<RunnerSpec> change) {
            if (r == null || !applies(r)) {
                return false;
            }
            change(r);
            return true;
        }

        /// <summary> Runners r0.. and entries s1.. in depth-first order; generated names follow the new ids. </summary>
        public static CaseSpec Renumber(CaseSpec s) {
            var c = s.Clone();
            var runnerMap = new Dictionary<int, int>();
            var entryMap = new Dictionary<int, int>();
            int nextRunner = 0;
            int nextEntry = 1;
            void Visit(int id) {
                if (runnerMap.ContainsKey(id)) {
                    return;
                }
                runnerMap[id] = nextRunner++;
                foreach (var e in c.Runner(id).Entries) {
                    entryMap[e.Id] = nextEntry++;
                    if (e.Kind == EntryKind.Group || e.Kind == EntryKind.Phase) {
                        Visit(e.RunnerId);
                    }
                }
            }
            Visit(0);
            foreach (var r in c.Runners) {
                foreach (var e in r.Entries) {
                    if (e.Kind == EntryKind.Group || e.Kind == EntryKind.Phase) {
                        e.RunnerId = runnerMap[e.RunnerId];
                    }
                    int newId = entryMap[e.Id];
                    if (e.Name != null && e.Name.Length > 1 && char.IsDigit(e.Name[1])) {
                        e.Name = e.Name[0] + newId.ToString();
                    }
                    e.Id = newId;
                }
                r.Id = runnerMap[r.Id];
            }
            c.Runners.Sort((a, b) => a.Id.CompareTo(b.Id));
            foreach (var op in c.Ops) {
                if (runnerMap.TryGetValue(op.RunnerId, out int nr)) {
                    op.RunnerId = nr;
                }
                if (op.EntryId >= 0 && entryMap.TryGetValue(op.EntryId, out int ne)) {
                    op.EntryId = ne;
                }
            }
            return c;
        }
    }
}
