// Targeted scenarios: fixed cases for the situations the 2.2.0 review found (rollback of
// Init-less systems, nested AggregateException, one runner in two slots) and their neighbours,
// run through the same model/executor as the random tester. Each scenario says which rule it
// aims at; a scenario that the version cannot express is skipped as "not in this version".
using System;
using System.Collections.Generic;
using System.Linq;
using Stand.Runner.Kit;

namespace Stand.Runner {
    public sealed class Scenario {
        public string Name;
        public string Title;
        /// <summary> Rule the scenario aims at (for the report); null = a control that must pass. </summary>
        public string Targets;
        public Func<Ctx, string> Unsupported = _ => null;
        public Func<Ctx, CaseSpec> Build;
        /// <summary> Contract question to record from the outcome (optional). </summary>
        public Func<Ctx, CaseSpec, CaseResult, ContractQuestion> Question;
        /// <summary> Extra observation over the whole case for the report (optional), e.g. a lifecycle call tally. </summary>
        public Func<Ctx, CaseSpec, string> Observe;
    }

    public static class Lifecycle {
        /// <summary>
        /// Runs every operation of the case on the real runner (no model, no early stop) and counts Init and
        /// Destroy calls of each Init-less system with IDestroySystem: the "Destroy is called once" view of R1.
        /// Frames are skipped while the root is not initialized, like the EcsBootstrap guard.
        /// </summary>
        public static string TallyInitless(Ctx ctx, CaseSpec spec) {
            var exec = new Exec(spec.Clone());
            exec.Build();
            if (exec.BuildError != null) {
                return null;
            }
            foreach (var op in exec.Spec.Ops) {
                exec.Apply(op, new Expectation { Op = op.Kind, FrameSkipped = op.Kind == OpKind.Frame && !exec.Root.IsInitialized });
            }
            var parts = new List<string>();
            foreach (var (_, e) in exec.Spec.AllEntries()) {
                if (e.Kind != EntryKind.Leaf || (e.Caps & Probes.D) == 0 || (e.Caps & Probes.I) != 0) {
                    continue;
                }
                int inits = exec.J.Log.Count(t => t == "init:" + e.Label);
                int destroys = exec.J.Log.Count(t => t == "destroy:" + e.Label);
                parts.Add(e.Label + " (" + Probes.ClassName(e.Caps) + "): Init " + inits + ", Destroy " + destroys);
            }
            if (parts.Count == 0) {
                return null;
            }
            return "over the whole case (" + string.Join(" -> ", exec.Spec.Ops.Select(op => ReproGen.Describe(op, exec.Spec))) + "): " + string.Join("; ", parts);
        }
    }

    /// <summary>
    /// A contract question: behaviour the documents of the tree under test do not pin down. Either an observation of
    /// open behaviour (kind "observation", with ObservedKey: a short stable form of what was seen, compared with the
    /// baseline), or a rule whose violation the contract profile does not count as a defect for this version (kind
    /// "rule", with the signatures, hits and repros of a finding).
    /// </summary>
    public sealed class ContractQuestion {
        public string Id;
        public string Status = "question";
        public string Kind = "observation";
        public string Title;
        public string Question;
        public string Observed;
        public string WhyNotFinding;
        public string ObservedKey;
        // Rule questions only.
        public string Rule;
        public string Known;
        public string Severity;
        public string Since;
        public List<string> Sources;
        public List<SignatureInfo> Signatures;
        public string Repro;
        public string Case;
        public int RandomHits;
        public bool? ReproVerified;
    }

    /// <summary> Small builder for scenario cases. </summary>
    public sealed class SpecBuilder {
        public readonly CaseSpec Case;
        private int _nextEntry = 1;
        private int _nextRunner = 1;

        public SpecBuilder(string origin) {
            Case = new CaseSpec { Origin = origin };
            Case.Runners.Add(new RunnerSpec { Id = 0 });
        }

        public RunnerSpec Root => Case.Root;

        public RunnerSpec NewRunner() {
            var r = new RunnerSpec { Id = _nextRunner++ };
            Case.Runners.Add(r);
            return r;
        }

