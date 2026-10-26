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

The config applies to every pool and filter, singletons included. To size the world for the peak of a level, reserve instead.

## Reserving for the peak

Reserve the world and the component types whose count changes during play, at load time:

<<< @/snippets/Guides/WorldConfig.cs#reserve{csharp}

| Call | Reserves | Costs |
|---|---|---|
| `world.ReserveEntities(n)` | the world's own storage for `n` entity slots: generations, alive flags, component counts, every mask word, the free-slot list. Pools and filters are not touched, except the reserved ones, whose sparse side it extends to `n` | about 13 bytes + 8 bytes per 64 component types, per slot |
| `world.Pool<T>().Reserve(count)` | room for `count` components of `T` on any reserved slot: the dense arrays get exactly `count` slots, the sparse array covers `world.ReservedEntities` (and follows a later `ReserveEntities`). Every filter whose Inc types are now all reserved is reserved with it | `count` × (4 bytes + size of `T`, + 4 with change tracking), plus 4 bytes per reserved slot |
| `filter.Reserve(count)` | room for `count` matching entities and sparse pages for the reserved slots | 4 bytes per entity, plus 4 bytes per reserved slot |

None of the three shrinks an array, each sizes exactly what it was asked for (growth during play doubles instead), and they can be called in any order: a pool reserved before `ReserveEntities` is extended by it, and a filter's reserve from its types follows the reserves as they are now. Only array sizes remember the order: a filter reserved for 5,000 from its types keeps that dense array when a later `ReserveEntities(1500)` brings its reserve down to 1,500, so reserve the entity slots first. `world.ReservedEntities`, `pool.ReservedComponents` and `filter.ReservedCount` tell what was reserved.

After `ReserveEntities(n)` and `Reserve` on every type whose count changes during play, the frames of play allocate nothing while at most `n` entities are alive and no reserved type holds more components than its reserve: creating and destroying entities, adding and removing the reserved components, entering and leaving the filters made of reserved types. What still allocates:

