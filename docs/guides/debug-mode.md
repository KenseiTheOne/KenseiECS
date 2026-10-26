# Release vs. KENSEI_DEBUG

KenseiECS has two builds of the same code. Release keeps the hot paths free of checks; `KENSEI_DEBUG` compiles in validation that turns misuse into exceptions.

| Situation | Release | KENSEI_DEBUG |
|---|---|---|
| `Add` of a component the entity already has | throws | throws |
| `Add`/`Get`/`Has`/`Remove`, `AddEvent`, `Subscribe`/`Unsubscribe` with a dead or stale `Entity` | undefined (may corrupt another entity) | throws, also while another entity is being destroyed; `Add`/`Get`/`Has`/`Remove` and `AddEvent` accept the entity whose `DestroyEntity` is running, for its listeners |
| Pool `Add(int)` on a dead slot | undefined | throws, except for the slot being destroyed |
| `Get` (world or pool) or pool `Modify` of a component the entity does not have | `IndexOutOfRangeException` on Mono and .NET; on IL2CPP, which skips bounds checks there, a read outside the pool's arrays, and a write through the `ref` corrupts memory | throws |
| `GetComponentTypes` / `GetComponentCount` of a dead entity | reads the slot: empty while it is free, another entity's components once it is reused | throws |
| A listener re-adds a component to the entity being destroyed on every removal pass | `DestroyEntity` never returns | throws after 1,000 passes |
| `Remove` of a missing component | no-op | no-op |
| `DestroyEntity` of a dead entity | no-op | no-op |
| `CopyEntity` of a dead entity | returns `Entity.Null` | throws |
| `GetEntity` on a dead slot | dead handle | dead handle |
| Removing an unvisited entity from an iterated filter (would double-visit) | double visit | throws |
| Runner: `Run` before `Init` | not checked: the systems run without their `Init` | throws |
| Runner: `Add` after `Init` | not checked: `Init` is not called for the system | throws |
| Runner: unknown name in `SetActive` / `IsActive` / `GetRunner` | ignored: does nothing, returns `false`, returns `null` | throws |
| Runner: a name registered twice in one runner | not checked: lookups by the name reach only one of the two | throws |
| Runner: a runner that is already registered added again (any slot, any parent), or added to itself or to a runner nested in it | throws | throws |
| `EcsBootstrap`: `Configure` registers something in the update runner under `"fixed"` or `"late"` | not checked: the name reaches two registrations | `Awake` throws right after `Configure` |
| Runner: `SetActive` / `IsActive` on a system without `Run` (Init/Destroy only) | does nothing / returns `false` | throws |
| Nested runner with a different World / SharedData | silently ignored | throws |
| `CreateEntity` / `Pool<T>` after `World.Destroy` | `NullReferenceException` | `ObjectDisposedException` |
| `World.Clear` / `ReserveEntities` / any `Reserve` (world, pool, filter) / `Filter` after `World.Destroy` | `ObjectDisposedException` | `ObjectDisposedException` |
| A reserved storage array grows past its reserve (`ReserveEntities`, `pool.Reserve`, `filter.Reserve`) | not checked | counted in `GrowthsAfterReserve`, the first one kept in `FirstGrowthAfterReserve`, logged by `EcsProfiler` while it records that world; growth of a pool or filter nothing reserved is not counted, and `EcsProfiler` logs it marked `(not reserved)` |

Validation code is not compiled in release; the cost is zero.

<<< @/snippets/Guides/DebugMode.cs#stale{csharp}

::: tip
Develop and run tests with `KENSEI_DEBUG`; a "may corrupt another entity" bug in release shows up there as an exception at the faulty call. Safe patterns for removing entities while iterating are covered in [Filters](../concepts/filters.md) and [CommandBuffer](./command-buffer.md).
:::

## Enabling it

- **Unity:** toggle via **KenseiECS -> Debug Mode**. It sets `KENSEI_DEBUG` in the active Build Profile (Unity 6) or, without one, in the Player Settings of the active build target only. A Build Profile is a shared asset under version control, so the toggle logs each write to it. Turning the mode off does not touch other targets; a warning lists those whose Player Settings still have the define. A non-development player build with the define fails unless `KENSEI_ALLOW_DEBUG_RELEASE` is set the same way. The error names where the define comes from: the profile or the Player Settings of the target (checked in the preprocess step), or `BuildPlayerOptions.extraScriptingDefines`, Additional Compiler Arguments or `Assets/csc.rsp` (checked right after the player scripts compile). Content builds (Addressables, AssetBundles) are not checked. The checks run inside the build, after Unity has started calling preprocessors, so build scripts should check the defines before `BuildPlayer`; see [Release build guard](../unity/debug-tools.md#release-build-guard).
- **.NET / tests:** the define has to reach the project that compiles the KenseiECS sources. With a project reference to `KenseiECS.NET/KenseiECS.csproj`, build with `-p:KenseiDebug=true`: it is a global property, so the referenced project gets it too. A project that compiles the sources itself, as `KenseiECS.Tests` does, adds `KENSEI_DEBUG` to its own `DefineConstants`; defining it in a test project that only references a built `KenseiECS.dll` changes nothing.

## Debug-only features

Besides the checks, `KENSEI_DEBUG` enables the [debug tooling](../unity/debug-tools.md): `EcsProfiler`, per-system timings in the runner, entity names, `World.GrowthsAfterReserve` and `World.FirstGrowthAfterReserve`. Code that uses them must compile in release too:

- `SetName`, `GetName`, `GrowthsAfterReserve` and `FirstGrowthAfterReserve` exist in every build. Without the define they do nothing, return `null`, 0 and an empty `StorageGrowth`, so they are safe in any expression (`SetName` still evaluates its arguments).
- `EcsProfiler` with `ProfileEvent` and `ProfileEventType`, the runner timings (`SystemInfo.LastRunMs` and `PeakRunMs`, `SystemsRunner.ResetTimings`) and the untyped `GetRaw`/`SetRaw` of a pool exist only under `KENSEI_DEBUG`, so code that uses them needs the `#if`:

<<< @/snippets/Guides/DebugMode.cs#debug-only{csharp}
