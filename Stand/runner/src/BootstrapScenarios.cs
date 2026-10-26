// EcsBootstrap (KenseiECS/Unity/EcsBootstrap.cs, compiled as is against kit/UnityStubs.cs):
// the documented lifecycle, the IsInitialized guard after a failed Start, and user names that
// collide with the bootstrap's own phases "fixed" and "late" (up to 2.2.1 a contract question;
// 2.2.2: a clear error naming the reserved names).
using System;
using System.Collections.Generic;
using System.Linq;
using KenseiECS;
using Stand.Runner.Kit;

namespace Stand.Runner {
    /// <summary> The subclass a game would write; Configure is supplied per scenario. </summary>
    public sealed class StandBootstrap : EcsBootstrap {
        public Action<SystemsRunner, SystemsRunner, SystemsRunner, SharedData> OnConfigure;

        protected override void Configure(SystemsRunner update, SystemsRunner fixedUpdate, SystemsRunner lateUpdate, SharedData shared) {
            OnConfigure?.Invoke(update, fixedUpdate, lateUpdate, shared);
        }
    }

    public static class BootstrapScenarios {
        public static void RunAll(Ctx ctx, CustomResult result) {
            Lifecycle(ctx, result, warmup: true);
            Lifecycle(ctx, result, warmup: false);
            StartFails(ctx, result);
            UserName(ctx, result, "fixed", group: false);
            UserName(ctx, result, "late", group: false);
            if (ctx.Contract == Contract.V22 && Api.HasAddGroup) {
                UserName(ctx, result, "fixed", group: true);
            } else {
                result.Scenarios.Add(("bootstrap-user-group-fixed", "n/a", "AddGroup is not in this version"));
            }
            NamesInsidePhases(ctx, result);
            GroupInPhaseShared(ctx, result);
        }

        private sealed class PhaseMarker { }

