# World Events

`IWorldEventListener` receives every entity and component change in a world.

<<< @/snippets/Guides/WorldEvents.cs#listener{csharp}

<<< @/snippets/Guides/WorldEvents.cs#register{csharp}

## Order and guarantees

- `OnEntityCreated` fires after the first component was added (`CreateEntity`) or after all components were copied (`CopyEntity`); `OnComponentAdded` for that first component fires before it. When a listener of one of those components destroys the entity, `OnEntityCreated` does not fire for it (up to 2.2.1 it did, for a dead entity or for the one a listener created in its slot), and `CopyEntity` copies nothing more.
- `OnComponentRemoved` fires while the entity is still alive. If it was the last component, auto-destroy (`OnEntityDestroyed`) follows.
- `OnEntityDestroyed` fires before components are removed; the entity is already dead (`IsAlive` false) but its components are still readable. They are removed one by one, and a listener of each removal still sees the components not removed yet, with `Has` as with `Get` (up to 2.2.1 `Has` said `false` for them while they were still readable). A listener that calls `World.Clear` ends the destroy: `Clear` has removed everything.
- If a [filter listener](../concepts/filters.md#enter-leave-events) removes the component again while it is being added (or destroys the entity), the `OnComponentRemoved` of that removal goes out and the `OnComponentAdded` does not, so the pair never ends with listeners believing the entity has the component. Up to 2.2.1 the `OnComponentAdded` followed the removal. A filter listener that removes the component and adds it back at once is the exception: the `Add` checks whether the entity has the component, which it does again, so after the `OnComponentRemoved` world listeners get the `OnComponentAdded` of the re-added component and then that of the original `Add`, two for the one component the entity has (as up to 2.2.1). Count a component once per entity if your listener can see that.
- The same in reverse: if a filter listener adds the component back while it is being removed (an `Inc<T>` listener in `OnEntityRemoved`, an `Exc<T>` listener in `OnEntityAdded`), the `OnComponentAdded` of that addition goes out and the `OnComponentRemoved` does not; if it destroys the entity and creates one that takes its slot, the new entity's events go out and no `OnComponentRemoved` for the slot follows them. Up to 2.2.1 the `OnComponentRemoved` came last and left listeners believing the entity had lost the component, or blamed the new entity. Events carry only the slot index, so after such a replacement the removal from the old entity is not reported; its `OnEntityDestroyed` was.
- Listeners may add or remove listeners during dispatch; the dispatch continues over the set captured at its start. A world listener that a filter listener subscribes during an `Add` or a `Remove` gets that change's `OnComponentAdded` or `OnComponentRemoved`, since world listeners are called after the filter listeners. Listeners may modify the world, including the entity being destroyed.
- `Warmup` and `Clear` fire no events. Neither does [`WorldSerializer.Load`](./snapshots.md).

::: warning Exceptions
If a listener throws, the exception propagates after the operation is brought to a consistent state: the entity slot is released. Components whose removal had not run yet stay in their pools and filters in a degraded state; treat the exception as fatal for that world or `Clear()` it.
:::

## Type indices

`typeIndex` resolves via `ComponentType.TypeOf(typeIndex)` / `ComponentType.NameOf(typeIndex)`:

<<< @/snippets/Guides/WorldEvents.cs#type-names{csharp}

For hooks on a single component type, [component listeners](./component-listeners.md) avoid the world-wide dispatch.
