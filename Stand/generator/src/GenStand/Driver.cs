using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using KenseiECS;

namespace KenseiStand.Generator {
    /// <summary> One stand program and what the oracle said about it. </summary>
    public sealed class Sample {
        public string Source;
        public Scenario Scenario;
        public int Seed;
        public List<string> Features = new();
        public ProgramModel Program;
        public List<Violation> Violations = new();
        public bool Invalid;
        public string InvalidCode;
        public string Outcome;
        public string Expectation;
        public List<(string Shape, string Id)> Shapes = new();
        /// <summary> Hash of the program model (the same program on another tree has the same hash). </summary>
        public string Hash;
        /// <summary> What this tree did with the program, for the cross-version rules. </summary>
        public Digest Digest;

        public int Size => Program.Classes.Count * 100 + Program.Classes.Sum(c => c.Fields.Count) * 10 + Program.Interfaces.Count;
    }

    /// <summary>
    /// The module: catalog and fuzz programs through the pipeline and the oracle (in parallel), the
    /// cross-version rules against the digests of other trees, then one finding per violation group
    /// with a minimized, re-checked repro and its signatures (rule, key and place), the known-issue
    /// table, the layout table, fuzz coverage, the comparison with a baseline report, and optionally
    /// the replay of another tree's findings here.
    /// </summary>
    public static class Driver {
        public const string FuzzParams = "Fuzzer v2: 1-3 hierarchies (root: generated, hand-written virtual/abstract/non-virtual/explicit/composite interface/hook/sealed/generic, non-system), 1-3 levels (generated, plain, override, new, Init overload, member named Init), Lib 30%, nesting 10%, partial parts 14%, fields: groups of 2-4 types, [Shared] of class/interface/abstract/generic/nested types, filter attributes in one list 15%, a foreign attribute first 6%; noise 22% (keywords, case of names, generic hint names, [Shared] keys, alias, unicode, nullable)";

        /// <summary> Rules that compare with another tree: their other side exists only for the original program, so they are not minimized. </summary>
        public static readonly HashSet<string> CrossRules = new() { "R-MIGRATION-ORDER", "R-LOST-SIGNAL", "R-REGRESSION-NULL" };

        public static List<Sample> BuildSamples(Options o) {
            var samples = new List<Sample>();
            foreach (var s in Catalog.All()) {
                samples.Add(new Sample { Source = "catalog:" + s.Id, Scenario = s, Program = s.Program });
            }
            for (int i = 0; i < o.Iterations; i++) {
                int seed = o.Seed + i;
                var program = Fuzzer.Generate(seed, out var features);
                samples.Add(new Sample { Source = "fuzz:" + seed, Seed = seed, Program = program, Features = features });
            }
            foreach (var s in samples) {
                s.Hash = CrossVersion.Hash(s.Program);
            }
            return samples;
        }

