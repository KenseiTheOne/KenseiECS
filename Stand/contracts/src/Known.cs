using System;
using System.Collections.Generic;
using System.Linq;

namespace Stand.Contracts {
    /// <summary> A problem found in an earlier release, and how to recognise it among this run's entries. </summary>
    public sealed class KnownDefect {
        public string Id;
        public string Title;
        public string FoundIn;        // where it was found first
        public string Shows;          // rel | dbg | both: the builds in which it can show at all
        public string Why;            // why it cannot show in the other build
        public Func<Finding, bool> Match;              // over defects, questions and documented entries
        public Func<ReportModel, string> NotChecked;   // why this run could not look for it (an API the version lacks), or null
    }

    /// <summary> Status of one known problem in this run. </summary>
    public sealed class KnownStatus {
        public string Id;
        public string Title;
        public string FoundIn;
        public string Status;         // found | regression | question | documented | not-found | not-applicable | not-checked
        public string Note;
        public List<string> Findings = new();     // ids of the defects that match
        public List<string> Regressions = new();  // ids of the regressions against the baseline that match (with --baseline)
        public List<string> Questions = new();    // ids of the questions that match
        public List<string> Documented = new();   // ids of the documented entries that match
        public List<string> Signatures = new();   // the signatures behind them: more than one is a new mechanism in the family
    }

    /// <summary>
    /// The problems of 2.2.0 this module was built to catch, and two it found itself. An entry that
    /// matches one is tagged with its id, so a later version that brings one back is recognised, and a
    /// fixed one is reported as not found. Matching is by what the entry is about, not by its text.
    /// The status follows the strongest match: a defect (the docs of this version promise otherwise),
    /// a question (they do not), or documented (they describe it).
    /// </summary>
    public static class Known {
        private static bool StateOf(Finding f) => f.Class == "state-corruption";

        // The id without the "q:" of a question, so one rule recognises the defect and the question.
        private static string Id(Finding f) => f.Id.StartsWith("q:", StringComparison.Ordinal) ? f.Id.Substring(2) : f.Id;

        public static readonly List<KnownDefect> All = new() {
            new KnownDefect {
                Id = "G1", FoundIn = "2.2.0 (user report)", Shows = "rel",
                Why = "under KENSEI_DEBUG CreateEntity throws ObjectDisposedException before it claims a slot",
                Title = "CreateEntity on a destroyed world in release: NullReferenceException, after which IsAlive and DestroyEntity throw NRE too",
                Match = f => StateOf(f) && f.Scenario == Scenarios.WorldDestroyed
                    && (f.Member.StartsWith("World.CreateEntity", StringComparison.Ordinal) || f.AlsoVia.Any(x => x.StartsWith("World.CreateEntity", StringComparison.Ordinal)))
            },
            new KnownDefect {
                Id = "G2", FoundIn = "2.2.0 (review)", Shows = "both",
                Title = "World.Clear after Destroy throws something other than ObjectDisposedException (in 2.2.0 ArgumentNullException without a null argument)",
                Match = f => f.Member == "World.Clear()" && f.Scenario == Scenarios.WorldDestroyed
            },
            new KnownDefect {
                Id = "G3", FoundIn = "2.2.0 (review)", Shows = "both",
                Title = "The [Obsolete] text of Add(SystemsRunner, string) promises \"creates a separate phase\", but with name = null an unnamed inline group is registered",
                Match = f => Id(f).StartsWith("obsolete-text:SystemsRunner.Add(SystemsRunner,string)", StringComparison.Ordinal),
                NotChecked = m => m.Diag == null ? "the diagnostics did not run"
                    : m.Diag.Obsolete.Any(o => o.Member == "SystemsRunner.Add(SystemsRunner, string)") ? null
                    : "Add(SystemsRunner, string) is not [Obsolete] in this version"
            },
            new KnownDefect {
                Id = "G4", FoundIn = "2.2.0 (user report)", Shows = "dbg",
                Why = "growth after Reserve is tracked only under KENSEI_DEBUG",
                Title = "No cheap signal of the first growth after Reserve (owner, array, tick) without EcsProfiler; 2.2.1 adds World.FirstGrowthAfterReserve, held to its XML contract",
                Match = f => Id(f).StartsWith("first-growth:", StringComparison.Ordinal) || Id(f).StartsWith("growth-owner:filter-dense:first-growth", StringComparison.Ordinal),
                NotChecked = m => m.Diag == null ? "the diagnostics did not run" : null
            },
            new KnownDefect {
                Id = "G5", FoundIn = "2.2.0 (user report)", Shows = "dbg",
                Why = "growth after Reserve is tracked only under KENSEI_DEBUG",
                Title = "StorageGrown does not tell filters apart: the owner of every filter is the constant \"Filter\"",
                Match = f => Id(f).StartsWith("growth-owner:filter", StringComparison.Ordinal),
                NotChecked = m => m.Diag == null ? "the diagnostics did not run"
                    : m.Diag.Growth.Any(g => g.Source.StartsWith("EcsProfiler", StringComparison.Ordinal) && g.Storage.StartsWith("filter", StringComparison.Ordinal) && g.Events.Count > 0) ? null
                    : (m.Diag.GrowthNote ?? "no filter growth events: the check did not trigger")
            },
            new KnownDefect {
                Id = "WL1", FoundIn = "2.2.0 (WeakReference probe)", Shows = "both",
                Title = "World.Destroy keeps references to its groups: a group and its pools with data are not collected while the world lives",
                Match = f => Id(f) == "leak:World.Destroy:groups",
                NotChecked = m => m.Diag?.Leak == null ? "the leak probe did not run"
                    : m.Diag.Leak.Status == "inconclusive" ? "the probe was inconclusive: " + m.Diag.Leak.Note : null
            },
            new KnownDefect {
                Id = "C-FILTER-DESTROYED", FoundIn = "2.1.1 (this stand)", Shows = "both",
                Title = "2.1.1: registering a filter on a destroyed world (FilterBuilder.End, World.Filter<...>) added it to the world's filters and failed with NullReferenceException",
                Match = f => StateOf(f) && f.Scenario == Scenarios.WorldDestroyed
                    && (f.Member.StartsWith("FilterBuilder.End", StringComparison.Ordinal) || f.Member.StartsWith("World.Filter", StringComparison.Ordinal))
            },
            new KnownDefect {
                Id = "C-LOAD", FoundIn = "2.2.0 (this stand)", Shows = "both",
                Title = "A failed WorldSerializer.Load leaves the target world partly restored (entities before pools, alive slots without components)",
                Match = f => StateOf(f) && f.Member.StartsWith("WorldSerializer.Load", StringComparison.Ordinal)
            },
            new KnownDefect {
                Id = "C-ADD-REJECT", FoundIn = "2.2.0 (this stand)", Shows = "dbg",
                Why = "the nested-runner check exists only under KENSEI_DEBUG",
                Title = "KENSEI_DEBUG: the nested-runner World check of Add/AddGroup/AddPhase throws after the registration (a phantom entry, the rejected runner marked as a child)",
                Match = f => StateOf(f) && f.Member.StartsWith("SystemsRunner.Add", StringComparison.Ordinal) && f.Variant == "curated:nested-other-world"
            },
        };

