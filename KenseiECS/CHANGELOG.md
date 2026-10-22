# Changelog

All notable changes to this package are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), versions follow [Semantic Versioning](https://semver.org/).

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