        // A group added to fixedUpdate inside Configure: the phases are attached to the root after Configure,
        // so the group inherits FixedUpdateSystems' own SharedData, and FixedUpdateSystems gets the root's later.
        // XML Shared: "Shared data passed to systems in Init" — checked against what the group's system got.
        private static void GroupInPhaseShared(Ctx ctx, CustomResult result) {
            var j = new Journal();
            var b = new StandBootstrap();
            SystemsRunner group = null;
            InitRunDestroy inGroup = null;
            bool phaseSharedIsShared = false;
            b.OnConfigure = (update, fixedUpdate, lateUpdate, shared) => {
                update.Add(new InitRunDestroy("u1", j));
                group = new SystemsRunner(fixedUpdate.World);
                inGroup = new InitRunDestroy("g1", j);
                group.Add(inGroup);
                fixedUpdate.Add(group);
                phaseSharedIsShared = ReferenceEquals(fixedUpdate.Shared, shared);
                fixedUpdate.Shared.Add(new PhaseMarker());
            };
            var awake = UnityLoop.Send(b, "Awake");
            var start = awake == null ? UnityLoop.Send(b, "Start") : null;
            if (awake != null || start != null) {
                result.Scenarios.Add(("bootstrap-group-in-phase-shared", "stand-error", "Awake/Start threw " + Journal.Describe(awake ?? start)));
                return;
            }
            bool gotRoot = ReferenceEquals(inGroup.InitShared, b.Shared);
            bool propertyOk = ReferenceEquals(group.Shared, inGroup.InitShared);
            bool markerSeen = inGroup.InitShared != null && inGroup.InitShared.TryGet<PhaseMarker>(out _);
            UnityLoop.Send(b, "OnDestroy");
            result.Questions.Add(new ContractQuestion {
                Id = "Q-BOOTSTRAP-PHASE-SHARED-IN-CONFIGURE",
                Title = "Inside Configure, is fixedUpdate.Shared (lateUpdate.Shared) the same SharedData as the shared parameter?",
                Question = "bootstrap.md: the phases \"are constructed without their own SharedData and inherit the root's — every system gets the same shared\", but Awake attaches them " +
                           "to the root after Configure. What code that writes to fixedUpdate.Shared inside Configure sees is not stated.",
                Observed = "inside Configure ReferenceEquals(fixedUpdate.Shared, shared) = " + phaseSharedIsShared +
                           "; an object added to fixedUpdate.Shared in Configure is " + (markerSeen ? "seen" : "not seen (their SharedData is the root's)") + " by the phase's systems in Init",
                ObservedKey = "same=" + phaseSharedIsShared + ";seen=" + markerSeen,
                WhyNotFinding = "The documents put data into the shared parameter (the Configure examples); nothing is promised about fixedUpdate.Shared during Configure."
            });
            var problems = new List<string>();
            if (!gotRoot) {
                problems.Add("the group's system g1 got another SharedData than the bootstrap's Shared in Init");
            }
            if (!propertyOk) {
                problems.Add("group.Shared is not the SharedData its system g1 got in Init" + (gotRoot ? " (g1 got the bootstrap's Shared; group.Shared is FixedUpdateSystems' original one)" : ""));
            }
            if (problems.Count == 0) {
                result.Scenarios.Add(("bootstrap-group-in-phase-shared", "pass", "a group added to fixedUpdate in Configure: its system and group.Shared both use the bootstrap's Shared"));
                return;
            }
            result.Findings.Add(new CustomFinding {
                Rule = gotRoot ? Rules.SharedProperty : Rules.InitArgs,
                Scenario = "bootstrap-group-in-phase-shared",
                Place = "bootstrap:group-in-fixedUpdate",
                Message = "EcsBootstrap, a group added to fixedUpdate in Configure: " + string.Join("; ", problems),
                ReproBody = "var j = new Journal();\n" +
                            "        var b = new ReproGroupInPhaseBootstrap { J = j };\n" +
                            "        UnityLoop.Send(b, \"Awake\");\n" +
                            "        UnityLoop.Send(b, \"Start\");\n" +
                            "        return Journal.ExpectEqual(ReferenceEquals(b.G1.InitShared, b.Shared), true, \"g1 got the bootstrap's Shared in Init\")\n" +
                            "            ?? Journal.ExpectEqual(ReferenceEquals(b.Group.Shared, b.G1.InitShared), true, \"group.Shared is the SharedData passed to its systems in Init\");",
                ReproExtraTypes = "public sealed class ReproGroupInPhaseBootstrap : EcsBootstrap {\n" +
                                  "    public Journal J;\n" +
                                  "    public SystemsRunner Group;\n" +
                                  "    public InitRunDestroy G1;\n" +
                                  "    protected override void Configure(SystemsRunner update, SystemsRunner fixedUpdate, SystemsRunner lateUpdate, SharedData shared) {\n" +
                                  "        update.Add(new InitRunDestroy(\"u1\", J));\n" +
                                  "        G1 = new InitRunDestroy(\"g1\", J);\n" +
                                  "        Group = new SystemsRunner(fixedUpdate.World).Add(G1);\n" +
                                  "        fixedUpdate.Add(Group); // the phase is attached to the root only after Configure\n" +
                                  "    }\n" +
                                  "}\n"
            });
            result.Scenarios.Add(("bootstrap-group-in-phase-shared", "finding", (gotRoot ? Rules.SharedProperty : Rules.InitArgs) + ": " + string.Join("; ", problems)));
        }

