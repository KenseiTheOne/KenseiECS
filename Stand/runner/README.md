# Stand module `runner`

Checks `SystemsRunner` and `EcsBootstrap` against their documented contract: the `Init` rollback,
`Destroy`, `Run`, groups and phases, `SetActive`/`IsActive`/`IndexOf`/`GetRunner`, OneFrame cleanup of
disabled runners, `SharedData` inheritance, the KENSEI_DEBUG checks, and the Unity lifecycle of
`EcsBootstrap` (compiled as is against minimal `UnityEngine` stubs, so no Unity install is needed).

Three sources of cases go through the same oracle:

- an executable model of the contract (`src/Model.cs`) run side by side with the real runner on random
  runner trees and operation scripts (`src/Fuzz.cs`, `src/Engine.cs`), with a shrinker that reduces a
  violating tree to a small repro;
- targeted scenarios for the known findings and their neighbours (`src/Scenarios.cs`);
- hand-written scenarios for what the model does not express: repeated names, KENSEI_DEBUG checks,
  `EcsBootstrap`, and observations of behaviour the documents leave open (`src/CustomScenarios.cs`,
  `src/BootstrapScenarios.cs`).

Every finding gets a C# repro (`repro/*.cs`) that is compiled against the same sources and run
(`repro-check/`), and a JSON case (`cases/*.json`) that `--replay` runs again.

## Running

```sh
Stand/runner/run.sh --quick                                   # KenseiECS/ of this repository, release
Stand/runner/run.sh --quick --debug                           # the same with KENSEI_DEBUG
Stand/runner/run.sh --quick --src Stand/out/src/v2.2.0/KenseiECS
Stand/runner/run.sh --quick --contract-version 2.2.2          # judge the tree by the 2.2.2 contract
Stand/runner/run.sh --quick --baseline v2.2.0                 # compare with that tree's report (same build)
Stand/runner/run.sh --replay Stand/out/reports/runner/wt-rel/cases/RUNNER-SHARED-PROPERTY-STALE.json
Stand/runner/run.sh --gen-seed 1533251187 --quick             # regenerate the random tree of a seed
Stand/runner/run.sh --selftest --configs rel,dbg              # mutation self-test, see below
```

`--quick` runs 20 000 random trees (about 1 s), `--full` 300 000 (about 30 s). The seeds are fixed, so
two runs with the same mode see the same trees: random hits compare between versions.
`run.sh --help` lists every option.

.NET: `$DOTNET`, else `$DOTNET_ROOT/dotnet`, else `dotnet` on `PATH`; without any of them a .NET 8 SDK is
downloaded once into `Stand/out/tools/dotnet-sdk/` with the official `dotnet-install` script.

## Statuses: defect, question, regression

A rule violation is a **defect** only when the documents of the tested version promise the rule.
Everything else the module reports is a **contract question** (behaviour those documents do not pin
down) or, against a baseline, a **regression** (open behaviour that changed without breaking the
contract).

### Contract profile

The profile (`src/Profile.cs`) starts from the version in the tree's `package.json`
(`--contract-version` overrides it) and decides per rule:

| rule | up to 2.2.1 | from 2.2.2 |
|---|---|---|
| `RUNNER-ROLLBACK-DESTROYS-INITLESS` (R1): the rollback destroys an Init-less system | question: runner.md and the XML of `Init` say every `IDestroySystem` before the failed one is destroyed, `ISystem.cs` says Destroy is called once, on shutdown | defect: the rollback destroys only systems whose `Init` completed — in the failing runner and in the nested runners it tears down ("A system without IInitSystem is never destroyed by a rollback, only by Destroy().") |
| `RUNNER-ROLLBACK-AGGREGATE-STRUCTURE` (R2): the rollback errors and the original exception | defect (2.2.0+): the original exception first, then the rollback errors | defect, also in the 2.2.2 words: "one flat AggregateException at any depth" |
| `BOOTSTRAP-PHASE-NAME-COLLISION-DEBUG` (R4): a user `"fixed"`/`"late"` in `update` breaks `Awake` under KENSEI_DEBUG | question: the names are not reserved anywhere | replaced by `BOOTSTRAP-RESERVED-NAME-ERROR`: under KENSEI_DEBUG the clash must be a clear error that names the reserved names (XML of `EcsBootstrap`: `The names "fixed" and "late" are reserved in the update runner`); release checks nothing |
| `RUNNER-SHARED-RUNNER-SLOTS` (R3): one runner in two slots, the slots disagree | defect (2.2.0+): a group's `IsEnabled` always equals its slot | replaced by `RUNNER-SHARED-RUNNER-ACCEPTED`: a second registration of a runner must throw in every build ("A runner can be registered only once: in one slot of one parent.") |
| `RUNNER-SHARED-PROPERTY-TWO-SLOTS` (R-SHARED for a runner in two slots, `+shared` places): which `SharedData` such a runner sees | question: no document allows or forbids two slots | off: the second registration throws, the case cannot arise |
| `RUNNER-DUPLICATE-NAME-RELEASE-LOOKUP` (R-DUPNAME): what a repeated name reaches in release | defect (2.1.0+): the second registration "silently replaces the first in the name lookup" | defect by the 2.2.2 lookup: a system or group and a phase with one name split it, in either order (`SetActive`/`IsActive`/`IndexOf` reach the system or group, `GetRunner` the last group or phase) |
| `RUNNER-GROUP-START-STATE`: a group switched off before `AddGroup` starts enabled | question on 2.2.0, defect from 2.2.1 (the promise appeared in 2.2.1) | defect |

