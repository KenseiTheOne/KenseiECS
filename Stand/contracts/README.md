# Stand/contracts

The contract matrix of the KenseiECS public API: lifecycle and misuse cases, the state after every
call, and the promises that live outside the matrix (`[Obsolete]` texts, `StorageGrown` owners, the
`FirstGrowthAfterReserve` contract, `Filter.Entities` spans around `Reserve`, references kept by
`World.Destroy`). The module only finds; it changes nothing in the package.

The rule of the stand: **a defect is a contradiction of a promise written in the documentation of
the version under test** (its XML docs, `docs/`, CHANGELOG). Anything else that looks wrong is a
question to the contract, documented, or, against a baseline, a regression.

## Running

```sh
Stand/contracts/run.sh [--quick|--full] [--src DIR] [--flavor NAME] [--debug] [--baseline FLAVOR] [--seed N]

Stand/contracts/run.sh --src Stand/out/src/v2.2.0/KenseiECS            # a released tree, release build
Stand/contracts/run.sh --src Stand/out/src/v2.2.0/KenseiECS --debug    # the same with KENSEI_DEBUG
Stand/contracts/run.sh --debug --baseline v2.2.0                       # this repository's package against 2.2.0
Stand/contracts/run.sh --selftest                                      # mutation self-test (see below)
```

A run takes a few seconds (the build included). The report is in
`Stand/out/reports/contracts/<flavor>-<rel|dbg>/`; `run.sh --help` lists every file.

Requirements: a .NET SDK 8 or newer (`dotnet` on `PATH`, or `$DOTNET`; without one, `run.sh`
downloads the .NET 8 SDK into `Stand/out/tools/dotnet-sdk/`), `git` for the docs of a tagged version,
Python 3.9+ for `--selftest`. Nothing outside the repository is referenced.

## What it does

1. **Catalog.** Reflection lists the public methods, properties and constructors of World,
   ComponentPool<CA>, Filter, FilterBuilder, Group<CC, CD>, CommandBuffer, SystemsRunner, SharedData,
   WorldSerializer, Listeners<T>, EventBuffer<T> and the World extension methods. A member added in a
   new version enters the matrix without a change here.
2. **Scenarios:** fresh, world-destroyed, runner-destroyed, runner-reinit, stale-dead, stale-reused,
   null-entity (Entity.Null and default structs), negative, huge, null-arg, plus curated cases for
   inputs the generic variants do not produce (a truncated snapshot, a nested runner over another
   World, null on an uninitialized runner, ...; `Matrix.Curated`).
3. **Call** through a DynamicMethod (`Invoker`): ref returns and ref structs are called too, and an
   exception arrives with its own stack. Every case builds its own state; cases run in a worker
   process, so a crash or a hang costs one case (CRASH / HANG).
4. **State after every call.** Safe queries (IsAlive, EntityCount, TryGetEntity, Has and
   DestroyEntity on a dead handle, the number of alive entities without a component, filter, group,
   runner, buffer and listener counts, the argument objects) are compared with the same scenario built
   again without the call. After a normal return too: a side effect before a check (a copy made and
   left behind by `CopyEntity` of a dead entity) is caught in release.
5. **Accepted null.** When a call with null returns normally, ordinary use afterwards (create an
   entity, walk a filter, Playback, Run, Save...) is compared with the same call given a valid argument.
6. **Outside the matrix** (`Diagnostics`): every `[Obsolete]` text against what its overload does with
   null; owners in `StorageGrown` (EcsProfiler) and `FirstGrowthAfterReserve` for pools and filters; the
   `FirstGrowthAfterReserve` contract (a later growth does not replace the first, `Clear` keeps both,
   empty in release); a `Filter.Entities` span taken before `Filter.Reserve` / `World.Reserve`, the
   filter's span after it, `Reserve` during a `foreach`, the capacity after `Reserve` (up to 2.2.1 the
   documents disagree on the old span: question Q-SPAN-RESERVE; 2.2.2 writes in the XML of
   `Filter.Entities` that a growing `Reserve` ends its validity and it reads -1: documented, with that
   sentence, claim `span/reserve-ends-validity`); the groups kept
   by `World.Destroy` (WeakReference). Every check is isolated: one that throws is a violation of its
   promise, and a check that cannot run at all is a stand error in the report, never a silent pass.
7. **Repros.** Every defect and question gets a C# class (`repro/`, `repro/questions/`).
   `Stand.Contracts.Repro` compiles them against the same tree and define, runs them and compares
   their output with what the stand saw (`repro-check.json`).

## Verdicts

- **Defect** (`findings` in report.json): the code contradicts a promise whose wording is in the docs
  of this version: a curated claim (`Policy.BuildClaims`), an XML `<exception>` tag whose condition
  names the scenario, a documented invariant ("an entity is alive if and only if it has at least one
  component"), or a promise about the safe queries after a call (TryGetEntity "Safe for any index",
  DestroyEntity of a dead entity a no-op, IsAlive/DestroyEntity on a destroyed world).