        // Control for R4: "fixed"/"late" used inside the fixed and late runners themselves. "Different runners
        // have separate names" (runner.md), so nothing collides with the root's phases.
        private static void NamesInsidePhases(Ctx ctx, CustomResult result) {
            var j = new Journal();
            var b = new StandBootstrap();
            b.OnConfigure = (update, fixedUpdate, lateUpdate, shared) => {
                update.Add(new InitRunDestroy("u1", j));
                fixedUpdate.Add(new InitRunDestroy("f1", j), "fixed");
                lateUpdate.Add(new InitRunDestroy("l1", j), "late");
            };
            var problems = new List<string>();
            var awake = UnityLoop.Send(b, "Awake");
            if (awake != null) {
                problems.Add("Awake threw " + Journal.Describe(awake));
            } else {
                var start = UnityLoop.Send(b, "Start");
                if (start != null) {
                    problems.Add("Start threw " + Journal.Describe(start));
                }
                var f = Tick(b, j);
                if (f.Fixed != null || f.Update != null || f.Late != null) {
                    problems.Add("a frame threw " + Journal.Describe(f.Fixed ?? f.Update ?? f.Late));
                }
                if (f.Log != "run:f1 run:u1 run:l1") {
                    problems.Add("frame: [" + f.Log + "], expected [run:f1 run:u1 run:l1]");
                }
                var thrown = Journal.Catch(() => b.FixedUpdateSystems.SetActive("fixed", false));
                if (thrown != null) {
                    problems.Add("FixedUpdateSystems.SetActive(\"fixed\", false) threw " + Journal.Describe(thrown));
                } else {
                    f = Tick(b, j);
                    if (f.Log != "run:u1 run:l1") {
                        problems.Add("after FixedUpdateSystems.SetActive(\"fixed\", false): [" + f.Log + "], expected [run:u1 run:l1]");
                    }
                    if (!b.FixedUpdateSystems.IsEnabled) {
                        problems.Add("FixedUpdateSystems.SetActive(\"fixed\", false) paused the whole phase instead of its system \"fixed\"");
                    }
                }
                UnityLoop.Send(b, "OnDestroy");
            }
            Report(result, "bootstrap-names-inside-phases", Rules.BootstrapLifecycle, problems,
                "fixedUpdate.Add(f1, \"fixed\"), lateUpdate.Add(l1, \"late\"): no collision with the root's phases (names are per runner)",
                NamesInsidePhasesRepro(), NamesInsidePhasesReproType());
        }

        private static string NamesInsidePhasesReproType() {
            return "public sealed class ReproPhaseNamesBootstrap : EcsBootstrap {\n" +
                   "    public Journal J;\n" +
                   "    protected override void Configure(SystemsRunner update, SystemsRunner fixedUpdate, SystemsRunner lateUpdate, SharedData shared) {\n" +
                   "        update.Add(new InitRunDestroy(\"u1\", J));\n" +
                   "        fixedUpdate.Add(new InitRunDestroy(\"f1\", J), \"fixed\"); // names are per runner\n" +
                   "        lateUpdate.Add(new InitRunDestroy(\"l1\", J), \"late\");\n" +
                   "    }\n" +
                   "}\n";
        }

        private static string NamesInsidePhasesRepro() {
            return "var j = new Journal();\n" +
                   "        var b = new ReproPhaseNamesBootstrap { J = j };\n" +
                   "        var e = UnityLoop.Send(b, \"Awake\") ?? UnityLoop.Send(b, \"Start\");\n" +
                   "        if (e != null) return \"Awake/Start threw \" + Journal.Describe(e);\n" +
                   "        j.Begin();\n" +
                   "        UnityLoop.Send(b, \"FixedUpdate\"); UnityLoop.Send(b, \"Update\"); UnityLoop.Send(b, \"LateUpdate\");\n" +
                   "        return j.ExpectLog(\"run:f1 run:u1 run:l1\");";
        }

        private sealed class Frame {
            public Exception Fixed;
            public Exception Update;
            public Exception Late;
            public string Log;
        }

        private static Frame Tick(StandBootstrap b, Journal j) {
            j.Begin();
            var f = new Frame {
                Fixed = UnityLoop.Send(b, "FixedUpdate"),
                Update = UnityLoop.Send(b, "Update"),
                Late = UnityLoop.Send(b, "LateUpdate")
            };
            f.Log = string.Join(" ", j.Slice());
            return f;
        }

