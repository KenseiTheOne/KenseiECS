using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Stand.Contracts {
    public sealed class Meta {
        public string Module = "contracts";
        public string Flavor;
        public string Cfg;
        public bool KenseiDebug;          // detected from the build, not taken from the flag
        public string Src;
        public string PackageVersion;     // package.json of the tree; null when there is none
        public string Docs;
        public string DocsSource;         // tree | archive | given | none
        public int DocsPages;
        public int XmlMembers;
        public string Mode;
        public int Seed;
        public int Cases;
        public int Members;
        public int Targets;
        public string Started;
        public double Seconds;
        public string Runtime;
        public int WorkerRestarts;
        public int MemoryRestarts;
        public int SecondOrderSeed;       // --full: seed of the second case order, 0 when there was none
        public string Baseline;
    }

    public sealed class ClaimStat {
        public string Id;
        public string Kind;        // Call | State | Partial | Diagnostic
        public string Quote;
        public string Since;
        public string State;       // active | profile | later | missing
        public string ActiveAt;    // where the wording was found (active), or why it is not held (the others)
        public int Cases;
        public int Violations;
    }

    public sealed class ExceptionText {
        public string Member;
        public string Scenario;
        public string Variant;
        public string Exception;
        public string Message;
        public string Thrower;
        public string Verdict;     // names-operation | names-other | names-parameter | names-nothing | raw-runtime
        public string Note;
    }

    public sealed class CellDiff {
        public string Key;
        public string Before;
        public string After;
        public string Change;      // changed | added | removed
    }

    /// <summary> One mechanism and place, with how many cases or checks fall into it. </summary>
    public sealed class SignatureStat {
        public string Signature;
        public string Kind;        // defect | question | documented
        public string Known;
        public int Hits;
        public List<string> Ids = new();
    }

    /// <summary> Signatures against the baseline: new ones (also inside a known family), gone ones, and sharp growth of the hits. </summary>
    public sealed class SignatureDiff {
        public List<SignatureStat> New = new();
        public List<SignatureStat> Gone = new();
        public List<SignatureGrowth> Grown = new();
        public string Note;
    }

    public sealed class SignatureGrowth {
        public string Signature;
        public string Kind;
        public string Known;
        public int Before;
        public int After;
    }

    public sealed class ReportModel {
        public Meta Meta = new();
        public Dictionary<string, int> Verdicts = new();
        public List<Finding> Findings = new();
        public List<Finding> Questions = new();
        public List<Finding> Documented = new();
        public List<Finding> Regressions = new();
        public List<Finding> Diagnostics = new();
        public DiagResult Diag;
        public List<ClaimStat> Claims = new();
        public List<string> ClaimWarnings = new();
        public List<string> StandErrors = new();     // checks the stand could not run: their absence is no green
        public List<SignatureStat> Signatures = new();
        public List<string> NotRun = new();
        public List<string> UnknownParams = new();   // parameters synthesized as null: types the factory does not know
        public List<string> Unstable = new();        // --full: cells whose outcome depends on the case order
        public List<KnownStatus> Known = new();
        public List<ExceptionText> ExceptionTexts = new();
        public List<CellDiff> BaselineChanges;
        public FindingDiff BaselineFindings;
        public SignatureDiff BaselineSignatures;
        public string BaselineNote;
    }

    /// <summary> Defects of the baseline run that are gone, defects that are new, and those in both (by id). </summary>
    public sealed class FindingDiff {
        public List<string> Fixed = new();      // "id (known)": in the baseline, not here
        public List<string> New = new();        // here, not in the baseline
        public List<string> Kept = new();       // in both
    }

    public sealed class MatrixCell {
        public string Key;
        public string Target;
        public string Member;
        public string Scenario;
        public string Variant;
        public string Cls;
        public string Verdict;
        public string Rules;
        public string Outcome;
        public string Message;
        public string Site;        // the first KenseiECS frame of the exception
    }

    public sealed class MatrixFile {
        public Meta Meta;
        public List<MatrixCell> Cells = new();
    }

    /// <summary> report.md, matrix.md, matrix.json and the comparison with a baseline. </summary>
    public static class Report {
        public static string Code(CaseResult r) {
            if (r.Verdict == Verdict.NotRun) {
                return "n/r";
            }
            string cls = r.Outcome?.Class ?? "?";
            switch (r.Verdict) {
                case Verdict.Finding: return cls + " !";
                case Verdict.Question: return cls + " ?";
                case Verdict.Doc: return cls + "(d)";
                case Verdict.Undefined: return cls + "(u)";
                default: return cls;
            }
        }

        public static MatrixFile BuildMatrix(Meta meta, List<CaseResult> results) {
            var m = new MatrixFile { Meta = meta };
            foreach (var r in results.OrderBy(x => x.Key, StringComparer.Ordinal)) {
                m.Cells.Add(new MatrixCell {
                    Key = r.Key, Target = r.Target, Member = r.Member, Scenario = r.Scenario, Variant = r.Variant,
                    Cls = r.Verdict == Verdict.NotRun ? "n/r" : r.Outcome?.Class, Verdict = r.Verdict.ToString(),
                    Rules = string.Join(",", r.Judgements.Where(j => j.Verdict >= Verdict.Question).Select(j => j.Rule + (j.ClaimId != null ? ":" + j.ClaimId : "")).Distinct()),
                    Outcome = r.Outcome == null ? null : r.Outcome.Threw ? r.Outcome.ExShort : "ok",
                    Message = r.Outcome?.Message,
                    Site = r.Outcome?.Site
                });
            }
            return m;
        }

        public static string RenderMatrix(MatrixFile m, List<CaseResult> results) {
            var sb = new StringBuilder();
            sb.AppendLine("# Contract matrix: " + m.Meta.Flavor + "-" + m.Meta.Cfg);
            sb.AppendLine();
            sb.AppendLine("A cell is the outcome class over every argument variant of the scenario (`ok`, `ODE`, `IOE`, `Arg`, `ArgRange`, `ArgNull`, `ArgNull(no-null)`, `KeyNotFound`, `NRE`, `IndexOOR`, `Cast`, `OOM`, `Other:<type>`).");
            sb.AppendLine("Marks: ` !` defect, ` ?` contract question, `(d)` suspicious but documented, `(u)` undefined by the docs, no mark: fine; `n/r`: not called. `xN`: that many variants had this outcome.");
            sb.AppendLine();
            foreach (var target in results.Select(r => r.Target).Distinct()) {
                var rows = results.Where(r => r.Target == target).ToList();
                var scenarios = Catalog.AllScenarios.Where(s => rows.Any(r => r.Scenario == s)).ToList();
                sb.AppendLine("## " + target);
                sb.AppendLine();
                sb.AppendLine("| member | " + string.Join(" | ", scenarios) + " |");
                sb.AppendLine("|---|" + string.Join("|", scenarios.Select(_ => "---")) + "|");
                foreach (var member in rows.Select(r => r.Member).Distinct().OrderBy(x => x, StringComparer.Ordinal)) {
                    var cells = new List<string>();
                    foreach (var s in scenarios) {
                        var vs = rows.Where(r => r.Member == member && r.Scenario == s).ToList();
                        if (vs.Count == 0) {
                            cells.Add("");
                            continue;
                        }
                        var parts = vs.GroupBy(Code).OrderByDescending(g => g.Max(x => (int)x.Verdict)).ThenBy(g => g.Key)
                            .Select(g => g.Count() > 1 ? g.Key + " x" + g.Count() : g.Key);
                        cells.Add(string.Join(", ", parts));
                    }
                    sb.AppendLine("| `" + Md(member) + "` | " + string.Join(" | ", cells.Select(Md)) + " |");
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        public static List<CellDiff> Compare(MatrixFile before, MatrixFile after) {
            var a = before.Cells.GroupBy(c => c.Key).ToDictionary(g => g.Key, g => g.First());
            var b = after.Cells.GroupBy(c => c.Key).ToDictionary(g => g.Key, g => g.First());
            var list = new List<CellDiff>();
            foreach (var kv in b) {
                if (!a.TryGetValue(kv.Key, out var old)) {
                    list.Add(new CellDiff { Key = kv.Key, Before = null, After = Cell(kv.Value), Change = "added" });
                } else if (Cell(old) != Cell(kv.Value)) {
                    list.Add(new CellDiff { Key = kv.Key, Before = Cell(old), After = Cell(kv.Value), Change = "changed" });
                }
            }
            foreach (var kv in a) {
                if (!b.ContainsKey(kv.Key)) {
                    list.Add(new CellDiff { Key = kv.Key, Before = Cell(kv.Value), After = null, Change = "removed" });
                }
            }
            list.Sort((x, y) => string.CompareOrdinal(x.Key, y.Key));
            return list;
        }

        public static string Cell(MatrixCell c) => c.Cls + " " + c.Verdict + (string.IsNullOrEmpty(c.Rules) ? "" : " [" + c.Rules + "]");

        public static FindingDiff CompareFindings(ReportModel before, ReportModel after) {
            List<Finding> Defects(ReportModel m) => m.Findings.Concat(m.Diagnostics.Where(d => d.Kind == "defect")).ToList();
            string Label(Finding f) => f.Id + (f.Known != null ? " (" + f.Known + ")" : "");
            var a = Defects(before);
            var b = Defects(after);
            var d = new FindingDiff();
            d.Fixed.AddRange(a.Where(x => b.All(y => y.Id != x.Id)).Select(Label));
            d.New.AddRange(b.Where(x => a.All(y => y.Id != x.Id)).Select(Label));
            d.Kept.AddRange(b.Where(x => a.Any(y => y.Id == x.Id)).Select(Label));
            return d;
        }

        /// <summary> A sharp growth of a signature's hits: at least twice as many and at least three more. </summary>
        public static bool SharpGrowth(int before, int after) => after >= 2 * before && after - before >= 3;

        public static SignatureDiff CompareSignatures(List<SignatureStat> before, List<SignatureStat> after) {
            var d = new SignatureDiff();
            if (before == null || before.Count == 0) {
                d.Note = "the baseline report has no signatures (written by an older stand): run the module on the baseline again";
                return d;
            }
            var a = before.GroupBy(s => s.Signature).ToDictionary(g => g.Key, g => g.First());
            var b = after.GroupBy(s => s.Signature).ToDictionary(g => g.Key, g => g.First());
            d.New.AddRange(after.Where(s => !a.ContainsKey(s.Signature)));
            d.Gone.AddRange(before.Where(s => !b.ContainsKey(s.Signature)));
            foreach (var s in after) {
                if (a.TryGetValue(s.Signature, out var old) && SharpGrowth(old.Hits, s.Hits)) {
                    d.Grown.Add(new SignatureGrowth { Signature = s.Signature, Kind = s.Kind, Known = s.Known, Before = old.Hits, After = s.Hits });
                }
            }
            return d;
        }

        public static string RenderBaseline(ReportModel model) {
            var m = model.Meta;
            var sb = new StringBuilder();
            sb.AppendLine("# Changes: " + m.Baseline + " -> " + m.Flavor + " (" + m.Cfg + ")");
            sb.AppendLine();
            var findings = model.BaselineFindings;
            if (findings != null) {
                sb.AppendLine("## Defects");
                sb.AppendLine();
                sb.AppendLine("- Gone (were in " + m.Baseline + "): " + List(findings.Fixed));
                sb.AppendLine("- New (not in " + m.Baseline + "): " + List(findings.New));
                sb.AppendLine("- Kept: " + List(findings.Kept));
                sb.AppendLine();
            }
            RenderSignatureDiff(sb, model);
            RenderRegressions(sb, model, 200);
            var changes = model.BaselineChanges ?? new List<CellDiff>();
            sb.AppendLine("## Matrix cells");
            sb.AppendLine();
            if (changes.Count == 0) {
                sb.AppendLine("No cell changed.");
                return sb.ToString();
            }
            foreach (var group in changes.GroupBy(c => c.Change)) {
                sb.AppendLine("### " + (group.Key == "changed" ? "Changed" : group.Key == "added" ? "New (not in " + m.Baseline + ")" : "Gone (were in " + m.Baseline + ")") + ": " + group.Count());
                sb.AppendLine();
                sb.AppendLine("| cell | " + m.Baseline + " | " + m.Flavor + " |");
                sb.AppendLine("|---|---|---|");
                foreach (var c in group) {
                    sb.AppendLine("| `" + Md(c.Key) + "` | " + Md(c.Before ?? "-") + " | " + Md(c.After ?? "-") + " |");
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private static string List(List<string> items) => items.Count == 0 ? "none" : string.Join(", ", items.Select(x => "`" + Md(x) + "`"));

        private static void RenderSignatureDiff(StringBuilder sb, ReportModel model) {
            var d = model.BaselineSignatures;
            if (d == null) {
                return;
            }
            sb.AppendLine("## Signatures (mechanism and place) against " + model.Meta.Baseline);
            sb.AppendLine();
            if (d.Note != null) {
                sb.AppendLine(d.Note + ".");
                sb.AppendLine();
                return;
            }
            sb.AppendLine("A new signature is a new mechanism or place, also inside a known family; a sharp growth is at least twice as many hits and at least three more.");
            sb.AppendLine();
            sb.AppendLine("- New: " + (d.New.Count == 0 ? "none" : d.New.Count.ToString()));
            foreach (var s in d.New) {
                sb.AppendLine("  - " + s.Kind + (s.Known != null ? " in known family " + s.Known : "") + ", hits " + s.Hits + ": `" + Md(s.Signature) + "` (" + string.Join(", ", s.Ids.Take(3).Select(x => "`" + Md(x) + "`")) + ")");
            }
            sb.AppendLine("- Gone: " + (d.Gone.Count == 0 ? "none" : d.Gone.Count.ToString()));
            foreach (var s in d.Gone) {
                sb.AppendLine("  - " + s.Kind + (s.Known != null ? " (" + s.Known + ")" : "") + ", hits " + s.Hits + ": `" + Md(s.Signature) + "`");
            }
            sb.AppendLine("- Sharp growth: " + (d.Grown.Count == 0 ? "none" : d.Grown.Count.ToString()));
            foreach (var s in d.Grown) {
                sb.AppendLine("  - " + s.Kind + (s.Known != null ? " (" + s.Known + ")" : "") + ", hits " + s.Before + " -> " + s.After + ": `" + Md(s.Signature) + "`");
            }
            sb.AppendLine();
        }

        private static void RenderRegressions(StringBuilder sb, ReportModel model, int limit) {
            if (model.Meta.Baseline == null || model.BaselineNote != null) {
                return;
            }
            sb.AppendLine("## Regressions against " + model.Meta.Baseline);
            sb.AppendLine();
            sb.AppendLine("A regression is a case that was fine (or a question of another kind) on " + model.Meta.Baseline + " and is now suspicious, while no document of this version promises otherwise: not a defect, not a plain question.");
            sb.AppendLine();
            if (model.Regressions.Count == 0) {
                sb.AppendLine("None.");
                sb.AppendLine();
                return;
            }
            sb.AppendLine("| id | known | cases | " + model.Meta.Baseline + " | " + model.Meta.Flavor + " | what | repro |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var f in model.Regressions.Take(limit)) {
                sb.AppendLine("| `" + Md(f.Id) + "` | " + (f.Known ?? "-") + " | " + f.Hits + " | " + Md(f.Expected) + " | " + Md(f.Outcome) + " | " + Md(Trim(f.Detail, 220)) + " | "
                    + (f.Repro != null ? "[repro](" + f.Repro + ")" : "-") + " |");
            }
            sb.AppendLine();
        }

        // =====================================================================
        // report.md
        // =====================================================================

        public static string Render(ReportModel model) {
            var m = model.Meta;
            var sb = new StringBuilder();
            sb.AppendLine("# Stand/contracts: " + m.Flavor + "-" + m.Cfg);
            sb.AppendLine();
            sb.AppendLine("- Package sources: `" + DisplayPath(m.Src) + "` (label `" + m.Flavor + "`, package.json version " + (m.PackageVersion ?? "unknown") + ").");
            sb.AppendLine("- KENSEI_DEBUG in the build: " + (m.KenseiDebug ? "yes" : "no") + " (detected from the build: the EcsProfiler type " + (m.KenseiDebug ? "exists" : "is absent") + ").");
            sb.AppendLine("- Mode " + m.Mode + ", seed " + m.Seed + ". Cases: " + m.Cases + ", API members: " + m.Members + " in " + m.Targets + " targets. Time: " + m.Seconds.ToString("0.0") + " s. Worker restarts: " + m.WorkerRestarts + ".");
            sb.AppendLine("- Contract: the XML docs of this tree (" + m.XmlMembers + " members), docs/ pages: " + m.DocsPages
                + (m.Docs != null ? " (`" + DisplayPath(m.Docs) + "`, " + m.DocsSource + ")" : " (not found: docs-page promises come from the version profile)") + ", the package CHANGELOG.");
            sb.AppendLine("- Matrix: [matrix.md](matrix.md), [matrix.json](matrix.json). Raw cases: `cases.ndjson`.");
            sb.AppendLine();

            sb.AppendLine("## Summary");
            sb.AppendLine();
            sb.AppendLine("| verdict | cases |");
            sb.AppendLine("|---|---|");
            foreach (var kv in model.Verdicts.OrderByDescending(x => x.Key)) {
                sb.AppendLine("| " + kv.Key + " | " + kv.Value + " |");
            }
            sb.AppendLine();
            var defects = model.Findings.Concat(model.Diagnostics.Where(d => d.Kind == "defect")).ToList();
            sb.AppendLine("Defects: **" + defects.Count + "**" + (defects.Count > 0 ? " (" + string.Join(", ", defects.GroupBy(f => f.Severity).OrderBy(g => SevRank(g.Key)).Select(g => g.Key + " " + g.Count())) + ")" : "")
                + ". Contract questions: " + model.Questions.Count + ". Documented: " + model.Documented.Count + "."
                + (m.Baseline != null && model.BaselineNote == null ? " Regressions against " + m.Baseline + ": " + model.Regressions.Count + "." : ""));
            sb.AppendLine();
            sb.AppendLine("A defect contradicts a promise written in the documentation of this version. A question is suspicious, but no document of this version promises otherwise (also a promise made only by a later version). Documented: suspicious on its face, but described by the docs.");
            sb.AppendLine();
            if (defects.Count > 0) {
                sb.AppendLine("| id | known | class | rule | severity | hits | repro | repro check |");
                sb.AppendLine("|---|---|---|---|---|---|---|---|");
                foreach (var f in defects) {
                    sb.AppendLine("| `" + Md(f.Id) + "` | " + (f.Known ?? "new") + " | " + f.Class + " | " + f.Rule + " | " + f.Severity + " | " + f.Hits + " | "
                        + (f.Repro != null ? "[" + Md(f.Repro) + "](" + f.Repro + ")" : "-") + " | " + (f.ReproStatus ?? "-") + " |");
                }
                sb.AppendLine();
            }
            if (model.StandErrors.Count > 0) {
                sb.AppendLine("**Stand errors:** checks that could not run, so their silence is not a pass:");
                foreach (var e in model.StandErrors) {
                    sb.AppendLine("- " + Md(e));
                }
                sb.AppendLine();
            }
            if (model.ClaimWarnings.Count > 0) {
                sb.AppendLine("**Stand warnings:** " + model.ClaimWarnings.Count + " promise(s) the version profile expects are worded differently in these docs, so they are held as questions only:");
                foreach (var w in model.ClaimWarnings) {
                    sb.AppendLine("- " + Md(w));
                }
                sb.AppendLine();
            }

            if (model.Known.Count > 0) {
                sb.AppendLine("## Known problems");
                sb.AppendLine();
                sb.AppendLine("The problems this module was built for. found: a defect; regression: it was fine on the baseline and shows now, but the docs of this version do not forbid it; question: it shows, but the docs of this version do not forbid it; documented: the docs describe it; not found: the check passed; not applicable: it can only show in the other build; not checked: this version lacks the API it is checked on. More than one signature means another mechanism inside the family.");
                sb.AppendLine();
                sb.AppendLine("| id | what | found in | this run | entries | signatures |");
                sb.AppendLine("|---|---|---|---|---|---|");
                foreach (var k in model.Known) {
                    string status = k.Status == "found" ? "**found**" : k.Status == "not-found" ? "not found"
                        : k.Status == "regression" ? "**regression**: " + k.Note
                        : k.Status == "question" ? "question" + (k.Note != null ? ": " + Trim(k.Note, 160) : "")
                        : k.Status == "documented" ? "documented" + (k.Note != null ? ": " + Trim(k.Note, 160) : "")
                        : k.Status == "not-checked" ? "not checked: " + k.Note
                        : "not applicable in this build" + (k.Note != null ? ": " + k.Note : "");
                    var ids = k.Findings.Concat(k.Regressions).Concat(k.Questions).Concat(k.Documented).ToList();
                    sb.AppendLine("| " + k.Id + " | " + Md(k.Title) + " | " + Md(k.FoundIn) + " | " + Md(status) + " | " + (ids.Count > 0 ? string.Join(", ", ids.Take(4).Select(x => "`" + Md(x) + "`")) + (ids.Count > 4 ? " (+" + (ids.Count - 4) + ")" : "") : "-") + " | " + k.Signatures.Count + " |");
                }
                sb.AppendLine();
            }

            sb.AppendLine("## Defects");
            sb.AppendLine();
            if (defects.Count == 0) {
                sb.AppendLine("None.");
                sb.AppendLine();
            }
            foreach (var f in defects) {
                RenderFinding(sb, f);
            }

            RenderRegressions(sb, model, 60);

            sb.AppendLine("## Contract questions");
            sb.AppendLine();
            sb.AppendLine("Suspicious outcomes (NRE, IndexOutOfRange, an unexpected type, a state that answers differently after a failure, a promise only a later version makes) that break no promise written in the docs of this version. Not defects: the contract's author decides.");
            sb.AppendLine();
            if (model.Questions.Count == 0) {
                sb.AppendLine("None.");
            } else {
                sb.AppendLine("| scenario | member | known | outcome | why a question | repro |");
                sb.AppendLine("|---|---|---|---|---|---|");
                foreach (var q in model.Questions.OrderBy(x => x.Scenario).ThenBy(x => x.Member, StringComparer.Ordinal)) {
                    sb.AppendLine("| " + q.Scenario + " | `" + Md(q.Member) + "`" + (q.AlsoVia.Count > 0 ? " (+" + q.AlsoVia.Count + " var.)" : "") + " | " + (q.Known ?? "-") + " | " + Md(q.Outcome) + " | " + Md(Trim(q.Detail, 400)) + " | "
                        + (q.Repro != null ? "[repro](" + q.Repro + ")" + (q.ReproStatus != null ? " " + q.ReproStatus : "") : "-") + " |");
                }
            }
            sb.AppendLine();

            sb.AppendLine("## Documented");
            sb.AppendLine();
            sb.AppendLine("A changed state after an exception that the docs of this version describe as the member's partial effect.");
            sb.AppendLine();
            if (model.Documented.Count == 0) {
                sb.AppendLine("None.");
            } else {
                sb.AppendLine("| id | known | member | outcome | what | contract |");
                sb.AppendLine("|---|---|---|---|---|---|");
                foreach (var f in model.Documented) {
                    sb.AppendLine("| `" + Md(f.Id) + "` | " + (f.Known ?? "-") + " | `" + Md(f.Member) + "` | " + Md(f.Outcome) + " | " + Md(Trim(f.Detail, 300)) + " | " + Md(f.DocRef) + " |");
                }
            }
            sb.AppendLine();

            RenderDiagnostics(sb, model);

            sb.AppendLine("## Claims the code was held to");
            sb.AppendLine();
            sb.AppendLine("A claim is a defect check only where its wording is in the docs of this version (active), or, without any docs/ page, where the version profile says the version makes it (profile). A promise only a later version makes (later), or one whose wording is gone although the version should have it (missing), turns its violations into questions.");
            sb.AppendLine();
            sb.AppendLine("| claim | kind | since | state | where | cases | violations |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var c in model.Claims) {
                sb.AppendLine("| `" + c.Id + "`: " + Md(Trim(c.Quote, 160)) + " | " + c.Kind + " | " + c.Since + " | " + c.State + " | " + Md(c.ActiveAt) + " | " + c.Cases + " | " + c.Violations + " |");
            }
            sb.AppendLine();

            sb.AppendLine("## Signatures");
            sb.AppendLine();
            sb.AppendLine("Mechanism and place (rule, claim, throw site, kind of damage) of every defect, question and documented entry, with the number of cases in it. The baseline comparison and the summary use these, not only the known families.");
            sb.AppendLine();
            if (model.Signatures.Count == 0) {
                sb.AppendLine("None.");
            } else {
                sb.AppendLine("| kind | known | hits | signature | entries |");
                sb.AppendLine("|---|---|---|---|---|");
                foreach (var s in model.Signatures.OrderBy(x => KindRank(x.Kind)).ThenByDescending(x => x.Hits)) {
                    sb.AppendLine("| " + s.Kind + " | " + (s.Known ?? "-") + " | " + s.Hits + " | `" + Md(s.Signature) + "` | " + string.Join(", ", s.Ids.Take(3).Select(x => "`" + Md(x) + "`")) + (s.Ids.Count > 3 ? " (+" + (s.Ids.Count - 3) + ")" : "") + " |");
                }
            }
            sb.AppendLine();

            sb.AppendLine("## Coverage");
            sb.AppendLine();
            if (model.NotRun.Count == 0) {
                sb.AppendLine("Every listed member was called.");
            } else {
                sb.AppendLine("Not called:");
                foreach (var n in model.NotRun) {
                    sb.AppendLine("- " + Md(n));
                }
            }
            sb.AppendLine();
            if (model.UnknownParams.Count > 0) {
                sb.AppendLine("Parameters of a type the argument factory does not know (called with null, so the normal path is not checked; teach `Args.Normal` the type):");
                foreach (var n in model.UnknownParams) {
                    sb.AppendLine("- `" + Md(n) + "`");
                }
                sb.AppendLine();
            }
            if (m.SecondOrderSeed != 0) {
                sb.AppendLine("Second run of the matrix in another case order (seed " + m.SecondOrderSeed + "): " + (model.Unstable.Count == 0
                    ? "every cell is the same."
                    : "the outcome depends on the order in " + model.Unstable.Count + " cases; they are never defects:"));
                foreach (var u in model.Unstable) {
                    sb.AppendLine("- `" + Md(u) + "`");
                }
                sb.AppendLine();
            }
            if (m.MemoryRestarts > 0) {
                sb.AppendLine("The worker was restarted because a case left memory behind: " + m.MemoryRestarts + " time(s).");
                sb.AppendLine();
            }

            if (model.BaselineChanges != null || model.BaselineNote != null) {
                sb.AppendLine("## Comparison with " + m.Baseline);
                sb.AppendLine();
                if (model.BaselineNote != null) {
                    sb.AppendLine(model.BaselineNote);
                } else {
                    if (model.BaselineFindings != null) {
                        var bf = model.BaselineFindings;
                        sb.AppendLine("Defects: gone " + List(bf.Fixed) + "; new " + List(bf.New) + "; kept " + List(bf.Kept) + ".");
                        sb.AppendLine();
                    }
                    RenderSignatureDiff(sb, model);
                    sb.AppendLine("Cells changed: " + model.BaselineChanges.Count(c => c.Change == "changed") + ", new: " + model.BaselineChanges.Count(c => c.Change == "added")
                        + ", gone: " + model.BaselineChanges.Count(c => c.Change == "removed") + ". Details: [diff-vs-" + m.Baseline + ".md](diff-vs-" + m.Baseline + ".md).");
                    sb.AppendLine();
                    var interesting = model.BaselineChanges.Where(c => c.Change == "changed").Take(60).ToList();
                    if (interesting.Count > 0) {
                        sb.AppendLine("| cell | " + m.Baseline + " | " + m.Flavor + " |");
                        sb.AppendLine("|---|---|---|");
                        foreach (var c in interesting) {
                            sb.AppendLine("| `" + Md(c.Key) + "` | " + Md(c.Before) + " | " + Md(c.After) + " |");
                        }
                    }
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private static int KindRank(string kind) => kind == "defect" ? 0 : kind == "question" ? 1 : 2;

        private static void RenderFinding(StringBuilder sb, Finding f) {
            sb.AppendLine("### `" + f.Id + "`");
            sb.AppendLine();
            sb.AppendLine("**" + Md(f.Title) + "**");
            sb.AppendLine();
            sb.AppendLine("- Class: " + f.Class + "; rule: " + f.Rule + "; severity: " + f.Severity + (f.Known != null ? "; known problem " + f.Known : "") + "; hits: " + f.Hits + ".");
            if (f.Member != null) {
                sb.AppendLine("- Member: `" + Md(f.Member) + "`; scenario: " + f.Scenario + (f.Variant != null && f.Variant != "-" ? "; variant: `" + Md(f.Variant) + "`" : "") + ".");
            }
            if (f.Detail != null) {
                sb.AppendLine("- What: " + Md(f.Detail));
            }
            if (f.Outcome != null) {
                sb.AppendLine("- Outcome: " + Md(f.Outcome));
            }
            if (f.Expected != null) {
                sb.AppendLine("- Expected: " + Md(f.Expected));
            }
            if (f.DocRef != null) {
                sb.AppendLine("- Contract: " + Md(f.DocRef) + (f.Since != null ? " (promised since " + f.Since + ")" : ""));
            }
            if (f.Signature != null) {
                sb.AppendLine("- Signature: `" + Md(f.Signature) + "`");
            }
            if (f.AlsoVia.Count > 0) {
                sb.AppendLine("- The same defect through: " + string.Join(", ", f.AlsoVia.Select(x => "`" + Md(x) + "`")));
            }
            sb.AppendLine("- Repro: " + (f.Repro != null ? "[" + f.Repro + "](" + f.Repro + ")" : "-") + "; repro check: " + (f.ReproStatus ?? "not run")
                + (f.ReproObserved != null && f.ReproStatus != "verified" ? " (the repro printed: `" + Md(f.ReproObserved) + "`)" : "") + ".");
            sb.AppendLine("- Parameters: " + Md(f.Params) + ".");
            sb.AppendLine();
        }

        private static void RenderDiagnostics(StringBuilder sb, ReportModel model) {
            var d = model.Diag;
            sb.AppendLine("## Diagnostics and promises outside the matrix");
            sb.AppendLine();
            sb.AppendLine("### [Obsolete]");
            sb.AppendLine();
            if (d == null || d.Obsolete.Count == 0) {
                sb.AppendLine("The package has no [Obsolete] members.");
            } else {
                sb.AppendLine("| member | text | call with null | actual behaviour | \"phase\" heuristic |");
                sb.AppendLine("|---|---|---|---|---|");
                foreach (var o in d.Obsolete) {
                    if (o.NullCalls.Count == 0) {
                        sb.AppendLine("| `" + Md(o.Member) + "` | " + Md(o.Message) + (o.IsError ? " (error)" : "") + " | - | - | " + Md(o.Heuristic ?? "-") + " |");
                    }
                    foreach (var c in o.NullCalls) {
                        sb.AppendLine("| `" + Md(o.Member) + "` | " + Md(o.Message) + (o.IsError ? " (error)" : "") + " | `" + Md(c.Code) + "` | " + Md(c.Behavior) + " | "
                            + (c.Param == "name" ? (o.Mismatch ? "**mismatch**: " : "") + Md(o.Heuristic ?? "-") : "-") + " |");
                    }
                }
            }
            sb.AppendLine();
            sb.AppendLine("### Owners in StorageGrown and FirstGrowthAfterReserve");
            sb.AppendLine();
            if (d?.GrowthNote != null) {
                sb.AppendLine(d.GrowthNote + ".");
                sb.AppendLine();
            }
            if (d != null && d.Growth.Count > 0) {
                sb.AppendLine("| storage | source | instances grown | owners | distinct | events |");
                sb.AppendLine("|---|---|---|---|---|---|");
                foreach (var g in d.Growth) {
                    sb.AppendLine("| " + g.Storage + " | " + g.Source + " | " + g.Instances + " | " + Md(string.Join("; ", g.Owners.Select(x => "\"" + x + "\""))) + " | "
                        + (!g.Available ? "not in this version" : g.Distinct ? "yes" : "**no**") + " | " + Md(string.Join("; ", g.Events.Take(4))) + (g.Note != null ? " (" + Md(g.Note) + ")" : "") + " |");
                }
                sb.AppendLine();
            }
            RenderProbes(sb, "### The FirstGrowthAfterReserve contract", d?.FirstGrowth);
            RenderProbes(sb, "### Filter.Entities spans and the enumerator around Reserve", d?.Spans);
            sb.AppendLine("### Exception texts");
            sb.AppendLine();
            if (!model.Meta.KenseiDebug) {
                sb.AppendLine("Checked in the KENSEI_DEBUG run; below is what release shows as well.");
                sb.AppendLine();
            }
            var texts = model.ExceptionTexts;
            sb.AppendLine("Exceptions the package throws on purpose (IOE, ODE, Argument*, KeyNotFound): " + texts.Count(t => t.Verdict != "raw-runtime")
                + "; they name the called operation: " + texts.Count(t => t.Verdict == "names-operation")
                + ", another operation: " + texts.Count(t => t.Verdict == "names-other")
                + ", only a parameter (Argument*): " + texts.Count(t => t.Verdict == "names-parameter")
                + ", nothing: " + texts.Count(t => t.Verdict == "names-nothing")
                + ". Raw runtime exceptions (NRE, IndexOutOfRange, ArgumentNull from the BCL): " + texts.Count(t => t.Verdict == "raw-runtime") + ".");
            sb.AppendLine();
            var notable = texts.Where(t => t.Verdict != "names-operation" && t.Verdict != "names-parameter")
                .OrderBy(t => t.Verdict == "names-other" ? 0 : t.Verdict == "names-nothing" ? 1 : 2)
                .GroupBy(t => t.Member + "|" + t.Exception + "|" + t.Message).Select(g => g.First()).Take(80).ToList();
            if (notable.Count > 0) {
                sb.AppendLine("| member | scenario | exception | text | verdict |");
                sb.AppendLine("|---|---|---|---|---|");
                foreach (var t in notable) {
                    sb.AppendLine("| `" + Md(t.Member) + "` | " + t.Scenario + " | " + t.Exception + " | " + Md(Trim(t.Message, 140)) + " | " + t.Verdict + (t.Note != null ? ": " + Md(t.Note) : "") + " |");
                }
                sb.AppendLine();
            }
            sb.AppendLine("### References after World.Destroy");
            sb.AppendLine();
            if (d?.Leak != null) {
                sb.AppendLine("Status: **" + d.Leak.Status + "**. " + Md(d.Leak.Note) + ". Group alive: " + d.Leak.GroupAlive + ", the group's pool alive: " + d.Leak.GroupPoolAlive
                    + "; control: filter alive: " + d.Leak.FilterAlive + ", pool without a group alive: " + d.Leak.PlainPoolAlive + ".");
            }
            sb.AppendLine();
        }

        private static void RenderProbes(StringBuilder sb, string title, List<ContractProbe> probes) {
            sb.AppendLine(title);
            sb.AppendLine();
            if (probes == null || probes.Count == 0) {
                sb.AppendLine("Not run in this build.");
                sb.AppendLine();
                return;
            }
            sb.AppendLine("| probe | status | observed | the promise requires |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var p in probes) {
                sb.AppendLine("| `" + p.Id + "` | " + (p.Status == "violated" ? "**violated**" : p.Status) + " | " + Md(p.Observed ?? p.Note) + " | " + Md(p.Expected ?? "-") + " |");
            }
            sb.AppendLine();
        }

        /// <summary> The repository root (run.sh passes --root), for paths in the reports. </summary>
        public static string Root;

        /// <summary> A path relative to the repository root when it lies inside it (reports name no home folders). </summary>
        public static string DisplayPath(string path) {
            if (string.IsNullOrEmpty(path)) {
                return path;
            }
            try {
                // Stand/out/bin/<flavor>-<cfg>/Stand.Contracts/Release/net8.0/ is seven levels below the root.
                var root = Path.GetFullPath(Root ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", ".."));
                var full = Path.GetFullPath(path);
                var rel = Path.GetRelativePath(root, full);
                return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? full : rel.Replace('\\', '/');
            } catch (Exception) {
                return path;
            }
        }

        private static int SevRank(string s) => s == "high" ? 0 : s == "medium" ? 1 : s == "low" ? 2 : 3;

        public static string Trim(string s, int n) => s == null ? null : s.Length > n ? s.Substring(0, n - 1) + "…" : s;

        public static string Md(string s) {
            if (s == null) {
                return "";
            }
            return s.Replace("|", "\\|").Replace("\r", "").Replace("\n", " ");
        }

        public static string Slug(string s) {
            s = Regex.Replace(s, @"[^A-Za-z0-9<>,.@()\-]+", "");
            return s;
        }
    }
}
