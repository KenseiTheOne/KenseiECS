# SystemsRunner

`SystemsRunner` holds systems and executes them in registration order. It also owns the cleanup of [one-frame components](../guides/one-frame.md) and can contain other runners.

<<< @/snippets/Concepts/Runner.cs#pipeline{csharp}

- `Add(system, name)` registers a system; the optional name is what `SetActive`/`IsActive` use. Unnamed systems are reported under their type name.
- `OneFrame<T>()` removes every `T` at the end of `Run`; `DelHere<T>()` removes them at that point of the pipeline, so systems registered before it see the components and systems after it do not. See [OneFrame Components](../guides/one-frame.md).
- `SetActive(name, false)` skips a named system during `Run`; `IsActive(name)` reads the flag.
- `Warmup()` runs `Init` and pre-touches the world before gameplay starts. See [World Lifecycle](../guides/world-lifecycle.md).

## Lifecycle contract

- `Init` runs each `IInitSystem` once, in registration order. If one throws, the runner stays uninitialized and the next `Init` resumes with the system that failed. `Add` after `Init` throws under `KENSEI_DEBUG`.
- `Run` executes enabled systems in order, then removes OneFrame components. Cleanup runs even if a system throws (systems after the failing one are skipped that frame). `Run` before `Init` throws under `KENSEI_DEBUG`.
- `Destroy` runs `IDestroySystem` in **reverse** registration order, is a no-op before `Init`, and resets the runner so `Init` can run again (scene reload).

## Nested runners

Update-phase systems live in the root runner. `root.Run()` advances the world tick, runs the root's systems and cleans the root's OneFrame components.

| Child runner | Runs as part of the parent's `Run()` | Driven by |
|---|---|---|
| **Named** (`Add(child, "fixed")`) | no — it is a separate phase (FixedUpdate/LateUpdate) | `GetRunner(name).Run()`: runs the child's systems and cleans the child's OneFrame components, without ticking |
| **Unnamed** (`Add(child)`) | yes — an inline group | the parent |

`Init()`/`Destroy()` cascade from root to all children; a child constructed without `SharedData` inherits the parent's.

The example below is the shape of a MonoBehaviour driving the runners from Unity's `Update` and `FixedUpdate`; [`EcsBootstrap`](../unity/bootstrap.md) does exactly this for you.

<<< @/snippets/Concepts/Runner.cs#nested{csharp}

`SetActive(name, false)` on a named child pauses the whole phase: its `Run()` becomes a no-op.

::: warning Release vs. KENSEI_DEBUG
Under `KENSEI_DEBUG`, adding, initializing or running a child constructed with a different `World` (or an explicitly passed different `SharedData`) throws instead of silently ignoring it, and unknown names passed to `SetActive`, `IsActive` or `GetRunner` throw. In release, an unknown name is ignored by `SetActive`, and `IsActive` returns `false` and `GetRunner` returns `null` for it.
:::

## Profiling and introspection

In Unity, every run system is wrapped in a `ProfilerMarker` named after the system (or its registration name), so systems show up in the Unity Profiler with no extra code. Under `KENSEI_DEBUG` the runner also records per-system timings:

<<< @/snippets/Concepts/Runner.cs#system-info{csharp}

`SystemCount` and `GetSystemInfo(i)` cover every registered entry, including nested runners (`ChildRunner`, `IsSeparatePhase`) and `DelHere` cleanups. `SetActive(int, bool)` toggles a run system or a nested runner by position. Under `KENSEI_DEBUG`, `ResetTimings()` clears the recorded timings.

`World.FilterCount`/`GetFilter(i)` and `World.ActivePools` expose filters and pools with their counts, capacities and `AllocatedBytes`, which is what the World Inspector's Filters and Pools tabs show:

<<< @/snippets/Concepts/Runner.cs#world-info{csharp}

The **KenseiECS -> Systems** window shows the runner tree with these toggles and timings; see [Debug Tools](../unity/debug-tools.md).
