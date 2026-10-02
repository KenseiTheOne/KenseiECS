# API Reference

Every public type of the KenseiECS runtime, generated from the `///` comments in the source. For how the pieces fit together, start with the [guide](../guide/introduction.md) and [core concepts](../concepts/entities.md); come here for exact signatures.

## What is covered

- The **.NET build** of the package: everything under `KenseiECS/Core` and `KenseiECS/Systems`, the same API in Unity and in plain .NET.
- The **release** surface. Types and members compiled only with `KENSEI_DEBUG`, such as `EcsProfiler`, are left out; see [Release vs. KENSEI_DEBUG](../guides/debug-mode.md).
- Not covered: the Unity layer (`EcsBootstrap`, `EcsEntityView`, providers — see the [Unity](../unity/bootstrap.md) section), the editor windows and internal types.

Pick a type in the sidebar. Classes, structs and interfaces are grouped separately; generic types appear with their type parameters, e.g. `ComponentPool<T>`, and arity overloads such as `Inc<T1, T2>` get a page each.

## Regenerating

The pages are generated, not committed. From `docs/` (needs the .NET SDK):

```
npm run gen:api
```

This runs [DocFX](https://dotnet.github.io/docfx/) (pinned as a local tool in `.config/dotnet-tools.json`) over the package sources and writes the Markdown pages and `api/sidebar.json`. Only this page is hand-written.
