using System;
using System.Collections.Generic;
using System.Linq;

namespace KenseiStand.FuzzCore {
    // What one side recorded while it ran an operation, at the log position Pos (the log length at that
    // moment; both logs are equal before their first difference, so positions before it line up):
    //   - a decision of the reference model that a documented promise dictates (Claim names the promise),
    //   - a contract-silent behaviour the model copies from the reference implementation (Mirror names the
    //     contract question; an alternative reading of it exists, see ModelSide.Readings),
    //   - a situation (a slot got a new occupant, what a stale handle pointed at, a stale call was accepted).
    // Facts make the failure signature (the mechanism, not just the violated rule), decide the defect family
    // and give the claims a finding rests on, and with them its status for the version under test.
    public readonly struct Fact {
        public readonly string Key;
        public readonly int Slot;
        public readonly int Pos;
        public readonly string Claim;
        public readonly string Mirror;
        public readonly bool Real;
        public readonly long Seq;       // recording order on its side
        // A decision: the model chose what to do next here (a claim or a mirrored question), as opposed to a situation
        // it observed (which may still name the claim that makes the situation matter).
        public readonly bool Decision;

        public Fact(string key, int slot, int pos, string claim, string mirror, bool real, long seq, bool decision) {
            Key = key;
            Slot = slot;
            Pos = pos;
            Claim = claim;
            Mirror = mirror;
            Real = real;
            Seq = seq;
            Decision = decision;
        }

        public override string ToString() => Key + "@" + Pos + (Claim != null ? " [" + Claim + "]" : "") + (Mirror != null ? " {" + Mirror + "}" : "");
    }

    // A documented promise the stand checks, with the first package version whose documents make it.
    public sealed class Claim {
        public string Id;
        public string Since;
        public string Source;
        public string Text;
    }

    // The contract profile of the tree under test: its package version (package.json) and which claims its
    // documents already make. A finding that rests on a claim made only by later documents is a contract
    // question for this tree, not a defect.
    public static class Contract {
        public static string PackageVersion;      // as written in package.json; null when unknown (every claim active)
        public static bool Unreleased;            // the tree's CHANGELOG starts with an [Unreleased] section
        public static string LastRelease;         // the newest released version the CHANGELOG names ("## [2.2.1]"), or null
        // The version whose documents the tree has: the package version, or, when the CHANGELOG has unreleased
        // changes and package.json still names its last release, the next patch version (a working copy between
        // releases already documents them). A package.json newer than the CHANGELOG's last release is the release
        // in the making itself (2.2.2 with an [Unreleased] section on top of [2.2.1] documents 2.2.2, not 2.2.3).
        public static string DocsVersion;

        // The behaviour the documents of this version describe, where a later version changed the contract: the
        // model follows the documents of the tree (2.2.1 and older: the old behaviour, 2.2.2 and newer: the new one).
        // Unknown version: the newest documents (as for the claims, the stand does not invent questions).
        public static bool Since(string version) => DocsVersion == null || Compare(DocsVersion, version) >= 0;

        // The contract changes of 2.2.2 (CHANGELOG [Unreleased] of the 2.2.2 candidate, Fixed and Changed).
        public static bool V222 => Since("2.2.2");

        public static readonly Dictionary<string, Claim> Claims = new(StringComparer.Ordinal);

        private static void C(string id, string since, string source, string text) =>
            Claims[id] = new Claim { Id = id, Since = since, Source = source, Text = text };