        public EntrySpec Leaf(RunnerSpec r, int caps, string name = null, Action<EntrySpec> configure = null) {
            var e = new EntrySpec { Id = _nextEntry++, Kind = EntryKind.Leaf, Caps = caps, Name = name };
            configure?.Invoke(e);
            r.Entries.Add(e);
            return e;
        }

        public EntrySpec Group(RunnerSpec r, RunnerSpec child, string name = null, int api = 0) {
            var e = new EntrySpec { Id = _nextEntry++, Kind = EntryKind.Group, RunnerId = child.Id, Name = name, Api = api };
            r.Entries.Add(e);
            return e;
        }

        public EntrySpec Phase(RunnerSpec r, RunnerSpec child, string name, int api = 0) {
            var e = new EntrySpec { Id = _nextEntry++, Kind = EntryKind.Phase, RunnerId = child.Id, Name = name, Api = api };
            r.Entries.Add(e);
            return e;
        }

        public SpecBuilder Op(OpKind kind, int runner = 0, int entry = -1, bool value = false, int type = 0) {
            Case.Ops.Add(new OpSpec { Kind = kind, RunnerId = runner, EntryId = entry, Value = value, Type = type });
            return this;
        }
    }

    public static class Scenarios {
        private const int I = Probes.I;
        private const int R = Probes.R;
        private const int D = Probes.D;

        private static string Need22(Ctx ctx) {
            return ctx.Contract == Contract.V22 ? null : "the 2.2 rollback/group contract is not in this version";
        }

        private static string NeedGroups(Ctx ctx) {
            if (ctx.Contract != Contract.V22) {
                return "AddGroup/AddPhase are not in this version";
            }
            return Api.HasAddGroup && Api.HasAddPhase ? null : "AddGroup/AddPhase are not in this version";
        }

        private static string NeedSelf(Ctx ctx) {
            return NeedGroups(ctx) ?? (Api.HasSetActiveSelf ? null : "SetActive(bool) is not in this version");
        }

        private static string NeedClean(Ctx ctx) {
            return Need22(ctx) ?? (Api.HasCleanWhenDisabled ? null : "OneFrame<T>(cleanWhenDisabled) is not in this version");
        }

