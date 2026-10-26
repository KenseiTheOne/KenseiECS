// The documented contract of SystemsRunner as an executable model. It is the oracle: for every
// operation it predicts the probe log (with optional and forbidden tokens where the contract
// allows either or neither), the exception, the tick, the OneFrame pool counts and what the
// introspection API must report. Every rule cites the documentation it comes from; behaviour the
// documents leave open is modelled as optional / unknown, never as a requirement.
//
// Sources (2.2.x): SystemsRunner XML docs, ISystem.cs XML docs, docs/concepts/runner.md,
// CHANGELOG 2.2.0. Sources (2.1.x): the same files at tag v2.1.1.
using System;
using System.Collections.Generic;
using System.Linq;
using Stand.Runner.Kit;

namespace Stand.Runner {
    public sealed class MRunner {
        public int Id;
        public RunnerSpec Spec;
        public readonly List<MEntry> Entries = new List<MEntry>();
        public readonly List<OneFrameSpec> OneFrames = new List<OneFrameSpec>();
        public bool Enabled = true;
        public bool Initialized;
        /// <summary> 2.1: number of Init entries completed (a failed Init resumes from here). </summary>
        public int Progress;
        public bool IsRoot;
        /// <summary> Entries referencing this runner; more than one = shared slots. </summary>
        public int Appearances;
        /// <summary> How the runner is registered ("root", "group", "phase"), for the place of a signature. </summary>
        public string Where;
        /// <summary> Levels below the root (0 for the root). </summary>
        public int Depth;
    }

    public sealed class MEntry {
        public EntrySpec Spec;
        public MRunner Owner;
        public MRunner Child;
        /// <summary> Run slot of a leaf or DelHere (and of an inline group under the 2.1 contract). </summary>
        public bool Slot = true;
        public int FailInitLeft;
        public int FailRunLeft;

        public bool IsLeaf => Spec.Kind == EntryKind.Leaf;
        public bool IsGroup => Spec.Kind == EntryKind.Group;
        public bool IsPhase => Spec.Kind == EntryKind.Phase;
        public bool IsDelHere => Spec.Kind == EntryKind.DelHere;
        public bool LeafI => IsLeaf && (Spec.Caps & Probes.I) != 0;
        public bool LeafR => IsLeaf && (Spec.Caps & Probes.R) != 0;
        public bool LeafD => IsLeaf && (Spec.Caps & Probes.D) != 0;
        public bool HasInit => LeafI || Child != null;
        public bool HasDestroy => LeafD || Child != null;
        /// <summary> Part of the owner's Run pipeline: run systems, inline groups and DelHere (SystemInfo.IsRunnable). </summary>
        public bool Runnable => LeafR || IsGroup || IsDelHere;
        public string Label => Spec.Label;

        public string TypeName {
            get {
                switch (Spec.Kind) {
                    case EntryKind.Leaf: return Probes.ClassName(Spec.Caps);
                    case EntryKind.DelHere: return "DelHere<Ev" + Spec.DelType + ">";
                    default: return "SystemsRunner";
                }
            }
        }

        /// <summary> SystemInfo.Name: the registration name, or the type name. </summary>
        public string InfoName => Spec.Kind == EntryKind.DelHere ? TypeName : Spec.Name ?? TypeName;
    }

    /// <summary> What one operation must do, per the contract. </summary>
    public sealed class Expectation {
        public OpKind Op;
        public readonly List<LogToken> Log = new List<LogToken>();
        /// <summary> Tokens the contract rules out, with the rule that forbids them and the place for the signature. </summary>
        public readonly Dictionary<string, (string Rule, string Place)> Forbidden = new Dictionary<string, (string Rule, string Place)>();
        /// <summary> For "see:" tokens: why each count has its value (index of the token in Log → reasons per type). </summary>
        public readonly Dictionary<int, string[]> SeeReasons = new Dictionary<int, string[]>();

        // Init / Warmup
        public int InitFailEntry = -1;

        // Frame
        public bool FrameSkipped;
        public int RootRunFailEntry = -1;
        public readonly Dictionary<int, int> PhaseRunFailEntry = new Dictionary<int, int>();
        public int Tick;

