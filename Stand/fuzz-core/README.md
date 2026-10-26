# Stand / fuzz-core

Model-based random tester of the KenseiECS core: `World`, pools, filters, owning groups, the three kinds of listeners,
`CommandBuffer`, `EventBuffer`/`Listeners`, OneFrame cleanup, `CopyEntity` and `Warmup`. Random scenarios run on
the real world and on a reference model (an ECS on dictionaries that follows the documented contract) side by
side; after every top-level operation the two are compared: the outcome, every callback and nested reaction in
order, the internal consistency of the world, and the full state.

```
Stand/fuzz-core/run.sh --quick [--debug] [--src <KenseiECS folder>] [--flavor <label>]
Stand/fuzz-core/run.sh --full --minutes 10
Stand/fuzz-core/run.sh --replay <report dir>/min/sig-001.json [--debug] [--reading Q-created-after-death]
Stand/fuzz-core/run.sh --quick --exclude-families all          # look past the known, unfixed families
Stand/fuzz-core/run.sh --quick --baseline v2.2.0               # compare with the report of another tree
Stand/fuzz-core/run.sh --selftest [--debug]                     # mutation check of the module itself
```

`--quick` runs a fixed set of 30000 scenarios (the same seeds every run, about a minute on 8 cores), shrinks one
scenario per signature, writes an NUnit repro for each and checks that it fails on the tree. `--full` explores
for `--minutes`. Requirements: a .NET SDK 8 or newer (`$DOTNET`, `dotnet` on `PATH`, `$DOTNET_ROOT`,
`Stand/out/tools/dotnet` or the usual install folders) and `python3`. Output goes to
`Stand/out/reports/fuzz-core/<flavor>-<rel|dbg>/`: `report.md`, `report.json`, `outcomes.json` (every failing
seed and its signature), `min/` (minimal scenarios), `repro/` (NUnit repros) and `corpus-candidates/`.

## The contract of the tree's version

The model follows the documents of the tree under test. Its version is `package.json`; a `CHANGELOG.md` that starts
with `[Unreleased]` documents the next release: the package version when `package.json` already names a version newer
than the CHANGELOG's last release (2.2.2 on top of `[2.2.1]` is 2.2.2), otherwise the next patch version (a working copy
still at 2.2.0 with the 2.2.1 changes is 2.2.1). Where a version changed the contract, the model does what the documents
of the tree say (`Contract.V222`, the claims since 2.2.2 in `src/Mechanism.cs`), and the old behaviour on older trees:

| claim (since 2.2.2) | 2.2.2 and newer | up to 2.2.1 |
|---|---|---|
| `world-add-lost` | a filter listener that removes `T` again during `Add<T>` (or destroys the entity): no `OnComponentAdded` | `OnComponentAdded` follows the removal |
| `world-remove-kept` | a filter listener that adds `T` back during `Remove<T>` (or replaces the entity in the slot): no `OnComponentRemoved` | `OnComponentRemoved` comes last |
| `created-alive-only` | no `OnEntityCreated` for an entity a listener destroyed while it was made (`CreateEntity`, `CopyEntity`, Playback) | sent (question `Q-created-after-death`) |
| `warmup-renew` | `Warmup` goes on with a new temporary entity once a listener destroyed it, and skips a type a listener already added | it stops (W1), and adding an added type throws (question `Q-warmup-already-has`) |
| `drain-has` | `DestroyEntity` clears each mask bit as it removes the component: `Has` sees the components not removed yet | the mask word is zeroed first (question `Q-has-during-drain`) |
| `dying-leaves-filters` | a dying entity leaves its Inc and Any filters (an Any filter at the first removal) and enters no Exc filter | it may enter (N4) |
| `pool-add-readd-ref` | the `ref` of `Add` points at a component a listener removed and added again | a scratch value (N5) |
| `cb-set-returns-list` | `CommandBuffer.Set` with a buffer that brings a list returns the replaced list: older copies are stale | the list is dropped |
| `copy-empty-null` | `CopyEntity` of a source with no components left (inside the removal of its last component) returns `Entity.Null` and creates nothing | it creates an entity without components (N3) |
| `dying-readd-enters` | a component a listener adds to an entity whose `DestroyEntity` is running lets the dying entity enter the filters it matches (documented, `D-dying-enter`) | question `Q-dying-enter` |
| `warmup-events-suppressed` | world events are suppressed for the whole `Warmup`, also for what listeners do to other entities (documented, `D-warmup-nested-world-events`) | question `Q-warmup-nested-world-events` |
| `eventbuffer-copy-store` | a stored `EventBuffer` value whose list went back to the pool, was rented again or belongs to another entity gets an empty list of its own; replacing the entity's buffer with it (`CommandBuffer.Set`) leaves that buffer as it is | a stale copy's content is unknown (`EB(?)`), a copy of another entity's buffer is not run (`Q-cross-entity-copy`) |

