# Filters

A filter is a cached query: the set of entities that have every `Inc` type, none of the `Exc` types and, if `Any` types are given, at least one of them. Filters are reactive: the world updates them on every structural change, so iterating one never scans the world.

## Building a filter

<<< @/snippets/Concepts/Filters.cs#builder{csharp}

Identical filter constraints return the same `Filter` instance. Build filters in `Init`, not in `Run`: `Filter()` allocates a builder.

`End()` throws `InvalidOperationException`:

- for filters without a single `Inc<T>` or `Any<T>` (exclude-only and empty filters are not supported);
- when the same component type is in both `Inc<T>` and `Exc<T>`, or in both `Any<T>` and `Exc<T>`.

## Any

`Any<T>` matches entities that have **at least one** of the listed types, on top of `Inc`/`Exc`:

<<< @/snippets/Concepts/Filters.cs#any{csharp}

## Static specs

Filters like these as one-liners, with the constraints in the type:

<<< @/snippets/Concepts/Filters.cs#specs{csharp}

`Inc` takes 1-6 types, `Exc` 1-4, `Any` 2-4; `None` fills an unused slot. Specs and builder filters deduplicate against each other.

## Helpers

<<< @/snippets/Concepts/Filters.cs#helpers{csharp}

`First`, `TryGetFirst`, `Single` and `Entities` give `int` slot indices; convert with `world.GetEntity(e)` before storing anything (see [Handles vs. indices](./entities.md#handles-vs-indices)).

## Enter / leave events

<<< @/snippets/Concepts/Filters.cs#listener{csharp}

Callbacks run synchronously inside the structural change; the entity is alive on enter and may already be dying on leave. `RemoveListener` unregisters a listener.

## Iteration contract

- Iteration order is unspecified and changes with structural modifications. Do not rely on it.
- Safe inside `foreach`: destroying the current entity, adding/removing components on the current entity, creating new entities. New entities that match the filter are not visited in the current loop.
- `foreach` does not lock the filter. An exception inside the loop leaves the world consistent.

::: warning Known limitation
Destroying or removing components from a **not-yet-visited** entity may cause an already-visited entity to be visited again (swap-remove). Under `KENSEI_DEBUG` this throws at the moment it would happen. Defer such changes with a [`CommandBuffer`](../guides/command-buffer.md).
:::

For loops that need contiguous component data, see [owning groups](../guides/groups.md).
