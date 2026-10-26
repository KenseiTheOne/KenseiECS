// Scenarios outside the model's unique-name world: duplicate names, Add after Init, and
// observations of behaviour the documents leave open (recorded as contract questions).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using KenseiECS;
using Stand.Runner.Kit;

namespace Stand.Runner {
    /// <summary> A finding produced by hand-written scenario code, with its hand-written repro. </summary>
    public sealed class CustomFinding {
        public string Rule;
        public string Scenario;
        /// <summary> Where in the scenario (a pair of registrations, a phase name); the signature is rule + scenario + place. </summary>
        public string Place;
        public string Message;
        public string ReproBody;   // C# statements of Run(); "return ...;" at the end
        public string ReproExtraTypes;
    }

    public sealed class CustomResult {
        public readonly List<CustomFinding> Findings = new List<CustomFinding>();
        public readonly List<ContractQuestion> Questions = new List<ContractQuestion>();
        public readonly List<(string Name, string Status, string Detail)> Scenarios = new List<(string, string, string)>();
    }

    public static class CustomScenarios {
        private enum Kind { Run, InitOnly, Group, Phase }

        public static void RunAll(Ctx ctx, CustomResult result) {
            DuplicateNames(ctx, result);
            AddAfterInit(ctx, result);
            ForeignChildDebug(ctx, result);
            RunBeforeInitDebug(ctx, result);
            NestedArgs(ctx, result);
            DelHereTwice(ctx, result);
            ObserveDestroyThrows(ctx, result);
            ObserveRollbackErrorOrder(ctx, result);
            ObserveDisabledRootGroups(ctx, result);
            ObserveDelHereName(ctx, result);
            ObserveSharedIndexOf(ctx, result);
        }

        private static void ObserveSharedIndexOf(Ctx ctx, CustomResult result) {
            if (ctx.Contract != Contract.V22 || !Api.HasIndexOf || !Api.HasAddPhase || ctx.Profile.RejectSharedRunner) {
                // 2.2.2 rejects the second slot, so the question does not arise.
                return;
            }
            var world = new World();
            var g = new SystemsRunner(world);
            var root = new SystemsRunner(world);
            root.Add(g);
            var rejected = Journal.Catch(() => Api.AddPhaseFn(root, "p", g));
            if (rejected != null) {
                return;
            }
            int index = Api.IndexOfNameFn(root, "p");
            result.Questions.Add(new ContractQuestion {
                Id = "Q-SHARED-INDEXOF",
                Title = "Which position does IndexOf(name) return for a runner registered in two slots?",
                Question = "root.Add(G) (slot 0) and root.AddPhase(\"p\", G) (slot 1): IndexOf(name) is the \"Position of the system with the given name\", " +
                           "but the name is resolved \"exactly as SetActive(string)\", and SetActive(\"p\") addresses the runner G itself, not a slot.",
                Observed = "IndexOf(\"p\") = " + index + (index == 0 ? ": the first slot of the instance G (the unnamed group), not the slot registered as \"p\"" : ""),
                ObservedKey = "index=" + index,
                WhyNotFinding = "For a runner in two slots the documents allow either answer; the stand accepts any slot of that runner. The slots disagreeing with each other is finding R3."
            });
        }

        private static void ObserveDelHereName(Ctx ctx, CustomResult result) {
            if (ctx.Contract != Contract.V22 || !Api.HasIndexOf) {
                return;
            }
            var world = new World();
            var root = new SystemsRunner(world);
            root.DelHere<Ev1>();
            root.DelHere<Ev1>();
            int index = Api.IndexOfNameFn(root, "DelHere<Ev1>");
            var thrown = Journal.Catch(() => root.SetActive("DelHere<Ev1>", false));
            string states = root.GetSystemInfo(0).IsEnabled + "/" + root.GetSystemInfo(1).IsEnabled;
            result.Questions.Add(new ContractQuestion {
                Id = "Q-DELHERE-NAME",
                Title = "Is the name DelHere<T> a registration name for IndexOf(name) and SetActive(name)?",
                Question = "The runner generates the name \"DelHere<T>\" (repeats are allowed: \"DelHere entries are exempt\"). IndexOf(name) promises the fallback \"to the first unnamed " +
                           "system whose type name (SystemInfo.Name) matches\" only when \"no system was registered under that name\" — and DelHere registers under that name internally.",
                Observed = "two DelHere<Ev1>: IndexOf(\"DelHere<Ev1>\") = " + index + "; SetActive(\"DelHere<Ev1>\", false) -> " + (thrown != null ? Journal.Describe(thrown) : "slots " + states),
                ObservedKey = "index=" + index + ";setactive=" + (thrown != null ? thrown.GetType().Name : states),
                WhyNotFinding = "The documents do not say whether a DelHere is addressed by name; IndexOf and SetActive agree with each other."
            });
        }