On 2.2.2 the model keeps every `EventBuffer` list with the entity that holds it, whether it is free and the number of
its rent (the EventBuffer XML doc: "Every rent of a pooled list is numbered and the renting buffer keeps the number"),
and stores values as the documents say. So fuzz-core runs on 2.2.2 what it skips on older trees: a copy of one
entity's `EventBuffer` written to another entity (`writeBack`, `cbSetClip`, also to an entity created in the same
`CommandBuffer`), and stale copies written back after their component was removed, with the content compared exactly.
A `CommandBuffer.Set` with a buffer without a list over one that has a list returns that list too, as the
implementation does; the documents name only a buffer that brings a list of its own (question
`Q-cb-set-listless-overwrite`). A copy of a `Listeners` or a boxed component written to another entity stays a
question (`Q-cross-entity-copy`), and a stale `EventBuffer` copy is never modified (documented misuse on 2.2.2,
`D-stale-copy`).

The corpus follows the same profile: an entry with `"Until": "2.2.1"` keeps its expectation for 2.2.1 and older and
must pass on later trees (`Q-created-after-death`, `Q-warmup-already-has`, `N4-dying-entity-enters-filter`,
`N3-copy-during-last-removal`).

**Documented.** The model reports behaviour the documents of the tree describe as `D-` items (next to the `Q-`
questions). `driver.py` (`DOCUMENTED`) looks each one's sentence up in the documents of the tree itself (the XML docs of
the package, its `CHANGELOG.md` and `README.md`, `docs/` without the generated `docs/api/` pages and `.vitepress`) and
writes it to `report.json` (`documented`: id, count, text, quote, where it was found). A tree whose documents do not
have the sentence gets the question the item replaces and a stand warning (`stand_warnings`): the documents were
reworded, update the sentence.

## What a failure is

Each check is a rule (`N-extra:typed-added`, `R-dead-slot`, `X-missing-throw`, `S-components`, ...) that rests on
documented promises, the **claims** in `src/Mechanism.cs`. Every claim has the first package version whose
documents make it (`since`). While it runs an operation, the model records **facts**:

- a decision a claim dictates, for example `add-replaced` (a listener destroyed the entity during `Add` and a new
  entity took the slot, so the rest of this `Add` does not reach it) or `reentrant-remove-interleaved`;
- a situation, for example `slot-reused`, `copy-src-dead`, or, from the real world, `stale-accepted:free+destroying`
  (a dead handle was accepted while some `DestroyEntity` was in progress);
- a **mirror**: a place where the contract is silent and the model copies the reference implementation, recording
  a contract question (`Q-created-after-death`, `Q-warmup-already-has`, `Q-has-during-drain`, ...).

The facts recorded before the first difference make the **signature** of a failure (when they show several
families, the one whose own fact came first is the mechanism):
`rule@operation[reaction]{facts}`, for example `X-nested-missing-throw:staleRemove@destroy[world-destroyed]{stale-accepted:free+destroying}`.
The reaction is the kind of callback whose scripted reaction was running (`top` for the operation itself,
`state` for a difference found after it).

## Families are assigned by mechanism

A known defect family is assigned only when its own facts are present (`src/Classifier.cs`, `Families`):

| family | mechanism (facts) |
|---|---|
| W1 | `warmup-temp-died`: a listener destroyed Warmup's temporary entity |
| W2 | `reentrant-remove-interleaved`: a component removed again from its own OnRemoved dispatch while a removal of the same type on another entity is in progress; only for a typed-removed or crash rule, or an OnRemoved at the first difference (an interleaved removal earlier in the operation does not explain a difference elsewhere) |
| W3 | `add-replaced`, `ref-replaced`, `remove-replaced`, `dispatch-replaced`, `filter-update-replaced` or `nested-removal-replaced`: an operation in progress reaches the new entity in the slot |
| W3c | `readd-in-removed` or `nested-removal-readded`: the same type re-added during the OnRemoved dispatch of its old component |
| W4 | `removed-in-add-filter-update` with a filter rule or a filter event at the first difference |
| W5 | `writeback-stale` with an ownership or content rule |
| W6 | `stale-accepted:free+destroying` (KENSEI_DEBUG) |
| N1 | `copy-dst-died` |
| N2 | `reentrant-remove-listeners-changed` with `N-missing:typed-removed` or `N-extra:typed-removed` |
| N3 | `copy-src-empty` with `R-alive-empty` |
| N4 | rule `N-dying-enter` |

