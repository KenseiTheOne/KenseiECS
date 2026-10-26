// Findings, contract questions, regressions and scenario statuses -> report.json (machine-readable)
// and report.md, plus one repro .cs and one case .json per signature.
//
// Every item has a status: "defect" (a violation of a promise the documents of the tested version
// make), "question" (behaviour those documents do not pin down, or a rule the contract profile does
// not count as a defect for this version; "contractQuestions"), or "regression" (open behaviour that
// changed against the baseline report; "regressions"). Defects are listed twice: "defects" groups
// them by rule (the mechanism) with their signatures nested, "findings" has one item per signature
// (rule @ place, with "magnitude" = the random trees that hit it) for readers that compare reports
// signature by signature, so that a new place or a jump in hits inside a known rule shows.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stand.Runner {
    public sealed class BaselineInfo {
        /// <summary> new | same | hits-grew | status-changed | rule-known (an old baseline without signatures). </summary>
        public string Change;
        public string Status;
        public int Hits;
    }

    /// <summary> One signature: a rule violated at one place. </summary>
    public sealed class SignatureInfo {
        public string Signature;
        public string Rule;
        public string Place;
        public string Status;
        /// <summary> Random trees that hit it (0: seen in the targeted scenarios only). </summary>
        public int Hits;
        public List<string> Sources = new List<string>();
        public int? Seed;
        public string Params;
        public string Message;
        public string Tree;
        public List<string> Steps = new List<string>();
        public string Repro;
        public string Case;
        public bool? ReproVerified;
        public string ReproVerifyOutput;
        public BaselineInfo Baseline;
    }

    public sealed class Finding {
        public string Id;
        public string Status = "defect";
        public string Known;
        public string Class;
        public string Severity;
        public string Title;
        public string Rule;
        /// <summary> Caveat of the rule (documents that disagree, a debug check behind it); null when there is none. </summary>
        public string Caveat;
        /// <summary> Why the contract profile counts it as a defect (or a question) for this version. </summary>
        public string ContractBasis;
        public string Since;
        /// <summary> The first signature (rule @ place); all of them are in Signatures. </summary>
        public string Signature;
        public List<SignatureInfo> Signatures = new List<SignatureInfo>();
        /// <summary> Further observations of the scenarios behind the finding (for example a whole-lifecycle call tally). </summary>
        public List<string> Observations = new List<string>();
        public List<string> Sources = new List<string>();
        public int? Seed;
        public string Params;
        public string Message;
        public string Tree;
        public List<string> Steps = new List<string>();
        public string Repro;
        public string Case;
        public int RandomHits;
        public bool? ReproVerified;
        public string ReproVerifyOutput;
    }

    /// <summary> One defect signature as an item of report.json "findings" (derived from Defects when the report is written). </summary>
    public sealed class SignatureFinding {
        public string Id;
        public string Signature;
        public string Place;
        public string Status;
        public string Known;
        public string Class;
        public string Severity;
        public string Title;
        public string Rule;
        public string ContractBasis;
        public string Since;
        public int Hits;
        /// <summary> Random trees that hit the signature (the same seeds every run, so it compares between versions). </summary>
        public int Magnitude;
        public List<string> Sources = new List<string>();
        public int? Seed;
        public string Params;
        public string Message;
        public string Tree;
        public List<string> Steps = new List<string>();
        public string Repro;
        public string Case;
        public bool? ReproVerified;
        public string ReproVerifyOutput;
        public BaselineInfo Baseline;
    }

    public sealed class Regression {
        public string Id;
        public string Status = "regression";
        public string Title;
        public string Was;
        public string Now;
        public string Detail;
    }

    public sealed class KnownStatus {
        public string Id;
        public string What;
        /// <summary> defect | question | not-found | not-applicable. </summary>
        public string Status;
        /// <summary> Status == "defect" (kept for readers of the older report format). </summary>
        public bool Found;
        public string Finding;
        public string Detail;
    }

    public sealed class ScenarioStatus {
        public string Name;
        public string Title;
        public string Status;
        public string Detail;
    }

    public sealed class ProfileReport {
        public string Version;
        public string VersionSource;
        public string Lifecycle;
        public bool RollbackOnlyInitCompleted;
        public bool RejectSharedRunner;
        public bool ReservedBootstrapNames;
        public bool DuplicateNameSplit;
        public bool ExplicitVersion;
        public string DocsDir;
        public List<RuleStatus> Rules = new List<RuleStatus>();
        public List<Claim> Claims = new List<Claim>();

        public static ProfileReport Of(ContractProfile p) {
            return new ProfileReport {
                Version = p.Version, VersionSource = p.VersionSource, Lifecycle = p.Base == Contract.V22 ? "2.2" : "2.1",
                RollbackOnlyInitCompleted = p.RollbackOnlyInitCompleted, RejectSharedRunner = p.RejectSharedRunner, ReservedBootstrapNames = p.ReservedBootstrapNames,
                DuplicateNameSplit = p.DuplicateNameSplit, ExplicitVersion = p.ExplicitVersion,
                DocsDir = p.DocsDir, Rules = p.Rules.Values.ToList(), Claims = p.Claims.ToList()
            };
        }
    }

    public sealed class BaselineComparison {
        public string Flavor;
        public string Report;
        public string Note;
        /// <summary> The random tests ran with the same mode, seed and number of trees, so hits compare. </summary>
        public bool HitsComparable;
        public List<string> NewSignatures = new List<string>();
        public List<string> GrownSignatures = new List<string>();
        public List<string> StatusChanged = new List<string>();
        public List<string> GoneSignatures = new List<string>();
    }

    public sealed class RunReport {
        public string Module = "runner";
        public int Format = 2;
        public string Flavor;
        public string Config;
        public bool KenseiDebug;
        public string Src;
        public string PackageVersion;
        public string Contract;
        public ProfileReport ContractProfile;
        public string Mode;
        public int BaseSeed;
        public string GenParams;
        public int RandomCases;
        public int RandomCasesNotApplicable;
        public int RandomCasesRejected;
        public int RandomOps;
        public long DurationMs;
        public string Generated;
        public Dictionary<string, bool> Features;
        public List<string> NotInThisVersion = new List<string>();
        /// <summary> Defects by rule, with their signatures. </summary>
        public List<Finding> Defects = new List<Finding>();
        /// <summary> Defects by signature (written from Defects). </summary>
        public List<SignatureFinding> Findings = new List<SignatureFinding>();
        public List<ContractQuestion> ContractQuestions = new List<ContractQuestion>();
        public List<Regression> Regressions = new List<Regression>();
        public BaselineComparison Baseline;
        public List<ScenarioStatus> Scenarios = new List<ScenarioStatus>();
        /// <summary> The findings of the 2.2.0 review (R1–R4): their status in this run. </summary>
        public List<KnownStatus> Known = new List<KnownStatus>();
        public List<string> Notes = new List<string>();
        public SortedDictionary<string, long> Coverage;

        public static readonly JsonSerializerOptions Json = new JsonSerializerOptions {
            IncludeFields = true,
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public int FindingCount;
        public int QuestionCount;
        public int RegressionCount;

        public void Write(string dir) {
            Findings = Defects.SelectMany(f => f.Signatures.Select(sg => new SignatureFinding {
                Id = f.Id, Signature = sg.Signature, Place = sg.Place, Status = f.Status, Known = f.Known, Class = f.Class, Severity = f.Severity,
                Title = f.Title, Rule = f.Rule, ContractBasis = f.ContractBasis, Since = f.Since, Hits = sg.Hits, Magnitude = sg.Hits, Sources = sg.Sources,
                Seed = sg.Seed, Params = sg.Params, Message = sg.Message, Tree = sg.Tree, Steps = sg.Steps, Repro = sg.Repro, Case = sg.Case,
                ReproVerified = sg.ReproVerified, ReproVerifyOutput = sg.ReproVerifyOutput, Baseline = sg.Baseline
            })).ToList();
            FindingCount = Findings.Count;
            QuestionCount = ContractQuestions.Count;
            RegressionCount = Regressions.Count;
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "report.json"), JsonSerializer.Serialize(this, Json));
            File.WriteAllText(Path.Combine(dir, "report.md"), Markdown());
        }

        public static RunReport Read(string dir) {
            return JsonSerializer.Deserialize<RunReport>(File.ReadAllText(Path.Combine(dir, "report.json")), Json);
        }

        /// <summary> Every signature of the defects and of the rule questions. </summary>
        public IEnumerable<SignatureInfo> AllSignatures() {
            foreach (var f in Defects) {
                foreach (var s in f.Signatures) {
                    yield return s;
                }
            }
            foreach (var q in ContractQuestions) {
                if (q.Signatures != null) {
                    foreach (var s in q.Signatures) {
                        yield return s;
                    }
                }
            }
        }

        private static readonly string[] SeverityOrder = { "critical", "high", "medium", "low" };

        private static string Cell(string s) {
            return (s ?? "").Replace("|", "\\|").Replace("\n", " ");
        }

        public string Markdown() {
            var sb = new StringBuilder();
            sb.AppendLine("# Stand / runner — " + Flavor + "-" + Config);
            sb.AppendLine();
            sb.AppendLine("| | |");
            sb.AppendLine("|---|---|");
            sb.AppendLine("| Sources | `" + Src + "` (package.json " + PackageVersion + ") |");
            sb.AppendLine("| Build | " + (KenseiDebug ? "KENSEI_DEBUG" : "release") + " |");
            sb.AppendLine("| Contract profile | " + Cell(Contract) + " |");
            sb.AppendLine("| Mode | " + Mode + ", seed " + BaseSeed + ", " + RandomCases + " random trees (" + RandomOps + " operations; " + RandomCasesNotApplicable + " not applicable" +
                          (RandomCasesRejected > 0 ? "; " + RandomCasesRejected + " with a rejected second registration" : "") + "), " + GenParams + " |");
            sb.AppendLine("| Time | " + (DurationMs / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s |");
            sb.AppendLine("| Generated | " + Generated + " |");
            sb.AppendLine();
            if (NotInThisVersion.Count > 0) {
                sb.AppendLine("Not in this version (those checks are off): " + string.Join("; ", NotInThisVersion) + ".");
                sb.AppendLine();
            }

            sb.AppendLine("## Verdict");
            sb.AppendLine();
            sb.AppendLine("- **Defects** (violations of what the documents of this version promise): " + Defects.Count + " rule(s), " + Defects.Sum(f => f.Signatures.Count) + " signature(s)" +
                          (Defects.Count > 0 ? " — " + string.Join(", ", OrderedFindings().Select(f => f.Id + (f.Known != null ? " (" + f.Known + ")" : ""))) : ""));
            var ruleQuestions = ContractQuestions.Where(q => q.Kind == "rule").ToList();
            sb.AppendLine("- **Contract questions**: " + ContractQuestions.Count + " — " + ruleQuestions.Count + " rule(s) the profile does not count as a defect for this version" +
                          (ruleQuestions.Count > 0 ? " (" + string.Join(", ", ruleQuestions.Select(q => q.Id + (q.Known != null ? " (" + q.Known + ")" : ""))) + ")" : "") +
                          ", " + (ContractQuestions.Count - ruleQuestions.Count) + " observation(s) of open behaviour");
            if (Baseline != null) {
                sb.AppendLine("- **Against the baseline " + Baseline.Flavor + "**: " + Baseline.NewSignatures.Count + " new signature(s), " + Baseline.GrownSignatures.Count +
                              " with a jump in hits, " + Baseline.StatusChanged.Count + " with another status, " + Baseline.GoneSignatures.Count + " gone; " +
                              Regressions.Count + " regression(s) (open behaviour that changed)" + (Baseline.Note != null ? ". " + Baseline.Note : ""));
            } else {
                sb.AppendLine("- **Regressions**: not compared (no --baseline)");
            }
            sb.AppendLine();

            if (Known.Count > 0) {
                sb.AppendLine("## Findings of the 2.2.0 review");
                sb.AppendLine();
                sb.AppendLine("| | what | status | rule | details |");
                sb.AppendLine("|---|---|---|---|---|");
                foreach (var k in Known) {
                    sb.AppendLine("| " + k.Id + " | " + Cell(k.What) + " | " + k.Status + " | " + (k.Finding ?? "—") + " | " + Cell(k.Detail) + " |");
                }
                sb.AppendLine();
            }

            if (ContractProfile != null) {
                sb.AppendLine("## Contract profile");
                sb.AppendLine();
                sb.AppendLine("Version " + ContractProfile.Version + " (" + ContractProfile.VersionSource + "), " + ContractProfile.Lifecycle + " lifecycle model; documents looked up in the " +
                              "package sources (XML docs, CHANGELOG.md)" + (ContractProfile.DocsDir != null ? " and in `" + ContractProfile.DocsDir + "`" : " (no docs/ folder next to the package)") + ".");
                sb.AppendLine("Rules not listed here are defects when violated.");
                sb.AppendLine();
                sb.AppendLine("| rule | status | promised since | basis |");
                sb.AppendLine("|---|---|---|---|");
                foreach (var r in ContractProfile.Rules) {
                    sb.AppendLine("| " + r.Rule + " | " + r.Status + " | " + (r.Since ?? "—") + " | " + Cell(r.Basis) + " |");
                }
                sb.AppendLine();
                if (ContractProfile.Claims.Count > 0) {
                    sb.AppendLine("| rule | document | sentence | in this tree |");
                    sb.AppendLine("|---|---|---|---|");
                    foreach (var c in ContractProfile.Claims) {
                        sb.AppendLine("| " + c.Rule + " | " + c.Source + " | " + Cell(c.Text) + " | " + c.State + " |");
                    }
                    sb.AppendLine();
                }
            }

            sb.AppendLine("## Defects (" + Defects.Count + ")");
            sb.AppendLine();
            if (Defects.Count == 0) {
                sb.AppendLine("None.");
                sb.AppendLine();
            } else {
                sb.AppendLine("| id | known | class | severity | signatures | random trees | repro | verified |");
                sb.AppendLine("|---|---|---|---|---|---|---|---|");
                foreach (var f in OrderedFindings()) {
                    sb.AppendLine("| " + f.Id + " | " + (f.Known ?? "—") + " | " + f.Class + " | " + f.Severity + " | " + f.Signatures.Count + " | " + f.RandomHits +
                                  " | `" + f.Repro + "` | " + Verified(f.ReproVerified) + " |");
                }
                sb.AppendLine();
                foreach (var f in OrderedFindings()) {
                    WriteFinding(sb, f.Id, f.Known, f.Title, f.Class, f.Severity, f.Rule, f.Caveat, f.ContractBasis, f.Observations, f.Signatures);
                }
            }

            sb.AppendLine("## Contract questions (" + ContractQuestions.Count + ")");
            sb.AppendLine();
            if (ContractQuestions.Count == 0) {
                sb.AppendLine("None.");
                sb.AppendLine();
            }
            foreach (var q in ContractQuestions) {
                if (q.Kind == "rule") {
                    var info = Stand.Runner.Rules.Get(q.Rule);
                    WriteFinding(sb, q.Id, q.Known, q.Title, info.Class, q.Severity, info.Contract, info.Caveat, q.Question, null, q.Signatures, question: true);
                    continue;
                }
                sb.AppendLine("### " + q.Id);
                sb.AppendLine();
                sb.AppendLine("**" + q.Title + "**");
                sb.AppendLine();
                sb.AppendLine("- Question: " + q.Question);
                sb.AppendLine("- Observed: " + q.Observed);
                sb.AppendLine("- Why it is not a defect: " + q.WhyNotFinding);
                sb.AppendLine();
            }

            if (Baseline != null) {
                sb.AppendLine("## Against the baseline " + Baseline.Flavor);
                sb.AppendLine();
                sb.AppendLine("Baseline report: `" + Baseline.Report + "`" + (Baseline.HitsComparable ? "; same mode, seed and number of random trees, so hits compare." : "; " + (Baseline.Note ?? "hits do not compare.")));
                sb.AppendLine();
                void List(string title, List<string> items) {
                    sb.AppendLine("- " + title + ": " + (items.Count == 0 ? "none" : ""));
                    foreach (var i in items) {
                        sb.AppendLine("  - `" + i + "`");
                    }
                }
                List("New signatures", Baseline.NewSignatures);
                List("Signatures with a jump in hits", Baseline.GrownSignatures);
                List("Signatures with another status", Baseline.StatusChanged);
                List("Signatures gone", Baseline.GoneSignatures);
                sb.AppendLine();
                sb.AppendLine("### Regressions (" + Regressions.Count + ")");
                sb.AppendLine();
                if (Regressions.Count == 0) {
                    sb.AppendLine("None: the open behaviour the questions observe is the same as on the baseline.");
                    sb.AppendLine();
                }
                foreach (var r in Regressions) {
                    string title = r.Title ?? "";
                    sb.AppendLine("- **" + r.Id + "** — " + title + (title.EndsWith("?") || title.EndsWith(".") ? "" : ".") + " Baseline: " + r.Was + ". Now: " + r.Now + "." +
                                  (r.Detail != null ? " " + r.Detail : ""));
                }
                if (Regressions.Count > 0) {
                    sb.AppendLine();
                }
            }

            sb.AppendLine("## Scenarios");
            sb.AppendLine();
            sb.AppendLine("| scenario | status | details |");
            sb.AppendLine("|---|---|---|");
            foreach (var s in Scenarios) {
                sb.AppendLine("| " + s.Name + " | " + s.Status + " | " + Cell((s.Title != null ? s.Title + ". " : "") + s.Detail) + " |");
            }
            sb.AppendLine();
            if (Coverage != null && Coverage.Count > 0) {
                sb.AppendLine("## Coverage of the random test");
                sb.AppendLine();
                sb.AppendLine("| what | count |");
                sb.AppendLine("|---|---|");
                foreach (var kv in Coverage) {
                    sb.AppendLine("| " + kv.Key + " | " + kv.Value + " |");
                }
                sb.AppendLine();
            }
            if (Notes.Count > 0) {
                sb.AppendLine("## Notes");
                sb.AppendLine();
                foreach (var n in Notes) {
                    sb.AppendLine("- " + n);
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private static string Verified(bool? v) {
            return v == null ? "—" : v.Value ? "yes" : "NO";
        }

        private static void WriteFinding(StringBuilder sb, string id, string known, string title, string cls, string severity, string rule, string caveat, string basis,
                                         List<string> observations, List<SignatureInfo> signatures, bool question = false) {
            sb.AppendLine("### " + id + (known != null ? " (" + known + ")" : "") + (question ? " — question" : ""));
            sb.AppendLine();
            sb.AppendLine("**" + title + "**");
            sb.AppendLine();
            sb.AppendLine("- Class / severity: " + cls + " / " + severity);
            sb.AppendLine("- Rule (contract): " + rule);
            if (caveat != null) {
                sb.AppendLine("- Caveat: " + caveat);
            }
            if (basis != null) {
                sb.AppendLine("- " + (question ? "Why a question for this version: " : "Contract profile: ") + basis);
            }
            if (observations != null) {
                foreach (var obs in observations) {
                    sb.AppendLine("- Observation: " + obs);
                }
            }
            sb.AppendLine();
            if (signatures == null || signatures.Count == 0) {
                return;
            }
            sb.AppendLine("| place | random trees | source | repro | verified | baseline |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var s in signatures) {
                sb.AppendLine("| `" + s.Place + "` | " + s.Hits + " | " + Cell(string.Join(", ", s.Sources) + (s.Seed != null ? "; seed " + s.Seed : "")) + " | `" + s.Repro + "` | " +
                              Verified(s.ReproVerified) + " | " + (s.Baseline == null ? "—" : s.Baseline.Change + (s.Baseline.Change != "new" ? " (" + s.Baseline.Status + ", " + s.Baseline.Hits + ")" : "")) + " |");
            }
            sb.AppendLine();
            var first = signatures[0];
            if (first.Tree != null) {
                sb.AppendLine("- Tree (first signature): `" + first.Tree + "`");
            }
            if (first.Steps.Count > 0) {
                sb.AppendLine("- Steps: " + string.Join(" -> ", first.Steps.Select(x => "`" + x + "`")));
            }
            sb.AppendLine("- What breaks: " + first.Message);
            foreach (var s in signatures.Where(x => x.ReproVerified == false)) {
                sb.AppendLine("- Repro `" + s.Repro + "` did NOT reproduce: " + s.ReproVerifyOutput);
            }
            sb.AppendLine();
        }

        private IEnumerable<Finding> OrderedFindings() {
            return Defects.OrderBy(f => Array.IndexOf(SeverityOrder, f.Severity) < 0 ? 9 : Array.IndexOf(SeverityOrder, f.Severity)).ThenBy(f => f.Known ?? "~").ThenBy(f => f.Id);
        }
    }
}
