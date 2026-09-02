# KenseiECS Performance Benchmarks

All numbers below come from one session on one machine (September 2026): .NET 8.0.14, Windows 11, x64 RyuJIT AVX2, BenchmarkDotNet 0.14.0, default job. Sources live in `Benchmark/`; run them with:

```
dotnet run -c Release --project Benchmark -- --filter '*'
```

**Contestants:**
- **KenseiECS** — sparse set ECS with reactive filters and owning groups
- **LeoEcsLite** (Leopotam.EcsLite 1.0.1) — sparse set ECS, widely used in Unity
- **LeoECS** (Leopotam.Ecs 1.0.1) — legacy version, kept for reference
- **Arch** (2.1.0) — archetype ECS

All frameworks allocate nothing at runtime in every scenario except entity creation (which grows arrays).

---

## Iteration — component traversal

`foreach` over Position + Velocity, read and write, 10,000 entities created in order.

| Framework | 10,000 entities |
|---|---:|
| KenseiECS, filter | 13.9 us |
| KenseiECS, owning group | **5.5 us** |
| LeoECS | 12.8 us |
| LeoEcsLite | 14.0 us |
| Arch | 5.4 us |

A filter loop pays two sparse lookups per entity (`dense -> sparse -> data` for each component) and lands next to LeoEcsLite. An owning group keeps the two pools' dense arrays aligned, so the loop reads `Data1[i]` and `Data2[i]` with no lookup at all and matches Arch's archetype iteration.

---

## Fragmented iteration — after a long session

Same loop, but the world was built by creating 20,000 entities, adding Velocity in shuffled order and destroying half in shuffled order. Filter order, pool order and entity indices no longer line up, which is what a real game looks like after minutes of play.

| Framework | 10,000 entities |
|---|---:|
| KenseiECS, filter | 14.9 us |
| KenseiECS, owning group | **5.5 us** |
| LeoEcsLite | 16.7 us |
| Arch | 5.4 us |

Sparse-set filters lose ~7% (KenseiECS) to ~20% (LeoEcsLite) to cache misses; groups are unaffected because their data stays contiguous whatever the history.

---

## Create Entity — spawning entities with components

Creating N entities with Position + Velocity into a fresh world (includes growing every array).

| Framework | 10,000 entities | Allocated |
|---|---:|---:|
| KenseiECS | 294 us | 1.60 MB |
| LeoECS | 380 us | 2.31 MB |
| LeoEcsLite | 344 us | 1.86 MB |
| Arch | **208 us** | **0.58 MB** |

Arch allocates whole archetype chunks; sparse-set frameworks grow one array per pool. KenseiECS stays ahead of both Leo variants.

---

## Structural changes — adding and removing components

Add Health to all N entities, then remove it from all. No filter watches Health in this scenario.

| Framework | 10,000 entities |
|---|---:|
| KenseiECS | 100 us |
| LeoECS | 145 us |
| LeoEcsLite | **73 us** |
| Arch | 590 us |

LeoEcsLite keeps no per-entity bitmask, so with nothing observing the type it does strictly less work. KenseiECS pays for the mask update and the component count on every change; that bookkeeping is what makes the next two scenarios cheap. Arch migrates the entity between archetypes on every change.

---

## Observed structural changes — filters watching the changed type

Same add + remove, but F filters constrain Health: half of them `Inc<Position, Health, Extra>`, half `Inc<Position> Exc<Health, Extra>`, each with a distinct extra type so none deduplicate.

| Filters watching Health | KenseiECS | LeoEcsLite |
|---:|---:|---:|
| 1 | **126 us** | 204 us |
| 8 | **334 us** | 909 us |
| 32 | **1,083 us** | 4,356 us |

KenseiECS tests one mask word per filter (and skips the test entirely for the half of the filters a change cannot move the entity into); LeoEcsLite walks a `Has` chain per included type for every filter. With 32 filters on the type KenseiECS is 4x faster, and real projects have far more than 32 filters over their hottest components.

---

## Wide masks — 1,024 registered component types

The production shape: 1,024 component types registered, the benchmarked types placed past the 16th mask word, every entity carrying a 17-word mask, three filters spanning two words watching Health.