        // ------------------------------------------------------------ duplicate names

        private static void DuplicateNames(Ctx ctx, CustomResult result) {
            var kinds = new List<Kind> { Kind.Run, Kind.InitOnly, Kind.Phase };
            if (ctx.Contract == Contract.V22 && Api.HasAddGroup) {
                kinds.Add(Kind.Group);
            }
            if (ctx.Debug && ctx.Contract == Contract.V22) {
                var missing = new List<string>();
                var missingPairs = new List<(Kind, Kind)>();
                foreach (var first in kinds) {
                    foreach (var second in kinds) {
                        var world = new World();
                        var j = new Journal();
                        var root = new SystemsRunner(world);
                        Register(root, first, "x", j, world, 1);
                        var thrown = Journal.Catch(() => Register(root, second, "x", j, world, 2));
                        if (!(thrown is InvalidOperationException)) {
                            missing.Add(first + "->" + second + ": " + Journal.Describe(thrown));
                            missingPairs.Add((first, second));
                        }
                    }
                }
                if (missing.Count > 0) {
                    foreach (var pair in missingPairs) {
                        result.Findings.Add(new CustomFinding {
                            Rule = Rules.DuplicateNameDebug,
                            Scenario = "duplicate-name-debug",
                            Place = "registration:" + pair.Item1 + "->" + pair.Item2,
                            Message = "the second registration under \"x\" did not throw InvalidOperationException: " + pair.Item1 + "->" + pair.Item2,
                            ReproBody = DuplicateDebugRepro(pair.Item1, pair.Item2)
                        });
                    }
                    result.Scenarios.Add(("duplicate-name-debug", "finding", Rules.DuplicateNameDebug + ": " + string.Join("; ", missing)));
                } else {
                    result.Scenarios.Add(("duplicate-name-debug", "pass", kinds.Count * kinds.Count + " pairs: the second registration throws"));
                }
                return;
            }

            // Release (and 2.1 in any build, which has no duplicate check): "the second one silently
            // replaces the first in the name lookup (the first keeps running, but only SetActive(int, bool)
            // can reach it)" (2.2.0 runner.md; the same sentence in 2.1.1).
            // Only a second registration that SetActive/IsActive can address (a run system, a group, a
            // phase) makes a finding. A second Init/Destroy-only system is not toggled by name at all
            // (2.2: "for an Init/Destroy-only system SetActive ... does nothing in release"), so what
            // "replaces the first in the name lookup" means for it is not stated: a contract question.
            // 2.2.2 documents the lookup per kind instead ("SetActive, IsActive and IndexOf(name) reach the last run system or
            // inline group registered under the name, and a phase only when no system or group has it; GetRunner reaches
            // the last group or phase"): every pair has a documented answer then.
            bool split = ctx.Profile.DuplicateNameSplit;
            var broken = new List<string>();
            var unclear = new List<string>();
            int pairs = 0;
            int unclearPairs = 0;
            foreach (var first in kinds) {
                foreach (var second in kinds) {
                    if (split) {
                        var (nameTarget, _) = Targets(ctx, first, second);
                        if (nameTarget < 0) {
                            // Two Init/Destroy-only systems: no run system, group or phase holds the name.
                            continue;
                        }
                    } else if (second == Kind.InitOnly) {
                        if (ctx.Debug) {
                            // 2.1 under KENSEI_DEBUG treats a name without a run system or runner as unknown
                            // and throws; whether that applies here is not documented — skip the pair.
                            continue;
                        }
                        unclearPairs++;
                        string observed = CheckReleasePair(ctx, first, second);
                        if (observed != null) {
                            unclear.Add(first + "->" + second + ": " + observed);
                        }
                        continue;
                    }
                    pairs++;
                    string problem = CheckReleasePair(ctx, first, second);
                    if (problem != null) {
                        // One signature per pair: a pair that starts failing is a new signature even while others already fail.
                        broken.Add(first + "->" + second + ": " + problem);
                        result.Findings.Add(new CustomFinding {
                            Rule = Rules.DuplicateNameRelease,
                            Scenario = "duplicate-name-release",
                            Place = "lookup:" + first + "->" + second,
                            Message = "first->second registration under \"x\" = " + first + "->" + second + ": the name lookup does not reach " +
                                      (split ? "the registration 2.2.2 documents for it" : "the second one") + ": " + problem,
                            ReproBody = DuplicateReleaseRepro(ctx, first, second)
                        });
                    }
                }
            }
            if (unclear.Count > 0) {
                result.Questions.Add(new ContractQuestion {
                    Id = "Q-DUPLICATE-NAME-INITLESS-SECOND",
                    Title = "Release: does an Init/Destroy-only system registered second under a used name replace the first one in the name lookup?",
                    Question = "runner.md: \"in release the second one silently replaces the first in the name lookup (the first keeps running, but only SetActive(int, bool) can reach it)\". " +
                               "But the lookup of SetActive/IsActive does not address a system without Run (2.2: SetActive on it does nothing in release), and what \"replaces\" means then is not stated.",
                    Observed = "in " + unclear.Count + " of " + unclearPairs + " pairs the name still leads to the first registration: " + string.Join("; ", unclear),
                    ObservedKey = string.Join(";", unclear.Select(u => u.Substring(0, u.IndexOf(':'))).OrderBy(x => x, StringComparer.Ordinal)),
                    WhyNotFinding = "For a second registration without Run the documents allow both readings; only pairs whose second registration is a run system, a group or a phase make a finding."
                });
            }
            if (broken.Count > 0) {
                result.Scenarios.Add(("duplicate-name-release", "finding", broken.Count + " of " + pairs + " pairs: " + string.Join("; ", broken)));
            } else {
                result.Scenarios.Add(("duplicate-name-release", "pass", pairs + (split ? " pairs reach the registration of each kind 2.2.2 documents" : " pairs follow the second registration")));
            }
        }

