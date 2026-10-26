// The contract profile of the tree under test: which documented promises its version makes. A rule
// violation is a defect only when the documents of that version promise the rule; otherwise it is a
// contract question (a promise that appeared in a later version, documents that disagree, behaviour
// the documents leave open).
//
// The version comes from the package.json of the tree (run.sh passes it; --contract-version overrides
// it). Where the tree carries the documents themselves — the XML docs in its sources, its CHANGELOG.md,
// a docs/ folder next to the package — the sentence a rule rests on is looked up there as well: a
// promise the tree's own documents make counts even when package.json lags behind, and a promise its
// documents no longer contain is a question, not a defect.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Stand.Runner {
    /// <summary> major.minor.patch of a package version; a pre-release suffix is ignored. </summary>
    public readonly struct SemVer : IComparable<SemVer> {
        public readonly int Major;
        public readonly int Minor;
        public readonly int Patch;

        public SemVer(int major, int minor, int patch) {
            Major = major;
            Minor = minor;
            Patch = patch;
        }

        public static bool TryParse(string text, out SemVer version) {
            version = default;
            if (string.IsNullOrWhiteSpace(text)) {
                return false;
            }
            var m = Regex.Match(text.Trim(), @"^v?(\d+)(?:\.(\d+))?(?:\.(\d+))?");
            if (!m.Success) {
                return false;
            }
            version = new SemVer(int.Parse(m.Groups[1].Value),
                m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0,
                m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
            return true;
        }

        public static SemVer Parse(string text) {
            if (!TryParse(text, out var v)) {
                throw new ArgumentException("not a version: " + text);
            }
            return v;
        }

        public int CompareTo(SemVer other) {
            int c = Major.CompareTo(other.Major);
            if (c != 0) {
                return c;
            }
            c = Minor.CompareTo(other.Minor);
            return c != 0 ? c : Patch.CompareTo(other.Patch);
        }

        public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;
        public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;
        public static bool operator <=(SemVer a, SemVer b) => a.CompareTo(b) <= 0;
        public static bool operator >=(SemVer a, SemVer b) => a.CompareTo(b) >= 0;

        public override string ToString() => Major + "." + Minor + "." + Patch;
    }

    /// <summary> One documented sentence a rule rests on, and whether the tree under test contains it. </summary>
    public sealed class Claim {
        public string Rule;
        /// <summary> xml:&lt;file in the package&gt;, changelog, or docs:&lt;file under docs/&gt;. </summary>
        public string Source;
        public string Text;
        /// <summary> present | absent | unavailable (the tree does not carry that document). </summary>
        public string State;
    }

    /// <summary> How a violation of one rule is reported for this tree. </summary>
    public sealed class RuleStatus {
        public string Rule;
        /// <summary> defect | question | off (not checked under this contract). </summary>
        public string Status;
        /// <summary> The version whose documents first make the promise, when one is known. </summary>
        public string Since;
        /// <summary> Why: which document makes the promise, or why there is none. </summary>
        public string Basis;
    }

    public sealed class ContractProfile {
        public static readonly SemVer V220 = new SemVer(2, 2, 0);
        public static readonly SemVer V221 = new SemVer(2, 2, 1);
        public static readonly SemVer V222 = new SemVer(2, 2, 2);

        /// <summary> The version the profile was chosen for, and where it came from (package.json, --contract-version, API probe). </summary>
        public string Version;
        public string VersionSource;
        public SemVer V;
        /// <summary> The lifecycle model: 2.1 (no rollback) or 2.2 (rollback, groups, phases). </summary>
        public Contract Base;

        /// <summary> 2.2.2: the Init rollback destroys only systems whose Init completed (Init-less systems are never destroyed by it). </summary>
        public bool RollbackOnlyInitCompleted;
        /// <summary> 2.2.2: registering one runner in a second slot throws in every build. </summary>
        public bool RejectSharedRunner;
        /// <summary> 2.2.2: a user system or group named "fixed" or "late" in EcsBootstrap's update runner gets a clear error naming the reserved names. </summary>
        public bool ReservedBootstrapNames;
        /// <summary>
        /// 2.2.2: in release a name used twice in one runner keeps one registration per kind: SetActive, IsActive and
        /// IndexOf reach the last run system or inline group (a phase only when none has the name), GetRunner the last
        /// group or phase ("A system or group and a phase with one name split it"). Up to 2.2.1: the second replaces the first.
        /// </summary>
        public bool DuplicateNameSplit;

        /// <summary> The tree is judged by an explicit --contract-version: its rules apply without their sentences in the tree's documents. </summary>
        public bool ExplicitVersion;

        /// <summary> Where the documents were looked up (null when the tree carries no docs/ folder). </summary>
        public string DocsDir;
        public readonly List<Claim> Claims = new List<Claim>();
        public readonly SortedDictionary<string, RuleStatus> Rules = new SortedDictionary<string, RuleStatus>(StringComparer.Ordinal);

        private string _src;
        private readonly Dictionary<string, string> _texts = new Dictionary<string, string>();

        public bool V22 => Base == Contract.V22;

        /// <summary> Status of a rule: defect unless the profile says otherwise. </summary>
        public string StatusOf(string rule) {
            return Rules.TryGetValue(rule, out var s) ? s.Status : "defect";
        }

        public RuleStatus Describe(string rule) {
            return Rules.TryGetValue(rule, out var s) ? s : null;
        }

        public string Summary() {
            var sb = new StringBuilder();
            sb.Append(Version).Append(" (").Append(VersionSource).Append("), ").Append(Base == Contract.V22 ? "2.2 lifecycle" : "2.1 lifecycle");
            var extras = new List<string>();
            if (RollbackOnlyInitCompleted) extras.Add("rollback destroys only systems whose Init completed");
            if (RejectSharedRunner) extras.Add("one runner in two slots is rejected");
            if (ReservedBootstrapNames) extras.Add("\"fixed\"/\"late\" reserved in EcsBootstrap");
            if (DuplicateNameSplit) extras.Add("a name of a system or group and of a phase is split between them");
            if (extras.Count > 0) {
                sb.Append("; 2.2.2 rules: ").Append(string.Join(", ", extras));
            }
            return sb.ToString();
        }

        /// <param name="version">Version to profile (package.json or --contract-version); null falls back to an API probe.</param>
        /// <param name="source">Where the version came from, for the report.</param>
        /// <param name="src">Package folder of the tree under test (its XML docs and CHANGELOG are read; docs/ next to it if present).</param>
        public static ContractProfile For(string version, string source, string src) {
            var p = new ContractProfile { _src = src };
            if (SemVer.TryParse(version, out var v)) {
                p.V = v;
                p.Version = version;
                p.VersionSource = source;
            } else {
                // No version: the API decides between the two lifecycle models.
                p.V = Api.HasAddGroup ? V220 : new SemVer(2, 1, 0);
                p.Version = p.V + "?";
                p.VersionSource = "API probe (no package version)";
            }
            p.Base = p.V >= V220 ? Contract.V22 : Contract.V21;
            p.ExplicitVersion = source != null && source.StartsWith("--contract-version", StringComparison.Ordinal);
            if (src != null) {
                string docs = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(src)) ?? ".", "docs");
                p.DocsDir = Directory.Exists(docs) ? docs : null;
            }
            // The decisions of 2.2.2 rest on its sentences: a tree whose own documents carry one is held to it (also when
            // package.json lags behind), a tree without those documents by its version, and --contract-version 2.2.2 on purpose.
            if (p.V22) {
                p.RollbackOnlyInitCompleted = p.Decided222(Stand.Runner.Rules.RollbackDestroysNoInit, RollbackClaims222);
                p.RejectSharedRunner = p.Decided222(Stand.Runner.Rules.SharedRunnerAccepted, SharedRunnerClaims222);
                p.ReservedBootstrapNames = p.Decided222(Stand.Runner.Rules.BootstrapReservedName, ReservedNameClaims222);
                p.DuplicateNameSplit = p.Decided222(Stand.Runner.Rules.DuplicateNameRelease, DuplicateNameClaims222);
            }
            p.Decide();
            return p;
        }

        // ------------------------------------------------------------------ the 2.2.2 sentences

        private static readonly (string Source, string Text)[] RollbackClaims222 = {
            ("xml:Systems/SystemsRunner.cs", "the systems whose Init completed before the failure and that implement IDestroySystem are destroyed in reverse order"),
            ("xml:Systems/ISystem.cs", "A system without IInitSystem is never destroyed by a rollback, only by Destroy()."),
            ("docs:concepts/runner.md", "the systems whose Init completed before the failure get Destroy, if they implement IDestroySystem, in reverse order"),
            ("changelog", "the rollback of a failed SystemsRunner.Init destroys only the systems whose Init completed and that implement IDestroySystem, at every nesting level"),
        };

        private static readonly (string Source, string Text)[] SharedRunnerClaims222 = {
            ("xml:Systems/SystemsRunner.cs", "A runner can be registered only once: in one slot of one parent."),
            ("docs:concepts/runner.md", "a second registration of a runner (or one inside itself), throw in every build"),
            ("changelog", "a SystemsRunner can be registered only once, in one slot of one parent, in every build"),
        };

        private static readonly (string Source, string Text)[] ReservedNameClaims222 = {
            ("xml:Unity/EcsBootstrap.cs", "The names \"fixed\" and \"late\" are reserved in the update runner"),
            ("docs:unity/bootstrap.md", "so both are reserved in the update runner: do not register a system, group or phase there under either of them"),
            ("changelog", "Awake now throws right after Configure, naming the system and saying that EcsBootstrap reserves these names for its phases"),
        };

        private static readonly (string Source, string Text)[] DuplicateNameClaims222 = {
            ("docs:concepts/runner.md", "A system or group and a phase with one name split it, in either order"),
            ("docs:unity/bootstrap.md", "The name then refers to two registrations"),
        };

        // Per rule decided by 2.2.2: whether the tree makes the promise, and the sentences found.
        private readonly Dictionary<string, (bool On, bool Missing, List<Claim> Found)> _v222 = new Dictionary<string, (bool, bool, List<Claim>)>();

        private bool Decided222(string rule, (string Source, string Text)[] claims) {
            var checkedClaims = claims.Select(c => Check(rule, c.Source, c.Text)).ToList();
            Claims.AddRange(checkedClaims);
            var present = checkedClaims.Where(c => c.State == "present").ToList();
            bool available = checkedClaims.Any(c => c.State != "unavailable");
            // An explicit --contract-version decides alone; otherwise the version or a sentence the tree's documents already carry.
            bool on = ExplicitVersion ? V >= V222 : present.Count > 0 || V >= V222;
            // A 2.2.2 tree whose documents no longer carry any of the sentences: the rule holds, as a question.
            bool missing = present.Count == 0 && available && V >= V222 && !ExplicitVersion;
            _v222[rule] = (on, missing, present);
            return on;
        }

        /// <summary> The status of a rule 2.2.2 decided: a defect where its sentence is in the tree (or the contract is given explicitly), else a question. </summary>
        private void Set222(string rule, string basis) {
            var (on, missing, found) = _v222.TryGetValue(rule, out var x) ? x : (true, false, new List<Claim>());
            if (missing) {
                Set(rule, "question", "2.2.2", basis + " None of the 2.2.2 sentences of this rule is in the documents of this tree: the promise may have been reworded or withdrawn; update the rule before treating a violation as a defect.");
                return;
            }
            string where = found.Count > 0 ? " Found in this tree: " + string.Join(", ", found.Select(c => c.Source)) + "."
                : ExplicitVersion ? " Judged by --contract-version " + Version + " (the sentences come from the 2.2.2 documents)."
                : " This tree carries none of the documents with the sentence; judged by the version " + Version + ".";
            Set(rule, "defect", "2.2.2", basis + where);
        }

        // ------------------------------------------------------------------ the rules whose status depends on the version

        private void Decide() {
            // R1. 2.2.0/2.2.1: the documents disagree; 2.2.2: the author's decision.
            if (!V22) {
                Set(Stand.Runner.Rules.RollbackDestroysNoInit, "off", null, "2.1 contract: a failed Init does not roll back, so there is no rollback Destroy to check.");
            } else if (RollbackOnlyInitCompleted) {
                Set222(Stand.Runner.Rules.RollbackDestroysNoInit,
                    "2.2.2 (runner.md: \"the systems whose Init completed before the failure get Destroy, if they implement IDestroySystem, in reverse order ... neither is a system without IInitSystem\"; " +
                    "ISystem.cs: \"A system without IInitSystem is never destroyed by a rollback, only by Destroy()\"): the Init rollback calls Destroy only on systems whose Init completed — " +
                    "never on an Init-less system, neither in the runner whose Init failed nor in a nested runner the rollback tears down.");
            } else {
                Set(Stand.Runner.Rules.RollbackDestroysNoInit, "question", "2.2.2",
                    "The documents of 2.2.0 and 2.2.1 disagree: runner.md and the XML of Init say that every IDestroySystem registered before the failed system " +
                    "gets Destroy (and V220RunnerTests pins that call), while ISystem.cs says Destroy is \"Called once on shutdown\" and runner.md gives the rollback the purpose " +
                    "of undoing Init. The author resolved it for 2.2.2: only systems whose Init completed are destroyed by the rollback.");
            }

            // R4. Up to 2.2.1 the bootstrap's phase names are not reserved anywhere; 2.2.2 makes the clash a clear error.
            if (ReservedBootstrapNames) {
                Set(Stand.Runner.Rules.BootstrapNameCollision, "off", null,
                    "2.2.2 replaces it with " + Stand.Runner.Rules.BootstrapReservedName + " (a user name \"fixed\"/\"late\" in update gets a clear error naming the reserved names).");
                Set222(Stand.Runner.Rules.BootstrapReservedName,
                    "2.2.2 (bootstrap.md, Reserved names: \"both are reserved in the update runner\"; \"Under KENSEI_DEBUG, Awake throws InvalidOperationException right after Configure, naming the system\"): " +
                    "a user system or group named \"fixed\" or \"late\" in EcsBootstrap's update runner is a clear error that names the reserved names (release checks nothing).");
            } else {
                Set(Stand.Runner.Rules.BootstrapNameCollision, "question", "2.2.2",
                    "Awake throws the documented KENSEI_DEBUG duplicate-name check. bootstrap.md names the phases \"fixed\" and \"late\" but reserves neither name, and no " +
                    "document says what a user name equal to a phase name does. The author resolved it for 2.2.2: a clear error naming the reserved names.");
                Set(Stand.Runner.Rules.BootstrapReservedName, "off", null, "Checked from 2.2.2 on.");
            }

            // R3. 2.2.0/2.2.1: "always equals the group's slot"; 2.2.2 forbids one runner in two slots altogether.
            if (!V22) {
                Set(Stand.Runner.Rules.SharedSlots, "defect", null, "2.1 contract: the slot of an unnamed group is the parent's own flag (checked by the 2.1 model).");
            } else if (RejectSharedRunner) {
                Set(Stand.Runner.Rules.SharedSlots, "off", null, "2.2.2 rejects the second registration of a runner, so a runner never sits in two slots.");
            } else {
                ByClaims(Stand.Runner.Rules.SharedSlots, V220,
                    "runner.md 2.2: the IsEnabled of a group \"always equals the group's slot in the parent\"; the XML of IsEnabled says the same. Registering one runner in two " +
                    "slots is not documented either way, but the slot that reports \"enabled\" no longer runs its group.",
                    ("docs:concepts/runner.md", "it always equals the group's slot in the parent"),
                    ("xml:Systems/SystemsRunner.cs", "for an inline group it always equals the parent's run slot"));
            }
            if (RejectSharedRunner) {
                Set222(Stand.Runner.Rules.SharedRunnerAccepted,
                    "2.2.2 (XML SystemsRunner: \"A runner can be registered only once: in one slot of one parent. A second registration, in the same parent or another one, and adding a runner to " +
                    "itself or to a runner nested in it throw InvalidOperationException in every build\"): the second registration of a runner must throw.");
            } else {
                Set(Stand.Runner.Rules.SharedRunnerAccepted, "off", null, "Up to 2.2.1 registering one runner twice is accepted and not documented; checked from 2.2.2 on.");
            }
            // CHANGELOG 2.2.2: "A rejected call registers nothing."
            Set(Stand.Runner.Rules.SharedRunnerRejectState, RejectSharedRunner ? "question" : "off", null,
                "Whether a rejected registration may leave an entry behind in the parent: the CHANGELOG of 2.2.2 says \"A rejected call registers nothing\", the XML and runner.md do not.");
            // The Shared property of a runner registered in two slots: up to 2.2.1 such a registration is described neither as
            // allowed nor as forbidden; 2.2.2 forbids it, so the case cannot arise.
            Set(Stand.Runner.Rules.SharedPropertyTwoSlots, !V22 || RejectSharedRunner ? "off" : "question", null,
                RejectSharedRunner ? "2.2.2 rejects the second registration of a runner, so a runner never sits in two slots."
                    : "XML Shared: \"Shared data passed to systems in Init\" — for a runner in two slots, which no document of this version allows or forbids (2.2.2 forbids it): a contract question.");

            if (V22) {
                // R2. The 2.2.0 sentences and their 2.2.2 rewording ("one flat AggregateException ... at any nesting depth").
                ByClaims(Stand.Runner.Rules.RollbackAggregate, V220,
                    "runner.md and the XML of Init (2.2.0+): the AggregateException holds the original exception first, followed by the rollback errors; 2.2.2: one flat AggregateException at any depth.",
                    ("xml:Systems/SystemsRunner.cs", "an AggregateException holds the original exception first"),
                    ("docs:concepts/runner.md", "whose first inner exception is the original one"),
                    ("changelog", "an AggregateException holds the original exception followed by the rollback errors"),
                    ("xml:Systems/SystemsRunner.cs", "one flat AggregateException holds the original exception first"),
                    ("docs:concepts/runner.md", "never an AggregateException nested in another"),
                    ("changelog", "It now throws one flat AggregateException at any depth"));
                // The 2.2.1 promise about the start state of a group switched off before registration.
                ByClaims(Stand.Runner.Rules.GroupStartState, V221,
                    "2.2.1 documents that the slot of a group starts as the group is: a group switched off with SetActive(false) before AddGroup (or an unnamed Add) starts disabled. " +
                    "2.2.0 only promises that the slot and the group's IsEnabled agree.",
                    ("xml:Systems/SystemsRunner.cs", "The slot starts as the group is at registration"),
                    ("docs:concepts/runner.md", "The slot starts as the group is when it is registered"),
                    ("changelog", "starts the group's slot as the group is"));
            }
            // Promises older than every version the stand models.
            ByClaims(Stand.Runner.Rules.SharedProperty, new SemVer(2, 0, 0),
                "XML of SystemsRunner.Shared (2.0.0+): \"Shared data passed to systems in Init\".",
                ("xml:Systems/SystemsRunner.cs", "Shared data passed to systems in Init"));
            if (DuplicateNameSplit) {
                Set222(Stand.Runner.Rules.DuplicateNameRelease,
                    "2.2.2 (runner.md: \"the lookup keeps one registration per kind: SetActive, IsActive and IndexOf(name) reach the last run system or inline group registered under the name, " +
                    "and a phase only when no system or group has it; GetRunner reaches the last group or phase. Of two systems or groups with one name, the second replaces the first ... " +
                    "A system or group and a phase with one name split it, in either order\").");
            } else {
                ByClaims(Stand.Runner.Rules.DuplicateNameRelease, new SemVer(2, 1, 0),
                    "runner.md (2.1.0+): in release a second registration under a used name \"silently replaces the first in the name lookup\".",
                    ("docs:concepts/runner.md", "silently replaces the first in the name lookup"));
            }
        }

        private void Set(string rule, string status, string since, string basis) {
            Rules[rule] = new RuleStatus { Rule = rule, Status = status, Since = since, Basis = basis };
        }

        // A promise that appeared in version `since`. Newer trees: a defect, unless the tree carries the documents
        // and none of them says it any more. Older trees: a question, unless the tree's own documents already say it.
        private void ByClaims(string rule, SemVer since, string basis, params (string Source, string Text)[] claims) {
            var checkedClaims = claims.Select(c => Check(rule, c.Source, c.Text)).ToList();
            Claims.AddRange(checkedClaims);
            var present = checkedClaims.Where(c => c.State == "present").ToList();
            bool available = checkedClaims.Any(c => c.State != "unavailable");
            if (V >= since) {
                if (available && present.Count == 0) {
                    Set(rule, "question", since.ToString(), basis + " None of these sentences is in the documents of this tree (" +
                                                             string.Join("; ", checkedClaims.Where(c => c.State == "absent").Select(c => c.Source + ": \"" + c.Text + "\"")) +
                                                             "): the promise may have been reworded or withdrawn; update the rule before treating a violation as a defect.");
                } else {
                    Set(rule, "defect", since.ToString(), basis + (present.Count > 0 ? " Found in this tree: " + string.Join(", ", present.Select(c => c.Source)) + "." : ""));
                }
            } else if (present.Count > 0) {
                Set(rule, "defect", since.ToString(), basis + " The package version is " + V + ", but this tree's own documents already make the promise (" +
                                                      string.Join(", ", present.Select(c => c.Source)) + ").");
            } else {
                Set(rule, "question", since.ToString(), basis + " The documents of " + V + " do not make this promise yet (it appeared in " + since + ").");
            }
        }

        private Claim Check(string rule, string source, string text) {
            var claim = new Claim { Rule = rule, Source = source, Text = text };
            string haystack = TextOf(source);
            claim.State = haystack == null ? "unavailable" : haystack.Contains(Normalize(text)) ? "present" : "absent";
            return claim;
        }

        private string TextOf(string source) {
            if (_texts.TryGetValue(source, out var cached)) {
                return cached;
            }
            string text = null;
            try {
                if (source.StartsWith("xml:", StringComparison.Ordinal) && _src != null) {
                    string path = Path.Combine(_src, source.Substring(4));
                    if (File.Exists(path)) {
                        // Only the XML documentation comments: a sentence in the code itself is not a promise.
                        var lines = File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.StartsWith("///", StringComparison.Ordinal)).Select(l => l.Substring(3));
                        text = Normalize(string.Join(" ", lines));
                    }
                } else if (source == "changelog" && _src != null) {
                    string path = Path.Combine(_src, "CHANGELOG.md");
                    text = File.Exists(path) ? Normalize(File.ReadAllText(path)) : null;
                } else if (source.StartsWith("docs:", StringComparison.Ordinal) && DocsDir != null) {
                    string path = Path.Combine(DocsDir, source.Substring(5));
                    text = File.Exists(path) ? Normalize(File.ReadAllText(path)) : null;
                }
            } catch (IOException) {
                text = null;
            }
            _texts[source] = text;
            return text;
        }

        /// <summary> Collapse whitespace and drop Markdown/XML markup so that a sentence matches across line breaks. </summary>
        private static string Normalize(string s) {
            // Tags such as <summary>, <see cref="..."/> or OneFrame<T>, never across a line (Markdown has "<<<" includes).
            s = Regex.Replace(s, @"</?[A-Za-z][^<>\n]*>", " ");
            s = s.Replace("`", "").Replace("*", "").Replace("&lt;", "<").Replace("&gt;", ">");
            return Regex.Replace(s, @"\s+", " ").Trim();
        }
    }
}
