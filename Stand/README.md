# KenseiECS test stand

The stand checks a KenseiECS source tree against the contract that the documents of that same version
promise, in release and in `KENSEI_DEBUG` builds, and gathers the result into one summary. Run it before
every release: the release candidate against the previous release.

It exists because the problems of 2.2.0 were found by hand. A game project ran the API in a real game, and a
review on Windows read the code and confirmed each finding with a probe. The stand does both automatically,
with one command, on any tree: six modules, each with its own oracle, a shared catalog of known problems, and
a comparison with a baseline tree.

The stand only finds; it fixes nothing. Its one rule:

> **A defect is a broken promise of the documents of the version under test** (XML docs, `docs/`,
> `CHANGELOG.md` of that tree), with a repro that the module compiled, ran and saw fail. Behaviour that no
> written promise forbids is a **question** to the contract, never a defect; behaviour that the documents
> describe is **documented**; a change against the baseline that breaks no promise is a **regression**.

The catalog of known problems with their status on 2.2.0 and on the 2.2.2 candidate: [CATALOG.md](CATALOG.md).

- [Quick start](#quick-start)
- [What the stand catches](#what-the-stand-catches)
- [Modules](#modules)
- [Statuses](#statuses)
- [The versioned contract](#the-versioned-contract)
- [Reading the reports](#reading-the-reports)
- [Corpus: every bug found becomes a corpus entry](#corpus-every-bug-found-becomes-a-corpus-entry)
- [Self-test](#self-test)
- [Adding a new class of problems](#adding-a-new-class-of-problems)
- [What the stand does not cover, and what to do before a release](#what-the-stand-does-not-cover-and-what-to-do-before-a-release)
- [Maintenance](#maintenance)
- [Timing](#timing)

## Quick start

```sh
Stand/run.sh --quick --ref v2.2.0                       # a release: all six modules, release and KENSEI_DEBUG
Stand/run.sh --quick --baseline v2.2.0                  # the working copy against 2.2.0
Stand/run.sh --full --ref v2.3.0 --baseline v2.2.1      # before a release: the candidate against the last release
Stand/run.sh --selftest                                 # does every module still catch what it must?
Stand/run.sh --quick --modules contracts,runner         # some of the modules
Stand/run.sh --summary-only --ref v2.2.0                # rebuild the summary from the reports that are there
```

The result is `Stand/out/reports/summary-<label>.md` (and `.json`); the self-test writes
`Stand/out/reports/selftest-<label>.md`. The label (flavor) is the tag for `--ref`, `wt` for the working copy,
otherwise the folder above `--src` or `--flavor`.

### Requirements

- bash, python3 3.9+ (standard library only), git, perl and curl.
- A .NET SDK 10. The stand's projects target `net8.0` and run on it with `DOTNET_ROLL_FORWARD=Major`. It is
  found through `$DOTNET`, `$DOTNET_ROOT`, `dotnet` on `PATH` or the usual install folders; without one,
  `Stand/run.sh` downloads the .NET 10 SDK into `Stand/out/tools/dotnet-sdk/` with the official
  `dotnet-install.sh`. `alloc` downloads the official .NET 8 runtime into `Stand/out/tools/dotnet8/` (checked
  by SHA-512), because CI runs the tests on it.
- For the `unity` module, a Unity 6 editor with a build module (Android if installed, otherwise the standalone
  target of the machine). The editor is `UNITY` (the binary, `Unity.app` or the editor folder), otherwise the
  newest suitable editor of the Unity Hub install folders on macOS, Windows or Linux (including the Hub's
  secondary install path); `UNITY_VERSION` picks a version. Without an editor the module is skipped and the
  summary says so. Unity runs in batchmode on a scratch project of its own under `Stand/out/unity/`, one process
  at a time, and never touches another project or an open editor.
- `--ref` needs `Stand/` inside a git checkout of KenseiECS (or `KENSEI_REPO` pointing at one). The stand only
  reads it (`git log`, `git show`, `git archive`).
- Tested on macOS arm64. The Windows and Linux branches (SDK and editor discovery, the .NET 8 runtime for
  `win-x64`/`linux-x64`) are written but were not run there.

### `Stand/run.sh`

| flag | what it does |
|---|---|
| `--ref <git-ref>` | test a git ref of the repository: it is extracted read-only with `git archive` into `Stand/out/src/<name>/`: the package, the generator, both test projects, the solution, `docs/` and `README.md`. `<name>` is the tag itself; a branch gets `<ref>-<short commit>` (a moved branch never replaces a tree silently). A tree extracted earlier is completed with the parts it lacks from the same commit. |
| `--src <folder>` | test a package folder (or a folder holding `KenseiECS/`). Default: `KenseiECS/` of this checkout, label `wt`. Its documents are `docs/` next to the package. |
| `--flavor <label>` | the label of the tree in report folders |
| `--quick` / `--full` | module mode (default `--quick`); `--minutes N` goes to `fuzz-core --full` |
| `--modules a,b` | modules to run (default all six). The summary always covers all six: a module that did not run now is taken from its last report on this tree, marked "earlier run"; a report older than the newest file of the tree (package, generator, docs) is left out as "stale". |
| `--configs rel,dbg` | builds (default both); `alloc` also runs `--ci-parity` once (build `ci`) |
| `--baseline <ref or label>` | compare with another tree. Every module gets `--baseline` (its own comparison: new signatures, growth, regressions); `alloc` and `generator` also get its sources. Modules without a report on the baseline run on it first (`--rerun-baseline`: always). The baseline summary is rebuilt, and the summary gets the section "Against &lt;baseline&gt;". Without `--baseline`, `alloc` still measures against v2.1.1. |
| `--no-unity` | do not start Unity; the summary takes the last `unity` reports of the tree if they were made on the same package (the module records a digest of it) |
| `--summary-only` | run nothing, rebuild the summary |
| `--selftest` | instead of a run: the mutation self-test of the selected modules on a copy of the tree ([Self-test](#self-test)) |
| `--module-timeout SEC` | limit of one module run (1800 s in `--quick`, 14400 s in `--full`, 3600 s per self-test) |
| `--no-history` | do not copy the previous reports to `Stand/out/history/` before they are overwritten |

Exit code: 0 every module ran (with `--selftest`: every self-test passed); 1 a module failed; 2 bad usage.
Findings do not change the exit code. Logs of every module run: `Stand/out/logs/stand/<label>-<time>/`. One run
per label at a time (a lock in `Stand/out/locks/`). Modules run fastest first (contracts, runner, generator,
fuzz-core, alloc, unity), so a broken build shows in seconds.

## What the stand catches

| class | what exactly | module | examples from 2.2.x |
|---|---|---|---|
| A listener changes the world during a structural change | a listener destroys or recreates the entity or a component inside `Add`, `Remove`, `DestroyEntity`, `Warmup`, `CopyEntity`, `CommandBuffer.Playback` | fuzz-core | W1-W4, W3c, N1-N5 |
| Ownership of pooled lists | one `List` shared by two entities, the list of a live buffer back in the free pool | fuzz-core, alloc | W5 |
| A debug check that lets a user error through | a stale handle under `KENSEI_DEBUG` passes while another entity is being destroyed | fuzz-core (dbg) | W6 |
| Lifecycle and misuse of the API | state broken after an exception, an exception of the wrong type, references kept after `Destroy`, spans after `Reserve` | contracts | G1, G2, WL1, C-LOAD, C-ADD-REJECT, Q-SPAN-RESERVE |
| The 2.3.0 reserve and deferred unsubscribe | `Filter.ReservedCount` taken from the types (the smallest Inc reserve, the sum of the Any reserves, the cap at `ReservedEntities`, any order of the calls, the cap of the obsolete `World.Reserve`); `World.UnsubscribeLater` (the outermost `Run`, a nested runner, a phase on its own, `NextTick`, a destroyed or reused entity, `Clear`, a destroyed world, a dying entity) | contracts | the 2.3.0 mutants |
| Quality of diagnostics | an `[Obsolete]` text that contradicts the behaviour, `StorageGrown` owners that cannot be told apart, a missing signal | contracts | G3, G4, G5 |
| Allocations and memory | a regression against the baseline, an allocation where the docs promise 0 B, the cost of `Reserve`, the 2.3.0 reserve for the peak (`ReserveEntities` and the hot pools, filters created afterwards) and a type nothing reserved (it grows, it is not counted), "green locally, red in CI" | alloc | A1-A5, A-PROFILER, A-FILTER-DENSE |
| The contract of SystemsRunner and EcsBootstrap | the `Init` rollback, a runner in two slots, phase names, `Shared` | runner | R1-R4, R-SHARED, R-DUPNAME |
| The source generator | silent non-injection, a false or missing KECS diagnostic, errors and warnings in `.g.cs`, a crashing generator, a lost compiler signal across versions, the documented upgrade changing the order | generator | GN1-GN8, NF1-NF5 |
| The Unity build pipeline and runtime | `#error` and the guard in content and Development builds, a way around the guard, `EcsEntityView.Spawn` | unity | U1, U-DEVDEFINE, U-SPAWN-NESTED |

Examples (full repros: the links in [CATALOG.md](CATALOG.md)):

```csharp
// W1 (fuzz-core): Warmup and a listener that destroys the temporary entity.
w.Pool<B>().AddListener(listenerThatDestroysOnAdded);
w.Warmup();                  // OnAdded<C> now arrives for a slot without an entity; in release the next
                             // CreateEntity inherits its components and its place in the filters

// W2 (fuzz-core): partners that remove each other in OnRemoved.
// OnRemoved<X>(e) -> pool<Y>.Remove(e); OnRemoved<Y>(e) -> pool<X>.Remove(e);
// -> endless recursion, stack overflow, the process dies (exit code -6): the fuzzer runs scenarios in worker
//    processes and catches it with the seed

// N5 (fuzz-core, in 2.2.0 and 2.2.1): a filter listener removes K and adds it back during Add<K>.
ref var k = ref pool.Add(e);  // the remaining OnAdded<K> go to the re-added K a second time, and k points at
k.P = 7;                      // the new component instead of the discarded scratch value ComponentPool.Add promises

// G1 (contracts, 2.2.0, release): CreateEntity after Destroy.
var w = new World(); w.Destroy();
w.CreateEntity(new CA());    // NullReferenceException, and afterwards...
w.IsAlive(Entity.Null);      // ...this throws too, although on a fresh destroyed world it returns false

// R3 (runner): one runner in two slots.
r0.Add(g); r0.Add(g); r0.SetActive(1, false);
r0.GetSystemInfo(0).IsEnabled;   // true, yet slot 0 no longer runs the group

// GN5 (generator): namespaces Game.AI and Game.Ai with a system of the same name: the generator throws
// ("hintName must be unique"), only a CS8785 warning is left, and no system of the assembly gets its Init.

// A1 (alloc, 2.2.0, KENSEI_DEBUG): the first growth of a pool builds typeof(T).Name, about 200 B per pool
// that 2.1.1 did not allocate (a regression against 2.1.1; 2.2.1 builds the name only when Reserve watches).

// U1 (unity, 2.2.0): Debug Mode on -> PlayerBuildInterface.CompilePlayerScripts(options = None) for a
// content build -> CS1029 from DevTools/KenseiDebugReleaseCheck.cs.
```

## Modules

Each module runs on its own: `Stand/<module>/run.sh [--quick|--full] [--src <KenseiECS folder>] [--flavor <label>]
[--debug] [--baseline <label>]`, and `--selftest`. It writes `Stand/out/reports/<module>/<label>-<rel|dbg>/report.md`
and `report.json`. Builds go to `Stand/out/bin|obj/<label>-<rel|dbg>/<project>/` (`Stand/Directory.Build.props`),
so versions and configurations never share an output folder. The stand's code builds against v2.1.1, v2.2.x
and the working copy; API newer than the oldest tree is reached through reflection, and a check whose API is
missing says so instead of failing. The flags of each module are in the header of its `run.sh` and its README.

| module | what it does | details |
|---|---|---|
| **fuzz-core** | Model-based random tester of the core. The reference is a small ECS on dictionaries that follows the documented contract (orders from `docs/architecture.md`). Random scenarios: `CreateEntity`, `Add`/`Remove`, `ref` writes, `CopyEntity`, `CommandBuffer`, `AddEvent`/`EventBuffer`, OneFrame cleanup, `Warmup`, filters; listeners of three kinds react with nested operations. After every operation the world and the model are compared: outcome, every callback in order, filters, internal consistency, list ownership. Workers are separate processes, so a stack overflow or a hang is caught with its seed. `--quick` runs a fixed set of 30 000 seeds, so two trees compare seed by seed. Families are assigned by mechanism facts, not by rule. | [fuzz-core/README.md](fuzz-core/README.md) |
| **contracts** | The matrix "public member x scenario" (destroyed world, destroyed and re-initialized runner, stale handles, `Entity.Null`, negative and huge arguments, `null`) with the outcome class of every cell and a probe of the state after every call; promises outside the matrix (`[Obsolete]` texts, `StorageGrown` owners, the `FirstGrowthAfterReserve` contract, `Filter.Entities` spans around `Reserve`, references kept by `World.Destroy`; since 2.3.0 the reserve a filter takes from its types and `World.UnsubscribeLater`). | [contracts/README.md](contracts/README.md) |
| **alloc** | Allocations and memory on .NET 10 and the official .NET 8 runtime: bytes per operation and one-time bytes, a fresh process per scenario, confirming runs, the cause of one-time bytes established by experiment (`--pretouch-type-names`, `--run-cctors-of`), the first growth of pools and filters, the 2.3.0 reserve for the peak (`ReserveEntities` and the hot pools, filters created afterwards: 0 B) and a type nothing reserved (it grows; not counted in `GrowthsAfterReserve`), memory of `Reserve` for 100-1000 types. Against the baseline (default v2.1.1); differences that only JIT tiering explains are left out. `--ci-parity` runs the package's test projects on .NET 8 as CI does. | [alloc/README.md](alloc/README.md) |
| **runner** | Random `SystemsRunner` trees (groups, phases, a runner in two slots, a failing `Init`, a throwing `Destroy`, `cleanWhenDisabled`) against an executable model of the contract, plus targeted scenarios; `EcsBootstrap` is compiled as is against minimal `UnityEngine` stubs. `--contract-version X.Y.Z` judges a tree by the contract of another version (the rules decided for 2.2.2 already exist). | [runner/README.md](runner/README.md) |
| **generator** | A systematic catalog of about 300 inheritance layouts and random programs -> `CSharpGeneratorDriver` -> compile -> load into a collectible `AssemblyLoadContext` -> `SystemsRunner.Init`. The oracle is a model of the documents: diagnostics, a clean `.g.cs`, every field injected with what the documented call returns, the `Init` order. Cross-version rules run programs on the 2.1 line and on the baseline too: the documented upgrade must keep the order (GN7), a compiler error must not turn into silence (GN8). | [generator/README.md](generator/README.md) |
| **unity** | The package in a scratch Unity project in batchmode: the Debug Mode switch with and without a Build Profile, the release guard and the check after the script compile, Development builds, SBP, AssetBundles, Addressables 2.9.1, the generator inside the editor, `EcsEntityView.Spawn`, a runtime smoke test, players on macOS. 28 cases in `--quick`, 53 in `--full` (`cases.json`). Every build finding gets an editor-script repro that the module runs in Unity again. Exit code 1 means findings. | `Stand/unity/run.sh --help`, `--list` |

## Statuses

Every entry of the catalog gets one status per tree, and one per build (`rel`, `dbg`, `ci`):

| status | meaning |
|---|---|
| **defect** | a promise in the documents of this version is broken; the module has a verified repro |
| **regression** | the behaviour changed against the baseline without breaking a documented promise (A1 on 2.2.0 against 2.1.1, GN8) |
| **question** | it reproduces, but the documents of this version neither promise nor forbid it, or the promise appears only in a later version: the contract's author decides (G1 on 2.2.0, R1, Q-SPAN-RESERVE) |
| **documented** | the documents of this version describe the behaviour (C-LOAD, A2 on 2.2.1) |
| **absent** | checked, does not reproduce |
| **not-run** | not checked in this run: the module was not selected, failed or was skipped, the case exists only in `--full`, the build did not run, or the problem cannot occur in the builds that ran |

Per build, **n/a** means the problem cannot occur in that build (G1 under `KENSEI_DEBUG`, W6 and R4 in release). In
a status like `question: dbg (rel n/a)` the builds after the colon show it. Modules report the same four kinds of
items (`defect`, `regression`, `question`, `documented`); everything they report as a defect or a regression that
no catalog entry claims is listed as outside the catalog.

A **documented** item names the sentence it rests on, as the documents of the tree write it (contracts and the
generator: the claim's quote; alloc: the promise's fragment; fuzz-core: the sentence it looks up for each `D-` item,
see [fuzz-core/README.md](fuzz-core/README.md)). The summary looks the quote up in the tree's own documents where it
decides something (the must-catch check below). The red CI of A5 is the assert of the package's own tests, not an
allocation of the library: alloc reports it as `test-artifact`, and the summary gives A5 the status defect (of the
package's test suite: a red CI blocks a release).

**Must-catch entries.** The 27 problems that the game project and the Windows review found on 2.2.0 (G1-G5, W1-W6,
R1-R4, GN1-GN6, A1-A5, U1) carry `"mustCatchOn": "2.2.0"` in `catalog.json`. The summary of a 2.2.0 tree checks that
the stand catches every one of them and says so at the top ("Must-catch entries of 2.2.0: 27 of 27 caught") and in a
section of its own. Caught means not absent and not not-run: defect, regression, question and documented all count,
a documented status only with a quote that is real text of the tree's documents (its XML docs, `docs/`,
`CHANGELOG.md`, `README.md`). G3 is documented on 2.2.0 (the XML of `Add(SystemsRunner, string)` describes the null
case), and that counts.

## The versioned contract

A promise counts only for the versions whose documents make it. Every module reads the version of the tree from
its `package.json`, and every rule rests on a quoted promise with the version where it first appears (`since`):
claims in `fuzz-core/src/Mechanism.cs`, `contracts/src/Policy.cs` and `Profile.cs`, `alloc/tools/contracts.py`,
`runner/src/Profile.cs`, `generator/src/GenStand/Claims.cs`, `unity/rules.json`.

- A violated promise that the documents of the tree make is a **defect**.
- A violated promise that only a later version makes is a **question** on the older tree. On 2.2.0, G1, G2 and G5
  are questions (2.2.1 wrote those promises down), U-DEVDEFINE too; on a 2.2.1 tree the same behaviour would be a
  defect.
- The modules look for the quoted sentence in the documents of the tree itself: `docs/` next to the package
  (`--ref` extracts it), the XML docs in the sources and `CHANGELOG.md`; without `docs/` they take the documents
  of the tag of that version with `git archive`. A sentence that should be there for the version but is not found
  turns the rule into a question and adds a stand warning (contracts: claim warnings, alloc: stale rules), so a
  rewritten sentence cannot switch a check off silently. The summary also lists, against the baseline, every
  promise a module checked there and no longer checks here.
- A `CHANGELOG.md` that starts with `[Unreleased]` means the next patch: the working copy (`package.json` 2.2.0
  with the 2.2.1 changes) is held to the promises its own documents already make.
- Decisions that are not written down yet can be modelled ahead: `Stand/runner/run.sh --contract-version 2.2.2`
  checks R1, R3 and R4 by the rules decided for 2.2.2.
- A **regression** needs a baseline: a module compares with the report of the same module and build on the
  baseline tree, and calls a change a regression when no promise of the tree under test forbids it.
- Where a later version describes what was a question, the question becomes **documented** on that version, with the
  sentence: on 2.2.2 the span from `Filter.Entities` after a growing `Reserve` (Q-SPAN-RESERVE, the XML of
  `Filter.Entities`), a dying entity that enters a filter because a listener re-added a component (`Q-dying-enter`, the
  XML of `IFilterListener`), the world events suppressed for the whole `Warmup` (`Q-warmup-nested-world-events`,
  `world-lifecycle.md`), a stale `EventBuffer` copy (`Q-stale-copy`, the XML of `EventBuffer<T>`), and the private
  injected fields of a base in another assembly that KECS006 does not see (`source-generator.md`). Where it describes
  the outcome of something the stand did not run, the stand runs it: on 2.2.2 fuzz-core stores `EventBuffer` copies of
  another entity's buffer and stale copies written back after their component was removed, and checks the documented
  outcome (an empty list of the entity's own, or the entity's buffer left as it is).
- The modules read only documents a clean checkout has: the API pages that `docs/scripts/gen-api.mjs` writes into
  `docs/api/` (everything there but `docs/api/index.md`) and VitePress' `docs/.vitepress/` are never read (they are
  ignored by git; the XML docs they come from are read from the sources), so a tree gives the same results with or
  without a local docs build.

## Reading the reports

### The summary: `Stand/out/reports/summary-<label>.md`

1. **Header**: the tree (path, package version, commit, uncommitted changes), mode, command; on a 2.2.0 tree the
   must-catch line ([Statuses](#statuses)).
2. **Module runs**: the state of every module and build (`ok`, `**failed**`, `earlier run`: taken from a previous run;
   `stale`: the tree changed after the report; `other tree`), mode, the module's own baseline (`vs`), time, and
   the number of signatures per status. A failed or stale report is left out of the summary.
3. **Catalog of known problems**: one row per entry of `Stand/summary/catalog.json` with its status, per build,
   what the modules saw and a link to the repro; on a 2.2.0 tree then **Must-catch entries of 2.2.0**: each of the 27
   with its status, caught or not, and the quote a documented one rests on.
4. **Defects outside the catalog**: the most important part for a new version. One row per signature that no
   entry claims. Triage each one ([Corpus](#corpus-every-bug-found-becomes-a-corpus-entry)).
5. **Regressions outside the catalog**: changes against the module's baseline that no entry claims.
6. **Against &lt;baseline&gt;** (with `--baseline`):
   - catalog entries that were fixed, appeared, became documented or changed;
   - signatures compared per module and build where both sides have a report with the same inputs (the same mode,
     and for fuzz-core and runner the same fixed seed set): new signatures **inside a known family or catalog
     entry** (the case a status comparison hides), new signatures of no known family, gone signatures, sharp
     growth (hits x3 and +20 from the same inputs, alloc bytes x2) and changed status;
   - promises a module checked on the baseline and does not check here;
   - the modules' own comparisons (fuzz-core: newly failing seeds; contracts: changed matrix cells; generator:
     programs with a new defect; ...).
7. **Stand warnings**: what a module says about its own run (unverified repros, unstable cells, stale rules).
8. **Contract questions**: counts per module and build; the questions are listed in the module reports.

A **signature** is a mechanism and a place, so one bug is one row however often it is hit, and a new bug does not
hide in an old family:

| module | signature | example |
|---|---|---|
| fuzz-core | `rule@operation[reaction]{facts}` | `R-cb-free-slot@warmup[top]{warmup-temp-died}` |
| contracts | `kind\|scenario\|frame\|detail` | `leak\|World.Destroy\|groups` |
| alloc | `metric:scenario:cause` | `first:growth.first_n_pools:pretouch_type_names` |
| runner | `RULE @ place` | `RUNNER-SHARED-PROPERTY-STALE @ Init:group:d2+` |
| generator | `rule\|class\|shape@place` | `R-LOST-SIGNAL\|LOSTSIGNAL\|drop-PublicOverrideNoBase@hand` |
| unity | `rule\|mechanism@place` | `entity-view-spawn\|spawn-nested:outer-lost-components` |

`summary-<label>.json` holds the same for tools: `catalog.entries[]` (`id`, `status`, `builds`, `repro`, `corpus`,
`findings`), `signatures[]` (one row per module, build and signature: `status`, `family`, `catalog`, `hits`,
`magnitude`), `defects[]`, `uncatalogued[]`, `regressions[]`, `uncataloguedRegressions[]`, `moduleRuns[]`,
`promises`, `warnings`, `comparability`, `baseline`.

### Module reports and repros

Every module's `report.json` lists its items with `id`, `signature`, status, class, rule, severity (an estimate),
the repro and the seed or parameters; `report.md` is the same for reading, with the quoted promise of each item.

| module | repro | how to repeat |
|---|---|---|
| fuzz-core | `repro/repro-*.cs`: an NUnit test (with `kit/ReproKit.cs`) that fails while the defect is there; `min/*.json`: the shrunk scenario | `Stand/fuzz-core/run.sh --replay <min/...json> [--debug] --src ...` |
| contracts | `repro/*.cs`: a class with `Run()` that prints what the stand saw; `repro-check.json` says it did | built by `Stand/contracts/repro/` |
| alloc | `repro/<id>/Program.cs` and `.csproj`: a console program against the sources of the tree | `dotnet run -c Release --project <...csproj>` |
| runner | `repro/*.cs` (with `kit/Probes.cs`), `cases/*.json` | `Stand/runner/run.sh --replay <cases/...json>` or `--gen-seed <seed>` |
| generator | `repro/<id>.cs` (the clean form), `.model.json`, `.generated.cs`, `.observed.txt` | the header of the `.cs` says what `ReproMain.Run()` prints |
| unity | `repro/*.cs`: an editor script for `Assets/.../Editor/` | the module runs it in Unity and compares it with the case |

Before reports are overwritten, the previous ones go to `Stand/out/history/<label>/<time>/` (the last 5 runs per
label are kept).

## Corpus: every bug found becomes a corpus entry

The corpus is the permanent part of each module (in its folder, not in `out/`). It runs on every run before the
random search and turns a bug found once into a deterministic check:

| module | corpus | what is in it |
|---|---|---|
| fuzz-core | `Stand/fuzz-core/corpus/*.json` | shrunk scenarios (seed, layout, listeners, operations) with the family and rule that must fire, or the contract question that must be met; statuses `reproduces`, `passes` (fixed), `fails-differently`, `skipped`, `question-met` |
| contracts | `src/Matrix.cs` (`Matrix.Curated`), `src/Known.cs` | curated cases and the known problems (G1-G5, WL1, C-LOAD, C-ADD-REJECT, C-FILTER-DESTROYED) with how to recognise them |
| alloc | `Probe/Scenarios.*.cs`, `tools/repro.py`, `watchlist()` in `tools/analyze.py` | the measured scenarios, repro templates, canaries for A1-A5, A-PROFILER, A-FILTER-DENSE |
| runner | `src/Scenarios.cs`, `src/CustomScenarios.cs`, `src/BootstrapScenarios.cs`, `src/Rules.cs` | targeted scenarios and rules linked to R1-R4, R-SHARED, R-DUPNAME |
| generator | `src/GenStand/Catalog.cs`, `src/GenStand/KnownIssues.cs` | the layout catalog (runs in `--quick` too) and the recognition of GN1-GN8, NF1-NF5 |
| unity | `cases.json`, `rules.json`, `known.json` | cases with the expected outcome, rules with quotes of the documents, the recognition of U1 and U-SPAWN-NESTED |

The layer across modules is `Stand/summary/catalog.json`: an entry per known problem with how to recognise it in
the module reports (`match`: a module's known record, a fuzz-core family or mechanism facts, a question or item id,
an alloc scenario). The summary gives each entry its status on any tree and lists what no entry claims.

Every random finding carries what is needed to get it again: fuzz-core `signatures[].seeds` and `min/*.json`
(scenario seed = `--seed` + index; a shrunk scenario no longer depends on it); runner `findings[].seed`
(`--gen-seed <seed>`); generator `sources` (`catalog:<layout>` or `fuzz:<seed>`) and `repro/<id>.model.json`;
contracts the case number and `--seed` of the case order. alloc and unity are deterministic: the scenario or case
is the repro.

**The rule: every bug found becomes a corpus entry.** When a finding is confirmed (a real broken promise, a
repro that reproduces):

1. Add a deterministic check to the corpus of the module that found it:
   - fuzz-core: take the candidate from `Stand/out/reports/fuzz-core/<label>-<build>/corpus-candidates/` (or run with
     `--save-corpus`), put it into `corpus/<family>-<what>.json`, write its `Note` and `Configs`; a new mechanism
     also gets a family in `src/Classifier.cs`;
   - contracts: a case in `Matrix.Curated` and an entry in `Known.All`; runner: a scenario in `Scenarios.cs` and
     the rule's link to the id in `Rules.cs`; generator: a layout in `Catalog.cs` and its recognition in
     `KnownIssues.cs`; unity: a case in `cases.json` and an entry in `known.json`; alloc: a scenario in `Probe/`
     and a canary in `analyze.py`.
2. Add an entry to `Stand/summary/catalog.json` (id, group, source, module, builds, title, example, `match`).
3. Run `Stand/run.sh` on a tree that has the bug: the entry must become `defect` (or `question`) and the finding
   must leave "outside the catalog". Then on a tree with the fix: `absent`.
4. Add a mutant for it to the module's `mutants/` (a rollback of the fix) so the self-test keeps proving the
   module catches it ([Self-test](#self-test)).
5. Regenerate the catalog page: `python3 -I Stand/summary/summary.py catalog-md` (from `summary-v2.2.0.json` and
   `summary-wt.json`; `--release`, `--current` and `--also` choose other labels).

A false finding is fixed in the module's rule, never hidden by a catalog entry: the catalog only recognises
findings. One exception to the rule is open: N5, which fuzz-core still reports under family NEW; it needs a family
of its own (`src/Classifier.cs`) and a corpus entry.

## Self-test

A module that stopped catching a class of bugs looks exactly like a tree without bugs. The self-test proves that
it still catches them: `Stand/run.sh --selftest` (or `Stand/<module>/run.sh --selftest`) applies each patch of
`Stand/<module>/mutants/` to a temporary copy of the tree under test (the tree itself is never touched), runs the
module on the copy and on the unmutated copy, and compares by signature:

- a **bug** mutant must produce a new signature with a verified repro, of the expected rule and status. The bug
  mutants are rollbacks of the 2.2.1 fixes (and of 2.1.1 fixes the fuzzer models) and "the same class in a new
  place" from the critique of the stand's completeness: for example a debug handle check that ignores the
  generation must come out as a new mechanism, not as W6; a dead `CopyEntity` source that leaks an entity must
  not shrink into N3; a first-growth string built for filters must show as a filter regression;
- a **fix** mutant (an emulated correction of a known defect: W1, W6, WL1, NF2, R1-R3, U-SPAWN-NESTED, ...) must
  make that defect disappear without any new finding: the check against false defects on corrected behaviour;
- a **doc** mutant (contracts) is a change the documents of the tree describe: it must come out as documented,
  without a defect;
- a **regression** mutant (fuzz-core) must come out with status regression only.

The patches are unified diffs against the tree (the package folder, or the folder that holds it), after a header of
`Key: value` lines (`Mutant`, `Kind`, `Expect`, `Removes`, `Why`, and per module `Configs`, `Cases`, `Contract`, ...;
fuzz-core keeps its metadata in `mutants/mutants.json`). The module READMEs describe their format.

**Versions.** A patch changes the code of some versions only. `Since: 2.2.2` / `Until: 2.2.1` (fuzz-core: `since`,
`until`; `Version-note` says why) name the contract versions of the trees it is for: the package.json version, or the
next patch when the CHANGELOG starts with a non-empty `[Unreleased]` section and package.json is not above its last
release. On a tree outside the range the mutant is **n/a**, listed with its reason ("contract version 2.2.2: the
mutant is for 2.2.1 and older ...") and in the summary's "Skipped by contract version". Within its range a patch that
does not apply is a failure (`not-applicable`): the code moved on, so the patch is refreshed, or it gets an `Until`
and a twin is cut for the new code, so that no class of defects drops out silently. Where 2.2.2 rewrote code, the
mutant has a `-222` twin cut against the final 2.2.2 code (fuzz-core 5, runner 15, generator 6), and the fixes 2.2.2
made itself are rolled back by mutants of their own (fuzz-core: the typed `OnAdded` after the world update and the
list holder of `EventBuffer` stores; runner: R1 in nested runners, the flat `AggregateException`, the single registration
of a runner, the reserved name "late"). A mutant that is equivalent to a version's code is gated the same way
(runner `novel-r3-fix-misses-name-path`: since 2.2.2 a runner sits in one slot only). Emulated fixes of a problem a
version fixed itself (W1, W6, WL1, R1-R3, R-SHARED, U-SPAWN-NESTED) stop at 2.2.1: on 2.2.2 the clean base run is
that check. Where 2.3.0 rewrote code, the old mutant stops at 2.2.2 (`Until: 2.2.2`) and a `-230` twin cut against the
2.3.0 code takes over: contracts 4 (the growth report of pools and filters, `World.OnStorageGrown`, the entity slots of
`CreateEntity`), alloc 2 (the growth report), runner 3 (`SystemsRunner.Run`, which now brackets its work with the world's
run depth). The 2.3.0 promises have mutants of their own (`Since: 2.3.0`): contracts 13 (the reserve a filter takes from
its types, `UnsubscribeLater`), alloc 2 (a filter created after the reserve, an unreserved growth counted). The 2.3.1 work in
progress moved `SystemsRunner.Run` once more (the loop into `RunSystems`, the exit split into `ExitRun` and
`ExitRunAfterFailure`): the four `-230` mutants of that code stop at 2.3.0 and `-231` twins take over (runner 3, contracts
`nov-unsub-phase-skipped-231`). Every module copies the tree into fresh files (new modification times) and gives its builds labels that
name the tree, so a self-test never runs a binary built from another tree.

Current sets: fuzz-core 20 (12 for 2.2.2 and 2.3.0, 13 for 2.2.1; and 4 rollbacks of 2.2.1 fixes it lists as out of its
scope: they live in code it does not exercise), contracts 32 (26 for 2.3.0 and for 2.3.1, 13 for 2.2.2), alloc 8 (6 for
2.3.0, 4 for 2.2.2), runner 68 (33 for 2.3.0, 2.3.1 and 2.2.2, 43 for 2.2.1), generator 18 (12 for each), unity 7 (6 for 2.2.2 and 2.3.0).
Every fix listed in the 2.2.1 CHANGELOG is rolled back by a mutant: G1, G2, G3 and G5 in contracts, A1 in alloc, U1 and
U-DEVDEFINE in unity; alloc also rolls back the test fix behind A5, contracts breaks the new `FirstGrowthAfterReserve`
contract, and runner the newly documented start state of a group.

The result is `Stand/out/reports/selftest-<label>.md` with a row per module and build (bug mutants caught, fix and doc
mutants accepted, n/a), the mutants skipped by contract version, and the reports of every module in
`Stand/out/reports/<module>/selftest-<label>-<build>/`. Run it after every change to a module and before a release.

## Adding a new class of problems

A new class is a new kind of input or a new promise the stand does not check yet. The order is always the same:
find the promise in the documents (without one there is no defect, only a question), teach the module to create
the situation, teach the oracle to judge it, add a corpus entry, a catalog entry and a mutant, and check it on a
version with the bug and one without.

- **fuzz-core.** An operation: generation in `src/Generator.cs`, the shared reaction engine in `src/Side.cs`, the
  semantics in `src/ModelSide.cs` (the reference) and the execution in `src/RealSide.cs` (the world under test;
  internals are read by reflection, a missing field turns a check off). A promise: a claim with its `since` in
  `src/Mechanism.cs`. A family: its mechanism facts in `src/Classifier.cs` and its triggers in `src/Exclusions.cs`.
  What the contract does not say, the model does not claim: such differences become questions (`Q-...`).
- **contracts.** A new parameter type: `Args.Normal`; a new promise: `Policy.BuildClaims` (the sentence, the cases
  it applies to, the verdict, `since`); a new target or probe: `Catalog.Targets`, `Scenarios.Build`,
  `Probes.Define`; a curated input: `Matrix.Curated`; a known problem: `Known.All`. See `contracts/README.md`.
- **alloc.** A scenario: `Probe/Scenarios.*.cs` (a measuring window without asserts, boxing or interpolation); a
  repro template: `tools/repro.py`; a "0 B" or "documented allocation" promise: `tools/contracts.py`; a canary:
  `watchlist()` in `tools/analyze.py`.
- **runner.** A scenario: `src/Scenarios.cs` (or `CustomScenarios.cs` for what the model cannot express); a rule with
  its quote: `src/Rules.cs`; the expected behaviour: `src/Model.cs`; a new operation: `src/Spec.cs`, `src/Exec.cs`,
  `src/Fuzz.cs`; the version profile: `src/Profile.cs`.
- **generator.** The program shape: `src/GenStand/Model.cs` and `Render.cs`; a systematic layout: `Catalog.cs`; a random
  one: `Fuzz.cs`; what the documents promise: `Contract.cs` and `Claims.cs`; the oracle: `Oracle.cs`; recognition:
  `KnownIssues.cs`.
- **unity.** A case: `cases.json` (group, tier `quick`/`full`, expectation), its execution: `kind` in
  `Template/Assets/KenseiStand/Editor/StandCases.cs`; a rule with its quotes: `rules.json` (without its quote in the
  documents of the version a violation is a question); recognition: `known.json`.
- **A new module.** A folder `Stand/<module>/` with a `run.sh` that takes the common flags (`--quick|--full`,
  `--src`, `--flavor`, `--debug`, `--baseline`, `--selftest`) and writes `report.json` with items that have `id`,
  `signature`, status, `severity` and `repro`. In `Stand/run.sh`: its name in `ALL_MODULES` and `RUN_ORDER`; in
  `Stand/summary/summary.py`: a normalizer in `NORMALIZE` and, if it needs one, a matcher in `MATCHERS`.

## What the stand does not cover, and what to do before a release

The stand runs the code on CoreCLR (.NET 10 and .NET 8) and in Unity batchmode on the machine that runs it. It
does not cover:

| not covered | why it matters | what to do before a release |
|---|---|---|
| **IL2CPP on a device** | the package turns off `NullChecks` and `ArrayBoundsChecks` (`Il2CppSetOption`): a `NullReferenceException` or `IndexOutOfRangeException` that contracts reports can be memory corruption there; allocations on Mono and IL2CPP differ (A1 is one string per pool there, the escape analysis of .NET 10 can hide an allocation); the nanoseconds are only indicative | build a real game (or a benchmark copy of one) with the candidate for Android IL2CPP, run its tests and a long fight, compare the allocations over a few hundred frames with the previous version, profile on the device; for every finding of the class "NRE / IndexOutOfRange", decide what the contract promises |
| **The live editor with its GUI** | the Profiler, World Inspector and Systems windows, the Debug Mode switch in Project Settings by hand, importing the package from a git URL, Play Mode with and without domain reload | open the candidate in a real project, go through the windows and the Debug Mode switch, enter Play Mode with `_warmupOnStart`, read the console |
| **Contradictions between pages of the documents** | each module checks a promise against its own quoted sentence; no module compares pages with each other, so two pages that disagree are only noticed when a check lands between them (Q-SPAN-RESERVE: the XML, the FAQ and architecture.md disagree; R1: runner.md against `ISystem.cs`; GN7: the inheritance guide against the upgrade notes) | read the diff of `docs/`, the XML docs and `CHANGELOG.md` against the previous release; for every changed statement search all pages for the same subject and settle the conflict; every settled promise gets a rule in a module (and so a check) |
| A full Android build through Gradle and a run on a phone | the unity module stops a build after the script compile; players run on the desktop only | one real release and one Development build of a game with Addressables |
| Windows and Linux x64 | CI runs on Linux, reviewers use Windows; the stand was run on macOS arm64 | wait for green CI; for a new class of allocations, measure on x64 |
| Other versions of Unity and of the packages | the module was run with Unity 6000.3 and Addressables 2.9.1 (and once with 6000.6) | `UNITY=... Stand/unity/run.sh --full`, `--addressables <version>` |
| Parts of the core outside the models | fuzz-core does not model `World.Destroy` and what follows, `WorldSerializer`, `ParallelRunner`, groups other than one `Group<A,B>`, `EventBuffer.Prewarm`, `Filter.Reserve`; contracts has no `ParallelRunner`, `StorageGrowth`, `WorldConfig`, `ComponentType`, `SystemInfo` in its matrix | read the diff of these places; for new API add an operation to fuzz-core or a target to contracts |
| Orders the contract may change | fuzz-core follows the orders of `docs/architecture.md`; a reorder within one kind of callback is a regression, across kinds a defect | check such a change by hand and update the model together with the documents |
| Performance | only nanoseconds per call of `Listeners.Invoke`; no frame-time benchmarks | the benchmarks of the repository (`BENCHMARKS.md`) against the previous release |
| Promises the stand does not know | rules rest on curated sentences (contracts, alloc, unity) and models (runner, generator, fuzz-core); a new sentence without a rule is not checked | for every new promise in the documents and the CHANGELOG, follow [Adding a new class of problems](#adding-a-new-class-of-problems) |

Release checklist:

1. `Stand/run.sh --selftest` on the candidate and on the previous release: every module catches its mutants, and
   the mutants skipped by contract version are the ones the CHANGELOG explains.
2. `Stand/run.sh --full --ref <candidate> --baseline <previous release>` (or `--src` for a working copy); add
   `--rerun-baseline` when the stand changed since the baseline's reports were made, so both sides are judged by the
   same stand.
3. In the summary: no module failed, no entry is `not-run` without a reason; "Defects outside the catalog" and
   "Regressions outside the catalog" are triaged: every item is fixed, or entered into the corpus and the
   catalog, or found false and its rule fixed.
4. In "Against &lt;baseline&gt;": no new signature without a decision (especially inside a known family), no sharp
   growth without an explanation, no promise switched off; "fixed" matches the CHANGELOG.
5. The questions the release settles got their sentence in the documents (they become `absent` or `documented`).
6. The manual part of the table above: IL2CPP on a device, the editor with its GUI, the documents read for
   contradictions.

## Maintenance

- Extracted trees `Stand/out/src/<name>/` are read-only. To refresh one (a moved branch):
  `chmod -R u+w Stand/out/src/<name> && rm -rf Stand/out/src/<name>`; the next `--ref` extracts it again.
- Everything the modules and the orchestrator write is under `Stand/out/` (ignored by git). `Stand/out/unity/`
  holds the scratch Unity projects (several GB); deleting them costs one import on the next run.
- The catalog is edited in `Stand/summary/catalog.json` only; `CATALOG.md` is generated from it.
- No false defects: a module that reports a defect without a quoted promise of the tree's documents is fixed,
  not hidden in the catalog. A self-test that stops passing is a broken module, not a flaky one.

## Timing

Measured on an 18-core arm64 Mac (the self-test while other heavy work ran on it):

| module | `--quick`, one build | self-test, one build |
|---|---|---|
| contracts | 5-7 s | 40-120 s |
| runner | 4-5 s (20 000 trees) | 70-80 s |
| generator | 10-12 s (catalog and 600 programs, with the cross-version observations) | about 45 s |
| fuzz-core | 40-100 s (30 000 seeds, shrinking, repros) | 6-9 min |
| alloc | 50-120 s, `--ci-parity` 35-45 s | about 4-5 min (its debug and CI runs) |
| unity | 45-65 s on a warm scratch project; the first import of a new tree takes 3-5 min | about 3.5 min |

The whole `Stand/run.sh --quick` (release and `KENSEI_DEBUG`, all modules) takes about 8 minutes, `--selftest`
about 30-35 minutes. `--full` takes about half an hour, most of it fuzzing (`--minutes`, default 10). Only `--full`
runs the second matrix order and `int.MinValue` in contracts, 300 000 runner trees, 6000 generator programs, all
53 unity cases and 3 + 3 runs per alloc scenario.