        /// <summary>
        /// Which of the two registrations under one name (index 0 or 1, -1 for none) the release lookup reaches: SetActive,
        /// IsActive and IndexOf(name), and GetRunner(name). Up to 2.2.1 the second one ("silently replaces the first"; GetRunner
        /// checked only for a second group or phase). 2.2.2: the last run system or group, a phase only when none has the
        /// name; GetRunner the last group or phase.
        /// </summary>
        private static (int Name, int Runner) Targets(Ctx ctx, Kind first, Kind second) {
            if (!ctx.Profile.DuplicateNameSplit) {
                return (1, second == Kind.Group || second == Kind.Phase ? 1 : -1);
            }
            var regs = new[] { first, second };
            int Last(Func<Kind, bool> what) {
                for (int i = regs.Length - 1; i >= 0; i--) {
                    if (what(regs[i])) {
                        return i;
                    }
                }
                return -1;
            }
            int name = Last(k => k == Kind.Run || k == Kind.Group);
            if (name < 0) {
                name = Last(k => k == Kind.Phase);
            }
            return (name, Last(k => k == Kind.Group || k == Kind.Phase));
        }

        private static ISystem Register(SystemsRunner root, Kind kind, string name, Journal j, World world, int id) {
            switch (kind) {
                case Kind.Run: {
                    var s = new RunOnly("s" + id, j);
                    root.Add(s, name);
                    return s;
                }
                case Kind.InitOnly: {
                    var s = new InitOnly("s" + id, j);
                    root.Add(s, name);
                    return s;
                }
                case Kind.Group: {
                    var g = new SystemsRunner(world);
                    g.Add(new RunOnly("s" + id + "in", j));
                    Api.AddGroupFn(root, name, g);
                    return g;
                }
                default: {
                    var p = new SystemsRunner(world);
                    p.Add(new RunOnly("s" + id + "in", j));
                    Api.AddPhase(root, name, p, Api.HasAddPhase ? 0 : 1);
                    return p;
                }
            }
        }

        private static bool StateOf(SystemsRunner root, int index) {
            var info = root.GetSystemInfo(index);
            return info.ChildRunner != null && info.IsSeparatePhase ? info.ChildRunner.IsEnabled : info.IsEnabled;
        }

