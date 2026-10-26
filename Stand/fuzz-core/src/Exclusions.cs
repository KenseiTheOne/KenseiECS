using System;
using System.Collections.Generic;
using System.Linq;

namespace KenseiStand.FuzzCore {
    // --exclude-families W1,W2,...: the generator does not build scenarios that are known to hit those unfixed
    // families, so that a saturated search (on 2.2.0 a third of all scenarios fail on known families) looks for
    // the rest. Each family is excluded by removing the shape that triggers its mechanism (see Classifier for
    // the mechanisms): reaction steps that destroy or re-add at the dangerous moment, the motif built for the
    // family, a filter constraint. What remains is still random; a failure of an excluded family that still
    // shows up means the exclusion is incomplete and is reported like any other.
    public static class Exclusions {
        public static readonly string[] Known = { "W1", "W2", "W3", "W3c", "W4", "W5", "W6", "N1", "N2", "N3", "N4" };

        // Motifs built for a family (Generator.MotifNames).
        public static readonly Dictionary<string, string[]> MotifFamilies = new() {
            ["warmup-destroy"] = new[] { "W1" },
            ["mutual-remove"] = new[] { "W2" },
            ["replace-in-slot"] = new[] { "W3" },
            ["exc-filter"] = new[] { "W4" },
            ["writeback"] = new[] { "W5" },
            ["stale-addevent"] = new[] { "W6" },
            ["copy-destroy"] = new[] { "N1" },
            ["unlisten-continue"] = new[] { "N2" },
            ["readd"] = new[] { "W3c" },
            ["dying-readd"] = new[] { "W3c" },
        };