        private static void Lifecycle(Ctx ctx, CustomResult result, bool warmup) {
            string name = warmup ? "bootstrap-lifecycle" : "bootstrap-lifecycle-no-warmup";
            var j = new Journal();
            var b = new StandBootstrap();
            if (!warmup) {
                UnityLoop.SetField(b, "_warmupOnStart", false);
            }
            b.OnConfigure = (update, fixedUpdate, lateUpdate, shared) => {
                update.Add(new InitRunDestroy("u1", j), "movement");
                update.Add(new InitRunDestroy("u2", j));
                fixedUpdate.Add(new InitRunDestroy("f1", j));
                lateUpdate.Add(new InitRunDestroy("l1", j));
            };
            var problems = new List<string>();
            var awake = UnityLoop.Send(b, "Awake");
            if (awake != null) {
                problems.Add("Awake threw " + Journal.Describe(awake));
            } else {
                var s = b.Systems;
                if (b.World == null || b.Shared == null || s == null || b.FixedUpdateSystems == null || b.LateUpdateSystems == null) {
                    problems.Add("Awake left World/Shared/runners null");
                } else {
                    if (s.SystemCount != 4) {
                        problems.Add("Systems.SystemCount = " + s.SystemCount + ", expected 4 (two systems + phases \"fixed\", \"late\")");
                    } else {
                        var fixedInfo = s.GetSystemInfo(2);
                        var lateInfo = s.GetSystemInfo(3);
                        if (fixedInfo.Name != "fixed" || !fixedInfo.IsSeparatePhase || !ReferenceEquals(fixedInfo.ChildRunner, b.FixedUpdateSystems)) {
                            problems.Add("entry 2 is not the phase \"fixed\" = FixedUpdateSystems");
                        }
                        if (lateInfo.Name != "late" || !lateInfo.IsSeparatePhase || !ReferenceEquals(lateInfo.ChildRunner, b.LateUpdateSystems)) {
                            problems.Add("entry 3 is not the phase \"late\" = LateUpdateSystems");
                        }
                    }
                    if (!ReferenceEquals(s.GetRunner("fixed"), b.FixedUpdateSystems) || !ReferenceEquals(s.GetRunner("late"), b.LateUpdateSystems)) {
                        problems.Add("GetRunner(\"fixed\"/\"late\") does not return FixedUpdateSystems/LateUpdateSystems");
                    }
                    if (!ReferenceEquals(b.FixedUpdateSystems.Shared, b.Shared) || !ReferenceEquals(b.LateUpdateSystems.Shared, b.Shared)) {
                        problems.Add("the phases do not share the root's SharedData");
                    }
                }
            }
            if (problems.Count == 0) {
                j.Begin();
                var start = UnityLoop.Send(b, "Start");
                string initLog = string.Join(" ", j.Slice());
                if (start != null) {
                    problems.Add("Start threw " + Journal.Describe(start));
                }
                if (initLog != "init:u1 init:u2 init:f1 init:l1") {
                    problems.Add("Start: init log [" + initLog + "], expected [init:u1 init:u2 init:f1 init:l1]");
                }
                if (!b.Systems.IsInitialized || !b.FixedUpdateSystems.IsInitialized || !b.LateUpdateSystems.IsInitialized) {
                    problems.Add("after Start not every runner is initialized");
                }
                int tick0 = b.World.Tick;
                for (int frame = 1; frame <= 2; frame++) {
                    var f = Tick(b, j);
                    if (f.Fixed != null || f.Update != null || f.Late != null) {
                        problems.Add("frame " + frame + " threw " + Journal.Describe(f.Fixed ?? f.Update ?? f.Late));
                    }
                    if (f.Log != "run:f1 run:u1 run:u2 run:l1") {
                        problems.Add("frame " + frame + ": [" + f.Log + "], expected [run:f1 run:u1 run:u2 run:l1]");
                    }
                }
                if (b.World.Tick != tick0 + 2) {
                    problems.Add("tick advanced by " + (b.World.Tick - tick0) + " in 2 frames, expected 2 (only Update ticks)");
                }
                b.Systems.SetActive("fixed", false);
                var paused = Tick(b, j);
                if (paused.Log != "run:u1 run:u2 run:l1") {
                    problems.Add("after Systems.SetActive(\"fixed\", false): [" + paused.Log + "], expected [run:u1 run:u2 run:l1]");
                }
                b.Systems.SetActive("fixed", true);
                b.Systems.SetActive("late", false);
                paused = Tick(b, j);
                if (paused.Log != "run:f1 run:u1 run:u2") {
                    problems.Add("after Systems.SetActive(\"late\", false): [" + paused.Log + "], expected [run:f1 run:u1 run:u2]");
                }
                j.Begin();
                var destroy = UnityLoop.Send(b, "OnDestroy");
                string destroyLog = string.Join(" ", j.Slice());
                if (destroy != null) {
                    problems.Add("OnDestroy threw " + Journal.Describe(destroy));
                }
                if (destroyLog != "destroy:l1 destroy:f1 destroy:u2 destroy:u1") {
                    problems.Add("OnDestroy: [" + destroyLog + "], expected [destroy:l1 destroy:f1 destroy:u2 destroy:u1] (reverse registration: late, fixed, then update's systems)");
                }
                if (!b.World.IsDestroyed) {
                    problems.Add("OnDestroy did not destroy the world");
                }
            }
            Report(result, name, Rules.BootstrapLifecycle, problems,
                "Awake -> Start" + (warmup ? " (Warmup)" : " (Init, Warmup On Start off)") + " -> 2 frames -> SetActive(\"fixed\"/\"late\", false) -> OnDestroy",
                LifecycleRepro(warmup));
        }

