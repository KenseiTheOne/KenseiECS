# Changelog

All notable changes to this package are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), versions follow [Semantic Versioning](https://semver.org/).

## [2.2.0] - 2026-10-22

### Added

- `EventBuffer<T>.Prewarm(lists, capacity)` fills the static list pool up front: the next `lists` lists the pool hands out get `Capacity` of at least `capacity`. Free lists already that large are used first, then the largest remaining ones are grown (at most `lists` of them, so a pool left large by an earlier peak is not inflated), and new lists are created only when fewer than `lists` are free; the prepared lists are moved to the top of the pool. A repeated call with the same arguments does nothing. The pool is static per `T` and shared by every world in the process; `World.Warmup` still does not fill it, and neither does `Prewarm` size the world (use `World.Reserve`).
- `World.Reserve(entities, components = 0)` grows storage ahead of time in one call: entity storage (generations, masks, free-slot list) and the sparse side of every pool and filter for `entities`, and the dense arrays of every pool (entities, data, change versions) and filter for `components`. Both apply to existing pools and filters and to those created later in the world. After `Reserve(n, n)`, creating and destroying n entities with components that enter filters allocates nothing; creating a pool or filter still allocates the object (and, in a new 64-type block, a mask word). `components` costs that many slots of every component type, singletons included; `ComponentPool<T>.Reserve(count)` (dense, data, change-version and sparse arrays) and `Filter.Reserve(count)` (dense array, safe during iteration) size one pool or filter. Nothing shrinks. On a destroyed world `Reserve` throws `ObjectDisposedException`.
- `World.GrowthsAfterReserve`: under `KENSEI_DEBUG` it counts every growth past what `Reserve` reserved (entity slots, free-slot list, pool sparse arrays and filter pages past `entities`; pool and filter dense arrays past `components`; a dimension left at 0 is not watched), and while `EcsProfiler` records the world each such growth is a `ProfileEventType.StorageGrown` event naming the array and its sizes (Profiler window: **Grow** filter). Its `EntityIndex` is -1. The window's search (`ProfileEvent.MatchesSearch`) treats a query of digits as an entity index and matches it only against events that have an entity, so typing `1` no longer lists every growth ("-1", "64 -> 128"); growth events are found by text (`dense`, a type name) or the **Grow** filter. Always 0 without the define; release builds check nothing.
- `Listeners<T>.Invoke(call, onError)` and `Invoke<TArg>(arg, call, onError)` call the listeners from a snapshot of the list in subscription order (first subscribed, first called; hand-written loops over `Values` usually went from the end). A listener removed from the list during the dispatch before its turn is skipped (a view destroyed with `Object.Destroy` stays subscribed until its `OnDestroy` at the end of the frame, so it is still called); listeners added during the dispatch are not called; each snapshot entry is called at most once. "Still subscribed" uses `Equals` like `Remove`, so a struct listener removed and added again counts as subscribed; a listener added several times is matched by value, not per copy. On an unchanged list the check is one reference comparison per listener; after a change the search widens from the previous listener's position in both directions, so a listener unsubscribing itself or a callback inserting listeners before the current one does not make the rest of the dispatch quadratic. If `onError` itself throws, the dispatch stops and that exception propagates. The snapshot is rented from `ArrayPool<T>.Shared` and cleared and returned in `finally`; nested calls work. Without `onError` an exception propagates; with it the error is reported per listener and the call continues. In the `TArg` overload `onError` is `Action<T, TArg, Exception>` and receives the argument too, so static lambdas for both `call` and `onError` allocate nothing.
- `SystemsRunner.AddPhase(name, runner)` registers a separate phase (same as the now obsolete `Add(runner, name)`); `AddGroup(name, runner)` registers a named inline group that runs inside the parent's `Run`, is reachable through `SetActive(name)`, `IsActive(name)`, `IndexOf(name)` and `GetRunner(name)`, and gives its name to its `ProfilerMarker` and `SystemInfo.Name`.
- `SystemsRunner.IndexOf(ISystem)` (by reference) and `IndexOf(string)` return the registration position used by `GetSystemInfo`/`SetActive(int)`, or -1. They find any registered system, Init/Destroy-only ones included, which `SetActive` cannot toggle (`GetSystemInfo(i).IsRunnable` is false). For a run system, group or phase `IndexOf(string)` resolves an explicit registration name exactly like `SetActive(string)`/`IsActive(string)` and falls back to the first unnamed system with that type name only when no system was registered under the name. Nested runners are not searched.
- `SystemsRunner.SetActive(bool)` toggles a runner through a reference to it: on an inline group (named or not) it switches the group's slot in the parent together with its `IsEnabled`, like the parent's `SetActive(name)`; on a phase or a root runner it sets the runner's own `IsEnabled` (a disabled root's `Run` still advances the tick but runs no systems).
- `SystemsRunner.OneFrame<T>(cleanWhenDisabled)`: with `true` the type is also removed in frames where the runner is skipped, at the slot of a disabled inline group (recursively into its inline groups) or in `Run()` of a disabled phase. Systems and `DelHere` of the skipped runner still do not run. `OneFrame<T>()` keeps the old behaviour. A runner tree without `cleanWhenDisabled` types passes disabled slots at the 2.1 cost.

