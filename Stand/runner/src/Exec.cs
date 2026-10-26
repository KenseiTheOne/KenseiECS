// Builds the real SystemsRunner tree of a case and applies its operations, recording what
// actually happened: the probe log, exceptions, results. No judgement here — Engine compares.
using System;
using System.Collections.Generic;
using System.Linq;
using KenseiECS;
using Stand.Runner.Kit;

namespace Stand.Runner {
    public sealed class Actual {
        public List<string> Log;
        public List<Exception> DestroyErrors;
        public Exception Thrown;
        public Exception RootThrown;
        public readonly Dictionary<int, Exception> PhaseThrown = new Dictionary<int, Exception>();
        public object Result;
        public int Tick;
    }

    public sealed class Exec {
        public const string UnknownName = "~no-such-name~";

        public readonly CaseSpec Spec;
        public readonly Journal J = new Journal();
        public World World;
        public SystemsRunner Root;
        public readonly Dictionary<int, SystemsRunner> Runners = new Dictionary<int, SystemsRunner>();
        /// <summary> Entry id → the instance passed to Add (probe or nested runner). </summary>
        public readonly Dictionary<int, ISystem> Systems = new Dictionary<int, ISystem>();
        public readonly Dictionary<int, Probe> Probes = new Dictionary<int, Probe>();
        public readonly List<int> PhaseOrder;

        /// <summary> Exception thrown while building, and the registration that threw. </summary>
        public Exception BuildError;
        public EntrySpec BuildErrorEntry;

        public Exec(CaseSpec spec) {
            Spec = spec;
            PhaseOrder = spec.PhaseOrder();
        }

        public void Build() {
            World = new World();
            var disabledOnce = new HashSet<int>();
            try {
                Root = BuildRunner(Spec.Root, disabledOnce);
                foreach (var r in Spec.Reachable()) {
                    foreach (var of in r.OneFrames.Where(o => o.AfterAdd)) {
                        Api.OneFrame(Runners[r.Id], of.Type, of.CleanWhenDisabled);
                    }
                }
                if (Spec.Root.StartDisabled) {
                    Api.SetActiveSelfFn(Root, false);
                }
            } catch (Exception e) {
                BuildError = e;
            }
        }

        private SystemsRunner BuildRunner(RunnerSpec spec, HashSet<int> disabledOnce) {
            var runner = new SystemsRunner(World);
            Runners[spec.Id] = runner;
            foreach (var e in spec.Entries) {
                BuildErrorEntry = e;
                switch (e.Kind) {
                    case EntryKind.Leaf: {
                        var probe = Kit.Probes.Create(e.Caps, e.Label, J);
                        probe.FailInit = e.FailInit;
                        probe.FailRun = e.FailRun;
                        probe.ThrowOnDestroy = e.ThrowOnDestroy;
                        probe.Produce = e.Produce;
                        probe.Observe = e.Observe;
                        Probes[e.Id] = probe;
                        Systems[e.Id] = probe;
                        runner.Add(probe, e.Name);
                        break;
                    }
                    case EntryKind.Group:
                    case EntryKind.Phase: {
                        if (!Runners.TryGetValue(e.RunnerId, out var child)) {
                            child = BuildRunner(Spec.Runner(e.RunnerId), disabledOnce);
                            BuildErrorEntry = e;
                        }
                        var childSpec = Spec.Runner(e.RunnerId);
                        if (childSpec.StartDisabled && disabledOnce.Add(childSpec.Id)) {
                            Api.SetActiveSelfFn(child, false);
                        }
                        Systems[e.Id] = child;
                        if (e.Kind == EntryKind.Group) {
                            Api.AddGroup(runner, e.Name, child, e.Api);
                        } else {
                            Api.AddPhase(runner, e.Name, child, e.Api);
                        }
                        break;
                    }
                    case EntryKind.DelHere:
                        Events.DelHere(runner, e.DelType);
                        break;
                }
            }
            BuildErrorEntry = null;
            foreach (var of in spec.OneFrames.Where(o => !o.AfterAdd)) {
                Api.OneFrame(runner, of.Type, of.CleanWhenDisabled);
            }
            return runner;
        }

        /// <summary> Position of an entry in its runner (what SetActive(int) and GetSystemInfo take). </summary>
        public int IndexInOwner(int entryId) {
            var (owner, entry) = Spec.FindEntry(entryId);
            return owner.Entries.IndexOf(entry);
        }

        public Actual Apply(OpSpec op, Expectation exp) {
            var act = new Actual();
            J.Begin();
            switch (op.Kind) {
                case OpKind.Init:
                    J.DestroyThrowsArmed = true;
                    act.Thrown = Journal.Catch(() => Root.Init());
                    J.DestroyThrowsArmed = false;
                    break;
                case OpKind.Warmup:
                    J.DestroyThrowsArmed = true;
                    act.Thrown = Journal.Catch(() => Root.Warmup());
                    J.DestroyThrowsArmed = false;
                    break;
                case OpKind.Frame:
                    if (exp.FrameSkipped) {
                        break;
                    }
                    act.RootThrown = Journal.Catch(() => Root.Run());
                    foreach (var id in PhaseOrder) {
                        var phase = Runners[id];
                        act.PhaseThrown[id] = Journal.Catch(() => phase.Run());
                    }
                    break;
                case OpKind.SetActiveIndex: {
                    var runner = Runners[op.RunnerId];
                    int index = IndexInOwner(op.EntryId);
                    act.Thrown = Journal.Catch(() => runner.SetActive(index, op.Value));
                    break;
                }
                case OpKind.SetActiveName: {
                    var runner = Runners[op.RunnerId];
                    string name = Spec.FindEntry(op.EntryId).Entry.Name;
                    act.Thrown = Journal.Catch(() => runner.SetActive(name, op.Value));
                    break;
                }
                case OpKind.SetActiveSelf: {
                    var runner = Runners[op.RunnerId];
                    act.Thrown = Journal.Catch(() => Api.SetActiveSelfFn(runner, op.Value));
                    break;
                }
                case OpKind.Emit:
                    Events.Create(World, op.Type);
                    break;
                case OpKind.Destroy:
                    act.Thrown = Journal.Catch(() => Root.Destroy());
                    break;
                case OpKind.UnknownName: {
                    var runner = Runners[op.RunnerId];
                    switch (op.Type) {
                        case 0:
                            act.Thrown = Journal.Catch(() => runner.SetActive(UnknownName, op.Value));
                            break;
                        case 1:
                            act.Thrown = Journal.Catch(() => act.Result = runner.IsActive(UnknownName));
                            break;
                        default:
                            act.Thrown = Journal.Catch(() => act.Result = runner.GetRunner(UnknownName));
                            break;
                    }
                    break;
                }
            }
            act.Log = J.Slice();
            act.DestroyErrors = J.StepDestroyErrors();
            act.Tick = World.Tick;
            return act;
        }
    }
}