- **Question** (`contractQuestions`): suspicious, but no document of this version promises otherwise:
  an NRE or IndexOutOfRange, ArgumentNullException without a null argument (a .NET convention, not a
  package promise), a state that answers differently after a failure nobody documented, the state
  after a documented KENSEI_DEBUG check, a crash, documents that disagree with each other, or a promise
  that only a later version makes (see the version profile).
- **Documented** (`documented`): a changed state after an exception that the docs describe as the
  member's partial effect (a failed `WorldSerializer.Load` leaves "a partially restored world",
  `Playback` discards the rest of the buffer, `Run` cleans OneFrame components in a `finally`).
- **Regression** (`regressions`, with `--baseline`): a case that was fine on the baseline (or a
  question of another kind) and is suspicious now, while no document of this version forbids it. Not a
  defect and not a plain question.

### Version profile

Every claim knows the version whose documentation first makes it (`Claim.Since`), and the module reads
the version from `package.json` of the tree. The documentation of the tree decides first: a claim
whose wording is found is **active**. When the wording is not there:

- **later**: the tree is older than `Since`. Its violations are questions: a promise of 2.2.1 is not
  held against 2.2.0 (G1, G2, G5 are questions on 2.2.0 and defects on a 2.2.1 tree that breaks them).
- **missing**: the tree should have the promise but the sentence is gone (rewritten docs). Its
  violations are questions, and the report and the console print a stand warning, so a rewritten
  sentence never switches a check off silently. A cell whose outcome is the baseline's (class, message,
  throw site) and whose verdict changed only because a claim of it is missing is not a regression: the
  claim warning counts it instead.
- **superseded**: a later version replaced the promise with another claim (`SupersededBy`), whose wording
  is there: the successor judges the case, without a warning (2.2.2 rewrote the debug-mode row of pool
  `Get(int)` into `debug-table/get-missing`, which covers `World.Get` and pool `Modify` too and documents
  `IndexOutOfRangeException` on .NET).

A claim lists the wording of every version that makes it: the debug-mode rows that 2.2.2 rewrote (the
stale-handle row with `AddEvent` and `Subscribe`, the runner rows split one per situation) and the
`AddEvent` XML sentence keep the 2.2.1 phrase next to the 2.2.2 one, or a part both share.
- **profile**: the tree has no `docs/` at all; a docs-page promise then counts as made by every version
  from `Since` on.

The docs come from `<tree>/docs` (`Stand/run.sh --ref` extracts them), otherwise from `git archive` of
the commit in `<tree>/.stand-ref` or of the tag named like the flavor, cached in
`Stand/out/contracts-cache/docs/`. `--docs DIR` overrides. Only the pages a clean checkout has are read
(`Docs.Generated`): the API pages `docs/scripts/gen-api.mjs` writes into `docs/api/` (all but
`docs/api/index.md`) and `docs/.vitepress/` are left out, so a local docs build changes nothing; the XML
docs they are made from come from the compiled sources. A documented item carries the sentence it rests on
(`quote` in report.json), the wording found in these documents.

### Signatures

Every entry carries a signature: the mechanism and the place, stable between runs and versions (the
rule, the claim, the first package frame of the exception or the member that returned, and the kind of
damage with handle arguments folded). Defects are grouped by signature; `signatures[]` in report.json
lists every signature with its hits (questions count each judgement). With `--baseline` the report
compares signatures: a new signature is visible even inside a known family (G1 reached through another
throw site), and a sharp growth of hits (at least twice and at least three more) is listed apart.

### Owning groups

Curated `World.Group<...>()` cases over a type that `Group<CC, CD>` owns (as the second, the first, the third type) and
over a type listed twice must throw (`docs/group-second-owner-throws`, groups.md and architecture.md RegisterGroup), and
their own probes check what the failed registration leaves: `Group<CC, CD>` stays aligned when a new member also gets
the freed type CE (`docs/group-aligned`), and a group over CE and CF, which no registered group owns, can still be
created (`docs/group-registration-atomic`). The mutant `nov-group-claims-pools` (M1 of the completeness critique) is
caught by both.

## Known problems

`Known.All` holds the problems the module was built for and how to recognise them (by meaning, not
text): G1-G5, WL1, C-FILTER-DESTROYED, C-LOAD, C-ADD-REJECT. Their status in `known[]`: `found` (a
defect), `regression`, `question`, `documented`, `not-found`, `not-applicable` (the other build only),
`not-checked` (the version lacks the API). An entry with the same signature as a recognised one gets
the same tag; more than one signature in a family is a new mechanism inside it.

## Self-test