Anything else is **NEW**: a new signature with its own minimal scenario and repro, even when its rule is one a
known family also violates (a stale handle accepted for a reused slot is not W6; a dead source leaking an empty
entity from `CopyEntity` is not N3). A known family's signature names only its own facts, so one mechanism does not
split into many signatures; a NEW signature names all facts. The shrinker keeps the family and the status, so a
new bug cannot shrink into a known family's scenario. NEW signatures are shrunk first.

## Status: defect, question, regression

- **defect**: every claim the failure rests on is made by the documents of the tree's version. A difference from
  the model rests on the rule's own claims and on those of the model's decisions before it; a check of the real
  world alone (a callback for a free slot, a broken invariant) rests on its rule's claims only.
- **question**: one of those claims appears only in later documents (the tree's `package.json` version is older
  than its `since`; a CHANGELOG that starts with `[Unreleased]` counts as the next patch version). Example: on
  2.1.0 a duplicate OnRemoved rests on `pool-remove-reentrant` (since 2.1.1).
- **regression**: the world behaves differently from the reference implementation where the contract is silent.
  When mirror facts come before a difference from the model, the executor replays the scenario with the alternative
  reading of each of those questions (`ModelSide.Readings`, then all of them together). The real world is the same
  in every replay: if under a reading the model follows it past the operation, the failure is a regression; if the
  reading explains the first difference and something else fails later in the same operation, that later failure is
  the finding, and the questions mirrored right before it are read the other way too (up to two readings at once).
  "Later" is the log position: an outcome difference counts at the first position where the logs differ (an outcome
  that differs only under the reading is not later than the original log difference), and at the same position the
  reading's failure is the finding when it changed what the model expects there and another rule fails (the world
  follows neither reading: an `OnEntityCreated` the mirror sends, an exception the world throws instead).
  A difference that is only another order of the same notifications within one kind of callback
  (`N-reorder:*`) is a regression too: that order is described by `architecture.md`, not promised by the public docs.

`report.json` keeps defects in `findings`, question-status findings in `questions` (with `kind:
"finding-question"`, next to the contract questions met on the way) and regressions in `regressions`.

## Excluding known families

`--exclude-families W1,W3,...` (or `all`) removes from the generated scenarios the shapes that trigger those
families (`src/Exclusions.cs`): reaction steps that destroy or re-add at the dangerous moment, the motif built for
the family, an `Exc` constraint in a lower mask word for N4. On 2.2.x about a third of all scenarios fail on known
families; excluding them lets the search look for the rest. A failure of an excluded family that still appears
means the exclusion is incomplete and is reported like any other.

## Comparing with a baseline

`--baseline <label>` reads `Stand/out/reports/fuzz-core/<label>-<rel|dbg>/` and adds to the report: new signatures
(no known family), new signatures inside known families, signatures whose hits per scenario grew at least threefold
(and by at least 20), signatures that are gone, and, when both runs used the same fixed seed set and generator
settings, a seed-by-seed comparison (newly failing seeds, seeds failing differently, fixed seeds).

## Corpus

`corpus/*.json` are minimal scenarios replayed at the start of every run. An entry with `Family` and `Rule` must
fail with that family (`reproduces`, `passes` once fixed, `fails-differently`, `skipped` in another build). An
entry with `"Expect": "question"` and `Question` keeps a contract question reachable (`question-met`): for example
`Q-warmup-already-has` (an OnAdded of Warmup's temporary entity adds a later pool's component, and Warmup then
throws "already has") and `Q-created-after-death`. `--save-corpus` copies the minimal scenarios of new defect
signatures into the corpus.

## Self-test