Where the tree carries its documents — XML docs in the sources, `CHANGELOG.md`, a `docs/` folder next to
the package — the sentence a rule rests on is looked up as well: a promise the tree's own documents make
counts even when `package.json` lags behind, and a promise they no longer contain turns a defect into a
question until the rule is updated. A rule 2.2.2 decided switches on as soon as one of its 2.2.2 sentences
(`Profile.cs`, quoted from the XML docs, runner.md, bootstrap.md and the CHANGELOG) is in the tree;
`--contract-version` decides by the version alone. The report lists every such sentence and whether the
tree has it.

### Signatures

A signature is the rule (the mechanism) plus the place it was seen at, for example
`RUNNER-ROLLBACK-DESTROYS-INITLESS @ rollback-failing:group:DestroyOnly` or
`RUNNER-SHARED-RUNNER-SLOTS @ SystemInfo.IsEnabled@group+shared:after-SetActiveName`. Places name the
code path, not the whole tree: the operation, the role of the runner (root, group, phase, `+shared` when
it sits in several slots), the kind of system, and a depth bucket where depth is the mechanism (inherited
`SharedData`). A `SharedData` violation at a `+shared` place belongs to `RUNNER-SHARED-PROPERTY-TWO-SLOTS`,
not to `RUNNER-SHARED-PROPERTY-STALE`. Each signature has its own shrunk repro and the number of random
trees that hit it, so a new place or a jump in hits inside a known rule is visible on its own.

The repros are checked in up to three rounds (`repro-check/`): a repro that does not compile is moved to
`repro/not-compiling/`, gets an `ERROR` line in `verify.txt` and its signature counts as unverified, so one
broken repro does not take the others with it.

With `--baseline LABEL` the module compares with `Stand/out/reports/runner/LABEL-<rel|dbg>/report.json`:
new signatures, signatures whose hits jumped (x3 and at least +20), signatures with another status,
signatures gone, and regressions — the contract questions whose observed behaviour changed.

## Report

`Stand/out/reports/runner/<flavor>-<rel|dbg>/report.md` and `report.json`:

| key | content |
|---|---|
| `defects` | defects by rule, with `signatures` (place, hits, seed, repro, verified) and the profile's `contractBasis` |
| `findings` | the same defects, one item per signature (`id` = rule, `signature`, `magnitude` = random hits) |
| `contractQuestions` | `kind: "rule"`: rules the profile does not count as defects for this version (with signatures and repros); `kind: "observation"`: open behaviour, with `observedKey` for the baseline comparison |
| `regressions` | open behaviour that changed against `--baseline` |
| `known` | R1–R4 of the 2.2.0 review: `defect`, `question`, `not-found` or `not-applicable` |
| `contractProfile` | version, its source, the per-rule statuses and the document sentences checked |
| `scenarios`, `coverage`, `notes` | scenario outcomes, what the random test exercised |

The catalog entries of the stand (`Stand/summary/catalog.json`) match the module's findings by rule id
(`kind: finding`), its `known` list (`kind: known`) and its rule questions (`kind: question`).

## Self-test

`run.sh --selftest` applies each mutant of `mutants/` to a temporary copy of the tree (default:
`KenseiECS/` of this repository) and checks that the module catches it. The copies live under
`Stand/out/selftest/runner/<flavor>/` as fresh files, and their builds are labelled by the tree, so no build of another
tree is taken for up to date; the sources under test are never touched. Report:
`Stand/out/reports/runner/selftest-<flavor>-<rel|dbg>/selftest.md` and `selftest.json`.