        private static string CheckReleasePair(Ctx ctx, Kind first, Kind second) {
            var world = new World();
            var j = new Journal();
            var root = new SystemsRunner(world);
            var regs = new[] { first, second };
            var systems = new[] { Register(root, first, "x", j, world, 1), Register(root, second, "x", j, world, 2) };
            var (target, runnerTarget) = Targets(ctx, first, second);
            string Nth(int i) => (i == 0 ? "first" : "second") + " registration (" + regs[i] + ")";
            var problems = new List<string>();
            var before = new[] { StateOf(root, 0), StateOf(root, 1) };
            var thrown = Journal.Catch(() => root.SetActive("x", false));
            if (thrown != null) {
                return "SetActive(\"x\", false) threw " + Journal.Describe(thrown);
            }
            for (int i = 0; i < 2; i++) {
                if (regs[i] == Kind.InitOnly) {
                    continue;
                }
                bool now = StateOf(root, i);
                if (i == target && now) {
                    problems.Add("the " + Nth(i) + ", which the name reaches, is still enabled");
                } else if (i != target && now != before[i]) {
                    problems.Add("SetActive(\"x\", false) switched off the " + Nth(i) + ", which the name does not reach");
                }
            }
            bool active = true;
            thrown = Journal.Catch(() => active = root.IsActive("x"));
            bool expectedActive = target >= 0 && regs[target] != Kind.InitOnly && StateOf(root, target);
            if (thrown != null) {
                problems.Add("IsActive(\"x\") threw " + Journal.Describe(thrown));
            } else if (active != expectedActive) {
                problems.Add("IsActive(\"x\") = " + active + ", the " + (target >= 0 ? Nth(target) : "registration") + " it reads is " +
                             (target >= 0 && regs[target] != Kind.InitOnly ? (StateOf(root, target) ? "enabled" : "disabled") : "not runnable (release: false)"));
            }
            if (ctx.Contract == Contract.V22 && Api.HasIndexOf && target >= 0) {
                int index = Api.IndexOfNameFn(root, "x");
                if (index != target) {
                    problems.Add("IndexOf(\"x\") = " + index + ", expected " + target + " (the " + Nth(target) + ")");
                }
            }
            if (runnerTarget >= 0) {
                var got = root.GetRunner("x");
                if (!ReferenceEquals(got, systems[runnerTarget])) {
                    problems.Add("GetRunner(\"x\") is not the " + Nth(runnerTarget));
                }
            }
            return problems.Count == 0 ? null : string.Join(", ", problems);
        }

        private static string RegisterCode(Kind kind, int id) {
            switch (kind) {
                case Kind.Run:
                    return "var s" + id + " = new RunOnly(\"s" + id + "\", j);\n        r0.Add(s" + id + ", \"x\");";
                case Kind.InitOnly:
                    return "var s" + id + " = new InitOnly(\"s" + id + "\", j);\n        r0.Add(s" + id + ", \"x\");";
                case Kind.Group:
                    return "var r" + id + " = new SystemsRunner(world);\n        r" + id + ".Add(new RunOnly(\"in" + id + "\", j));\n        r0.AddGroup(\"x\", r" + id + ");";
                default:
                    return "var r" + id + " = new SystemsRunner(world);\n        r" + id + ".Add(new RunOnly(\"in" + id + "\", j));\n        " +
                           (Api.HasAddPhase ? "r0.AddPhase(\"x\", r" + id + ");" : "r0.Add((ISystem)r" + id + ", \"x\");");
            }
        }

        private static string StateCode(Kind kind, int id) {
            return kind == Kind.Phase ? "r" + id + ".IsEnabled" : "r0.GetSystemInfo(" + (id - 1) + ").IsEnabled";
        }