`run.sh --selftest [--debug]` applies each patch of `mutants/` (described in `mutants/mutants.json`) to a copy of
the tree under test (`Stand/out/mutants/fuzz-core/<flavor>/`, fresh files) and runs the module on it with the same fixed
seed set as on the unmutated copy. `since` / `until` (and `version_note`) name the contract versions of the trees a
mutant's patch is for: outside that range it is n/a and listed with the reason, within it a patch that does not apply
fails. A `bug` mutant is killed only by a new signature of family NEW that the baseline does not have,
with a repro that reproduces, of the mechanism it was written for: its `match` in `mutants.json` lists rule prefixes
and facts (`{"rule": "R-alive-empty", "fact": "copy-src-dead"}`, a trailing `*` matches a fact prefix), and the
signature's rule must start with one and its facts contain that entry's fact. New signatures of another mechanism
are `KILLED-OTHER`, a mutant without `match` is `UNSPECIFIED`; both fail, like `ABSORBED` (it went into a known
family instead). The bug mutants include the critique's M1 (`nov-group-claims-pools`: a rejected group keeps a pool)
and M2 in three places (`nov-world-compadded-live-array`, `nov-filter-leave-live-array`, `nov-typed-added-live-array`:
a dispatch that walks the live listener array). A mutant whose code 2.2.2 rewrote has a `-222` twin cut against the final 2.2.2 code
(`rev-add-stale-ref-222`, `nov-typed-added-live-array-222`, `nov-copy-dead-leaks-entity-222`, ...; the old one has
`until` 2.2.1, the twin `since` 2.2.2). On 2.2.2 two more bug mutants roll back what 2.2.2 itself changed:
`rev-typed-added-early-capture-222` (the `OnAdded` dispatch takes the typed listeners from before the world update
again) and `nov-eventbuffer-other-holder-222` (storing an `EventBuffer` value no longer checks which entity holds its
list, so a copy of another entity's buffer shares that entity's list: caught only by the cross-entity copies 2.2.2
runs, fact `cross-copy-store`). The fix mutants (W1, W6) and the regression mutant stop at 2.2.1: 2.2.2 fixed W1 and
W6 itself and documents the behaviour the regression mutant changes. A `fix`
mutant (an emulated correction of W1 or W6) must make that family disappear without any new signature. A
`regression` mutant (OnEntityCreated no longer sent for an entity that died during its creation) must come out with
status regression only. Report:
`Stand/out/reports/fuzz-core/selftest-<flavor>-<rel|dbg>/selftest.md` and `selftest.json`.

## Adding a promise or a mechanism

1. A new documented promise: add a claim with its `since` version to `src/Mechanism.cs`; record a fact with that
   claim where the model's behaviour follows from it (`ModelSide`, `AddFact(key, slot, claim)`).
2. A contract-silent behaviour: mirror the implementation, record `Questions.Add(...)` and a fact with `mirror:` the
   question id, and implement the alternative reading behind `Readings.Contains(id)`.
3. A new known defect family: add a `FamilyDef` with its fact keys to `Classifier.Families` (and its title to
   `driver.py`), a corpus entry, and the shapes that trigger it to `Exclusions.Apply`.
4. A new operation: generation in `src/Generator.cs`, the shared reaction engine in `src/Side.cs`, both sides
   (`RealSide`, `ModelSide`), and the repro writer.

## Owning groups and listener sets

The operation `group` registers `world.Group<...>()` over 2 to 4 random types (a type drawn twice, or one another group
owns, makes it throw), and the motif `group-conflict` builds the case of the critique: a group over an owned type, then
its freed type in the existing group's members and in a new group. The model keeps the groups and the owner of every
pool (a rejected registration owns nothing: `group-registration`), the snapshot compares every group's member count,
and the consistency check holds every group to exact membership and aligned dense arrays (`R-group`).

The motif `listeners-change` makes a world, filter or typed listener unsubscribe itself or the next listener and
subscribe another during a dispatch; the model records the fact `listeners-changed:<kind>`, and the dispatch must go on
over the array it captured at its start (claim `listener-snapshot`).

## Limits

The model follows the orders that `docs/architecture.md` describes; a reorder within one kind of callback is
reported as a regression, a reorder across kinds as a defect. Reactions are off during OneFrame cleanup and filter
iteration. Not covered: `World.Destroy` and what follows it, `WorldSerializer`, `ParallelRunner`, the data spans of
groups (only membership and dense order), `EventBuffer.Prewarm`, `Filter.Reserve`. W6 exists only under KENSEI_DEBUG; stale handles are not
used in release (undefined there). Crash confirmation and hang detection cost up to 8 s per case.
