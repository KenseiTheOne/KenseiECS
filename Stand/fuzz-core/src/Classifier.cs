using System;
using System.Collections.Generic;
using System.Linq;

namespace KenseiStand.FuzzCore {
    // Classification of a failure: the class of the violated rule, the claims it rests on, its defect family
    // (matched by mechanism, from the facts of the failing operation) and its status for the tree under test.
    public static class Classifier {
        public static string ClassOf(string rule) {
            if (rule == null) return "other";
            if (rule.StartsWith("C-")) return "crash";
            if (rule.StartsWith("X-missing-throw") || rule.StartsWith("X-nested-missing-throw")) return "validation";
            if (rule.StartsWith("X-")) return "exception";
            if (rule.StartsWith("N-")) return "notification";
            if (rule.StartsWith("I-")) return "iteration";
            if (rule.StartsWith("L-")) return "ownership";
            if (rule.StartsWith("R-") || rule.StartsWith("S-")) return "state";
            return "other";
        }

        // ------------------------------------------------------------------ claims

        // The documented promises a violated rule checks by itself, whatever the model decided on the way.
        // AddEvent operations rest on the AddEvent promise, CopyEntity of a dead source on the debug-mode table.
        public static List<string> BaseClaims(string rule, string opKind, string step) {
            var c = new List<string>();
            rule ??= "";
            if (rule.StartsWith("C-")) {
                c.Add("terminates");
            } else if (rule == "N-dying-enter") {
                c.Add("filter-listener");
            } else if (rule.StartsWith("N-order:") || rule.StartsWith("N-reorder:")) {
                c.Add("event-order");
            } else if (rule.StartsWith("N-")) {
                if (rule.Contains("typed-")) c.Add("typed-listener");
                else if (rule.Contains("filter-")) c.Add("filter-listener");
                else if (rule.Contains("world-")) c.Add("world-listener");
                else c.Add("typed-listener");
            } else if (rule.StartsWith("X-missing-throw") || rule.StartsWith("X-nested-missing-throw")) {
                string what = (step ?? "") + " " + (opKind ?? "");
                if (what.IndexOf("ddEvent", StringComparison.Ordinal) >= 0) c.Add("addevent-stale");
                else if (opKind == "copy") c.Add("copy-dead");
                else c.Add("debug-handles");
            } else if (rule.StartsWith("X-")) {
                c.Add("op-outcome");
            } else if (rule == "S-handle") {
                c.Add("op-outcome");
            } else if (rule.StartsWith("I-")) {
                c.Add("iteration");
            } else if (rule.StartsWith("L-")) {
                c.Add("eventbuffer-lists");
            } else if (rule.StartsWith("R-dead") || rule == "R-alive-empty") {
                c.Add("alive-iff-component");
            } else if (rule == "R-cb-free-slot") {
                c.Add("alive-iff-component");
                c.Add("typed-listener");
            } else if (rule == "R-cb-destroyed") {
                c.Add("world-listener");
            } else if (rule.StartsWith("R-filter") || rule == "S-filter") {
                c.Add("filter-set");
            } else if (rule == "R-group" || rule == "S-group") {
                c.Add("group-registration");
            } else if (rule.StartsWith("R-")) {
                c.Add("storage-consistency");
            } else if (rule == "S-version") {
                c.Add("change-tracking");
            } else if (rule == "S-autoreset") {
                c.Add("auto-reset");
            } else if (rule.StartsWith("S-")) {
                c.Add("op-outcome");
            }
            return c;
        }

        // Status of a failure for the tree under test: "question" when a claim it rests on is made only by later
        // documents than the tree's (the version profile), "defect" otherwise. "regression" (behaviour changed
        // where the contract is silent) is decided by the executor, which can re-run the scenario.
        public static string StatusOf(IEnumerable<string> claims, out List<string> inactive) {
            inactive = claims.Where(c => !KenseiStand.FuzzCore.Contract.Active(c)).Distinct().ToList();
            return inactive.Count > 0 ? "question" : "defect";
        }

