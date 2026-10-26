using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Stand.Contracts {
    /// <summary> XML documentation of one member, flattened to text. </summary>
    public sealed class DocEntry {
        public string Id;
        public string Summary = "";
        public string Remarks = "";
        public string Text = "";   // summary + remarks + params + returns + exceptions
        public readonly List<(string Cref, string Condition)> Exceptions = new();
    }

    /// <summary>
    /// The contract as written for the source tree under test: the XML documentation the compiler
    /// produced from it (Stand.Contracts.xml next to the binary), the docs/ pages of the same version
    /// and its CHANGELOG. A curated claim is checked only when its wording is present here, so a
    /// promise made in 2.2.1 is not held against 2.2.0.
    /// </summary>
    public sealed class Docs {
        public readonly Dictionary<string, DocEntry> Members = new();
        public readonly Dictionary<string, string> Pages = new();   // relative path -> normalized text
        public string Changelog = "";
        public string DocsRoot;
        public string XmlPath;

        public static Docs Load(string xmlPath, string docsDir, string srcDir) {
            var d = new Docs { XmlPath = xmlPath, DocsRoot = docsDir };
            if (xmlPath != null && File.Exists(xmlPath)) {
                var doc = XDocument.Load(xmlPath);
                foreach (var m in doc.Descendants("member")) {
                    var id = (string)m.Attribute("name");
                    if (id == null || !(id.StartsWith("M:KenseiECS.") || id.StartsWith("P:KenseiECS.") || id.StartsWith("T:KenseiECS.") || id.StartsWith("F:KenseiECS."))) {
                        continue;
                    }
                    var e = new DocEntry { Id = id };
                    e.Summary = Flatten(m.Element("summary"));
                    e.Remarks = Flatten(m.Element("remarks"));
                    foreach (var ex in m.Elements("exception")) {
                        e.Exceptions.Add((((string)ex.Attribute("cref") ?? "").Replace("T:", ""), Flatten(ex)));
                    }
                    e.Text = Normalize(string.Join(" ", m.Elements().Select(Flatten)));
                    d.Members[id] = e;
                }
            }
            if (docsDir != null && Directory.Exists(docsDir)) {
                foreach (var f in Directory.GetFiles(docsDir, "*.md", SearchOption.AllDirectories)) {
                    var rel = Path.GetRelativePath(docsDir, f).Replace('\\', '/');
                    if (Generated(rel)) {
                        continue;
                    }
                    d.Pages["docs/" + rel] = Normalize(File.ReadAllText(f));
                }
            }
            var changelog = srcDir != null ? Path.Combine(srcDir, "CHANGELOG.md") : null;
            if (changelog != null && File.Exists(changelog)) {
                d.Changelog = Normalize(File.ReadAllText(changelog));
            }
            return d;
        }

        /// <summary>
        /// Pages a clean checkout does not have (the repository's .gitignore): the API reference that
        /// docs/scripts/gen-api.mjs writes into docs/api/ (only docs/api/index.md is tracked), VitePress' cache and
        /// build output, node_modules. They are never read, so a tree gives the same report with or without a local
        /// docs build; the XML docs they are generated from are read from the sources.
        /// </summary>
        public static bool Generated(string rel) =>
            (rel.StartsWith("api/", StringComparison.Ordinal) && rel != "api/index.md")
            || rel.StartsWith(".vitepress/", StringComparison.Ordinal)
            || rel.StartsWith("node_modules/", StringComparison.Ordinal) || rel.Contains("/node_modules/");

        public bool HasPages => Pages.Count > 0;

        public DocEntry For(string id) {
            return id != null && Members.TryGetValue(id, out var e) ? e : null;
        }

        /// <summary> Where the phrase is written: a docs page, "xml:&lt;id&gt;", or CHANGELOG; null if nowhere. </summary>
        public string Find(string phrase, bool xmlFirst = false) {
            var p = Normalize(phrase);
            if (xmlFirst) {
                foreach (var kv in Members) {
                    if (kv.Value.Text.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) {
                        return "xml:" + kv.Key;
                    }
                }
            }
            // docs/guides/debug-mode.md first: it holds the release vs KENSEI_DEBUG table.
            foreach (var kv in Pages.OrderBy(x => x.Key.Contains("debug-mode") ? 0 : x.Key.StartsWith("docs/guides/") ? 1 : 2).ThenBy(x => x.Key, StringComparer.Ordinal)) {
                if (kv.Value.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) {
                    return kv.Key;
                }
            }
            foreach (var kv in Members) {
                if (kv.Value.Text.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) {
                    return "xml:" + kv.Key;
                }
            }
            if (Changelog.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) {
                return "KenseiECS/CHANGELOG.md";
            }
            return null;
        }

        /// <summary> Whether the member's own XML documentation contains the phrase. </summary>
        public bool MemberSays(string id, string phrase) {
            var e = For(id);
            return e != null && e.Text.IndexOf(Normalize(phrase), StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string Flatten(XElement e) {
            if (e == null) {
                return "";
            }
            var parts = new List<string>();
            foreach (var node in e.Nodes()) {
                if (node is XText t) {
                    parts.Add(t.Value);
                } else if (node is XElement x) {
                    switch (x.Name.LocalName) {
                        case "see":
                        case "seealso":
                            var cref = (string)x.Attribute("cref") ?? (string)x.Attribute("langword") ?? (string)x.Attribute("href") ?? "";
                            parts.Add(x.IsEmpty || x.Value.Length == 0 ? CrefName(cref) : x.Value);
                            break;
                        case "paramref":
                        case "typeparamref":
                            parts.Add((string)x.Attribute("name") ?? "");
                            break;
                        default:
                            parts.Add(Flatten(x));
                            break;
                    }
                }
            }
            return Normalize(string.Join("", parts));
        }

        // M:KenseiECS.World.Reserve(System.Int32,System.Int32) -> Reserve; T:KenseiECS.Filter -> Filter.
        private static string CrefName(string cref) {
            int colon = cref.IndexOf(':');
            if (colon >= 0) {
                cref = cref.Substring(colon + 1);
            }
            int paren = cref.IndexOf('(');
            if (paren >= 0) {
                cref = cref.Substring(0, paren);
            }
            // Listeners`1.Values -> Listeners.Values; Pool``1 -> Pool.
            cref = Regex.Replace(cref, "`+\\d+", "");
            int dot = cref.LastIndexOf('.');
            return dot >= 0 ? cref.Substring(dot + 1) : cref;
        }

        private static readonly Regex Spaces = new Regex(@"\s+", RegexOptions.Compiled);

        public static string Normalize(string s) {
            if (s == null) {
                return "";
            }
            s = s.Replace("`", "").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");
            return Spaces.Replace(s, " ").Trim();
        }
    }
}