        // SetActive / IsActive / GetRunner under KENSEI_DEBUG
        public bool ExpectInvalidOperation;
        public string InvalidOperationRule;
        /// <summary> The documents do not say whether the call throws (2.1: SetActive on an Init/Destroy-only system); its exception is not checked. </summary>
        public bool ExceptionUnspecified;
        /// <summary> UnknownName in release: what the call returns (false for IsActive, null for GetRunner). </summary>
        public bool CheckReleaseResult;

        public void Event(string text) {
            Log.Add(new LogToken { Text = text });
        }

        public void Optional(string text) {
            Log.Add(new LogToken { Text = text, Optional = true });
        }

        public void Forbid(string text, string rule, string place) {
            Forbidden[text] = (rule, place);
        }
    }

    public sealed class Model {
        public readonly CaseSpec Spec;
        public readonly Contract Contract;
        public readonly ContractProfile Profile;
        public readonly bool Debug;
        public readonly Dictionary<int, MRunner> Runners = new Dictionary<int, MRunner>();
        public readonly Dictionary<int, MEntry> Entries = new Dictionary<int, MEntry>();
        public MRunner Root;
        public int Tick;
        public readonly List<int> PhaseOrder;

        /// <summary> Ev pool counts; null = the contract does not pin the value down. </summary>
        public readonly int?[] Counts = new int?[Events.TypeCount];
        /// <summary> Why each count has its current value (for classifying OneFrame mismatches). </summary>
        public readonly string[] CountReason = new string[Events.TypeCount];

        private bool V22 => Contract == Contract.V22;

        public Model(CaseSpec spec, Ctx ctx) {
            Spec = spec;
            Profile = ctx.Profile;
            Contract = ctx.Profile.Base;
            Debug = ctx.Debug;
            for (int t = 0; t < Counts.Length; t++) {
                Counts[t] = 0;
                CountReason[t] = "start:nothing removed yet";
            }
            foreach (var r in spec.Reachable()) {
                Runners[r.Id] = new MRunner { Id = r.Id, Spec = r, IsRoot = r.Id == 0 };
            }
            foreach (var r in Runners.Values) {
                foreach (var e in r.Spec.Entries) {
                    var m = new MEntry {
                        Spec = e,
                        Owner = r,
                        FailInitLeft = e.FailInit,
                        FailRunLeft = e.FailRun
                    };
                    if (e.Kind == EntryKind.Group || e.Kind == EntryKind.Phase) {
                        m.Child = Runners[e.RunnerId];
                        m.Child.Appearances++;
                    }
                    r.Entries.Add(m);
                    Entries[e.Id] = m;
                }
                r.OneFrames.AddRange(r.Spec.OneFrames);
            }
            Root = Runners[0];
            foreach (var r in Runners.Values) {
                r.Where = spec.RoleOf(r.Id);
                r.Depth = spec.DepthOf(r.Id);
            }
            foreach (var r in Runners.Values) {
                if (r.Spec.StartDisabled) {
                    // runner.SetActive(false) before registration; the slot starts as the group is
                    // (2.2: "it always equals the group's slot"; wt docs: "the slot starts as the group is").
                    r.Enabled = false;
                }
            }
            PhaseOrder = spec.PhaseOrder();
        }

        // =============================================================== introspection

        /// <summary>
        /// Slot state of a runnable entry. 2.2: an inline group's slot always equals the group's IsEnabled
        /// (runner.md "IsEnabled of a nested runner"); 2.1: the slot is the parent's own flag and the
        /// child keeps IsEnabled == true (runner.md 2.1.1 "IsEnabled of an unnamed child").
        /// </summary>
        public bool SlotEnabled(MEntry e) {
            if (e.IsGroup && V22) {
                return e.Child.Enabled;
            }
            return e.Slot;
        }

        /// <summary> SystemInfo.IsEnabled: the run slot for runnable entries, the phase's IsEnabled for a phase, false otherwise. </summary>
        public bool InfoEnabled(MEntry e) {
            if (e.Runnable) {
                return SlotEnabled(e);
            }
            if (e.IsPhase) {
                return e.Child.Enabled;
            }
            return false;
        }

        public bool IsShared(MRunner r) {
            return r != null && r.Appearances > 1;
        }

        /// <summary> Take over an observed start state (a violation already reported) so that the case can go on. </summary>
        public void AdoptEnabled(MRunner r, bool enabled) {
            r.Enabled = enabled;
        }

        // =============================================================== operations

