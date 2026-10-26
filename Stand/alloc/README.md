# Stand/alloc: allocations and memory

The alloc module measures what KenseiECS allocates, on .NET 10 and on the official .NET 8 runtime, in
release and `KENSEI_DEBUG` builds, and compares a tree with a baseline tree measured the same way. It
reports a defect only where the documentation of the tree under test promises no allocation.

```
Stand/alloc/run.sh [--quick|--full] [--src <KenseiECS folder>] [--flavor <label>] [--debug]
                   [--baseline <label>] [--baseline-src <KenseiECS folder>] [--only id,id] [--runtimes net10,net8]
Stand/alloc/run.sh --ci-parity [--quick|--full] [--src <KenseiECS folder>] [--flavor <label>]
Stand/alloc/run.sh --selftest [--src <KenseiECS folder>] [--mutants a,b] [--keep]
Stand/alloc/run.sh --check-repros [--debug] [--src <KenseiECS folder>]
```

`Stand/run.sh` runs it in release, in `KENSEI_DEBUG` and once with `--ci-parity`, against the `--baseline`
of the stand run or, without one, against v2.1.1.

## Requirements

- Python 3.9+ (standard library only), git, a .NET SDK that builds `net8.0` projects and has a .NET 10
  runtime. The SDK is found through `DOTNET_ROOT`, the `dotnet` on `PATH` or the standard install folders
  of macOS, Linux and Windows.
- The official .NET 8 runtime (CI runs the tests on it) is downloaded on first use from the dotnet release
  metadata into `Stand/out/tools/dotnet8`, checked by SHA-512, for the machine's runtime identifier
  (`osx-arm64`, `linux-x64`, `win-x64`, ...). `Stand/out/tools/alloc/dnroot` then links the local SDK and
  both runtimes into one `DOTNET_ROOT`, as CI has them.

## What is measured

| scenarios | what |
|---|---|
| `create_destroy`, `add_remove.*` | create/destroy and Add/Remove of every kind of component: one-time bytes of the first pass, bytes per operation afterwards |
| `events.*` | `OneFrame<EventBuffer<T>>` frames with and without `Prewarm`, and the documented write-back of a copy through `cb.Set` as a long run in geometric spans |
| `invoke.*` | `Listeners.Invoke` with a static lambda, a method group, a capturing lambda, against a hand-written loop (bytes and ns) |
| `runner.*`, `commandbuffer.playback`, `copy_entity.*`, `foreach.*`, `parallel.run` | frames of the runner, command buffers, copies, iteration, `ParallelRunner` |
| `access.*` | reads and writes of existing components in a steady frame, release and `KENSEI_DEBUG`: `World.Get`/`Has` by `Entity`, `Modify`/`MarkChanged`/`ChangedSince`, `GetSingleton`, a loop over a group's `Entities`/`Data`, `Filter.Contains` (frame-zero) |
| `growth.first_n_pools`, `growth.first_n_filters` | the first growth of 100 pools and of 100 filters without `Reserve`: the grown arrays are expected, bytes beyond them are reported |
| `create.first_entity_of_process`, `reserve.*` | the first `CreateEntity` of a process, and the `World.Reserve(n, n)` promise |
| `filter.dense_capacity` | a filter's dense capacity after `Filter.Reserve`, `World.Reserve` and organic growth, against the pool of its type asked the same |
| `mem` (Mem probe) | world memory of `Reserve(entities)` with 100-1000 component types and 50 000 entities |

A measured window is its own non-inlined, fully optimized method holding only the two counter reads and
the loop (`Meter/AllocMeter.cs`); the meter checks itself on every run (empty window 0 B, `new object()`
24 B). Every scenario runs in a fresh process; a candidate runs three more times in fresh processes,
target and baseline, before it is reported.

## Statuses

| status | meaning | in the summary |
|---|---|---|
| finding | an allocation where the documentation of this tree promises none: a defect | found |
| regression | more than the baseline (same runtime and build) where the contract does not forbid it: not a defect | regression (its own status and column) |
| contract question | an allocation the documentation of this tree does not cover | question |
| documented | an allocation the documentation of this tree describes | documented |