        // The documented promise a rule checks (repro headers and the report).
        public static string Contract(string rule) {
            if (rule == null) return "";
            if (rule.StartsWith("C-crash")) return "Operations on a world terminate when the scripted listeners terminate under the documented semantics (the model's run is finite); a process crash is never acceptable.";
            if (rule.StartsWith("C-")) return "Operations terminate when the documented semantics terminate.";
            if (rule.StartsWith("N-reorder:")) return "The same notifications arrive in another order within one kind of callback; architecture.md describes the order, the public docs do not promise it.";
            if (rule.StartsWith("N-extra:typed-removed") || rule.StartsWith("N-missing:typed-removed") || rule.StartsWith("N-payload:typed-removed"))
                return "ComponentPool.Remove XML doc: a typed listener may remove the same component again from OnRemoved; the component is removed once and every listener still gets exactly one OnRemoved. Architecture, Event ordering: a dispatch loops over the listener array it captured at its start.";
            if (rule.StartsWith("N-extra:typed-added") || rule.StartsWith("N-missing:typed-added") || rule.StartsWith("N-payload:typed-added"))
                return "ComponentPool.Add XML doc and 2.1.1 changelog: OnAdded is called after the component is stored; once the component is removed before Add returns, the remaining typed listeners get no OnAdded and the returned ref points at a scratch value.";
            if (rule.Contains("filter-enter") || rule.Contains("filter-leave") || rule == "N-dying-enter")
                return "IFilterListener: OnEntityAdded when an entity starts matching, OnEntityRemoved when it stops; the entity is alive on enter. A filter is the set of entities that have every Inc type, none of the Exc types and, if Any is given, one of them.";
            if (rule.Contains("world-"))
                return "IWorldEventListener XML doc and world-events guide: OnEntityCreated after the entity's first component; OnComponentAdded/Removed after the change and the filter update; OnEntityDestroyed before the components are removed.";
            if (rule.StartsWith("X-nested-missing-throw") || rule == "X-missing-throw")
                return "debug-mode.md: under KENSEI_DEBUG, Add/Get/Has/Remove with a dead or stale Entity throw; AddEvent XML doc: a stale handle throws, whether or not the slot's new occupant has a buffer.";
            if (rule.StartsWith("X-nested-unexpected-throw") || rule == "X-unexpected-throw")
                return "The operation is documented to succeed here (no misuse: the handle is alive or being destroyed, the component is absent before Add).";
            if (rule.StartsWith("X-")) return "The operation's documented outcome (return value or exception).";
            if (rule.StartsWith("I-")) return "concepts/filters.md, Iteration contract: inside foreach it is safe to destroy the current entity, add or remove its components and create entities; new entities are not visited.";
            if (rule.StartsWith("L-")) return "EventBuffer<T>: lists are pooled and returned on remove; a pooled list has one owner (architecture.md explains that a struct copy written back after a remove would otherwise hand a pooled list to two entities).";
            if (rule.StartsWith("R-dead") || rule == "R-alive-empty")
                return "An entity is alive if and only if it has at least one component (entities.md, FAQ); DestroyEntity removes all its components and frees the slot.";
            if (rule.StartsWith("R-filter")) return "Filter: the set of alive entities that have every Inc type, none of the Exc types and one of the Any types; Count, Contains and iteration agree.";
            if (rule == "R-group" || rule == "S-group") return "guides/groups.md: membership is exact; a pool belongs to one group, a second group over its type throws; the group keeps the dense arrays of its pools aligned (Entities[i] is the member at index i of every owned pool).";
            if (rule.StartsWith("R-")) return "Pools, masks, component counts and groups describe the same components (architecture.md, Data model).";
            if (rule.StartsWith("S-")) return "The world equals the reference model of the documented semantics (same entities, components and values, filters, pool counts, change versions).";
            return "";
        }

        // ------------------------------------------------------------------ families

        // A family is a known defect mechanism. It is assigned only when the facts of the failing operation show
        // that mechanism; a failure that shows none of them is a new signature (NEW) with its own minimal repro,
        // even if its rule is one a known family also violates.
        public sealed class FamilyDef {
            public string Id;
            public string Title;
            public string Contract;
            public string Severity;
            public string Mechanism;      // the facts that identify it, for the report
            public string[] Keys;         // those facts: a signature of the family names only them
            public Func<string, HashSet<string>, bool> Match;
        }

