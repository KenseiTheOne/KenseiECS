# Contributing

How the repository is laid out, how to run the tests, and how to work on this documentation site.

## Repository layout

```
KenseiECS/            Unity package (com.kensei.ecs)
├── Core/             Entity, World, ComponentPool, Filter, Group, CommandBuffer, Serialization, ...
├── Systems/          ISystem, SystemsRunner
├── Unity/            EcsBootstrap, EcsEntityView, EcsComponentProvider, providers
├── DevTools/         EcsProfiler, WorldDebugView (KENSEI_DEBUG)
├── Editor/           World Inspector, Systems window, Profiler window, Debug Mode toggle
├── Plugins/          KenseiECS.Generators.dll (Roslyn source generator)
├── Samples~/         BasicGame sample
├── package.json, KenseiECS.asmdef, CHANGELOG.md
KenseiECS.NET/        .NET project (netstandard2.1 + net8.0) compiling the package sources
KenseiECS.Generators/ Source generator project (the DLL above is built from it)
KenseiECS.Tests/      NUnit tests (compile Core + Systems directly)
KenseiECS.Generators.Tests/  Generator tests (compile snippets through the generator)
Benchmark/            BenchmarkDotNet suite vs LeoEcsLite / Arch
Example/              Console game using the framework
Demo/                 "Horde", the live demo (see below)
├── Game/             The game: components, 22 systems, HordeGame facade (net8.0)
├── Headless/         Plays the game without a screen; run in CI
├── Web/              Browser host: .NET WebAssembly + canvas renderer (net10.0)
docs/                 This documentation site (VitePress), incl. architecture, migration from LeoEcsLite, FAQ
├── snippets/         Compiled C# projects the site imports its code samples from
├── api/              API reference: index.md is hand-written, the rest is generated (gitignored)
├── scripts/          check-readme.mjs, gen-api.mjs
├── docfx.json        DocFX configuration for the API reference
.config/dotnet-tools.json  Local .NET tools (DocFX, pinned)
.github/workflows/    ci.yml (every push/PR), pages.yml (docs site + demo)
BENCHMARKS.md         Benchmark results and analysis
```

The internals behind each folder are described in [Architecture](./architecture.md).

## Tests

```
dotnet test KenseiECS.Tests -c Release
dotnet test KenseiECS.Tests -c Release -p:KenseiDebug=true
```

The `KenseiDebug` flag builds with `KENSEI_DEBUG` and additionally covers the debug validation layer (see [Release vs. KENSEI_DEBUG](./guides/debug-mode.md)). Both configurations run in CI on every push.

::: tip Generator tests
The source generator has its own test project: `dotnet test KenseiECS.Generators.Tests -c Release`. CI runs it too.
:::

## Running the docs locally

