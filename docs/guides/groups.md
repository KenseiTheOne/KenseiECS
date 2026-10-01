# Groups

A [filter](../concepts/filters.md) finds entities; each component access still goes through the pool's sparse array. An **owning group** removes that indirection: it keeps the dense arrays of its pools aligned, so every member sits at the same index in each pool, packed at the front.

<<< @/snippets/Guides/Groups.cs#moving{csharp}

`Data1`, `Data2` (up to `Data4`) are `Span<T>` views over the first `Count` elements of each owned pool's dense array, so the loop reads component data directly — no sparse lookups. Groups exist for two, three and four component types: `world.Group<T1, T2>()`, `world.Group<T1, T2, T3>()`, `world.Group<T1, T2, T3, T4>()`.

## Rules

- Membership is exact and follows `Add`/`Remove`/`DestroyEntity`/`Clear`.
- A pool can be owned by **one** group; a second group over the same type throws. Filters over grouped types keep working.
- Calling `world.Group<...>()` again with the same types returns the existing group.
- `Entities[i]` gives the entity index at each position.
- Iterate in reverse when destroying members or removing owned components inside the loop: the last member is swapped into the freed slot.
- Adding a component to a grouped pool costs one extra swap per owned pool when the entity becomes a member.

<<< @/snippets/Guides/Groups.cs#reverse{csharp}

::: tip Group or filter?
A group pays off for hot loops over the same set of components. Since a pool can belong to only one group, pick the combination your heaviest system iterates and keep using filters elsewhere.
:::

Groups can be injected by the [source generator](./source-generator.md) with `[Group]`, and their spans can be split across threads with [`ParallelRunner`](./threading.md#parallelrunner). The internals are described in [Architecture](../architecture.md).
