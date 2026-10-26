// Stand module "runner": entry point. See run.sh for the command line.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Stand.Runner.Kit;

namespace Stand.Runner {
    public static class Program {
#if KENSEI_DEBUG
        private const bool CompiledDebug = true;
#else
        private const bool CompiledDebug = false;
#endif

        private sealed class Options {
            public string Mode = "quick";
            public string Src;
            public string Flavor;
            public bool Debug;
            public int Seed = 20261023;
            public int Cases = -1;
            public string Out;
            public string PackageVersion;
            /// <summary> Profile the contract of this version instead of the package.json one. </summary>
            public string ContractVersion;
            public string Replay;
            public string Finalize;
            public string KitDir;
            public double TimeBudgetSeconds = -1;
            /// <summary> Regenerate the random tree of one seed from a report (with the --quick/--full parameters) and print what it violates. </summary>
            public int? GenSeed;
            /// <summary> report.json of the baseline run of this module (same build) to compare signatures and observations with. </summary>
            public string BaselineReport;
            public string BaselineLabel;
        }

        public static int Main(string[] args) {
            Options o;
            try {
                o = Parse(args);
            } catch (ArgumentException e) {
                Console.Error.WriteLine("runner: " + e.Message);
                return 64;
            }
            if (o.Finalize != null) {
                return Finalize(o.Finalize);
            }
            if (o.Debug != CompiledDebug) {
                Console.Error.WriteLine("runner: --debug=" + o.Debug + " but the stand was compiled " + (CompiledDebug ? "with" : "without") + " KENSEI_DEBUG");
                return 65;
            }
            if (Api.LibraryDebug != CompiledDebug) {
                Console.Error.WriteLine("runner: the package was compiled " + (Api.LibraryDebug ? "with" : "without") + " KENSEI_DEBUG, the stand the other way");
                return 65;
            }
            var profile = o.ContractVersion != null
                ? ContractProfile.For(o.ContractVersion, "--contract-version; package.json says " + (o.PackageVersion ?? "?"), o.Src)
                : ContractProfile.For(o.PackageVersion, "package.json", o.Src);
            var ctx = new Ctx { Profile = profile, Debug = CompiledDebug };
            if (o.Replay != null) {
                return Replay(o.Replay, ctx);
            }
            if (o.GenSeed != null) {
                return FromSeed(o.GenSeed.Value, ParamsOf(o.Mode), ctx);
            }
            return Run(o, ctx);
        }

        private static GenParams ParamsOf(string mode) {
            return mode == "full"
                ? new GenParams { MaxEntries = 7, MaxDepth = 4, MaxTotalEntries = 28, MaxOps = 32 }
                : new GenParams { MaxEntries = 5, MaxDepth = 3, MaxTotalEntries = 14, MaxOps = 16 };
        }

        private static string Sig(string rule, string place) {
            return rule + " @ " + (place ?? "?");
        }

        /// <summary> The random case of a seed (as the report names it), its violations and, per signature, the shrunk case. </summary>
        private static int FromSeed(int seed, GenParams p, Ctx ctx) {
            var spec = Gen.Random(seed, p, ctx);
            Console.WriteLine("seed " + seed + " (" + p + "), contract " + ctx.Profile.Summary());
            Console.WriteLine("tree: " + spec.Describe());
            Console.WriteLine("steps: " + string.Join(" -> ", spec.Ops.Select(op => ReproGen.Describe(op, spec))));
            var result = Engine.Run(spec, ctx);
            if (result.NotApplicable) {
                Console.WriteLine("not applicable: " + result.NotApplicableReason);
                return 0;
            }
            if (result.Violations.Count == 0) {
                Console.WriteLine("no violation");
                return 0;
            }
            foreach (var v in result.Violations) {
                Console.WriteLine(Sig(v.Rule, v.Place) + " [" + StatusFor(ctx.Profile, v.Rule) + "] at step " + (v.OpIndex + 1) + ": " + v.Message);
                var shrunk = Shrinker.Shrink(spec, v.Rule, v.Place, ctx);
                Console.WriteLine("  shrunk tree: " + shrunk.Describe());
                Console.WriteLine("  shrunk steps: " + string.Join(" -> ", shrunk.Ops.Select(op => ReproGen.Describe(op, shrunk))));
            }
            return 1;
        }

        /// <summary> defect or question: a rule the profile switched off counts as a question if it fires anyway. </summary>
        private static string StatusFor(ContractProfile profile, string rule) {
            string s = profile.StatusOf(rule);
            return s == "defect" ? "defect" : "question";
        }

