using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace KenseiStand.Generator {
    /// <summary>
    /// One documented promise an oracle rule rests on: where it is written, the exact sentence and
    /// the first package version whose documents contain it. A rule whose promise the tree under
    /// test does not make (an older version, or documents that no longer contain the sentence)
    /// reports a contract question, never a defect.
    /// </summary>
    public sealed class Claim {
        public string Id;
        /// <summary> First package version whose documents contain <see cref="Quote"/>. </summary>
        public string Since;
        /// <summary> Document, relative to the repository root (CHANGELOG lives in the package folder). </summary>
        public string Doc;
        public string Section;
        public string Quote;

        // Per tree (Claims.For):
        public bool Active;
        /// <summary> active (the sentence is there), by-version (the document is not in the tree; judged by the version), later (the promise is newer than the tree), missing (the document no longer has the sentence). </summary>
        public string State;
        public string Note;
    }

    /// <summary> The claims of one tree, with the version they were judged against. </summary>
    public sealed class ClaimSet {
        public string Version;
        /// <summary> The CHANGELOG has a non-empty [Unreleased] section: the documents may be ahead of package.json. </summary>
        public bool Unreleased;
        public bool DocsAvailable;
        public List<Claim> Claims = new();

        public Claim Get(string id) => id == null ? null : Claims.FirstOrDefault(c => c.Id == id);

        /// <summary>
        /// Whether a rule resting on <paramref name="id"/> reports defects: no claim, an unknown one, or an active one.
        /// "a|b" rests on either claim (the same promise in the words of two versions): active when one of them is.
        /// </summary>
        public bool IsActive(string id) => id == null || Alternatives(id).Any(a => Get(a)?.Active != false);

        /// <summary> Whether this tree's documents make the promise of a claim (strict: an unknown claim does not count). </summary>
        public bool Has(string id) => Get(id)?.Active == true;

        public static IEnumerable<string> Alternatives(string id) => (id ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// The versioned contract profile of the generator: every promise the oracle checks, with the
    /// version it first appears in. Profile selection (Contract 2.1 / 2.2) picks the layout rules of
    /// a version line; these claims gate individual rules inside it, so a promise made later than the
    /// tree, or one its documents dropped, turns the rule's violations into questions.
    /// </summary>
    public static class Claims {
        public const string Guide = "docs/guides/source-generator.md";
        public const string Changelog = "KenseiECS/CHANGELOG.md";

        public static List<Claim> Catalog() => new() {
            new() { Id = "generated-init", Since = "2.1.0", Doc = Guide, Section = "What gets generated",
                Quote = "The generated `Init` fills the marked fields in declaration order, then calls `OnInit`." },
            new() { Id = "inject-table", Since = "2.1.0", Doc = Guide, Section = "What gets generated",
                Quote = "`world.Filter().Inc<...>().Exc<...>().Any<...>().End()`" },
            new() { Id = "diagnostics-are-errors", Since = "2.1.0", Doc = Guide, Section = "Diagnostics",
                Quote = "All diagnostics are errors." },
            new() { Id = "kecs006-2.1", Since = "2.1.1", Doc = Changelog, Section = "[2.1.1] Generator",
                Quote = "KECS006 (a derived system where its own or the base `Init` would never run" },
            new() { Id = "inheritance", Since = "2.2.0", Doc = Guide, Section = "Inheritance",
                Quote = "A system with injected fields can derive from another one." },
            // The order of a chained Init: 2.2.0 and 2.2.1 called base.Init before the derived fields, 2.2.2 after them
            // and before the derived OnInit. Each version's rule rests on its own sentence.
            new() { Id = "inheritance-order", Since = "2.2.0", Doc = Guide, Section = "Inheritance",
                Quote = "each gets its own fields filled and its own `OnInit` called: the base first, then the derived class." },
            new() { Id = "order-chain-220", Since = "2.2.0", Doc = Guide, Section = "Inheritance",
                Quote = "the derived one overrides it and calls `base.Init` before its own fields" },
            new() { Id = "inheritance-order-222", Since = "2.2.2", Doc = Guide, Section = "Inheritance",
                Quote = "The derived fields are filled first, then the base `Init` runs (the base fields and the base `OnInit`), then the derived `OnInit`" },
            new() { Id = "kecs006-2.2", Since = "2.2.0", Doc = Guide, Section = "Inheritance",
                Quote = "`KECS006` reports the setups that cannot be chained" },
            new() { Id = "kecs006-reason", Since = "2.2.0", Doc = Guide, Section = "Diagnostics (KECS006)",
                Quote = "The message names the reason." },
            new() { Id = "kecs012", Since = "2.2.0", Doc = Guide, Section = "Inheritance",
                Quote = "is `KECS012`, because it would run the base `Init` (fields and `OnInit`) a second time" },
            new() { Id = "layout21-works", Since = "2.2.0", Doc = Guide, Section = "Inheritance",
                Quote = "Over a base `Init` that cannot be overridden, the 2.1 layout still works" },
            new() { Id = "layout21-kecs006-222", Since = "2.2.2", Doc = Guide, Section = "Inheritance",
                Quote = "A class in this layout without a `base.Init` call is `KECS006`" },
            new() { Id = "layout21-new-unrelated-222", Since = "2.2.2", Doc = Guide, Section = "Inheritance",
                Quote = "a `new Init` in between that does not implement `IInitSystem.Init`, with no injected fields above it" },
            // KECS006 of the 2.1 layout counts the injected fields of a base in another assembly only when it can see them.
            new() { Id = "kecs006-lib-visible-222", Since = "2.2.2", Doc = Guide, Section = "Inheritance",
                Quote = "of a base in another assembly only the `protected` and `public` ones are seen, as for `KECS013`, so a base there with only `private` injected fields is not reported" },
            // Hand-written overrides of a public Init that an explicit IInitSystem.Init next to it does not call: a derived class
            // that lists IInitSystem itself is chained over them (the interface maps to its generated Init).
            new() { Id = "chain-over-overrides-222", Since = "2.2.2", Doc = Guide, Section = "Inheritance",
                Quote = "a derived class that lists `IInitSystem` itself is still chained over them, since the interface then maps to its generated `Init`" },
            new() { Id = "virtual-init-222", Since = "2.2.2", Doc = Guide, Section = "Inheritance",
                Quote = "The generated `Init` is `public virtual` in every class that is not `sealed`" },
            new() { Id = "generic-helper-allowed", Since = "2.2.2", Doc = Guide, Section = "Inheritance",
                Quote = "A generic helper such as `Init<T>(World, SharedData)` whose type parameter the two arguments cannot infer does not take the call and is allowed." },
            new() { Id = "hook-222", Since = "2.2.2", Doc = Guide, Section = "A base with its own OnInit",
                Quote = "Override the hook, and it runs once, after the derived fields are filled" },
            new() { Id = "hook-layout21-222", Since = "2.2.2", Doc = Guide, Section = "A base with its own OnInit",
                Quote = "an override of a hook that this base `Init` can call is `KECS006`" },
            new() { Id = "kecs013", Since = "2.2.2", Doc = Guide, Section = "Inheritance",
                Quote = "an override of a generated `Init` without that call leaves the base fields `null` and skips the base `OnInit`, which is the warning `KECS013`" },
            new() { Id = "kecs013-lib-visible", Since = "2.2.2", Doc = Guide, Section = "Inheritance",
                Quote = "for a base in another assembly it is recognised only by injected fields visible from the derived assembly, `protected` or `public`" },
            new() { Id = "diagnostics-are-errors-222", Since = "2.2.2", Doc = Guide, Section = "Diagnostics",
                Quote = "All diagnostics are errors, except the warning KECS013." },
            new() { Id = "upgrade-delete-line", Since = "2.2.0", Doc = Guide, Section = "Upgrading from 2.1",
                Quote = "that call is `KECS012`: delete the line" },
            new() { Id = "upgrade-order-222", Since = "2.2.2", Doc = Guide, Section = "Upgrading from 2.1",
                Quote = "so the base `Init` still sees the derived fields, as it did when the call stood at the top of `OnInit`" },
            new() { Id = "init-hiding-new", Since = "2.2.0", Doc = Changelog, Section = "[2.2.0] Generator",
                Quote = "Where the generated `Init` hides an inherited one, it is declared `new` (no more CS0108/CS0114 in generated code)." },
            new() { Id = "no-second-error", Since = "2.2.0", Doc = Changelog, Section = "[2.2.0] Generator",
                Quote = "A derived class of a base that failed with its own error (for example KECS001) gets no second error." },
        };

        /// <summary>
        /// 2.2.0 and 2.2.1 describe the order after the upgrade from 2.1 twice, and the two sentences disagree: the
        /// generated Init "makes the call before the derived fields" and "The order stays the same, the base Init runs
        /// first" (on 2.1 the base ran from OnInit, after the derived fields). A migration pair whose base sees the
        /// derived fields null is a contract question there; 2.2.2 promises the 2.1 order (claim upgrade-order-222).
        /// </summary>
        public const string UpgradeContradiction220 =
            "docs/guides/source-generator.md 2.2.0/2.2.1, Upgrading from 2.1: 'delete the line, the generated Init makes the call before the derived fields. The order stays the same, the base Init runs first.' " +
            "The first sentence describes exactly this order, the second contradicts it (CHANGELOG 2.2.0: 'base fields, base OnInit, then derived fields and OnInit')";

        /// <summary> Judges every claim against the tree of <paramref name="src"/> (the KenseiECS package folder). </summary>
        public static ClaimSet For(string src, string version) {
            var set = new ClaimSet { Version = version };
            string root = string.IsNullOrEmpty(src) ? null : Path.GetDirectoryName(Path.GetFullPath(src));
            string changelog = string.IsNullOrEmpty(src) ? null : Path.Combine(src, "CHANGELOG.md");
            set.DocsAvailable = root != null && Directory.Exists(Path.Combine(root, "docs"));
            set.Unreleased = changelog != null && File.Exists(changelog) && HasUnreleased(File.ReadAllText(changelog));
            var texts = new Dictionary<string, string>();
            string Text(string doc) {
                if (texts.TryGetValue(doc, out var t)) {
                    return t;
                }
                string path = doc == Changelog ? changelog : root == null ? null : Path.Combine(root, doc.Replace('/', Path.DirectorySeparatorChar));
                t = path != null && File.Exists(path) ? Normalize(File.ReadAllText(path)) : null;
                texts[doc] = t;
                return t;
            }
            foreach (var c in Catalog()) {
                bool reached = CompareVersions(version, c.Since) >= 0;
                string text = Text(c.Doc);
                if (text != null) {
                    bool found = text.Contains(Normalize(c.Quote), StringComparison.Ordinal);
                    if (found) {
                        c.Active = true;
                        c.State = "active";
                        c.Note = reached ? "" : $"the sentence is already in this tree's {c.Doc} (package {version}, documents ahead of package.json)";
                    } else if (!reached) {
                        c.Active = false;
                        c.State = "later";
                        c.Note = $"the promise appears in the documents of {c.Since}; this tree is {version}";
                    } else {
                        c.Active = false;
                        c.State = "missing";
                        c.Note = $"{c.Doc} of this tree no longer contains the sentence the rule rests on (update Claims.cs)";
                    }
                } else {
                    c.Active = reached;
                    c.State = reached ? "by-version" : "later";
                    c.Note = reached ? $"{c.Doc} is not part of this tree; judged by the version ({version} >= {c.Since})"
                        : $"the promise appears in the documents of {c.Since}; this tree is {version}";
                }
                set.Claims.Add(c);
            }
            return set;
        }

        private static bool HasUnreleased(string changelog) {
            var m = Regex.Match(changelog, @"(?ms)^## \[Unreleased\]\s*$(.*?)(?=^## |\z)");
            return m.Success && m.Groups[1].Value.Trim().Length > 0;
        }

        /// <summary> Markdown-insensitive form: no backticks or emphasis, whitespace collapsed. </summary>
        public static string Normalize(string text) => Regex.Replace(text.Replace("`", "").Replace("**", ""), @"\s+", " ");

        /// <summary> Numeric comparison of the leading major.minor.patch ("?" counts as the newest). </summary>
        public static int CompareVersions(string a, string b) {
            int[] Parse(string v) {
                if (string.IsNullOrEmpty(v) || v == "?") {
                    return new[] { int.MaxValue, 0, 0 };
                }
                var parts = Regex.Matches(v, @"\d+").Select(m => int.TryParse(m.Value, out int n) ? n : 0).Take(3).ToList();
                while (parts.Count < 3) {
                    parts.Add(0);
                }
                return parts.ToArray();
            }
            var x = Parse(a);
            var y = Parse(b);
            for (int i = 0; i < 3; i++) {
                if (x[i] != y[i]) {
                    return x[i].CompareTo(y[i]);
                }
            }
            return 0;
        }
    }
}