        private static void StartFails(Ctx ctx, CustomResult result) {
            var j = new Journal();
            var b = new StandBootstrap();
            b.OnConfigure = (update, fixedUpdate, lateUpdate, shared) => {
                update.Add(new InitRunDestroy("u1", j));
                fixedUpdate.Add(new InitRunDestroy("f1", j) { FailInit = 1 });
                lateUpdate.Add(new InitRunDestroy("l1", j));
            };
            var problems = new List<string>();
            var awake = UnityLoop.Send(b, "Awake");
            if (awake != null) {
                problems.Add("Awake threw " + Journal.Describe(awake));
            }
            var start = UnityLoop.Send(b, "Start");
            if (start == null) {
                problems.Add("Start did not report the failing Init");
            }
            if (b.Systems != null && b.Systems.IsInitialized) {
                problems.Add("Systems.IsInitialized is true after a failed Start");
            }
            for (int frame = 1; frame <= 2 && b.Systems != null; frame++) {
                var f = Tick(b, j);
                if (f.Fixed != null || f.Update != null || f.Late != null) {
                    problems.Add("frame " + frame + " after the failed Start threw " + Journal.Describe(f.Fixed ?? f.Update ?? f.Late));
                }
                if (f.Log.Length > 0) {
                    problems.Add("frame " + frame + " after the failed Start ran systems: [" + f.Log + "]");
                }
            }
            j.Begin();
            var destroy = UnityLoop.Send(b, "OnDestroy");
            if (destroy != null) {
                problems.Add("OnDestroy threw " + Journal.Describe(destroy));
            }
            if (j.Slice().Count > 0) {
                problems.Add("OnDestroy after the failed Start destroyed again: [" + string.Join(" ", j.Slice()) + "]");
            }
            Report(result, "bootstrap-start-fails", Rules.BootstrapGuard, problems,
                "Init of a FixedUpdate system throws in Start -> frames -> OnDestroy", StartFailsRepro());
        }

