using System.Collections.Generic;
using System.Linq;

namespace Stand.Contracts {
    /// <summary>
    /// report.json: the machine-readable result of one run, in the shape the other stand modules use.
    /// Serialized with camelCase names. The full internal model is model.json next to it.
    ///
    ///   findings           defects only: the code contradicts a promise in the docs of this version
    ///   contractQuestions  suspicious, no document of this version promises otherwise
    ///   documented         a changed state after an exception that the docs describe (C-LOAD)
    ///   regressions        with --baseline: cases fine on the baseline and suspicious now, no contract broken
    ///   signatures         mechanism and place of every entry with its hits; baseline.signatures compares them
    ///   known              status of the known problems: found | question | documented | not-found | not-applicable | not-checked
    ///   claims             every curated promise with its state for this version: active | profile | later | missing
    /// </summary>
    public sealed class PublicReport {
        public string Module = "contracts";
        public int Format = 2;
        public string Flavor;
        public string Config;
        public bool KenseiDebug;
        public string Src;
        public string PackageVersion;
        public string Docs;
        public string DocsSource;
        public string Mode;
        public int Seed;
        public int SecondOrderSeed;
        public string Generated;
        public double DurationSec;
        public int Cases;
        public int Members;
        public int Targets;
        public Dictionary<string, int> Verdicts;
        public int FindingCount;
        public int QuestionCount;
        public int DocumentedCount;
        public int RegressionCount;
        public List<PublicFinding> Findings = new();
        public List<PublicFinding> ContractQuestions = new();
        public List<PublicFinding> Documented = new();
        public List<PublicFinding> Regressions = new();
        public List<PublicFinding> DiagnosticNotes = new();
        public List<KnownStatus> Known;
        public List<SignatureStat> Signatures;
        public List<PublicClaim> Claims = new();
        public List<string> ClaimWarnings;
        public List<string> StandErrors;
        public List<string> NotRun;
        public List<string> UnknownParams;
        public List<string> Unstable;
        public PublicBaseline Baseline;
        public PublicDiagnostics Diagnostics;
        public Dictionary<string, string> Files = new();

        public static PublicReport From(ReportModel m) {
            var r = new PublicReport {
                Flavor = m.Meta.Flavor, Config = m.Meta.Cfg, KenseiDebug = m.Meta.KenseiDebug, Src = m.Meta.Src, PackageVersion = m.Meta.PackageVersion,
                Docs = m.Meta.Docs, DocsSource = m.Meta.DocsSource, Mode = m.Meta.Mode,
                Seed = m.Meta.Seed, SecondOrderSeed = m.Meta.SecondOrderSeed, Generated = m.Meta.Started, DurationSec = System.Math.Round(m.Meta.Seconds, 1),
                Cases = m.Meta.Cases, Members = m.Meta.Members, Targets = m.Meta.Targets, Verdicts = m.Verdicts, Known = m.Known, Signatures = m.Signatures,
                ClaimWarnings = m.ClaimWarnings, StandErrors = m.StandErrors, NotRun = m.NotRun, UnknownParams = m.UnknownParams, Unstable = m.Unstable
            };
            foreach (var f in m.Findings.Concat(m.Diagnostics.Where(d => d.Kind == "defect"))) {
                r.Findings.Add(PublicFinding.From(f));
            }
            foreach (var f in m.Questions) {
                r.ContractQuestions.Add(PublicFinding.From(f));
            }
            foreach (var f in m.Documented) {
                r.Documented.Add(PublicFinding.From(f));
            }
            foreach (var f in m.Regressions) {
                r.Regressions.Add(PublicFinding.From(f));
            }
            foreach (var f in m.Diagnostics.Where(d => d.Kind != "defect")) {
                r.DiagnosticNotes.Add(PublicFinding.From(f));
            }
            r.FindingCount = r.Findings.Count;
            r.QuestionCount = r.ContractQuestions.Count;
            r.DocumentedCount = r.Documented.Count;
            r.RegressionCount = r.Regressions.Count;
            foreach (var c in m.Claims) {
                r.Claims.Add(new PublicClaim { Id = c.Id, Kind = c.Kind, Quote = c.Quote, Since = c.Since, State = c.State, ActiveAt = c.ActiveAt, Cases = c.Cases, Violations = c.Violations });
            }
            if (m.Meta.Baseline != null) {
                r.Baseline = new PublicBaseline { Flavor = m.Meta.Baseline, Note = m.BaselineNote };
                if (m.BaselineChanges != null) {
                    r.Baseline.Changed = m.BaselineChanges.Count(c => c.Change == "changed");
                    r.Baseline.Added = m.BaselineChanges.Count(c => c.Change == "added");
                    r.Baseline.Removed = m.BaselineChanges.Count(c => c.Change == "removed");
                    r.Baseline.File = "diff-vs-" + m.Meta.Baseline + ".md";
                    r.Baseline.Regressions = m.Regressions.Count;
                }
                r.Baseline.Findings = m.BaselineFindings;
                r.Baseline.Signatures = m.BaselineSignatures;
            }
            r.Diagnostics = new PublicDiagnostics {
                Obsolete = m.Diag?.Obsolete, Growth = m.Diag?.Growth, GrowthNote = m.Diag?.GrowthNote, FirstGrowth = m.Diag?.FirstGrowth, Spans = m.Diag?.Spans, Leak = m.Diag?.Leak,
                ExceptionTexts = m.ExceptionTexts.GroupBy(t => t.Verdict).ToDictionary(g => g.Key, g => g.Count()),
                // Texts that name another operation or none: one line per member, exception and message.
                ExceptionTextNotes = m.ExceptionTexts.Where(t => t.Verdict == "names-other" || t.Verdict == "names-nothing")
                    .GroupBy(t => t.Member + "|" + t.Exception + "|" + t.Message).Select(g => g.First()).ToList()
            };
            r.Files["report"] = "report.md";
            r.Files["model"] = "model.json";
            r.Files["matrixMd"] = "matrix.md";
            r.Files["matrixJson"] = "matrix.json";
            r.Files["cases"] = "cases.ndjson";
            r.Files["repro"] = "repro/";
            r.Files["reproCheck"] = "repro-check.json";
            return r;
        }
    }

