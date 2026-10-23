# World Lifecycle

<<< @/snippets/Guides/WorldLifecycle.cs#lifecycle{csharp}

## Clear

`world.Clear()` destroys all entities and empties all pools, filters and groups. Pools, filters and their registrations are preserved, so nothing is reallocated on next use. Every existing `Entity` handle becomes stale. `Clear` fires no world events and resets `world.Tick` to 0.

## Destroy

`world.Destroy()` clears the world and nulls its internal references so the GC can collect them. Do not use the world afterwards; `world.IsDestroyed` reports this state. A second `Destroy` is a no-op; `Clear`, `Reserve` and creating a filter throw `ObjectDisposedException`, and so do `CreateEntity` and `Pool<T>` under `KENSEI_DEBUG` (in release they fail with `NullReferenceException`). Even after such a failed `CreateEntity`, `IsAlive` returns `false` and `DestroyEntity` does nothing for any handle, as on any destroyed world.

## Warmup

`systems.Warmup()` calls `Init`, then `world.Warmup()`, which creates a temporary entity, adds a default component of every registered type and destroys it — exercising Add/Remove paths and filter updates. Existing entities and their data are not touched, and world event listeners and the profiler do not observe the temporary entity. Call once before gameplay starts (e.g. during a loading screen).

::: tip
Only types whose pools exist at that point are touched — those registered by systems' `Init` or by earlier `Pool<T>()` calls.
:::

::: warning Pool and filter listeners
Only `IWorldEventListener` dispatch is suppressed during warmup. The temporary entity goes through the normal `Add`/`Remove` path, so [component listeners](./component-listeners.md) and filter listeners registered before `Warmup` see it. `systems.Warmup()` runs `Init` first, so this includes every listener a system subscribes in `Init`:

- `IComponentListener<T>.OnAdded` and then `OnRemoved` are called once per registered type with `default(T)` data;
- `IFilterListener.OnEntityAdded` and `OnEntityRemoved` are called as the entity enters and leaves filters. Components are added one type at a time, so it can briefly match a filter with `Exc` types too.

The entity index is a real slot that is freed right after and reused by the next `CreateEntity`. A listener that spawns views, counts spawns or sends analytics must recognise or tolerate this entity, or subscribe after `Warmup`.
:::

::: tip Event list pool
`Warmup` does not pre-allocate the pooled lists of [`EventBuffer<T>`](./one-frame.md#prewarming-the-list-pool): its default buffer rents no list. Call `EventBuffer<T>.Prewarm(lists, capacity)` for that, and [`world.Reserve`](./world-config.md#reserving-capacity-later) to size entity storage, pools and filters for the peak.
:::