        private static void UserName(Ctx ctx, CustomResult result, string phaseName, bool group) {
            string name = "bootstrap-user-" + (group ? "group" : "system") + "-" + phaseName;
            string place = "bootstrap:" + (group ? "group" : "system") + "-" + phaseName;
            string what = (group ? "group" : "system") + " \"" + phaseName + "\"";
            var j = new Journal();
            var b = new StandBootstrap();
            b.OnConfigure = (update, fixedUpdate, lateUpdate, shared) => {
                if (group) {
                    var g = new SystemsRunner(update.World);
                    g.Add(new RunOnly("u1", j));
                    Api.AddGroupFn(update, phaseName, g);
                } else {
                    update.Add(new RunOnly("u1", j), phaseName);
                }
                fixedUpdate.Add(new RunOnly("f1", j));
                lateUpdate.Add(new RunOnly("l1", j));
            };
            var awake = UnityLoop.Send(b, "Awake");
            string repro = UserNameRepro(phaseName, group);
            if (ctx.Profile.ReservedBootstrapNames && (awake != null || ctx.Debug)) {
                // 2.2.2: the clash is a clear error that names the reserved names (R4 resolved). In release a
                // version that accepts the name silently is still held to the name lookup below.
                string text = awake != null ? Journal.Messages(awake) : "";
                bool clear = awake != null && text.IndexOf("reserv", StringComparison.OrdinalIgnoreCase) >= 0 && text.Contains(phaseName);
                if (clear) {
                    result.Scenarios.Add((name, "pass", "Awake rejects the user " + what + " with a clear error: " + Journal.Describe(awake)));
                    return;
                }
                string problem = awake == null
                    ? "Awake accepted a user " + what + " in update without an error"
                    : "Awake threw " + Journal.Describe(awake) + ", which does not name the reserved names";
                result.Findings.Add(new CustomFinding {
                    Rule = Rules.BootstrapReservedName, Scenario = name, Place = place, Message = problem,
                    ReproBody = ReservedNameRepro(phaseName, group), ReproExtraTypes = UserNameReproType(phaseName, group)
                });
                result.Scenarios.Add((name, "finding", Rules.BootstrapReservedName + ": " + problem));
                return;
            }
            if (awake != null) {
                // Up to 2.2.1 not documented as reserved: the user's name is unique in the runner they registered it in.
                var start = UnityLoop.Send(b, "Start");
                var frame = Tick(b, j);
                string detail = "Awake threw " + Journal.Describe(awake) + "; after Start: Systems.IsInitialized=" + b.Systems.IsInitialized +
                                ", FixedUpdateSystems.IsInitialized=" + b.FixedUpdateSystems.IsInitialized + ", LateUpdateSystems.IsInitialized=" + b.LateUpdateSystems.IsInitialized +
                                "; a frame: FixedUpdate " + (frame.Fixed != null ? "threw " + Journal.Describe(frame.Fixed) : "ok") +
                                ", LateUpdate " + (frame.Late != null ? "threw " + Journal.Describe(frame.Late) : "ok") + " (every frame)" +
                                (start != null ? "; Start threw " + Journal.Describe(start) : "");
                result.Findings.Add(new CustomFinding { Rule = Rules.BootstrapNameCollision, Scenario = name, Place = place, Message = detail, ReproBody = repro, ReproExtraTypes = UserNameReproType(phaseName, group) });
                result.Scenarios.Add((name, "finding", Rules.BootstrapNameCollision + ": " + Journal.Describe(awake)));
                return;
            }
            // Awake went through (release, or a version that resolves the clash). Up to 2.2.1 the phase must be what the
            // name reaches, since the bootstrap registers it second ("the second one silently replaces the first"). 2.2.2
            // documents the split instead: "SetActive, IsActive and IndexOf with it reach your system or group, not the
            // phase, and only GetRunner returns the phase" (bootstrap.md, Reserved names).
            UnityLoop.Send(b, "Start");
            var runner = phaseName == "fixed" ? b.FixedUpdateSystems : b.LateUpdateSystems;
            string phaseText = phaseName == "fixed" ? "FixedUpdateSystems" : "LateUpdateSystems";
            var problems = new List<string>();
            var thrown = Journal.Catch(() => b.Systems.SetActive(phaseName, false));
            if (thrown != null) {
                problems.Add("Systems.SetActive(\"" + phaseName + "\", false) threw " + Journal.Describe(thrown));
            } else if (ctx.Profile.DuplicateNameSplit) {
                if (b.Systems.GetSystemInfo(0).IsEnabled) {
                    problems.Add("Systems.SetActive(\"" + phaseName + "\", false) did not switch off the user's " + (group ? "group" : "system") + ", which 2.2.2 says the name reaches");
                }
                if (!runner.IsEnabled) {
                    problems.Add("it paused the phase (" + phaseText + ".IsEnabled false), which 2.2.2 says only GetRunner reaches");
                }
            } else {
                if (runner.IsEnabled) {
                    problems.Add("Systems.SetActive(\"" + phaseName + "\", false) did not pause the phase (" + phaseText + ".IsEnabled still true)");
                }
                if (!b.Systems.GetSystemInfo(0).IsEnabled) {
                    problems.Add("it switched off the user's " + (group ? "group" : "system") + " \"" + phaseName + "\" instead");
                }
            }
            var got = b.Systems.GetRunner(phaseName);
            if (!ReferenceEquals(got, runner)) {
                problems.Add("GetRunner(\"" + phaseName + "\") is not the bootstrap's phase");
            }
            UnityLoop.Send(b, "OnDestroy");
            if (problems.Count == 0) {
                result.Scenarios.Add((name, "pass", "Awake ok; the name reaches the bootstrap's phase"));
                return;
            }
            result.Findings.Add(new CustomFinding {
                Rule = Rules.DuplicateNameRelease,
                Scenario = name,
                Place = place,
                Message = "EcsBootstrap, user " + (group ? "group" : "system") + " \"" + phaseName + "\" in update: " + string.Join("; ", problems),
                ReproBody = UserNameReleaseRepro(phaseName, group, ctx.Profile.DuplicateNameSplit),
                ReproExtraTypes = UserNameReproType(phaseName, group)
            });
            result.Scenarios.Add((name, "finding", Rules.DuplicateNameRelease + ": " + string.Join("; ", problems)));
        }