`run.sh --selftest [--src DIR] [--flavor NAME] [--debug | --configs rel,dbg] [--only m1,m2] [--keep]`
copies the tree (default: this repository's package and its `docs/`) to `Stand/out/selftest/contracts/<flavor>/`,
applies each patch of `mutants/` to its own copy and runs the module on it in the builds the patch
names. The copies are fresh files and the builds are labelled by the tree (`selftest-base-<flavor>`,
`selftest-<flavor>-<mutant>`), so the base of one tree never runs a binary built from another one. A **bug** mutant is killed only when every `Expect:` line of the patch holds and a defect that an
`Expect:` line names (by id prefix, or the defects of an expected known problem) has a signature the
unpatched copy (the base) does not have: the mutant is caught by the mechanism it was written for.
New defects of another kind are KILLED-OTHER, a bug mutant without a defect `Expect:` is UNSPECIFIED;
both fail the self-test. A **doc** mutant is a change the docs of the tree describe: accepted when its
`Expect:` lines hold (a documented entry, a known problem that is now `documented`) and no new defect
appears (`mut-obsolete-text`: G3 is documented, since the overload's XML describes the null case). A **fix** mutant emulates a correction of a
known problem (`fix-wl1-destroy-releases-groups`): it is accepted when that problem is gone and nothing
new appears, a check against false defects on corrected behaviour (not a proposal for the package).
Report: `Stand/out/reports/contracts/selftest-<flavor>-<rel|dbg>/selftest.md|json`; exit code 0 when
every bug mutant is killed and every fix and doc mutant accepted. `Since:` / `Until:` (with `Version-note:`)
name the contract versions of the trees a patch is for: outside the range it is n/a with the reason
(`fix-wl1-destroy-releases-groups` stops at 2.2.1, where 2.2.2 fixed WL1 itself); within it a patch that
no longer applies is not-applicable, a failure: refresh it.

The patches: the reverted 2.2.1 fixes (`mut-clear-after-destroy`, `mut-create-after-destroy`,
`mut-filter-debugname`, and the doc mutant `mut-obsolete-text`), the same classes in new places from the completeness
review (`nov-reserve-after-destroy`, `nov-firstgrowth-last`, `nov-copy-dead-leaks-entity`,
`nov-pool-owner-const`, `nov-playback-no-alive-check`, `nov-debug-stale-reused-passes`, `nov-group-claims-pools`), and two that
keep the new probes honest (`nov-clear-resets-firstgrowth`, `nov-reserve-drops-entities`). A patch
starts with a header (`Mutant`, `Kind`, `Class`, `Configs`, `Since`, `Until`, `Version-note`, `Expect` or `Removes`, `Why`, `Not-<cfg>`),
then a unified diff against the folder that holds `KenseiECS/` (`patch -p1` or `git apply`). `Expect:`
conditions: `known <ID> found|documented`, `defect <id prefix> [repro-verified]`, `question <id prefix>`,
`documented <id prefix>`;
`Removes: known <ID>`; either optionally prefixed with `rel:` or `dbg:`.

## Extending

- **A new promise in the docs:** a claim in `Policy.BuildClaims` with `Phrases` (the sentence as
  written; a debug-mode table row quoted whole, both columns), `Since` (the first version that says
  it), `Applies` and `Check` (a Call claim), or `Violations` over the probe diffs (a State claim), or a
  Partial claim for a documented partial effect. Promises outside the matrix: `DiagClaims` in
  `Program.cs` next to a probe in `Diagnostics`.
- **A new parameter type:** `Args.Normal`. Until the factory knows a type it passes null, and the
  report lists such parameters under Coverage.
- **A new target or probe:** `Catalog.Targets`, `Scenarios.Build` (its C# goes into the repros),
  `Probes.Define`.
- **A known problem to follow across versions:** `Known.All`.
- **A mutant:** a patch in `mutants/` with the header above.

The stand code uses only the 2.1.1 API; newer API (`Reserve`, `AddGroup`, `FirstGrowthAfterReserve`,
`Filter.Reserve`, `DenseCapacity`) is called through reflection after checking that it exists, and a
check whose API is missing says "not in this version".

## Limitations

- CoreCLR (net8.0, rolled forward to the installed runtime), not IL2CPP. IL2CPP builds of the package
  turn off null and bounds checks, so an NRE or IndexOutOfRange of the matrix may be memory corruption
  there; they stay questions.
- Promises are checked through curated claims and XML `<exception>` tags; a new sentence without a
  claim is not checked, a rewritten one becomes a question with a stand warning.
- The state probe sees only the safe queries; an internal inconsistency they do not reveal is not
  caught. Object arguments are probed only in curated cases (the Load target, the rejected runner).
- One normal value per argument type; fewer than 64 probe component types (one mask word).
- The growth-owner check parses `ProfileEvent.ComponentType` as `<owner> <storage> <old> -> <new>`; a
  new format makes it "the check did not trigger", not green.
- The leak probe relies on a deterministic GC in the process; its control (a filter and a pool without a
  group must be collected) turns a doubtful result into inconclusive.