        private static string DuplicateReleaseRepro(Ctx ctx, Kind first, Kind second) {
            var (target, _) = Targets(ctx, first, second);
            var sb = new StringBuilder();
            sb.AppendLine("var j = new Journal();");
            sb.AppendLine("        var world = new World();");
            sb.AppendLine("        var r0 = new SystemsRunner(world);");
            sb.AppendLine("        " + RegisterCode(first, 1));
            sb.AppendLine("        " + RegisterCode(second, 2) + " // the same name again: release has no check");
            sb.AppendLine("        r0.SetActive(\"x\", false);");
            if (ctx.Profile.DuplicateNameSplit) {
                sb.AppendLine("        // contract (runner.md 2.2.2): \"SetActive, IsActive and IndexOf(name) reach the last run system or inline group registered");
                sb.AppendLine("        // under the name, and a phase only when no system or group has it\"");
            } else {
                sb.AppendLine("        // contract: \"in release the second one silently replaces the first in the name lookup");
                sb.AppendLine("        // (the first keeps running, but only SetActive(int, bool) can reach it)\"");
            }
            var regs = new[] { first, second };
            var checks = new List<string>();
            for (int i = 0; i < 2; i++) {
                if (regs[i] == Kind.InitOnly) {
                    continue;
                }
                string which = i == 0 ? "first" : "second";
                checks.Add(i == target
                    ? "Journal.ExpectEqual(" + StateCode(regs[i], i + 1) + ", false, \"" + which + " registration switched off by SetActive(name)\")"
                    : "Journal.ExpectEqual(" + StateCode(regs[i], i + 1) + ", true, \"" + which + " registration untouched by SetActive(name)\")");
            }
            if (target >= 0 && regs[target] != Kind.InitOnly) {
                checks.Add("Journal.ExpectEqual(r0.IsActive(\"x\"), false, \"IsActive(\\\"x\\\") reads the registration the name reaches\")");
            }
            if (Api.HasIndexOf && target >= 0) {
                checks.Add("Journal.ExpectEqual(r0.IndexOf(\"x\"), " + target + ", \"IndexOf(\\\"x\\\")\")");
            }
            sb.Append("        return " + string.Join("\n            ?? ", checks) + ";");
            return sb.ToString();
        }

        private static string DuplicateDebugRepro(Kind first, Kind second) {
            return "var j = new Journal();\n        var world = new World();\n        var r0 = new SystemsRunner(world);\n        " + RegisterCode(first, 1) +
                   "\n        return Journal.ExpectThrows<InvalidOperationException>(() => { " + RegisterCode(second, 2).Replace("\n        ", " ") + " }, \"second registration under \\\"x\\\"\");";
        }

        // ------------------------------------------------------------ Add after Init (KENSEI_DEBUG)

        private static void AddAfterInit(Ctx ctx, CustomResult result) {
            if (!ctx.Debug) {
                result.Scenarios.Add(("add-after-init-debug", "n/a", "KENSEI_DEBUG only"));
                return;
            }
            var world = new World();
            var j = new Journal();
            var root = new SystemsRunner(world);
            root.Add(new RunOnly("s1", j));
            root.Init();
            var thrown = Journal.Catch(() => root.Add(new RunOnly("s2", j)));
            if (thrown is InvalidOperationException) {
                result.Scenarios.Add(("add-after-init-debug", "pass", "Add after Init throws InvalidOperationException"));
                return;
            }
            result.Findings.Add(new CustomFinding {
                Rule = Rules.AddAfterInitDebug,
                Scenario = "add-after-init-debug",
                Message = "Add after Init under KENSEI_DEBUG: " + Journal.Describe(thrown),
                ReproBody = "var j = new Journal();\n        var world = new World();\n        var r0 = new SystemsRunner(world);\n        r0.Add(new RunOnly(\"s1\", j));\n        r0.Init();\n" +
                            "        return Journal.ExpectThrows<InvalidOperationException>(() => r0.Add(new RunOnly(\"s2\", j)), \"Add after Init\");"
            });
            result.Scenarios.Add(("add-after-init-debug", "finding", Rules.AddAfterInitDebug));
        }

        // ------------------------------------------------------------ KENSEI_DEBUG checks of nesting and Run