        private static void Report(CustomResult result, string name, string rule, List<string> problems, string what, string repro, string reproTypes = null) {
            if (problems.Count == 0) {
                result.Scenarios.Add((name, "pass", what));
                return;
            }
            result.Findings.Add(new CustomFinding {
                Rule = rule, Scenario = name, Message = string.Join("; ", problems), ReproBody = repro, ReproExtraTypes = reproTypes ?? LifecycleReproType()
            });
            result.Scenarios.Add((name, "finding", rule + ": " + string.Join("; ", problems)));
        }

        // ------------------------------------------------------------ repro templates

        private static string LifecycleReproType() {
            return "public sealed class ReproLifecycleBootstrap : EcsBootstrap {\n" +
                   "    public Journal J;\n" +
                   "    public bool FailFixedInit;\n" +
                   "    protected override void Configure(SystemsRunner update, SystemsRunner fixedUpdate, SystemsRunner lateUpdate, SharedData shared) {\n" +
                   "        update.Add(new InitRunDestroy(\"u1\", J), \"movement\");\n" +
                   "        update.Add(new InitRunDestroy(\"u2\", J));\n" +
                   "        fixedUpdate.Add(new InitRunDestroy(\"f1\", J) { FailInit = FailFixedInit ? 1 : 0 });\n" +
                   "        lateUpdate.Add(new InitRunDestroy(\"l1\", J));\n" +
                   "    }\n" +
                   "}\n";
        }

        private static string LifecycleRepro(bool warmup) {
            return "var j = new Journal();\n" +
                   "        var b = new ReproLifecycleBootstrap { J = j };\n" +
                   (warmup ? "" : "        UnityLoop.SetField(b, \"_warmupOnStart\", false);\n") +
                   "        var e = UnityLoop.Send(b, \"Awake\") ?? UnityLoop.Send(b, \"Start\");\n" +
                   "        if (e != null) return \"Awake/Start threw \" + Journal.Describe(e);\n" +
                   "        j.Begin();\n" +
                   "        UnityLoop.Send(b, \"FixedUpdate\"); UnityLoop.Send(b, \"Update\"); UnityLoop.Send(b, \"LateUpdate\");\n" +
                   "        var frame = j.ExpectLog(\"run:f1 run:u1 run:u2 run:l1\");\n" +
                   "        if (frame != null) return frame;\n" +
                   "        j.Begin();\n" +
                   "        UnityLoop.Send(b, \"OnDestroy\");\n" +
                   "        return j.ExpectLog(\"destroy:l1 destroy:f1 destroy:u2 destroy:u1\");";
        }

        private static string StartFailsRepro() {
            return "var j = new Journal();\n" +
                   "        var b = new ReproLifecycleBootstrap { J = j, FailFixedInit = true };\n" +
                   "        UnityLoop.Send(b, \"Awake\");\n" +
                   "        UnityLoop.Send(b, \"Start\"); // Init of f1 throws\n" +
                   "        j.Begin();\n" +
                   "        var e = UnityLoop.Send(b, \"FixedUpdate\") ?? UnityLoop.Send(b, \"Update\") ?? UnityLoop.Send(b, \"LateUpdate\");\n" +
                   "        if (e != null) return \"a frame after the failed Start threw \" + Journal.Describe(e);\n" +
                   "        return j.ExpectLog(\"\");";
        }

        private static string UserNameReproType(string phaseName, bool group) {
            string cls = "Repro" + (group ? "Group" : "System") + char.ToUpperInvariant(phaseName[0]) + phaseName.Substring(1) + "Bootstrap";
            return "public sealed class " + cls + " : EcsBootstrap {\n" +
                   "    public Journal J;\n" +
                   "    protected override void Configure(SystemsRunner update, SystemsRunner fixedUpdate, SystemsRunner lateUpdate, SharedData shared) {\n" +
                   (group
                       ? "        update.AddGroup(\"" + phaseName + "\", new SystemsRunner(update.World).Add(new RunOnly(\"u1\", J))); // a user group that happens to be called \"" + phaseName + "\"\n"
                       : "        update.Add(new RunOnly(\"u1\", J), \"" + phaseName + "\"); // a user system that happens to be called \"" + phaseName + "\"\n") +
                   "        fixedUpdate.Add(new RunOnly(\"f1\", J));\n" +
                   "        lateUpdate.Add(new RunOnly(\"l1\", J));\n" +
                   "    }\n" +
                   "}\n";
        }