        public Expectation Apply(OpSpec op) {
            var exp = new Expectation { Op = op.Kind };
            switch (op.Kind) {
                case OpKind.Init:
                case OpKind.Warmup:
                    ApplyInit(exp);
                    break;
                case OpKind.Frame:
                    ApplyFrame(exp);
                    break;
                case OpKind.SetActiveIndex:
                    ApplySetActive(exp, Entries[op.EntryId], op.Value, byName: false);
                    break;
                case OpKind.SetActiveName:
                    ApplySetActive(exp, Entries[op.EntryId], op.Value, byName: true);
                    break;
                case OpKind.SetActiveSelf:
                    // runner.SetActive(bool): the runner's own IsEnabled; for an inline group its slots follow.
                    Runners[op.RunnerId].Enabled = op.Value;
                    break;
                case OpKind.Emit:
                    Produce(op.Type, "emitted outside the runners");
                    break;
                case OpKind.Destroy:
                    DestroyRunner(Root, exp);
                    break;
                case OpKind.UnknownName:
                    // Unknown names: throw under KENSEI_DEBUG; in release SetActive does nothing, IsActive
                    // returns false, GetRunner returns null (XML docs of SetActive/IsActive/GetRunner, both versions).
                    if (Debug) {
                        exp.ExpectInvalidOperation = true;
                        exp.InvalidOperationRule = "unknown-name";
                    } else {
                        exp.CheckReleaseResult = true;
                    }
                    break;
            }
            exp.Tick = Tick;
            return exp;
        }

        private void ApplySetActive(Expectation exp, MEntry e, bool value, bool byName) {
            if (e.Runnable) {
                if (e.IsGroup && V22) {
                    // "For an inline group this sets both its run slot here and the group's IsEnabled."
                    e.Child.Enabled = value;
                } else {
                    e.Slot = value;
                }
                return;
            }
            if (e.IsPhase) {
                // "for a phase it sets the phase's IsEnabled"
                e.Child.Enabled = value;
                return;
            }
            // Init/Destroy-only system: "under KENSEI_DEBUG it throws, in release the call does nothing" (2.2).
            // 2.1 documents toggling run systems and nested runners only: whether such a call throws
            // (by name it is an unknown name under KENSEI_DEBUG, by index it did nothing) is left open;
            // the state stays as it is either way.
            if (!V22) {
                exp.ExceptionUnspecified = true;
                return;
            }
            if (Debug) {
                exp.ExpectInvalidOperation = true;
                exp.InvalidOperationRule = byName ? "setactive-name-not-runnable" : "setactive-index-not-runnable";
            }
        }

        // --------------------------------------------------------------- Init

        private sealed class Failure {
            public MEntry Origin;
        }

        private void ApplyInit(Expectation exp) {
            var failure = V22 ? Init22(Root, exp) : Init21(Root, exp);
            if (failure != null) {
                exp.InitFailEntry = failure.Origin.Spec.Id;
            }
        }

        // 2.2 lifecycle contract (runner.md "Lifecycle contract", XML Init(World, SharedData), CHANGELOG 2.2.0):
        // Init runs each IInitSystem once, in registration order; no-op if already initialized. If one
        // throws, every IDestroySystem registered before it is destroyed in reverse order (a nested runner
        // rolls itself back first, then the parent destroys what came before it), the runner stays
        // uninitialized, and the next Init starts over from the first system.
        private Failure Init22(MRunner r, Expectation exp) {
            if (r.Initialized) {
                return null;
            }
            for (int k = 0; k < r.Entries.Count; k++) {
                var e = r.Entries[k];
                if (!e.HasInit) {
                    continue;
                }
                if (e.Child != null) {
                    var nested = Init22(e.Child, exp);
                    if (nested != null) {
                        Rollback22(r, k, exp);
                        return nested;
                    }
                    continue;
                }
                exp.Event("init:" + e.Label);
                if (e.FailInitLeft > 0) {
                    e.FailInitLeft--;
                    Rollback22(r, k, exp);
                    return new Failure { Origin = e };
                }
            }
            r.Initialized = true;
            return null;
        }