        /// <summary>
        /// Set Finding.Known on every entry that matches a known problem (the first that matches), then on
        /// every entry with the same signature as a tagged one: the same mechanism in the same place is the
        /// same problem, reached through another member (CommandBuffer.Playback for G1, say).
        /// </summary>
        public static void Tag(IEnumerable<Finding> entries) {
            var list = entries.ToList();
            foreach (var f in list) {
                var k = All.FirstOrDefault(x => Matches(x, f));
                f.Known = k?.Id;
            }
            var bySignature = list.Where(f => f.Known != null && f.Signature != null).GroupBy(f => f.Signature).ToDictionary(g => g.Key, g => g.First().Known);
            foreach (var f in list.Where(f => f.Known == null && f.Signature != null)) {
                if (bySignature.TryGetValue(f.Signature, out var known)) {
                    f.Known = known;
                }
            }
        }

        private static bool Matches(KnownDefect k, Finding f) {
            try {
                return k.Match(f);
            } catch (Exception) {
                return false;
            }
        }

        public static List<KnownStatus> Status(ReportModel model, bool debug) {
            var list = new List<KnownStatus>();
            var defects = model.Findings.Concat(model.Diagnostics.Where(d => d.Kind == "defect")).ToList();
            foreach (var k in All) {
                var s = new KnownStatus { Id = k.Id, Title = k.Title, FoundIn = k.FoundIn };
                var d = defects.Where(f => f.Known == k.Id || Matches(k, f)).ToList();
                var reg = model.Regressions.Where(f => f.Known == k.Id || Matches(k, f)).ToList();
                var q = model.Questions.Where(f => f.Known == k.Id || Matches(k, f)).ToList();
                var doc = model.Documented.Where(f => f.Known == k.Id || Matches(k, f)).ToList();
                s.Findings.AddRange(d.Select(f => f.Id));
                s.Regressions.AddRange(reg.Select(f => f.Id));
                s.Questions.AddRange(q.Select(f => f.Id));
                s.Documented.AddRange(doc.Select(f => f.Id));
                s.Signatures.AddRange(d.Concat(q).Concat(doc).Select(f => f.Signature).Where(x => x != null).Distinct());
                bool applicable = k.Shows == "both" || (k.Shows == "dbg") == debug;
                if (d.Count > 0) {
                    s.Status = "found";
                } else if (reg.Count > 0) {
                    s.Status = "regression";
                    s.Note = reg[0].Expected + " -> " + reg[0].Outcome;
                } else if (q.Count > 0) {
                    s.Status = "question";
                    s.Note = q[0].Detail;
                } else if (doc.Count > 0) {
                    s.Status = "documented";
                    s.Note = doc[0].DocRef;
                } else if (!applicable) {
                    s.Status = "not-applicable";
                    s.Note = k.Why;
                } else if (k.NotChecked?.Invoke(model) is string why) {
                    s.Status = "not-checked";
                    s.Note = why;
                } else {
                    s.Status = "not-found";
                }
                list.Add(s);
            }
            return list;
        }
    }
}
