# SystemsRunner

`SystemsRunner` holds systems and executes them in registration order. It also owns the cleanup of [one-frame components](../guides/one-frame.md) and can contain other runners.

<<< @/snippets/Concepts/Runner.cs#pipeline{csharp}

- `Add(system, name)` registers a system; the optional name is what `SetActive`/`IsActive` use and what the Unity Profiler shows. Unnamed systems are reported under their type name. Names must be unique within one runner: under `KENSEI_DEBUG` registering a second system, group or phase under a name already used in that runner throws; in release the second one silently replaces the first in the name lookup (the first keeps running, but only `SetActive(int, bool)` can reach it). `DelHere` entries are exempt — the same type may be cleaned at several points. Different runners have separate names.
- `AddGroup(name, runner)` and `AddPhase(name, runner)` nest another runner; see [nested runners](#nested-runners).
- `OneFrame<T>()` removes every `T` at the end of `Run`; `DelHere<T>()` removes them at that point of the pipeline, so systems registered before it see the components and systems after it do not. See [OneFrame Components](../guides/one-frame.md).
- `SetActive(name, false)` skips a named system during `Run`; `IsActive(name)` reads the flag. Only systems with `Run`, groups and phases can be toggled: for an Init/Destroy-only system `SetActive` throws under `KENSEI_DEBUG` and does nothing in release.
- `IndexOf(system)` and `IndexOf(name)` return the position in registration order — the index `GetSystemInfo` and `SetActive(int, bool)` take — or `-1`. They find any registered system, Init/Destroy-only ones included (`GetSystemInfo(i).IsRunnable` tells which can be toggled). For a run system, group or phase, `IndexOf(name)` resolves a registration name the same way `SetActive(name)` and `IsActive(name)` do, so `SetActive(IndexOf(name), x)` and `SetActive(name, x)` act on the same system; only when nothing was registered under that name does it fall back to the first unnamed system whose type name matches. Neither searches nested runners.

::: warning Names are case-sensitive and not checked in release
Names are compared ordinally: `"sim"` and `"Sim"` are different names. Under `KENSEI_DEBUG` an unknown name passed to `SetActive`, `IsActive` or `GetRunner` throws (the message says so); in release `SetActive` does nothing, `IsActive` returns `false` and `GetRunner` returns `null`, without any message. A pause that calls `SetActive("Sim", …)` every frame silently does nothing in a release build. Keep names in constants, or hold the runner itself and call [`SetActive(bool)`](#isenabled-of-a-nested-runner) on it.
:::
- `Warmup()` runs `Init` and pre-touches the world before gameplay starts. The temporary warmup entity is invisible to world event listeners, but pool listeners (`IComponentListener<T>`) and filter listeners (`IFilterListener`) — including the ones systems subscribe in `Init` — see it being added and removed. See [World Lifecycle](../guides/world-lifecycle.md#warmup).

## Lifecycle contract

- `Init` runs each `IInitSystem` once, in registration order. `Add` after `Init` throws under `KENSEI_DEBUG`.
- If a system's `Init` throws, `Init` rolls back: every `IDestroySystem` registered **before** the failed system gets `Destroy`, in reverse order — systems that subscribed to events or allocated in `Init` can undo it. The failed system and the ones after it are not destroyed. A nested runner rolls itself back the same way before the exception reaches its parent, which then destroys what came before that runner. The runner stays uninitialized and the original exception is rethrown; if a `Destroy` during the rollback throws as well, the rollback still finishes and `Init` throws an `AggregateException` whose first inner exception is the original one, followed by the rollback errors. The next `Init` starts over from the first system.
- `Run` executes enabled systems in order, then removes OneFrame components. Cleanup runs even if a system throws (systems after the failing one are skipped that frame). `Run` before `Init` throws under `KENSEI_DEBUG`.
- `Destroy` runs `IDestroySystem` in **reverse** registration order, is a no-op before `Init` (and after a failed one, which already rolled back), and resets the runner so `Init` can run again (scene reload).

::: warning Changed in 2.2.0
Up to 2.1.x a failed `Init` destroyed nothing and the next `Init` resumed with the system that failed, so the systems before it were initialized once. Since 2.2.0 those systems are destroyed by the rollback and initialized again by the retry. A system whose `Init` is not safe to repeat after its `Destroy` needs a `Destroy` that undoes its `Init`.
:::

## Nested runners

Update-phase systems live in the root runner. `root.Run()` advances the world tick, runs the root's systems and cleans the root's OneFrame components.

| Child runner | Runs as part of the parent's `Run()` | Driven by | `SetActive`/`IsActive` by name |
|---|---|---|---|
| **Phase** — `AddPhase("fixed", child)`, or the obsolete `Add(child, "fixed")` | no — a separate phase (FixedUpdate/LateUpdate) | `GetRunner(name).Run()`: runs the child's systems and cleans the child's OneFrame components, without ticking | yes |
| **Named group** — `AddGroup("sim", child)` | yes — inline, at its registration position | the parent | yes |
| **Unnamed group** — `Add(child)` | yes — inline, at its registration position | the parent | no (only by index) |

`Add(child, "name")` with a runner typed as `SystemsRunner` makes a phase, like `AddPhase`, and is marked `[Obsolete]` since 2.2.0: next to `AddGroup` it reads like a group, but its systems never run unless `GetRunner(name).Run()` is called. Replace it with `AddPhase(name, child)` to keep the phase, or with `AddGroup(name, child)` if the systems should run inside the parent. `Add(child)` without a name (an unnamed group) and `Add(system, name)` for ordinary systems do not warn.

`Init()`/`Destroy()` cascade from root to all children; a child constructed without `SharedData` inherits the parent's. A named group's name is also its Unity `ProfilerMarker` and its `SystemInfo.Name` (an unnamed group shows up as `SystemsRunner`), and `GetRunner(name)` returns it — for inspection only: it already runs inside the parent, so do not call its `Run()` yourself.

The example below is the shape of a MonoBehaviour driving the runners from Unity's `Update` and `FixedUpdate`; [`EcsBootstrap`](../unity/bootstrap.md) does exactly this for you.

<<< @/snippets/Concepts/Runner.cs#nested{csharp}

`SetActive(name, false)` on a phase pauses the whole phase: its `Run()` runs no systems. On a group it pauses the group: the parent skips it.

### `IsEnabled` of a nested runner

`child.IsEnabled` is what the parent's `SetActive` sets. For a phase it is the phase's own flag. For a group — named or not — it always equals the group's slot in the parent: `SetActive(index, …)` and `SetActive(name, …)` change both, and `GetSystemInfo(index).IsEnabled`, `IsActive(name)` and `child.IsEnabled` report the same value. The slot starts as the group is when it is registered: a group switched off with `child.SetActive(false)` before `AddGroup` (or an unnamed `Add(child)`) starts disabled, so a game that boots paused can build its pipeline in one chain.

`child.SetActive(bool)` does the same from a reference to the runner, without its name or its parent: on a group it switches the group's slot in the parent together with `IsEnabled`, on a phase the phase's flag. Code that holds the group (a pause system, say) can toggle it directly:

<<< @/snippets/Concepts/Runner.cs#group-handle{csharp}

On a root runner `SetActive(false)` stops its systems: `Run()` still advances the tick and removes the root's `cleanWhenDisabled` types, but runs nothing else. A root runner is enabled until then.

::: info Changed in 2.2.0
In 2.1.x `SetActive(int, false)` on an unnamed group switched only the parent's slot and `child.IsEnabled` kept returning `true`.
:::

### Disabled runners and their OneFrame components

By default a skipped runner does not clean its OneFrame components: the cleanup is part of its `Run`. A runner is skipped when its slot in the parent is disabled (the parent does not run it at all — neither its systems nor its `DelHere` and `OneFrame` cleanups) or when `Run()` is called on a disabled phase. A one-frame component of that runner's types added while it is paused — by a system of another runner, say — stays until the runner is enabled again and finishes its next `Run`. A second `world.Add` of the same plain one-frame component to that entity throws, and `world.AddEvent` keeps appending to the same `EventBuffer<T>`.

Register the type with `OneFrame<T>(cleanWhenDisabled: true)` to remove it in the paused frames too:

<<< @/snippets/Concepts/Runner.cs#clean-when-disabled{csharp}

- When the parent's `Run` reaches the disabled slot of a group, it removes the group's `cleanWhenDisabled` types right there, at the slot's position in the parent's pipeline — systems registered before the slot still see the components, systems after it do not. Groups nested inside the skipped group are skipped with it and clean their `cleanWhenDisabled` types the same way.
- When `Run()` is called on a disabled phase, it runs no systems and only removes its `cleanWhenDisabled` types (and those of its groups). A phase nobody calls `Run()` on is not cleaned.
- Systems and `DelHere` of a skipped runner never run; types registered with plain `OneFrame<T>()` keep the default behavior.

The alternative is to register such a `OneFrame<T>` on a runner that keeps running, or not to produce those components while the runner is paused.

::: warning Release vs. KENSEI_DEBUG
Under `KENSEI_DEBUG`, adding, initializing or running a child constructed with a different `World` (or an explicitly passed different `SharedData`) throws instead of silently ignoring it, a repeated name throws, and unknown names passed to `SetActive`, `IsActive` or `GetRunner` throw. In release, an unknown name is ignored by `SetActive`, and `IsActive` returns `false` and `GetRunner` returns `null` for it. `AddGroup`/`AddPhase` with a `null` runner or an empty name throw in every build.
:::

## Profiling and introspection

In Unity, every run system is wrapped in a `ProfilerMarker` named after the system (or its registration name), so systems show up in the Unity Profiler with no extra code. Under `KENSEI_DEBUG` the runner also records per-system timings:

<<< @/snippets/Concepts/Runner.cs#system-info{csharp}

`SystemCount` and `GetSystemInfo(i)` cover every registered entry, including nested runners (`ChildRunner`, `IsSeparatePhase`) and `DelHere` cleanups. `SetActive(int, bool)` toggles a run system or a nested runner by position; `IndexOf` finds that position. Under `KENSEI_DEBUG`, `ResetTimings()` clears the recorded timings.

`World.FilterCount`/`GetFilter(i)` and `World.ActivePools` expose filters and pools with their counts, capacities and `AllocatedBytes`, which is what the World Inspector's Filters and Pools tabs show:

<<< @/snippets/Concepts/Runner.cs#world-info{csharp}

The **KenseiECS -> Systems** window shows the runner tree with these toggles and timings; see [Debug Tools](../unity/debug-tools.md).
