using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Stand.Contracts {
    /// <summary>
    /// The version profile of the tree under test: the package version from its package.json. Every
    /// curated claim knows the version whose documentation first made the promise (Claim.Since). The
    /// documentation of the tree decides first: a promise is held against the code where its wording
    /// is written. When the wording is not there, the profile says why: the promise is only made by
    /// a later version (the outcome is a question for this one, not a defect), or the version should
    /// have it and the documentation was rewritten (a question too, and a stand warning, so that a
    /// rewritten sentence never switches a check off silently). When the tree has no docs/ folder at
    /// all, a docs-page promise counts as made by every version from Since on.
    /// </summary>
    public sealed class VersionProfile {
        public string PackageVersion;   // "version" of package.json, as written; null when there is none
        public Version Numeric;         // its numeric part (2.2.0-rc2 -> 2.2.0)

        public static VersionProfile Load(string srcDir) {
            var p = new VersionProfile();
            var path = srcDir != null ? Path.Combine(srcDir, "package.json") : null;
            if (path != null && File.Exists(path)) {
                var m = Regex.Match(File.ReadAllText(path), "\"version\"\\s*:\\s*\"([^\"]+)\"");
                if (m.Success) {
                    p.PackageVersion = m.Groups[1].Value;
                    p.Numeric = Parse(p.PackageVersion);
                }
            }
            return p;
        }

        /// <summary> 2.2.1 -> 2.2.1; 2.2.0-rc2 -> 2.2.0 (a release candidate has the documentation of its release). </summary>
        public static Version Parse(string s) {
            if (s == null) {
                return null;
            }
            var m = Regex.Match(s, @"^\s*v?(\d+)\.(\d+)(?:\.(\d+))?");
            if (!m.Success) {
                return null;
            }
            return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
        }

        public bool Known => Numeric != null;

        /// <summary> True when this version is since or newer; false when older or unknown. </summary>
        public bool AtLeast(string since) {
            var s = Parse(since);
            return Numeric != null && s != null && Numeric >= s;
        }

        public override string ToString() => PackageVersion ?? "unknown";
    }
}
