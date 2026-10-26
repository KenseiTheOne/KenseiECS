# Stand/generator

Tester of the KenseiECS source generator (`KenseiECS.Generators`). It builds stand programs — system
hierarchies over generated, hand-written, cross-assembly and generic bases, composite interfaces,
`OnInit` hooks, nesting, keyword and case-only names, partial parts — runs the generator of the tree
under test through `CSharpGeneratorDriver`, compiles, emits into memory, loads the assemblies into a
collectible `AssemblyLoadContext` and calls `SystemsRunner.Init`. The oracle compares what happened
with a model of the documented contract. Every finding gets a minimized repro that is compiled again
in a clean form and checked.

```sh
Stand/generator/run.sh --quick                                   # the working copy
Stand/generator/run.sh --quick --src Stand/out/src/v2.2.0/KenseiECS
Stand/generator/run.sh --quick --baseline v2.2.0                 # the working copy against 2.2.0
Stand/generator/run.sh --selftest                                # mutation self-test
Stand/generator/run.sh --help                                    # every option
```

`Stand/run.sh` runs it in release and `KENSEI_DEBUG` and passes `--baseline`. The module needs a .NET
SDK 8 or newer: `dotnet` from `PATH`, `$DOTNET` or `$DOTNET_ROOT/dotnet`; without one it installs the
.NET 8 SDK into `Stand/out/tools/dotnet-sdk/` with the official `dotnet-install.sh`.

## What a run checks

1. **Programs.** The layout catalog (`src/GenStand/Catalog.cs`, about 340 layouts: every base kind ×
   assembly × the derived class listing `IInitSystem` × `OnInit` calling `base.Init`, feature probes —
   groups of two to four types, `[Shared]` of interface, abstract, generic and nested types, filter
   attributes in one list, a foreign attribute in front of the injection attribute, hand-written
   overrides without `base.Init` in one and in two assemblies —, the documented diagnostics) and random
   programs (`Fuzz.cs`; seed N+i for program i, 600 in `--quick`, 6000 in `--full`). A program that does
   not compile even without the generator is discarded, never reported.
2. **Pipeline** (`Pipeline.cs`). Lib and Main compilations, the generator, the compiler, the runtime;
   every hand-written `Init`, `OnInit` and hook records which injected fields were still null.
3. **Oracle** (`Oracle.cs`) against the contract model (`Contract.cs`). Universal rules: the generator
   does not throw, reports only KECS diagnostics, emits code that compiles, every injected field is
   filled, each `Init`/`OnInit` runs once and the base before the derived `OnInit`, a diagnostic names
   the class, field or type it is about. Contract rules: documented layouts work, documented errors are
   reported (with the reason KECS006 must name, `R-KECS-TEXT`), nothing else is; the order of a chained
   `Init` is the one of the tree's version (2.2.0/2.2.1: `base.Init` before the derived fields, 2.2.2:
   after them and before the derived `OnInit`); 2.2.2's warning KECS013 on a hand-written override
   without `base.Init` (`R-MISSING-WARNING` when it is missing).
4. **Injected values.** After `SystemsRunner.Init` every non-null injected field is compared with what
   the documented call returns on the same `World` and `SharedData` (docs/guides/source-generator.md,
   "Injected with"): the filter of its `[Inc]/[Exc]/[Any]` attributes (`End` returns the existing
   filter for identical constraints), `world.Pool<T>()`, `world.Group<...>()`, `shared.Get<T>(key)`.
   Another object is `R-WRONG-INJECTION`, with the mechanism (`exc-dropped`, `key-ignored`, ...).
5. **Cross-version rules** (`CrossVersion.cs`) against other trees, see below.

## Statuses

| status | meaning |
|---|---|
| defect | breaks a promise the documents of *this* tree make (`findings` in report.json) |
| regression | a change against another tree that no promise forbids (`regressions`) |
| question | behaviour the documents do not settle, or a promise this tree does not make yet (`questions`) |
| documented | behaviour the documents of this tree describe as it is, such as a lost signal they explain (`documented`) |