        private static bool Has(HashSet<string> f, string k) => f.Contains(k);
        private static bool HasPrefix(HashSet<string> f, string p) => f.Any(x => x.StartsWith(p, StringComparison.Ordinal));

        private static bool FilterRule(string rule) =>
            rule.StartsWith("R-filter") || rule == "S-filter" || rule.Contains("filter-enter") || rule.Contains("filter-leave");

        // W2's symptoms: OnRemoved notified twice or not at all (in or out of order), or a recursion that never ends. The
        // rule names the model's line at the first difference; the real world's line there counts too (a second OnRemoved
        // where the model has the next event is classified by that next event).
        private static bool W2Rule(string rule) => rule.StartsWith("C-") || rule.Contains("typed-removed");

        // Checked in this order: the more specific mechanism first (a W1 or N1 scenario also replaces an entity).
        public static readonly List<FamilyDef> Families = new() {
            new FamilyDef {
                Id = "N4", Keys = new[] { "dying-enter" }, Severity = "low", Mechanism = "rule N-dying-enter (the model saw a dying entity enter a filter with nothing re-added)",
                Title = "A dying entity enters a filter: OnEntityAdded for an entity that is being destroyed",
                Contract = "IFilterListener XML doc and concepts/filters.md: callbacks run inside the structural change, so the entity is alive on enter (and may already be dying on leave).",
                Match = (r, f) => r == "N-dying-enter",
            },
            new FamilyDef {
                Id = "W5", Keys = new[] { "writeback-stale" }, Severity = "high", Mechanism = "writeback-stale (an EventBuffer copy written back after its component was removed) with an ownership or content rule",
                Title = "EventBuffer copy written back after its component was removed: one pooled list for two owners",
                Contract = "EventBuffer<T> XML doc: lists are pooled, AutoReset returns the list on remove. CommandBuffer.Set XML doc: a modified copy sharing its EventBuffer list can be written back. architecture.md: a struct copy written back after a remove would otherwise hand a pooled list to two entities.",
                Match = (r, f) => Has(f, "writeback-stale") && (r.StartsWith("L-") || r.StartsWith("N-payload") || r == "S-value"),
            },
            new FamilyDef {
                Id = "W6", Keys = new[] { "stale-accepted:free+destroying" }, Severity = "medium", Mechanism = "stale-accepted:free+destroying (a dead, not reused handle accepted while some DestroyEntity is in progress)",
                Title = "KENSEI_DEBUG: the stale-handle check is skipped while any entity is being destroyed",
                Contract = "debug-mode.md: under KENSEI_DEBUG, Add/Get/Has/Remove with a dead or stale Entity throw. architecture.md: ValidateHandle accepts a dead handle only when its own slot is mid-destroy. AddEvent XML doc: under KENSEI_DEBUG a stale handle throws.",
                Match = (r, f) => Has(f, "stale-accepted:free+destroying"),
            },
            new FamilyDef {
                Id = "W1", Keys = new[] { "warmup-temp-died" }, Severity = "high", Mechanism = "warmup-temp-died (a listener destroyed Warmup's temporary entity)",
                Title = "Warmup keeps adding default components into the slot after a listener destroyed its temporary entity",
                Contract = "World.Warmup XML doc: Warmup creates a temporary entity, adds a default component of every registered type and destroys it; existing entities and their data are not touched. world-lifecycle.md: the slot is freed right after and reused by the next CreateEntity. Nothing may stay on a freed slot (entities.md: an entity is alive if and only if it has a component).",
                Match = (r, f) => Has(f, "warmup-temp-died"),
            },
            new FamilyDef {
                Id = "N1", Keys = new[] { "copy-dst-died" }, Severity = "high", Mechanism = "copy-dst-died (a listener destroyed the copy while CopyEntity was copying into it)",
                Title = "CopyEntity keeps copying into the copy after a listener destroyed it (components on a free slot or on the slot's new entity)",
                Contract = "World.CopyEntity XML doc: creates a new entity with copies of all components; OnEntityCreated fires after all components are copied. Components go to the copy only: a freed slot holds nothing, and an entity that took the slot is not the copy.",
                Match = (r, f) => Has(f, "copy-dst-died"),
            },
            new FamilyDef {
                Id = "N3", Keys = new[] { "copy-src-empty" }, Severity = "low", Mechanism = "copy-src-empty with R-alive-empty (CopyEntity of an alive source that has no components left: inside the removal of its last component)",
                Title = "CopyEntity inside the removal of the last component creates a live entity without components",
                Contract = "entities.md and FAQ: an entity is alive if and only if it has at least one component; listeners never observe an entity without components (OnEntityCreated after the components are in place).",
                Match = (r, f) => r == "R-alive-empty" && Has(f, "copy-src-empty"),
            },
            new FamilyDef {
                Id = "W2", Keys = new[] { "reentrant-remove-interleaved" }, Severity = "critical", Mechanism = "reentrant-remove-interleaved (a component removed again from its own OnRemoved dispatch while a removal of the same type on another entity is in progress)",
                Title = "Re-entrant OnRemoved: mutual removal recurses without end (stack overflow), interleaved removal notifies twice",
                Contract = "ComponentPool.Remove XML doc (2.1.1): a typed listener may remove the same component again from OnRemoved; the component is removed once and every listener still gets exactly one OnRemoved. The documented semantics terminate; the process must not crash.",
                // The fact explains a failure only where the mechanism shows: the OnRemoved dispatch of that type (a second,
                // a missing or a reordered OnRemoved) or a recursion without end. A difference elsewhere in an operation that
                // also had an interleaved removal (world events, Has during a drain) is not W2.
                Match = (r, f) => Has(f, "reentrant-remove-interleaved") && (W2Rule(r) || Has(f, AtFirstDifference + "typed-removed")),
            },
            new FamilyDef {
                Id = "N2", Keys = new[] { "reentrant-remove-listeners-changed" }, Severity = "medium", Mechanism = "reentrant-remove-listeners-changed with N-missing or N-extra:typed-removed (the nested Remove resumes on a listener array changed mid-dispatch)",
                Title = "Re-entrant Remove resumes the OnRemoved dispatch on a changed listener array: a listener is skipped (or called twice)",
                Contract = "architecture.md, event ordering: listener lists are copy-on-write; a dispatch loops over the array it captured at its start, so a listener that removes listeners mid-dispatch neither shifts the loop nor gets skipped. ComponentPool.Remove XML doc: every listener gets exactly one OnRemoved.",
                Match = (r, f) => Has(f, "reentrant-remove-listeners-changed") && (r.StartsWith("N-missing:typed-removed") || r.StartsWith("N-extra:typed-removed")),
            },
            new FamilyDef {
                Id = "W3c", Keys = new[] { "readd-in-removed", "nested-removal-readded" }, Severity = "high", Mechanism = "readd-in-removed or nested-removal-readded (the same type re-added to the slot during the OnRemoved dispatch of its old component)",
                Title = "Remove does not notice that a listener removed and re-added the component: the new one is removed silently or loses OnRemoved",
                Contract = "IComponentListener.OnRemoved runs before each removal of a component; ComponentPool.Remove XML doc: every listener gets exactly one OnRemoved. A component a listener added again is a new component: it stays until removed, and its removal is notified like any other.",
                Match = (r, f) => Has(f, "readd-in-removed") || Has(f, "nested-removal-readded"),
            },
            new FamilyDef {
                Id = "W4", Keys = new[] { "removed-in-add-filter-update" }, Severity = "high", Mechanism = "removed-in-add-filter-update with a filter rule (a filter listener removed T again before Add<T> updated the Exc<T> filters)",
                Title = "Adding T drops the entity from Exc<T> filters after a filter listener already removed T again",
                Contract = "concepts/filters.md: a filter is the set of entities that have every Inc type, none of the Exc types and, if Any is given, one of them; World updates it on every structural change.",
                // The rule names the model's line at the first difference: a filter event of the real world there (the
                // Exc<T> leave the model does not send) shows the mechanism too.
                Match = (r, f) => Has(f, "removed-in-add-filter-update")
                                  && (FilterRule(r) || Has(f, AtFirstDifference + "filter-leave") || Has(f, AtFirstDifference + "filter-enter")),
            },
            new FamilyDef {
                Id = "W3", Keys = new[] { "add-replaced", "ref-replaced", "remove-replaced", "dispatch-replaced", "filter-update-replaced", "nested-removal-replaced" }, Severity = "high",
                Mechanism = "add-replaced, ref-replaced, remove-replaced, dispatch-replaced, filter-update-replaced or nested-removal-replaced (an operation in progress on an entity reaches the new entity in its slot)",
                Title = "Add/Remove/Create do not notice that a listener destroyed the entity and a new one took the slot: they act on the replacement",
                Contract = "ComponentPool.Add XML doc and 2.1.1 changelog: once the component is removed before Add returns, the remaining typed listeners get no OnAdded and the returned ref points at a scratch value. World.Remove removes the entity's own component; a slot reused by a new entity is a different entity (entities.md, handles vs. indices).",
                Match = (r, f) => Has(f, "add-replaced") || Has(f, "ref-replaced") || Has(f, "remove-replaced") || Has(f, "dispatch-replaced")
                                  || Has(f, "filter-update-replaced") || Has(f, "nested-removal-replaced"),
            },
        };