### Changed

- **Behaviour change:** a `SystemsRunner.Init` that throws now rolls back. `Destroy` is called on every `IDestroySystem` registered before the failing system, in reverse order (a nested runner that fails rolls itself back first), the progress is reset and the original exception is rethrown; if rollback `Destroy` calls throw too, the rollback still completes (also inside nested runners registered before the failing system: each of their systems is destroyed even when another one throws) and an `AggregateException` holds the original exception followed by the rollback errors. The next `Init` starts from the first system. Up to 2.1.x the systems before the failing one were left initialized and the next `Init` resumed with the failing system. `Destroy()` after a failed `Init` does nothing.
- **Behaviour change:** `SetActive(index|name)` on an inline group (named or unnamed) switches both the parent's slot and the group's own `IsEnabled`; up to 2.1.x the unnamed child's `IsEnabled` stayed `true`. `GetSystemInfo` and `IsActive` agree with it. A direct `Run()` of such a group while its slot is off now runs nothing (it only removes `OneFrame` types registered with `cleanWhenDisabled`); in 2.1.x it ran the group's systems.
- Under `KENSEI_DEBUG` `SetActive(int)`, `SetActive(string)` and `IsActive(string)` on a system without `Run` (Init/Destroy only) throw `InvalidOperationException` saying so; `SetActive(int)` used to do nothing, and by name the error claimed that nothing was registered under the name. Release builds still ignore it. Names stay case-sensitive and an unknown name stays silent in release; both are now spelled out in the XML docs and on the runner page.
- `World.Filter` after `World.Destroy` throws `ObjectDisposedException`, and under `KENSEI_DEBUG` so do `CreateEntity` and `Pool<T>`, instead of a `NullReferenceException`.
- Under `KENSEI_DEBUG` registering a name that is already used in the same runner (system, group or phase) throws `InvalidOperationException`; before, the later registration silently replaced the earlier one in the name lookup. `AddPhase`/`AddGroup` throw for a `null` runner or an empty name in every build.

### Deprecated

- `SystemsRunner.Add(SystemsRunner runner, string name)` is a new overload marked `[Obsolete]` (a warning) with the 2.1 behaviour: a named runner passed to `Add` is a separate phase, which next to `AddGroup(name, runner)` reads like a group whose systems then never run. Use `AddPhase(name, runner)` for a phase or `AddGroup(name, runner)` for an inline group. `Add(runner)` without a name, `Add(system, name)` for other systems and a runner passed as an `ISystem` do not warn.

### Generator