        // runner.md (2.1.1 and 2.2.0): "Under KENSEI_DEBUG, adding, initializing or running a child constructed
        // with a different World (or an explicitly passed different SharedData) throws instead of silently
        // ignoring it". Any of the three steps may be the one that throws.
        private static void ForeignChildDebug(Ctx ctx, CustomResult result) {
            if (!ctx.Debug) {
                result.Scenarios.Add(("debug-foreign-child", "n/a", "KENSEI_DEBUG only"));
                return;
            }
            var missing = new List<string>();
            string repro = null;
            int combos = 0;
            foreach (bool phase in new[] { false, true }) {
                foreach (bool foreignShared in new[] { false, true }) {
                    combos++;
                    var world = new World();
                    var j = new Journal();
                    var root = new SystemsRunner(world);
                    var child = foreignShared ? new SystemsRunner(world, new SharedData()) : new SystemsRunner(new World());
                    child.Add(new RunOnly("c1", j));
                    var thrown = Journal.Catch(() => {
                        if (phase) {
                            Api.AddPhase(root, "p", child, Api.HasAddPhase ? 0 : 1);
                        } else {
                            root.Add(child);
                        }
                        root.Init();
                        root.Run();
                        child.Run();
                    });
                    if (!(thrown is InvalidOperationException)) {
                        missing.Add((phase ? "phase" : "unnamed group") + " constructed with " + (foreignShared ? "its own SharedData" : "another World") + ": " + Journal.Describe(thrown));
                        repro ??= ForeignChildRepro(phase, foreignShared);
                    }
                }
            }
            if (missing.Count == 0) {
                result.Scenarios.Add(("debug-foreign-child", "pass", combos + " combinations (group/phase × World/SharedData): Add, Init or Run throws InvalidOperationException"));
                return;
            }
            result.Findings.Add(new CustomFinding {
                Rule = Rules.DebugCheckPrefix + "FOREIGN-CHILD",
                Scenario = "debug-foreign-child",
                Message = "a child with a foreign World/SharedData was added, initialized and run without InvalidOperationException: " + string.Join("; ", missing),
                ReproBody = repro
            });
            result.Scenarios.Add(("debug-foreign-child", "finding", string.Join("; ", missing)));
        }

        private static string ForeignChildRepro(bool phase, bool foreignShared) {
            string add = phase ? (Api.HasAddPhase ? "r0.AddPhase(\"p\", r1);" : "r0.Add((ISystem)r1, \"p\");") : "r0.Add(r1);";
            return "var j = new Journal();\n" +
                   "        var world = new World();\n" +
                   "        var r0 = new SystemsRunner(world);\n" +
                   "        var r1 = " + (foreignShared ? "new SystemsRunner(world, new SharedData()); // its own SharedData" : "new SystemsRunner(new World()); // another World") + "\n" +
                   "        r1.Add(new RunOnly(\"c1\", j));\n" +
                   "        // contract: adding, initializing or running such a child throws under KENSEI_DEBUG\n" +
                   "        var thrown = Journal.Catch(() => { " + add + " r0.Init(); r0.Run(); r1.Run(); });\n" +
                   "        return thrown is InvalidOperationException ? null : \"no InvalidOperationException: \" + Journal.Describe(thrown);";
        }

        // runner.md (2.1.1 and 2.2.0): "Run before Init throws under KENSEI_DEBUG" — the root's Run() and a phase's.
        private static void RunBeforeInitDebug(Ctx ctx, CustomResult result) {
            if (!ctx.Debug) {
                result.Scenarios.Add(("debug-run-before-init", "n/a", "KENSEI_DEBUG only"));
                return;
            }
            var world = new World();
            var j = new Journal();
            var phase = new SystemsRunner(world);
            phase.Add(new RunOnly("p1", j));
            var root = new SystemsRunner(world);
            root.Add(new RunOnly("s1", j));
            Api.AddPhase(root, "p", phase, Api.HasAddPhase ? 0 : 1);
            var rootThrown = Journal.Catch(() => root.Run());
            var phaseThrown = Journal.Catch(() => phase.Run());
            var problems = new List<string>();
            if (!(rootThrown is InvalidOperationException)) {
                problems.Add("root.Run() before Init: " + Journal.Describe(rootThrown));
            }
            if (!(phaseThrown is InvalidOperationException)) {
                problems.Add("phase.Run() before Init: " + Journal.Describe(phaseThrown));
            }
            if (problems.Count == 0) {
                result.Scenarios.Add(("debug-run-before-init", "pass", "root.Run() and a phase's Run() before Init throw InvalidOperationException"));
                return;
            }
            string add = Api.HasAddPhase ? "r0.AddPhase(\"p\", r1);" : "r0.Add((ISystem)r1, \"p\");";
            result.Findings.Add(new CustomFinding {
                Rule = Rules.DebugCheckPrefix + "RUN-BEFORE-INIT",
                Scenario = "debug-run-before-init",
                Message = string.Join("; ", problems) + " (expected InvalidOperationException)",
                ReproBody = "var j = new Journal();\n        var world = new World();\n        var r1 = new SystemsRunner(world);\n        r1.Add(new RunOnly(\"p1\", j));\n" +
                            "        var r0 = new SystemsRunner(world);\n        r0.Add(new RunOnly(\"s1\", j));\n        " + add + "\n" +
                            "        return Journal.ExpectThrows<InvalidOperationException>(() => r0.Run(), \"r0.Run() before Init\")\n" +
                            "            ?? Journal.ExpectThrows<InvalidOperationException>(() => r1.Run(), \"phase Run() before Init\");"
            });
            result.Scenarios.Add(("debug-run-before-init", "finding", string.Join("; ", problems)));
        }

