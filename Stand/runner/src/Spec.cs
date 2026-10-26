// A test case as data: a runner tree (runner 0 is the root; a runner referenced by two entries
// sits in two slots) and a script of operations. The model, the executor, the shrinker and the
// repro generator all work on this form, and a case is saved as JSON next to its repro.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Stand.Runner.Kit;

namespace Stand.Runner {
    public enum EntryKind { Leaf, Group, Phase, DelHere }

    public enum OpKind {
        Init,
        Warmup,
        /// <summary> root.Run(), then Run() of every phase (each phase in its own try, like a Unity message). </summary>
        Frame,
        SetActiveIndex,
        SetActiveName,
        /// <summary> runner.SetActive(bool) (2.2+). </summary>
        SetActiveSelf,
        /// <summary> Create an entity with Ev[Type] from outside the runners. </summary>
        Emit,
        Destroy,
        /// <summary> An unknown name passed to SetActive (Type 0), IsActive (1) or GetRunner (2). </summary>
        UnknownName
    }

    public sealed class OneFrameSpec {
        public int Type;
        public bool CleanWhenDisabled;
        /// <summary> Registered after the whole tree is built (exercises propagation to the parents). </summary>
        public bool AfterAdd;
    }

    public sealed class EntrySpec {
        public int Id;
        public EntryKind Kind;
        /// <summary> Explicit registration name, or null. </summary>
        public string Name;

        // Leaf
        public int Caps;
        public int FailInit;
        public int FailRun;
        public bool ThrowOnDestroy;
        public int Produce = -1;
        public bool Observe;

        // Group / Phase: the nested runner (shared when several entries name the same runner).
        public int RunnerId = -1;
        /// <summary> Phase: 0 AddPhase, 1 Add((ISystem)runner, name), 2 Add(runner, name) [obsolete on 2.2]. Unnamed group: 0 Add(runner), 1 Add(runner, null). </summary>
        public int Api;

        // DelHere
        public int DelType;

        public string Label => "s" + Id;
    }

    public sealed class RunnerSpec {
        public int Id;
        public List<EntrySpec> Entries = new List<EntrySpec>();
        public List<OneFrameSpec> OneFrames = new List<OneFrameSpec>();
        /// <summary> SetActive(false) on the runner before it is registered (2.2+). </summary>
        public bool StartDisabled;
    }

    public sealed class OpSpec {
        public OpKind Kind;
        public int RunnerId;
        public int EntryId = -1;
        public bool Value;
        public int Type;
    }

    public sealed class CaseSpec {
        public string Origin;
        public int Seed;
        public List<RunnerSpec> Runners = new List<RunnerSpec>();
        public List<OpSpec> Ops = new List<OpSpec>();

        public RunnerSpec Runner(int id) {
            return Runners.First(r => r.Id == id);
        }

        public RunnerSpec RunnerOrNull(int id) {
            return Runners.FirstOrDefault(r => r.Id == id);
        }

        public RunnerSpec Root => Runner(0);

        public IEnumerable<(RunnerSpec Owner, EntrySpec Entry)> AllEntries() {
            foreach (var r in Runners) {
                foreach (var e in r.Entries) {
                    yield return (r, e);
                }
            }
        }

        public (RunnerSpec Owner, EntrySpec Entry) FindEntry(int entryId) {
            foreach (var pair in AllEntries()) {
                if (pair.Entry.Id == entryId) {
                    return pair;
                }
            }
            return (null, null);
        }

        /// <summary> Number of entries that reference the runner (more than one: shared slots). </summary>
        public int Appearances(int runnerId) {
            return AllEntries().Count(p => p.Entry.RunnerId == runnerId && (p.Entry.Kind == EntryKind.Group || p.Entry.Kind == EntryKind.Phase));
        }

        /// <summary> Runners reachable from the root, in depth-first pre-order (each once). </summary>
        public List<RunnerSpec> Reachable() {
            var seen = new HashSet<int>();
            var order = new List<RunnerSpec>();
            void Visit(int id) {
                if (!seen.Add(id)) {
                    return;
                }
                var r = RunnerOrNull(id);
                if (r == null) {
                    return;
                }
                order.Add(r);
                foreach (var e in r.Entries) {
                    if (e.Kind == EntryKind.Group || e.Kind == EntryKind.Phase) {
                        Visit(e.RunnerId);
                    }
                }
            }
            Visit(0);
            return order;
        }

        /// <summary> Distinct runners registered as a phase somewhere, in depth-first pre-order: the frame driver runs them after the root. </summary>
        public List<int> PhaseOrder() {
            var phases = new HashSet<int>(AllEntries().Where(p => p.Entry.Kind == EntryKind.Phase).Select(p => p.Entry.RunnerId));
            return Reachable().Where(r => phases.Contains(r.Id)).Select(r => r.Id).ToList();
        }

        /// <summary>
        /// For every nested runner, the entry that registers it first — in the order Exec.Build performs the
        /// registrations (depth-first; a nested runner is built when its first entry is reached). Every other
        /// entry naming that runner registers it a second time.
        /// </summary>
        public Dictionary<int, EntrySpec> FirstRegistrations() {
            var first = new Dictionary<int, EntrySpec>();
            void Visit(RunnerSpec r) {
                foreach (var e in r.Entries) {
                    if ((e.Kind == EntryKind.Group || e.Kind == EntryKind.Phase) && !first.ContainsKey(e.RunnerId) && e.RunnerId != 0) {
                        first[e.RunnerId] = e;
                        var child = RunnerOrNull(e.RunnerId);
                        if (child != null) {
                            Visit(child);
                        }
                    }
                }
            }
            Visit(Root);
            return first;
        }

