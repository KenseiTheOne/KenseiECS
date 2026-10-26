using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using KenseiECS;

namespace Stand.Contracts {
    /// <summary>
    /// Stand module "contracts". Normally started by Stand/contracts/run.sh.
    ///
    ///   Stand.Contracts --out DIR [--flavor F] [--mode quick|full] [--seed N] [--docs DIR] [--docs-source S]
    ///                   [--src DIR] [--baseline FLAVOR] [--root DIR]
    ///   Stand.Contracts --finalize --out DIR          merge repro-check.json into the report
    ///
    /// The supervisor runs the matrix in worker processes (--worker matrix): a case that crashes the
    /// process (StackOverflow, fail-fast) or hangs is recorded as CRASH / HANG and the next worker
    /// continues after it. Diagnostics run in their own worker (--worker diag).
    /// </summary>
    public static class Program {
        private sealed class Options {
            public string Out;
            public string Flavor = "wt";
            public string Cfg;
            public string Mode = "quick";
            public int Seed;
            public string Docs;
            public string DocsSource;
            public string Src;
            public string Baseline;
            public string Root;
            public string Worker;
            public int From;
            public string Results;
            public string Marker;
            public bool Finalize;
            public int CaseTimeout = 120;
        }

        public static int Main(string[] argv) {
            Options o;
            try {
                o = Parse(argv);
            } catch (ArgumentException e) {
                Console.Error.WriteLine(e.Message);
                return 2;
            }
            bool debug = typeof(World).Assembly.GetType("KenseiECS.EcsProfiler") != null;
            o.Cfg ??= debug ? "dbg" : "rel";
            Report.Root = o.Root;
            if (o.Worker == "matrix") {
                return MatrixWorker(o, debug);
            }
            if (o.Worker == "diag") {
                return DiagWorker(o, debug);
            }
            if (o.Finalize) {
                return Finalize(o);
            }
            return Supervise(o, debug);
        }

        private static Options Parse(string[] a) {
            var o = new Options();
            for (int i = 0; i < a.Length; i++) {
                string Next() => i + 1 < a.Length ? a[++i] : throw new ArgumentException("missing value after " + a[i]);
                switch (a[i]) {
                    case "--out": o.Out = Next(); break;
                    case "--flavor": o.Flavor = Next(); break;
                    case "--cfg": o.Cfg = Next(); break;
                    case "--mode": o.Mode = Next(); break;
                    case "--quick": o.Mode = "quick"; break;
                    case "--full": o.Mode = "full"; break;
                    case "--seed": o.Seed = int.Parse(Next()); break;
                    case "--docs": o.Docs = Next(); break;
                    case "--docs-source": o.DocsSource = Next(); break;
                    case "--src": o.Src = Next(); break;
                    case "--baseline": o.Baseline = Next(); break;
                    case "--root": o.Root = Next(); break;
                    case "--worker": o.Worker = Next(); break;
                    case "--from": o.From = int.Parse(Next()); break;
                    case "--results": o.Results = Next(); break;
                    case "--marker": o.Marker = Next(); break;
                    case "--finalize": o.Finalize = true; break;
                    case "--case-timeout": o.CaseTimeout = int.Parse(Next()); break;
                    default: throw new ArgumentException("unknown argument " + a[i]);
                }
            }
            if (o.Out == null) {
                throw new ArgumentException("--out is required");
            }
            if (o.Docs == "") {
                o.Docs = null;
            }
            return o;
        }

        private static string XmlPath => Path.ChangeExtension(typeof(Program).Assembly.Location, ".xml");

        private static Policy MakePolicy(Options o, bool debug, out Docs docs) {
            docs = Docs.Load(XmlPath, o.Docs, o.Src);
            return new Policy(docs, debug, VersionProfile.Load(o.Src));
        }

        // =====================================================================
        // Workers
        // =====================================================================

        // A case that leaves this much live memory behind (a static pool filled by a huge request, say)
        // would starve the cases after it: the worker stops and the supervisor starts a fresh one.
        private const long MemoryRestartBytes = 384L << 20;

        private static int MatrixWorker(Options o, bool debug) {
            var policy = MakePolicy(o, debug, out var docs);
            var specs = Matrix.Enumerate(o.Mode == "full", o.Seed);
            using var writer = new StreamWriter(o.Results, true) { AutoFlush = true };
            for (int i = o.From; i < specs.Count; i++) {
                File.WriteAllText(o.Marker, i + "\t" + specs[i].Key);
                var r = Matrix.Run(specs[i], policy, docs, debug, Part.All, out var mat);
                if (mat != null) {
                    mat = Minimize(specs[i], policy, docs, debug, r, mat);
                }
                writer.WriteLine(Json.Serialize(new CaseLine { Result = r, Repro = mat }));
                if (i + 1 < specs.Count && GC.GetTotalMemory(false) > MemoryRestartBytes && GC.GetTotalMemory(true) > MemoryRestartBytes) {
                    File.WriteAllText(o.Marker, "restart\t" + (i + 1));
                    return 3;
                }
            }
            File.WriteAllText(o.Marker, "done");
            return 0;
        }

        // The repro keeps only the setup parts the case refers to, if the case still judges the same with them.
        private static ReproMaterial Minimize(CaseSpec spec, Policy policy, Docs docs, bool debug, CaseResult full, ReproMaterial mat) {
            var required = Matrix.PartsOf(full.CallCode + " " + string.Join(" ", mat.Preludes) + " " + (spec.TargetCode ?? ""));
            if (spec.Scenario == Scenarios.StaleDead || spec.Scenario == Scenarios.StaleReused) {
                required |= Part.Entities;
            }
            if (spec.Scenario == Scenarios.RunnerDestroyed || spec.Scenario == Scenarios.RunnerReinit) {
                required |= Part.Runner;
            }
            if (spec.PostPlayback) {
                required |= Part.Buffer;
            }
            // Smallest first: the target's own objects without the entities (when nothing names them), then
            // with them. A setup is kept only if the case is judged the same with it.
            var candidates = new List<Part>();
            var withoutEntities = (spec.Member.Target.Parts & ~Part.Entities) | required;
            candidates.Add(withoutEntities);
            var withTarget = spec.Member.Target.Parts | required;
            if (withTarget != withoutEntities) {
                candidates.Add(withTarget);
            }
            foreach (var parts in candidates) {
                if (parts == Part.All) {
                    break;
                }
                try {
                    var small = Matrix.Run(spec, policy, docs, debug, parts, out var smallMat);
                    if (smallMat != null && SameJudgement(full, small)) {
                        return smallMat;
                    }
                } catch (Exception) {
                    // try the next, larger setup
                }
            }
            return mat;
        }

        private static bool SameJudgement(CaseResult a, CaseResult b) {
            if (a.Verdict != b.Verdict || a.Outcome.Class != b.Outcome.Class) {
                return false;
            }
            string Sig(CaseResult r) => string.Join(",", r.Judgements.Where(j => j.Verdict >= Verdict.Question).Select(j => j.Rule + ":" + j.ClaimId + ":" + j.Verdict).OrderBy(x => x));
            if (Sig(a) != Sig(b)) {
                return false;
            }
            if (a.Judgements.Any(j => j.Rule == "state") && !b.Diffs.Any(d => !d.Extended)) {
                return false;
            }
            return true;
        }

        private static int DiagWorker(Options o, bool debug) {
            var d = Diagnostics.Run(debug);
            Json.WriteFile(Path.Combine(o.Out, "diag.json"), d);
            return 0;
        }

        // =====================================================================
        // Supervisor
        // =====================================================================

        private static int Supervise(Options o, bool debug) {
            var clock = Stopwatch.StartNew();
            Directory.CreateDirectory(o.Out);
            foreach (var f in new[] { "cases.ndjson", "cases-order2.ndjson", "marker.txt", "diag.json", "report.md", "report.json", "model.json", "matrix.md", "matrix.json",
                         "repro-check.json", "worker.log" }) {
                var p = Path.Combine(o.Out, f);
                if (File.Exists(p)) {
                    File.Delete(p);
                }
            }
            foreach (var f in Directory.GetFiles(o.Out, "diff-vs-*")) {
                File.Delete(f);
            }
            var reproDir = Path.Combine(o.Out, "repro");
            if (Directory.Exists(reproDir)) {
                Directory.Delete(reproDir, true);
            }
            Directory.CreateDirectory(reproDir);

            var policy = MakePolicy(o, debug, out var docs);
            var specs = Matrix.Enumerate(o.Mode == "full", o.Seed);
            string results = Path.Combine(o.Out, "cases.ndjson");
            string marker = Path.Combine(o.Out, "marker.txt");
            string log = Path.Combine(o.Out, "worker.log");
            Console.WriteLine("contracts: " + specs.Count + " cases, " + o.Flavor + "-" + o.Cfg + ", package " + policy.Profile + ", mode " + o.Mode + ", seed " + o.Seed);

            var lines = RunMatrix(o, specs, o.Seed, results, marker, log, policy, out int restarts, out int memoryRestarts);

            // --full: the same matrix in another case order. Static state of the package (type indices,
            // the event list pool, the profiler) is shared by the cases of one worker; a cell whose
            // outcome depends on the order is reported as unstable and is never a defect.
            int seed2 = 0;
            if (o.Mode == "full") {
                seed2 = o.Seed == 0 ? 7919 : unchecked(o.Seed * 31 + 7919);
                var specs2 = Matrix.Enumerate(true, seed2);
                var lines2 = RunMatrix(o, specs2, seed2, Path.Combine(o.Out, "cases-order2.ndjson"), marker, log, policy, out int r2, out int m2);
                restarts += r2;
                memoryRestarts += m2;
                MarkUnstable(lines, lines2, seed2);
            }

            var (dcode, dmarker, _) = RunWorker("diag", o, 0, o.Seed, results, marker, log);
            DiagResult diag = null;
            var diagPath = Path.Combine(o.Out, "diag.json");
            if (dcode == 0 && File.Exists(diagPath)) {
                diag = Json.ReadFile<DiagResult>(diagPath);
            }

            var model = Aggregate(o, debug, lines, diag, docs, policy, specs, restarts);
            model.Meta.MemoryRestarts = memoryRestarts;
            model.Meta.SecondOrderSeed = seed2;
            model.Meta.Seconds = clock.Elapsed.TotalSeconds;

            var matrix = Report.BuildMatrix(model.Meta, lines.Select(l => l.Result).ToList());
            Json.WriteFile(Path.Combine(o.Out, "matrix.json"), matrix);
            File.WriteAllText(Path.Combine(o.Out, "matrix.md"), Report.RenderMatrix(matrix, lines.Select(l => l.Result).ToList()));

            if (o.Baseline != null) {
                CompareWithBaseline(o, model, matrix, lines);
                model.Known = Known.Status(model, debug);   // a known problem may now be a regression
            }

            WriteReports(o.Out, model);
            if (File.Exists(marker)) {
                File.Delete(marker);
            }
            PrintSummary(o, model);
            return 0;
        }

