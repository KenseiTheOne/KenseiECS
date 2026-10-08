# Release vs. KENSEI_DEBUG

KenseiECS has two builds of the same code. Release keeps the hot paths free of checks; `KENSEI_DEBUG` compiles in validation that turns misuse into exceptions.

| Situation | Release | KENSEI_DEBUG |
|---|---|---|
| `Add` of a component the entity already has | throws | throws |
| `Add`/`Get`/`Has`/`Remove` with a dead or stale `Entity` | undefined (may corrupt another entity) | throws |
| Pool `Add(int)` on a dead slot | undefined | throws |
| Pool `Get(int)` without the component | reads garbage | throws |
| `Remove` of a missing component | no-op | no-op |
| `DestroyEntity` of a dead entity | no-op | no-op |
| `CopyEntity` of a dead entity | returns `Entity.Null` | throws |
| `GetEntity` on a dead slot | dead handle | dead handle |
| Removing an unvisited entity from an iterated filter (would double-visit) | double visit | throws |
| Runner: `Run` before `Init`, `Add` after `Init`, unknown name | silent | throws |
| Nested runner with a different World / SharedData | silently ignored | throws |

Validation code is not compiled in release; the cost is zero.

<<< @/snippets/Guides/DebugMode.cs#stale{csharp}

::: tip
Develop and run tests with `KENSEI_DEBUG`; a "may corrupt another entity" bug in release shows up there as an exception at the faulty call. Safe patterns for removing entities while iterating are covered in [Filters](../concepts/filters.md) and [CommandBuffer](./command-buffer.md).
:::

## Enabling it

- **Unity:** toggle via **KenseiECS -> Debug Mode** (sets `KENSEI_DEBUG` for all build targets).
- **.NET / tests:** add the define to your test project. When building `KenseiECS.NET/KenseiECS.csproj` from source, `-p:KenseiDebug=true` defines it.

## Debug-only features

Besides the checks, `KENSEI_DEBUG` enables the [debug tooling](../unity/debug-tools.md): `EcsProfiler`, per-system timings in the runner, and entity names. Code that calls these must compile in release too. `SetName` is an ordinary method that does nothing without the define, so it is safe in any expression; `EcsProfiler` exists only under `KENSEI_DEBUG` and still needs the `#if`:

<<< @/snippets/Guides/DebugMode.cs#debug-only{csharp}
