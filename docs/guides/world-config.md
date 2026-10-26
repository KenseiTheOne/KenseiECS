# WorldConfig

`WorldConfig` sets the starting sizes of a world's internal arrays.

<<< @/snippets/Guides/WorldConfig.cs#config{csharp}

Every array grows on demand; the config only sets starting sizes. Raise them to avoid growth during gameplay when you know the scale up front.

| Field | Sizes | Default |
|---|---|---|
| `InitialEntityCapacity` | entity slots and mask words; the free-slot list starts at a quarter of it | 256 |
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

None of them shrinks anything, and all of them allocate, so call them at load time. After `world.Reserve(n, n)`, creating and destroying up to `n` entities with any of the world's components, entering any of its filters, allocates nothing. Creating a pool or a filter still allocates the object itself (and, for a component type in a new block of 64 type indices, one mask word), so let the systems create them at load too. A filter does not create the pools of its component types: a pool that first appears with its first `Add` during play allocates there, `Reserve` or not, so create it at load with `world.Pool<T>()`.

`components` applies to every pool, singletons included, so it costs `components` slots of every component type. In one game with about 60 pools, mostly small or single-entity ones, `Reserve(528, 528)` held about 0.6 MB more pool storage after load than `Reserve(528)`. When only a few types reach the peak, reserve the entities on the world and the dense side per pool and filter instead:

<<< @/snippets/Guides/WorldConfig.cs#reserve-one{csharp}

Every `Reserve` sizes the arrays to exactly what it was asked for (growth during play doubles instead). Like any growth, one that grows a pool's or filter's dense arrays moves them: a `ref` from `Get`/`Add`, a `RawData`/`RawEntities` array or a span from `filter.Entities` taken before it is invalid afterwards (a retired filter array reads -1), so take them again after `Reserve`. A running `foreach` over the filter is not affected.

If an allocation fails (`OutOfMemoryException`), the world stays consistent and usable: every array is allocated before it replaces the old one, some pools and filters may already have grown, and the reserved sizes are recorded only once everything succeeded, so pools and filters created afterwards do not try the failed sizes again. A size no array can hold fails the same way, also `int.MaxValue` for a filter (`Filter.Reserve`, or `World.Reserve` in a world with a filter): its dense array needs one slot more. In 2.2.1 that call did nothing, and `World.Reserve` with only filters in the world recorded the size, so every pool created afterwards threw.

`World.Reserve` and `World.Clear` on a destroyed world throw `ObjectDisposedException`.

::: tip Did the reserve hold?
Under `KENSEI_DEBUG` every later growth in a dimension `Reserve` covered — an entity slot or sparse array past `entities`, a dense array past `components` — increments `world.GrowthsAfterReserve`, and `world.FirstGrowthAfterReserve` keeps the first of them: which array of which pool, filter or world grew, its old, new and reserved sizes and the tick (`ToString()` gives, for example, `Position pool dense 512 -> 1024 (reserved 512) at tick 182`). That is enough to find the storage that outgrew the reserve without the profiler. While the [ECS Profiler](../unity/debug-tools.md) records the world, each growth is also logged as a `StorageGrown` event (**Grow** filter) with the same description; a filter is named by its constraints, as in `Filter Inc<Position, Velocity> filter dense 512 -> 1024 (reserved 512)`, and its dense sizes are capacities in entities. A dimension that was not reserved (`components` left at 0) is not reported. Without the define the count is always 0, `FirstGrowthAfterReserve` is always empty (`IsEmpty`), and nothing is checked.
:::

### What reserving entities costs

`entities` is paid by every pool and every filter, whether or not its type ever reaches high entity indices, and again by every pool and filter created later: about 4 bytes × `entities` per pool (its sparse array) and 4 KB per 1024 entities per filter (its sparse pages). For a world with a few dozen types that is small (60 pools at 1,000 entities: about 0.25 MB). It grows with types × entities: 1000 component types at 50,000 entities is about 200 MB of pool sparse arrays, and about 236 MB with 150 filters and the world's own slot arrays on top. Growing on demand allocates only for the pools whose components actually reach high indices: in the same measurement, with most types rare or singletons, about 14 MB.

A future version will let you reserve the world's own entity storage without the sparse side of every pool. Until then, for a world with hundreds of types and tens of thousands of entities:

- Create the world with `WorldConfig.InitialEntityCapacity` set to the peak instead of calling `world.Reserve(entities)`. That sizes the entity slots and mask words; the free-slot list starts at a quarter of it and doubles when more slots are free at once.
- Reserve only the pools that reach the peak with `pool.Reserve(count)` (its dense arrays and its sparse side up to `count`) and their filters with `filter.Reserve(count)`. Rare and singleton pools keep their small sparse arrays; a pool's sparse array grows straight to the highest index it sees.
- A filter still allocates a 4 KB page the first time an entity in a new range of 1024 indices enters it; a warmup frame at the peak load takes those allocations out of gameplay.
- Leave `InitialPoolSparseCapacity` at its default: it applies to every pool too.

## Defaults

`new World()` uses `WorldConfig.Default()`. A field left at zero (or set negative) falls back to its default, so a config can set only what it needs:

<<< @/snippets/Guides/WorldConfig.cs#partial{csharp}