        private void Rollback22(MRunner r, int failedIndex, Expectation exp) {
            r.Initialized = false;
            r.Progress = 0;
            for (int i = failedIndex - 1; i >= 0; i--) {
                var e = r.Entries[i];
                if (!e.HasDestroy) {
                    continue;
                }
                if (e.Child != null) {
                    Teardown22(e.Child, exp);
                } else if (e.LeafI) {
                    exp.Event("destroy:" + e.Label);
                } else {
                    // A system without IInitSystem in the runner whose own Init failed: it has nothing to
                    // undo, and IDestroySystem is "Called once during SystemsRunner.Destroy()" /
                    // "Called once on shutdown" (ISystem.cs). The rollback is there so that "systems that
                    // subscribed to events or allocated in Init can undo it" (runner.md). Whether a call is
                    // a defect or a contract question is the profile's decision (2.2.0/2.2.1: the documents
                    // disagree; 2.2.2: only systems whose Init completed are destroyed).
                    exp.Forbid("destroy:" + e.Label, Rules.RollbackDestroysNoInit, "rollback-failing:" + r.Where + ":" + Probes.ClassName(e.Spec.Caps));
                }
            }
        }

        // A nested runner registered before the failed system completed its Init: the parent's rollback
        // destroys it ("destroys what came before that runner"; CHANGELOG: "each of their systems is
        // destroyed even when another one throws").
        private void Teardown22(MRunner n, Expectation exp) {
            if (!n.Initialized) {
                return;
            }
            n.Initialized = false;
            n.Progress = 0;
            for (int i = n.Entries.Count - 1; i >= 0; i--) {
                var e = n.Entries[i];
                if (!e.HasDestroy) {
                    continue;
                }
                if (e.Child != null) {
                    Teardown22(e.Child, exp);
                } else if (e.LeafI) {
                    exp.Event("destroy:" + e.Label);
                } else if (Profile.RollbackOnlyInitCompleted) {
                    // 2.2.2: the rollback destroys only systems whose Init completed — an Init-less system
                    // is not one of them, wherever it sits.
                    exp.Forbid("destroy:" + e.Label, Rules.RollbackDestroysNoInit, "rollback-completed:" + n.Where + ":" + Probes.ClassName(e.Spec.Caps));
                } else {
                    // Init-less system inside a runner whose Init completed and is now torn down: up to 2.2.1
                    // the documents allow both readings (the runner is destroyed, so its shutdown hooks may
                    // run; or only Init is undone). Contract question Q-ROLLBACK-INITLESS-IN-COMPLETED-RUNNER.
                    exp.Optional("destroy:" + e.Label);
                }
            }
        }

        // 2.1 lifecycle contract (runner.md at v2.1.1): "If one throws, the runner stays uninitialized
        // and the next Init resumes with the system that failed." Nothing is destroyed.
        private Failure Init21(MRunner r, Expectation exp) {
            if (r.Initialized) {
                return null;
            }
            var inits = r.Entries.Where(e => e.HasInit).ToList();
            for (; r.Progress < inits.Count; r.Progress++) {
                var e = inits[r.Progress];
                if (e.Child != null) {
                    var nested = Init21(e.Child, exp);
                    if (nested != null) {
                        return nested;
                    }
                    continue;
                }
                exp.Event("init:" + e.Label);
                if (e.FailInitLeft > 0) {
                    e.FailInitLeft--;
                    return new Failure { Origin = e };
                }
            }
            r.Initialized = true;
            return null;
        }

        // --------------------------------------------------------------- Destroy

        // "Destroy runs IDestroySystem in reverse registration order, is a no-op before Init (and after a
        // failed one, which already rolled back), and resets the runner so Init can run again."
        private void DestroyRunner(MRunner r, Expectation exp) {
            if (!r.Initialized) {
                return;
            }
            r.Initialized = false;
            r.Progress = 0;
            for (int i = r.Entries.Count - 1; i >= 0; i--) {
                var e = r.Entries[i];
                if (!e.HasDestroy) {
                    continue;
                }
                if (e.Child != null) {
                    DestroyRunner(e.Child, exp);
                } else {
                    exp.Event("destroy:" + e.Label);
                }
            }
        }

        // --------------------------------------------------------------- Run

