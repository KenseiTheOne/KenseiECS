# Component Listeners

Typed hooks on one pool, without world-wide dispatch:

<<< @/snippets/Guides/ComponentListeners.cs#hooks{csharp}

<<< @/snippets/Guides/ComponentListeners.cs#register{csharp}

- `OnAdded` runs after the component is stored and filters are updated. The `ref` points at the stored component, so the listener can adjust it.
- `OnRemoved` runs before [`AutoReset`](../concepts/components.md), so the component data is still intact.

For notifications about every entity and component type, use [world events](./world-events.md). For the Unity-side pattern of notifying views from systems, see [Listener Bridge](../unity/listener-bridge.md).
