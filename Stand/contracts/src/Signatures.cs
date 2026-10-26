using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Stand.Contracts {
    /// <summary>
    /// Signatures: the mechanism and the place of a judgement, stable between runs and versions (no
    /// ordinals, seeds, line numbers or probe values). Two cases with one signature are the same bug
    /// reached through different members; a new signature is a new bug even when it belongs to a known
    /// family (G1 reached through another throw site, say). The baseline comparison works on them.
    /// </summary>
    public static class Signatures {
        /// <summary> Of one judgement of one case; null for the harmless ones (Ok). </summary>
        public static string Of(CaseResult r, Judgement j) {
            var o = r.Outcome ?? new Outcome();
            string cls = r.Verdict == Verdict.NotRun ? "n/r" : o.Class;
            string place = Place(r);
            switch (j.Rule) {
                case "contract":
                case "contract-later":
                case "contract-missing":
                    return j.Rule + "|" + j.ClaimId + "|" + cls + "|" + place;
                case "xml-exception":
                    return "xml-exception|" + r.MemberId + "|" + cls;
                case "state":
                case "state-extended":
                case "state-after-query":
                case "state-partial":
                    // The scenario is how the place was reached, not part of the mechanism: a stale and a null
                    // handle that leave the same damage through the same code are one bug.
                    return j.Rule + "|" + (j.ClaimId ?? "-") + "|" + place + "|" + string.Join(",", j.Damage ?? new List<string>());
                case "argnull":
                    return "argnull|" + Short(o.TopFrame) + "|" + (o.ParamName ?? "?");
                case "suspicious":
                    return "suspicious|" + cls + "|" + place;
                case "null-deferred":
                    return "null-deferred|" + r.Member + "|" + string.Join(",", (r.SmokeDiffs ?? new List<ProbeDiff>()).Select(d => d.Probe + "=" + Change(d)).OrderBy(x => x, StringComparer.Ordinal));
                case "null-registered":
                    return "null-registered|" + r.Member;
                case "crash":
                case "hang":
                case "resource":
                case "crash-undefined":
                    return j.Rule + "|" + r.Member + "|" + r.Scenario + "|" + r.Variant;
                case "unstable":
                    return "unstable|" + r.Key;
                default:
                    return j.Rule + "|" + r.Member + "|" + r.Scenario;
            }
        }

        /// <summary> Where the mechanism lives: the first package frame of the exception, or the member that returned. </summary>
        public static string Place(CaseResult r) {
            var o = r.Outcome;
            if (o != null && o.Threw) {
                return Short(o.Site ?? o.TopFrame ?? o.ExShort ?? "?");
            }
            return "returned:" + r.Target.Split('<')[0] + "." + r.Name;
        }

        public static string Short(string frame) {
            if (frame == null) {
                return "?";
            }
            return frame.StartsWith("KenseiECS.", StringComparison.Ordinal) ? frame.Substring("KenseiECS.".Length) : frame;
        }

        // IsAlive(e0) and IsAlive(stale) are one probe for the mechanism; values are not part of it.
        private static readonly Regex HandleArgs = new Regex(@"\((?:e\d|stale|reused|Entity\.Null|stale\.Index|\d+)\)", RegexOptions.Compiled);

        /// <summary> The kind of damage: the probes that differ, with their handle arguments folded, and how they changed. </summary>
        public static List<string> Damage(IEnumerable<ProbeDiff> diffs) {
            return diffs.Select(d => HandleArgs.Replace(d.Probe, "(*)") + "=" + Change(d)).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        }

        private static string Change(ProbeDiff d) {
            if (d.Actual.StartsWith("ex:", StringComparison.Ordinal)) {
                return d.Actual;
            }
            if (d.Expected.StartsWith("ex:", StringComparison.Ordinal)) {
                return "no-longer-" + d.Expected;
            }
            return "value";
        }
    }
}