        private static void WriteReports(string outDir, ReportModel model) {
            Json.WriteFile(Path.Combine(outDir, "model.json"), model);
            File.WriteAllText(Path.Combine(outDir, "report.json"), Json.SerializePublic(PublicReport.From(model)));
            File.WriteAllText(Path.Combine(outDir, "report.md"), Report.Render(model));
            if (model.Meta.Baseline != null && model.BaselineNote == null) {
                File.WriteAllText(Path.Combine(outDir, "diff-vs-" + model.Meta.Baseline + ".md"), Report.RenderBaseline(model));
                Json.WriteFile(Path.Combine(outDir, "diff-vs-" + model.Meta.Baseline + ".json"), new {
                    cells = model.BaselineChanges, findings = model.BaselineFindings, signatures = model.BaselineSignatures,
                    regressions = model.Regressions.Select(PublicFinding.From).ToList()
                });
            }
        }

        /// <summary>
        /// Run the matrix in worker processes until every case has a result. A worker that dies or
        /// stops answering costs one case (CRASH / HANG, judged by the policy); a worker that stops
        /// itself because a case left too much memory behind is replaced without a mark.
        /// </summary>
        private static List<CaseLine> RunMatrix(Options o, List<CaseSpec> specs, int seed, string results, string marker, string log, Policy policy,
            out int restarts, out int memoryRestarts) {
            if (File.Exists(results)) {
                File.Delete(results);
            }
            restarts = 0;
            memoryRestarts = 0;
            int from = 0;
            while (from < specs.Count) {
                if (File.Exists(marker)) {
                    File.Delete(marker);
                }
                var (code, text, kind) = RunWorker("matrix", o, from, seed, results, marker, log);
                if (code == 0 && text == "done") {
                    break;
                }
                if (text == null) {
                    // The worker died before its first case: not a finding of any case.
                    throw new InvalidOperationException("matrix worker did not start (exit code " + code + "), see " + log);
                }
                if (code == 3 && text != null && text.StartsWith("restart\t", StringComparison.Ordinal) && int.TryParse(text.Substring(8), out int next) && next > from) {
                    memoryRestarts++;
                    from = next;
                    continue;
                }
                restarts++;
                int k = ReadMarker(text, from);
                var crashed = new CaseResult {
                    Ordinal = k, Key = specs[k].Key, Target = specs[k].Member.Target.Name, Member = specs[k].Member.Display, Name = specs[k].Member.Name,
                    MemberId = specs[k].Member.DocId, Scenario = specs[k].Scenario, Variant = specs[k].VariantLabel,
                    Outcome = new Outcome {
                        Threw = true, ExType = kind, ExShort = kind,
                        Message = kind == "HANG" ? "the worker did not answer for " + o.CaseTimeout + " s" : "the worker exited with code " + code
                    }
                };
                policy.JudgeCrash(specs[k], crashed);
                File.AppendAllText(results, Json.Serialize(new CaseLine { Result = crashed }) + Environment.NewLine);
                Console.WriteLine("  worker " + kind + " at case " + k + ": " + specs[k].Key);
                from = k + 1;
            }
            if (!File.Exists(results)) {
                return new List<CaseLine>();
            }
            return File.ReadAllLines(results).Where(l => l.Length > 0).Select(Json.Deserialize<CaseLine>).OrderBy(l => l.Result.Ordinal).ToList();
        }

        private static string CellSignature(CaseResult r) {
            return (r.Verdict == Verdict.NotRun ? "n/r" : r.Outcome?.Class) + " " + r.Verdict
                + " [" + string.Join(",", r.Judgements.Where(j => j.Verdict >= Verdict.Question).Select(j => j.Rule).Distinct().OrderBy(x => x, StringComparer.Ordinal)) + "]";
        }

        // A case judged differently under the second order is not trusted: its defects become questions.
        private static void MarkUnstable(List<CaseLine> first, List<CaseLine> second, int seed2) {
            var other = second.GroupBy(l => l.Result.Key).ToDictionary(g => g.Key, g => g.First().Result);
            foreach (var line in first) {
                var r = line.Result;
                if (!other.TryGetValue(r.Key, out var r2)) {
                    continue;
                }
                string a = CellSignature(r);
                string b = CellSignature(r2);
                if (a == b) {
                    continue;
                }
                r.Unstable = b + " (seed " + seed2 + ")";
                foreach (var j in r.Judgements.Where(j => j.Verdict == Verdict.Finding)) {
                    j.Verdict = Verdict.Question;
                    j.Reason = "unstable: in another case order (seed " + seed2 + ") the outcome is " + b + "; " + j.Reason;
                }
                r.Judgements.Add(new Judgement {
                    Verdict = Verdict.Question, Rule = "unstable", Severity = "low",
                    Reason = "the outcome depends on the case order: " + a + " with the default seed, " + b + " with seed " + seed2
                });
                r.Verdict = r.Judgements.Max(j => j.Verdict);
            }
        }

