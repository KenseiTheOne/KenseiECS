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

- **Unity 6 with an active [Build Profile](https://docs.unity3d.com/Manual/build-profiles.html):** the define goes into that profile's **Scripting Defines**. Other profiles are untouched, so a release profile stays clean while a dev profile keeps Debug Mode. The profile is an asset, usually under version control and shared by the team: every toggle changes it for everyone who pulls the commit. The toggle logs one line with the profile's name and asset path whenever it writes to it, so the change does not slip into a commit unnoticed. Turning the mode off and on again restores the define, but Unity may leave other serialized fields changed (seen: `m_HasScriptingDefines` going from `0` to `1`), so the asset is not always byte-identical afterwards; revert it in version control if that matters.
- **No active Build Profile, or Unity before 6:** the define goes into the Player Settings defines of the **active build target only**. Switching to another platform does not carry the mode over; turn it on there separately.
- The checkmark shows whether the define is in effect for the active profile/target, from either the profile or Player Settings. Turning it off removes it from both, so a define that versions before 2.2 wrote to every build target does not keep the mode on silently.
- Turning it off changes only the active profile and the active target. If the Player Settings of other targets still have `KENSEI_DEBUG` (versions before 2.2 wrote it to every target), a warning in the Console lists them; the toggle does not remove it there, since another target may keep it on purpose. Remove it by hand in **Player Settings -> Scripting Define Symbols** of each listed target. Unity may also leave an empty define entry for a target in `ProjectSettings.asset` after the last define is removed; it is harmless but shows up in version control.
- When enabled, profiler hooks, inspector editing, the World Inspector and the Profiler window are active, together with the runtime validation listed in [Release vs. KENSEI_DEBUG](../guides/debug-mode.md). When disabled, all debug overhead is stripped from compilation.

### Release build guard

A player build that is **not** a Development Build fails in the preprocess step if its defines contain `KENSEI_DEBUG`: validation, debug names and timings would otherwise ship in a release player. The defines checked are those of the active Build Profile (Unity 6) plus the Player Settings of the built target, and the error says where the define was found: the profile (name and asset path) and/or the Player Settings of the target. Turn Debug Mode off, tick **Development Build**, or — to ship validation on purpose — add `KENSEI_ALLOW_DEBUG_RELEASE` to the same defines; that also turns the check off.

The preprocess step does not see defines passed only to the build: `BuildPlayerOptions.extraScriptingDefines` or an `Assets/csc.rsp`. For those a compile-time check in the runtime assembly takes over: a release player compiled with `KENSEI_DEBUG` from any source stops with a `#error` that says so (the error comes from the script compile, after the preprocessors). `KENSEI_ALLOW_DEBUG_RELEASE` passed the same way satisfies that check, but not the preprocess step: when `KENSEI_DEBUG` is in the profile or Player Settings, put the allow define there too.

The guard is a last line of defence, not a pre-flight check. It runs early (`callbackOrder` -1000), but throwing does not stop the build step: preprocessors ordered before it have already run, and Unity still calls the ones after it. Whatever those change in assets (render pipeline global settings, preloaded assets and the like) can stay behind after the failed build. In CI, check the defines in the build script before `BuildPipeline.BuildPlayer`, from the sources the guard reads plus the ones the script passes itself:

```csharp
// Unity 6 build script: call before BuildPipeline.BuildPlayer, so no preprocessor runs at all.
static void ThrowIfDebugRelease(BuildProfile profile, NamedBuildTarget target, bool development,
    string[] extraScriptingDefines = null) {
    if (development) {
        return;
    }
    // Activate the profile first, as BuildPlayer(BuildPlayerWithProfileOptions) will: a profile
    // with its own Player Settings makes PlayerSettings read its copy only while it is active.
    BuildProfile.SetActiveBuildProfile(profile);
    var defines = new List<string>(profile.scriptingDefines);
    PlayerSettings.GetScriptingDefineSymbols(target, out string[] symbols);
    defines.AddRange(symbols);
    // Defines the script passes to the build itself (BuildPlayerOptions.extraScriptingDefines).
    if (extraScriptingDefines != null) {
        defines.AddRange(extraScriptingDefines);
    }
    if (defines.Contains("KENSEI_DEBUG") && !defines.Contains("KENSEI_ALLOW_DEBUG_RELEASE")) {
        throw new BuildFailedException("KENSEI_DEBUG is on for a release build of " + profile.name);
    }
}
```

A script build through `BuildPipeline.BuildPlayer(BuildPlayerWithProfileOptions)` makes that profile active before the build, so its defines are the ones checked (and it stays active afterwards). The snippet activates the profile itself for the same reason: without that, `PlayerSettings` would answer for whichever profile is active at that moment (see below), and a release build could fail on a dev profile's define or pass with its own.

::: warning Profiles with their own Player Settings
A Build Profile can override Player Settings (**Customize player settings** in the profile). While such a profile is active, Unity redirects every `PlayerSettings` read and write to the copy stored in the profile asset. For KenseiECS this means:

- Turning Debug Mode off removes `KENSEI_DEBUG` from that copy, so the profile asset changes; the toggle logs this with the profile's name and path, like its other profile writes.
- The build guard and the warning about other targets read that copy too. Their messages name it ("the Player Settings stored in Build Profile '…'"), so look for the define in the profile, not in **Project Settings -> Player**. The warning cannot see leftovers in the project's own Player Settings (`ProjectSettings.asset`) while the profile is active, and says so.
- If the profile's Player Settings were copied while 2.1.x was installed, they may still contain `KENSEI_DEBUG` for every target.

Whether a profile overrides Player Settings is read through an internal Unity property; if a Unity version does not have it, the messages name both possible places.
:::

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

- Toolbar: event count, type filters **All** / **Created** / **Destroyed** / **+Comp** / **-Comp** / **Grow**, a search field and **Clear**, which also clears the recorded events. A search of digits only is an entity index and lists only events of matching entities (growth events have none); any other text matches the component type or the growth description (`dense`, a type name).
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

Each `ProfileEvent` has `Tick`, `Type` (`Created`, `Destroyed`, `ComponentAdded`, `ComponentRemoved`, `StorageGrown`), `EntityIndex`, `Generation` (entity events), `ComponentType` (component events; for `StorageGrown`, which array grew past [`World.Reserve`](../guides/world-config.md#reserving-capacity-later) and its sizes, with `EntityIndex` -1), `TimestampMs` (since `Enable`) and `CallStack`. Events raised while the world suppresses its own events (`World.Warmup`, for example) are not recorded. In Unity, entering play mode without a domain reload resets the profiler, its events and `CaptureStacks`.

## EcsEntityView inspector

With Debug Mode on, selecting a GameObject with an [`EcsEntityView`](./entity-view.md) shows the bound entity's components in the Inspector with editable fields, nested structs and lists. `Entity` fields are clickable and navigate to that entity, with **< Back** / **Forward >** history and **Home** to return to the view's own entity.