        // CHANGELOG 2.2.0 / runner.md: "AddPhase/AddGroup throw for a null runner or an empty name in every build".
        // The exception type is not named, so any exception counts.
        private static void NestedArgs(Ctx ctx, CustomResult result) {
            if (ctx.Contract != Contract.V22 || !Api.HasAddGroup || !Api.HasAddPhase) {
                result.Scenarios.Add(("nested-args", "n/a", "AddGroup/AddPhase are not in this version"));
                return;
            }
            var calls = new List<(string Code, Action<SystemsRunner, World> Call)> {
                ("r0.AddGroup(\"g\", null)", (r, w) => Api.AddGroupFn(r, "g", null)),
                ("r0.AddGroup(\"\", new SystemsRunner(world))", (r, w) => Api.AddGroupFn(r, "", new SystemsRunner(w))),
                ("r0.AddPhase(\"p\", null)", (r, w) => Api.AddPhaseFn(r, "p", null)),
                ("r0.AddPhase(\"\", new SystemsRunner(world))", (r, w) => Api.AddPhaseFn(r, "", new SystemsRunner(w)))
            };
            var silent = new List<string>();
            var thrownTypes = new List<string>();
            foreach (var (code, call) in calls) {
                var world = new World();
                var root = new SystemsRunner(world);
                var thrown = Journal.Catch(() => call(root, world));
                if (thrown == null) {
                    silent.Add(code);
                } else {
                    thrownTypes.Add(code + " -> " + thrown.GetType().Name);
                }
            }
            if (silent.Count == 0) {
                result.Scenarios.Add(("nested-args", "pass", string.Join("; ", thrownTypes)));
                return;
            }
            result.Findings.Add(new CustomFinding {
                Rule = Rules.NestedArgs,
                Scenario = "nested-args",
                Message = "no exception from: " + string.Join("; ", silent),
                ReproBody = "var world = new World();\n        var r0 = new SystemsRunner(world);\n        return " +
                            string.Join("\n            ?? ", silent.Select(code => "Journal.ExpectThrows<Exception>(() => " + code + ", \"" + code.Replace("\"", "\\\"") + "\")")) + ";"
            });
            result.Scenarios.Add(("nested-args", "finding", "no exception from: " + string.Join("; ", silent)));
        }

        private static void DelHereTwice(Ctx ctx, CustomResult result) {
            var world = new World();
            var root = new SystemsRunner(world);
            var thrown = Journal.Catch(() => {
                root.DelHere<Ev0>();
                root.DelHere<Ev0>();
            });
            if (thrown == null) {
                result.Scenarios.Add(("delhere-twice", "pass", "the same DelHere<T> twice is accepted (\"DelHere entries are exempt\")"));
                return;
            }
            result.Findings.Add(new CustomFinding {
                Rule = Rules.BuildThrew,
                Scenario = "delhere-twice",
                Message = "DelHere<Ev0>() twice threw " + Journal.Describe(thrown) + " (runner.md: \"DelHere entries are exempt\")",
                ReproBody = "var world = new World();\n        var r0 = new SystemsRunner(world);\n        return Journal.ExpectNoThrow(() => { r0.DelHere<Ev0>(); r0.DelHere<Ev0>(); }, \"DelHere twice\");"
            });
            result.Scenarios.Add(("delhere-twice", "finding", Rules.BuildThrew));
        }

        // ------------------------------------------------------------ observations -> contract questions

