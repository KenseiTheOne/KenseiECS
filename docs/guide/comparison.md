# KenseiECS vs other ECS

This page compares KenseiECS with the C# ECS frameworks people usually weigh it against. It sticks to design and capabilities. Performance numbers come only from this repository's [benchmarks](../benchmarks.md), which cover LeoEcsLite and Arch; Friflo, Unity Entities and DefaultEcs were not benchmarked here, so no numbers are given for them.

Project status was checked in October 2026. Every claim about another library links to a source in the list at the bottom of the page.

## At a glance

| | KenseiECS | Arch | Friflo.Engine.ECS | Unity Entities | LeoEcsLite | DefaultEcs |
|---|---|---|---|---|---|---|
| Storage | Sparse set per type, optional owning groups | Archetypes in 16 KB chunks | Archetypes, contiguous memory | Archetypes in chunks | Sparse-set pools | Per-type component storage |
| Latest version | 2.0.0 | 2.1.0 (May 2025) | 3.6.0 (Mar 2026), 4.0 in preview | 6.7 (package) | 2026.4.25 (Apr 2026) | 0.17.2 (Feb 2022), 0.18.0-beta01 (Apr 2023) |
| Status | Active, new | Active | Active | Active, by Unity | **Archived**; author recommends EcsProto | Inactive since 2024 |
| Plain Unity without DOTS | Yes | Yes (via Arch.Unity) | Yes | No, it is DOTS | Yes | Yes |
| Burst / Unity jobs | No | Yes, through Arch.Unity's `IJobArchChunk` | Not documented | Yes, built in | Not documented | Not documented |
| Plain .NET | netstandard2.1, net8.0 | netstandard2.1, .NET 6/8 | netstandard2.1, .NET 5 to 10, Native AOT | No | Yes | netstandard1.1+ |
| NuGet package | No (git URL / project reference) | Yes | Yes | No (Unity package) | — | Yes |

## Storage model and structural changes

KenseiECS keeps each component type in its own sparse set. Adding or removing a component touches that type's pool and the filters that watch it, and never moves the entity's other components. Archetype ECSs (Arch, Friflo, Unity Entities) group entities with the same component set into contiguous chunks. Iteration is as fast as it gets, but every add or remove moves the entity's whole component set to another archetype.

Unity's documentation states the cost directly: on an archetype change "Unity must move the entity to another chunk", structural changes cannot run in jobs, and they create sync points where the main thread waits for all scheduled jobs. Unity offers entity command buffers and enableable components (toggled without a structural change) to work around this. Arch and Friflo also have command buffers for deferring changes.

Measured in this repository (10,000 entities, add + remove one component, see [Benchmarks](../benchmarks.md)):

| | KenseiECS | LeoEcsLite | Arch |
|---|---:|---:|---:|
| Add + remove, nothing observing the type | 100 us | **73 us** | 590 us |
| Add + remove, 32 filters observing the type | **1,083 us** | 4,356 us | — |

LeoEcsLite wins the unobserved case because it keeps no per-entity bitmask. KenseiECS pulls ahead once filters care about the changed type, and against Arch the gap is the archetype move.

## Iteration speed

Iteration is where archetype ECSs win. Measured in this repository over Position + Velocity, 10,000 entities:

| | KenseiECS filter | KenseiECS owning group | LeoEcsLite | Arch |
|---|---:|---:|---:|---:|
| Fresh world | 13.9 us | 5.5 us | 14.0 us | **5.4 us** |
| Fragmented world | 14.9 us | 5.5 us | 16.7 us | **5.4 us** |
| Mixed game-loop frame | **34 us** | — | 40 us | 78 us |

A KenseiECS filter loop is on par with LeoEcsLite and about 2.5x slower than Arch. An [owning group](../guides/groups.md) matches Arch, but only for the 2–4 component types it owns, and a pool can belong to one group only. In an archetype ECS every query gets contiguous iteration without choosing groups up front. The mixed frame, where one-frame components are added and removed every tick, is where KenseiECS comes out ahead.

Friflo and Unity Entities were not benchmarked here. They are archetype ECSs, so expect their iteration to behave like Arch's rather than like a sparse set, but this page gives no numbers for them.

## Queries and filters

| | Query model |
|---|---|
| KenseiECS | Reactive [filters](../concepts/filters.md) with `Inc`, `Exc` and `Any`. The cached entity set is updated on each structural change, and identical filters are deduplicated. |
| Arch | `QueryDescription` with `WithAll`, `WithAny` and `WithNone`. |
| Friflo | Queries with component types and tag filters (`AllTags`). Also O(1) lookup by indexed component value, plus relations and parent/child hierarchies. |
| Unity Entities | `EntityQuery`, with change filters and shared-component value filters. |
| LeoEcsLite | Filters with `Inc` and `Exc` that cache entity lists. |
| DefaultEcs | `EntitySet` built with `With` and `Without`. The README says it is "automatically updated as you change your entity component composition". Also `EntityMap` and `EntityMultiMap` keyed by component value, and `WhenAdded`, `WhenChanged` and `WhenRemoved` rules. |