        private static string UserNameRepro(string phaseName, bool group) {
            string cls = "Repro" + (group ? "Group" : "System") + char.ToUpperInvariant(phaseName[0]) + phaseName.Substring(1) + "Bootstrap";
            return "var b = new " + cls + " { J = new Journal() };\n" +
                   "        var awake = UnityLoop.Send(b, \"Awake\"); // Unity logs it and goes on\n" +
                   "        if (awake != null) {\n" +
                   "            UnityLoop.Send(b, \"Start\");\n" +
                   "            var fixedUpdate = UnityLoop.Send(b, \"FixedUpdate\");\n" +
                   "            return \"Awake threw \" + Journal.Describe(awake) + \"; FixedUpdateSystems.IsInitialized=\" + b.FixedUpdateSystems.IsInitialized +\n" +
                   "                   \"; FixedUpdate: \" + Journal.Describe(fixedUpdate);\n" +
                   "        }\n" +
                   "        return null;";
        }

        private static string ReservedNameRepro(string phaseName, bool group) {
            string cls = "Repro" + (group ? "Group" : "System") + char.ToUpperInvariant(phaseName[0]) + phaseName.Substring(1) + "Bootstrap";
            return "var b = new " + cls + " { J = new Journal() };\n" +
                   "        var awake = UnityLoop.Send(b, \"Awake\");\n" +
                   "        // 2.2.2: a user name \"fixed\"/\"late\" in update is a clear error that names the reserved names\n" +
                   "        if (awake == null) return \"Awake accepted the user " + (group ? "group" : "system") + " \\\"" + phaseName + "\\\" without an error\";\n" +
                   "        string text = Journal.Messages(awake);\n" +
                   "        return text.IndexOf(\"reserv\", StringComparison.OrdinalIgnoreCase) >= 0 && text.Contains(\"" + phaseName + "\") ? null\n" +
                   "            : \"Awake threw \" + Journal.Describe(awake) + \", which does not name the reserved names\";";
        }

        private static string UserNameReleaseRepro(string phaseName, bool group, bool split) {
            string cls = "Repro" + (group ? "Group" : "System") + char.ToUpperInvariant(phaseName[0]) + phaseName.Substring(1) + "Bootstrap";
            string phase = phaseName == "fixed" ? "FixedUpdateSystems" : "LateUpdateSystems";
            if (split) {
                // 2.2.2, bootstrap.md: "SetActive, IsActive and IndexOf with it reach your system or group, not the phase".
                return "var b = new " + cls + " { J = new Journal() };\n" +
                       "        UnityLoop.Send(b, \"Awake\");\n" +
                       "        UnityLoop.Send(b, \"Start\");\n" +
                       "        b.Systems.SetActive(\"" + phaseName + "\", false); // 2.2.2: reaches the user's " + (group ? "group" : "system") + ", not the phase\n" +
                       "        return Journal.ExpectEqual(b.Systems.GetSystemInfo(0).IsEnabled, false, \"the user's " + (group ? "group" : "system") + " \\\"" + phaseName + "\\\" after Systems.SetActive(\\\"" + phaseName + "\\\", false)\")\n" +
                       "            ?? Journal.ExpectEqual(b." + phase + ".IsEnabled, true, \"" + phase + ".IsEnabled stays true\");";
            }
            return "var b = new " + cls + " { J = new Journal() };\n" +
                   "        UnityLoop.Send(b, \"Awake\");\n" +
                   "        UnityLoop.Send(b, \"Start\");\n" +
                   "        b.Systems.SetActive(\"" + phaseName + "\", false); // meant: pause the " + phaseName + " phase\n" +
                   "        return Journal.ExpectEqual(b." + phase + ".IsEnabled, false, \"" + phase + ".IsEnabled after Systems.SetActive(\\\"" + phaseName + "\\\", false)\")\n" +
                   "            ?? Journal.ExpectEqual(b.Systems.GetSystemInfo(0).IsEnabled, true, \"the user's " + (group ? "group" : "system") + " \\\"" + phaseName + "\\\" stays enabled\");";
        }
    }
}
