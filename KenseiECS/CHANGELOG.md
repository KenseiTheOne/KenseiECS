# Changelog

All notable changes to this package are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), versions follow [Semantic Versioning](https://semver.org/).

## [2.0.0] - Unreleased

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

## [1.0.0] - 2026-03-28

Initial release.