KenseiECS has no relations, hierarchies or component-value indexes. Friflo is the most complete in that area.

## Change tracking and events

| | Change tracking | Structural events |
|---|---|---|
| KenseiECS | Opt-in per pool: [`Modify`/`MarkChanged`](../guides/change-tracking.md) stamp a per-component version, and consumers ask `ChangedSince`. Per entity, but writes through `Get` are not seen. | [Component listeners](../guides/component-listeners.md) per pool and [world events](../guides/world-events.md), always compiled in. Also [one-frame components](../guides/one-frame.md). |
| Arch | Events include a "component modification" event. | Entity create/destroy and component add/set/remove events, **behind `#define EVENTS`**, which requires building Arch from source (a fork), because "it affects efficiency somewhat". |
| Friflo | Component events carry an `Update` action with access to the old value. `EventRecorder` and `EventFilter` let queries filter by recorded events. | Component, tag, script and child-entity events, plus custom struct signals. |
| Unity Entities | Change filters "apply to whole archetype chunks, and not individual entities". A chunk counts as changed when a system with write access ran, "not whether it changed any data". | No per-entity callbacks. Structural work goes through entity command buffers. |
| LeoEcsLite | None. | World and filter events behind `LEOECSLITE_WORLD_EVENTS` / `LEOECSLITE_FILTER_EVENTS`, which the author advises against for performance reasons. World events are also on in every `DEBUG` build. |
| DefaultEcs | `WhenChanged` sets, triggered by `Set` or `NotifyChanged`. | Message publish and subscribe, plus `WhenAdded` and `WhenRemoved` sets. |

## Unity integration

- **KenseiECS** is a regular UPM package with no DOTS dependency. It supports Unity 2021.3+ and IL2CPP, and ships a [bootstrap and inspector authoring](../unity/bootstrap.md), a [listener bridge](../unity/listener-bridge.md) to MonoBehaviours and [editor windows](../unity/debug-tools.md). `World`, pools and filters are managed classes, so **Burst does not apply** and the Unity job system is not used.
- **Unity Entities** is Unity's own DOTS ECS. It brings baking from GameObjects, subscenes, Burst-compiled `IJobEntity` jobs scheduled in parallel, and integration with Entities Graphics, Unity Physics and Netcode for Entities. For heavy simulation in Unity, it is the strongest option on this list. In exchange, the project is built around DOTS rather than plain GameObjects.
- **Arch** runs in Unity, and the community package Arch.Unity adds GameObject-to-entity conversion, an Arch Hierarchy window, PlayerLoop systems and `IJobArchChunk` for Burst jobs over Arch queries. Its dependencies are installed through NuGet For Unity.
- **Friflo** supports Unity (Mono, AOT/IL2CPP, WebGL). The friflo-ecs-unity package adds Inspector components and syncs with GameObjects and scene files.
- **LeoEcsLite** is tested on Unity 2020.3, with a separate editor integration package. The repository is archived.
- **DefaultEcs** can be used in Unity, but its serializers use `Reflection.Emit` and do not work on AOT platforms.

## .NET support

KenseiECS targets `netstandard2.1` and `net8.0` from the same sources the Unity package uses. There is no NuGet package: you reference the project or copy the sources (see [Installation](./installation.md)). The site's [live demo](./demo.md) runs KenseiECS on .NET WebAssembly. Arch and Friflo ship on NuGet, and Friflo has the widest platform list, from .NET Standard 2.1 to .NET 10 plus WASM and Native AOT. Unity Entities does not run outside Unity.

## Threading

- **KenseiECS**: the world is single-threaded. [`ParallelRunner`](../guides/threading.md) splits a filter or group over its own worker threads with struct jobs and no allocations. Jobs may only touch component data in their range.
- **Arch**: parallel query overloads (`ParallelQuery`, `InlineParallelChunkQuery`, …) on its own allocation-free `JobScheduler`. Its command buffers are described as thread-safe.
- **Friflo**: multithreaded queries ("Parallel Query Job").
- **Unity Entities**: the C# job system with Burst, dependency tracking and parallel scheduling. Structural changes cause sync points on the main thread.
- **LeoEcsLite**: not thread-safe, "and never will be", according to its README.
- **DefaultEcs**: `IParallelRunner` and parallel systems. Creating and disposing entities is not thread-safe.

## Code generation

KenseiECS ships a Roslyn [source generator](../guides/source-generator.md) that writes a system's `Init` from `[Inc]`, `[Exc]`, `[Pool]`, `[Group]` and `[Shared]` field attributes, with compile-time diagnostics. Queries stay hand-written loops. Friflo 3.6 added a Query Generator for query code. Arch.Extended adds systems and source generators. Unity Entities generates the job code for `IJobEntity`. DefaultEcs has a separate Roslyn analyzer. LeoEcsLite has none.

## Debug tooling