A promise counts only when it passes two gates (`tools/contracts.py`): one of its sentences is in the
documentation of the tree itself (XML docs, the package `CHANGELOG.md` and `README.md`, the repository
`docs/`), and the tree's version (`package.json`) is at least the version that first documented it. A tree
whose `CHANGELOG.md` starts with a non-empty `[Unreleased]` section is the next release in the making, so
what its own docs promise applies. A promise documented only later gives a question on an older version,
never a defect. A promise the version should document but whose sentences are all missing is reported as
a stale rule (the docs were reworded), and the report lists promises active on the baseline but not on
the target.

The write-back long run (`events.writeback_frames`) has a control that runs the same frames and the same `world.Get`
calls without the write-back: when the control does not settle either (a `Get` that allocates), only what the
write-back allocates beyond the control in every span is its item, so its signature does not take bytes that are not
its own (they show in `access.world_get_has`).

### Documented costs

A first pass also records a census of what it created in its world (`Probe/Census.cs`, outside the window): pool
objects, the hook frames of a pool's first `AddListener`, the list owner of an `EventBuffer` pool, list objects of
`EventBuffer` pools, the doublings of the entity storage and the component data arrays of the pools (`cost_data`: per
component type the elements of the dense data arrays the pass allocated, from the length before the pass, or a new
pool's initial capacity, doubling up to the length after it, and the bytes of one element), each sized by allocating
one more object of its exact type. A one-time regression that the per-object differences against the baseline account for to the byte, each of
them backed by a sentence of the target's docs (`contracts.COSTS`), is a documented cost of that version: status
documented, signature `first:<scenario>:documented-cost`, the accounting in the item. Since 2.2.2: the pool object holds
the innermost hook frame (`pool-hook-fields`, +32 B per pool), the first `AddListener` allocates the frames
(`pool-hook-frames`), a pooled list records its holder (`pooled-list-holder`, +16 B per list), an `EventBuffer` pool has a
list owner (`pool-list-owner`), a growth allocates its arrays before it replaces the old ones (`staged-growth`, the
entity growth stages its mask rows: 24 + 8 B per mask word per doubling), and an `EventBuffer<T>` keeps the number of
the rent of its list (`eventbuffer-lease`: one field more, 8 -> 16 B per element of its pool's data array; a growth
of another component's element, or more elements than the baseline allocated, stays unexplained). In
`add_remove.eventbuffer` against 2.2.1 that is 1023 lists x 16 B + 1920 elements x 8 B = 31 728 B, documented. A byte
the census does not account for keeps the regression.

Every probe process (and every generated repro) first initializes classes of its own until the runtime allocates a
new bucket of its static-field storage (`Probe/StaticsPrimer.cs`): the runtime keeps static fields in pinned
`object[]` buckets (2 040 slots, then 4 080) and allocates the next one, 16 344 B, in whatever code initializes a class
when the bucket is full. Without the primer those bytes landed in the first pass of one scenario or another depending on
how many classes the process had initialized before (on .NET 8 they moved between `reserve.first_entities_of_process`
of 2.2.0 and the runner and invoke first passes with unrelated changes of the probe). KenseiECS types are not touched,
so a first pass is still the package's first use in the process.

One-time differences are explained by experiments in fresh processes rather than guessed:
`--pretouch-type-names` (type names created beforehand), `--run-cctors-of <type>` (static state created
beforehand, narrowed to one type) and `DOTNET_TieredCompilation=0` with
`DOTNET_JitObjectStackAllocation=0`. A difference that only the last one removes is code generation of the
runtime (ReadyToRun coverage, tier-0 paths), not a regression of the library: it is listed in the report
and not reported as an item.

Every item has a signature, `metric:scenario:mechanism` (for example
`first:growth.first_n_pools:pretouch_type_names`). The catalog (`Stand/summary/catalog.json`) matches alloc
items by place and mechanism, and the summary compares signatures with the baseline, so a new mechanism in a
known place, or a known mechanism in a new place, is shown as new instead of being folded into a known
problem.

## Canaries

The report starts with the known problems of 2.2.0 and the stand's own: `2.2.0-A1` (a type-name string per
pool at the first growth under `KENSEI_DEBUG`), `A1-FILTERS` (the same class in filters), `2.2.0-A2` to
`2.2.0-A4`, `2.2.0-W1` (the `cb.Set` write-back of an `EventBuffer` copy), `A-PROFILER` (the first
`CreateEntity` of a process allocates `EcsProfiler`'s 10 000-event ring buffer under `KENSEI_DEBUG`) and
`FILTER-DENSE` (a reserve sizes a filter's dense array up to twice the request, a pool exactly). Status:
present, absent, n/a (only in the other build), not in this version, no data.

## --ci-parity

The test projects of the tree on the official .NET 8 runtime as CI runs them: `KenseiECS.Tests` in
`-c Release` and with `-p:KenseiDebug=true`, `KenseiECS.Generators.Tests` in `-c Release`, built one by one
and tested with `--no-build` in a copy of the tree (`Stand/out/ci/<flavor>`). Projects an extracted tree
lacks come from `git archive` of its commit. Controls in Debug and on .NET 10 show what is green locally.
Every red test gets a cause class: `test-measurement-artifact` (the box of the expected `0` of
`Is.EqualTo(0)` inside the measured window; confirmed by a copy with `Is.Zero` in that line, which passes),
`fails-locally-too` or `ci-only`. Only the artifact is catalog entry A5, and it has a status of its own,
`test-artifact` (`test_artifacts` in report.json, not `findings`): the library allocates nothing in that window, the
test's assert does, so it is a defect of the test suite, not a broken promise of the documents. Any other red test
stays a finding outside the catalog with its cause.

## --selftest

Mutants are patches in `mutants/` with "Key: value" lines before the diff (`Mutant`, `Kind`, `Origin`,
`Why`, optionally `Since` / `Until` / `Version-note`: the contract versions of the trees the patch is for, n/a and
listed outside them, a failure when it does not apply within them; and one `Run:` line per module run: the build, `rel`, `dbg` or `ci`, then what must be found:
scenario ids, `status=finding|regression|test-artifact`, `cause=X` or `cause!=X` for a red CI test; a run line that
names none of them is unspecified and fails). Paths are relative
to the repository root, so `patch -p1` and `git apply` take them as well.

The self-test copies the tree under test (default: the working copy) to `Stand/out/selftest/alloc/`: a base
copy (the baseline of every run), an unmutated control copy and one copy per mutant, fresh files after removing the
copies and build outputs of any earlier self-test (of this tree or another), so no earlier build is taken for up to
date. Each mutant run is
compared with the control run: the mutant is killed when it reports an item the control does not, where its
`Run:` line says. The control itself must report no regression against the identical base. Report:
`Stand/out/reports/alloc/selftest-<flavor>/selftest.md` and `.json`, with every run under `runs/`. Exit code
0 when every bug mutant is killed, every mutant within its version range applies and the control is clean.

| mutant | kind | what it does | caught by |
|---|---|---|---|
| `mut-pool-watches-growth` | revert of a 2.2.1 fix | pools build their type name on every first growth again (A1) | `dbg`: `growth.first_n_pools` as a finding (the 2.2.1 CHANGELOG promise), plus a red package test in `ci` that is not the assert artifact |
| `nov-filter-watches-growth` | the same class in a new place | filters build their description on every first growth | `dbg`: a regression in `growth.first_n_filters` |
| `mut-tests-boxed-zero` | revert of a 2.2.1 fix | an allocation test asserts with `Is.EqualTo(0)` again | `ci`: a red test of cause `test-measurement-artifact`, status `test-artifact` (A5) |
| `nov-debug-world-get-label` | the same class in a new place (M3 of the completeness critique) | under `KENSEI_DEBUG` `World.Get<T>(Entity)` builds its operation label on every call | `dbg`: `access.world_get_has` as a finding (frame-zero) |

## Output

`Stand/out/reports/alloc/<flavor>-<rel|dbg>/report.md`, `report.json` (`findings`, `regressions`,
`contract_questions`, `documented`, `watchlist`, `contract_profile`, `promise_drift`, the difference table)
and `repro/<id>/`: a console program with a `.csproj` that compiles the KenseiECS sources of the tree
(`dotnet run -c Release --project <csproj>`), built and run once by the stand to check that it reproduces
the number. `--ci-parity` writes `<flavor>-ci/`.

## Adding a check

- A scenario: a class in `Probe/Scenarios.*.cs` registered in `Registry.All()` (newer API only through
  `Probe/Compat.cs`, so the probe still compiles against 2.1.1), its rules in `tools/contracts.py`
  (`RULES`), a repro template in `tools/repro.py` and, for a known problem, a canary in
  `tools/analyze.py` and an entry in `Stand/summary/catalog.json`.
- A promise: `promise(id, since, what, *sentences)` in `tools/contracts.py`, with `since` the first version
  whose docs contain it.
- A mutant: a patch in `mutants/` with its `Run:` lines; check it with `Stand/alloc/run.sh --selftest --mutants <name>`.
