# World Events

`IWorldEventListener` receives every entity and component change in a world.

<<< @/snippets/Guides/WorldEvents.cs#listener{csharp}

<<< @/snippets/Guides/WorldEvents.cs#register{csharp}

## Order and guarantees

- `OnEntityCreated` fires after the first component was added (`CreateEntity`) or after all components were copied (`CopyEntity`); `OnComponentAdded` for that first component fires before it.
- `OnComponentRemoved` fires while the entity is still alive. If it was the last component, auto-destroy (`OnEntityDestroyed`) follows.
- `OnEntityDestroyed` fires before components are removed; the entity is already dead (`IsAlive` false) but its components are still readable.
- Listeners may add or remove listeners during dispatch; the dispatch continues over the set captured at its start. Listeners may modify the world, including the entity being destroyed.
- `Warmup` and `Clear` fire no events. Neither does [`WorldSerializer.Load`](./snapshots.md).

::: warning Exceptions
If a listener throws, the exception propagates after the operation is brought to a consistent state: the entity slot is released. Components whose removal had not run yet stay in their pools and filters in a degraded state; treat the exception as fatal for that world or `Clear()` it.
:::

## Type indices

`typeIndex` resolves via `ComponentType.TypeOf(typeIndex)` / `ComponentType.NameOf(typeIndex)`:

<<< @/snippets/Guides/WorldEvents.cs#type-names{csharp}

For hooks on a single component type, [component listeners](./component-listeners.md) avoid the world-wide dispatch.