- a type that is not reserved, when it grows: its first component on an entity index past its sparse size (256 by default) grows the sparse array to that index (or to twice its size, when that is more), more components than its dense size (64) double the dense arrays;
- a pool, a filter, or a component type in a new block of 64 type indices (one mask word per slot) created during play: let the systems create them at load. A filter does not create the pools of its types, so create the pools with `world.Pool<T>()`;
- the list pool of `EventBuffer<T>` ([`Prewarm`](./one-frame.md#prewarming-the-list-pool)) and a `CommandBuffer` past its previous peak.

A singleton or anything else created at load needs no reserve: it lives on a low entity index and never grows.

### Filters follow their types

A filter is reserved by its component types when every one of its Inc types is reserved: an entity in the filter has all of them, so the filter never holds more entities than the smallest of their reserves. With all of its Any types reserved too, it never holds more than the sum of theirs, whichever is smaller; a filter without Inc types is reserved by its Any types alone. Exc types do not matter. Once the world reserved its entity slots, a reserve taken from the types is at most `world.ReservedEntities`: no filter holds more entities than are alive, so three Any types reserved for 1,000 each give 1,500 in a world of 1,500 slots, not 3,000, and a later `ReserveEntities` moves the cap. This holds for a filter created after its types were reserved, so the filters that systems create in `Init` are reserved at creation, and for a filter that existed before.

| Filter | Reserved types | `filter.ReservedCount` |
|---|---|---|
| `Inc<Position, Velocity>` | Position 5,000, Velocity 5,000 | 5,000 |
| `Inc<Projectile, Position>`, `Exc<Frozen>` | Projectile 300, Position 5,000 | 300 |
| `Any<Poisoned, Burning>` | Poisoned 40, Burning 60 | 100 |
| `Inc<Position, Burning>` | Position 5,000; Burning not reserved | 0: not reserved |

A filter over a type that is not reserved grows on demand, like that type. Reserve it by hand when it must not:

<<< @/snippets/Guides/WorldConfig.cs#reserve-filter{csharp}

`filter.Reserve(count)` also raises a filter that its types reserved, never lowers it.

### What to reserve

| Component | Reserve |
|---|---|
| on every unit (`Position`, `Velocity`, `Health`) | the peak entity count |
| added and removed during play (`Stunned`, a one-frame event) | its own peak |
| a singleton, level data created at load | nothing |
| rare, on any entity (`Boss`) | its peak, if its first appearance must not allocate: it pays 4 bytes per reserved slot for its sparse array, and so does every filter it completes (the filter's sparse pages: at 50,000 slots about 200 KB for the pool and 200 KB per filter). Otherwise nothing, and it grows once, when it first lands on a high index |

What the choice costs, in two worlds measured on the 2.3.0 prototype (pools, filters and the world's slot arrays after the reserve; [details](#measurements)):

| World | `Reserve(n, n)` (obsolete) | `Reserve(n)` (obsolete) + hot pools and filters | `ReserveEntities` + hot types |
|---|---:|---:|---:|
| 60 types, 528 entities, 40 filters, 10 hot types | 0.76 MB | 0.42 MB | 0.37 MB |
| 1000 types, 50,000 entities, 150 filters, 30 hot types | 916 MB | 256 MB | 44 MB |

Reserving the hot types is smaller than letting everything grow during the first battle (0.40 MB and 53 MB there): growth doubles, a reserve sizes exactly.

### Small worlds: every pool

A world of a few dozen types can reserve every pool that exists, after the systems created theirs. In an `EcsBootstrap` that is the `Init` of an `IInitSystem` registered last in `lateUpdate`: the update systems are initialized first, then the `fixed` phase, then `late`.

<<< @/snippets/Guides/WorldConfig.cs#reserve-all{csharp}

A pool or filter created after the loop is not reserved by it.

### Growth moves arrays

Like any growth, a reserve that grows a pool's or filter's dense arrays moves them: a `ref` from `Get`/`Add`, a `RawData`/`RawEntities` array or a span from `filter.Entities` taken before it is invalid afterwards (a retired filter array reads -1), so take them again after `Reserve`. `pool.Reserve` can grow the filters over its type too. A running `foreach` over the filter is not affected.

If an allocation fails (`OutOfMemoryException`), the world stays consistent and usable: every array is allocated before it replaces the old one, some pools and filters may already have grown, and the reserved sizes are raised only once everything succeeded, so a later reserve does not take the failed size. A size no array can hold fails the same way, also `int.MaxValue` for a filter (`Filter.Reserve`, or the obsolete `World.Reserve` in a world with a filter): its dense array needs one slot more.

Every reserve on a destroyed world throws `ObjectDisposedException`, as does `World.Clear`.

::: tip Did the reserve hold?
Under `KENSEI_DEBUG` every later growth past a reserve — the world's slots or free-slot list past `ReservedEntities`, a reserved pool's dense arrays past its `ReservedComponents` or its sparse array past `ReservedEntities`, a reserved filter's dense array past its `ReservedCount` or its pages past `ReservedEntities` — increments `world.GrowthsAfterReserve`, and `world.FirstGrowthAfterReserve` keeps the first of them: which array of which pool, filter or world grew, its old, new and reserved sizes and the tick (`ToString()` gives, for example, `Position pool dense 512 -> 1024 (reserved 512) at tick 182`). Growth of a pool or filter that nothing reserved is not counted: it is how unreserved types work. While the [ECS Profiler](../unity/debug-tools.md) records the world, each counted growth is a `StorageGrown` event (**Grow** filter) with the same description, and once the world has reserved its entity slots, so is every growth of an unreserved pool or filter, marked `(not reserved)`, as in `Boss pool sparse 256 -> 40001 (not reserved)`: that is how a hot type that was forgotten shows. Without the define the count is always 0, `FirstGrowthAfterReserve` is always empty (`IsEmpty`), and nothing is checked.
:::

### Migrating from `World.Reserve`

`World.Reserve(entities, components)` is obsolete since 2.3.0 and will be removed in 3.0. It still works as in 2.2: it sizes every pool and filter, the ones created later too, for both arguments, and it does not reserve them (`ReservedComponents` stays 0, a later `ReserveEntities` does not extend them).

| 2.2 | 2.3 |
|---|---|
| `world.Reserve(n)` | `world.ReserveEntities(n)`, and `Pool<T>().Reserve(count)` for each type whose count changes during play (their sparse side covers `n`) |
| `world.Reserve(n, n)` | the same; filters over the reserved types follow |
| `world.Reserve(n)` + `pool.Reserve(k)` + `filter.Reserve(k)` | `world.ReserveEntities(n)` + `pool.Reserve(k)`; drop `filter.Reserve` where the filter's Inc types are all reserved |
| `world.Reserve(n, n)` in a small world | `ReserveEntities(n)` and the [loop over `ActivePools`](#small-worlds-every-pool) after the systems initialized |
| `WorldConfig.InitialEntityCapacity` set to the peak, as 2.2.2 advised for large worlds | keep it, or replace it with `ReserveEntities`; only `ReserveEntities` makes the reserved pools and filters follow the slots and `GrowthsAfterReserve` watch them |

Two calls changed meaning: `pool.Reserve(count)` now also covers `world.ReservedEntities` with its sparse array (2.2: the indices below `count`) and reserves the filters over its type; `filter.Reserve(count)` now also allocates the sparse pages for the reserved slots (2.2: the dense array only). Both only add what a peak would grow anyway.

In an `EcsBootstrap`, reserve in `Configure`: the world exists, and the filters that the systems create in `Init` are reserved by their types.

::: tip Checking 0 bytes on a device
On IL2CPP `GC.GetAllocatedBytesForCurrentThread()` always returns 0. Check a battle frame with the Unity Profiler (**GC Alloc**), or with `GC.GetTotalMemory(false)` around the frame while `GarbageCollector.GCMode` is `Disabled` or `Manual`. IL2CPP zeroes every new array, so a reserve is resident memory at once, and its heap gives little back to the system: what a reserve allocates stays in the process, which is why reserving only the hot types matters for the memory limits of iOS and Android.
:::

## Measurements

The numbers above come from the benchmark of the 2.3.0 design (`Benchmark` scenarios `game` and `big`): types and filters created at load, one entity per cold type at a low index, then the reserve, then battle frames that create every unit with the hot types, add and remove the rare types on a few units spread over the index range and destroy everything. Memory is the sum of the pools' and filters' `AllocatedBytes` and the world's slot arrays; it does not depend on the runtime (.NET and IL2CPP give the same bytes). Every reserved variant allocates 0 bytes per battle frame; without any reserve the first battle frame allocates 0.4 MB (`game`) and 90 MB (`big`). Peak resident memory of the IL2CPP build (macOS ARM64) with 300 types and 50,000 entities: 107 MB with `ReserveEntities` and the hot types, 197 MB with `Reserve(n)` and the hot pools and filters, 394 MB with `Reserve(n, n)`, 118 MB without any reserve.

| `big`: 1000 types, 50,000 entities, 150 filters | World | Pools | Filters | Total |
|---|---:|---:|---:|---:|
| no reserve, after the first battle frame (which allocated 90 MB) | 9.2 MB | 27.5 MB | 16.7 MB | 53.4 MB |
| `Reserve(n, n)` (obsolete) | 7.1 MB | 850 MB | 58.6 MB | 916 MB |
| `Reserve(n)` (obsolete) + hot pools and filters | 7.1 MB | 214 MB | 34.6 MB | 256 MB |
| `ReserveEntities(n)` + 30 hot types | 7.1 MB | 22.0 MB | 14.9 MB | 44.0 MB |

## Defaults

`new World()` uses `WorldConfig.Default()`. A field left at zero (or set negative) falls back to its default, so a config can set only what it needs:

<<< @/snippets/Guides/WorldConfig.cs#partial{csharp}