- **Behaviour change:** a derived system with injected fields whose base can be chained (a generated base in the same compilation, or a hand-written base with a `public virtual` or `abstract` `Init`) always gets a generated `Init` that overrides the base one and calls `base.Init(world, shared)` first (none for an abstract base `Init`). A `base.Init` written in the class itself is now the new error KECS012 ("generated Init already calls base.Init first; remove the call from OnInit"): the 2.1 layout, where the derived class re-implements `IInitSystem` and calls `base.Init` from `OnInit`, is no longer kept for such classes. To upgrade, delete the `base.Init(...)` line; the base still runs first. The check is syntactic: any `base.Init` member access in the class's own parts counts, whatever overload it binds to (rename a hand-written base helper named `Init`); one inside a nested type does not. A class that lists `IInitSystem` itself over such a base without calling `base.Init` is chained too, so that base `Init` now runs (on 2.1 it was skipped). Over a base `Init` that cannot be overridden (not virtual, sealed, not public, explicit), a class that lists `IInitSystem` itself keeps the 2.1 layout as before.
- Systems with injected fields can derive from a `partial` system with injected fields in the same compilation, or from a base with a `public virtual Init`. The base's generated `Init` becomes `public virtual`, the derived one a `public override` that calls `base.Init`: base fields, base `OnInit`, then derived fields and `OnInit`. Works over several levels, through intermediate classes without fields and with generic bases. A hand-written subclass that overrides `Init` (directly or through hand-written intermediate classes that override it and call `base.Init`) also makes the generated base `Init` virtual, so such a chain compiles instead of failing with CS0506. Systems without inheritance get the same `.g.cs` as before. Where the generated `Init` hides an inherited one, it is declared `new` (no more CS0108/CS0114 in generated code).
- KECS006 is now reported only where inheritance cannot work: a base `Init` that is not virtual, sealed, not public or explicit (unless the class lists `IInitSystem` itself, see above), an intermediate class that hides `Init`, an intermediate hand-written class with another `Init` overload that the generated `base.Init(world, shared)` would bind to instead of the chained base `Init` (for example `Init(World, object)`, `Init(World, SharedData, int x = 0)`, a generic or `params` overload; rename it), or a base from another assembly whose `Init` is not virtual. The message names the reason. A derived class of a base that failed with its own error (for example KECS001) gets no second error.
- `Plugins/KenseiECS.Generators.dll` is rebuilt; the packaged-DLL test covers the inheritance case and KECS012.

### Editor

- **Behaviour change:** the Debug Mode toggle writes `KENSEI_DEBUG` to the active Build Profile's scripting defines on Unity 6 when a profile is active, otherwise to the Player Settings of the current build target only (2.1.x wrote every target). The checkbox shows whether the define is in effect for the active profile or target; turning it off removes it from both the profile and the current target's Player Settings, so a define written to all targets by 2.1.x does not keep the mode on. Other targets are not changed; if their Player Settings still have the define, a warning lists them. Each write to a Build Profile asset (shared, usually under version control) is logged with its name and path.
- **Behaviour change:** a new build guard (`IPreprocessBuildWithReport`) fails a non-development player build whose defines (active profile plus Player Settings of the built target) contain `KENSEI_DEBUG`. The error names where the define comes from (the profile's name and asset path and/or the Player Settings target). Define `KENSEI_ALLOW_DEBUG_RELEASE` to allow it. Defines the guard cannot see (`BuildPlayerOptions.extraScriptingDefines`, `csc.rsp`) are caught by a `#error` in the runtime assembly when a release player is compiled with `KENSEI_DEBUG`; an allow define passed only that way satisfies the `#error` but not the guard. The guard runs early (`callbackOrder` -1000) but does not stop other preprocessors: Unity still calls those ordered after it, so their asset changes can remain after the failed build; build scripts should check the defines before `BuildPlayer`. `BuildPlayer(BuildPlayerWithProfileOptions)` makes the profile active before the build, so its defines are checked (and it stays active afterwards). A profile with its own Player Settings ("Customize player settings") makes Unity read and write that copy while it is active: turning Debug Mode off then edits the profile asset and logs it, and the guard error and the other-targets warning say that the Player Settings are the copy in the profile (the warning also notes that the project's `ProjectSettings.asset` was not checked). With the Dedicated Server subtarget active, the toggle and the guard use the Server define slot on every supported Unity version (2021.3+).

### Documentation

- One-frame guide: prewarming the list pool, the static shared pool, `cleanWhenDisabled` for paused runners.
- Runner page: phase / named group / unnamed group, `AddPhase`/`AddGroup`, `IndexOf`, `IsEnabled` of inline groups, the `Init` rollback contract; architecture, migration guide and bootstrap page updated to match.
- World config and lifecycle pages: `Reserve` with `components`, `GrowthsAfterReserve`, use after `Destroy`. Listener bridge: `Invoke` order, skipping of unsubscribed listeners (and why a view destroyed with `Object.Destroy` is still called in the same dispatch), matching by `Equals`, duplicates and a throwing `onError`. One-frame guide and migration guide: `Prewarm` does not size the world; a warmup that created entities needs `World.Reserve` as well. Runner page: case-sensitive names, silent unknown names in release, `SetActive(bool)`, the obsolete `Add(runner, name)`. Architecture: why `Listeners<T>` lists are not pooled (a copy written back after a remove would share a pooled list between entities).
- Source generator guide: inheritance, the new KECS006 cases, KECS012 and upgrading from 2.1; FAQ, architecture and migration guide updated.
- Debug tools, debug mode, installation and FAQ: where the toggle writes the define and the release build guard; the profile is a shared asset (Unity may leave `m_HasScriptingDefines` changed after off/on), the warning about other targets, profiles with their own Player Settings, and a pre-`BuildPlayer` check for CI (it activates the profile before reading `PlayerSettings`).
- `World.Warmup`/`SystemsRunner.Warmup` XML docs and the package README: typed and filter listeners see Warmup's temporary entity.