        // A frame: root.Run() (advances the tick), then every phase's Run() (no tick), each in its own
        // try like a Unity message. Skipped while the root is not initialized (the EcsBootstrap guard).
        private void ApplyFrame(Expectation exp) {
            if (!Root.Initialized) {
                exp.FrameSkipped = true;
                return;
            }
            Tick++;
            var thrown = RunRunner(Root, exp);
            if (thrown != null) {
                exp.RootRunFailEntry = thrown.Spec.Id;
            }
            foreach (var id in PhaseOrder) {
                var phaseThrown = RunRunner(Runners[id], exp);
                if (phaseThrown != null) {
                    exp.PhaseRunFailEntry[id] = phaseThrown.Spec.Id;
                }
            }
        }

        // Run executes the enabled systems in registration order, then removes this runner's OneFrame
        // components; the cleanup runs even when a system throws, and the systems after the failing one
        // are skipped that frame. A disabled runner runs no systems; under 2.2 it only removes its
        // cleanWhenDisabled types (and those of its inline groups).
        private MEntry RunRunner(MRunner r, Expectation exp) {
            if (!r.Enabled) {
                if (V22) {
                    SkipClean(r, rootDisabled: r.IsRoot);
                }
                return null;
            }
            MEntry thrown = null;
            foreach (var e in r.Entries) {
                if (!e.Runnable) {
                    continue;
                }
                if (!SlotEnabled(e)) {
                    // 2.2: "When the parent's Run reaches the disabled slot of a group, it removes the
                    // group's cleanWhenDisabled types right there" (cascading into its inline groups).
                    if (V22 && e.IsGroup) {
                        SkipClean(e.Child, rootDisabled: false);
                    }
                    continue;
                }
                if (e.IsDelHere) {
                    Clean(e.Spec.DelType, "delhere:" + e.Label + " in r" + r.Id);
                    continue;
                }
                if (e.IsGroup) {
                    thrown = RunRunner(e.Child, exp);
                    if (thrown != null) {
                        break;
                    }
                    continue;
                }
                exp.Event("run:" + e.Label);
                if (e.FailRunLeft > 0) {
                    e.FailRunLeft--;
                    thrown = e;
                    break;
                }
                if (e.Spec.Observe) {
                    Observe(e, exp);
                }
                if (e.Spec.Produce >= 0) {
                    Produce(e.Spec.Produce, "produced by " + e.Label);
                }
            }
            foreach (var of in r.OneFrames) {
                Clean(of.Type, "end:end of r" + r.Id + ".Run");
            }
            return thrown;
        }

        private void SkipClean(MRunner r, bool rootDisabled) {
            foreach (var of in r.OneFrames) {
                if (of.CleanWhenDisabled) {
                    Clean(of.Type, "skip:r" + r.Id + " skipped (cleanWhenDisabled)");
                }
            }
            foreach (var e in r.Entries) {
                if (!e.IsGroup) {
                    continue;
                }
                if (rootDisabled) {
                    // A disabled root "only removes its cleanWhenDisabled OneFrame types" (SetActive(bool)
                    // XML); whether its groups' types go too is not stated: unknown.
                    MaybeClean(e.Child);
                } else {
                    SkipClean(e.Child, rootDisabled: false);
                }
            }
        }

        private void MaybeClean(MRunner r) {
            foreach (var of in r.OneFrames) {
                if (of.CleanWhenDisabled && Counts[of.Type] != 0) {
                    Counts[of.Type] = null;
                    CountReason[of.Type] = "unknown:group of a disabled root (contract silent)";
                }
            }
            foreach (var e in r.Entries) {
                if (e.IsGroup) {
                    MaybeClean(e.Child);
                }
            }
        }

        // CountReason holds "<kind>:<detail>" of the last removal (kind: end, skip, delhere, unknown, start);
        // a mismatch where the components are still there is classified by it.
        private void Clean(int type, string reason) {
            Counts[type] = 0;
            CountReason[type] = reason;
        }

        private void Produce(int type, string reason) {
            if (Counts[type].HasValue) {
                Counts[type]++;
            }
        }

        private void Observe(MEntry e, Expectation exp) {
            var parts = new string[Counts.Length];
            var reasons = new string[Counts.Length];
            for (int t = 0; t < Counts.Length; t++) {
                parts[t] = Counts[t].HasValue ? Counts[t].Value.ToString() : "?";
                reasons[t] = CountReason[t];
            }
            exp.SeeReasons[exp.Log.Count] = reasons;
            exp.Event("see:" + e.Label + ":" + string.Join(",", parts));
        }
    }
}
