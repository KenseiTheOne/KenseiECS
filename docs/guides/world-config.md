# WorldConfig

`WorldConfig` sets the starting sizes of a world's internal arrays.

<<< @/snippets/Guides/WorldConfig.cs#config{csharp}

Every array grows on demand; the config only sets starting sizes. Raise them to avoid growth during gameplay when you know the scale up front.

| Field | Sizes | Default |
|---|---|---|
| `InitialEntityCapacity` | entity slots, mask words, filter sparse arrays | 256 |
| `InitialPoolSparseCapacity` | per-pool sparse array | 256 |
| `InitialPoolDenseCapacity` | per-pool and per-filter dense arrays | 64 |
| `InitialPoolCount` | pool registry size | 32 |

## Reserving capacity later

`WorldConfig` is fixed when the world is created. When the scale is known only later (a level loads with its own enemy cap), reserve it:

<<< @/snippets/Guides/WorldConfig.cs#reserve{csharp}

| Call | Grows |
|---|---|
| `world.Reserve(entities)` | entity slots (generations, masks, component counts, free list) and the sparse arrays of every pool and filter; pools and filters created afterwards start with sparse arrays of at least that size |
| `world.Reserve(entities, components)` | the same, plus the dense arrays of every pool (entities, data, change versions) and every filter to at least `components` — again for existing pools and filters and for the ones created afterwards |
| `pool.Reserve(count)` | one pool's dense entity and data arrays (and change versions when tracked) and its sparse array |
| `filter.Reserve(count)` | one filter's dense entity array |

None of them shrinks anything, and all of them allocate, so call them at load time. After `world.Reserve(n, n)`, creating and destroying up to `n` entities with any of the world's components, entering any of its filters, allocates nothing. Creating a pool or a filter still allocates the object itself (and, for a component type in a new block of 64 type indices, one mask word), so let the systems create them at load too.

`components` applies to every pool, singletons included, so it costs `components` slots of every component type. When only a few types reach the peak, reserve the entities on the world and the dense side per pool and filter instead:

<<< @/snippets/Guides/WorldConfig.cs#reserve-one{csharp}

`World.Reserve` on a destroyed world throws `ObjectDisposedException`.

::: tip Did the reserve hold?
Under `KENSEI_DEBUG` every later growth in a dimension `Reserve` covered — an entity slot or sparse array past `entities`, a dense array past `components` — increments `world.GrowthsAfterReserve`. While the [ECS Profiler](../unity/debug-tools.md) records the world, each one is also logged as a `StorageGrown` event (**Grow** filter) naming the array and its old, new and reserved sizes, for example `Position pool dense 512 -> 1024 (reserved 512)`. A dimension that was not reserved (`components` left at 0) is not reported. Without the define the property is always 0 and nothing is checked.
:::

## Defaults

`new World()` uses `WorldConfig.Default()`. A field left at zero (or set negative) falls back to its default, so a config can set only what it needs:

<<< @/snippets/Guides/WorldConfig.cs#partial{csharp}
