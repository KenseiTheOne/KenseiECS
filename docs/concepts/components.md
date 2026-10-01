# Components

All components must be structs implementing `IComponent`:

<<< @/snippets/Concepts/Components.cs#declare{csharp}

## Access

Through the world, by `Entity` handle:

<<< @/snippets/Concepts/Components.cs#access{csharp}

Removing the last component destroys the entity (see [auto-destroy](./entities.md#creating-and-destroying)).

Through the pool, by `int` index. Cache pools in `Init`; filter loops give you the index directly:

<<< @/snippets/Concepts/Components.cs#pool{csharp}

::: warning Pool access is unchecked in release
`ComponentPool<T>.Get(int)` on an entity without the component reads garbage in release and throws under `KENSEI_DEBUG`. See [Release vs. KENSEI_DEBUG](../guides/debug-mode.md).
:::

### Ref lifetime

A `ref T` from `Get` points into the pool's dense array. It is valid until the next `Add` of the same component type (the array may grow) or until that component is removed (swap-remove moves the last element into its slot). Re-acquire the ref after structural changes.

<<< @/snippets/Concepts/Components.cs#stale-ref{csharp}

Changes to other component types do not affect it.

## IAutoReset

Custom cleanup when a component is removed:

<<< @/snippets/Concepts/Components.cs#auto-reset{csharp}

Components without `IAutoReset` are reset to `default(T)` automatically. `AutoReset` is called for the removed component only; the component moved into its slot by swap-remove is untouched. `Warmup` and `Clear` also call it, so it must handle `default(T)`.

The bridge is a cached delegate, AOT/IL2CPP-safe: one boxing allocation per pool at construction, zero allocations per `Remove`. Explicit interface implementations are supported.

## IAutoCopy

Custom deep-copy logic for `CopyEntity`:

<<< @/snippets/Concepts/Components.cs#auto-copy{csharp}

`AutoCopy` is called on a shallow copy of the source component, before it is stored on the new entity; replace the reference fields there. Components without `IAutoCopy` are copied by value (shallow copy).

## Component types

Every component type gets a process-wide index (`ComponentType<T>.Index`). The index resolves back to the type:

<<< @/snippets/Concepts/Components.cs#component-types{csharp}

`GetPool(int)` returns the untyped `ComponentPoolBase`, or `null` if the world has no pool for that type yet.

::: warning
Indices depend on first-touch order and are not stable across runs; do not persist them.
:::

## See also

- [Debug names](./entities.md#debug-names) for entities
- [Component listeners](../guides/component-listeners.md): typed hooks on one pool
- [Change tracking](../guides/change-tracking.md): `Modify` and `ChangedSince`
- [Singletons](../guides/singletons.md) and [OneFrame components](../guides/one-frame.md)