        private static Options Parse(string[] args) {
            var o = new Options();
            for (int i = 0; i < args.Length; i++) {
                string a = args[i];
                string Next() {
                    if (i + 1 >= args.Length) {
                        throw new ArgumentException(a + " needs a value");
                    }
                    return args[++i];
                }
                switch (a) {
                    case "--quick": o.Mode = "quick"; break;
                    case "--full": o.Mode = "full"; break;
                    case "--src": o.Src = Next(); break;
                    case "--flavor": o.Flavor = Next(); break;
                    case "--debug": o.Debug = true; break;
                    case "--seed": o.Seed = int.Parse(Next()); break;
                    case "--cases": o.Cases = int.Parse(Next()); break;
                    case "--out": o.Out = Next(); break;
                    case "--pkg-version": o.PackageVersion = Next(); break;
                    case "--contract-version": o.ContractVersion = Next(); break;
                    case "--replay": o.Replay = Next(); break;
                    case "--finalize": o.Finalize = Next(); break;
                    case "--kit": o.KitDir = Next(); break;
                    case "--time-budget": o.TimeBudgetSeconds = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                    case "--gen-seed": o.GenSeed = int.Parse(Next()); break;
                    case "--baseline-report": o.BaselineReport = Next(); break;
                    case "--baseline-label": o.BaselineLabel = Next(); break;
                    default: throw new ArgumentException("unknown argument " + a);
                }
            }
            if (o.Replay == null && o.Finalize == null && o.GenSeed == null && o.Out == null) {
                throw new ArgumentException("--out <report dir> is required");
            }
            if (o.ContractVersion != null && !SemVer.TryParse(o.ContractVersion, out _)) {
                throw new ArgumentException("--contract-version needs a version such as 2.2.2, got " + o.ContractVersion);
            }
            return o;
        }

        // ------------------------------------------------------------------ main run

        private sealed class Collector {
            public readonly Dictionary<string, Finding> Findings = new Dictionary<string, Finding>();
            public readonly Dictionary<string, string> ReproTexts = new Dictionary<string, string>();
            public RunReport Report;
            public Ctx Ctx;
            public string CaseDir;

            public SignatureInfo SignatureOf(Finding f, string place) {
                string sig = Sig(f.Id, place);
                return f.Signatures.FirstOrDefault(s => s.Signature == sig);
            }

            public (Finding Finding, SignatureInfo Signature, bool NewSignature, string FileBase) Get(string rule, string place) {
                var info = Rules.Get(rule);
                if (!Findings.TryGetValue(rule, out var f)) {
                    f = new Finding {
                        Id = info.Id, Known = info.Known, Class = info.Class, Severity = info.Severity, Title = info.Title, Rule = info.Contract, Caveat = info.Caveat
                    };
                    Findings[rule] = f;
                }
                var si = SignatureOf(f, place);
                if (si != null) {
                    return (f, si, false, null);
                }
                si = new SignatureInfo { Signature = Sig(rule, place), Rule = rule, Place = place };
                string fileBase = f.Signatures.Count == 0 ? info.Id : info.Id + ".s" + (f.Signatures.Count + 1);
                f.Signatures.Add(si);
                if (f.Signatures.Count == 1) {
                    f.Signature = si.Signature;
                }
                return (f, si, true, fileBase);
            }
        }