        public static List<Scenario> All() {
            var list = new List<Scenario>();

            // ------------------------------------------------ R1: Init-less systems in the rollback
            list.Add(new Scenario {
                Name = "rollback-destroy-only-direct",
                Title = "Rollback: DestroyOnly before a failing Init in the same runner; then Init again and Destroy",
                Targets = Rules.RollbackDestroysNoInit,
                Unsupported = Need22,
                Build = ctx => {
                    var b = new SpecBuilder("scenario:rollback-destroy-only-direct");
                    b.Leaf(b.Root, D);
                    b.Leaf(b.Root, I | D, configure: e => e.FailInit = 1);
                    return b.Op(OpKind.Init).Op(OpKind.Init).Op(OpKind.Destroy).Case;
                },
                Observe = Lifecycle.TallyInitless
            });
            list.Add(new Scenario {
                Name = "rollback-run-destroy-direct",
                Title = "Rollback: RunDestroy (IRunSystem + IDestroySystem, no Init) before a failing Init",
                Targets = Rules.RollbackDestroysNoInit,
                Unsupported = Need22,
                Build = ctx => {
                    var b = new SpecBuilder("scenario:rollback-run-destroy-direct");
                    b.Leaf(b.Root, I | D);
                    b.Leaf(b.Root, R | D);
                    b.Leaf(b.Root, I | D, configure: e => e.FailInit = 1);
                    return b.Op(OpKind.Init).Op(OpKind.Init).Op(OpKind.Frame).Op(OpKind.Destroy).Case;
                },
                Observe = Lifecycle.TallyInitless
            });
            list.Add(new Scenario {
                Name = "rollback-destroy-only-in-failing-group",
                Title = "Rollback: DestroyOnly inside a group whose Init fails",
                Targets = Rules.RollbackDestroysNoInit,
                Unsupported = Need22,
                Build = ctx => {
                    var b = new SpecBuilder("scenario:rollback-destroy-only-in-failing-group");
                    var g = b.NewRunner();
                    b.Leaf(b.Root, I | D);
                    b.Group(b.Root, g);
                    b.Leaf(g, D);
                    b.Leaf(g, I | D, configure: e => e.FailInit = 1);
                    return b.Op(OpKind.Init).Op(OpKind.Init).Op(OpKind.Destroy).Case;
                },
                Observe = Lifecycle.TallyInitless
            });
            list.Add(new Scenario {
                Name = "rollback-destroy-only-in-failing-phase",
                Title = "Rollback: DestroyOnly inside a phase whose Init fails",
                Targets = Rules.RollbackDestroysNoInit,
                Unsupported = Need22,
                Build = ctx => {
                    var b = new SpecBuilder("scenario:rollback-destroy-only-in-failing-phase");
                    var p = b.NewRunner();
                    b.Leaf(b.Root, I | R | D);
                    b.Phase(b.Root, p, "fixed", api: Api.HasAddPhase ? 0 : 1);
                    b.Leaf(p, D);
                    b.Leaf(p, I | D, configure: e => e.FailInit = 1);
                    return b.Op(OpKind.Init).Op(OpKind.Init).Op(OpKind.Destroy).Case;
                },
                Observe = Lifecycle.TallyInitless
            });
            list.Add(new Scenario {
                Name = "rollback-destroy-only-in-completed-group",
                Title = "Rollback: DestroyOnly inside a group whose Init completed before the failing system (a contract question up to 2.2.1; 2.2.2: no Destroy)",
                Targets = null,
                Unsupported = Need22,
                Build = ctx => {
                    var b = new SpecBuilder("scenario:rollback-destroy-only-in-completed-group");
                    var g = b.NewRunner();
                    b.Group(b.Root, g);
                    b.Leaf(g, D);
                    b.Leaf(g, I | D);
                    b.Leaf(b.Root, I | D, configure: e => e.FailInit = 1);
                    return b.Op(OpKind.Init).Op(OpKind.Init).Op(OpKind.Destroy).Case;
                },
                Observe = Lifecycle.TallyInitless,
                Question = (ctx, c, result) => {
                    if (ctx.Profile.RollbackOnlyInitCompleted) {
                        // 2.2.2 answers it: the model forbids that Destroy (rule R1, place rollback-completed).
                        return null;
                    }
                    var spec = c.Clone();
                    var model = new Model(spec, ctx);
                    var exec = new Exec(spec);
                    exec.Build();
                    var exp = model.Apply(spec.Ops[0]);
                    var act = exec.Apply(spec.Ops[0], exp);
                    bool called = act.Log.Contains("destroy:s2");
                    return new ContractQuestion {
                        Id = "Q-ROLLBACK-INITLESS-IN-COMPLETED-RUNNER",
                        Title = "Does a Destroy-only system get Destroy when the parent's rollback tears down a nested runner whose Init completed?",
                        Question = "The XML docs and runner.md say \"every IDestroySystem registered before the failed system gets Destroy\" and \"destroys what came before that runner\", " +
                                   "while ISystem.cs says \"Called once during SystemsRunner.Destroy()\". For a group that initialized completely and is torn down by its parent's rollback " +
                                   "both readings hold: it is a Destroy of the runner and a rollback of Init. 2.2.2 answers it: only systems whose Init completed are destroyed.",
                        Observed = called
                            ? "it does: rollback log [" + string.Join(" ", act.Log) + "]; after the next Init and Destroy s2 gets a second Destroy (the group went through two Init cycles)"
                            : "it does not: rollback log [" + string.Join(" ", act.Log) + "]",
                        ObservedKey = called ? "destroyed" : "not-destroyed",
                        WhyNotFinding = "Up to 2.2.1 the documents do not decide it and the stand accepts both. A finding (R1) is only a Destroy of an Init-less system in the runner whose own Init failed."
                    };
                }
            });

            // ------------------------------------------------ R2: nested rollback, AggregateException structure
            list.Add(new Scenario {
                Name = "rollback-aggregate-two-levels",
                Title = "Nested rollback: Destroy throws both in the group and in the root (F5a)",
                Targets = Rules.RollbackAggregate,
                Unsupported = Need22,
                Build = ctx => {
                    var b = new SpecBuilder("scenario:rollback-aggregate-two-levels");
                    var g = b.NewRunner();
                    b.Leaf(b.Root, I | D, configure: e => e.ThrowOnDestroy = true);
                    b.Group(b.Root, g);
                    b.Leaf(g, I | D, configure: e => e.ThrowOnDestroy = true);
                    b.Leaf(g, I | D, configure: e => e.FailInit = 1);
                    b.Leaf(b.Root, I | D);
                    return b.Op(OpKind.Init).Op(OpKind.Init).Op(OpKind.Destroy).Case;
                }
            });
            list.Add(new Scenario {
                Name = "rollback-aggregate-three-levels",
                Title = "Nested rollback over three levels, every level throws (F5b)",
                Targets = Rules.RollbackAggregate,
                Unsupported = Need22,
                Build = ctx => {
                    var b = new SpecBuilder("scenario:rollback-aggregate-three-levels");
                    var mid = b.NewRunner();
                    var inner = b.NewRunner();
                    b.Leaf(b.Root, I | D, configure: e => e.ThrowOnDestroy = true);
                    b.Group(b.Root, mid);
                    b.Leaf(mid, I | D, configure: e => e.ThrowOnDestroy = true);
                    b.Group(mid, inner);
                    b.Leaf(inner, I | D, configure: e => e.ThrowOnDestroy = true);
                    b.Leaf(inner, I | D, configure: e => e.FailInit = 1);
                    return b.Op(OpKind.Init).Case;
                }
            });
            list.Add(new Scenario {
                Name = "rollback-aggregate-phase",
                Title = "Nested rollback through a phase (like \"fixed\" in EcsBootstrap), both levels throw (F5e)",
                Targets = Rules.RollbackAggregate,
                Unsupported = Need22,
                Build = ctx => {
                    var b = new SpecBuilder("scenario:rollback-aggregate-phase");
                    var p = b.NewRunner();
                    b.Leaf(b.Root, I | D, configure: e => e.ThrowOnDestroy = true);
                    b.Phase(b.Root, p, "fixed", api: Api.HasAddPhase ? 0 : 1);
                    b.Leaf(p, I | D, configure: e => e.ThrowOnDestroy = true);
                    b.Leaf(p, I | D, configure: e => e.FailInit = 1);
                    return b.Op(OpKind.Init).Case;
                }
            });
            list.Add(new Scenario {
                Name = "rollback-aggregate-inner-only",
                Title = "Control: only the Destroy in the group throws (F5c) — the group's AggregateException passes through as is",
                Targets = null,
                Unsupported = Need22,
                Build = ctx => {
                    var b = new SpecBuilder("scenario:rollback-aggregate-inner-only");
                    var g = b.NewRunner();
                    b.Leaf(b.Root, I | D);
                    b.Group(b.Root, g);
                    b.Leaf(g, I | D, configure: e => e.ThrowOnDestroy = true);
                    b.Leaf(g, I | D, configure: e => e.FailInit = 1);
                    return b.Op(OpKind.Init).Case;
                }
            });
            list.Add(new Scenario {
                Name = "rollback-aggregate-outer-only",
                Title = "Control: only the Destroy in the root throws (F5d)",
                Targets = null,
                Unsupported = Need22,
                Build = ctx => {
                    var b = new SpecBuilder("scenario:rollback-aggregate-outer-only");
                    var g = b.NewRunner();
                    b.Leaf(b.Root, I | D, configure: e => e.ThrowOnDestroy = true);
                    b.Group(b.Root, g);
                    b.Leaf(g, I | D);
                    b.Leaf(g, I | D, configure: e => e.FailInit = 1);
                    return b.Op(OpKind.Init).Case;
                }
            });
            list.Add(new Scenario {
                Name = "rollback-flat",
                Title = "Control: flat rollback, two throwing Destroy — order and AggregateException(original, ...)",
                Targets = null,
                Unsupported = Need22,
                Build = ctx => {
                    var b = new SpecBuilder("scenario:rollback-flat");
                    b.Leaf(b.Root, I | D, configure: e => e.ThrowOnDestroy = true);
                    b.Leaf(b.Root, I | D, configure: e => e.ThrowOnDestroy = true);
                    b.Leaf(b.Root, I | D, configure: e => e.FailInit = 1);
                    b.Leaf(b.Root, I | D);
                    return b.Op(OpKind.Init).Op(OpKind.Destroy).Op(OpKind.Init).Op(OpKind.Destroy).Op(OpKind.Destroy).Case;
                }
            });
            list.Add(new Scenario {
                Name = "init-failure-retry",
                Title = "Control: failed Init, Destroy (no-op), Init again (2.2: from the start, 2.1: from the failed system), Run, Destroy",
                Targets = null,
                Build = ctx => {
                    var b = new SpecBuilder("scenario:init-failure-retry");
                    var g = b.NewRunner();
                    b.Leaf(b.Root, I | R | D, "a");
                    b.Group(b.Root, g);
                    b.Leaf(g, I | R | D);
                    b.Leaf(g, I | R | D, configure: e => e.FailInit = 1);
                    b.Leaf(b.Root, I | R | D);
                    return b.Op(OpKind.Init).Op(OpKind.Destroy).Op(OpKind.Frame).Op(OpKind.Init).Op(OpKind.Frame)
                        .Op(OpKind.Destroy).Op(OpKind.Destroy).Op(OpKind.Warmup).Op(OpKind.Frame).Op(OpKind.Destroy).Case;
                }
            });

            // ------------------------------------------------ R3: one runner in two slots
            list.Add(new Scenario {
                Name = "shared-group-two-named-slots",
                Title = "One group in two slots: AddGroup(\"a\", G).AddGroup(\"b\", G), SetActive(\"a\", false) (F6a)",
                Targets = Rules.SharedSlots,
                Unsupported = NeedGroups,
                Build = ctx => {
                    var b = new SpecBuilder("scenario:shared-group-two-named-slots");
                    var g = b.NewRunner();
                    var a = b.Group(b.Root, g, "a");
                    b.Group(b.Root, g, "b");
                    b.Leaf(g, I | R | D);
                    return b.Op(OpKind.Init).Op(OpKind.Frame).Op(OpKind.SetActiveName, 0, a.Id, false).Op(OpKind.Frame).Case;
                }
            });
            list.Add(new Scenario {
                Name = "shared-group-by-index",
                Title = "One unnamed group in two slots: Add(G).Add(G), SetActive(0, false) (F6b)",
                Targets = Rules.SharedSlots,
                Build = ctx => {
                    var b = new SpecBuilder("scenario:shared-group-by-index");
                    var g = b.NewRunner();
                    var first = b.Group(b.Root, g);
                    b.Group(b.Root, g);
                    b.Leaf(g, I | R | D);
                    return b.Op(OpKind.Init).Op(OpKind.Frame).Op(OpKind.SetActiveIndex, 0, first.Id, false).Op(OpKind.Frame)
                        .Op(OpKind.SetActiveIndex, 0, first.Id, true).Op(OpKind.Frame).Op(OpKind.Destroy).Case;
                }
            });
            list.Add(new Scenario {
                Name = "shared-group-phase-and-inline",
                Title = "One runner as a phase and as a group: AddPhase(\"p\", G).Add(G), SetActive(\"p\", false) (F6e)",
                Targets = Rules.SharedSlots,
                Unsupported = NeedGroups,
                Build = ctx => {
                    var b = new SpecBuilder("scenario:shared-group-phase-and-inline");
                    var g = b.NewRunner();
                    var p = b.Phase(b.Root, g, "p");
                    b.Group(b.Root, g);
                    b.Leaf(g, I | R | D);
                    return b.Op(OpKind.Init).Op(OpKind.Frame).Op(OpKind.SetActiveName, 0, p.Id, false).Op(OpKind.Frame).Case;
                }
            });
            list.Add(new Scenario {
                Name = "shared-group-two-parents",
                Title = "One group in two parents: A[G], B[G], A.SetActive(0, false) (F6d)",
                Targets = Rules.SharedSlots,
                Build = ctx => {
                    var b = new SpecBuilder("scenario:shared-group-two-parents");
                    var a = b.NewRunner();
                    var bb = b.NewRunner();
                    var g = b.NewRunner();
                    b.Group(b.Root, a);
                    b.Group(b.Root, bb);
                    var inA = b.Group(a, g);
                    b.Group(bb, g);
                    b.Leaf(g, I | R | D);
                    return b.Op(OpKind.Init).Op(OpKind.Frame).Op(OpKind.SetActiveIndex, a.Id, inA.Id, false).Op(OpKind.Frame).Case;
                }
            });
            list.Add(new Scenario {
                Name = "shared-group-set-active-self",
                Title = "Control: a group in two slots, G.SetActive(false) switches every slot",
                Targets = null,
                Unsupported = NeedSelf,
                Build = ctx => {
                    var b = new SpecBuilder("scenario:shared-group-set-active-self");
                    var g = b.NewRunner();
                    b.Group(b.Root, g, "a");
                    b.Group(b.Root, g, "b");
                    b.Leaf(g, I | R | D);
                    return b.Op(OpKind.Init).Op(OpKind.SetActiveSelf, g.Id, value: false).Op(OpKind.Frame)
                        .Op(OpKind.SetActiveSelf, g.Id, value: true).Op(OpKind.Frame).Op(OpKind.Destroy).Case;
                }
            });

            // ------------------------------------------------ groups, phases, flags (controls)
            list.Add(new Scenario {
                Name = "groups-and-phases",
                Title = "Control: named and unnamed groups, a phase; SetActive by index, name and reference; only the root ticks",
                Targets = null,
                Unsupported = NeedSelf,
                Build = ctx => {
                    var b = new SpecBuilder("scenario:groups-and-phases");
                    var named = b.NewRunner();
                    var unnamed = b.NewRunner();
                    var phase = b.NewRunner();
                    b.Leaf(b.Root, I | R | D, "move");
                    var gn = b.Group(b.Root, named, "sim");
                    b.Leaf(named, R, "hit");
                    b.Leaf(named, I | R | D);
                    var gu = b.Group(b.Root, unnamed);
                    b.Leaf(unnamed, R | D);
                    var ph = b.Phase(b.Root, phase, "fixed");
                    b.Leaf(phase, I | R | D);
                    var initOnly = b.Leaf(b.Root, I, "boot");
                    b.Leaf(b.Root, D);
                    return b.Op(OpKind.Init).Op(OpKind.Frame)
                        .Op(OpKind.SetActiveName, 0, gn.Id, false).Op(OpKind.Frame)
                        .Op(OpKind.SetActiveIndex, 0, gu.Id, false).Op(OpKind.Frame)
                        .Op(OpKind.SetActiveSelf, unnamed.Id, value: true).Op(OpKind.Frame)
                        .Op(OpKind.SetActiveName, 0, ph.Id, false).Op(OpKind.Frame)
                        .Op(OpKind.SetActiveSelf, 0, value: false).Op(OpKind.Frame)
                        .Op(OpKind.SetActiveSelf, 0, value: true)
                        .Op(OpKind.SetActiveIndex, 0, initOnly.Id, false)
                        .Op(OpKind.SetActiveName, 0, initOnly.Id, false)
                        .Op(OpKind.UnknownName, 0, type: 0).Op(OpKind.UnknownName, 0, type: 1).Op(OpKind.UnknownName, 0, type: 2)
                        .Op(OpKind.Destroy).Op(OpKind.Destroy).Case;
                }
            });
            list.Add(new Scenario {
                Name = "group-start-disabled",
                Title = "Control: a group switched off with SetActive(false) before AddGroup starts with its slot off",
                Targets = null,
                Unsupported = NeedSelf,
                Build = ctx => {
                    var b = new SpecBuilder("scenario:group-start-disabled");
                    var g = b.NewRunner();
                    g.StartDisabled = true;
                    b.Group(b.Root, g, "paused");
                    b.Leaf(g, R);
                    b.Leaf(b.Root, R);
                    return b.Op(OpKind.Init).Op(OpKind.Frame).Op(OpKind.SetActiveSelf, g.Id, value: true).Op(OpKind.Frame).Case;
                }
            });
            list.Add(new Scenario {
                Name = "obsolete-add-runner-name",
                Title = "Control: Add(runner, name) is a phase, Add(runner, null) an unnamed group",
                Targets = null,
                Build = ctx => {
                    var b = new SpecBuilder("scenario:obsolete-add-runner-name");
                    var p = b.NewRunner();
                    var g = b.NewRunner();
                    b.Phase(b.Root, p, "fixed", api: 2);
                    b.Leaf(p, R);
                    b.Group(b.Root, g, null, api: 1);
                    b.Leaf(g, R);
                    return b.Op(OpKind.Init).Op(OpKind.Frame).Op(OpKind.Destroy).Case;
                }
            });

            // ------------------------------------------------ Run exceptions and OneFrame
            list.Add(new Scenario {
                Name = "run-throws-cleanup",
                Title = "Control: a system throws in Run — OneFrame is still cleaned, the systems after it are skipped",
                Targets = null,
                Build = ctx => {
                    var b = new SpecBuilder("scenario:run-throws-cleanup");
                    var g = b.NewRunner();
                    b.Leaf(b.Root, R, configure: e => e.Produce = 0);
                    b.Group(b.Root, g);
                    b.Leaf(g, R, configure: e => e.Produce = 1);
                    b.Leaf(g, R, configure: e => e.FailRun = 1);
                    g.OneFrames.Add(new OneFrameSpec { Type = 1 });
                    b.Leaf(b.Root, R, configure: e => e.Observe = true);
                    b.Root.OneFrames.Add(new OneFrameSpec { Type = 0 });
                    return b.Op(OpKind.Init).Op(OpKind.Frame).Op(OpKind.Frame).Case;
                }
            });
            list.Add(new Scenario {
                Name = "oneframe-skipped-slot",
                Title = "Control: cleanWhenDisabled on the disabled slot of a group is cleaned at the slot's position; a plain OneFrame is kept",
                Targets = null,
                Unsupported = NeedClean,
                Build = ctx => {
                    var b = new SpecBuilder("scenario:oneframe-skipped-slot");
                    var g = b.NewRunner();
                    b.Leaf(b.Root, R, configure: e => e.Produce = 0);
                    b.Leaf(b.Root, R, configure: e => e.Produce = 1);
                    var slot = b.Group(b.Root, g, "g");
                    b.Leaf(g, R, configure: e => e.Observe = true);
                    g.OneFrames.Add(new OneFrameSpec { Type = 0, CleanWhenDisabled = true });
                    g.OneFrames.Add(new OneFrameSpec { Type = 1, CleanWhenDisabled = false });
                    b.Leaf(b.Root, R, configure: e => e.Observe = true);
                    return b.Op(OpKind.Init).Op(OpKind.Frame).Op(OpKind.SetActiveName, 0, slot.Id, false).Op(OpKind.Frame).Op(OpKind.Frame)
                        .Op(OpKind.SetActiveName, 0, slot.Id, true).Op(OpKind.Frame).Case;
                }
            });
            list.Add(new Scenario {
                Name = "oneframe-disabled-phase",
                Title = "Control: Run() of a disabled phase removes only its cleanWhenDisabled types",
                Targets = null,
                Unsupported = NeedClean,
                Build = ctx => {
                    var b = new SpecBuilder("scenario:oneframe-disabled-phase");
                    var p = b.NewRunner();
                    b.Leaf(b.Root, R, configure: e => e.Observe = true);
                    var ph = b.Phase(b.Root, p, "late");
                    b.Leaf(p, R);
                    p.OneFrames.Add(new OneFrameSpec { Type = 2, CleanWhenDisabled = true });
                    p.OneFrames.Add(new OneFrameSpec { Type = 3 });
                    return b.Op(OpKind.Init).Op(OpKind.SetActiveName, 0, ph.Id, false).Op(OpKind.Emit, type: 2).Op(OpKind.Emit, type: 3)
                        .Op(OpKind.Frame).Op(OpKind.Frame).Case;
                }
            });
            return list;
        }
    }
}