**Versioned contract profile.** `Contract.cs` has the layout rules of a version line (profile 2.1 for
2.1.x packages, 2.2 otherwise; the version comes from `package.json` of the tree). On top of that every
rule rests on a *claim* (`Claims.cs`): the document, the exact sentence and the first version whose
documents contain it. A claim is active when the tree's document contains the sentence (or, when the
document is not part of the tree, when the version is new enough). A violation of an inactive claim is
a question: a promise made later than the tree, or one its documents dropped (then the report says to
update `Claims.cs`). A rule may rest on the same promise in the words of two versions (`a|b`): one active
sentence is enough. The report lists every claim and its state.

**2.2.2.** The rules 2.2.2 changed switch on the 2.2.2 sentences of the tree's own documents (also before
`package.json` is bumped), each with its quote in `Claims.cs`:

| claim (since 2.2.2) | what changes |
|---|---|
| `inheritance-order-222` | "The derived fields are filled first, then the base `Init` runs (...), then the derived `OnInit`": the chain target must see the derived fields filled (`ORDER-FIELDS-FIRST`); `order-chain-220` ("calls `base.Init` before its own fields") holds for 2.2.0/2.2.1 |
| `virtual-init-222` | "The generated `Init` is `public virtual` in every class that is not `sealed`": a generated base in another assembly is chained (KECS012 for a kept `base.Init`, works without it) |
| `generic-helper-allowed` | "A generic helper such as `Init<T>(World, SharedData)` whose type parameter the two arguments cannot infer does not take the call and is allowed" (no KECS006) |
| `layout21-kecs006-222`, `layout21-new-unrelated-222` | "A class in this layout without a `base.Init` call is `KECS006`", over any base `Init` but an explicit `IInitSystem.Init` and "a `new Init` in between that does not implement `IInitSystem.Init`, with no injected fields above it" (documented: the `Init` it hides does not run). On 2.2.0/2.2.1 the layout is documented ("the base runs only through your call"), the loss of the 2.1.1 KECS006 is `R-LOST-SIGNAL` (GN1); a defect there only with an `Init` overload in between over a chainable base (the KECS006 overload rule of CHANGELOG 2.2.0, which chains such a class too) |
| `kecs013`, `diagnostics-are-errors-222`, `kecs013-lib-visible` | the warning KECS013 on a hand-written override of a generated `Init` without `base.Init`; no lost signal where it is given; a documented miss over a base in another assembly whose injected fields are all private |
| `kecs006-lib-visible-222` | "of a base in another assembly only the `protected` and `public` ones are seen, as for `KECS013`, so a base there with only `private` injected fields is not reported": the 2.1 layout without `base.Init` over an unrelated `new Init` whose only injected fields above are private fields of a base in another assembly gets no KECS006, and those fields stay null: status documented (`R-FIELDS-NULL`, key `NULLD|...`), with the sentence (`quote` in report.json) |
| `chain-over-overrides-222` | "a derived class that lists `IInitSystem` itself is still chained over them, since the interface then maps to its generated `Init`": over hand-written overrides of a public `Init` that an explicit `IInitSystem.Init` next to it does not call, such a class is chained (KECS012 with `base.Init` in `OnInit`); on 2.2.0/2.2.1 the same layout is the 2.1 layout, and its KECS012 stays GN2 |
| `hook-222`, `hook-layout21-222` | "Override the hook, and it runs once, after the derived fields are filled" (CS0111/CS0108/CS0114 over the hook become defects; on 2.2.0/2.2.1 they are questions, GN4); KECS006 for such an override in the 2.1 layout |
| `upgrade-order-222` | after the documented upgrade from 2.1 the base still sees the derived fields (GN7). The 2.2.0/2.2.1 guide says both "makes the call before the derived fields" and "The order stays the same": a question there |

Warnings in generated code: CS0108/CS0114 where the generated `Init` hides an inherited `Init` break
the CHANGELOG 2.2.0 promise ("declared new, no more CS0108/CS0114") and are defects; CS0108/CS0114 on
`OnInit` and CS0109 are questions (over a base `OnInit` hook defects from 2.2.2, claim `hook-222`).
The error CS0111 of the same clash (the derived class overrides the hook and the generated code declares
its own `OnInit`) is a question up to 2.2.1 as well (GN4), not a defect of the universal rule.

