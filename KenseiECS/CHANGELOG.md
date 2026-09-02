# Changelog

All notable changes to this package are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), versions follow [Semantic Versioning](https://semver.org/).

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