    public sealed class PublicFinding {
        public string Id;
        public string Known;
        public string Kind;
        public string Class;
        public string Rule;
        public string Severity;
        public string Title;
        public string Member;
        public string Scenario;
        public string Variant;
        public string Outcome;
        public string Expected;
        public string Contract;
        public string Quote;
        public string ClaimState;
        public string Since;
        public string Detail;
        public string Signature;
        public int Hits;
        public string Repro;
        public string ReproStatus;
        public string ReproObserved;
        public int Seed;
        public string Params;
        public List<string> AlsoVia;
        public List<string> Cases;

        public static PublicFinding From(Finding f) {
            return new PublicFinding {
                Id = f.Id, Known = f.Known, Kind = f.Kind, Class = f.Class, Rule = f.Rule, Severity = f.Severity, Title = f.Title, Member = f.Member,
                Scenario = f.Scenario, Variant = f.Variant, Outcome = f.Outcome, Expected = f.Expected, Contract = f.DocRef, Quote = f.Quote, ClaimState = f.ClaimState, Since = f.Since,
                Detail = f.Detail, Signature = f.Signature, Hits = f.Hits,
                Repro = f.Repro, ReproStatus = f.ReproStatus, ReproObserved = f.ReproStatus == "verified" ? null : f.ReproObserved,
                Seed = f.Seed, Params = f.Params,
                AlsoVia = f.AlsoVia.Count > 0 ? f.AlsoVia : null, Cases = f.CaseKeys.Count > 0 ? f.CaseKeys : null
            };
        }
    }

    public sealed class PublicDiagnostics {
        public List<ObsoleteEntry> Obsolete;
        public List<GrowthCheck> Growth;
        public string GrowthNote;
        public List<ContractProbe> FirstGrowth;
        public List<ContractProbe> Spans;
        public LeakCheck Leak;
        public Dictionary<string, int> ExceptionTexts;     // verdict -> count (names-operation, names-other, names-parameter, names-nothing, raw-runtime)
        public List<ExceptionText> ExceptionTextNotes;
    }

    public sealed class PublicClaim {
        public string Id;
        public string Kind;
        public string Quote;
        public string Since;
        public string State;
        public string ActiveAt;
        public int Cases;
        public int Violations;
    }

    public sealed class PublicBaseline {
        public string Flavor;
        public int Changed;
        public int Added;
        public int Removed;
        public int Regressions;
        public string File;
        public string Note;
        public FindingDiff Findings;
        public SignatureDiff Signatures;
    }
}