**Known issues** (`KnownIssues.cs`) are recognised by mechanism and place: where one diagnostic code stands
for several mechanisms, the layout shape of the class decides (GN2's CS0114 only in the explicit layouts,
GN6's CS0108/CS0109 only over a member named `Init` or an `Init(in ...)`/`Init<T>` overload). A key whose
violations carry different labels makes one finding per label, so a known issue never claims the
signatures of a new mechanism with the same code.

**Minimization** keeps a repro valid C#: a reduction that gains an error without the generator (an
override whose virtual base was cut away, say) is rejected.

## Cross-version rules

`run.sh` builds the stand against other trees and runs them with `--observe`: the same programs (the
catalog and the same seeds), digested into `xobs/<label>.json` next to the report. The run on the tree
under test then compares program by program (by model hash):

- **Migration pairs** (`R-MIGRATION-ORDER`, a defect from 2.2.2, claim `upgrade-order-222`, a question
  on 2.2.0/2.2.1; GN7). Against the newest
  tree of the 2.1 line (`Stand/out/src/v2.1.*`, extracted with `git archive` if missing, or
  `--reference-src`): a program in the 2.1 layout (the derived class lists `IInitSystem` and calls
  `base.Init` from `OnInit`) that works there, upgraded as "Upgrading from 2.1" says (delete the line),
  must show every base `Init`/`OnInit`/hook the same derived fields and run it as often. "The order
  stays the same, the base Init runs first."
