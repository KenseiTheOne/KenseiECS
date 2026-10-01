# World Lifecycle

<<< @/snippets/Guides/WorldLifecycle.cs#lifecycle{csharp}

## Clear

`world.Clear()` destroys all entities and empties all pools, filters and groups. Pools, filters and their registrations are preserved, so nothing is reallocated on next use. Every existing `Entity` handle becomes stale. `Clear` fires no world events and resets `world.Tick` to 0.

## Destroy

`world.Destroy()` clears the world and nulls its internal references so the GC can collect them. Do not use the world afterwards; `world.IsDestroyed` reports this state.

## Warmup

`systems.Warmup()` calls `Init`, then `world.Warmup()`, which creates a temporary entity, adds a default component of every registered type and destroys it — exercising Add/Remove paths and filter updates. Existing entities and their data are not touched, and world event listeners and the profiler do not observe the temporary entity. Call once before gameplay starts (e.g. during a loading screen).

::: tip
Only types whose pools exist at that point are touched — those registered by systems' `Init` or by earlier `Pool<T>()` calls.
:::

::: warning Pool and filter listeners
Only `IWorldEventListener` dispatch is suppressed during warmup. The temporary entity goes through the normal `Add`/`Remove` path, so [component listeners](./component-listeners.md) and filter listeners registered before `Warmup` see it.
:::