        private static (int Code, string Marker, string Kind) RunWorker(string kind, Options o, int from, int seed, string results, string marker, string log) {
            var dll = typeof(Program).Assembly.Location;
            var host = Environment.ProcessPath ?? "dotnet";
            var psi = new ProcessStartInfo(host) {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            };
            if (Path.GetFileNameWithoutExtension(host) == "dotnet") {
                psi.ArgumentList.Add(dll);
            }
            void Arg(string name, string value) {
                if (value != null) {
                    psi.ArgumentList.Add(name);
                    psi.ArgumentList.Add(value);
                }
            }
            Arg("--worker", kind);
            Arg("--out", o.Out);
            Arg("--from", from.ToString());
            Arg("--results", results);
            Arg("--marker", marker);
            Arg("--mode", o.Mode);
            Arg("--seed", seed.ToString());
            Arg("--docs", o.Docs);
            Arg("--src", o.Src);
            Arg("--flavor", o.Flavor);
            Arg("--cfg", o.Cfg);
            // A huge request must fail fast with OutOfMemoryException instead of paging the machine.
            if (Environment.GetEnvironmentVariable("DOTNET_GCHeapHardLimit") == null) {
                psi.Environment["DOTNET_GCHeapHardLimit"] = "0x80000000";
            }
            using var p = Process.Start(psi);
            using var logWriter = new StreamWriter(log, true) { AutoFlush = true };
            p.OutputDataReceived += (_, e) => { if (e.Data != null) { lock (logWriter) { logWriter.WriteLine("[" + kind + "] " + e.Data); } } };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) { lock (logWriter) { logWriter.WriteLine("[" + kind + " err] " + e.Data); } } };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            string last = null;
            var since = Stopwatch.StartNew();
            while (!p.WaitForExit(500)) {
                string now = File.Exists(marker) ? SafeRead(marker) : null;
                if (now != last) {
                    last = now;
                    since.Restart();
                } else if (since.Elapsed.TotalSeconds > o.CaseTimeout) {
                    try {
                        p.Kill(true);
                    } catch (Exception) {
                        // already gone
                    }
                    p.WaitForExit();
                    return (-1, SafeRead(marker), "HANG");
                }
            }
            p.WaitForExit();
            return (p.ExitCode, kind == "matrix" ? SafeRead(marker) : "done", "CRASH");
        }

        private static string SafeRead(string path) {
            try {
                return File.Exists(path) ? File.ReadAllText(path) : null;
            } catch (IOException) {
                return null;
            }
        }

        // "<case index>\t<key>": the case the worker was in when it stopped.
        private static int ReadMarker(string text, int fallback) {
            if (text == null) {
                return fallback;
            }
            int tab = text.IndexOf('\t');
            return tab > 0 && int.TryParse(text.Substring(0, tab), out int k) ? k : fallback;
        }

        // =====================================================================
        // Aggregation
        // =====================================================================

        private static string ClassOfRule(string rule) {
            switch (rule) {
                case "state":
                case "state-extended":
                case "state-after-query":
                case "state-partial":
                    return "state-corruption";
                case "contract":
                case "contract-later":
                case "contract-missing":
                case "xml-exception":
                    return "contract-violation";
                case "argnull":
                    return "misleading-exception";
                case "suspicious":
                    return "suspicious-outcome";
                case "null-deferred":
                case "null-registered":
                    return "null-accepted";
                case "crash":
                case "hang":
                    return "crash";
                default:
                    return rule;
            }
        }

        private static int RulePreference(string rule) {
            switch (rule) {
                case "state": return 0;
                case "contract": case "xml-exception": return 1;
                case "contract-later": case "contract-missing": return 2;
                case "crash": case "hang": return 3;
                case "argnull": return 4;
                default: return 5;
            }
        }

        private static ReportModel Aggregate(Options o, bool debug, List<CaseLine> lines, DiagResult diag, Docs docs, Policy policy, List<CaseSpec> specs, int restarts) {
            var model = new ReportModel { Diag = diag };
            var targets = Catalog.Targets();
            model.Meta = new Meta {
                Flavor = o.Flavor, Cfg = o.Cfg, KenseiDebug = debug, Src = o.Src, PackageVersion = policy.Profile.PackageVersion, Docs = o.Docs,
                DocsSource = o.Docs == null ? "none" : o.DocsSource ?? "given", DocsPages = docs.Pages.Count, XmlMembers = docs.Members.Count,
                Mode = o.Mode, Seed = o.Seed, Cases = lines.Count, Targets = targets.Count, Members = targets.Sum(t => Catalog.Members(t).Count),
                Started = DateTime.UtcNow.ToString("u"), Runtime = Environment.Version.ToString(), WorkerRestarts = restarts, Baseline = o.Baseline
            };
            var results = lines.Select(l => l.Result).ToList();
            foreach (var g in results.GroupBy(r => r.Verdict)) {
                model.Verdicts[g.Key.ToString()] = g.Count();
            }

            string Params(CaseResult r) => "flavor=" + o.Flavor + ", cfg=" + o.Cfg + ", mode=" + o.Mode + ", seed=" + o.Seed + ", case #" + r.Ordinal + " " + r.Key;
            var reproDir = Path.Combine(o.Out, "repro");
            Directory.CreateDirectory(Path.Combine(reproDir, "questions"));

            // --- defects from the matrix: one per signature (mechanism and place) ---
            var groups = new Dictionary<string, List<(CaseLine Line, Judgement J)>>();
            var order = new List<string>();
            foreach (var line in lines) {
                foreach (var j in line.Result.Judgements.Where(x => x.Verdict == Verdict.Finding)) {
                    string key = Signatures.Of(line.Result, j);
                    if (!groups.TryGetValue(key, out var list)) {
                        groups[key] = list = new List<(CaseLine, Judgement)>();
                        order.Add(key);
                    }
                    list.Add((line, j));
                }
            }
            foreach (var key in order) {
                var list = groups[key];
                var (rep, f) = Representative(list);
                var r = rep.Line.Result;
                var j = rep.J;
                f.Kind = "defect";
                f.Rule = j.Rule;
                f.Severity = list.Select(x => x.J.Severity).OrderBy(SevRank).First();
                f.DocRef = j.DocRef;
                f.ClaimState = j.ClaimState;
                f.Since = j.Since;
                f.Signature = key;
                f.Hits = list.Select(x => x.Line.Result.Key).Distinct().Count();
                f.Seed = o.Seed;
                f.Params = Params(r);
                f.Detail = j.Reason;
                f.Class = ClassOfRule(j.Rule);
                string where = Scenarios.DescribeCase(r.Scenario, r.Variant);
                switch (j.Rule) {
                    case "state":
                        f.Id = "state:" + r.Scenario + ":" + Report.Slug(r.Member);
                        f.Title = r.Member + " (" + where + ") " + (r.Outcome.Threw ? "throws " + r.Outcome.ExShort : "returns") + ", and afterwards the safe queries break a documented promise";
                        f.Expected = "as on a fresh object in the same state: " + string.Join("; ", r.Diffs.Where(d => !d.Extended).Select(d => d.Probe + " -> " + d.Expected));
                        break;
                    case "contract":
                        f.Id = "contract:" + j.ClaimId + ":" + Report.Slug(r.Member);
                        f.Title = r.Member + " (" + where + (r.Variant != "-" && !r.Variant.StartsWith("curated:", StringComparison.Ordinal) ? ", " + r.Variant : "") + "): " + j.Reason;
                        f.Expected = j.DocRef;
                        break;
                    case "xml-exception":
                        f.Id = "xml-exception:" + Report.Slug(r.Member) + "@" + r.Scenario;
                        f.Title = r.Member + " (" + where + "): " + j.Reason;
                        f.Expected = j.DocRef;
                        break;
                    default:
                        f.Id = j.Rule + ":" + Report.Slug(r.Member) + "@" + r.Scenario;
                        f.Title = r.Member + " (" + where + "): " + j.Reason;
                        break;
                }
                f.Id = Unique(f.Id, model.Findings);
                if (rep.Line.Repro != null) {
                    f.Repro = "repro/" + ReproWriter.FileName(f.Id);
                    File.WriteAllText(Path.Combine(o.Out, f.Repro), ReproWriter.Write(f, rep.Line.Repro, o.Flavor, o.Cfg, o.Mode, o.Seed));
                }
                model.Findings.Add(f);
            }

            // --- questions: one per member, scenario, outcome and set of rules; the signature of the main one ---
            var qgroups = new Dictionary<string, List<(CaseLine Line, Judgement J, List<Judgement> All)>>();
            var qorder = new List<string>();
            foreach (var line in lines) {
                var r = line.Result;
                if (r.Verdict != Verdict.Question) {
                    continue;
                }
                var qs = r.Judgements.Where(x => x.Verdict == Verdict.Question).ToList();
                var main = qs.OrderByDescending(CaseResult.Rank).ThenBy(x => RulePreference(x.Rule)).First();
                string rules = string.Join("+", qs.Select(x => x.Rule).Distinct());
                string key = rules + "|" + r.Member + "|" + r.Scenario + "|" + r.Outcome.Class;
                if (!qgroups.TryGetValue(key, out var list)) {
                    qgroups[key] = list = new List<(CaseLine, Judgement, List<Judgement>)>();
                    qorder.Add(key);
                }
                list.Add((line, main, qs));
            }
            foreach (var key in qorder) {
                var list = qgroups[key];
                var rep = list.OrderBy(x => x.Line.Repro == null ? 1 : 0).ThenBy(x => x.Line.Result.Ordinal).First();
                var r = rep.Line.Result;
                string rules = string.Join("+", rep.All.Select(x => x.Rule).Distinct());
                var f = new Finding {
                    Kind = "question", Class = ClassOfRule(rep.J.Rule), Rule = rules, Severity = rep.All.Select(x => x.Severity).OrderBy(SevRank).First(),
                    Member = r.Member, Scenario = r.Scenario, Variant = r.Variant, Seed = o.Seed, Params = Params(r),
                    Detail = string.Join("; ", rep.All.Select(x => x.Reason).Distinct()),
                    DocRef = rep.J.DocRef, ClaimState = rep.J.ClaimState, Since = rep.J.Since,
                    Signature = Signatures.Of(r, rep.J), Hits = list.Count,
                    Outcome = r.Outcome.Threw ? r.Outcome.ExShort + (r.Outcome.Message != null ? ": " + Trim(r.Outcome.Message, 100) : "") : "returned " + r.Outcome.Value,
                    Title = r.Member + " (" + Scenarios.DescribeCase(r.Scenario, r.Variant) + "): " + rep.J.Reason
                };
                f.CaseKeys.AddRange(list.Select(x => x.Line.Result.Key).Distinct());
                f.AlsoVia.AddRange(list.Where(x => x.Line != rep.Line).Select(x => x.Line.Result.Variant).Distinct());
                f.Id = Unique("q:" + rules + ":" + Report.Slug(r.Member) + "@" + r.Scenario + ":" + r.Outcome.Class, model.Questions);
                if (rep.Line.Repro != null) {
                    f.Repro = "repro/questions/" + ReproWriter.FileName(f.Id);
                    File.WriteAllText(Path.Combine(o.Out, f.Repro), ReproWriter.Write(f, rep.Line.Repro, o.Flavor, o.Cfg, o.Mode, o.Seed));
                }
                model.Questions.Add(f);
            }

            // --- documented: a changed state after an exception that the docs describe ---
            var dgroups = new Dictionary<string, List<(CaseLine Line, Judgement J)>>();
            var dorder = new List<string>();
            foreach (var line in lines) {
                var r = line.Result;
                if (r.Verdict == Verdict.Finding) {
                    continue;
                }
                foreach (var j in r.Judgements.Where(x => x.Rule == "state-partial" && x.Verdict == Verdict.Doc)) {
                    string key = Signatures.Of(r, j);
                    if (!dgroups.TryGetValue(key, out var list)) {
                        dgroups[key] = list = new List<(CaseLine, Judgement)>();
                        dorder.Add(key);
                    }
                    list.Add((line, j));
                }
            }
            foreach (var key in dorder) {
                var list = dgroups[key];
                var rep = list.OrderBy(x => x.Line.Result.Ordinal).First();
                var r = rep.Line.Result;
                var f = new Finding {
                    Kind = "documented", Class = "state-corruption", Rule = "state-partial", Severity = "info", Member = r.Member, Scenario = r.Scenario, Variant = r.Variant,
                    Seed = o.Seed, Params = Params(r), Detail = rep.J.Reason, DocRef = rep.J.DocRef, Quote = rep.J.Quote, ClaimState = rep.J.ClaimState, Since = rep.J.Since,
                    Signature = key, Hits = list.Select(x => x.Line.Result.Key).Distinct().Count(),
                    Outcome = r.Outcome.Threw ? r.Outcome.ExShort + ": " + Trim(r.Outcome.Message, 100) : "returned " + r.Outcome.Value,
                    Title = r.Member + " (" + Scenarios.DescribeCase(r.Scenario, r.Variant) + ") throws " + r.Outcome.ExShort + " and leaves the state changed, as its documented partial effect says"
                };
                f.CaseKeys.AddRange(list.Select(x => x.Line.Result.Key).Distinct());
                f.AlsoVia.AddRange(list.Where(x => x.Line != rep.Line).Select(x => x.Line.Result.Variant).Distinct());
                f.Id = Unique("state:" + r.Scenario + ":" + Report.Slug(r.Member), model.Documented);
                model.Documented.Add(f);
            }

            // --- diagnostics and the promises outside the matrix ---
            if (diag != null) {
                AddDiagnostics(o, model, diag, policy, debug);
                foreach (var e in diag.Errors) {
                    model.StandErrors.Add("diagnostics check " + e);
                }
            } else {
                model.StandErrors.Add("the diagnostics worker did not finish (see worker.log): no [Obsolete], growth, FirstGrowthAfterReserve, span or leak check in this report");
            }

            // --- exception texts ---
            var vocabulary = new HashSet<string>(targets.SelectMany(Catalog.Members).Where(x => !x.IsGetter && !x.IsCtor).Select(x => x.Name));
            var paramsByMember = targets.SelectMany(Catalog.Members).GroupBy(x => x.Display).ToDictionary(g => g.Key, g => g.First().Params.Select(p => p.Name).ToList());
            foreach (var r in results) {
                if (r.Outcome == null || !r.Outcome.Threw || r.Verdict == Verdict.NotRun || r.Outcome.ExType == "CRASH" || r.Outcome.ExType == "HANG") {
                    continue;
                }
                paramsByMember.TryGetValue(r.Member, out var ps);
                model.ExceptionTexts.Add(ClassifyText(r, vocabulary, ps));
            }

            // --- claims ---
            foreach (var (claim, act) in policy.Claims) {
                var judged = results.SelectMany(r => r.Judgements).Where(j => j.ClaimId == claim.Id).ToList();
                model.Claims.Add(new ClaimStat {
                    Id = claim.Id, Kind = claim.Kind.ToString(), Quote = claim.Quote, Since = claim.Since, State = act.State, ActiveAt = act.Where,
                    Cases = judged.Count, Violations = judged.Count(j => j.Verdict == Verdict.Finding || (j.Verdict == Verdict.Question && j.Rule != null && (j.Rule.StartsWith("contract-") || j.Rule == "state")))
                });
            }
            foreach (var c in model.Claims.Where(c => c.State == "missing")) {
                model.ClaimWarnings.Add(c.Id + " (since " + c.Since + "): " + c.ActiveAt);
            }

            // --- coverage ---
            foreach (var g in results.Where(r => r.Verdict == Verdict.NotRun).GroupBy(r => r.Member + ": " + r.WhyNotRun)) {
                model.NotRun.Add(g.Key + " (" + g.Count() + " case(s))");
            }
            foreach (var g in results.SelectMany(r => r.UnknownParams.Select(p => r.Member + " - " + p)).GroupBy(x => x)) {
                model.UnknownParams.Add(g.Key);
            }
            foreach (var r in results.Where(r => r.Unstable != null)) {
                model.Unstable.Add(r.Key + ": " + CellSignature(r) + " / " + r.Unstable);
            }

            // --- known problems and signatures ---
            Known.Tag(model.Findings.Concat(model.Diagnostics).Concat(model.Questions).Concat(model.Documented));
            model.Known = Known.Status(model, debug);
            model.Signatures = SignatureStats(model, lines);
            return model;
        }

        // The representative is the member the others call (World.CreateEntity rather than CommandBuffer.Playback
        // or Warmup, which fail inside it), then a case whose exception the docs name, then the shortest call.
        private static ((CaseLine Line, Judgement J) Rep, Finding F) Representative(List<(CaseLine Line, Judgement J)> list) {
            int Callers((CaseLine Line, Judgement J) c) {
                string frame = "KenseiECS." + c.Line.Result.Target.Split('<')[0] + "." + c.Line.Result.Name;
                int through = list.Count(x => x.Line != c.Line && x.Line.Result.Outcome.Frames.Contains(frame));
                int sites = list.Count(x => x.Line.Result.Outcome.Site != null && x.Line.Result.Outcome.Site.Contains("." + c.Line.Result.Name));
                return through + sites;
            }
            bool Documented((CaseLine Line, Judgement J) c) => c.Line.Result.Judgements.Any(x => x.Rule == "contract" && x.Verdict <= Verdict.Doc);
            var rep = list.OrderBy(x => x.Line.Repro == null ? 1 : 0).ThenByDescending(Callers).ThenByDescending(Documented)
                .ThenBy(x => (x.Line.Result.CallCode ?? "").Length).ThenBy(x => x.Line.Result.Ordinal).First();
            var r = rep.Line.Result;
            var f = new Finding {
                Member = r.Member, Scenario = r.Scenario, Variant = r.Variant,
                Outcome = r.Outcome.Threw ? r.Outcome.ExShort + ": " + r.Outcome.Message : "returned " + r.Outcome.Value
            };
            f.CaseKeys.AddRange(list.Select(x => x.Line.Result.Key).Distinct());
            f.AlsoVia.AddRange(list.Select(x => x.Line.Result.Member + " [" + x.Line.Result.Variant + "]").Distinct().Where(x => x != r.Member + " [" + r.Variant + "]"));
            return (rep, f);
        }

        /// <summary>
        /// Every signature of this run with its hits: defects and documented entries are one signature
        /// each; a question counts each of its judgements, so a new mechanism next to an old one in the
        /// same cell is a signature of its own.
        /// </summary>
        private static List<SignatureStat> SignatureStats(ReportModel model, List<CaseLine> lines) {
            var map = new Dictionary<string, SignatureStat>();
            var order = new List<string>();
            void Add(string sig, string kind, string known, string id, int hits) {
                if (sig == null) {
                    return;
                }
                if (!map.TryGetValue(sig, out var s)) {
                    map[sig] = s = new SignatureStat { Signature = sig, Kind = kind, Known = known };
                    order.Add(sig);
                }
                s.Hits += hits;
                if (id != null && !s.Ids.Contains(id)) {
                    s.Ids.Add(id);
                }
                s.Known ??= known;
            }
            foreach (var f in model.Findings.Concat(model.Diagnostics.Where(d => d.Kind == "defect"))) {
                Add(f.Signature, "defect", f.Known, f.Id, Math.Max(1, f.Hits));
            }
            var byCase = new Dictionary<string, Finding>();
            foreach (var q in model.Questions) {
                foreach (var k in q.CaseKeys) {
                    byCase[k] = q;
                }
            }
            foreach (var line in lines) {
                var r = line.Result;
                if (r.Verdict != Verdict.Question) {
                    continue;
                }
                byCase.TryGetValue(r.Key, out var entry);
                foreach (var j in r.Judgements.Where(x => x.Verdict == Verdict.Question)) {
                    Add(Signatures.Of(r, j), "question", entry?.Known, entry?.Id, 1);
                }
            }
            foreach (var q in model.Questions.Where(x => x.CaseKeys.Count == 0)) {
                Add(q.Signature, "question", q.Known, q.Id, Math.Max(1, q.Hits));
            }
            foreach (var d in model.Documented) {
                Add(d.Signature, "documented", d.Known, d.Id, Math.Max(1, d.Hits));
            }
            return order.Select(s => map[s]).ToList();
        }

        private static string Unique(string id, List<Finding> existing) {
            string candidate = id;
            int n = 2;
            while (existing.Any(f => f.Id == candidate)) {
                candidate = id + "#" + n++;
            }
            return candidate;
        }

        private static int SevRank(string s) => s == "high" ? 0 : s == "medium" ? 1 : s == "low" ? 2 : 3;

        private static string Trim(string s, int n) => s == null ? null : s.Length > n ? s.Substring(0, n - 1) + "…" : s;

        // =====================================================================
        // Baseline: cells, defects, signatures, regressions
        // =====================================================================

        private static void CompareWithBaseline(Options o, ReportModel model, MatrixFile matrix, List<CaseLine> lines) {
            var basePath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(o.Out).TrimEnd('/', '\\')), o.Baseline + "-" + o.Cfg, "matrix.json");
            if (!File.Exists(basePath)) {
                model.BaselineNote = "No " + Report.DisplayPath(basePath) + ": run the module on " + o.Baseline + " in the same configuration (" + o.Cfg + ") first.";
                return;
            }
            var before = Json.ReadFile<MatrixFile>(basePath);
            model.BaselineChanges = Report.Compare(before, matrix);
            ReportModel old = null;
            var baseModel = Path.Combine(Path.GetDirectoryName(basePath), "model.json");
            if (File.Exists(baseModel)) {
                try {
                    old = Json.ReadFile<ReportModel>(baseModel);
                } catch (Exception) {
                    old = null;
                }
            }
            if (old != null) {
                // Defects too: the diagnostics (Obsolete texts, growth owners, the leak) have no matrix cell.
                model.BaselineFindings = Report.CompareFindings(old, model);
                model.BaselineSignatures = Report.CompareSignatures(old.Signatures, model.Signatures);
            } else {
                model.BaselineSignatures = new SignatureDiff { Note = "no model.json next to the baseline matrix" };
            }

            // Regressions: a question now where the baseline cell was fine (or a question of another kind),
            // grouped by the question entry the cell belongs to.
            var cells = before.Cells.GroupBy(c => c.Key).ToDictionary(g => g.Key, g => g.First());
            var regressed = new Dictionary<string, List<(CaseResult R, MatrixCell Before)>>();
            var lostWording = new Dictionary<string, int>();
            foreach (var line in lines) {
                var r = line.Result;
                if (r.Verdict != Verdict.Question || !cells.TryGetValue(r.Key, out var b)) {
                    continue;
                }
                var nowRules = r.Judgements.Where(j => j.Verdict == Verdict.Question).Select(j => j.Rule + (j.ClaimId != null ? ":" + j.ClaimId : "")).Distinct().ToList();
                var beforeRules = new HashSet<string>((b.Rules ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries));
                bool wasFine = b.Verdict == nameof(Verdict.Ok) || b.Verdict == nameof(Verdict.Doc) || b.Verdict == nameof(Verdict.Undefined);
                bool newKind = b.Verdict == nameof(Verdict.Question) && nowRules.Any(x => !beforeRules.Contains(x));
                if (!wasFine && !newKind) {
                    continue;
                }
                // The same outcome as on the baseline (class, message, throw site) and a claim of the cell whose wording these
                // docs lack: the verdict moved because the stand lost the promise (a claim warning says so), not because the
                // package changed.
                bool sameOutcome = r.Outcome != null && b.Cls == r.Outcome.Class && b.Outcome == (r.Outcome.Threw ? r.Outcome.ExShort : "ok")
                    && b.Message == r.Outcome.Message && (b.Site == null || b.Site == r.Outcome.Site);
                if (sameOutcome && r.MissingClaims.Count > 0) {
                    string k = string.Join(", ", r.MissingClaims);
                    lostWording.TryGetValue(k, out int n);
                    lostWording[k] = n + 1;
                    continue;
                }
                var entry = model.Questions.FirstOrDefault(q => q.CaseKeys.Contains(r.Key));
                string id = entry?.Id ?? r.Key;
                if (!regressed.TryGetValue(id, out var list)) {
                    regressed[id] = list = new List<(CaseResult, MatrixCell)>();
                }
                list.Add((r, b));
            }
            foreach (var kv in lostWording) {
                model.ClaimWarnings.Add("not counted as regressions: " + kv.Value + " cell(s) answer as on " + o.Baseline + " (same outcome, message and throw site), "
                    + "but the wording of " + kv.Key + " is missing in these docs, so their verdict changed: update the claim");
            }
            foreach (var kv in regressed) {
                var entry = model.Questions.FirstOrDefault(q => q.Id == kv.Key);
                var (r, b) = kv.Value[0];
                var f = new Finding {
                    Id = "regression:" + kv.Key, Kind = "regression", Known = entry?.Known, Class = entry?.Class ?? "regression", Rule = entry?.Rule, Severity = entry?.Severity ?? "low",
                    Member = r.Member, Scenario = r.Scenario, Variant = r.Variant, Signature = entry?.Signature, Hits = kv.Value.Count,
                    Expected = o.Baseline + ": " + Report.Cell(b), Outcome = o.Flavor + ": " + CellSignature(r),
                    Detail = entry?.Detail, DocRef = entry?.DocRef, Repro = entry?.Repro, Seed = o.Seed, Params = entry?.Params,
                    Title = "regression against " + o.Baseline + ": " + (entry?.Title ?? r.Member)
                };
                f.CaseKeys.AddRange(kv.Value.Select(x => x.R.Key));
                model.Regressions.Add(f);
            }
            // Questions outside the matrix (growth owners, FirstGrowthAfterReserve, spans) that the baseline did not have.
            if (old != null) {
                string Bare(string id) => id.StartsWith("q:", StringComparison.Ordinal) ? id.Substring(2) : id;
                var oldIds = new HashSet<string>(old.Questions.Concat(old.Findings).Concat(old.Diagnostics).Select(x => Bare(x.Id)));
                foreach (var q in model.Questions.Where(x => x.CaseKeys.Count == 0 && !oldIds.Contains(Bare(x.Id)))) {
                    model.Regressions.Add(new Finding {
                        Id = "regression:" + q.Id, Kind = "regression", Known = q.Known, Class = q.Class, Rule = q.Rule, Severity = q.Severity, Member = q.Member, Scenario = q.Scenario,
                        Variant = q.Variant, Signature = q.Signature, Hits = 1, Expected = o.Baseline + ": no such question", Outcome = o.Flavor + ": " + q.Outcome,
                        Detail = q.Detail, DocRef = q.DocRef, Repro = q.Repro, Seed = o.Seed, Params = q.Params, Title = "regression against " + o.Baseline + ": " + q.Title
                    });
                }
            }
        }

        // =====================================================================
        // Diagnostics and the promises outside the matrix
        // =====================================================================

        private sealed class DiagClaim {
            public string Id;
            public string Since;
            public string Quote;
            public string[] Phrases;
        }

        private static readonly Dictionary<string, DiagClaim> DiagClaims = new[] {
            new DiagClaim { Id = "growth/pool-owner", Since = "2.2.1", Quote = "StorageGrowth.Owner: what owns the array: the component type name for a pool", Phrases = new[] { "the component type name for a pool" } },
            new DiagClaim { Id = "growth/filter-owner", Since = "2.2.1", Quote = "a filter is named by its constraints, as in Filter Inc<Position, Velocity> filter dense 512 -> 1024", Phrases = new[] { "a filter is named by its constraints", "the filter's description" } },
            new DiagClaim { Id = "first-growth/later-growths-keep", Since = "2.2.1", Quote = "FirstGrowthAfterReserve: Later growths do not replace it", Phrases = new[] { "Later growths do not replace it" } },
            new DiagClaim { Id = "first-growth/clear-keeps", Since = "2.2.1", Quote = "FirstGrowthAfterReserve: Clear resets neither it nor the count", Phrases = new[] { "resets neither it nor the count" } },
            new DiagClaim { Id = "first-growth/release-empty", Since = "2.2.1", Quote = "FirstGrowthAfterReserve: Empty while nothing outgrew the reserve, and always unless KenseiECS is compiled with KENSEI_DEBUG", Phrases = new[] { "always unless KenseiECS is compiled with KENSEI_DEBUG", "always without KENSEI_DEBUG", "FirstGrowthAfterReserve is always empty" } },
            new DiagClaim { Id = "span/entities-valid", Since = "2.1.1", Quote = "Filter.Entities: Matching entity indices as a span. Valid until the next structural change", Phrases = new[] { "Valid until the next structural change" } },
            new DiagClaim { Id = "span/structural-changes", Since = "2.1.1", Quote = "structural change (Add, Remove, CreateEntity, DestroyEntity, Clear)", Phrases = new[] { "structural change (Add, Remove, CreateEntity, DestroyEntity, Clear)" } },
            new DiagClaim { Id = "span/growth-poisons", Since = "2.1.1", Quote = "GrowDense ... then fills the old array entirely with FreeSlot", Phrases = new[] { "array entirely with FreeSlot" } },
            new DiagClaim { Id = "span/entities-match", Since = "2.1.1", Quote = "Filter.Entities: Matching entity indices as a span", Phrases = new[] { "Matching entity indices as a span" } },
            new DiagClaim { Id = "span/reserve-during-iteration", Since = "2.2.0", Quote = "Filter.Reserve: Safe during iteration: a running enumerator switches to the new array as after any other growth", Phrases = new[] { "Safe during iteration" } },
            // 2.3.0 rewrote the XML of Filter.Reserve: "its dense entity array gets room for exactly count ... never shrinks".
            new DiagClaim { Id = "span/reserve-capacity", Since = "2.2.0", Quote = "Filter.Reserve: Grow the dense entity array so that count matching entities fit without further growth. Never shrinks. (2.3.0: its dense entity array gets room for exactly count ... Raises ReservedCount; never shrinks.)", Phrases = new[] { "matching entities fit without further growth", "its dense entity array gets room for exactly count" } },
            // 2.2.2 settles Q-SPAN-RESERVE in the XML doc of Filter.Entities: a growing Reserve ends the validity of a span.
            new DiagClaim { Id = "span/reserve-ends-validity", Since = "2.2.2", Quote = "growth moves the entities to a new array and fills the old one with -1, so a span taken before it reads -1 from then on", Phrases = new[] { "growth moves the entities to a new array and fills the old one with -1, so a span taken before it reads -1 from then on" } },
            // 2.3.0: the reserve a filter takes from its component types (Filter.ReservedCount, world-config.md).
            new DiagClaim { Id = "reserve/inc-smallest", Since = "2.3.0", Quote = "Filter.ReservedCount: A filter whose Inc types are all reserved (ComponentPoolBase.Reserve) takes the smallest of their reserves, since a matching entity has every one of them", Phrases = new[] { "takes the smallest of their reserves", "never holds more entities than the smallest of their reserves" } },
            new DiagClaim { Id = "reserve/any-sum", Since = "2.3.0", Quote = "Filter.ReservedCount: when its Any types are all reserved too, at most the sum of theirs. A filter without Inc types takes the sum of its Any types' reserves when they are all reserved", Phrases = new[] { "takes the sum of its Any types' reserves", "a filter without Inc types is reserved by its Any types alone" } },
            new DiagClaim { Id = "reserve/cap-reserved-entities", Since = "2.3.0", Quote = "Filter.ReservedCount: Once the world reserved entity slots, the reserve taken from the types is at most World.ReservedEntities: no filter holds more entities than are alive", Phrases = new[] { "no filter holds more entities than are alive" } },
            new DiagClaim { Id = "reserve/any-order", Since = "2.3.0", Quote = "Filter.ReservedCount: This holds for a filter created after its types were reserved as well. The reserve taken from the types follows their reserves as they are now, whatever the order of the calls", Phrases = new[] { "whatever the order of the calls", "a filter's reserve from its types follows the reserves as they are now" } },
            new DiagClaim { Id = "reserve/obsolete-moves-cap", Since = "2.3.0", Quote = "CHANGELOG 2.3.0: The reserve follows the reserves of the types as they are now, whatever the order of the calls (a later ReserveEntities can lower it; arrays never shrink), and the obsolete World.Reserve moves its cap too", Phrases = new[] { "the obsolete World.Reserve moves its cap too" } },
            // 2.3.0: World.UnsubscribeLater (WorldListenerExtensions XML, SystemsRunner.Run XML, listener-bridge.md).
            new DiagClaim { Id = "unsub-later/outermost-run", Since = "2.3.0", Quote = "The removal is queued and applied when the outermost SystemsRunner.Run of the world returns, after its systems and its OneFrame cleanup, also when a system threw; until then the listener stays in the list", Phrases = new[] { "applied when the outermost SystemsRunner.Run of the world returns", "applied when the outermost Run of this world returns" } },
            new DiagClaim { Id = "unsub-later/nested-runner-defers", Since = "2.3.0", Quote = "SystemsRunner.Run(World): The outermost Run on the world (not one called from inside another runner's Run) then applies the listener removals queued by UnsubscribeLater", Phrases = new[] { "not one called from inside another runner's Run" } },
            new DiagClaim { Id = "unsub-later/phase-applies", Since = "2.3.0", Quote = "In EcsBootstrap the update runner and its fixed and late phases each run on their own, so each applies the removals queued during it", Phrases = new[] { "each applies the removals queued during it" } },
            new DiagClaim { Id = "unsub-later/outside-run-next-tick", Since = "2.3.0", Quote = "Called while no runner runs, the removal is applied at the next World.NextTick", Phrases = new[] { "the removal is applied at the next World.NextTick", "the removal is applied at the next NextTick" } },
            new DiagClaim { Id = "unsub-later/destroyed-entity-skipped", Since = "2.3.0", Quote = "A removal whose entity was destroyed by then is skipped, even if a new entity took the slot", Phrases = new[] { "A removal whose entity was destroyed by then is skipped" } },
            new DiagClaim { Id = "unsub-later/clear-drops", Since = "2.3.0", Quote = "World.Clear drops the queue", Phrases = new[] { "World.Clear drops the queue", "Clear drops the queued removals" } },
            new DiagClaim { Id = "unsub-later/destroyed-world-noop", Since = "2.3.0", Quote = "on a destroyed world the call does nothing", Phrases = new[] { "on a destroyed world the call does nothing" } },
            new DiagClaim { Id = "unsub-later/dying-entity", Since = "2.3.0", Quote = "A listener may call it while its entity is being destroyed", Phrases = new[] { "A listener may call it while its entity is being destroyed", "A listener may call it while its entity's own DestroyEntity runs" } },
            new DiagClaim { Id = "leak/destroy-nulls", Since = "2.1.1", Quote = "World.Destroy: Fully destroy the world. Nulls all internal references so GC can collect.", Phrases = new[] { "Nulls all internal references so GC can collect" } },
        }.ToDictionary(c => c.Id);

        private static ClaimActivation Act(Policy policy, string id) {
            var c = DiagClaims[id];
            return policy.Activate(id, c.Since, c.Phrases);
        }

        private static void AddDiagnostics(Options o, ReportModel model, DiagResult diag, Policy policy, bool debug) {
            string Params(string what) => "flavor=" + o.Flavor + ", cfg=" + o.Cfg + ", mode=" + o.Mode + ", seed=" + o.Seed + ", check " + what;
            var used = new Dictionary<string, (int Cases, int Violations)>();
            void Count(string claim, bool violated) {
                used.TryGetValue(claim, out var c);
                used[claim] = (c.Cases + 1, c.Violations + (violated ? 1 : 0));
            }
            // A violated promise: a defect where these docs make it, a question where they do not. Decided
            // before the repro is written, since the repro carries the final id.
            string Decide(Finding f, ClaimActivation act, string quote, string since) {
                f.ClaimState = act.State;
                f.Since = since;
                if (act.Holds) {
                    f.Kind = "defect";
                    f.DocRef = act.Where + ": " + quote;
                    return "repro/";
                }
                f.Kind = "question";
                f.DocRef = (act.State == "disputed" ? "disputed: " : "not in these docs: ") + quote + " (" + act.Where + ")";
                f.Detail = (f.Detail != null ? f.Detail + "; " : "") + "a question, not a defect: " + act.Where;
                f.Id = "q:" + f.Id;
                return "repro/questions/";
            }
            void Add(Finding f) {
                (f.Kind == "defect" ? model.Diagnostics : model.Questions).Add(f);
            }

            // [Obsolete] text against what the overload does: the text is documentation of this version itself. When the
            // overload's own XML doc describes the null case ("With a null name it adds an unnamed inline group"), the call
            // does what its documentation says and only the [Obsolete] message generalizes: documented, not a defect.
            foreach (var ob in diag.Obsolete.Where(x => x.Mismatch)) {
                var call = ob.NullCalls.First(c => c.Param == "name");
                var xml = policy.Activate("xml/obsolete-null-name", "2.2.0", "With a null name it adds an unnamed inline group");
                if (xml.Holds) {
                    model.Documented.Add(new Finding {
                        Id = "obsolete-text:" + Report.Slug(ob.Member), Kind = "documented", Class = "diagnostics", Rule = "obsolete-text-generalizes", Severity = "info",
                        Member = ob.Member, Scenario = "null-arg", Variant = "name=null", ClaimState = xml.State, Since = "2.2.0",
                        Title = "The [Obsolete] text of " + ob.Member + " speaks of a separate phase; with name = null the overload adds an unnamed inline group, as its XML doc says",
                        Detail = ob.Heuristic + "; the behaviour is the one the overload's XML doc describes, only the [Obsolete] message generalizes",
                        Outcome = call.Behavior, Expected = "the behaviour the XML doc of the overload describes",
                        DocRef = xml.Where + ": \"With a null name it adds an unnamed inline group, like Add(runner).\" ([Obsolete(\"" + ob.Message + "\")])",
                        Quote = "With a null name it adds an unnamed inline group, like Add(runner).",
                        Signature = "obsolete-text|" + ob.Member, Hits = 1, Seed = o.Seed, Params = Params("obsolete " + ob.Member)
                    });
                    continue;
                }
                var f = new Finding {
                    Id = "obsolete-text:" + Report.Slug(ob.Member), Kind = "defect", Class = "diagnostics", Rule = "obsolete-phase-heuristic", Severity = "low",
                    Member = ob.Member, Scenario = "null-arg", Variant = "name=null",
                    Title = "The [Obsolete] text of " + ob.Member + " promises a phase, but with name = null no phase is registered",
                    Detail = ob.Heuristic, Outcome = call.Behavior, Expected = "the message matches what the overload does with name = null",
                    DocRef = "[Obsolete(\"" + ob.Message + "\")]" + (xml.Holds
                        ? "; the XML docs of the same overload (" + xml.Where + "): \"With a null name it adds an unnamed inline group, like Add(runner).\"" : ""),
                    Signature = "obsolete-text|" + ob.Member, Hits = 1, Seed = o.Seed, Params = Params("obsolete " + ob.Member)
                };
                f.Repro = "repro/" + ReproWriter.FileName(f.Id);
                // The call exactly as the diagnostics made it, on a fresh uninitialized runner, and what it registered.
                string expected = "call=ok:" + (call.Outcome == "ok" ? "SystemsRunner" : "?") + " | IsSeparatePhase=ok:" + (call.RegisteredPhase ? "True" : "False")
                    + " | IsRunnable=ok:" + (call.RegisteredRunnable ? "True" : "False");
                File.WriteAllText(Path.Combine(o.Out, f.Repro), CustomRepro(f, o, expected, new[] {
                    "var world = new World();",
                    "var shared = new SharedData();",
                    "var r = new SystemsRunner(world, shared);"
                }, "return " + call.CallCode + ";", new[] {
                    ("IsSeparatePhase", "r.GetSystemInfo(r.SystemCount - 1).IsSeparatePhase"),
                    ("IsRunnable", "r.GetSystemInfo(r.SystemCount - 1).IsRunnable")
                }));
                model.Diagnostics.Add(f);
            }

            // Owners of growth events: distinct where the docs of this version promise a name per storage.
            foreach (var g in diag.Growth.Where(x => x.Available && x.Events.Count > 0 && x.Claim != null)) {
                var dc = DiagClaims[g.Claim];
                var act = Act(policy, g.Claim);
                Count(g.Claim, !g.Distinct);
                if (g.Distinct) {
                    continue;
                }
                string source = g.Source.StartsWith("EcsProfiler", StringComparison.Ordinal) ? "profiler" : "first-growth";
                var f = new Finding {
                    Id = "growth-owner:" + g.Storage.Replace(' ', '-') + ":" + source, Class = "diagnostics", Rule = "growth-owner-distinct", Severity = "medium",
                    Member = g.Source, Scenario = "KENSEI_DEBUG, Reserve", Variant = g.Storage,
                    Title = g.Source + ": " + g.Instances + " different \"" + g.Storage + "\" storages grew, but their owners are the same: " + string.Join(", ", g.Owners.Select(x => "\"" + x + "\"")),
                    Detail = "events: " + string.Join("; ", g.Events.Take(4)),
                    Outcome = "owners: " + string.Join(", ", g.Owners), Expected = "every storage has its own owner (a pool: its component type; a filter: its constraints)",
                    Signature = "growth-owner|" + g.Source + "|" + g.Storage, Hits = g.Instances, Seed = o.Seed, Params = Params("growth " + g.Storage + " via " + g.Source)
                };
                string folder = Decide(f, act, dc.Quote, dc.Since);
                if (g.SetupCode != null) {
                    f.Repro = folder + ReproWriter.FileName(f.Id);
                    string expected = source == "profiler" ? "owners=" + string.Join(",", g.Owners.OrderBy(x => x, StringComparer.Ordinal)) : "owners=" + string.Join(",", g.Owners);
                    File.WriteAllText(Path.Combine(o.Out, f.Repro), GrowthRepro(f, o, expected, g, source));
                }
                Add(f);
            }
            // G4: no FirstGrowthAfterReserve at all (a wish for the API, a question; only KENSEI_DEBUG tracks growth).
            var api = diag.FirstGrowth.FirstOrDefault(p => p.Id == "first-growth/api");
            if (api != null && debug) {
                model.Questions.Add(new Finding {
                    Id = "q:first-growth:api", Kind = "question", Class = "diagnostics", Rule = "api-missing", Severity = "low", Member = "World.FirstGrowthAfterReserve",
                    Scenario = "KENSEI_DEBUG, Reserve", Title = "No cheap signal of the first growth after Reserve: " + api.Note, Detail = api.Note,
                    Outcome = "not in this version", Signature = "api-missing|World.FirstGrowthAfterReserve", Hits = 1, Seed = o.Seed, Params = Params("first-growth api")
                });
            }
            // The FirstGrowthAfterReserve contract.
            foreach (var p in diag.FirstGrowth.Where(x => x.Status == "violated" || x.Status == "ok")) {
                var dc = DiagClaims[p.Id];
                var act = Act(policy, p.Id);
                Count(p.Id, p.Status == "violated");
                if (p.Status == "ok") {
                    continue;
                }
                var f = ProbeFinding(o, p, "first-growth:" + p.Id.Substring("first-growth/".Length), "World.FirstGrowthAfterReserve", "first-growth-contract",
                    "World.FirstGrowthAfterReserve breaks its documented contract: " + p.Expected, Params("first-growth " + p.Id));
                WriteProbeRepro(o, f, p, Decide(f, act, dc.Quote, dc.Since));
                Add(f);
            }
            foreach (var p in diag.FirstGrowth.Where(x => x.Status == "inconclusive")) {
                model.Diagnostics.Add(new Finding { Id = "diag:" + p.Id, Kind = "diagnostic", Class = "stand", Rule = "inconclusive", Severity = "info", Title = p.Id + ": " + p.Note, Detail = p.Observed });
            }

            // Filter spans and the enumerator around Reserve.
            foreach (var p in diag.Spans.Where(x => x.Status == "violated" || x.Status == "ok")) {
                ClaimActivation act;
                string claimId, quote, since;
                if (p.Id == "span/old-span-after-reserve" || p.Id == "span/old-span-after-world-reserve") {
                    // 2.2.2: the XML doc of Filter.Entities says it: a Reserve that grows the filter ends the span's
                    // validity, the old span reads -1. An old span that reads -1 is documented, with that sentence.
                    var ends = Act(policy, "span/reserve-ends-validity");
                    if (ends.Holds && p.Status == "violated" && OldSpanPoisoned(p.Observed)) {
                        Count("span/reserve-ends-validity", false);
                        var dc = DiagClaims["span/reserve-ends-validity"];
                        string op = p.Id.Contains("world-reserve") ? "World.Reserve" : "Filter.Reserve";
                        model.Documented.Add(new Finding {
                            Id = p.Id.Replace('/', ':'), Kind = "documented", Class = "contract", Rule = "span-contract", Severity = "info",
                            Member = p.Id.Contains("world-reserve") ? "World.Reserve(int, int)" : "Filter.Reserve(int)", Scenario = p.Area, Variant = p.Id,
                            Title = "A span from Filter.Entities taken before " + op + " reads -1 after the filter grew, as the XML doc of Filter.Entities says",
                            Detail = p.Note, Outcome = p.Observed, Expected = "the documented end of the span's validity: the old array is filled with -1",
                            DocRef = ends.Where + ": \"" + dc.Quote + "\"", Quote = dc.Quote, ClaimState = ends.State, Since = dc.Since,
                            Signature = "span-contract|" + p.Id, Hits = 1, Seed = o.Seed, Params = Params(p.Id)
                        });
                        continue;
                    }
                    // Valid until the next structural change, and Reserve is none; but the docs also say that
                    // growth poisons the retired array. When they say both, they disagree: a question.
                    var valid = Act(policy, "span/entities-valid");
                    var structural = Act(policy, "span/structural-changes");
                    var poisons = Act(policy, "span/growth-poisons");
                    claimId = "span/entities-valid";
                    quote = DiagClaims[claimId].Quote + "; " + DiagClaims["span/structural-changes"].Quote;
                    since = DiagClaims[claimId].Since;
                    if (valid.Holds && structural.Holds && poisons.Holds) {
                        act = new ClaimActivation { State = "disputed", Where = "the docs disagree: " + valid.Where + " keeps the span valid until a structural change, " + structural.Where
                            + " does not count Reserve as one, but " + poisons.Where + " says a growth fills the old array with FreeSlot" };
                    } else if (valid.Holds && structural.Holds) {
                        act = new ClaimActivation { State = "active", Where = valid.Where + " + " + structural.Where };
                    } else {
                        act = valid.Holds ? structural : valid;
                    }
                } else if (p.Id == "span/entities-after-reserve") {
                    claimId = "span/entities-match";
                    act = Act(policy, claimId);
                    quote = DiagClaims[claimId].Quote;
                    since = DiagClaims[claimId].Since;
                } else if (p.Id == "span/enumerator-during-reserve") {
                    claimId = "span/reserve-during-iteration";
                    act = Act(policy, claimId);
                    quote = DiagClaims[claimId].Quote;
                    since = DiagClaims[claimId].Since;
                } else {
                    claimId = "span/reserve-capacity";
                    act = Act(policy, claimId);
                    quote = DiagClaims[claimId].Quote;
                    since = DiagClaims[claimId].Since;
                }
                Count(claimId, p.Status == "violated");
                if (p.Status == "ok") {
                    continue;
                }
                var f = ProbeFinding(o, p, p.Id.Replace('/', ':'), p.Id.Contains("world-reserve") ? "World.Reserve(int, int)" : "Filter.Reserve(int)", "span-contract",
                    "Around " + (p.Id.Contains("world-reserve") ? "World.Reserve" : "Filter.Reserve") + " the promise does not hold: " + p.Expected, Params(p.Id));
                WriteProbeRepro(o, f, p, Decide(f, act, quote, since));
                Add(f);
            }

            // 2.3.0: Filter.ReservedCount from the types, World.UnsubscribeLater (Diagnostics230).
            foreach (var p in (diag.V230 ?? new List<ContractProbe>()).Where(x => x.Status == "violated" || x.Status == "ok")) {
                var dc = DiagClaims[p.Id];
                var act = Act(policy, p.Id);
                Count(p.Id, p.Status == "violated");
                if (p.Status == "ok") {
                    continue;
                }
                bool reserve = p.Area == "reserve-from-types";
                var f = ProbeFinding(o, p, p.Id.Replace('/', ':'), reserve ? "Filter.ReservedCount" : "World.UnsubscribeLater<T>(Entity, T)", reserve ? "reserve-from-types" : "unsubscribe-later",
                    (reserve ? "Filter.ReservedCount breaks its documented contract: " : "World.UnsubscribeLater breaks its documented contract: ") + p.Expected, Params(p.Id));
                WriteProbeRepro(o, f, p, Decide(f, act, dc.Quote, dc.Since));
                Add(f);
            }
            foreach (var p in (diag.V230 ?? new List<ContractProbe>()).Where(x => x.Status == "inconclusive")) {
                model.Diagnostics.Add(new Finding { Id = "diag:" + p.Id, Kind = "diagnostic", Class = "stand", Rule = "inconclusive", Severity = "info", Title = p.Id + ": " + p.Note, Detail = p.Observed });
            }

            // References kept after World.Destroy.
            if (diag.Leak != null && diag.Leak.Status == "leak") {
                var dc = DiagClaims["leak/destroy-nulls"];
                var act = Act(policy, "leak/destroy-nulls");
                Count("leak/destroy-nulls", true);
                var f = new Finding {
                    Id = "leak:World.Destroy:groups", Class = "leak", Rule = "contract", Severity = "medium",
                    Member = "World.Destroy()", Scenario = "world-destroyed",
                    Title = "World.Destroy does not let its groups go: while the world is referenced, a group and its pools (with data) are not collected",
                    Detail = diag.Leak.Note, Outcome = "the WeakReference to the group is alive after Destroy and a full GC; GroupCount = " + diag.Leak.GroupCountAfterDestroy + ", FilterCount = " + diag.Leak.FilterCountAfterDestroy,
                    Expected = "Destroy nulls every internal reference, as it does for filters and pools",
                    Signature = "leak|World.Destroy|groups", Hits = 1, Seed = o.Seed, Params = Params("weak-reference leak probe")
                };
                f.Repro = Decide(f, act, dc.Quote, dc.Since) + ReproWriter.FileName(f.Id);
                string expected = "groupAlive=True | GroupCount=" + diag.Leak.GroupCountAfterDestroy;
                File.WriteAllText(Path.Combine(o.Out, f.Repro), LeakRepro(f, o, expected));
                Add(f);
            } else if (diag.Leak != null) {
                Count("leak/destroy-nulls", false);
            }

            foreach (var dc in DiagClaims.Values) {
                var act = Act(policy, dc.Id);
                used.TryGetValue(dc.Id, out var c);
                model.Claims.Add(new ClaimStat { Id = dc.Id, Kind = "Diagnostic", Quote = dc.Quote, Since = dc.Since, State = act.State, ActiveAt = act.Where, Cases = c.Cases, Violations = c.Violations });
            }
        }

        // "... | oldSpanAfterReserve=[-1,-1,-1] | ..." of a span probe: the old span reads only -1.
        private static bool OldSpanPoisoned(string observed) {
            var m = System.Text.RegularExpressions.Regex.Match(observed ?? "", @"oldSpanAfterReserve=\[([^\]]*)\]");
            if (!m.Success || m.Groups[1].Value.Length == 0) {
                return false;
            }
            return m.Groups[1].Value.Split(',').All(x => x.Trim() == "-1");
        }

        private static Finding ProbeFinding(Options o, ContractProbe p, string id, string member, string rule, string title, string parameters) {
            return new Finding {
                Id = id, Class = "contract-violation", Rule = rule, Severity = "medium", Member = member, Scenario = p.Area, Variant = p.Id,
                Title = title, Detail = p.Note, Outcome = p.Observed, Expected = p.Expected, Signature = rule + "|" + p.Id, Hits = 1, Seed = o.Seed, Params = parameters
            };
        }

        private static void WriteProbeRepro(Options o, Finding f, ContractProbe p, string folder) {
            if (p.Code != null) {
                f.Repro = folder + ReproWriter.FileName(f.Id);
                File.WriteAllText(Path.Combine(o.Out, f.Repro), ProbeRepro(f, o, p));
            }
        }

        // =====================================================================
        // Repros of the diagnostics
        // =====================================================================

        private static string Header(Finding f, Options o) {
            return "// " + f.Id + "\n// " + f.Title.Replace("\n", " ") + "\n// Stand/contracts, " + o.Flavor + "-" + o.Cfg + ", mode " + o.Mode + ", seed " + o.Seed + "\n// Class: " + f.Class + ", rule " + f.Rule + ", severity " + f.Severity + "\n"
                + "#pragma warning disable CS0618, CS0168, CS0219, CS8321\nusing System;\nusing System.Collections.Generic;\nusing System.IO;\nusing System.Runtime.CompilerServices;\nusing KenseiECS;\nusing Stand.Contracts.Probe;\n\n"
                + "namespace Stand.Contracts.Repros {\n    public static class " + ReproWriter.ClassName(f.Id) + " {\n        public const string Id = " + ReproWriter.Lit(f.Id) + ";\n";
        }

        private static string Body(string code, string indent) => string.Concat(code.Split('\n').Where(l => l.Length > 0).Select(l => indent + l + "\n"));

        private static string CustomRepro(Finding f, Options o, string expected, string[] setup, string callBody, (string Name, string Code)[] probes) {
            var s = Header(f, o) + "        public const string Expected = " + ReproWriter.Lit(expected) + ";\n\n        public static string Run() {\n";
            foreach (var line in setup) {
                s += "            " + line + "\n";
            }
            s += "            var log = new List<string>();\n";
            s += "            log.Add(\"call=\" + Fmt.Try(() => { " + callBody + " }));\n";
            foreach (var (name, code) in probes) {
                s += "            log.Add(" + ReproWriter.Lit(name + "=") + " + Fmt.Try(() => " + code + "));\n";
            }
            s += "            return string.Join(\" | \", log);\n        }\n    }\n}\n";
            return s;
        }

        private static string GrowthRepro(Finding f, Options o, string expected, GrowthCheck g, string source) {
            var s = Header(f, o) + "        public const string Expected = " + ReproWriter.Lit(expected) + ";\n\n        public static string Run() {\n";
            s += Body(g.SetupCode, "            ");
            if (source == "profiler") {
                s += "            var owners = new SortedSet<string>(StringComparer.Ordinal);\n";
                s += "            foreach (var ev in EcsProfiler.GetEventsByType(ProfileEventType.StorageGrown)) {\n";
                s += "                int k = ev.ComponentType.IndexOf(" + ReproWriter.Lit(" " + g.Storage + " ") + ", StringComparison.Ordinal);\n";
                s += "                if (k >= 0) { owners.Add(ev.ComponentType.Substring(0, k)); }\n";
                s += "            }\n";
                s += "            EcsProfiler.Disable();\n";
            }
            s += "            return \"owners=\" + string.Join(\",\", owners);\n        }\n    }\n}\n";
            return s;
        }

        private static string ProbeRepro(Finding f, Options o, ContractProbe p) {
            return Header(f, o) + "        public const string Expected = " + ReproWriter.Lit(p.Observed) + ";\n\n"
                + "        // The promise: " + (p.Expected ?? "").Replace("\n", " ") + "\n"
                + "        public static string Run() {\n            try {\n" + Body(p.Code, "                ") + "            } catch (Exception e) {\n                return \"ex:\" + e.GetType().Name;\n            }\n        }\n\n"
                + "        private static string Sorted(ReadOnlySpan<int> span) {\n            var a = span.ToArray();\n            Array.Sort(a);\n            return \"[\" + string.Join(\",\", a) + \"]\";\n        }\n\n"
                + "        private static string Sorted(List<int> list) {\n            var a = list.ToArray();\n            Array.Sort(a);\n            return \"[\" + string.Join(\",\", a) + \"]\";\n        }\n    }\n}\n";
        }

        private static string LeakRepro(Finding f, Options o, string expected) {
            return Header(f, o) + "        public const string Expected = " + ReproWriter.Lit(expected) + ";\n\n" +
@"        [MethodImpl(MethodImplOptions.NoInlining)]
        private static (World, WeakReference) Make() {
            var w = new World();
            var e = w.CreateEntity(new CC { V = 1 });
            w.Add(e, new CD { V = 2 });
            var g = w.Group<CC, CD>();
            w.Destroy();
            return (w, new WeakReference(g));
        }

        public static string Run() {
            var (w, weak) = Make();
            for (int i = 0; i < 3; i++) {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
            }
            string result = ""groupAlive="" + weak.IsAlive + "" | GroupCount="" + w.GroupCount;
            GC.KeepAlive(w);
            return result;
        }
    }
}
";
        }

        // An operation named in a message: Type.Method, Method<T> or Method(.
        private static readonly Regex OperationToken = new Regex(@"(?:\b(?:World|SystemsRunner|Filter|CommandBuffer|WorldSerializer|SharedData|ComponentPool|Group|Pool)\.(\w+))|(?:\b(\w+)(?:<\w|\())", RegexOptions.Compiled);

        private static ExceptionText ClassifyText(CaseResult r, HashSet<string> vocabulary, List<string> parameters) {
            var o = r.Outcome;
            var t = new ExceptionText { Member = r.Member, Scenario = r.Scenario, Variant = r.Variant, Exception = o.ExShort, Message = o.Message, Thrower = o.TopFrame };
            string cls = o.Class;
            bool packageThrown = o.TopFrame != null && o.TopFrame.StartsWith("KenseiECS.", StringComparison.Ordinal);
            bool deliberate = packageThrown && (cls == "IOE" || cls == "ODE" || cls == "Arg" || cls == "ArgRange" || cls == "ArgNull" || cls == "KeyNotFound"
                || cls == "InvalidData" || cls == "EndOfStream" || cls == "Aggregate");
            if (!deliberate) {
                t.Verdict = "raw-runtime";
                t.Note = "thrown by " + (o.TopFrame ?? "?");
                return t;
            }
            var names = new List<string> { r.Name };
            if (r.Name == "End") {
                names.Add("Filter");
            }
            string msg = o.Message ?? "";
            if (names.Any(n => n != null && Regex.IsMatch(msg, @"\b" + Regex.Escape(n) + @"\b"))) {
                t.Verdict = "names-operation";
                return t;
            }
            foreach (Match m in OperationToken.Matches(msg)) {
                string op = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                if (op != r.Name && vocabulary.Contains(op)) {
                    t.Verdict = "names-other";
                    t.Note = "called " + r.Name + ", but the text names " + m.Value.TrimEnd('<', '(');
                    return t;
                }
            }
            if (o.ParamName != null && parameters != null && parameters.Contains(o.ParamName)) {
                t.Verdict = "names-parameter";
                t.Note = "names the parameter " + o.ParamName + " (fine for Argument*)";
                return t;
            }
            t.Verdict = "names-nothing";
            t.Note = "the text does not name the operation " + r.Name;
            return t;
        }

        // =====================================================================
        // Finalize: merge the repro check
        // =====================================================================

        public sealed class ReproCheckResult {
            public string Id;
            public string Expected;
            public string Observed;
            public string Status;
            public string Error;
        }

        private static int Finalize(Options o) {
            var modelPath = Path.Combine(o.Out, "model.json");
            var checkPath = Path.Combine(o.Out, "repro-check.json");
            if (!File.Exists(modelPath)) {
                Console.Error.WriteLine("no model.json in " + o.Out);
                return 2;
            }
            var model = Json.ReadFile<ReportModel>(modelPath);
            var excluded = new HashSet<string>();
            var excludedPath = Path.Combine(o.Out, "repro-excluded.txt");
            if (File.Exists(excludedPath)) {
                foreach (var line in File.ReadAllLines(excludedPath).Where(l => l.Trim().Length > 0)) {
                    excluded.Add(Path.GetRelativePath(o.Out, line.Trim()).Replace('\\', '/'));
                }
            }
            var all = model.Findings.Concat(model.Questions).Concat(model.Diagnostics).Concat(model.Documented).Concat(model.Regressions).ToList();
            foreach (var f in all) {
                f.ReproStatus = null;
                f.ReproObserved = null;
            }
            if (File.Exists(checkPath)) {
                var checks = Json.ReadFile<List<ReproCheckResult>>(checkPath).GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
                foreach (var f in all) {
                    if (f.Repro == null) {
                        continue;
                    }
                    string id = f.Kind == "regression" && f.Id.StartsWith("regression:", StringComparison.Ordinal) ? f.Id.Substring("regression:".Length) : f.Id;
                    if (checks.TryGetValue(id, out var c)) {
                        f.ReproStatus = c.Status;
                        f.ReproObserved = c.Observed ?? c.Error;
                    } else {
                        f.ReproStatus = excluded.Contains(f.Repro) ? "compile-error" : "not-run";
                    }
                }
            } else {
                foreach (var f in all.Where(x => x.Repro != null)) {
                    f.ReproStatus = "not-run";
                }
            }
            WriteReports(o.Out, model);
            PrintSummary(o, model);
            return 0;
        }

        private static void PrintSummary(Options o, ReportModel model) {
            var defects = model.Findings.Concat(model.Diagnostics.Where(d => d.Kind == "defect")).ToList();
            Console.WriteLine("contracts " + model.Meta.Flavor + "-" + model.Meta.Cfg + " (package " + (model.Meta.PackageVersion ?? "?") + "): " + model.Meta.Cases + " cases; defects " + defects.Count
                + ", questions " + model.Questions.Count + ", documented " + model.Documented.Count
                + (model.Meta.Baseline != null && model.BaselineNote == null ? ", regressions vs " + model.Meta.Baseline + " " + model.Regressions.Count : "")
                + "; report " + Report.DisplayPath(Path.Combine(o.Out, "report.md")));
            foreach (var f in defects) {
                Console.WriteLine("  [" + f.Severity + "] " + (f.Known != null ? f.Known + " " : "") + f.Id + (f.ReproStatus != null ? " (repro " + f.ReproStatus + ")" : ""));
            }
            if (model.Known.Count > 0) {
                Console.WriteLine("  known: " + string.Join(", ", model.Known.Select(k => k.Id + "=" + k.Status)));
            }
            if (model.ClaimWarnings.Count > 0) {
                Console.WriteLine("  warning: " + model.ClaimWarnings.Count + " claim(s) worded differently than the version profile expects: " + string.Join("; ", model.ClaimWarnings));
            }
            foreach (var e in model.StandErrors) {
                Console.WriteLine("  STAND ERROR: " + e);
            }
            if (model.BaselineSignatures != null && model.BaselineSignatures.Note == null) {
                var s = model.BaselineSignatures;
                Console.WriteLine("  signatures vs " + model.Meta.Baseline + ": new " + s.New.Count + ", gone " + s.Gone.Count + ", sharp growth " + s.Grown.Count);
            }
            if (model.BaselineNote != null) {
                Console.WriteLine("  " + model.BaselineNote);
            }
        }
    }
}
