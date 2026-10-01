# Debug Tools

KenseiECS ships editor windows under the **KenseiECS** menu. They are Editor-only (nothing of them reaches a player build), and all but one need the `KENSEI_DEBUG` define.

| Menu item | Needs `KENSEI_DEBUG` | Shows |
|---|---|---|
| **KenseiECS -> Systems** | no (timings only with it) | Runner tree with enable toggles |
| **KenseiECS -> World Inspector** | yes | Entities, filters, pools |
| **KenseiECS -> Profiler** | yes | Entity lifecycle events with call stacks |
| **KenseiECS -> Debug Mode** | — | Toggles the define |

The World Inspector and Profiler windows are not compiled without `KENSEI_DEBUG`, so their menu items appear only after Debug Mode is turned on.

World is auto-discovered via `IEcsWorldProvider` on any MonoBehaviour in the scene, and the runner via `IEcsSystemsProvider`. [`EcsBootstrap`](./bootstrap.md) implements both; nothing has to be wired by hand. The windows re-check at most once a second in play mode and drop the previous session's world when play mode starts or stops.

## Debug Mode

Menu: **KenseiECS -> Debug Mode** — toggles the `KENSEI_DEBUG` scripting define.

- The define is applied to every build target, so switching platforms keeps the mode. The menu checkmark reflects the currently selected target.
- On Unity 2023.1+ it is set for Standalone, Server, Android, iOS, WebGL and the current target; on older versions for every build target group the editor supports.
- When enabled, profiler hooks, inspector editing, the World Inspector and the Profiler window are active, together with the runtime validation listed in [Release vs. KENSEI_DEBUG](../guides/debug-mode.md). When disabled, all debug overhead is stripped from compilation.

When enabled you get:

- **KenseiECS -> World Inspector** — Entities tab with editable components and debug names, Filters and Pools tabs with counts, capacities and memory
- **KenseiECS -> Profiler** — lifecycle events with call stacks
- **EcsEntityView** inspector with entity navigation
- **Validation** — see the [Release vs. KENSEI_DEBUG table](../guides/debug-mode.md)
- **IDE debugger view** — hovering a `World` in Rider or Visual Studio lists the alive entities with all their components (a `DebuggerTypeProxy`, no runtime cost)

## Systems window

**KenseiECS -> Systems** works in any build: the runner tree with enable toggles per system and phase, plus per-system timings under `KENSEI_DEBUG`. It shows content only in play mode.

- Toolbar: the world's current **Tick** and whether the root runner is **Initialized**. Under `KENSEI_DEBUG` a **Reset peaks** button clears the timings of the whole tree.
- Tree: the **Root** runner, then every system in order. Nested runners are shown in bold with their own systems indented below; named runners (the `fixed` and `late` phases of `EcsBootstrap`) are tagged **phase**. Each row also shows the system's type name.
- Toggles: run systems and nested runners have a checkbox that enables or disables them at runtime (the same as `SetActive` on the runner, see [SystemsRunner](../concepts/runner.md)). Init-only and destroy-only systems have no toggle. Disabled rows are greyed out.
- Under `KENSEI_DEBUG`: **Last ms** and **Peak ms** columns for every run system.

## World Inspector

**KenseiECS -> World Inspector** (`KENSEI_DEBUG` only) has three tabs:

**Entities**
- Toolbar with the entity and pool counts, a search field and **Clear**. Search matches the entity index, its debug name or any of its component type names.
- Alive entities, 100 per page (**< Prev** / **Next >**). Each row shows the entity, its debug name and component count.
- Expanding an entity shows an editable **Name** field (the debug name; clear it to remove the name) and every component. Field values, nested structs and lists are editable in play mode.

**Filters** — every registered filter with its **Count**, **Dense cap** and **Memory**, plus the total.

**Pools** — every component pool, sorted by memory, with **Count**, **Sparse cap**, **Dense cap**, component **Size** (`managed` when the component holds references and cannot be measured) and **Memory**. The footer sums pools + filters.

## Profiler

**KenseiECS -> Profiler** (`KENSEI_DEBUG` only) shows the events recorded by the runtime `EcsProfiler`. It stays empty ("Profiler is not enabled") until you enable recording from code:

<<< @/snippets/Guides/DebugTools.cs#profiler{csharp}

`EcsProfiler` is compiled only under `KENSEI_DEBUG`, so calls to it must be wrapped in `#if KENSEI_DEBUG`. It has a runtime cost — use it for debugging only.

The window shows:

- Toolbar: event count, type filters **All** / **Created** / **Destroyed** / **+Comp** / **-Comp**, a search field (entity index or component type) and **Clear**, which also clears the recorded events.
- Event list, newest first, 200 per page: **Tick**, **Time (ms)**, **Type**, **Entity** (with its debug name when the world has one), **Component**. Rows are colored by event type.
- Clicking an entity narrows the list to that entity's history. **<** / **>** step back and forward through the entities you visited; **Show All** returns to the full list.
- Selecting an event shows its call stack in the bottom panel. Stacks are recorded only when `EcsProfiler.CaptureStacks` is on.

### Runtime API

The profiler can also be read from code, without the window:

<<< @/snippets/Guides/DebugTools.cs#query{csharp}

| Member | Description |
|---|---|
| `Enable(world)` | Start recording for one world. Events from other worlds are ignored. Clears previous events. |
| `Disable()` | Stop recording. Also happens automatically when the profiled world is destroyed. |
| `IsEnabled` | Whether the profiler is recording. |
| `CaptureStacks` | Capture a call stack per event. Off by default; expensive. |
| `MaxEvents` | Ring buffer size (10 000 by default, must be positive). The oldest events are discarded; resizing keeps the newest ones. |
| `GetEvents()` | All recorded events in chronological order. |
| `GetEntityHistory(index)` / `GetEventsByType(type)` / `GetEventsByTick(tick)` | Filtered copies. |
| `Clear()` | Drop the recorded events. |

Each `ProfileEvent` has `Tick`, `Type` (`Created`, `Destroyed`, `ComponentAdded`, `ComponentRemoved`), `EntityIndex`, `Generation` (entity events), `ComponentType` (component events), `TimestampMs` (since `Enable`) and `CallStack`. Events raised while the world suppresses its own events (`World.Warmup`, for example) are not recorded. In Unity, entering play mode without a domain reload resets the profiler, its events and `CaptureStacks`.

## EcsEntityView inspector

With Debug Mode on, selecting a GameObject with an [`EcsEntityView`](./entity-view.md) shows the bound entity's components in the Inspector with editable fields, nested structs and lists. `Entity` fields are clickable and navigate to that entity, with **< Back** / **Forward >** history and **Home** to return to the view's own entity.