| Scenario, 10,000 entities | KenseiECS | LeoEcsLite |
|---|---:|---:|
| Iteration (2 comp, `Exc` on a third) | 13.7 us | 14.3 us |
| Structural (add + remove Health, 3 filters) | **243 us** | 341 us |
| Destroy all + create all (2 comp) | **645 us** | 732 us |

Mask width does not touch iteration, and a change on a high-index type costs the same as on a low one: matching reads only the words a filter constrains. Destroying an entity walks its mask words (17 here) instead of every pool, which keeps destroy + create ahead of LeoEcsLite's mask-free design.

---

## Memory footprint

Bytes held by a world after N entities were created and each of T component types was added to at least one high-index entity (every pool's sparse array grows to the highest entity index that ever had the component), plus F filters.

| Types | Filters | 10,000 entities |
|---:|---:|---:|
| 64 | 0 | 3.7 MB |
| 64 | 100 | 4.1 MB |
| 1,024 | 0 | 44.3 MB |
| 1,024 | 100 | 44.9 MB |

The cost model is O(types x entity capacity): ~40 KB per pool at 10,000 entities, dominated by the sparse array. Filters are paged (a page covers 1,024 entity slots and is allocated on first touch), so 100 filters add well under a megabyte.

---

## Game loop — a realistic frame

Movement over Position + Velocity, health regeneration over Health, add a one-frame Damage to 10% of entities, run the damage system, remove Damage.

| Framework | 10,000 entities |
|---|---:|
| KenseiECS | **34 us** |
| LeoEcsLite | 40 us |
| Arch | 78 us |

Per frame at 60 FPS this is 0.2% of the budget. Iteration dominates and structural changes are a minority, which is where the sparse-set + reactive-filter design pays off against archetype migration.

---

## Summary

| Operation | vs LeoEcsLite | vs Arch |
|---|---|---|
| Iteration, filter | on par | 2.5x slower |
| Iteration, owning group | 2.5x faster | on par |
| Fragmented iteration, group | 3x faster | on par |
| Entity creation | 1.2x faster | 1.4x slower |
| Structural changes, unobserved type | 1.4x slower | 5.9x faster |
| Structural changes, 32 filters observing | 4x faster | — |
| 1,024 types: structural | 1.4x faster | — |
| 1,024 types: destroy + create | 1.1x faster | — |
| Game loop | 1.2x faster | 2.3x faster |
| Runtime allocations | 0 B | 0 B |

---

## 2.0 versus the 1.0 core

Same machine, same session, the 1.0 core (commit `903dbf2`) built against the same benchmark sources. 10,000 entities.

| Scenario | 1.0 core | 2.0 | Change |
|---|---:|---:|---|
| Iteration, filter | 13.9 us | 13.9 us | same |
| Iteration, owning group | — | 5.5 us | new, 2.5x faster than the filter |
| Fragmented iteration, filter | 15.9 us | 14.9 us | 6% faster |
| Entity creation | 300 us | 294 us | 2% faster |
| Structural, unobserved type | 105 us | 100-105 us | within noise |
| Structural, 8 filters observing | 381 us | 334 us | 12% faster |
| Structural, 32 filters observing | 1,146 us | 1,083 us | 5% faster |
| 1,024 types: structural, 3 filters | 234 us | 243 us | 4% slower |
| 1,024 types: destroy + create | 700 us | 645 us | 8% faster |
| Game loop | 32 us | 33 us | 4% slower |
| 100 filters, memory (64 types) | 2.6 MB | 0.36 MB | 7x less |
| 100 filters, memory (1,024 types) | 4.0 MB | 0.56 MB | 7x less |

The two small regressions are the price of paged filter sparse arrays (one more indirection when an entity enters or leaves a filter) and of the hook infrastructure behind listeners, groups and change tracking (one predictable branch per add and remove). Everything that iterates is unchanged or faster; everything that changes structure under observation is faster; filters cost a fraction of the memory.

### Where the numbers come from

- **Sparse sets** — O(1) add, remove and has.
- **Owning groups** — pools kept aligned so hot loops read arrays directly.
- **Reactive filters with per-type include/exclude/any lists** — a change only tests the filters it can move the entity into, one mask word each.
- **Multi-word bitmasks** — unlimited component types; destroy and copy walk the mask, not the pools.
- **Paged filter sparse arrays** — filters cost memory proportional to the entities they touch.
- **Struct enumerator with a sentinel terminator** — one bounds check per step, no allocations.