        private static int Run(Options o, Ctx ctx) {
            var clock = Stopwatch.StartNew();
            string config = ctx.Debug ? "dbg" : "rel";
            var report = new RunReport {
                Flavor = o.Flavor ?? Attr("StandFlavor"),
                Config = config,
                KenseiDebug = ctx.Debug,
                Src = o.Src ?? Attr("KenseiSrc"),
                PackageVersion = o.PackageVersion ?? "?",
                Contract = ctx.Profile.Summary(),
                ContractProfile = ProfileReport.Of(ctx.Profile),
                Mode = o.Mode,
                BaseSeed = o.Seed,
                Features = Api.Features(),
                Generated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };
            foreach (var kv in report.Features.Where(kv => !kv.Value)) {
                report.NotInThisVersion.Add(kv.Key);
            }
            if (ctx.Contract == Contract.V22 && !Api.HasAddGroup) {
                report.Notes.Add("the contract profile says 2.2+, but AddGroup is missing: the group checks are off");
            }
            string outDir = o.Out;
            string reproDir = Path.Combine(outDir, "repro");
            string caseDir = Path.Combine(outDir, "cases");
            if (Directory.Exists(reproDir)) {
                Directory.Delete(reproDir, true);
            }
            if (Directory.Exists(caseDir)) {
                Directory.Delete(caseDir, true);
            }
            Directory.CreateDirectory(reproDir);
            Directory.CreateDirectory(caseDir);

            var c = new Collector { Report = report, Ctx = ctx, CaseDir = caseDir };

            // 1. Targeted scenarios through the model.
            foreach (var sc in Scenarios.All()) {
                string unsupported = sc.Unsupported(ctx);
                if (unsupported != null) {
                    report.Scenarios.Add(new ScenarioStatus { Name = sc.Name, Title = sc.Title, Status = "n/a", Detail = unsupported });
                    continue;
                }
                var spec = sc.Build(ctx);
                spec.Origin = "scenario:" + sc.Name;
                CaseResult result;
                try {
                    result = Engine.Run(spec, ctx);
                } catch (Exception e) {
                    report.Scenarios.Add(new ScenarioStatus { Name = sc.Name, Title = sc.Title, Status = "stand-error", Detail = e.ToString() });
                    continue;
                }
                if (sc.Question != null) {
                    var q = sc.Question(ctx, spec, result);
                    if (q != null) {
                        report.ContractQuestions.Add(q);
                    }
                }
                if (result.NotApplicable) {
                    report.Scenarios.Add(new ScenarioStatus { Name = sc.Name, Title = sc.Title, Status = "n/a", Detail = result.NotApplicableReason });
                    continue;
                }
                string observation = null;
                if (sc.Observe != null) {
                    try {
                        observation = sc.Observe(ctx, spec);
                    } catch (Exception e) {
                        observation = "observation failed: " + e.GetType().Name + ": " + e.Message;
                    }
                }
                string rejected = result.SharedRejected ? "the second registration of the shared runner was rejected, as 2.2.2 requires; the rest ran without it" : null;
                if (result.Violations.Count == 0) {
                    report.Scenarios.Add(new ScenarioStatus {
                        Name = sc.Name, Title = sc.Title, Status = "pass",
                        Detail = Join(sc.Targets != null ? "no " + sc.Targets : "the contract holds", rejected, observation)
                    });
                    continue;
                }
                var rules = string.Join(", ", result.Violations.Select(v => Sig(v.Rule, v.Place) + " [" + StatusFor(ctx.Profile, v.Rule) + "]"));
                bool onlyQuestions = result.Violations.All(v => StatusFor(ctx.Profile, v.Rule) == "question");
                report.Scenarios.Add(new ScenarioStatus {
                    Name = sc.Name, Title = sc.Title, Status = onlyQuestions ? "question" : "finding",
                    Detail = Join(rules + ": " + result.Violations[0].Message, rejected, observation)
                });
                foreach (var v in result.Violations) {
                    AddModelFinding(c, v, spec, "scenario:" + sc.Name, seed: null, genParams: null);
                    if (observation != null && c.Findings.TryGetValue(v.Rule, out var withObservation)) {
                        withObservation.Observations.Add(sc.Name + ": " + observation);
                    }
                }
            }

            // 2. Hand-written scenarios: duplicate names, Add after Init, EcsBootstrap, observations.
            var custom = new CustomResult();
            try {
                CustomScenarios.RunAll(ctx, custom);
                BootstrapScenarios.RunAll(ctx, custom);
            } catch (Exception e) {
                report.Notes.Add("stand error in the hand-written scenarios: " + e);
            }
            foreach (var (name, status, detail) in custom.Scenarios) {
                bool question = status == "finding" && custom.Findings.Where(f => f.Scenario == name).All(f => StatusFor(ctx.Profile, f.Rule) == "question");
                report.Scenarios.Add(new ScenarioStatus { Name = name, Status = question ? "question" : status, Detail = detail });
            }
            report.ContractQuestions.AddRange(custom.Questions);
            foreach (var cf in custom.Findings) {
                AddCustomFinding(c, cf);
            }

            // 3. Random trees.
            var p = ParamsOf(o.Mode);
            int cases = o.Cases > 0 ? o.Cases : o.Mode == "full" ? 300000 : 20000;
            double budget = o.TimeBudgetSeconds > 0 ? o.TimeBudgetSeconds : o.Mode == "full" ? 600 : 60;
            int shrinkCap = o.Mode == "full" ? 200 : 60;
            int shrinks = 0;
            report.GenParams = p.ToString();
            var sigHits = new Dictionary<string, int>();
            var ruleHits = new Dictionary<string, int>();
            var coverage = new Coverage();
            var fuzzCtx = new Ctx { Profile = ctx.Profile, Debug = ctx.Debug, Coverage = coverage };
            var fuzzClock = Stopwatch.StartNew();
            int ran = 0;
            var unshrunk = new HashSet<string>();
            for (int i = 0; i < cases; i++) {
                if (fuzzClock.Elapsed.TotalSeconds > budget) {
                    report.Notes.Add("the random test stopped after " + ran + " trees (time limit " + budget + " s)");
                    break;
                }
                int seed = unchecked(o.Seed * 7919 + i);
                var spec = Gen.Random(seed, p, ctx);
                CaseResult result;
                try {
                    result = Engine.Run(spec, fuzzCtx);
                } catch (Exception e) {
                    report.Notes.Add("stand error on seed " + seed + ": " + e.GetType().Name + ": " + e.Message);
                    File.WriteAllText(Path.Combine(caseDir, "stand-error-" + seed + ".json"), spec.ToJson());
                    continue;
                }
                ran++;
                report.RandomOps += result.OpsRun;
                if (result.SharedRejected) {
                    report.RandomCasesRejected++;
                }
                if (result.NotApplicable) {
                    report.RandomCasesNotApplicable++;
                    continue;
                }
                foreach (var v in result.Violations) {
                    string sig = Sig(v.Rule, v.Place);
                    sigHits.TryGetValue(sig, out int hits);
                    sigHits[sig] = hits + 1;
                    ruleHits.TryGetValue(v.Rule, out int rh);
                    ruleHits[v.Rule] = rh + 1;
                    if (c.Findings.TryGetValue(v.Rule, out var known)) {
                        var si = c.SignatureOf(known, v.Place);
                        if (si != null) {
                            if (si.Seed == null) {
                                // Seen in a scenario already: keep the seed for --gen-seed, the scenario repro stays.
                                si.Seed = seed;
                                si.Params = p.ToString();
                                if (!si.Sources.Contains("random")) {
                                    si.Sources.Add("random");
                                }
                                if (!known.Sources.Contains("random")) {
                                    known.Sources.Add("random");
                                }
                            }
                            continue;
                        }
                    }
                    if (shrinks >= shrinkCap) {
                        unshrunk.Add(sig);
                    }
                    bool firstOfRule = !c.Findings.ContainsKey(v.Rule);
                    CaseSpec shrunk;
                    Violation again;
                    if (shrinks < shrinkCap) {
                        shrinks++;
                        shrunk = Shrinker.Shrink(spec, v.Rule, v.Place, ctx, firstOfRule ? 4000 : 1500, firstOfRule ? 2000 : 800);
                        again = Engine.Run(shrunk, ctx).Violations.FirstOrDefault(x => x.Rule == v.Rule && x.Place == v.Place);
                        if (again == null) {
                            shrunk = spec.Clone();
                            again = v;
                        }
                    } else {
                        shrunk = spec.Clone();
                        again = v;
                    }
                    shrunk.Seed = seed;
                    shrunk.Origin = "random";
                    AddModelFinding(c, again, shrunk, "random", seed, p.ToString());
                }
            }
            if (unshrunk.Count > 0) {
                report.Notes.Add(unshrunk.Count + " signature(s) found after the shrink limit (" + shrinkCap + ") keep their unshrunk case: " + string.Join(", ", unshrunk.Take(10)));
            }
            report.RandomCases = ran;
            report.Coverage = coverage.Counts;
            report.Notes.Add("random test: " + fuzzClock.Elapsed.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s");
            foreach (var f in c.Findings.Values) {
                ruleHits.TryGetValue(f.Id, out int h);
                f.RandomHits = h;
                foreach (var s in f.Signatures) {
                    sigHits.TryGetValue(s.Signature, out int sh);
                    s.Hits = sh;
                }
            }

            // 4. Status by the contract profile: defects, or questions for this version.
            foreach (var f in c.Findings.Values.OrderBy(f => f.Id, StringComparer.Ordinal)) {
                var rs = ctx.Profile.Describe(f.Id);
                f.Status = StatusFor(ctx.Profile, f.Id);
                f.ContractBasis = rs?.Basis;
                f.Since = rs?.Since;
                foreach (var s in f.Signatures) {
                    s.Status = f.Status;
                }
                if (f.Status == "defect") {
                    report.Defects.Add(f);
                    continue;
                }
                report.ContractQuestions.Add(new ContractQuestion {
                    Id = f.Id, Kind = "rule", Rule = f.Id, Known = f.Known, Severity = f.Severity, Since = f.Since, Title = f.Title,
                    Question = rs?.Basis ?? "The contract profile of this version does not count a violation of this rule as a defect.",
                    Observed = f.Message + (f.Observations.Count > 0 ? "; " + string.Join("; ", f.Observations) : ""),
                    WhyNotFinding = rs != null && rs.Status == "off"
                        ? "The rule is not part of the contract of this version."
                        : "The documents of " + ctx.Profile.Version + " do not promise it as stated (see the question); it becomes a defect where they do.",
                    Sources = f.Sources, Signatures = f.Signatures, Repro = f.Repro, Case = f.Case, RandomHits = f.RandomHits
                });
            }

            // 5. Files.
            foreach (var kv in c.ReproTexts) {
                File.WriteAllText(Path.Combine(reproDir, kv.Key), kv.Value);
            }
            string kit = o.KitDir;
            if (kit != null && Directory.Exists(kit)) {
                File.Copy(Path.Combine(kit, "Probes.cs"), Path.Combine(reproDir, "ReproKit.cs"), true);
                File.Copy(Path.Combine(kit, "UnityStubs.cs"), Path.Combine(reproDir, "ReproUnityStubs.cs"), true);
            }
            report.Known = KnownStatuses(report, ctx);
            if (o.BaselineReport != null) {
                CompareWithBaseline(report, o.BaselineReport, o.BaselineLabel);
            }
            report.DurationMs = clock.ElapsedMilliseconds;
            report.Write(outDir);

            Console.WriteLine("runner " + report.Flavor + "-" + config + " (contract " + ctx.Profile.Version + "): " + report.Defects.Count + " defects, " +
                              report.ContractQuestions.Count + " contract questions (" + report.ContractQuestions.Count(q => q.Kind == "rule") + " rule questions), " +
                              (report.Baseline != null ? report.Regressions.Count + " regressions vs " + report.Baseline.Flavor + ", " : "") +
                              report.RandomCases + " random trees, " + (report.DurationMs / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s");
            foreach (var f in report.Defects.OrderBy(f => f.Known ?? "~").ThenBy(f => f.Id)) {
                Console.WriteLine("  defect   " + (f.Known ?? "  ") + " " + f.Id + " [" + f.Severity + "] " + f.Signatures.Count + " signature(s), " + f.RandomHits + " random trees");
            }
            foreach (var q in report.ContractQuestions.Where(q => q.Kind == "rule")) {
                Console.WriteLine("  question " + (q.Known ?? "  ") + " " + q.Id + " " + q.Signatures.Count + " signature(s), " + q.RandomHits + " random trees");
            }
            if (report.Baseline != null) {
                foreach (var s in report.Baseline.NewSignatures) {
                    Console.WriteLine("  new signature: " + s);
                }
                foreach (var s in report.Baseline.GrownSignatures) {
                    Console.WriteLine("  hits jumped: " + s);
                }
                foreach (var r in report.Regressions) {
                    Console.WriteLine("  regression " + r.Id + ": " + r.Now);
                }
            }
            Console.WriteLine("report: " + Path.Combine(outDir, "report.md"));
            return 0;
        }

        private static string Join(params string[] parts) {
            return string.Join("; ", parts.Where(x => !string.IsNullOrEmpty(x)));
        }

        private static List<KnownStatus> KnownStatuses(RunReport report, Ctx ctx) {
            var known = new List<(string Id, string What, string[] Rules)> {
                ("R1", "the Init rollback calls Destroy on systems that implement IDestroySystem only", new[] { Rules.RollbackDestroysNoInit }),
                ("R2", "nested rollback: the AggregateExceptions nest, InnerExceptions[0] is not the original exception", new[] { Rules.RollbackAggregate }),
                ("R3", "a group in two slots: SetActive(index) switches it off everywhere, the other slots report \"enabled\" (2.2.2: the second registration must throw)",
                    new[] { Rules.SharedSlots, Rules.SharedRunnerAccepted, Rules.SharedPropertyTwoSlots }),
                ("R4", "EcsBootstrap with a user system \"fixed\"/\"late\": Awake throws under KENSEI_DEBUG (2.2.2: a clear error naming the reserved names)",
                    new[] { Rules.BootstrapNameCollision, Rules.BootstrapReservedName })
            };
            var list = new List<KnownStatus>();
            foreach (var (id, what, rules) in known) {
                var defect = report.Defects.FirstOrDefault(f => rules.Contains(f.Id));
                var question = report.ContractQuestions.FirstOrDefault(q => q.Kind == "rule" && rules.Contains(q.Rule));
                var status = new KnownStatus { Id = id, What = what };
                if (defect != null) {
                    status.Status = "defect";
                    status.Finding = defect.Id;
                    status.Detail = string.Join(", ", defect.Sources) + (defect.RandomHits > 0 ? "; random test: " + defect.RandomHits + " trees" : "") +
                                    "; " + defect.Signatures.Count + " signature(s)";
                } else if (question != null) {
                    status.Status = "question";
                    status.Finding = question.Rule;
                    status.Detail = "seen (" + string.Join(", ", question.Sources ?? new List<string>()) + (question.RandomHits > 0 ? "; random test: " + question.RandomHits + " trees" : "") +
                                    "), a contract question for " + ctx.Profile.Version + ": see " + question.Rule;
                } else if (ctx.Contract == Contract.V21 && id != "R4") {
                    status.Status = "not-applicable";
                    status.Detail = "2.1 contract: no Init rollback (R1, R2), the slot of an unnamed group is separate from child.IsEnabled (R3) — not a violation";
                } else if (id == "R4" && !ctx.Debug && !ctx.Profile.ReservedBootstrapNames) {
                    status.Status = "not-applicable";
                    status.Detail = "R4 is KENSEI_DEBUG only; in release the same names give " + Rules.DuplicateNameRelease +
                                    (report.Defects.Any(x => x.Id == Rules.DuplicateNameRelease) ? " (found)" : "");
                } else if (id == "R4" && ctx.Contract == Contract.V21) {
                    status.Status = "not-applicable";
                    status.Detail = "2.1 has no KENSEI_DEBUG check of repeated names; the same names give " + Rules.DuplicateNameRelease;
                } else {
                    status.Status = "not-found";
                    status.Detail = "does not reproduce in this version";
                }
                status.Found = status.Status == "defect";
                list.Add(status);
            }
            return list;
        }

        private static string Attr(string key) {
            return typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;
        }

        private static void AddModelFinding(Collector c, Violation v, CaseSpec given, string source, int? seed, string genParams) {
            var info = Rules.Get(v.Rule);
            // The case the violation's step index refers to (2.2.2 runs a case without its extra slots).
            var spec = v.Spec ?? given;
            if (!ReferenceEquals(spec, given)) {
                spec.Seed = given.Seed;
                spec.Origin = given.Origin;
            }
            var (f, si, isNew, fileBase) = c.Get(v.Rule, v.Place);
            if (!f.Sources.Contains(source)) {
                f.Sources.Add(source);
            }
            if (!si.Sources.Contains(source)) {
                si.Sources.Add(source);
            }
            if (seed != null && si.Seed == null) {
                si.Seed = seed;
                si.Params = genParams;
                f.Seed ??= seed;
                f.Params ??= genParams;
            }
            if (!isNew) {
                return;
            }
            var steps = spec.Ops.Take(v.OpIndex + 1).Select(op => ReproGen.Describe(op, spec)).ToList();
            var report = c.Report;
            var header = new List<string> {
                info.Id + (info.Known != null ? " (" + info.Known + ")" : "") + " — " + info.Title,
                "signature: " + si.Signature,
                "class: " + info.Class + "; severity: " + info.Severity + "; status under contract " + c.Ctx.Profile.Version + ": " + StatusFor(c.Ctx.Profile, v.Rule),
                "flavor: " + report.Flavor + "-" + report.Config + " (" + (c.Ctx.Debug ? "KENSEI_DEBUG" : "release") + "), package " + report.PackageVersion,
                "source: " + source + (seed != null ? "; seed " + seed + "; " + genParams : ""),
                "tree: " + spec.Describe(),
                "steps: " + (steps.Count > 0 ? string.Join(" -> ", steps) : "(build only)"),
                "rule: " + info.Contract,
                "observed: " + v.Message,
                "Needs ReproKit.cs (Stand/runner/kit/Probes.cs) and the KenseiECS sources; Stand/runner/repro-check runs every repro."
            };
            c.ReproTexts[fileBase + ".cs"] = ReproGen.Generate(spec, v, ReproGen.ClassName(fileBase), header, c.Ctx);
            File.WriteAllText(Path.Combine(c.CaseDir, fileBase + ".json"), spec.ToJson());
            si.Repro = "repro/" + fileBase + ".cs";
            si.Case = "cases/" + fileBase + ".json";
            si.Message = v.Message;
            si.Tree = spec.Describe();
            si.Steps = steps;
            if (f.Signatures.Count == 1) {
                f.Repro = si.Repro;
                f.Case = si.Case;
                f.Message = v.Message;
                f.Tree = si.Tree;
                f.Steps = steps;
            }
        }

        // Helper types of the hand-written bootstrap repros; renamed per repro file so that several files can share one.
        private static readonly string[] ReproHelperTypes = {
            "ReproLifecycleBootstrap", "ReproSystemFixedBootstrap", "ReproSystemLateBootstrap", "ReproGroupFixedBootstrap", "ReproPhaseNamesBootstrap",
            "ReproGroupInPhaseBootstrap"
        };

        private static void AddCustomFinding(Collector c, CustomFinding cf) {
            var info = Rules.Get(cf.Rule);
            string place = cf.Place ?? cf.Scenario;
            var (f, si, isNew, fileBase) = c.Get(cf.Rule, place);
            string source = "scenario:" + cf.Scenario;
            if (!f.Sources.Contains(source)) {
                f.Sources.Add(source);
            }
            if (!si.Sources.Contains(source)) {
                si.Sources.Add(source);
            }
            if (!isNew) {
                return;
            }
            si.Message = cf.Message;
            if (f.Signatures.Count == 1) {
                f.Message = cf.Message;
            }
            string className = ReproGen.ClassName(fileBase);
            var report = c.Report;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("// " + info.Id + (info.Known != null ? " (" + info.Known + ")" : "") + " — " + info.Title);
            sb.AppendLine("// signature: " + si.Signature);
            sb.AppendLine("// class: " + info.Class + "; severity: " + info.Severity + "; status under contract " + c.Ctx.Profile.Version + ": " + StatusFor(c.Ctx.Profile, cf.Rule));
            sb.AppendLine("// flavor: " + report.Flavor + "-" + report.Config + " (" + (c.Ctx.Debug ? "KENSEI_DEBUG" : "release") + "), package " + report.PackageVersion);
            sb.AppendLine("// source: scenario " + cf.Scenario);
            sb.AppendLine("// rule: " + info.Contract);
            sb.AppendLine("// observed: " + cf.Message);
            sb.AppendLine("// Needs ReproKit.cs and ReproUnityStubs.cs (Stand/runner/kit) and the KenseiECS sources; Stand/runner/repro-check runs every repro.");
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using KenseiECS;");
            sb.AppendLine("using Stand.Runner.Kit;");
            sb.AppendLine();
            if (cf.ReproExtraTypes != null) {
                string extra = cf.ReproExtraTypes;
                // Several findings may share one helper type: keep its name unique per file.
                foreach (var name in ReproHelperTypes) {
                    extra = extra.Replace(name, name + "_" + className);
                }
                sb.AppendLine(extra);
            }
            sb.AppendLine("public static class " + className + " {");
            sb.AppendLine("    /// <summary> Null when the contract holds, otherwise what broke. </summary>");
            sb.AppendLine("    public static string Run() {");
            string body = cf.ReproBody;
            foreach (var name in ReproHelperTypes) {
                body = body.Replace(name, name + "_" + className);
            }
            sb.AppendLine("        " + body);
            sb.AppendLine("    }");
            sb.AppendLine("}");
            c.ReproTexts[fileBase + ".cs"] = sb.ToString();
            si.Repro = "repro/" + fileBase + ".cs";
            if (f.Signatures.Count == 1) {
                f.Repro = si.Repro;
            }
        }

        // ------------------------------------------------------------------ baseline

        /// <summary>
        /// Signatures against the baseline report of the same build (new, a jump in hits, another status, gone) and the
        /// observations of open behaviour that changed: those are the regressions — a change without a contract violation.
        /// </summary>
        private static void CompareWithBaseline(RunReport report, string path, string label) {
            var cmp = new BaselineComparison { Flavor = label ?? Path.GetFileName(Path.GetDirectoryName(path)), Report = path };
            report.Baseline = cmp;
            RunReport b;
            try {
                b = RunReport.Read(Path.GetDirectoryName(path));
            } catch (Exception e) {
                cmp.Note = "the baseline report could not be read (" + e.GetType().Name + ": " + e.Message + "); nothing compared";
                return;
            }
            cmp.HitsComparable = b.Mode == report.Mode && b.BaseSeed == report.BaseSeed && b.RandomCases == report.RandomCases && b.GenParams == report.GenParams;
            if (!cmp.HitsComparable) {
                cmp.Note = "the random tests differ (baseline: " + b.Mode + ", seed " + b.BaseSeed + ", " + b.RandomCases + " trees), so hits are not compared";
            }
            var baseSigs = new Dictionary<string, SignatureInfo>();
            var baseRulesWithoutSignatures = new Dictionary<string, string>();
            if (b.Defects != null && b.Defects.Count > 0) {
                foreach (var f in b.Defects) {
                    foreach (var s in f.Signatures ?? new List<SignatureInfo>()) {
                        s.Status ??= "defect";
                        baseSigs[s.Signature] = s;
                    }
                }
            } else {
                // An older report: findings by rule, without signatures.
                foreach (var f in b.Findings ?? new List<SignatureFinding>()) {
                    if (f.Signature == null) {
                        baseRulesWithoutSignatures[f.Id] = "defect";
                    } else {
                        baseSigs[f.Signature] = new SignatureInfo { Signature = f.Signature, Rule = f.Id, Place = f.Place, Status = f.Status ?? "defect", Hits = f.Hits };
                    }
                }
            }
            foreach (var q in b.ContractQuestions ?? new List<ContractQuestion>()) {
                if (q.Signatures == null) {
                    continue;
                }
                foreach (var s in q.Signatures) {
                    s.Status ??= "question";
                    baseSigs[s.Signature] = s;
                }
            }
            var current = new HashSet<string>();
            foreach (var s in report.AllSignatures()) {
                current.Add(s.Signature);
                if (baseSigs.TryGetValue(s.Signature, out var bs)) {
                    string change = "same";
                    if (bs.Status != s.Status) {
                        change = "status-changed";
                        cmp.StatusChanged.Add(s.Signature + " (" + bs.Status + " -> " + s.Status + ")");
                    } else if (cmp.HitsComparable && s.Hits >= 20 && s.Hits >= 3 * Math.Max(bs.Hits, 1)) {
                        change = "hits-grew";
                        cmp.GrownSignatures.Add(s.Signature + " (" + bs.Hits + " -> " + s.Hits + " random trees)");
                    }
                    s.Baseline = new BaselineInfo { Change = change, Status = bs.Status, Hits = bs.Hits };
                } else if (baseRulesWithoutSignatures.TryGetValue(s.Rule, out var oldStatus)) {
                    s.Baseline = new BaselineInfo { Change = "rule-known", Status = oldStatus };
                } else {
                    s.Baseline = new BaselineInfo { Change = "new" };
                    cmp.NewSignatures.Add(s.Signature + " [" + s.Status + "]");
                }
            }
            foreach (var kv in baseSigs) {
                if (!current.Contains(kv.Key)) {
                    cmp.GoneSignatures.Add(kv.Key + " [" + kv.Value.Status + "]");
                }
            }
            var baseQuestions = (b.ContractQuestions ?? new List<ContractQuestion>()).Where(q => q.Kind != "rule" && q.Id != null).GroupBy(q => q.Id).ToDictionary(g => g.Key, g => g.First());
            foreach (var q in report.ContractQuestions.Where(q => q.Kind != "rule" && q.ObservedKey != null)) {
                if (!baseQuestions.TryGetValue(q.Id, out var bq) || bq.ObservedKey == null) {
                    continue;
                }
                if (bq.ObservedKey != q.ObservedKey) {
                    report.Regressions.Add(new Regression {
                        Id = q.Id, Title = "open behaviour changed: " + q.Title, Was = bq.Observed, Now = q.Observed,
                        Detail = "No document promises either behaviour, so this is not a defect; check that the change is intended."
                    });
                }
            }
        }

        // ------------------------------------------------------------------ replay / finalize

        private static int Replay(string path, Ctx ctx) {
            var spec = CaseSpec.FromJson(File.ReadAllText(path));
            Console.WriteLine("contract " + ctx.Profile.Summary());
            Console.WriteLine("tree: " + spec.Describe());
            Console.WriteLine("steps: " + string.Join(" -> ", spec.Ops.Select(op => ReproGen.Describe(op, spec))));
            var result = Engine.Run(spec, ctx);
            if (result.NotApplicable) {
                Console.WriteLine("not applicable: " + result.NotApplicableReason);
                return 0;
            }
            if (result.Violations.Count == 0) {
                Console.WriteLine("no violation");
                return 0;
            }
            foreach (var v in result.Violations) {
                Console.WriteLine(Sig(v.Rule, v.Place) + " [" + StatusFor(ctx.Profile, v.Rule) + "] at step " + (v.OpIndex + 1) + ": " + v.Message);
            }
            return 1;
        }

        /// <summary> Merge repro-check results (verify.txt: "REPRODUCED|NOT REPRODUCED|ERROR &lt;class&gt;: text") into the report. </summary>
        private static int Finalize(string dir) {
            var report = RunReport.Read(dir);
            string verify = Path.Combine(dir, "repro", "verify.txt");
            if (!File.Exists(verify)) {
                report.Notes.Add("repro-check did not run: " + verify + " is missing");
                report.Write(dir);
                return 0;
            }
            var lines = File.ReadAllLines(verify);
            foreach (var s in report.AllSignatures()) {
                if (s.Repro == null) {
                    continue;
                }
                string cls = ReproGen.ClassName(Path.GetFileNameWithoutExtension(s.Repro));
                var line = lines.FirstOrDefault(l => l.Contains(" " + cls + ":") || l.EndsWith(" " + cls));
                if (line == null) {
                    s.ReproVerified = false;
                    s.ReproVerifyOutput = "repro-check did not find the class " + cls;
                    continue;
                }
                s.ReproVerified = line.StartsWith("REPRODUCED");
                int colon = line.IndexOf(": ", StringComparison.Ordinal);
                s.ReproVerifyOutput = colon >= 0 ? line.Substring(colon + 2) : line;
            }
            foreach (var f in report.Defects) {
                Aggregate(f.Signatures, out f.ReproVerified, out f.ReproVerifyOutput);
            }
            foreach (var q in report.ContractQuestions.Where(q => q.Signatures != null)) {
                Aggregate(q.Signatures, out q.ReproVerified, out _);
            }
            report.Notes.Add("repro-check: " + lines.Count(l => l.StartsWith("REPRODUCED")) + " of " + lines.Length + " repros reproduce");
            report.Write(dir);
            Console.WriteLine("runner: repro verification merged into " + Path.Combine(dir, "report.json"));
            return 0;
        }

        private static void Aggregate(List<SignatureInfo> signatures, out bool? verified, out string output) {
            var checkedOnes = signatures.Where(s => s.ReproVerified != null).ToList();
            verified = checkedOnes.Count == 0 ? (bool?)null : checkedOnes.All(s => s.ReproVerified == true);
            output = checkedOnes.FirstOrDefault(s => s.ReproVerified == false)?.ReproVerifyOutput ?? checkedOnes.FirstOrDefault()?.ReproVerifyOutput;
        }
    }
}
