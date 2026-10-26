# Component Listeners

Typed hooks on one pool, without world-wide dispatch:

<<< @/snippets/Guides/ComponentListeners.cs#hooks{csharp}

<<< @/snippets/Guides/ComponentListeners.cs#register{csharp}

- `OnAdded` runs after the component is stored and filters are updated. The `ref` points at the stored component, so the listener can adjust it.
- `OnRemoved` runs before [`AutoReset`](../concepts/components.md), so the component data is still intact.
- A listener may call its own pool again. From `OnRemoved` it may remove components of the same type, the one being removed included: by destroying its entity, or through a linked entity whose listener removes this one back. Every listener still gets one `OnRemoved` per component, and a cycle of entities that remove each other ends (up to 2.2.1 such a cycle recursed until the stack overflowed).
- A component that a listener removes and adds again, to the same entity or to a new entity that takes the freed slot, is a new component: the outer `Remove` leaves it alone, and an outer `Add` gives its remaining listeners no `OnAdded` for it, since the nested `Add` already notified them.
- Listeners may subscribe and unsubscribe listeners of the pool. The `OnAdded` dispatch of an `Add` starts after the filter and world listeners of that `Add`: a typed listener they unsubscribe gets no `OnAdded`, one they subscribe gets it. The exception is a pool that has no typed listener when the `Add` starts: it notifies none for that `Add`, so a listener subscribed during it gets its first `OnAdded` from the next `Add` (up to 2.2.1 it got this one when the pool tracked changes, belonged to an owning group or held `EventBuffer<T>`). Once a dispatch has started, it goes over the listeners it started with: one unsubscribed by an earlier listener is still called, one subscribed is not. A removal notifies the listeners it started with.
- During `DestroyEntity` the components go one by one, and `OnRemoved` of each still sees the ones not removed yet: `world.Has` and `Get` agree, as during a plain `Remove` (up to 2.2.1 `Has` said `false` for them, so `if (!world.Has<B>(e)) world.Add(e, …)` threw "already has component"). A listener that calls `World.Clear` ends the destroy (up to 2.2.1 the destroy went on and left the world with a negative entity count and an entity that was not alive).
- `OnAdded` may destroy the entity, also one that `CreateEntity`, `CopyEntity` or `EcsEntityView.Spawn` is still building: the rest of its components are not added (up to 2.2.1 `CopyEntity` and `Spawn` put them into the freed slot, where the next entity inherited them), and the returned handle is dead.
- `Warmup` is not hidden from these listeners: they see its temporary entity, with `default(T)` data, added and removed. A listener may destroy it (a death rule firing on the default `Health`): `Warmup` then goes on with a new temporary entity for the remaining types. See [Warmup](./world-lifecycle.md#warmup).

For notifications about every entity and component type, use [world events](./world-events.md). For the Unity-side pattern of notifying views from systems, see [Listener Bridge](../unity/listener-bridge.md).