- **KenseiECS**: Unity editor windows for systems (with per-system timings), the World Inspector (editable components) and a Profiler for entity lifecycle events, plus a `DebuggerTypeProxy` for IDEs. Most of it requires the `KENSEI_DEBUG` define, which also turns on runtime validation of stale handles, and costs nothing in release builds. See [Debug Tools](../unity/debug-tools.md) and [Release vs. KENSEI_DEBUG](../guides/debug-mode.md).
- **Unity Entities**: Entities Hierarchy, the Systems window and the Inspector integration.
- **Arch**: Arch Hierarchy in Arch.Unity, plus community debuggers for Godot and Stride.
- **Friflo**: debugger views of entities, components, tags, relations, query results and systems, plus the Unity Inspector integration.
- **LeoEcsLite**: a separate Unity editor package for world monitoring.

## Maturity and community

KenseiECS is new: version 2.0.0, a small community and no NuGet package. Every other project here has more users and a longer track record. Unity Entities has Unity behind it. Arch is the most-starred of the standalone libraries (about 1.8k GitHub stars). Friflo is under active development with frequent releases. LeoEcsLite is widely used in Unity projects but is now archived, and DefaultEcs has not had a stable release since 2022. If long-term support from a large community matters most, that counts against KenseiECS.

## When to pick KenseiECS

- You build a Unity game on plain GameObjects and want an ECS for gameplay logic without moving to DOTS.
- Your gameplay is heavy on structural changes: one-frame events, status effects, tag components added and removed every frame. Sparse-set storage and reactive filters keep that cheap, and the mixed-frame benchmark is where KenseiECS leads.
- You want reactive filters, per-pool listeners, world events and per-entity change tracking that are always available, with no compile flags.
- You are migrating from LeoEcsLite (now archived) and want a similar API. See [Migrating from LeoEcsLite](../migration-from-leoecslite.md).
- You want the same ECS core in Unity and on a .NET server or in tools.

## When not to

- **Raw iteration over large, stable entity sets dominates your frame.** Arch iterates about 2.5x faster than a KenseiECS filter (other archetype ECSs were not measured here). Owning groups close the gap only for the types they own.
- **You need Burst and the Unity job system**, or you are already on DOTS (Entities Graphics, Unity Physics, Netcode for Entities). Use Unity Entities.
- **You need relations, hierarchies or indexed component lookups** built into the ECS. Friflo has all three.
- **You need a published NuGet package or Native AOT as a documented target today.** Arch and Friflo are on NuGet, and Friflo documents the widest platform list.
- **You need a large community and a long track record.** KenseiECS is young.

::: details Sources
Opened in October 2026. Version and status data come from the GitHub repositories, their release lists and the NuGet package indexes.

- Arch: <https://github.com/genaray/Arch>, <https://github.com/genaray/Arch/releases>, <https://github.com/genaray/Arch/wiki>, <https://github.com/genaray/Arch/wiki/Utility-Features> (events behind `EVENTS`, command buffers), <https://github.com/genaray/Arch/wiki/Performance-Features> (JobScheduler, parallel queries), <https://api.nuget.org/v3-flatcontainer/arch/index.json>
- Arch.Extended: <https://github.com/genaray/Arch.Extended>
- Arch.Unity: <https://github.com/AnnulusGames/Arch.Unity>
- Friflo.Engine.ECS: <https://github.com/friflo/Friflo.Engine.ECS>, <https://github.com/friflo/Friflo.Engine.ECS/releases>, <https://friflo.gitbook.io/friflo.engine.ecs>, <https://friflo.gitbook.io/friflo.engine.ecs/documentation/events>, <https://friflo.gitbook.io/friflo.engine.ecs/project/unity-extension>, <https://github.com/friflo/friflo-ecs-unity>, <https://api.nuget.org/v3-flatcontainer/friflo.engine.ecs/index.json>
- Unity Entities: <https://docs.unity3d.com/Packages/com.unity.entities@6.7/manual/index.html>, <https://docs.unity3d.com/Packages/com.unity.entities@6.7/manual/concepts-structural-changes.html>, <https://docs.unity3d.com/Packages/com.unity.entities@6.7/manual/systems-entityquery-filters.html>, <https://docs.unity3d.com/Packages/com.unity.entities@6.7/manual/components-enableable-intro.html>, <https://docs.unity3d.com/Packages/com.unity.entities@6.7/manual/iterating-data-ijobentity.html>, <https://docs.unity3d.com/Packages/com.unity.entities@6.7/manual/editor-entities-windows.html>
- LeoEcsLite: <https://github.com/Leopotam/ecslite> (archived, README status notice, threading, event defines), <https://github.com/Leopotam/ecslite/releases>
- DefaultEcs: <https://github.com/Doraku/DefaultEcs>, <https://github.com/Doraku/DefaultEcs/blob/master/README.md>, <https://github.com/Doraku/DefaultEcs/releases>, <https://api.nuget.org/v3-flatcontainer/defaultecs/index.json>
- KenseiECS numbers: [Benchmarks](../benchmarks.md) (BENCHMARKS.md in the repository root; LeoEcsLite 1.0.1 and Arch 2.1.0, .NET 8, September 2026)
:::