        private static void ObserveDestroyThrows(Ctx ctx, CustomResult result) {
            var world = new World();
            var j = new Journal();
            var group = new SystemsRunner(world);
            var inner = new InitDestroy("s2", j);
            group.Add(inner);
            var root = new SystemsRunner(world);
            root.Add(group);
            var thrower = new InitDestroy("s3", j) { ThrowOnDestroy = true };
            root.Add(thrower);
            root.Init();
            j.Begin();
            j.DestroyThrowsArmed = true;
            var thrown = Journal.Catch(() => root.Destroy());
            j.DestroyThrowsArmed = false;
            string destroyLog = string.Join(" ", j.Slice());
            bool groupStillInit = group.IsInitialized;
            j.Begin();
            var reinit = Journal.Catch(() => root.Init());
            string reinitLog = string.Join(" ", j.Slice());
            result.Questions.Add(new ContractQuestion {
                Id = "Q-DESTROY-THROWS",
                Title = "What does Destroy() do when the Destroy of one system throws?",
                Question = "The contract of Destroy speaks only of the order and of a no-op before Init, not of an exception. Does Destroy stop, are the remaining systems destroyed, " +
                           "do nested runners stay initialized?",
                Observed = "root[group[s2], s3 throws in Destroy]: Destroy -> " + Journal.Describe(thrown) + ", log [" + destroyLog + "], " +
                           "group.IsInitialized=" + groupStillInit + "; Init again -> log [" + reinitLog + "]" + (reinit != null ? ", " + Journal.Describe(reinit) : "") +
                           (groupStillInit ? ": s2 is not destroyed and is not initialized again by the next Init" : ""),
                ObservedKey = (thrown != null ? thrown.GetType().Name : "none") + "|" + destroyLog + "|group-initialized=" + groupStillInit + "|" + reinitLog,
                WhyNotFinding = "What Destroy() does when a Destroy throws is not documented (the rollback of Init is described separately and is checked)."
            });
        }

        private static void ObserveRollbackErrorOrder(Ctx ctx, CustomResult result) {
            if (ctx.Contract != Contract.V22) {
                return;
            }
            var world = new World();
            var j = new Journal();
            var root = new SystemsRunner(world);
            root.Add(new InitDestroy("s1", j) { ThrowOnDestroy = true });
            root.Add(new InitDestroy("s2", j) { ThrowOnDestroy = true });
            root.Add(new InitDestroy("s3", j) { FailInit = 1 });
            j.DestroyThrowsArmed = true;
            var thrown = Journal.Catch(() => root.Init());
            j.DestroyThrowsArmed = false;
            result.Questions.Add(new ContractQuestion {
                Id = "Q-ROLLBACK-ERROR-ORDER",
                Title = "In which order do the rollback errors follow the original one in the AggregateException?",
                Question = "The documents say \"followed by the rollback errors\" and name no order among them (the test V220RunnerTests pins the order of the rollback itself).",
                Observed = Journal.Structure(thrown),
                ObservedKey = Journal.Structure(thrown),
                WhyNotFinding = "The stand only checks that the original comes first, followed by exactly the rollback errors (as a set)."
            });
        }

        private static void ObserveDisabledRootGroups(Ctx ctx, CustomResult result) {
            if (ctx.Contract != Contract.V22 || !Api.HasSetActiveSelf || !Api.HasCleanWhenDisabled) {
                return;
            }
            var world = new World();
            var j = new Journal();
            var group = new SystemsRunner(world);
            group.Add(new RunOnly("s2", j));
            Api.OneFrame(group, 0, true);
            var root = new SystemsRunner(world);
            root.Add(group);
            Api.OneFrame(root, 1, true);
            root.Init();
            Api.SetActiveSelfFn(root, false);
            Events.Create(world, 0);
            Events.Create(world, 1);
            root.Run();
            result.Questions.Add(new ContractQuestion {
                Id = "Q-DISABLED-ROOT-GROUP-CLEANUP",
                Title = "Does Run() of a disabled root remove the cleanWhenDisabled types of its groups?",
                Question = "SetActive(bool): \"a disabled root runs no systems and only removes its cleanWhenDisabled OneFrame types\"; for a phase the documents add \"(and those of its groups)\", for the root they do not.",
                Observed = "root cleanWhenDisabled Ev1 -> " + Events.Count(world, 1) + " left; group cleanWhenDisabled Ev0 -> " + Events.Count(world, 0) + " left",
                ObservedKey = "root=" + Events.Count(world, 1) + ";group=" + Events.Count(world, 0),
                WhyNotFinding = "The model of the stand treats the number of such components as unknown and does not check it."
            });
        }
    }
}
