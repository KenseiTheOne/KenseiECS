using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace KenseiStand.Generator {
    /// <summary> One signature (rule, key and place) of a finding, with its hits and its change against the baseline. </summary>
    public sealed class SignatureRow {
        public string Id;
        public string Text;
        public string Place;
        public int CatalogHits;
        public int FuzzHits;
        /// <summary> Against the baseline report: new, spike, same; null without a comparable baseline. </summary>
        public string Change;
        public int? BaselineHits;
    }

    public sealed class Finding {
        public string Id;
        public string GroupKey;
        public string Known;
        public string Class;
        public string Rule;
        public string Severity;
        /// <summary> defect, question, regression or note. </summary>
        public string Status;
        public string Key;
        public string Title;
        public string Message;
        public string Doc;
        public string Claim;
        /// <summary> documented: the sentence of the claim it rests on, as the tree's documents write it. </summary>
        public string Quote;
        public string Downgrade;
        public string Against;
        public string Evidence;
        public string Expected;
        public string ClassId;
        public string RunId;
        public string ClassName;
        public List<string> Sources = new();
        public int CatalogHits;
        public int FuzzHits;
        public List<SignatureRow> Signatures = new();
        public string Exemplar;
        public int? Seed;
        public string Params;
        public string Repro;
        public string ReproModel;
        public string ReproGenerated;
        public string ReproObserved;
        public string ReproStatus;
        public int MinimizeEvaluations;
        public bool Question;
    }

    public sealed class LayoutRow {
        public string Id;
        public string Group;
        public string Title;
        public string Expectation;
        public string Outcome;
        public string Verdict;
        public List<string> Findings = new();
    }

    public sealed class KnownRow {
        public string Id;
        public string Title;
        public string ExpectedSeverity;
        /// <summary> Found through a defect (what the summary's catalog counts as "found"). </summary>
        public bool Detected;
        /// <summary> found (a defect), regression, question or not-found. </summary>
        public string Status;
        public List<string> Findings = new();
        public List<string> Questions = new();
        public List<string> Regressions = new();
        /// <summary> Findings with the status documented (a limitation the documents describe). </summary>
        public List<string> Documented = new();
        public List<string> Witnesses = new();
        public string Note;
    }

    public sealed class CoverageRow {
        public string Shape;
        public int Count;
        public int Flagged;
    }

    public sealed class ReplayRow {
        public string Finding;
        public string Known;
        public string Severity;
        public string Rule;
        public string Key;
        public string Title;
        public string Outcome;
        public string ExpectedHere;
        public string ExpectedThere;
        public bool SameSymptom;
        public bool Regression;
        public string Verdict;
        public List<string> Violations = new();
    }

    public sealed class ReplayBlock {
        public string Of;
        public string OfProfile;
        public string OfDir;
        public List<ReplayRow> Rows = new();
    }

    public sealed class SigChange {
        public string Signature;
        public string Text;
        public string Finding;
        public string Known;
        public string Status;
        public string Title;
        public int Hits;
        public int BaselineHits;
    }

    public sealed class ProgramChange {
        public string Source;
        public List<string> Added = new();
        public List<string> Removed = new();
    }

    /// <summary> Signature-level comparison with the report of a baseline tree. </summary>
    public sealed class BaselineBlock {
        public string Flavor;
        public string Dir;
        public bool Comparable;
        public string Note;
        public List<SigChange> NewSignatures = new();
        public List<SigChange> GoneSignatures = new();
        public List<SigChange> Spikes = new();
        /// <summary> Question signatures the baseline did not have at all (informational: a new open point, not a defect). </summary>
        public List<SigChange> NewQuestions = new();
        public int ProgramsChanged;
        public List<ProgramChange> Programs = new();
    }

    /// <summary> A tree the cross-version rules compared with. </summary>
    public sealed class CrossBlock {
        public string Tag;
        public string Version;
        public string Profile;
        public string Role;
        public int Compared;
        public int MigrationPairs;
        public string Note;
    }

    public sealed class ReportData {
        public string Module = "generator";
        public string Flavor;
        public string Config;
        public bool KenseiDebug;
        public string Profile;
        public string PackageVersion;
        public bool Unreleased;
        public string Src;
        public string GenSrc;
        public string Root;
        public string GeneratorDll;
        public string Roslyn;
        public string Mode;
        public int Seed;
        public int Iterations;
        public string Started;
        public double Seconds;
        public string ProgramSet;
        public Dictionary<string, int> Stats = new();
        public Dictionary<string, int> InvalidByCode = new();
        public List<string> InvalidExamples = new();
        public Dictionary<string, int> Features = new();
        public List<Finding> Findings = new();
        public List<Finding> Regressions = new();
        public List<Finding> Questions = new();
        /// <summary> Behaviour the documents of this tree describe (a documented limitation, such as a lost signal they explain). </summary>
        public List<Finding> Documented = new();
        public List<Finding> Inconclusive = new();
        public List<LayoutRow> Layouts = new();
        public List<KnownRow> Known = new();
        public List<CoverageRow> Coverage = new();
        public List<ReplayBlock> Replays = new();
        public List<Claim> Claims = new();
        public BaselineBlock Baseline;
        public List<CrossBlock> Cross = new();
        /// <summary> source -> signature ids per status (d: defects, r: regressions, q: questions), only programs with any. </summary>
        public SortedDictionary<string, Dictionary<string, List<string>>> Programs = new(StringComparer.Ordinal);
        public List<string> Notes = new();
    }

    public static class Report {
        public static string Tag(ReportData r) => $"{r.Flavor}-{r.Config}";

        private static object Slim(Finding f) => new {
            id = f.Id, status = f.Status, @class = f.Class, rule = f.Rule, severity = f.Severity, known = f.Known, title = f.Title, key = f.Key,
            message = f.Message, contract = f.Doc, claim = f.Claim, quote = f.Quote, downgrade = f.Downgrade, against = f.Against, evidence = f.Evidence,
            expected = f.Expected, classId = f.ClassId, runId = f.RunId, className = f.ClassName,
            sources = f.Sources, catalogHits = f.CatalogHits, fuzzHits = f.FuzzHits,
            signatures = f.Signatures.Select(s => new { id = s.Id, text = s.Text, place = s.Place, catalogHits = s.CatalogHits, fuzzHits = s.FuzzHits, change = s.Change, baselineHits = s.BaselineHits }),
            exemplar = f.Exemplar, seed = f.Seed, @params = f.Params,
            repro = f.Repro, reproModel = f.ReproModel, reproGenerated = f.ReproGenerated, reproObserved = f.ReproObserved,
            reproStatus = f.ReproStatus, minimizeEvaluations = f.MinimizeEvaluations,
        };

        private static object SigJson(SigChange x) => new {
            signature = x.Signature, text = x.Text, finding = x.Finding, known = x.Known, status = x.Status, title = x.Title, hits = x.Hits, baselineHits = x.BaselineHits,
        };

        public static void Write(ReportData r, string dir) {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "report.json"), Json.WriteReport(new {
                module = r.Module, format = 2, flavor = r.Flavor, config = r.Config, kenseiDebug = r.KenseiDebug, profile = r.Profile,
                packageVersion = r.PackageVersion, unreleased = r.Unreleased,
                src = r.Src, genSrc = r.GenSrc, generatorDll = r.GeneratorDll, roslyn = r.Roslyn, mode = r.Mode, seed = r.Seed, iterations = r.Iterations,
                started = r.Started, seconds = Math.Round(r.Seconds, 1), programSet = r.ProgramSet,
                stats = r.Stats, invalidByCode = r.InvalidByCode, invalidExamples = r.InvalidExamples,
                findings = r.Findings.Select(Slim),
                regressions = r.Regressions.Select(Slim),
                questions = r.Questions.Select(Slim),
                documented = r.Documented.Select(Slim),
                inconclusive = r.Inconclusive.Select(Slim),
                known = r.Known.Select(k => new {
                    id = k.Id, title = k.Title, expectedSeverity = k.ExpectedSeverity, detected = k.Detected, status = k.Status,
                    findings = k.Findings, questions = k.Questions, regressions = k.Regressions, documented = k.Documented, witnesses = k.Witnesses, note = k.Note,
                }),
                claims = r.Claims.Select(c => new { id = c.Id, since = c.Since, doc = c.Doc, section = c.Section, quote = c.Quote, active = c.Active, state = c.State, note = c.Note }),
                baseline = r.Baseline == null ? null : new {
                    flavor = r.Baseline.Flavor, dir = r.Baseline.Dir, comparable = r.Baseline.Comparable, note = r.Baseline.Note,
                    newSignatures = r.Baseline.NewSignatures.Select(SigJson), spikes = r.Baseline.Spikes.Select(SigJson), goneSignatures = r.Baseline.GoneSignatures.Select(SigJson),
                    newQuestions = r.Baseline.NewQuestions.Select(SigJson),
                    programsChanged = r.Baseline.ProgramsChanged,
                    programs = r.Baseline.Programs.Select(x => new { source = x.Source, added = x.Added, removed = x.Removed }),
                },
                crossVersion = r.Cross.Select(x => new { tree = x.Tag, version = x.Version, profile = x.Profile, role = x.Role, compared = x.Compared, migrationPairs = x.MigrationPairs, note = x.Note }),
                programs = r.Programs,
                layouts = r.Layouts.Select(l => new { id = l.Id, group = l.Group, title = l.Title, expectation = l.Expectation, outcome = l.Outcome, verdict = l.Verdict, findings = l.Findings }),
                coverage = r.Coverage.Select(c => new { shape = c.Shape, count = c.Count, flagged = c.Flagged }),
                features = r.Features,
                replays = r.Replays.Select(b => new { of = b.Of, ofProfile = b.OfProfile, rows = b.Rows }),
                notes = r.Notes,
            }));
            File.WriteAllText(Path.Combine(dir, "report.md"), Markdown(r));
        }

        private static string Count(int n, string noun) => n + " " + noun + (n == 1 ? "" : "s");

        private static string Esc(string s) => (s ?? "").Replace("|", "\\|").Replace("\n", " ").Replace("\r", " ");

        /// <summary> A path for the markdown: relative to the repository root when it lies inside. </summary>
        public static string Rel(string root, string path) {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) {
                return path;
            }
            string full = Path.GetFullPath(path);
            string r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(r, StringComparison.Ordinal) ? full.Substring(r.Length).Replace('\\', '/') : full;
        }

        private static string StatusText(KnownRow k) => k.Status switch {
            "found" => "**found**",
            "regression" => "**regression**",
            "question" => "question",
            "documented" => "documented",
            "not-checked" => "not checked",
            "not-applicable" => "n/a",
            _ => "not found",
        };

        private static string Markdown(ReportData r) {
            var sb = new StringBuilder();
            sb.AppendLine($"# Stand/generator: {Tag(r)}");
            sb.AppendLine();
            sb.AppendLine($"Systematic and randomized tester of the KenseiECS source generator. Package `{Rel(r.Root, r.Src)}` (version {r.PackageVersion}{(r.Unreleased ? ", CHANGELOG with an [Unreleased] section" : "")}), generator `{Rel(r.Root, r.GenSrc)}`, " +
                          $"configuration `{r.Config}`{(r.KenseiDebug ? " (runtime with KENSEI_DEBUG)" : "")}, contract profile {r.Profile}, Roslyn of the driver {r.Roslyn}. " +
                          $"Mode {r.Mode}: the layout catalog + {r.Iterations} random programs with seeds {r.Seed}..{r.Seed + r.Iterations - 1}; {r.Seconds:F0} s, started {r.Started}.");
            sb.AppendLine();
            sb.AppendLine($"Result: {Count(r.Findings.Count, "defect")}, {Count(r.Regressions.Count, "regression")}, {Count(r.Questions.Count, "contract question")}{(r.Documented.Count > 0 ? ", " + Count(r.Documented.Count, "documented item") : "")}. " +
                          "A defect breaks a promise of this tree's documents; a regression is a change against another tree that no promise forbids; a question is behaviour the documents do not settle; a documented item is behaviour they describe.");
            sb.AppendLine();
            sb.AppendLine("Statistics: " + string.Join(", ", r.Stats.Select(kv => $"{kv.Key} {kv.Value}")) + ".");
            if (r.InvalidByCode.Count > 0) {
                sb.AppendLine();
                sb.AppendLine("Discarded stand programs (they do not compile even without the generator, so they are not findings): " +
                              string.Join(", ", r.InvalidByCode.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} ×{kv.Value}")) + ". Examples: " +
                              string.Join("; ", r.InvalidExamples.Take(6).Select(Esc)) + ".");
            }
            sb.AppendLine();

            sb.AppendLine("## Known issues: GN1–GN8 from the 2.2.0 review, NF1–NF5 found by the stand");
            sb.AppendLine();
            sb.AppendLine("| id | issue | status here | defects | regressions / questions | hits |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var k in r.Known) {
                sb.AppendLine($"| {k.Id} | {Esc(k.Title)} | {StatusText(k)} | {string.Join(", ", k.Findings)} | {string.Join(", ", k.Regressions.Concat(k.Questions).Concat(k.Documented))} | {Esc(k.Note)} |");
            }
            sb.AppendLine();
            sb.AppendLine("Outcome of the witness layouts on this tree:");
            sb.AppendLine();
            foreach (var k in r.Known) {
                sb.AppendLine($"- **{k.Id}**: " + string.Join("; ", k.Witnesses.Select(Esc)));
            }
            sb.AppendLine();

            if (r.Baseline != null) {
                var b = r.Baseline;
                sb.AppendLine($"## Changes against the baseline {b.Flavor}");
                sb.AppendLine();
                sb.AppendLine(b.Comparable
                    ? $"By signature (rule, key and place) and by program, against `{b.Dir}`. A new signature inside a known issue, or a jump in hits (at least twice the baseline and 5 more), is listed even when the known issue was found before."
                    : $"Against `{b.Dir}`: {b.Note}");
                sb.AppendLine();
                if (b.Comparable) {
                    void Table(string title, List<SigChange> rows, bool hits) {
                        sb.AppendLine($"**{title}: {rows.Count}.**");
                        sb.AppendLine();
                        if (rows.Count == 0) {
                            return;
                        }
                        sb.AppendLine(hits ? "| signature | finding | known | status | hits (baseline → here) | what |" : "| signature | finding | known | status | hits | what |");
                        sb.AppendLine("|---|---|---|---|---|---|");
                        foreach (var x in rows) {
                            sb.AppendLine($"| `{Esc(x.Text)}` | {x.Finding} | {x.Known ?? "new"} | {x.Status} | {(hits ? x.BaselineHits + " → " + x.Hits : x.Hits.ToString())} | {Esc(x.Title)} |");
                        }
                        sb.AppendLine();
                    }
                    Table("New signatures", b.NewSignatures, false);
                    Table("Hit spikes", b.Spikes, true);
                    Table("Signatures gone", b.GoneSignatures, false);
                    Table("New question signatures (open points, not defects)", b.NewQuestions, false);
                    sb.AppendLine($"**Programs with a new defect or regression signature: {b.ProgramsChanged}.**" + (b.Programs.Count > 0 ? " The first ones:" : ""));
                    sb.AppendLine();
                    foreach (var pc in b.Programs.Take(20)) {
                        sb.AppendLine($"- `{pc.Source}`: +{string.Join(", +", pc.Added)}" + (pc.Removed.Count > 0 ? $"; gone {string.Join(", ", pc.Removed)}" : ""));
                    }
                    sb.AppendLine();
                }
            }

            if (r.Cross.Count > 0) {
                sb.AppendLine("## Cross-version checks");
                sb.AppendLine();
                sb.AppendLine("The same programs (catalog and seeds) were run on the trees below; migration pairs run the 2.1 layout there and its documented 2.2 upgrade here (R-MIGRATION-ORDER), the other rules look for a lost compiler signal (R-LOST-SIGNAL) and fields injected there but null here (R-REGRESSION-NULL).");
                sb.AppendLine();
                sb.AppendLine("| tree | version | profile | role | programs compared | migration pairs | note |");
                sb.AppendLine("|---|---|---|---|---|---|---|");
                foreach (var x in r.Cross) {
                    sb.AppendLine($"| {x.Tag} | {x.Version} | {x.Profile} | {x.Role} | {x.Compared} | {x.MigrationPairs} | {Esc(x.Note)} |");
                }
                sb.AppendLine();
            }

            foreach (var block in r.Replays) {
                sb.AppendLine($"## Findings of {block.Of} on this tree (the regression question)");
                sb.AppendLine();
                sb.AppendLine($"Every minimal repro from `{Rel(r.Root, block.OfDir)}` was run here and judged by the contract {block.OfProfile} (the same symptom?) and by the contract {r.Profile} (is the behaviour here right?).");
                sb.AppendLine();
                sb.AppendLine("| finding | known | severity | outcome here | same symptom | verdict |");
                sb.AppendLine("|---|---|---|---|---|---|");
                foreach (var row in block.Rows.OrderBy(x => x.Known ?? "ZZ").ThenBy(x => x.Finding)) {
                    sb.AppendLine($"| {row.Finding} | {row.Known ?? "-"} | {row.Severity} | {Esc(row.Outcome)} | {(row.SameSymptom ? "yes" : "no")} | {Esc(row.Verdict)} |");
                }
                sb.AppendLine();
            }

            void FindingTable(string heading, string intro, List<Finding> list) {
                sb.AppendLine("## " + heading);
                sb.AppendLine();
                if (intro != null) {
                    sb.AppendLine(intro);
                    sb.AppendLine();
                }
                if (list.Count == 0) {
                    sb.AppendLine("None.");
                    sb.AppendLine();
                    return;
                }
                sb.AppendLine("| id | known | severity | class | rule | what | catalog / fuzz | signatures | repro |");
                sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
                foreach (var f in list) {
                    string sigs = f.Signatures.Count + (f.Signatures.Any(s => s.Change == "new") ? $" ({f.Signatures.Count(s => s.Change == "new")} new)" : "");
                    sb.AppendLine($"| {f.Id} | {f.Known ?? "**new**"} | {f.Severity} | {f.Class} | {f.Rule} | {Esc(f.Title)} | {f.CatalogHits} / {f.FuzzHits} | {sigs} | [{Path.GetFileName(f.Repro)}]({f.Repro}) |");
                }
                sb.AppendLine();
                foreach (var f in list) {
                    WriteFinding(sb, f);
                }
            }
            FindingTable("Defects", null, r.Findings);
            FindingTable("Regressions", "Changes against another tree that no promise of this tree's documents forbids: not defects, but worth a look before a release.", r.Regressions);

            sb.AppendLine("## Contract questions");
            sb.AppendLine();
            sb.AppendLine("Behaviour the documents of this tree (XML docs, docs/, CHANGELOG) do not settle, or a promise this tree does not make (yet): not defects.");
            sb.AppendLine();
            if (r.Questions.Count == 0) {
                sb.AppendLine("None.");
            } else {
                foreach (var q in r.Questions) {
                    sb.AppendLine($"- **{q.Id}**{(q.Known != null ? " (" + q.Known + ")" : "")} ({q.CatalogHits} catalog layouts, {q.FuzzHits} random programs): {Esc(q.Title)}. {Esc(q.Message)} Contract: {Esc(q.Doc)}.{(q.Downgrade != null ? " Why a question: " + Esc(q.Downgrade) + "." : "")} Repro: [{q.Repro}]({q.Repro}); where: {string.Join("; ", q.Sources.Take(4))}.");
                }
            }
            sb.AppendLine();
            if (r.Documented.Count > 0) {
                sb.AppendLine("## Documented");
                sb.AppendLine();
                sb.AppendLine("Behaviour the documents of this tree describe as it is (a documented limitation): neither a defect nor a regression.");
                sb.AppendLine();
                foreach (var q in r.Documented) {
                    sb.AppendLine($"- **{q.Id}**{(q.Known != null ? " (" + q.Known + ")" : "")} ({q.CatalogHits} catalog layouts, {q.FuzzHits} random programs): {Esc(q.Title)}. {Esc(q.Message)} Documents: {Esc(q.Doc)}. Repro: [{q.Repro}]({q.Repro}).");
                }
                sb.AppendLine();
            }
            if (r.Inconclusive.Count > 0) {
                sb.AppendLine("## Not checked");
                sb.AppendLine();
                foreach (var q in r.Inconclusive) {
                    sb.AppendLine($"- {q.Id}: {Esc(q.Message)} ({string.Join("; ", q.Sources.Take(4))})");
                }
                sb.AppendLine();
            }

            sb.AppendLine("## Contract claims");
            sb.AppendLine();
            sb.AppendLine($"The promises the rules rest on (Claims.cs), judged against this tree (package {r.PackageVersion}). A rule whose claim is not active reports questions instead of defects.");
            sb.AppendLine();
            sb.AppendLine("| claim | since | document | state | note |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var c in r.Claims) {
                sb.AppendLine($"| `{c.Id}` | {c.Since} | {c.Doc}, {Esc(c.Section)} | {c.State} | {Esc(c.Note)} |");
            }
            sb.AppendLine();

            sb.AppendLine("## Catalog layouts");
            sb.AppendLine();
            sb.AppendLine("The expectation comes from the contract model (docs/guides/source-generator.md, CHANGELOG), not from the generator; the outcome is what the generator of this tree did and what the SystemsRunner.Init run showed.");
            sb.AppendLine();
            foreach (var group in r.Layouts.GroupBy(l => l.Group)) {
                sb.AppendLine($"### {group.Key}");
                sb.AppendLine();
                sb.AppendLine("| layout | program | expectation | outcome | verdict |");
                sb.AppendLine("|---|---|---|---|---|");
                foreach (var l in group) {
                    sb.AppendLine($"| `{l.Id}` | {Esc(l.Title)} | {Esc(l.Expectation)} | {Esc(l.Outcome)} | {Esc(l.Verdict)} |");
                }
                sb.AppendLine();
            }

            sb.AppendLine("## Coverage of the random programs");
            sb.AppendLine();
            sb.AppendLine("Layout shapes of the generated classes by the contract model (how often each occurred, how many had a defect):");
            sb.AppendLine();
            sb.AppendLine("| shape | classes | with a defect |");
            sb.AppendLine("|---|---|---|");
            foreach (var c in r.Coverage) {
                sb.AppendLine($"| `{Esc(c.Shape)}` | {c.Count} | {c.Flagged} |");
            }
            sb.AppendLine();
            sb.AppendLine("Program features: " + string.Join(", ", r.Features.Select(kv => $"{kv.Key} ×{kv.Value}")) + ".");
            sb.AppendLine();

            if (r.Notes.Count > 0) {
                sb.AppendLine("## Limitations and notes");
                sb.AppendLine();
                foreach (var n in r.Notes) {
                    sb.AppendLine("- " + n);
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private static void WriteFinding(StringBuilder sb, Finding f) {
            sb.AppendLine($"### {f.Id}{(f.Known != null ? " (" + f.Known + ")" : " (new)")}: {f.Title}");
            sb.AppendLine();
            sb.AppendLine($"- Status {f.Status}; class `{f.Class}`, rule `{f.Rule}`, severity {f.Severity} (an estimate), key `{f.Key}`{(f.Claim != null ? $", claim `{f.Claim}`" : "")}.");
            sb.AppendLine($"- Expected by the contract: {f.Expected}. Source: {f.Doc}.");
            sb.AppendLine($"- Observed (minimal repro): {f.Message}");
            if (f.Evidence != null) {
                sb.AppendLine($"- Other tree ({f.Against}): {f.Evidence}");
            }
            sb.AppendLine($"- Signatures (catalog/fuzz hits): {string.Join("; ", f.Signatures.Select(s => $"`{s.Id}` @{s.Place} ({s.CatalogHits}/{s.FuzzHits}{(s.Change != null ? ", " + s.Change + (s.BaselineHits != null ? ", baseline " + s.BaselineHits : "") : "")})"))}.");
            sb.AppendLine($"- Found in: {string.Join("; ", f.Sources)}{(f.CatalogHits + f.FuzzHits > f.Sources.Count ? $" ({f.CatalogHits} catalog layouts and {f.FuzzHits} random programs in all)" : "")}.");
            sb.AppendLine($"- Original program: {f.Params}.");
            sb.AppendLine($"- Repro: [{f.Repro}]({f.Repro}) (model [{Path.GetFileName(f.ReproModel)}]({f.ReproModel}), generated code [{Path.GetFileName(f.ReproGenerated)}]({f.ReproGenerated}), observation [{Path.GetFileName(f.ReproObserved)}]({f.ReproObserved})); minimized in {f.MinimizeEvaluations} runs; repro check: {f.ReproStatus}.");
            sb.AppendLine();
        }

        public static void WriteReplay(ReportData here, ReplayBlock block, string dir) {
            Directory.CreateDirectory(dir);
            string name = $"replay-of-{block.Of}";
            File.WriteAllText(Path.Combine(dir, name + ".json"), Json.WriteReport(new { of = block.Of, ofProfile = block.OfProfile, here = Tag(here), hereProfile = here.Profile, rows = block.Rows }));
        }

        /// <summary> Known issues and layouts across trees, and the replay verdicts (regressions). </summary>
        public static string Compare(List<(string Dir, JsonElement Root)> reports) {
            var sb = new StringBuilder();
            var tags = reports.Select(r => r.Root.GetProperty("flavor").GetString() + "-" + r.Root.GetProperty("config").GetString()).ToList();
            sb.AppendLine("# Stand/generator: comparison of trees");
            sb.AppendLine();
            sb.AppendLine("Reports: " + string.Join(", ", reports.Select((r, i) => $"[{tags[i]}]({Path.GetFileName(r.Dir)}/report.md)")));
            sb.AppendLine();
            sb.AppendLine("## Known issues per tree");
            sb.AppendLine();
            sb.AppendLine("| id | " + string.Join(" | ", tags) + " |");
            sb.AppendLine("|---|" + string.Concat(tags.Select(_ => "---|")));
            var knownIds = reports.SelectMany(r => r.Root.GetProperty("known").EnumerateArray().Select(k => k.GetProperty("id").GetString())).Distinct();
            foreach (var id in knownIds) {
                var cells = reports.Select(r => {
                    var k = r.Root.GetProperty("known").EnumerateArray().FirstOrDefault(x => x.GetProperty("id").GetString() == id);
                    if (k.ValueKind == JsonValueKind.Undefined) {
                        return "?";
                    }
                    var findings = string.Join(", ", k.GetProperty("findings").EnumerateArray().Select(x => x.GetString()));
                    string status = k.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : (k.GetProperty("detected").GetBoolean() ? "found" : "not-found");
                    return status switch {
                        "found" => "**found** (" + findings + ")",
                        "regression" => "regression",
                        "question" => "question",
                        "documented" => "documented",
                        "not-checked" => "not checked",
                        "not-applicable" => "n/a",
                        _ => "no",
                    };
                });
                sb.AppendLine($"| {id} | {string.Join(" | ", cells)} |");
            }
            sb.AppendLine();
            sb.AppendLine("## Findings per tree");
            sb.AppendLine();
            var findingRows = new Dictionary<string, (string Known, string Severity, string Title, Dictionary<string, string> Cells)>();
            // Reports of the current format first, so titles and labels come from the newest wording.
            var order = Enumerable.Range(0, reports.Count).OrderByDescending(i => reports[i].Root.TryGetProperty("format", out var fm) ? fm.GetInt32() : 1).ThenBy(i => i).ToList();
            foreach (int i in order) {
                foreach (var list in new[] { "findings", "regressions" }) {
                    if (!reports[i].Root.TryGetProperty(list, out var items)) {
                        continue;
                    }
                    foreach (var f in items.EnumerateArray()) {
                        string id = f.GetProperty("id").GetString();
                        if (!findingRows.TryGetValue(id, out var row)) {
                            row = (f.GetProperty("known").ValueKind == JsonValueKind.String ? f.GetProperty("known").GetString() : null, f.GetProperty("severity").GetString(), f.GetProperty("title").GetString(), new Dictionary<string, string>());
                            findingRows[id] = row;
                        }
                        row.Cells[tags[i]] = $"{(list == "regressions" ? "regression" : "yes")} ({f.GetProperty("catalogHits").GetInt32()}/{f.GetProperty("fuzzHits").GetInt32()})";
                    }
                }
            }
            sb.AppendLine("| finding | known | severity | what | " + string.Join(" | ", tags) + " |");
            sb.AppendLine("|---|---|---|---|" + string.Concat(tags.Select(_ => "---|")));
            foreach (var kv in findingRows.OrderBy(x => x.Value.Known ?? "ZZ").ThenBy(x => Driver.SeverityRank(x.Value.Severity)).ThenBy(x => x.Key)) {
                sb.AppendLine($"| {kv.Key} | {kv.Value.Known ?? "new"} | {kv.Value.Severity} | {Esc(kv.Value.Title)} | {string.Join(" | ", tags.Select(t => kv.Value.Cells.TryGetValue(t, out var c) ? c : "no"))} |");
            }
            sb.AppendLine();
            foreach (var (dir, root) in reports) {
                if (!root.TryGetProperty("replays", out var replays)) {
                    continue;
                }
                foreach (var block in replays.EnumerateArray()) {
                    string here = root.GetProperty("flavor").GetString() + "-" + root.GetProperty("config").GetString();
                    sb.AppendLine($"## Findings of {block.GetProperty("of").GetString()}, replayed on {here}");
                    sb.AppendLine();
                    sb.AppendLine("| finding | known | outcome on " + here + " | verdict |");
                    sb.AppendLine("|---|---|---|---|");
                    foreach (var row in block.GetProperty("rows").EnumerateArray().OrderBy(x => x.GetProperty("Known").ValueKind == JsonValueKind.String ? x.GetProperty("Known").GetString() : "ZZ")) {
                        string known = row.GetProperty("Known").ValueKind == JsonValueKind.String ? row.GetProperty("Known").GetString() : "-";
                        sb.AppendLine($"| {row.GetProperty("Finding").GetString()} | {known} | {Esc(row.TryGetProperty("Outcome", out var o) ? o.GetString() : "")} | {Esc(row.GetProperty("Verdict").GetString())} |");
                    }
                    sb.AppendLine();
                }
            }
            sb.AppendLine("## Catalog layouts with different outcomes");
            sb.AppendLine();
            sb.AppendLine("| layout | " + string.Join(" | ", tags) + " |");
            sb.AppendLine("|---|" + string.Concat(tags.Select(_ => "---|")));
            var rows = reports.Select(r => r.Root.GetProperty("layouts").EnumerateArray()
                .ToDictionary(l => l.GetProperty("id").GetString(), l => l.GetProperty("outcome").GetString() + " — " + l.GetProperty("verdict").GetString())).ToList();
            foreach (var id in rows.SelectMany(d => d.Keys).Distinct()) {
                var cells = rows.Select(d => d.TryGetValue(id, out var v) ? v : "-").ToList();
                if (cells.Distinct().Count() == 1) {
                    continue;
                }
                sb.AppendLine($"| `{id}` | {string.Join(" | ", cells.Select(Esc))} |");
            }
            return sb.ToString();
        }
    }
}
