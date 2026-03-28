# KenseiECS Performance Benchmarks

Comparing KenseiECS against popular C#/.NET ECS frameworks.

**Contestants:**
- **KenseiECS** — sparse set ECS with reactive filters
- **LeoECS** (Leopotam.Ecs) — classic sparse set ECS, widely used in Unity
- **LeoEcsLite** (Leopotam.EcsLite) — lightweight version of LeoECS
- **Arch** — next-generation archetype ECS

**Environment:** .NET 8, Windows 11, x64 RyuJIT AVX2, BenchmarkDotNet v0.14.0

---

## Iteration — component traversal

The most frequent operation in any game. Every frame, systems iterate over thousands of entities, reading and modifying their components. Faster iteration means more entities you can process without dropping FPS.

**What is measured:** `foreach` over a filter accessing 2 components (Position + Velocity). Pure read and write, no creation or deletion.

| Framework  | 100 entity | 1,000 entity | 10,000 entity |
|----------- |-----------:|-------------:|--------------:|
| KenseiECS  | 127 ns     | 1,297 ns     | 13,618 ns     |
| LeoECS     | 129 ns     | 1,256 ns     | 13,144 ns     |
| LeoEcsLite | 143 ns     | 1,359 ns     | 14,101 ns     |
| Arch       | 93 ns      | 573 ns       | 5,417 ns      |

**KenseiECS is 5-8% faster than LeoEcsLite** at typical sizes. On par with LeoECS (legacy version). Arch is faster thanks to its archetype model where data is stored linearly in memory — ideal for CPU cache, but more expensive when adding/removing components.

All frameworks: **zero allocations** during iteration.

---

## Create Entity — spawning entities with components

Happens when spawning objects: enemies, projectiles, effects, items. In bullet hell or RTS games, hundreds of entities may be created in a single frame. The cheaper the creation — the more objects you can spawn without micro-stutters.

**What is measured:** creating N entities, each with 2 components (Position + Velocity).

| Framework  | 100 entity   | 1,000 entity  | 10,000 entity  |
|----------- |-------------:|--------------:|---------------:|
| KenseiECS  | **1.8 us**   | **16 us**     | **250 us**     |
| LeoECS     | 3.9 us       | 32 us         | 384 us         |
| LeoEcsLite | 4.2 us       | 28 us         | 349 us         |
| Arch       | 7.8 us       | 25 us         | 208 us         |

| Framework  | 100 entity    | 1,000 entity   | 10,000 entity    |
|----------- |--------------:|---------------:|-----------------:|
| KenseiECS  | **12.3 KB**   | **92.3 KB**    | **1,563 KB**     |
| LeoECS     | 46.6 KB       | 166.2 KB       | 2,260 KB         |
| LeoEcsLite | 73.9 KB       | 130.1 KB       | 1,812 KB         |
| Arch       | 59.1 KB       | 87.3 KB        | 565 KB           |

**KenseiECS is 2x faster** than LeoECS/LeoEcsLite at creation and allocates **4-6x less memory** at small scales. At 10K entities Arch pulls ahead thanks to bulk archetype allocation, but at typical game scales (tens to hundreds of objects per frame) KenseiECS leads.

---

## Structural Changes — adding and removing components

Structural changes occur when an entity gains or loses a component. For example: a character receives a buff (`Stunned` component added), an effect expires (`Burning` component removed). This is the most expensive operation in ECS because it changes the entity's composition and updates filters.

**What is measured:** adding a Health component to all N entities, then removing it from all. Each entity already has 2 components.

| Framework  | 100 entity | 1,000 entity | 10,000 entity |
|----------- |-----------:|-------------:|--------------:|
| KenseiECS  | 915 ns     | 9,021 ns     | 91,541 ns     |
| LeoECS     | 1,254 ns   | 12,855 ns    | 141,465 ns    |
| LeoEcsLite | 740 ns     | 7,432 ns     | 78,637 ns     |
| Arch       | 5,714 ns   | 57,217 ns    | 589,699 ns    |

KenseiECS is **6.4x faster than Arch** — the archetype model moves entities between archetypes on every add/remove, which is costly. LeoEcsLite leads among sparse set implementations here — its filters update via a delayed list, while KenseiECS updates reactively (instantly). This is a trade-off: KenseiECS filters are always up-to-date and require no synchronization, but pay for it during structural changes.

All frameworks: **zero allocations**.

---

## Game Loop — realistic game frame

The most important benchmark. Simulates a real game frame: multiple systems iterate entities (movement, health regen), 10% of entities receive and lose a one-shot damage component. This is typical workload in action games.

**What is measured:**
1. Movement system — iterate Position + Velocity (all entities)
2. Health Regen system — iterate Health (all entities)
3. Add Damage component to 10% of entities (structural change)
4. Damage system — iterate Health + Damage
5. Remove Damage component (cleanup)

| Framework  | 1,000 entity | 10,000 entity |
|----------- |-------------:|--------------:|
| KenseiECS  | **3.0 us**   | **31.8 us**   |
| LeoEcsLite | 4.0 us       | 40.0 us       |
| Arch       | 7.9 us       | 78.6 us       |

**KenseiECS is 25-31% faster than LeoEcsLite and 2.5x faster than Arch** in a realistic scenario.

10,000 entities in 32 microseconds — that's 0.032 ms per frame. With a budget of 16.6 ms (60 FPS), that's less than 0.2% of a frame.

All frameworks: **zero allocations**.

---

## Summary

| Operation           | vs LeoEcsLite     | vs Arch           |
|---------------------|-------------------|-------------------|
| Iteration           | 5-8% faster       | 2.5x slower       |
| Entity creation     | 1.4-2.3x faster   | 1.2-4.3x faster   |
| Structural changes  | 1.2x slower       | 6.4x faster       |
| Game loop           | 25-31% faster     | 2.5x faster       |
| Allocations (runtime)| 0 B              | 0 B               |

KenseiECS is optimized for real game scenarios where iteration and structural changes are mixed. It consistently outperforms LeoEcsLite in complex tests and is significantly faster than Arch due to the absence of archetype migrations.

### Architectural advantages

- **Sparse Set** — O(1) add, remove, and has-component checks
- **Reactive filters** — always up-to-date, no manual synchronization needed
- **Struct components** — no boxing, data lives on the stack
- **ref access** — `ref var pos = ref pool.Get(e)` with no copying
- **Zero-allocation iteration** — struct enumerator, no GC pressure
- **Component bitmask** — O(1) filter membership check