        /// <summary> Group and phase entries in the order Exec.Build registers them (a nested runner's own entries before the entry that adds it). </summary>
        public List<EntrySpec> NestedRegistrationOrder() {
            var order = new List<EntrySpec>();
            var built = new HashSet<int>();
            void Visit(RunnerSpec r) {
                built.Add(r.Id);
                foreach (var e in r.Entries) {
                    if (e.Kind != EntryKind.Group && e.Kind != EntryKind.Phase) {
                        continue;
                    }
                    var child = RunnerOrNull(e.RunnerId);
                    if (child != null && !built.Contains(child.Id)) {
                        Visit(child);
                    }
                    order.Add(e);
                }
            }
            Visit(Root);
            return order;
        }

        public bool HasSharedRunner() {
            return Runners.Any(r => r.Id != 0 && Appearances(r.Id) > 1);
        }

        /// <summary> How a runner is registered, for the place of a signature: "root", "group" or "phase" (its first registration). </summary>
        public string RoleOf(int runnerId) {
            if (runnerId == 0) {
                return "root";
            }
            return FirstRegistrations().TryGetValue(runnerId, out var entry) ? (entry.Kind == EntryKind.Phase ? "phase" : "group") : "unreachable";
        }

        /// <summary> Levels below the root along the first registrations (the root is 0). </summary>
        public int DepthOf(int runnerId) {
            var first = FirstRegistrations();
            int depth = 0;
            int id = runnerId;
            while (id != 0 && first.TryGetValue(id, out var entry) && depth < 16) {
                depth++;
                id = FindEntry(entry.Id).Owner?.Id ?? 0;
            }
            return depth;
        }

        /// <summary> A copy without the second and later registrations of each nested runner (2.2.2 rejects them), and without the steps that named them. </summary>
        public CaseSpec WithoutExtraSlots() {
            var c = Clone();
            var first = c.FirstRegistrations();
            foreach (var r in c.Runners) {
                r.Entries.RemoveAll(e => (e.Kind == EntryKind.Group || e.Kind == EntryKind.Phase) && first.TryGetValue(e.RunnerId, out var f) && !ReferenceEquals(f, e));
            }
            c.Normalize();
            return c;
        }

        public CaseSpec Clone() {
            return FromJson(ToJson());
        }

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions {
            IncludeFields = true,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        public string ToJson() {
            return JsonSerializer.Serialize(this, Json);
        }

        public static CaseSpec FromJson(string json) {
            return JsonSerializer.Deserialize<CaseSpec>(json, Json);
        }

        /// <summary> Drop runners no entry reaches and ops that point at removed runners or entries. </summary>
        public void Normalize() {
            var reachable = new HashSet<int>(Reachable().Select(r => r.Id));
            Runners.RemoveAll(r => !reachable.Contains(r.Id));
            var entryIds = new HashSet<int>(AllEntries().Select(p => p.Entry.Id));
            Ops.RemoveAll(op => {
                switch (op.Kind) {
                    case OpKind.SetActiveIndex:
                        return !entryIds.Contains(op.EntryId) || FindEntry(op.EntryId).Owner.Id != op.RunnerId;
                    case OpKind.SetActiveName:
                        if (!entryIds.Contains(op.EntryId)) {
                            return true;
                        }
                        var (owner, entry) = FindEntry(op.EntryId);
                        return owner.Id != op.RunnerId || entry.Name == null;
                    case OpKind.SetActiveSelf:
                    case OpKind.UnknownName:
                        return !reachable.Contains(op.RunnerId);
                    default:
                        return false;
                }
            });
        }

        public int TreeSize() {
            return Runners.Sum(r => r.Entries.Count + r.OneFrames.Count);
        }

        /// <summary> One-line description of the tree, e.g. r0[s1:InitDestroy, g2:group r1[s3:DestroyOnly]]. </summary>
        public string Describe() {
            var seen = new HashSet<int>();
            string Runner(int id) {
                var r = RunnerOrNull(id);
                if (r == null) {
                    return "r" + id + "?";
                }
                if (!seen.Add(id)) {
                    return "r" + id + "(again)";
                }
                var parts = new List<string>();
                foreach (var e in r.Entries) {
                    parts.Add(Entry(e));
                }
                foreach (var of in r.OneFrames) {
                    parts.Add("OneFrame<Ev" + of.Type + ">" + (of.CleanWhenDisabled ? "(clean)" : ""));
                }
                return "r" + id + (r.StartDisabled ? "(off)" : "") + "[" + string.Join(", ", parts) + "]";
            }
            string Entry(EntrySpec e) {
                switch (e.Kind) {
                    case EntryKind.Leaf:
                        var flags = new List<string>();
                        if (e.FailInit > 0) flags.Add("failInit" + e.FailInit);
                        if (e.FailRun > 0) flags.Add("failRun" + e.FailRun);
                        if (e.ThrowOnDestroy) flags.Add("throwDestroy");
                        if (e.Produce >= 0) flags.Add("produce" + e.Produce);
                        if (e.Observe) flags.Add("observe");
                        return e.Label + (e.Name != null ? "\"" + e.Name + "\"" : "") + ":" + Probes.ClassName(e.Caps) +
                               (flags.Count > 0 ? "{" + string.Join(",", flags) + "}" : "");
                    case EntryKind.Group:
                        return (e.Name != null ? "group \"" + e.Name + "\" " : "group ") + Runner(e.RunnerId);
                    case EntryKind.Phase:
                        return "phase \"" + e.Name + "\" " + Runner(e.RunnerId);
                    default:
                        return "DelHere<Ev" + e.DelType + ">";
                }
            }
            return Runner(0);
        }
    }
}