        public const string NewFamily = "NEW";

        public static readonly Dictionary<string, FamilyDef> ById = Families.ToDictionary(f => f.Id);

        // The facts a signature names: a known family's own facts (the rest of the operation's facts vary from seed
        // to seed and would split one mechanism into many signatures), every fact for a new signature.
        public static List<string> SignatureFacts(string family, IEnumerable<string> facts) {
            if (family != null && ById.TryGetValue(family, out var d)) {
                return facts.Where(f => d.Keys.Contains(f)).OrderBy(f => f, StringComparer.Ordinal).ToList();
            }
            return facts.OrderBy(f => f, StringComparer.Ordinal).ToList();
        }

        // When the facts show several families, the one whose own fact came first is the mechanism that put the run on
        // its way (a Warmup whose current Add went on with a new entity in the slot is W3,
        // even though Warmup then also finds its temporary entity dead, which alone would be W1). Ties: the order above.
        // The callback kinds of the two lines at the first difference, as pseudo-facts for the families whose mechanism
        // shows in a callback (they never enter a signature).
        public const string AtFirstDifference = "@first:";

        public static string FamilyOf(string rule, List<Fact> facts, IEnumerable<string> atFirst = null) {
            rule ??= "";
            var keys = new HashSet<string>(facts.Where(f => f.Mirror == null).Select(f => f.Key), StringComparer.Ordinal);
            foreach (var k in atFirst ?? Enumerable.Empty<string>()) {
                keys.Add(AtFirstDifference + k);
            }
            string best = NewFamily;
            (int pos, long seq) bestAt = (int.MaxValue, long.MaxValue);
            foreach (var def in Families) {
                if (!def.Match(rule, keys)) {
                    continue;
                }
                (int pos, long seq) at = (int.MaxValue, long.MaxValue);
                foreach (var f in facts) {
                    if (f.Mirror == null && def.Keys.Contains(f.Key)) {
                        var c = (f.Pos, f.Seq);
                        if (c.Item1 < at.pos || (c.Item1 == at.pos && c.Item2 < at.seq)) {
                            at = c;
                        }
                    }
                }
                if (best == NewFamily || at.pos < bestAt.pos || (at.pos == bestAt.pos && at.seq < bestAt.seq)) {
                    best = def.Id;
                    bestAt = at;
                }
            }
            return best;
        }

        public static string Title(string family) =>
            family != null && ById.TryGetValue(family, out var d) ? d.Title : "New signature: no known defect mechanism matches (triage it, its minimal repro is separate)";

        public static string FamilyContract(string family) => family != null && ById.TryGetValue(family, out var d) ? d.Contract : "";

        public static string FamilySeverity(string family) => family != null && ById.TryGetValue(family, out var d) ? d.Severity : "medium";
    }
}