        public static string ProgramSetHash(IEnumerable<Sample> samples) {
            var text = string.Join("\n", samples.Select(s => s.Source + "=" + s.Hash).OrderBy(x => x, StringComparer.Ordinal));
            return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text)), 0, 6).ToLowerInvariant();
        }

        public static Settings SettingsFor(Options o, out string version) {
            version = PackageVersion(o.Src);
            string profile = o.Profile == "auto" ? ProfileFor(version) : o.Profile;
            return new Settings { Profile = profile, Debug = o.Debug, Claims = Claims.For(o.Src, version) };
        }

        public static int Run(Options o) {
            var started = DateTime.UtcNow;
            var clock = Stopwatch.StartNew();
            var settings = SettingsFor(o, out string version);
            string profile = settings.Profile;
            Directory.CreateDirectory(o.Out);
            var data = new ReportData {
                Flavor = o.Flavor, Config = o.Config, Profile = profile, PackageVersion = version, Unreleased = settings.Claims.Unreleased, Src = o.Src, GenSrc = o.GenSrc, Root = o.Root,
                GeneratorDll = Report.Rel(o.Root, Pipeline.GeneratorLocation), Roslyn = typeof(Microsoft.CodeAnalysis.Compilation).Assembly.GetName().Version?.ToString(),
                Mode = o.Mode, Seed = o.Seed, Iterations = o.Iterations, Started = started.ToString("yyyy-MM-dd HH:mm:ss") + "Z",
                KenseiDebug = KenseiDebugCompiled(), Claims = settings.Claims.Claims,
            };
            Console.WriteLine($"generator stand: {o.Flavor}-{o.Config}, package {version}, contract profile {profile}, {o.Mode}: catalog + {o.Iterations} fuzz programs (seed {o.Seed}), {o.Jobs} jobs");

            var samples = BuildSamples(o);
            data.ProgramSet = ProgramSetHash(samples);
            Parallel.ForEach(samples, new ParallelOptions { MaxDegreeOfParallelism = o.Jobs }, s => Evaluate(s, settings));
            var catalogSamples = samples.Where(s => s.Scenario != null).ToList();
            var fuzzSamples = samples.Where(s => s.Scenario == null).ToList();
            Console.WriteLine($"  evaluated {samples.Count} programs in {clock.Elapsed.TotalSeconds:F1} s ({fuzzSamples.Count(s => s.Invalid)} fuzz programs discarded as invalid)");

            // Cross-version rules against the digests of other trees (run.sh observes them first).
            foreach (var path in o.XObs) {
                DigestFile other;
                try {
                    other = CrossVersion.Load(path);
                } catch (Exception e) {
                    data.Notes.Add($"Cross-version: {Report.Rel(o.Root, path)} could not be read ({e.GetType().Name}).");
                    continue;
                }
                var (compared, pairs) = CrossVersion.Apply(samples, other, settings, o.Jobs);
                bool line21 = other.Profile == "2.1" && profile != "2.1";
                data.Cross.Add(new CrossBlock {
                    Tag = other.Tag, Version = other.Version, Profile = other.Profile, Compared = compared, MigrationPairs = pairs,
                    Role = line21 ? "2.1 line: migration pairs, lost compiler signals, regressions" : "baseline: lost compiler signals, regressions",
                    Note = compared == 0 ? "no program in common (another stand version, mode or seed range)" : "",
                });
                Console.WriteLine($"  cross-version against {other.Tag} ({other.Version}): {compared} programs compared, {pairs} migration pairs");
            }

            // Findings: one per violation group (rule and key) and known issue; signatures (with the place) inside. A key
            // whose violations carry different known labels (one diagnostic code in the layout of a known issue and in
            // another one) makes one finding per label: a known issue never claims the signatures of another mechanism.
            var groups = new Dictionary<(string Key, string Known), List<(Sample S, Violation V)>>();
            foreach (var s in samples) {
                foreach (var v in s.Violations) {
                    var key = (GroupKey(v), v.Known);
                    if (!groups.TryGetValue(key, out var list)) {
                        groups[key] = list = new List<(Sample, Violation)>();
                    }
                    list.Add((s, v));
                }
            }
            // The part of a key with a label (or the only part) keeps the id of the key, the others get their own.
            var primary = groups.Keys.GroupBy(k => k.Key).ToDictionary(g => g.Key,
                g => g.Select(x => x.Known).OrderBy(k => k == null ? 1 : 0).ThenBy(k => k, StringComparer.Ordinal).First());
            string IdOf((string Key, string Known) g) => primary[g.Key] == g.Known ? FindingId(g.Key) : FindingId(g.Key + "|" + (g.Known ?? "new"));
            var findings = new ConcurrentBag<Finding>();
            var reproDir = Path.Combine(o.Out, "repro");
            if (Directory.Exists(reproDir)) {
                Directory.Delete(reproDir, true);
            }
            Directory.CreateDirectory(reproDir);
            Parallel.ForEach(groups, new ParallelOptions { MaxDegreeOfParallelism = o.Jobs }, g => {
                var f = BuildFinding(g.Key.Key, g.Key.Known, IdOf(g.Key), g.Value, settings, o, reproDir);
                if (f != null) {
                    findings.Add(f);
                }
            });
            var all = findings.ToList();
            data.Findings = all.Where(f => f.Status == "defect").OrderBy(f => SeverityRank(f.Severity)).ThenBy(f => f.Known ?? "ZZ").ThenBy(f => f.Rule).ThenBy(f => f.Id).ToList();
            data.Regressions = all.Where(f => f.Status == "regression").OrderBy(f => f.Known ?? "ZZ").ThenBy(f => f.Rule).ThenBy(f => f.Id).ToList();
            data.Questions = all.Where(f => f.Status == "question").OrderBy(f => f.Rule).ThenBy(f => f.Id).ToList();
            data.Documented = all.Where(f => f.Status == "documented").OrderBy(f => f.Rule).ThenBy(f => f.Id).ToList();
            data.Inconclusive = all.Where(f => f.Status == "note").OrderBy(f => f.Id).ToList();
            var byGroup = all.ToDictionary(f => (f.GroupKey, f.Known), f => f);

            // Signatures per program (for the comparison with a baseline report).
            foreach (var s in samples.Where(s => s.Violations.Count > 0)) {
                var map = new Dictionary<string, List<string>>();
                foreach (var g in s.Violations.Where(v => !v.IsNote).GroupBy(v => v.Status)) {
                    string k = g.Key == "defect" ? "d" : g.Key == "regression" ? "r" : g.Key == "documented" ? "doc" : "q";
                    map[k] = g.Select(v => SignatureId(Signature(v))).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
                }
                if (map.Count > 0) {
                    data.Programs[s.Source] = map;
                }
            }

            // Layout table (catalog) and the known-issue table.
            foreach (var s in catalogSamples) {
                var ids = s.Violations.Select(v => byGroup.TryGetValue((GroupKey(v), v.Known), out var f) ? f : null).Where(f => f != null).Distinct().ToList();
                string verdict = s.Invalid ? "the stand program is invalid (not a finding)"
                    : ids.Count == 0 ? "ok"
                    : string.Join(", ", ids.OrderBy(f => f.Status == "defect" ? 0 : f.Status == "regression" ? 1 : f.Status == "documented" ? 3 : 2)
                        .Select(f => (f.Status == "defect" ? "" : f.Status + " ") + f.Id + (f.Known != null ? " (" + f.Known + ")" : "")));
                data.Layouts.Add(new LayoutRow {
                    Id = s.Scenario.Id, Group = s.Scenario.Group, Title = s.Scenario.Title, Expectation = s.Expectation, Outcome = s.Outcome, Verdict = verdict,
                    Findings = ids.Where(f => f.Status == "defect").Select(f => f.Id).ToList(),
                });
            }
            bool migrationRan = data.Cross.Any(x => x.MigrationPairs > 0);
            bool crossRan = data.Cross.Any(x => x.Compared > 0);
            foreach (var k in KnownIssue.All.Concat(KnownIssue.New)) {
                var defects = data.Findings.Where(f => f.Known == k.Id).ToList();
                var regressions = data.Regressions.Where(f => f.Known == k.Id).ToList();
                var questions = data.Questions.Where(f => f.Known == k.Id).ToList();
                var documented = data.Documented.Where(f => f.Known == k.Id).ToList();
                string status = defects.Count > 0 ? "found" : regressions.Count > 0 ? "regression" : questions.Count > 0 ? "question" : documented.Count > 0 ? "documented" : "not-found";
                bool crossIssue = k.Id is "GN7" or "GN8";
                bool needsCross = k.Id == "GN7" ? !migrationRan : k.Id == "GN8" && !crossRan;
                if (status == "not-found" && crossIssue && profile == "2.1") {
                    // The upgrade note and the 2.2 chain exist only from 2.2 on.
                    status = "not-applicable";
                } else if (status == "not-found" && needsCross) {
                    status = "not-checked";
                }
                var row = new KnownRow {
                    Id = k.Id, Title = k.Title, ExpectedSeverity = k.Severity, Detected = defects.Count > 0, Status = status,
                    Findings = defects.Select(f => f.Id).ToList(), Regressions = regressions.Select(f => f.Id).ToList(), Questions = questions.Select(f => f.Id).ToList(),
                    Documented = documented.Select(f => f.Id).ToList(),
                };
                foreach (var w in k.Witnesses) {
                    var s = catalogSamples.FirstOrDefault(x => x.Scenario.Id == w);
                    if (s != null) {
                        row.Witnesses.Add($"`{w}`: {s.Outcome} → {data.Layouts.First(l => l.Id == w).Verdict}");
                    }
                }
                var mine = defects.Concat(regressions).Concat(questions).Concat(documented).ToList();
                row.Note = mine.Count > 0 ? $"catalog: {mine.Sum(f => f.CatalogHits)}, fuzz: {mine.Sum(f => f.FuzzHits)}"
                    : status == "not-applicable" ? "not applicable: a 2.2 issue, judged by comparing a 2.2 tree with the 2.1 line"
                    : status == "not-checked" ? (k.Id == "GN7" ? "not checked: no 2.1 tree was observed for the migration pairs" : "not checked: no other tree was observed")
                    : "the witness layouts break no promise on this tree (outcomes below)";
                data.Known.Add(row);
            }

            // Fuzz coverage.
            data.Stats["catalog layouts"] = catalogSamples.Count;
            data.Stats["fuzz programs"] = fuzzSamples.Count;
            data.Stats["fuzz programs discarded (invalid)"] = fuzzSamples.Count(s => s.Invalid);
            data.Stats["fuzz programs with a defect"] = fuzzSamples.Count(s => s.Violations.Any(v => v.IsDefect));
            data.Stats["fuzz programs with Lib"] = fuzzSamples.Count(s => s.Program.HasLib);
            data.Stats["generated classes in fuzz"] = fuzzSamples.Where(s => !s.Invalid).Sum(s => s.Shapes.Count);
            data.Stats["defects"] = data.Findings.Count;
            data.Stats["regressions"] = data.Regressions.Count;
            data.Stats["contract questions"] = data.Questions.Count;
            data.Stats["defect signatures"] = data.Findings.Sum(f => f.Signatures.Count);
            foreach (var s in fuzzSamples.Where(s => s.Invalid)) {
                data.InvalidByCode[s.InvalidCode] = data.InvalidByCode.TryGetValue(s.InvalidCode, out int n) ? n + 1 : 1;
            }
            foreach (var g in fuzzSamples.Where(s => s.Invalid).GroupBy(s => s.InvalidCode)) {
                foreach (var s in g.Take(3)) {
                    data.InvalidExamples.Add($"{s.Source}: {s.Outcome}");
                }
            }
            var flagged = new HashSet<string>(fuzzSamples.SelectMany(s => s.Violations.Where(v => v.IsDefect).Select(v => s.Source + "#" + (v.ClassId ?? ""))));
            foreach (var g in fuzzSamples.Where(s => !s.Invalid).SelectMany(s => s.Shapes.Select(x => (x.Shape, Flagged: flagged.Contains(s.Source + "#" + x.Id)))).GroupBy(x => x.Shape)) {
                data.Coverage.Add(new CoverageRow { Shape = g.Key, Count = g.Count(), Flagged = g.Count(x => x.Flagged) });
            }
            data.Coverage = data.Coverage.OrderByDescending(c => c.Count).ToList();
            foreach (var g in fuzzSamples.SelectMany(s => s.Features).GroupBy(x => x).OrderBy(g => g.Key)) {
                data.Features[g.Key] = g.Count();
            }

            if (o.BaselineReport != null) {
                data.Baseline = CompareWithBaseline(o.BaselineReport, data);
            }
            data.Notes.AddRange(Notes(o, settings, version, data));
            foreach (var replay in o.Replay) {
                ReplayInto(data, replay, settings, o);
            }
            data.Seconds = clock.Elapsed.TotalSeconds;
            Report.Write(data, o.Out);
            Console.WriteLine($"  {data.Findings.Count} defects ({string.Join(", ", data.Findings.GroupBy(f => f.Severity).OrderBy(g => SeverityRank(g.Key)).Select(g => g.Count() + " " + g.Key))}), " +
                              $"{data.Regressions.Count} regressions, {data.Questions.Count} contract questions, {data.Findings.Count(f => f.Known == null)} defects and {data.Regressions.Count(f => f.Known == null)} regressions without a label; known: " +
                              string.Join(" ", data.Known.Select(k => k.Id + (k.Status switch { "found" => "+", "regression" => "~r", "question" => "~q", "not-checked" => "?", _ => "-" }))));
            if (data.Baseline != null) {
                var b = data.Baseline;
                Console.WriteLine(b.Comparable
                    ? $"  against {b.Flavor}: {b.NewSignatures.Count} new signatures, {b.Spikes.Count} hit spikes, {b.GoneSignatures.Count} gone, {b.ProgramsChanged} programs with a new defect or regression, {b.NewQuestions.Count} new question signatures"
                    : $"  against {b.Flavor}: {b.Note}");
            }
            Console.WriteLine($"  report: {Report.Rel(o.Root, Path.Combine(o.Out, "report.md"))} ({data.Seconds:F0} s)");
            return 0;
        }

        public static int SeverityRank(string s) => s switch { "high" => 0, "medium" => 1, "low" => 2, "question" => 3, _ => 4 };

        // Violations of one finding. The runtime trace of a generator crash (every system without Init)
        // belongs to the crash itself.
        // A leaf below a class with the defect ("blocked:<shape of that class>") is the same finding.
        public static string GroupKey(Violation v) {
            if (v.Rule == "R-FIELDS-NULL" && v.Key.StartsWith("NULL|after-crash|")) {
                return "R-CRASH|CRASH|" + v.Key.Substring("NULL|after-crash|".Length);
            }
            return v.Rule + "|" + v.Key.Replace("blocked:", "");
        }

        /// <summary> Mechanism and place: the group key and the contract shape of the class it is about. </summary>
        public static string Signature(Violation v) => GroupKey(v) + "@" + (v.Place ?? "-").Replace("blocked:", "");

        public static string SignatureId(string signature) => "SIG-" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(signature)), 0, 3).ToLowerInvariant();

        public static string FindingId(string groupKey) {
            var hash = SHA1.HashData(Encoding.UTF8.GetBytes(groupKey));
            return "GEN-" + Convert.ToHexString(hash, 0, 3).ToLowerInvariant();
        }

        private static void Evaluate(Sample s, Settings settings) {
            Observation obs;
            try {
                obs = Pipeline.Execute(s.Program, settings);
            } catch (Exception e) {
                s.Invalid = true;
                s.InvalidCode = "stand:" + e.GetType().Name;
                return;
            }
            s.Digest = Digest.Of(s.Source, s.Hash, obs);
            var contract = new Contract(s.Program, settings.Profile, settings.Claims);
            if (obs.Invalid) {
                s.Invalid = true;
                s.InvalidCode = obs.Units.SelectMany(u => u.InvalidErrors).Select(d => d.Id).FirstOrDefault() ?? "?";
                s.Outcome = "the stand program does not compile even without the generator: " + string.Join("; ", obs.Units.SelectMany(u => u.InvalidErrors).Take(2).Select(d => $"{d.Id} {d.File}:{d.Line} [{d.ClassName}] {d.Message}"));
                return;
            }
            s.Violations = Oracle.Check(s.Program, contract, obs);
            foreach (var v in s.Violations) {
                v.Known = KnownIssue.Of(v);
            }
            foreach (var c in s.Program.Classes.Where(c => c.IsGenerated && s.Program.IsActive(c))) {
                s.Shapes.Add((contract.For(c).Shape, c.Id));
            }
            if (s.Scenario != null) {
                var focus = s.Program.Cls(s.Scenario.Focus);
                s.Expectation = focus == null ? "-" : contract.For(focus).ToString();
                s.Outcome = Summary.Outcome(s.Program, obs, s.Scenario.Focus);
            }
        }

        private static Finding BuildFinding(string groupKey, string known, string id, List<(Sample S, Violation V)> hits, Settings settings, Options o, string reproDir) {
            var first = hits.Select(h => h.V).OrderBy(v => v.Rule == "R-CRASH" ? 0 : 1).First();
            var f = new Finding {
                Id = id, GroupKey = groupKey, Rule = first.Rule, Class = first.Class, Severity = first.Severity, Key = first.Key, Status = first.Status,
                Known = known, Question = first.Question, Doc = first.Doc,
                Claim = first.Claim, Downgrade = first.Downgrade, Against = first.Against,
                Quote = first.Documented ? settings.Claims?.Get(first.Claim)?.Quote : null,
                CatalogHits = hits.Where(h => h.S.Scenario != null).Select(h => h.S.Source).Distinct().Count(),
                FuzzHits = hits.Where(h => h.S.Scenario == null).Select(h => h.S.Source).Distinct().Count(),
            };
            f.Sources = hits.Select(h => h.S.Source).Distinct().OrderBy(x => x.StartsWith("catalog:") ? 0 : 1).ThenBy(x => x.Length).ThenBy(x => x).Take(12).ToList();
            f.Signatures = hits.GroupBy(h => Signature(h.V)).Select(g => new SignatureRow {
                Id = SignatureId(g.Key), Text = g.Key, Place = (g.First().V.Place ?? "-").Replace("blocked:", ""),
                CatalogHits = g.Where(h => h.S.Scenario != null).Select(h => h.S.Source).Distinct().Count(),
                FuzzHits = g.Where(h => h.S.Scenario == null).Select(h => h.S.Source).Distinct().Count(),
            }).OrderByDescending(x => x.CatalogHits + x.FuzzHits).ThenBy(x => x.Text, StringComparer.Ordinal).ToList();
            if (f.Severity == "note") {
                f.Title = "The run did not get to the check: " + first.Message;
                f.Message = first.Message;
                return f;
            }

            if (CrossRules.Contains(f.Rule)) {
                // The other tree's side exists only for the original program: no minimization; the repro
                // is the program this tree ran (for a migration pair, its upgraded form).
                var (s0, v0) = hits.OrderBy(h => h.S.Scenario != null ? 0 : 1).ThenBy(h => (h.V.Subject ?? h.S.Program).Classes.Count).ThenBy(h => h.S.Seed).First();
                var program = (v0.Subject ?? s0.Program).Clone();
                program.Title = f.Id;
                f.Exemplar = s0.Source;
                f.Seed = s0.Scenario == null ? s0.Seed : (int?)null;
                f.Params = s0.Scenario != null ? $"catalog, layout {s0.Scenario.Id}: {s0.Scenario.Title}" : $"fuzz seed {s0.Seed} ({FuzzParams}); program features: {string.Join(", ", s0.Features)}";
                var obsX = Pipeline.Execute(program, settings);
                var cx = new Contract(program, settings.Profile, settings.Claims);
                f.Message = v0.Message;
                f.Evidence = v0.Evidence;
                f.ClassId = v0.ClassId;
                f.RunId = v0.RunId;
                f.ClassName = program.Cls(v0.ClassId)?.Plain;
                f.Expected = f.Rule switch {
                    "R-MIGRATION-ORDER" when v0.Key.EndsWith("|base-no-longer-runs") => "after the documented upgrade ('delete the line') the generated Init calls the base Init, so every base Init/OnInit runs as often as in the 2.1 layout",
                    "R-MIGRATION-ORDER" => settings.Claims != null && settings.Claims.Has("upgrade-order-222")
                        ? "after the documented upgrade the base sees the derived fields filled, as in the 2.1 layout (2.2.2: 'so the base Init still sees the derived fields')"
                        : "not settled: the 2.2.0/2.2.1 guide says both 'the generated Init makes the call before the derived fields' and 'The order stays the same'",
                    "R-LOST-SIGNAL" when v0.Documented => "documented: KECS013 recognises a generated base in another assembly only by its protected or public injected fields",
                    "R-LOST-SIGNAL" => $"none by the documents; {v0.Against} rejected the program at build time",
                    _ => $"none by the documents; {v0.Against} filled the fields",
                };
                f.Title = Titles.For(v0, program, cx);
                var vs = new List<Violation> { v0 };
                if (!obsX.Invalid) {
                    vs.AddRange(Oracle.Check(program, cx, obsX));
                }
                Repro.Write(f, program, obsX, vs, settings, o, reproDir);
                return f;
            }

            // Exemplar: a catalog layout if one shows it, otherwise the smallest random program.
            var exemplar = hits.Select(h => h.S).Distinct().OrderBy(x => x.Scenario != null ? 0 : 1).ThenBy(x => x.Size).ThenBy(x => x.Seed).First();
            f.Exemplar = exemplar.Source;
            f.Seed = exemplar.Scenario == null ? exemplar.Seed : (int?)null;
            f.Params = exemplar.Scenario != null
                ? $"catalog, layout {exemplar.Scenario.Id}: {exemplar.Scenario.Title}"
                : $"fuzz seed {exemplar.Seed} ({FuzzParams}); program features: {string.Join(", ", exemplar.Features)}";

            // A reduction must stay a program the exemplar's errors without the generator explain: one that gains an error
            // of its own there (an override whose virtual base was cut away, say) would show the defect only through
            // code that never compiled.
            HashSet<string> allowedPre;
            try {
                allowedPre = Pipeline.Execute(exemplar.Program, settings).PreErrors;
            } catch (Exception) {
                allowedPre = new HashSet<string>();
            }
            bool StillFails(ProgramModel q) {
                var obs = Pipeline.Execute(q, settings);
                if (obs.Invalid || !obs.PreErrors.IsSubsetOf(allowedPre)) {
                    return false;
                }
                return Oracle.Check(q, new Contract(q, settings.Profile, settings.Claims), obs).Any(v => GroupKey(v) == groupKey && KnownIssue.Of(v) == known);
            }
            var minimized = Minimizer.Run(exemplar.Program, StillFails, o.Budget, out int evaluations);
            minimized.Title = f.Id;
            f.MinimizeEvaluations = evaluations;

            var obsMin = Pipeline.Execute(minimized, settings);
            var contractMin = new Contract(minimized, settings.Profile, settings.Claims);
            var vsMin = Oracle.Check(minimized, contractMin, obsMin);
            var vMin = vsMin.Where(v => GroupKey(v) == groupKey).OrderBy(v => KnownIssue.Of(v) == known ? 0 : 1).ThenBy(v => v.Rule == first.Rule ? 0 : 1).FirstOrDefault() ?? first;
            f.Message = vMin.Message;
            f.ClassId = vMin.ClassId;
            f.RunId = vMin.RunId;
            f.ClassName = minimized.Cls(vMin.ClassId)?.Plain;
            var cls = minimized.Cls(vMin.ClassId) ?? minimized.Cls(vMin.RunId);
            f.Expected = f.Rule == "R-CRASH" ? "the generator does not throw (universal rule); every system gets the outcome of its own layout"
                : cls == null ? "-" : contractMin.For(cls).ToString();
            f.Title = Titles.For(vMin, minimized, contractMin);
            Repro.Write(f, minimized, obsMin, vsMin, settings, o, reproDir);
            return f;
        }

        /// <summary>
        /// Signature-level comparison with the report of another tree: new signatures (also inside a
        /// known issue that was found before), hit spikes and, when the program sets are the same,
        /// every program that gained a defect or regression signature.
        /// </summary>
        private static BaselineBlock CompareWithBaseline(string dir, ReportData data) {
            var block = new BaselineBlock { Dir = Report.Rel(data.Root, dir), Flavor = Path.GetFileName(dir.TrimEnd('/', '\\')) };
            string path = Path.Combine(dir, "report.json");
            if (!File.Exists(path)) {
                block.Note = "no report.json there: run the module on the baseline first";
                return block;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            block.Flavor = root.GetProperty("flavor").GetString() + "-" + root.GetProperty("config").GetString();
            if (!root.TryGetProperty("format", out var format) || format.GetInt32() < 2) {
                block.Note = "the baseline report was written by an older version of this module (no signatures): run the module on the baseline again";
                return block;
            }
            string Str(JsonElement e, string name) => e.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;
            bool sameSet = Str(root, "programSet") == data.ProgramSet;
            var baseSigs = new Dictionary<string, (string Status, int Hits, string Text, string Finding, string Known, string Title)>();
            foreach (var list in new[] { "findings", "regressions", "questions" }) {
                if (!root.TryGetProperty(list, out var items)) {
                    continue;
                }
                foreach (var f in items.EnumerateArray()) {
                    if (!f.TryGetProperty("signatures", out var sigs)) {
                        continue;
                    }
                    foreach (var s in sigs.EnumerateArray()) {
                        int hits = s.GetProperty("catalogHits").GetInt32() + s.GetProperty("fuzzHits").GetInt32();
                        baseSigs[Str(s, "id")] = (Str(f, "status") ?? "defect", hits, Str(s, "text"), Str(f, "id"), Str(f, "known"), Str(f, "title"));
                    }
                }
            }
            block.Comparable = true;
            if (!sameSet) {
                block.Note = "the program sets differ (another stand version, mode or seed range): signatures are compared, hits and programs are not";
            }
            var current = new HashSet<string>();
            foreach (var f in data.Findings.Concat(data.Regressions)) {
                foreach (var s in f.Signatures) {
                    current.Add(s.Id);
                    int hits = s.CatalogHits + s.FuzzHits;
                    var change = new SigChange { Signature = s.Id, Text = s.Text, Finding = f.Id, Known = f.Known, Status = f.Status, Title = f.Title, Hits = hits };
                    if (!baseSigs.TryGetValue(s.Id, out var b) || b.Status is "question" or "note") {
                        s.Change = "new";
                        block.NewSignatures.Add(change);
                        continue;
                    }
                    s.BaselineHits = b.Hits;
                    change.BaselineHits = b.Hits;
                    if (sameSet && hits >= 2 * b.Hits + 5) {
                        s.Change = "spike";
                        block.Spikes.Add(change);
                    } else {
                        s.Change = "same";
                    }
                }
            }
            foreach (var f in data.Questions) {
                foreach (var s in f.Signatures.Where(s => !baseSigs.ContainsKey(s.Id))) {
                    s.Change = "new";
                    block.NewQuestions.Add(new SigChange { Signature = s.Id, Text = s.Text, Finding = f.Id, Known = f.Known, Status = f.Status, Title = f.Title, Hits = s.CatalogHits + s.FuzzHits });
                }
            }
            foreach (var (id, b) in baseSigs.Where(kv => kv.Value.Status is "defect" or "regression" && !current.Contains(kv.Key))) {
                block.GoneSignatures.Add(new SigChange { Signature = id, Text = b.Text, Finding = b.Finding, Known = b.Known, Status = b.Status, Title = b.Title, Hits = 0, BaselineHits = b.Hits });
            }
            if (sameSet && root.TryGetProperty("programs", out var programs)) {
                var before = new Dictionary<string, HashSet<string>>();
                foreach (var p in programs.EnumerateObject()) {
                    var set = new HashSet<string>();
                    foreach (var k in new[] { "d", "r" }) {
                        if (p.Value.TryGetProperty(k, out var ids)) {
                            set.UnionWith(ids.EnumerateArray().Select(x => x.GetString()));
                        }
                    }
                    before[p.Name] = set;
                }
                foreach (var (source, map) in data.Programs) {
                    var now = new HashSet<string>(map.Where(kv => kv.Key is "d" or "r").SelectMany(kv => kv.Value));
                    before.TryGetValue(source, out var was);
                    was ??= new HashSet<string>();
                    var added = now.Except(was).OrderBy(x => x, StringComparer.Ordinal).ToList();
                    if (added.Count == 0) {
                        continue;
                    }
                    block.ProgramsChanged++;
                    if (block.Programs.Count < 50) {
                        block.Programs.Add(new ProgramChange { Source = source, Added = added, Removed = was.Except(now).OrderBy(x => x, StringComparer.Ordinal).ToList() });
                    }
                }
            }
            block.NewSignatures = block.NewSignatures.OrderBy(x => x.Known ?? "ZZ").ThenBy(x => x.Text, StringComparer.Ordinal).ToList();
            return block;
        }

        public static string PackageVersion(string src) {
            try {
                string path = Path.Combine(src, "package.json");
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                return doc.RootElement.GetProperty("version").GetString();
            } catch (Exception) {
                return "?";
            }
        }

        /// <summary> Contract profile: 2.1 for the 2.1.x packages, otherwise the latest transcribed contract (2.2). </summary>
        public static string ProfileFor(string version) => version != null && version.StartsWith("2.1.") ? "2.1" : "2.2";

        private static bool KenseiDebugCompiled() {
            return typeof(World).Assembly.GetTypes().Any(t => t.Namespace == "KenseiECS" && (t.Name == "EcsProfiler" || t.Name == "WorldDebugView"));
        }

        /// <summary> Notes of the report, among them generator entries of the package CHANGELOG newer than the contract the stand transcribes. </summary>
        private static IEnumerable<string> Notes(Options o, Settings settings, string version, ReportData data) {
            string profile = settings.Profile;
            var probe = new Contract(new ProgramModel(), profile, settings.Claims);
            var rules222 = new List<string>();
            if (probe.FieldsBeforeBase) rules222.Add("derived fields before the base Init");
            if (probe.VirtualEverywhere) rules222.Add("generated Init virtual unless sealed");
            if (probe.Layout21NeedsCall) rules222.Add("KECS006 for the 2.1 layout without base.Init");
            if (probe.Kecs013) rules222.Add("warning KECS013");
            if (probe.GenericHelperAllowed) rules222.Add("a generic Init<T> helper takes no call");
            if (probe.HookSupported) rules222.Add("an override of a base OnInit hook runs once");
            yield return $"Contract: profile {profile} (Contract.cs) transcribes docs/guides/source-generator.md and CHANGELOG {(profile == "2.1" ? "2.1.1" : "2.2.0-2.2.2")}; " +
                         (profile == "2.1" ? "" : rules222.Count > 0 ? "the 2.2.2 rules apply where this tree's documents make them (" + string.Join(", ", rules222) + "); " : "the rules of 2.2.0 and 2.2.1 apply (the 2.2.2 sentences are not in this tree's documents); ") +
                         "what the documents leave open is checked only by the universal rules (no crash, only KECS diagnostics, generated code without errors, every injected field filled with what the documented call returns, the base Init once and before the derived OnInit).";
            var inactive = settings.Claims.Claims.Where(c => !c.Active).ToList();
            yield return $"Versioned profile: {settings.Claims.Claims.Count} claims, {settings.Claims.Claims.Count - inactive.Count} active on this tree (package {version}{(settings.Claims.Unreleased ? ", with unreleased CHANGELOG entries" : "")}{(settings.Claims.DocsAvailable ? "" : ", no docs/ folder: claims from docs/ are judged by the version")})" +
                         (inactive.Count == 0 ? "." : "; the rules of " + string.Join(", ", inactive.Select(c => $"{c.Id} ({c.State})")) + " report questions instead of defects.");
            yield return "Warnings in the generated code: CS0108/CS0114 where the generated Init hides an inherited Init break the CHANGELOG 2.2.0 promise ('declared new, no more CS0108/CS0114'; claim init-hiding-new) and are defects; CS0108/CS0114 on OnInit and CS0109 are not covered by any document and are questions, except over a base OnInit hook from 2.2.2 on (claim hook-222), which also turns CS0111 there from a question into a defect.";
            yield return "Diagnostic texts (R-KECS-TEXT): a KECS006 the contract expects must name the reason of its layout (claim kecs006-reason: 'The message names the reason.'), and every expected diagnostic the class, field, containing type or component type it is about.";
            yield return "Injected values: after SystemsRunner.Init every non-null injected field is compared with what the documented call returns on the same World and SharedData (the filter of its attributes, world.Pool<T>(), world.Group<...>(), shared.Get<T>(key)); a different object is R-WRONG-INJECTION.";
            if (data.Cross.Count == 0) {
                yield return "Cross-version rules (migration pairs GN7, lost compiler signal GN8, regressions) did not run: no other tree was observed. Stand/generator/run.sh observes the newest 2.1 tree under Stand/out/src and the --baseline-src tree.";
            }
            yield return "Stand programs that do not compile even without the generator are discarded (not findings); see the statistics.";
            string changelog = Path.Combine(o.Src, "CHANGELOG.md");
            if (File.Exists(changelog)) {
                var text = File.ReadAllText(changelog);
                var sections = Regex.Split(text, @"(?m)^## ");
                // The newest release the model transcribes; the sections above it are reported. An [Unreleased] section is
                // modelled when this tree's documents carry the 2.2.2 sentences the rules rest on.
                var modelled = profile == "2.1" ? new[] { "[2.1.1]" } : new[] { "[2.2.2]", "[2.2.1]", "[2.2.0]" };
                bool unreleasedModelled = profile != "2.1" && probe.FieldsBeforeBase && probe.VirtualEverywhere && probe.Layout21NeedsCall && probe.Kecs013;
                foreach (var section in sections) {
                    if (!section.StartsWith("[")) {
                        continue;
                    }
                    if (modelled.Any(section.StartsWith)) {
                        break;
                    }
                    if (section.StartsWith("[Unreleased]") && unreleasedModelled) {
                        continue;
                    }
                    var m = Regex.Match(section, @"(?ms)^### Generator\s*$(.*?)(?=^### |\z)");
                    if (m.Success && m.Groups[1].Value.Trim().Length > 0) {
                        string head = section.Split('\n')[0].Trim();
                        yield return $"CHANGELOG {head} has generator entries the stand's contract model does not know yet — check Contract.cs and Claims.cs: {m.Groups[1].Value.Trim().Replace("\n", " ")}";
                    }
                }
            }
        }

        // Replay of another report's findings on this tree (for the regression question).
        private static void ReplayInto(ReportData data, string otherDir, Settings settings, Options o) {
            string path = Path.Combine(otherDir, "report.json");
            if (!File.Exists(path)) {
                data.Notes.Add($"Replay: {Report.Rel(o.Root, path)} not found.");
                return;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            string otherTag = root.GetProperty("flavor").GetString() + "-" + root.GetProperty("config").GetString();
            string otherProfile = root.GetProperty("profile").GetString();
            var block = new ReplayBlock { Of = otherTag, OfProfile = otherProfile, OfDir = otherDir };
            foreach (var item in root.GetProperty("findings").EnumerateArray()) {
                string id = item.GetProperty("id").GetString();
                string modelPath = Path.Combine(otherDir, item.GetProperty("reproModel").GetString());
                var row = new ReplayRow {
                    Finding = id, Known = item.TryGetProperty("known", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null,
                    Severity = item.GetProperty("severity").GetString(), Rule = item.GetProperty("rule").GetString(), Key = item.GetProperty("key").GetString(),
                    Title = item.GetProperty("title").GetString(),
                };
                block.Rows.Add(row);
                if (!File.Exists(modelPath)) {
                    row.Verdict = "no repro model";
                    continue;
                }
                var model = Json.Read<ProgramModel>(File.ReadAllText(modelPath));
                var obs = Pipeline.Execute(model, settings);
                if (obs.Invalid) {
                    row.Outcome = "the program does not compile even without the generator on this tree";
                    row.Verdict = "not comparable";
                    continue;
                }
                row.Outcome = Summary.Outcome(model, obs, item.TryGetProperty("classId", out var cid) && cid.ValueKind == JsonValueKind.String ? cid.GetString() : null);
                string groupKey = GroupKey(new Violation { Rule = row.Rule, Key = row.Key });
                var theirs = Oracle.Check(model, new Contract(model, otherProfile), obs);
                var ours = Oracle.Check(model, new Contract(model, settings.Profile, settings.Claims), obs);
                row.SameSymptom = theirs.Any(v => GroupKey(v) == groupKey);
                var ourDefects = ours.Where(v => v.IsDefect).ToList();
                row.Violations = ourDefects.Select(v => v.Rule + " " + v.Key).Distinct().ToList();
                string clsId = item.TryGetProperty("classId", out var c1) && c1.ValueKind == JsonValueKind.String ? c1.GetString()
                    : item.TryGetProperty("runId", out var r1) && r1.ValueKind == JsonValueKind.String ? r1.GetString() : null;
                var cls = model.Cls(clsId);
                var expHere = cls == null ? null : new Contract(model, settings.Profile, settings.Claims).For(cls);
                var expThere = cls == null ? null : new Contract(model, otherProfile).For(cls);
                row.ExpectedHere = expHere?.ToString();
                row.ExpectedThere = expThere?.ToString();
                if (row.SameSymptom) {
                    row.Verdict = ourDefects.Count == 0 && otherProfile != settings.Profile
                        ? $"not a regression: the same behaviour here, and the contract {settings.Profile} allows it"
                        : "not a regression: the same problem here";
                } else if (ourDefects.Count == 0) {
                    bool newPromise = expThere?.Kind == ExpKind.Works && expHere?.Kind == ExpKind.Reports;
                    row.Regression = !newPromise;
                    row.Verdict = newPromise
                        ? $"a new feature of {otherTag}: here the layout is rejected by a documented error ({expHere}), {otherTag} promises support but has the defect — not a regression"
                        : $"REGRESSION of {otherTag}: here {row.Outcome}, which is right by the contract {settings.Profile}";
                } else {
                    row.Verdict = "another defect here: " + string.Join("; ", row.Violations);
                }
            }
            data.Replays.Add(block);
            Report.WriteReplay(data, block, o.Out);
        }

        /// <summary> --observe: the digests of every program of this run, for a run on another tree. </summary>
        public static int Observe(Options o) {
            var clock = Stopwatch.StartNew();
            var settings = SettingsFor(o, out string version);
            var samples = BuildSamples(o);
            Parallel.ForEach(samples, new ParallelOptions { MaxDegreeOfParallelism = o.Jobs }, s => {
                try {
                    s.Digest = Digest.Of(s.Source, s.Hash, Pipeline.Execute(s.Program, settings));
                } catch (Exception e) {
                    s.Digest = new Digest { Source = s.Source, Hash = s.Hash, Invalid = true, RuntimeError = "stand: " + e.GetType().Name };
                }
            });
            var file = new DigestFile {
                Flavor = o.Flavor, Config = o.Config, Version = version, Profile = settings.Profile, Mode = o.Mode, Seed = o.Seed, Iterations = o.Iterations,
                ProgramSet = ProgramSetHash(samples), Programs = samples.Select(s => s.Digest).ToList(),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(o.Observe)));
            File.WriteAllText(o.Observe, Json.WriteCompact(file));
            Console.WriteLine($"generator stand: observed {samples.Count} programs on {o.Flavor}-{o.Config} (package {version}, profile {settings.Profile}) in {clock.Elapsed.TotalSeconds:F1} s -> {Report.Rel(o.Root, o.Observe)}");
            return 0;
        }

        /// <summary> Debug view of catalog layouts: what the pipeline and the oracle saw. </summary>
        public static int Show(Options o) {
            var settings = SettingsFor(o, out _);
            string profile = settings.Profile;
            var samples = Catalog.All().Where(s => s.Id.StartsWith(o.Show)).Select(s => (s.Id, s.Title, s.Program, s.Focus)).ToList();
            if (o.Show.StartsWith("fuzz:")) {
                int seed = int.Parse(o.Show.Substring(5));
                samples.Add(("fuzz:" + seed, "fuzz", Fuzzer.Generate(seed, out _), null));
            }
            if (File.Exists(o.Show)) {
                samples.Add((o.Show, "model file", Json.Read<ProgramModel>(File.ReadAllText(o.Show)), null));
            }
            foreach (var (id, title, program, focus) in samples) {
                var obs = Pipeline.Execute(program, settings);
                var contract = new Contract(program, profile, settings.Claims);
                Console.WriteLine($"== {id}: {title}");
                foreach (var c in program.Classes.Where(c => c.IsGenerated)) {
                    Console.WriteLine($"   expect {c.Plain}: {contract.For(c)} [{contract.For(c).Shape}]");
                }
                foreach (var u in obs.Units) {
                    foreach (var d in u.InvalidErrors) {
                        Console.WriteLine($"   INVALID {u.Asm}: {d}");
                    }
                    foreach (var d in u.GeneratorDiagnostics) {
                        Console.WriteLine($"   gen {u.Asm}: {d}");
                    }
                    foreach (var d in u.Compile) {
                        Console.WriteLine($"   cs  {u.Asm}: {d}{(d.Unresolved ? " (unresolved)" : "")}");
                    }
                    if (u.GeneratorException != null) {
                        Console.WriteLine($"   EXC {u.GeneratorException}");
                    }
                }
                foreach (var r in obs.Runs) {
                    Console.WriteLine($"   run {Renderer.ExpectedReproLine(program, r)}");
                    foreach (var w in r.Wrong) {
                        Console.WriteLine($"       wrong {w.Field}: {w.Kind} {w.Mechanism}; expected {w.Expected}, got {w.Actual}");
                    }
                }
                if (obs.RuntimeError != null) {
                    Console.WriteLine($"   RUNTIME {obs.RuntimeError}");
                }
                foreach (var v in Oracle.Check(program, contract, obs)) {
                    Console.WriteLine($"   !! {v} {(KnownIssue.Of(v) is string k ? "(" + k + ")" : "")}");
                }
                Console.WriteLine($"   outcome: {Summary.Outcome(program, obs, focus)}");
                if (Environment.GetEnvironmentVariable("STAND_SHOW_SOURCE") == "1") {
                    Console.WriteLine(Renderer.RenderClean(program));
                    foreach (var u in obs.Units) {
                        foreach (var g in u.Generated) {
                            Console.WriteLine("// generated " + g.Path + "\n" + g.Text);
                        }
                    }
                }
            }
            return 0;
        }

        /// <summary> Comparison of several report folders (known issues and layouts per tree, replay verdicts). </summary>
        public static int Compare(Options o) {
            var reports = new List<(string Dir, JsonElement Root)>();
            var docs = new List<JsonDocument>();
            var skipped = new List<string>();
            foreach (var dir in o.Compare) {
                string path = Path.Combine(dir, "report.json");
                if (!File.Exists(path)) {
                    Console.Error.WriteLine("no report: " + path);
                    continue;
                }
                var doc = JsonDocument.Parse(File.ReadAllText(path));
                docs.Add(doc);
                if (!doc.RootElement.TryGetProperty("format", out var format) || format.GetInt32() < 2) {
                    // Written by an older version of the module (no statuses, no signatures).
                    skipped.Add(Path.GetFileName(dir.TrimEnd('/', '\\')));
                    continue;
                }
                reports.Add((dir, doc.RootElement));
            }
            string text = Report.Compare(reports);
            if (skipped.Count > 0) {
                text += $"\nLeft out (written by an older version of this module; run the module on those trees again): {string.Join(", ", skipped)}.\n";
            }
            string outDir = o.Out ?? Path.GetDirectoryName(Path.GetFullPath(o.Compare[0]));
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "compare.md"), text);
            Console.WriteLine("comparison: " + Report.Rel(o.Root, Path.Combine(outDir, "compare.md")));
            foreach (var d in docs) {
                d.Dispose();
            }
            return 0;
        }
    }
}