- **Lost compiler signal** (`R-LOST-SIGNAL`, a regression; GN8 for a hand-written override without
  `base.Init`, GN1 for the 2.1 layout without it). The other tree rejected the very class that now
  silently drops the base call (or a class between the run's leaf and the null fields), this tree builds
  the program and fields stay null. A KECS warning there (2.2.2: KECS013) is a signal: nothing is lost.
  A miss the documents describe (KECS013 over a base in another assembly with only private injected
  fields) is `documented`.
- **Fields null here, filled there** (`R-REGRESSION-NULL`, a regression) when no promise is broken.

With `--baseline LABEL` the baseline tree (`--baseline-src`, `Stand/out/src/LABEL/KenseiECS` or the
working copy for `wt`) is observed as well, so the regression rules also compare with it.

## Signatures and the baseline

A finding groups violations by rule and key (`GEN-xxxxxx`); its **signatures** add the place, the
contract shape of the class (`SIG-xxxxxx`, "mechanism + place"), with catalog and fuzz hits each.
report.json also maps every program to its signatures (`programs`). With `--baseline LABEL` the report
of the baseline (`Stand/out/reports/generator/LABEL-<cfg>/report.json`) is compared:

- new defect or regression signatures, also inside a known issue that was found before;
- hit spikes (at least twice the baseline and 5 more) when the program sets are the same;
- programs that gained a defect or regression signature;
- signatures gone, and new question signatures.

So a new bug does not hide in GN1–GN8: the known issues match by place, and the CS0108/CS0114 of
`rb-init-new-modifier` in layouts GN2 and GN6 do not cover make a finding of their own.

## Self-test

`run.sh --selftest [--src DIR] [--debug] [--only a,b] [--keep]` applies every patch of
`Stand/generator/mutants/` to a temporary copy of the tree (`Stand/out/selftest/generator/`; the tree
itself is never changed), runs the module on an unmutated copy and on each mutant with the same
programs, and compares the reports by signature and by program. Report:
`Stand/out/reports/generator/selftest-<flavor>-<cfg>/selftest.md` and `.json`; exit code 0 when every
bug mutant is killed and every fix mutant accepted.

A bug mutant counts as **killed** only with a new defect or regression signature of a rule its `Expect:`
names, outside the known issues the unmutated copy already shows, whose repro was verified (the clean
form compiled again and gave the recorded output). ABSORBED (every new signature inside a known issue of
the base), KILLED-OTHER-RULE, KILLED-NO-REPRO and SURVIVED are failures.

| mutant | kind | what it does | caught by |
|---|---|---|---|
| `nov-gen-no-kecs012` | bug | KECS012 is not reported, the base `Init` runs twice | R-ONINIT-TWICE, R-BASE-INIT-TWICE |
| `nov-gen-exc-dropped` | bug | `[Exc]` is not emitted, the filter gets Inc/Any only | R-WRONG-INJECTION |
| `nov-gen-shared-key-ignored` | bug | `[Shared("key")]` is injected with `shared.Get<T>()` | R-WRONG-INJECTION, R-RUNTIME-EXCEPTION |
| `rb-kecs006-overload` | bug | rollback of the 2.2.0 KECS006 for an `Init` overload in between | R-FIELDS-NULL, R-BASE-INIT-SKIPPED |
| `rb-init-new-modifier` | bug | rollback of the 2.2.0 `new` on a hiding `Init` | R-GEN-WARNING (outside GN2/GN6) |
| `diag-text-kecs006-sealed-reason` | bug | KECS006 over a sealed base `Init` says "is not virtual" | R-KECS-TEXT |
| `diag-args-kecs008-field-type` | bug | KECS008 names the field's type, not the type that is no component | R-KECS-TEXT |
| `uninjected-group4-field` | bug | a `[Group]` of four types is skipped without a diagnostic | R-FIELDS-NULL, R-FIELD-DROPPED, R-GEN-MISSING |
| `receiver-first-attribute-of-list` | bug | the receiver reads the first attribute of a list only (`[System.NonSerialized, Pool]`) | R-GEN-MISSING, R-FIELDS-NULL |
| `receiver-first-list-only` | bug | the receiver reads the first attribute list only (`[System.NonSerialized] [Pool]`) | R-GEN-MISSING, R-FIELDS-NULL |
| `shared-type-flat-name` | bug | a `[Shared]` type is written as namespace + simple name (nested and generic types break) | R-GEN-ERROR |
| `fix-nf2-key-escape` | fix | emulated correction of NF2: NF2 must go, nothing new may appear | — |
| `fix-nf2-key-unicode-escape-222` | fix | 2.2.2 fixed NF2 itself: another correct form of the key literal (`\uXXXX` escapes) must give nothing new | — |

`nov-gen-exc-dropped`, `nov-gen-shared-key-ignored`, `rb-init-new-modifier`, `diag-text-kecs006-sealed-reason` and
`uninjected-group4-field` change code that 2.2.2 rewrote: they stop at 2.2.1 (`Until: 2.2.1`), and a `-222` twin
(`Since: 2.2.2`) makes the same change in the final 2.2.2 generator; `fix-nf2-key-escape` stops at 2.2.1 too, where
`fix-nf2-key-unicode-escape-222` takes over.

A patch is a unified diff against the repository root with a header before the first `diff`/`---`
line: `Mutant:`, `Kind: bug|fix`, `Expect:` (rules a bug mutant should add), `Removes:` (known issues a
fix mutant removes), `Why:`, optionally `Since:` / `Until:` (the contract versions of the trees the patch
is for) and `Version-note:`. `git apply` and `patch -p1` accept the same files. The self-test applies
the hunks by exact context. A mutant outside its version range is n/a, listed with the reason; within it,
a patch that no longer matches the tree is not-applicable, a failure. The copies are fresh files and the
builds are labelled by the tree (`st-<flavor>-<mutant>`).

## Output

`Stand/out/reports/generator/<flavor>-<rel|dbg>/` (or `--report-dir`):

- `report.md`, `report.json` (format 2: `findings`, `regressions`, `questions`, `known` with a
  `status` per known issue, `claims`, `baseline`, `crossVersion`, `programs`, `layouts`, ...);
- `repro/<id>.cs` (the clean program with a header and the expected `ReproMain.Run()` output, which
  also names wrong injections), `<id>.model.json` (replayable with `--show FILE` or `--replay`),
  `<id>.generated.cs`, `<id>.observed.txt`;
- `xobs/` — digests of the other trees and their build logs.

## Extending

- A new documented promise: a claim in `Claims.cs` (document, sentence, since), the rule in
  `Oracle.cs` with `Claim = "<id>"`, and, for layouts, the expectation in `Contract.cs`.
- A new layout: `Catalog.cs` (the catalog runs in every mode); a known issue: `KnownIssues.cs` and an
  entry in `Stand/summary/catalog.json` with `{"module": "generator", "kind": "known", "key": ...}`.
- A new mutant: a patch in `mutants/` with the header above.

## Limitations

Programs come from the model (`Model.cs`): no properties with injection, no other generators in the
same compilation, no incremental IDE runs and no real Unity pipeline (asmdef, Bee); the driver is
Roslyn 4.8 or, with `--roslyn 3.8.0`, the generation Unity 2021.3 uses. The runtime check is
`SystemsRunner.Init` on a fresh `World`; `Run` and `Destroy` are not driven. Severity is an estimate.