        static Contract() {
            C("alive-iff-component", "2.0.0", "concepts/entities.md, FAQ, architecture.md",
              "An entity is alive if and only if it has at least one component; removing the last one destroys it, and a freed slot holds nothing.");
            C("entity-handles", "2.0.0", "concepts/entities.md (Handles vs. indices), architecture.md (Generation changes on reuse)",
              "An Entity handle names one entity: once its slot is reused by another entity, the old handle does not reach the new one.");
            C("typed-listener", "2.0.0", "IComponentListener XML doc",
              "OnAdded runs after the component is stored and the filters are updated; OnRemoved runs before the removal (data intact); one notification per Add and per removal.");
            C("filter-listener", "2.0.0", "IFilterListener XML doc, concepts/filters.md",
              "OnEntityAdded when an entity starts matching, OnEntityRemoved when it stops; callbacks run inside the structural change, the entity is alive on enter and may already be dying on leave.");
            C("world-listener", "2.0.0", "IWorldEventListener XML doc, guides/world-events.md",
              "OnEntityCreated after the entity's components are in place; OnComponentAdded/Removed after the change and the filter update; OnEntityDestroyed before the components are removed (dead, still readable).");
            C("listener-snapshot", "2.0.0", "architecture.md (Event ordering), guides/world-events.md",
              "Listener lists are copy-on-write: a dispatch loops over the array it captured at its start, so adding or removing listeners mid-dispatch neither shifts the loop nor skips or repeats a listener.");
            C("event-order", "2.0.0", "architecture.md (Event ordering)",
              "Add: filter listeners, OnComponentAdded, OnAdded. Remove: OnRemoved, filter listeners, OnComponentRemoved. DestroyEntity: OnEntityDestroyed first. CreateEntity/CopyEntity: OnEntityCreated last.");
            C("filter-set", "2.0.0", "concepts/filters.md, IFilterListener XML doc",
              "A filter is the set of alive entities that have every Inc type, none of the Exc types and, when Any is given, one of them; the world keeps it up to date on every structural change.");
            C("iteration", "2.1.0", "concepts/filters.md (Iteration contract)",
              "Inside foreach over a filter it is safe to destroy the current entity, add or remove its components and create entities; new entities are not visited.");
            C("storage-consistency", "2.0.0", "architecture.md (Data model)",
              "Pools, component masks, component counts and groups describe the same components.");
            C("op-outcome", "2.0.0", "XML docs of the operation",
              "The operation's documented outcome: its return value, or the exception it is documented to throw.");
            C("terminates", "2.0.0", "XML docs and guides (listeners may modify the world)",
              "An operation terminates when the scripted listeners terminate under the documented semantics; a process crash or hang is never acceptable.");
            C("auto-reset", "2.0.0", "IAutoReset / IAutoCopy docs",
              "AutoReset runs on the value a removal takes away; AutoCopy runs on the value CopyEntity copies.");
            C("change-tracking", "2.0.0", "guides/change-tracking.md",
              "A tracked pool stamps a new change version on Add, Modify and MarkChanged.");
            C("oneframe", "2.0.0", "guides/one-frame.md",
              "OneFrame<T> removes every T at the end of the runner's Run.");
            C("copy-entity", "2.0.0", "World.CopyEntity XML doc, concepts/entities.md",
              "CopyEntity creates a new entity with copies of all components of the source; OnEntityCreated fires after all of them are copied.");
            C("copy-dead", "2.1.0", "guides/debug-mode.md, concepts/entities.md",
              "CopyEntity of a dead entity returns Entity.Null in release and throws under KENSEI_DEBUG.");
            C("warmup", "2.0.0", "World.Warmup XML doc",
              "Warmup creates a temporary entity, adds a default component of every registered type and destroys it; existing entities and their data are not touched.");
            C("warmup-listeners", "2.1.1", "World.Warmup XML doc, CHANGELOG 2.1.1",
              "Typed and filter listeners see Warmup's temporary entity; if a listener throws, the temporary entity is still destroyed.");
            C("pool-add-gone", "2.1.1", "ComponentPool.Add XML doc, CHANGELOG 2.1.1",
              "A component a listener removes before Add returns gets no further OnAdded, and the returned ref points at a scratch value whose writes are discarded.");
            C("pool-remove-reentrant", "2.1.1", "ComponentPool.Remove XML doc, CHANGELOG 2.1.1",
              "A typed listener may remove the same component again from OnRemoved: the component is removed once and every listener still gets exactly one OnRemoved.");
            C("filter-readd", "2.1.1", "CHANGELOG 2.1.1 (Fixed)",
              "An IFilterListener.OnEntityRemoved that adds the removed component back keeps the entity in the remaining Inc filters of that type.");
            C("debug-handles", "2.1.0", "guides/debug-mode.md, architecture.md (KENSEI_DEBUG layer)",
              "Under KENSEI_DEBUG, Add/Get/Has/Remove with a dead or stale Entity throw, and pool Add(int) on a dead slot throws; only the entity being destroyed may be touched through its dead handle.");
            C("addevent-stale", "2.1.1", "EventBuffer XML doc (AddEvent), CHANGELOG 2.1.1",
              "Under KENSEI_DEBUG AddEvent on a stale handle throws, whether or not the slot's new occupant has a buffer.");
            C("eventbuffer-lists", "2.0.0", "EventBuffer XML doc",
              "EventBuffer lists are pooled: AutoReset returns the list on remove, and a pooled list has one owner.");
            C("copy-own-list", "2.1.1", "EventBuffer / Listeners XML docs, CHANGELOG 2.1.1",
              "CopyEntity gives the copy its own EventBuffer and Listeners list.");
            C("cb-set", "2.1.1", "CommandBuffer.Set XML doc, CHANGELOG 2.1.1",
              "An overwrite is a plain write that change tracking sees; AutoReset is not run on the old value, so a modified copy sharing its EventBuffer list can be written back.");
            C("cb-dead-skipped", "2.0.0", "CommandBuffer XML doc",
              "Commands targeting an Entity that is dead at Playback time are skipped.");
            C("group-registration", "2.1.1", "guides/groups.md (Rules), architecture.md (Owning groups)",
              "A pool can be owned by one group: a second group over the same type throws, and so does a group that lists a type twice; the same types in the same order return the existing group. RegisterGroup checks before it claims a pool, so a rejected group owns nothing. Membership is exact, and the owned pools' dense arrays stay aligned.");
            C("copy-overwrite", "2.1.1", "CHANGELOG 2.1.1, IAutoCopy docs",
              "A CopyEntity that overwrites a component (one a listener added during the copy) writes through Modify and runs AutoReset on the overwritten value for IAutoCopy types.");

            // ---- 2.2.2: contract changes (CHANGELOG [Unreleased] of the candidate, the XML docs and guides that go with it).
            // The model follows them on a tree whose documents are 2.2.2 or newer, and the old behaviour on older trees.
            C("warmup-renew", "2.2.2", "World.Warmup XML doc, CHANGELOG 2.2.2 (Fixed), guides/world-lifecycle.md",
              "If a listener destroys the temporary entity, the remaining types go to a new temporary entity, never to the freed slot or to an entity the listener created in it. A type a listener already added to the temporary entity is skipped.");
            C("created-alive-only", "2.2.2", "World.CreateEntity / World.CopyEntity XML docs, CHANGELOG 2.2.2 (Fixed), guides/world-events.md",
              "If a listener of the component destroys the entity, OnEntityCreated does not fire and the returned handle is dead (CreateEntity, CopyEntity, CommandBuffer creations).");
            C("drain-has", "2.2.2", "World.DestroyEntity XML doc, CHANGELOG 2.2.2 (Fixed), guides/world-events.md",
              "Listeners of a component removed here see the components not removed yet (Has, Get): DestroyEntity clears each component's mask bit as it removes it.");
            C("dying-leaves-filters", "2.2.2", "CHANGELOG 2.2.2 (Fixed), architecture.md (OnComponentRemoved of a dying entity)",
              "A dying entity leaves filters without entering any: its removals take it out of Inc and Any filters and put it into no Exc filter.");
            C("world-add-lost", "2.2.2", "CHANGELOG 2.2.2 (Changed), guides/world-events.md",
              "If a filter listener removes the component again while it is being added (or destroys the entity), the OnComponentRemoved of that removal goes out and the OnComponentAdded does not.");
            C("world-remove-kept", "2.2.2", "CHANGELOG 2.2.2 (Changed), guides/world-events.md",
              "If a filter listener adds the component back while it is being removed, the OnComponentAdded of that addition goes out and the OnComponentRemoved does not; if it destroys the entity and creates one that takes its slot, no OnComponentRemoved for the slot follows the new entity's events.");
            C("pool-add-readd-ref", "2.2.2", "ComponentPool.Add XML doc, CHANGELOG 2.2.2 (Changed)",
              "If a listener removed the component and added it to the same entity again, the ref returned by Add points at that new component.");
            C("cb-set-returns-list", "2.2.2", "CHANGELOG 2.2.2 (Fixed)",
              "CommandBuffer.Set overwriting an EventBuffer<T> with a buffer that brings its own list returns the replaced list to the pool: a copy that shares the replaced list is stale from then on.");
            C("copy-empty-null", "2.2.2", "World.CopyEntity XML doc, CHANGELOG 2.2.2 (Fixed)",
              "A source with no components left (inside the removal of its last component) is not copied: CopyEntity returns Entity.Null and creates nothing, in every build.");
            C("dying-readd-enters", "2.2.2", "IFilterListener XML doc, concepts/filters.md",
              "A component that a listener adds to an entity whose DestroyEntity is running lets the dying entity (IsAlive false) enter the filters it matches, until the destroy removes that component too.");
            C("warmup-events-suppressed", "2.2.2", "guides/world-lifecycle.md",
              "World event listeners do not see what listeners do during Warmup: events are suppressed for the whole call.");
            C("eventbuffer-copy-store", "2.2.2", "EventBuffer XML doc, CommandBuffer.Set XML doc",
              "A pooled list is held by one entity at most: a stored value whose list went back to the pool, was rented again or belongs to another entity is stored with an empty list of its own, or, when it replaces the entity's buffer, leaves that buffer as it is.");
        }