        public static HashSet<string> Parse(string csv) {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(csv)) {
                return set;
            }
            foreach (var part in csv.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)) {
                string f = part.Trim();
                if (f == "all" || f == "known") {
                    set.UnionWith(Known);
                } else if (Array.IndexOf(Known, f) >= 0) {
                    set.Add(f);
                } else {
                    throw new ArgumentException("unknown family " + f + " (known: " + string.Join(",", Known) + ", or all)");
                }
            }
            return set;
        }

        public static bool MotifHits(string motif, HashSet<string> exclude) =>
            exclude != null && exclude.Count > 0 && MotifFamilies.TryGetValue(motif, out var fams) && fams.Any(exclude.Contains);

        private static bool Self(Step st) => st.Who == null || st.Who == "self";
        private static bool SelfRemoving(Step st) => (st.A == "destroy" || st.A == "remove" || st.A == "poolRemove") && Self(st);
        private static bool PartnerRemoving(Step st) => (st.A == "destroy" || st.A == "remove" || st.A == "poolRemove") && st.Who == "partner";
        private static bool SelfAdding(Step st) => (st.A == "add" || st.A == "poolAdd" || st.A == "addEvent" || st.A == "subscribe") && Self(st);
        private static bool Creating(Step st) => st.A == "create" || st.A == "copy";

        // Callbacks that run while a component is being removed or an entity destroyed.
        private static bool RemovalTime(ListenerDef l, Rule r) =>
            (l.Kind == "typed" && r.On == "removed") || (l.Kind == "filter" && r.On == "leave") ||
            (l.Kind == "world" && (r.On == "destroyed" || r.On == "compRemoved"));

        private static void Strip(Scenario s, Func<ListenerDef, Rule, bool> where, Func<Step, bool> drop) {
            foreach (var l in s.Listeners) {
                foreach (var r in l.Rules) {
                    if (where(l, r)) {
                        r.Do.RemoveAll(st => drop(st));
                    }
                }
            }
        }

        private static bool AnyStep(Scenario s, Func<Step, bool> p) => s.Listeners.Any(l => l.Rules.Any(r => r.Do.Any(p)));

        private static bool AnyRuleStep(Scenario s, Func<ListenerDef, Rule, bool> where, Func<Step, bool> p) =>
            s.Listeners.Any(l => l.Rules.Any(r => where(l, r) && r.Do.Any(p)));

        public static void Apply(Scenario s, HashSet<string> ex) {
            if (ex == null || ex.Count == 0) {
                return;
            }
            // W1: a listener destroys Warmup's temporary entity. Typed and filter listeners see it during Warmup.
            if (ex.Contains("W1") && s.Ops.Any(o => o.K == "warmup")) {
                Strip(s, (l, r) => l.Kind == "typed" || l.Kind == "filter", SelfRemoving);
            }
            // W2: a removal callback removes or destroys the partner, whose removal comes back (mutual or interleaved).
            if (ex.Contains("W2")) {
                Strip(s, RemovalTime, PartnerRemoving);
            }
            // W3: an entity dies inside an operation on it and a reaction creates the entity that takes its slot.
            if (ex.Contains("W3") && AnyStep(s, SelfRemoving)) {
                Strip(s, (l, r) => true, Creating);
            }
            // W3c: a component is put back on its entity while its OnRemoved dispatch is in progress: by OnRemoved itself,
            // or by a filter or world listener of a removal nested in that dispatch (OnRemoved destroying its own entity).
            if (ex.Contains("W3c")) {
                Strip(s, (l, r) => l.Kind == "typed" && r.On == "removed", SelfAdding);
                if (AnyRuleStep(s, RemovalTime, SelfAdding)) {
                    Strip(s, (l, r) => l.Kind == "typed" && r.On == "removed", SelfRemoving);
                }
            }
            // W4: a filter listener removes the just-added type again before the Exc filters are updated.
            if (ex.Contains("W4") && s.Ops.Any(o => o.K == "filter" && o.Exc != null && o.Exc.Length > 0)) {
                Strip(s, (l, r) => l.Kind == "filter", st => (st.A == "remove" || st.A == "poolRemove") && Self(st));
            }
            // W5: a copy of an EventBuffer written back (Add or CommandBuffer.Set) after its component was removed.
            if (ex.Contains("W5")) {
                s.Ops.RemoveAll(o => o.K == "writeBack" || o.K == "cbSetClip");
                Strip(s, (l, r) => true, st => st.A == "writeBack" || st.A == "cbSetClip");
            }
            // W6 (KENSEI_DEBUG): a dead handle used from a callback while another entity is being destroyed.
            if (ex.Contains("W6")) {
                Strip(s, (l, r) => true, st => st.A.StartsWith("stale", StringComparison.Ordinal));
            }
            // N1: a listener destroys the copy while CopyEntity copies into it.
            if (ex.Contains("N1") && (s.Ops.Any(o => o.K == "copy") || AnyStep(s, st => st.A == "copy"))) {
                Strip(s, (l, r) => (l.Kind == "typed" && r.On == "added") || l.Kind == "filter" || (l.Kind == "world" && r.On == "compAdded"), SelfRemoving);
            }
            // N2: a removal callback unsubscribes a listener and the nested Remove resumes on the new listener array.
            if (ex.Contains("N2")) {
                Strip(s, RemovalTime, st => st.A == "unlisten");
            }
            // N3: CopyEntity from inside the removal of the source's last component.
            if (ex.Contains("N3")) {
                Strip(s, RemovalTime, st => st.A == "copy");
            }
            // N4: with two mask words, the drain zeroes the lower word first, so an entity enters Inc<upper> Exc<lower>.
            if (ex.Contains("N4")) {
                foreach (var o in s.Ops.Where(o => o.K == "filter" && o.Exc != null && o.Exc.Length > 0)) {
                    var incAny = (o.Inc ?? Array.Empty<string>()).Concat(o.Any ?? Array.Empty<string>()).ToList();
                    if (incAny.Count == 0) {
                        continue;
                    }
                    int top = incAny.Max(k => TypeMap.Of(TypeMap.Parse(k)) >> 6);
                    o.Exc = o.Exc.Where(k => (TypeMap.Of(TypeMap.Parse(k)) >> 6) >= top).ToArray();
                }
            }
            // A rule left without steps stays: its callbacks are still logged and compared.
        }
    }
}