The site lives in `docs/` and is built with [VitePress](https://vitepress.dev) (Node.js 18 or newer):

```
cd docs
npm ci
npm run dev
```

`npm run dev` serves the site with hot reload; `npm run build` produces the static site and fails on dead links, so run it before submitting a change to the docs. Without `npm run gen:api` (see [API reference](#api-reference)) the site builds with only the API overview page; run it first to build and link-check the full reference the way CI does. `docs/README.md` is a GitHub-only index and is excluded from the site.

[Benchmarks](./benchmarks.md) and the [changelog](./changelog.md) are not copies: their pages include `BENCHMARKS.md` and `KenseiECS/CHANGELOG.md` from the repository, so edit those files.

## API reference

The [API reference](./api/index.md) is generated from the `///` XML comments of the package sources by [DocFX](https://dotnet.github.io/docfx/), pinned as a local .NET tool in `.config/dotnet-tools.json`. From `docs/` (needs the .NET SDK, 8 or newer):

```
npm run gen:api
```

`scripts/gen-api.mjs` copies `KenseiECS/**/*.cs` (without `Editor/` and `Samples~/`) to `.vitepress/cache/api-src`, runs `dotnet tool restore` and `dotnet docfx metadata docfx.json`, then turns DocFX's Markdown into pages VitePress can compile and writes `docs/api/<Type>.md` plus `docs/api/sidebar.json`, which `.vitepress/config.mts` reads for the `/api/` sidebar. Run it through `npm run gen:api`: `docfx metadata` alone has no sources to read.

- Only `docs/api/index.md` is committed; the generated pages are gitignored and rebuilt by CI and the Pages workflow on every run.
- The reference shows the release .NET API: neither `UNITY_*` nor `KENSEI_DEBUG` is defined, so the Unity layer and debug-only types (`EcsProfiler`) are not in it, and neither are `internal` types.
- Write doc comments as valid XML: escape `<`, `>` and `&` in prose (`Get&lt;T&gt;()`, `i &lt; n`) and put examples in `<code>`. The script repairs the common cases while copying, but the compiler drops a malformed comment from the XML documentation (warning CS1570, an error in this repository) and from IntelliSense. Every public type and member needs a doc comment: a missing one (CS1591) is an error too.

## Running the demo locally

The [live demo](./guide/demo.md) is a normal .NET solution folder. The game and the headless runner need the .NET 8 SDK or newer; the browser host needs the .NET 10 SDK (no `wasm-tools` workload).

Play it headless — a bot circles and always takes the first upgrade; the exit code is 0 unless something threw:

```
dotnet run --project Demo/Headless -c Release -- --frames 3000
dotnet run --project Demo/Headless -c Release -p:KenseiDebug=true -- --stress 20 --god --frames 3000
```

Other options: `--seed N`, `--toggle` (switches every system and phase off for 100 frames in turn), `--json`. With `KenseiDebug` the output includes per-system timings.

Build and serve the browser version:

```
dotnet publish Demo/Web -c Release -o demo-out
npx serve demo-out/wwwroot        # or: python3 -m http.server -d demo-out/wwwroot
```

`KenseiDebug` defaults to `true` for `Demo/Web`, so the overlay shows per-system timings; pass `-p:KenseiDebug=false` for a release build of the library. The page must be served over HTTP — opening `index.html` from disk does not load the WebAssembly runtime.

To see the demo inside the docs site the way GitHub Pages serves it, build the site, copy the publish output to `dist/demo/` and preview:

```
cd docs
npm run build
cp -R ../demo-out/wwwroot .vitepress/dist/demo
npm run preview                    # http://localhost:4173/KenseiECS/guide/demo
```

The Pages workflow does the same on every push to `main` that touches `docs/`, `Demo/` or the library.

## Code samples are compiled

Every C# sample in the guide, concepts, guides and Unity pages that does not need `UnityEngine` is compiled, and so are the C# blocks of `README.md` and `KenseiECS/README.md`. The reference pages are compiled too: `docs/snippets/Reference/` holds the FAQ and migration samples (the LeoEcsLite side against the `Leopotam.EcsLite` 1.0.1 NuGet repack, which is unofficial but has the same API as the official repository, in its own namespaces) and the architecture excerpts, which compile against stand-ins that mirror the private fields of `World` and `Filter`. The code lives in projects under `docs/snippets/` and pages import it by region, so a sample that stops compiling against the current API breaks the build instead of silently going stale.

- Three projects: `docs/snippets/Concepts/` for the guide/ and concepts/ pages, `docs/snippets/Guides/` for the guides/ pages and the compilable Unity samples (Unity pages whose code does not need `UnityEngine`), `docs/snippets/Reference/` for architecture, FAQ and the LeoEcsLite migration guide. Each page gets its own file, `docs/snippets/<Project>/<PageName>.cs`, with its own namespace `Docs.Snippets.<PageName>` so stub types of different pages do not clash.
- `docs/snippets/Directory.Build.props` is shared by all snippet projects: it targets `net8.0` with C# 9 (`LangVersion 9.0`), references `KenseiECS.NET/KenseiECS.csproj` and the packaged source generator `KenseiECS/Plugins/KenseiECS.Generators.dll`, and defines `KENSEI_DEBUG` when built with `-p:KenseiDebug=true`.
- The snippet projects are part of `KenseiECS.sln`, so building the solution builds them.

Mark the part of a file the page shows with a named region:

```csharp
// #region create-entity
var world = new World();
var entity = world.CreateEntity(new Position { X = 1, Y = 2 });
// #endregion create-entity
```

and import it in the page with a line of its own:

```md
<<< @/snippets/Concepts/Entities.cs#create-entity{csharp}
```

`@` is the `docs/` folder. The region body is shown as written, with its common indentation removed. Statement fragments go inside a method of a class; keep scaffolding the reader does not need (stub component types, setup) outside the region. Use `Console.WriteLine`, not `Debug.Log`, in compiled samples, and guard code that only exists in debug builds (for example `EcsProfiler`) with `#if KENSEI_DEBUG`.

Build a snippet project in both configurations before submitting:

```
dotnet build docs/snippets/Concepts -c Release
dotnet build docs/snippets/Concepts -c Release -p:KenseiDebug=true
```

Samples that need `UnityEngine` (a `MonoBehaviour`, `EcsBootstrap`, `EcsEntityView`) cannot compile here; they stay inline in the page as fenced `csharp` blocks. Check every member they use against `KenseiECS/Unity/` by hand. Exception: the Reference project compiles its few Unity samples against stubs in `docs/snippets/Reference/Stubs.cs` that mirror `KenseiECS/Unity` signatures; keep those in sync. The architecture excerpts in `docs/snippets/Reference/Architecture.cs` name their source method above each region: when you change that method in `World.cs` or `Filter.cs`, update the excerpt too, since the stand-ins only prove it compiles.

The root `README.md` and the UPM package `KenseiECS/README.md` follow the same rule: every C# block in them must be a verbatim copy (common indentation removed) of a `// #region` under `docs/snippets/`. `npm run check:readme` in `docs/` verifies this, and CI runs it.