## [2.1.1] - 2026-10-22

### Fixed

- A typed listener that removes the same component again from `OnRemoved` (for example by destroying its own entity) corrupted the pool: the outer `Remove` swap-removed a slot that was already gone. The nested `Remove` now continues the notification from the next listener and does the removal, the outer one sees the component gone and stops; every listener gets one `OnRemoved`. Works with change tracking and owning groups.
- `World.Add` and `pool.Add` returned a `ref` to a stale slot when a filter, world or typed listener added or removed the same type elsewhere, or removed the just-added component: writes through it landed on another entity. The dense index is re-read before each typed `OnAdded` and before returning; a component removed before `Add` returns gets no further `OnAdded`, and the `ref` points at a scratch value whose writes are discarded.
- `CopyEntity` shared one `List` between the source and the copy for `EventBuffer<T>` and `Listeners<T>`; after the next OneFrame cleanup the pool could hand that list to two entities. Both now implement `IAutoCopy<T>` and give the copy its own list.
- `ParallelRunner`: after a job threw, the chunk counter jump made `chunk * chunkSize` overflow, so chunks ran again or with a negative start (`Execute(-1024, 0)`). The start index is computed in `long`.
- `world.AddEvent` on a stale handle appended to the new occupant's buffer under `KENSEI_DEBUG`; it now throws like the other entity operations.
- An `IFilterListener.OnEntityRemoved` that added the removed component back left the entity out of the remaining `Inc` filters of that type.
- `World.Warmup` left its temporary entity alive, and world events suppressed, when a listener threw; both are now restored in `finally`.
- `World.Destroy` called twice threw; the second call is a no-op.
- `CommandBuffer.Set` on an existing component and a `CopyEntity` that overwrites a component (one a listener added during the copy) were invisible to change tracking; both now write through `Modify`. `Set` still does not run `AutoReset` on the old value, so writing back a modified copy (`var b = world.Get<EventBuffer<Hit>>(e); b.Add(h); cb.Set(e, b);`) keeps its list. `CopyEntity` runs `AutoReset` on the overwritten value for types with `IAutoCopy`, whose copied value owns its references.

### Performance

- Under `KENSEI_DEBUG`, pool `Add`/`Remove` call `EcsProfiler` only while it is enabled, and the component type name is cached per pool; before, every structural change evaluated `typeof(T).Name`, which allocates on Mono and IL2CPP.
- `Remove` tests the pool's `_autoReset` delegate instead of a `static readonly bool`, which IL2CPP does not fold and checks through class initialization on every call.

### Generator