        public static void SetVersion(string version, bool unreleased, string lastRelease = null) {
            PackageVersion = string.IsNullOrWhiteSpace(version) ? null : version.Trim();
            Unreleased = unreleased;
            LastRelease = string.IsNullOrWhiteSpace(lastRelease) ? null : lastRelease.Trim();
            DocsVersion = PackageVersion;
            if (PackageVersion != null && unreleased && (LastRelease == null || Compare(PackageVersion, LastRelease) <= 0)) {
                var v = Parse(PackageVersion);
                DocsVersion = v[0] + "." + v[1] + "." + (v[2] + 1);
            }
        }

        // 2.2.1, 2.3.0-rc1 -> (2, 2, 1), (2, 3, 0). A pre-release is compared as its release: its documents
        // already make that version's promises.
        public static int[] Parse(string v) {
            var r = new int[3];
            if (string.IsNullOrEmpty(v)) {
                return r;
            }
            var core = v.Split('-', '+')[0].Split('.');
            for (int i = 0; i < 3 && i < core.Length; i++) {
                int.TryParse(core[i], out r[i]);
            }
            return r;
        }

        public static int Compare(string a, string b) {
            var x = Parse(a);
            var y = Parse(b);
            for (int i = 0; i < 3; i++) {
                if (x[i] != y[i]) {
                    return x[i].CompareTo(y[i]);
                }
            }
            return 0;
        }

        // A claim is active when the tree's documents make it: its "since" version is not newer than the
        // documents' version. Unknown version or claim: active (the stand does not invent questions).
        public static bool Active(string claimId) {
            if (DocsVersion == null || claimId == null || !Claims.TryGetValue(claimId, out var c)) {
                return true;
            }
            return Compare(c.Since, DocsVersion) <= 0;
        }

        public static string Describe(string claimId) =>
            Claims.TryGetValue(claimId, out var c) ? c.Id + " (since " + c.Since + ", " + c.Source + "): " + c.Text : claimId;
    }
}
