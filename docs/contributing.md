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
docs/                 This documentation site (VitePress), incl. architecture, migration from LeoEcsLite, FAQ
├── snippets/         Compiled C# projects the site imports its code samples from
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

`npm run dev` serves the site with hot reload; `npm run build` produces the static site and fails on dead links, so run it before submitting a change to the docs. `docs/README.md` is a GitHub-only index and is excluded from the site.

[Benchmarks](./benchmarks.md) and the [changelog](./changelog.md) are not copies: their pages include `BENCHMARKS.md` and `KenseiECS/CHANGELOG.md` from the repository, so edit those files.

## Code samples are compiled

Every C# sample in the guide, concepts, guides and Unity pages that does not need `UnityEngine` is compiled, and so are the C# blocks of `README.md` and `KenseiECS/README.md`. The reference pages (architecture, FAQ, migration from LeoEcsLite) keep short inline fragments for illustration; those are not built. The code lives in projects under `docs/snippets/` and pages import it by region, so a sample that stops compiling against the current API breaks the build instead of silently going stale.

- Two projects: `docs/snippets/Concepts/` for the guide/ and concepts/ pages, `docs/snippets/Guides/` for the guides/ pages and the compilable Unity samples (Unity pages whose code does not need `UnityEngine`). Each page gets its own file, `docs/snippets/<Project>/<PageName>.cs`, with its own namespace `Docs.Snippets.<PageName>` so stub types of different pages do not clash.
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

Samples that need `UnityEngine` (a `MonoBehaviour`, `EcsBootstrap`, `EcsEntityView`) cannot compile here; they stay inline in the page as fenced `csharp` blocks. Check every member they use against `KenseiECS/Unity/` by hand.

The root `README.md` and the UPM package `KenseiECS/README.md` follow the same rule: every C# block in them must be a verbatim copy (common indentation removed) of a `// #region` under `docs/snippets/`. `npm run check:readme` in `docs/` verifies this, and CI runs it.