- New errors for injection that compiled but left fields `null` at runtime or failed inside the generated file: KECS006 (a derived system where its own or the base `Init` would never run; deriving still works when the class lists `IInitSystem` among its bases if the base implements it, and calls `base.Init` from `OnInit` if the base has injected fields), KECS007 (no types in `[Inc]` and `[Any]`), KECS008 (a non-component type in a filter attribute), KECS009 (`readonly` injected field), KECS010 (`record` or `struct` system), KECS011 (system nested in a `record` or `interface`).
- KECS002 also covers an explicit `void IInitSystem.Init(...)`; KECS003 also covers `[Shared]` on a value type, which failed with CS0452 in the generated file.
- No false KECS003 on nullable-annotated fields (`Filter?`, `ComponentPool<T>?`) in a `#nullable enable` context.
- `Plugins/KenseiECS.Generators.dll` is rebuilt from these sources. The test that loads it now uses a separate load context (it used to get the generator project's own assembly) and checks a 2.1.1 diagnostic, so a stale DLL fails the tests.

### Editor

- Systems window repaints at most about 10 times per second instead of on every editor update.
- Profiler window caches the event snapshot (refreshed at most twice a second while recording, on enable/disable, after Clear and on entering Play Mode) instead of copying the buffer on every `OnGUI`, and keeps showing recorded events after the world is destroyed or recording stops.
- World Inspector counts components only for entities on the shown page (or for search matching), disables Next when no further match exists, and clamps the page when entities die.

### Documentation

- Generator diagnostics table, FAQ and migration guide list KECS006-KECS011 and the new KECS002/KECS003 cases.
- `ref` lifetime from `Get`/`Add`: any `Add` or `Remove` of the type on any entity invalidates it, and in an owning group any of the group's types does (components, groups, FAQ, architecture, XML docs).
- `EventBuffer<T>` allocates nothing only once its static, process-wide list pool has reached the peak; `World.Warmup` does not fill it.
- A disabled runner (or an unnamed child disabled through the parent's `SetActive(int, bool)`) does not clean its OneFrame/DelHere types; the unnamed child's own `IsEnabled` stays true.
- `World.Warmup` is visible to pool and filter listeners subscribed in `Init`.
- `EcsEntityView`/`SetName` without `KENSEI_DEBUG`: the call is an empty method, not compiled out.
- Benchmarks: measured on .NET 8 x64 only (no Mono, IL2CPP or ARM), the .NET build uses `BitOperations` while Unity uses the De Bruijn fallback, no `EventBuffer`/OneFrame scenarios, and "0 B" holds after capacities reach their peak.
- `CommandBuffer.Set` and change tracking pages describe the overwrite; `CopyEntity` overwrite and `AutoReset` are described under `IAutoCopy`.

## [2.1.0] - 2026-10-08

### Added

- Documentation site at https://kenseitheone.github.io/KenseiECS/: getting started, core concepts, feature guides, Unity integration, architecture, FAQ, migration from LeoEcsLite, benchmarks and changelog. `package.json` `documentationUrl` points to it.
- API reference on the site, generated from the XML doc comments of the public .NET API.
- Comparison page: KenseiECS against Arch, Friflo.Engine.ECS, Unity Entities, LeoEcsLite and DefaultEcs, including where the others are the better choice.
- XML doc comments for every public type and member of the .NET API (117 had none: the `Inc`/`Exc`/`Any` filter specs, `Group<T1..T4>` data arrays, enumerators, constructors, system and listener interfaces, attribute properties). A public member without one (CS1591) now fails the build.
- Live demo "Horde": a survivors-like game on KenseiECS running in the browser on .NET WebAssembly, with an ECS inspector (per-system timings, system toggles, filter and pool sizes, stress mode) and touch controls; "Inside the Demo" walks through its systems.

### Changed

- Every C# sample in the documentation that does not need `UnityEngine` (plus the FAQ and migration samples, Unity-related ones against stubs) and the C# blocks of both READMEs is compiled from projects in `docs/snippets/` (the LeoEcsLite side of the migration guide against the `Leopotam.EcsLite` 1.0.1 NuGet repack, which is unofficial but has the same API as the official repository) and checked in CI in release and `KENSEI_DEBUG`.
- The root README is now a short landing page that links to the site; the installation guide covers building from source for .NET.
- `World.SetName` is no longer `[Conditional("KENSEI_DEBUG")]`. Without the define it is an empty method and its arguments are evaluated; before, the compiler dropped the call together with its arguments, so `world.SetName(world.CreateEntity(...), "Player")` created no entity when the define was off - in practice, in release builds and in any test run without it. On .NET the name now depends on how KenseiECS itself is built rather than on the calling assembly, so assemblies compiled against 2.0.x need a rebuild to see names. A name built at the call site (`$"Enemy {i}"`) is now built in release too; wrap such a call in `#if KENSEI_DEBUG` if that allocation matters.

### Fixed

- README Quick Start: wrong call order and a missing `using`; other README samples had duplicate locals, an `EcsProfiler` call not guarded by `#if KENSEI_DEBUG` and a double `CommandBuffer.Add`.
- Migration guide and FAQ samples that did not compile or threw at runtime: a duplicate `Add` in the deferred-changes loop (now `Set` on the KenseiECS side and a `Has`-guarded `Add` on the LeoEcsLite side, each followed by `break`), a doubly declared variable, out-of-scope variables, statements mixed with declarations.
- XML documentation of 21 public types and members (among them `SystemsRunner`, `SharedData`, `CommandBuffer`, `Group`, `EventBuffer<T>`, `WorldSerializer` and the `[Inc]`/`[Pool]`/`[Group]`/`[Shared]` attributes) was dropped from `KenseiECS.xml` as badly formed XML, so IDEs showed no summaries for them. The comments are escaped now, and a badly formed doc comment (CS1570) fails the build.
- Performance tables in the README, introduction and `BENCHMARKS.md` bolded KenseiECS's owning group (5.5 us) as the fastest two-component iteration; Arch (5.4 us) is.

## [2.0.0] - 2026-09-02

### Breaking

- `IComponentPool` replaced by the abstract class `ComponentPoolBase`. `Clear`, `AddDefault` and `CopyTo` are now internal; user code can no longer desynchronize masks and filters by calling them.
- `World.GetEntity(int)` on a dead slot returns the dead entity's own handle (`IsAlive` is false) instead of throwing under `KENSEI_DEBUG`. A slot's generation now changes when the slot is reused, not when it is freed.
- `IWorldEventListener.OnEntityCreated` fires after the first component is added (`CreateEntity`) or after all components are copied (`CopyEntity`). Listeners never observe an entity without components.
- `World.Unsubscribe<T>` keeps the empty `Listeners<T>` component; unsubscribing the last listener no longer auto-destroys the entity. `HasListeners<T>` is false for an empty list.
- `World.Remove<T>` no longer creates the pool for a type that was never added.
- `SystemsRunner.Destroy` runs `IDestroySystem.Destroy` in reverse registration order, is a no-op before `Init`, and resets the runner so `Init` can run again.
- `Listeners<T>` and `WorldListenerExtensions` moved from `Unity/` to `Core/` and no longer depend on Unity.
- The .NET project file moved from `KenseiECS/KenseiECS.csproj` to `KenseiECS.NET/KenseiECS.csproj`; the `KenseiECS/` folder is now a UPM package.

### Added

- `FilterBuilder.Any<T>()`: match entities with at least one of the listed types. `Any` and `Exc` of the same type is rejected; `Any` of an `Inc` type is dropped as redundant.
- `world.Filter<Inc<A, B>, Exc<C>, Any<D, E>>()` static filter specs (`Inc` 1-6 types, `Exc` 1-4, `Any` 2-4, `None`).
- `Filter.IsEmpty`, `First()`, `TryGetFirst`, `Single()`, `Entities` span, `AddListener(IFilterListener)` for enter/leave notifications.
- `ComponentPool<T>.AddListener(IComponentListener<T>)`: `OnAdded` after filters update, `OnRemoved` before AutoReset.
- `CommandBuffer`: deferred `CreateEntity`/`Add`/`Set`/`Remove`/`DestroyEntity` with `PendingEntity` handles, typed payload storage, zero allocations after warmup; commands on dead entities are skipped.
- `World.GetSingleton<T>()`, `GetSingletonEntity<T>()`, `HasSingleton<T>()`.
- `EventBuffer<T>` component and `world.AddEvent(entity, value)` for several events per entity per frame; lists are pooled.
- `KENSEI_DEBUG`: removing an entity from a filter that is being iterated throws when the swap-remove would make the loop visit an entity twice (nested loops tracked per enumerator).
- `KenseiECS.NET` multi-targets `netstandard2.1` and `net8.0`; the .NET build uses `BitOperations`.
- `SystemsRunner.GetSystemInfo(int)`, `SystemCount`, `SetActive(int, bool)`, `IsEnabled`, `World`: introspection for tooling. Every run system gets a `ProfilerMarker` on Unity; `LastRunMs`/`PeakRunMs` and `ResetTimings()` under `KENSEI_DEBUG`.
- `World.FilterCount`/`GetFilter(int)`; `Filter.IncludedTypes`/`ExcludedTypes`/`AnyTypes`, `ToString()`, `DenseCapacity`, `AllocatedBytes`; `ComponentPoolBase.SparseCapacity`, `DenseCapacity`, `ComponentSize`, `AllocatedBytes`.
- `World.SetName(Entity, string)` / `GetName(Entity)` debug entity names (`SetName` compiles out without `KENSEI_DEBUG`).
- Filter sparse arrays are paged (1024 slots per page, allocated on first touch); a filter no longer costs an `int` per entity slot of the world.
- Owning groups: `world.Group<T1, T2>()` (up to four types) keeps the owned pools' dense arrays aligned so members can be iterated through `Data1..DataN` spans with no sparse lookups. A pool belongs to at most one group. `World.GroupCount`/`GetGroup`.
- Change tracking: `pool.TrackChanges()`, `Modify(e)`, `MarkChanged(e)`, `ChangedVersion(e)`, `ChangedSince(e, version)` with `World.ChangeVersion` as the consumer's bookmark.
- `WorldSerializer`: `Save(world, stream)` / `Load(world, stream)` snapshots that keep entity indices and generations (so `Entity` fields inside components stay valid), identify types by name, write unmanaged components bit-for-bit and use `IComponentFormatter<T>` for components with references.
- Source generator (`KenseiECS.Generators`, shipped as `Plugins/KenseiECS.Generators.dll`): `[Inc]`, `[Exc]`, `[Any]`, `[Pool]`, `[Group]`, `[Shared]` on fields of a partial system class generate `Init` and call `partial void OnInit(World, SharedData)`. Diagnostics KECS001-KECS005 for misuse.
- `World.TryGetEntity(int, out Entity)`.
- `ParallelRunner` and `IRangeJob`: struct jobs over an index range (filter `Entities` or group `Data` spans) on fixed worker threads plus the caller, dynamic chunking, no allocations per `Run`, worker exceptions rethrown on the caller.
- Unity: `EcsBootstrap` base MonoBehaviour (World, SharedData, update/fixed/late runners, warmup), `IEcsSystemsProvider`, `EcsComponentProvider<T>` inspector-authored components, `EcsEntityView.Spawn(world)` and `EntityName`, `KenseiECS -> Systems` window with enable toggles and timings, Filters and Pools tabs in the World Inspector, debug names in inspector and profiler, `Samples~/BasicGame`.
- UPM package: `package.json`, `KenseiECS` and `KenseiECS.Editor` assembly definitions, committed `.meta` files. Install via git URL with `?path=/KenseiECS`.
- `ComponentType.TypeOf(int)` and `ComponentType.NameOf(int)` resolve the `typeIndex` passed to world event listeners.
- `World.GetComponentTypes(Entity, List<int>)`, `World.GetComponentCount(Entity)`, `World.GetPool(int)`.
- `ComponentPoolBase.ComponentType`.
- `SystemsRunner.DelHere<T>()` removes all `T` at that point of the pipeline; `SystemsRunner.IsInitialized`, `SystemsRunner.Shared`.
- `SystemsRunner.SetActive`/`IsActive` work on named child runners.
- `Listeners<T>.Count`.
- `IAutoReset<T>` and `IAutoCopy<T>` may be implemented explicitly.
- `KENSEI_DEBUG` validation: pool `Add` on a dead or never-allocated slot, `Run` before `Init`, `Add` after `Init`, unknown names in `SetActive`/`IsActive`/`GetRunner`, `GetComponentTypes` on a dead entity.
- GitHub Actions workflow running the test suite in both configurations.

### Fixed

- An exception thrown by a world event listener or `AutoReset` during `DestroyEntity` left a permanent zombie (dead flag set, components and filter membership intact, slot never freed). The slot is now always released and components are drained in `finally`.
- An exception in `SystemsRunner.Init` marked the runner initialized and skipped the remaining systems forever; `Init` now resumes at the failed system.
- An exception in a system skipped OneFrame cleanup, so events were processed twice; cleanup now runs in `finally`.
- `Warmup` fired world events and profiler records for its temporary entity.
- Removing a world event listener with a lower index from inside a dispatch invoked the current listener twice; the listener list is now copy-on-write.
- A child `SystemsRunner` constructed without `SharedData` got an empty container instead of the parent's.
- The World Inspector and EcsEntityView inspector lost edits made to nested structs and array elements.
- `KenseiECS -> Debug Mode` applied `KENSEI_DEBUG` only to the current build target group.
- `EcsProfiler` kept a static reference to a destroyed world and survived Enter Play Mode without domain reload.
- `Il2CppSetOption` did not apply to `Filter.Enumerator` (nested types do not inherit it), so the hottest loop kept bounds checks under IL2CPP.
- `ComponentPool.Clear` and `World.Clear` were O(entity capacity) per pool; sparse entries are now reset through the dense list.

### Changed

- Per-type filter lists are split into include/exclude/any, so adding or removing a component tests the mask only for filters it can move the entity into.

## [1.0.0] - 2026-03-28

Initial release.