A mutant is a unified diff against the folder that holds `KenseiECS/` (it also applies with `git apply`
or `patch -p1` there), preceded by a header:

```text
Mutant: novel-r2-fix-skips-phases
Kind: bug                      # bug: must be caught; fix: an emulated correction, must be accepted
Class: same-class-new-place    # lifecycle, flags, names, oneframe, shared, bootstrap, known-finding, 2.2.1-promise, 2.2.2, ...
Configs: rel, dbg
Contract: 2.2.2                # optional: --contract-version for the mutant and its baseline
Until: 2.2.1                   # optional, with Since: the contract versions of the trees whose code the patch is for
Version-note: built on an emulated fix of the 2.2.1 code that 2.2.2 replaced with its own
Base: fix-r2-flat-aggregate    # optional: mutants applied first; the baseline run has only these
Expect: RUNNER-ROLLBACK-AGGREGATE-STRUCTURE         # rules that must show a new signature; "(question)" for a question
Removes: RUNNER-ROLLBACK-AGGREGATE-STRUCTURE        # fix only: "RULE" or "RULE @ place-prefix"
Why: R2 after a partial fix: unwrapped for a failing group but not for a failing phase
```

A **bug** mutant is killed when a signature of an expected rule with the expected status appears that the
baseline run does not have, or its random hits jump, and the repro of one such signature, compiled against
the mutated tree, reproduces (the mutant run verifies its repros). Caught without a reproduced repro is
KILLED-NO-REPRO, a failure. A **fix** mutant is accepted when the signatures it names are gone and nothing
new appears (no false positive on the corrected behaviour). The set covers:

- defects injected into the lifecycle, flags, names, OneFrame cleanup, `SharedData` and `EcsBootstrap`;
- emulated fixes of R1, R2, R3 and R-SHARED;
- known classes in a new place: R-DUPNAME for run systems, R-SHARED for phases, R3 through `SetActive(bool)`,
  and partial fixes of R2, R3 and R-SHARED that leave one place behind;
- the 2.2.1 promise on the start state of a group, with and without the documents that make it (a defect
  on a tree that promises it, a question on one that does not);
- the 2.2.2 decisions: fixes that satisfy them (the reserved names in KENSEI_DEBUG only, as documented), and
  partial ones that must still be caught (only `"fixed"` reserved; a release `SetActive(name)` that
  prefers a phase or group over a run system with the same name, against the documented split);
- on 2.2.2 itself: a `-222` twin of every bug mutant whose code 2.2.2 rewrote (rollback order, nested
  teardown, the rethrown original, the init progress, the flat aggregate, `SetActive(bool)` of a group, the
  cleanup cascade, the tick of a phase, the KENSEI_DEBUG name check, `Shared` of groups and phases, the
  bootstrap phases and guard, the start state of a group with and without its documents), and rollbacks of
  what 2.2.2 fixed: R1 in a completed nested runner, the flat `AggregateException` (R2), a runner registered
  twice (R3), the reserved name `"late"`.

The patches written for the 2.2.0/2.2.1 code stop there (`Until: 2.2.1`): their code was rewritten, or, for the
emulated fixes and the 2.2.2 emulations on the 2.2.1 code, 2.2.2 has the change itself (a clean base run is that
check). `novel-r3-fix-misses-name-path` is equivalent to the 2.2.2 code (a runner sits in one slot only). A mutant
outside its range is n/a with the reason; within its range a patch that does not apply fails.

2.2.1 changed the runner only in documentation and in the text of an `[Obsolete]` message (checked by the
`contracts` module), so its "revert" here is the mutant that breaks the 2.2.1 promise.

To add a mutant, edit a copy of the package and write the diff with the header above, for example
`diff -u a/KenseiECS/Systems/SystemsRunner.cs b/KenseiECS/Systems/SystemsRunner.cs`, then check it with
`run.sh --selftest --only <name>`.

## Limits

- The contract texts live in `src/Rules.cs`, `src/Model.cs` and `src/Profile.cs`; the profile checks that
  the key sentences are still in the tree's documents, but a reworded promise needs the rule updated.
- `EcsBootstrap` runs against `UnityEngine` stubs: no real Unity, IL2CPP or `ProfilerMarker`.
- The random trees use unique names; repeated names and the KENSEI_DEBUG checks are covered by the
  hand-written scenarios only. The module is single-threaded; `ParallelRunner` is out of its scope.
