# KenseiECS Architecture

How the framework works internally. Written for contributors and for users who want to reason about the cost of an operation before they write it. The user-facing API is documented in the [root README](../README.md); this document explains the mechanisms behind that API.

All code discussed here lives in `KenseiECS/Core` and `KenseiECS/Systems`.

## Data model at a glance

```
World
├── entity slots     _generations[i]  _alive[i]  _componentCounts[i]      indexed by entity index
├── component masks  _componentMasks[word][i]                            ulong per (word, entity); 64 types per word
├── free list        _freeIndices[] / _freeCount, _nextIndex             slot recycling stack and high-water mark
├── pool registry    _pools[typeIndex]                                   ComponentPoolBase, created on first Pool<T>()
├── filter registry  _allFilters                                         plus per type index:
│                    _includeFilters[t]  _excludeFilters[t]  _anyFilters[t]
└── world listeners  IWorldEventListener[]                               copy-on-write array

ComponentPool<T>     _sparse[entity] -> dense index      _denseEntities[d] -> entity      _denseData[d] -> T
Filter               paged sparse[entity] -> dense slot  _denseEntities[slot] -> entity   slot 0 = terminator
```

Three kinds of identifiers appear throughout:

- `int` entity index: a slot number. Filters yield these; pools are addressed by them.
- `Entity` handle: `(Index, Generation)`, 8 bytes. `World` methods take these.
- `int` type index: `ComponentType<T>.Index`, process-wide.

## Entity slots

An entity is a slot index into four parallel arrays owned by `World`:

| Array | Meaning |
|---|---|
| `_generations[i]` | Generation currently stamped on the slot. Starts at 1. |
| `_alive[i]` | Whether the slot currently holds a live entity. |
| `_componentCounts[i]` | Number of components on the entity. Drives auto-destroy. |
| `_componentMasks[w][i]` | Bit `t & 63` of word `t >> 6` is set when the entity has component type `t`. |

`_nextIndex` is the high-water mark: slots below it have been handed out at least once. `_freeIndices` is a stack of released slots. `CreateEntityInternal` pops the free stack if it is not empty, otherwise takes `_nextIndex++` and grows every slot array (doubling) when needed.

### Generation changes on reuse, not on free

```csharp
// CreateEntityInternal, free-list branch
index = _freeIndices[--_freeCount];
int generation = _generations[index] + 1;
if (generation == 0) {
    generation = 1;
}
_generations[index] = generation;
```

`DestroyEntity` clears `_alive[i]` and pushes the slot onto the free stack but leaves `_generations[i]` alone. The generation only advances when the slot is handed to a new entity. The consequence:

- `IsAlive(handle)` is `idx < _nextIndex && _alive[idx] && _generations[idx] == handle.Generation`. A handle to a destroyed entity fails on `_alive`; after the slot is reused it fails on the generation.
- `GetEntity(int)` is simply `new Entity(index, _generations[index])`. On a dead slot it returns exactly the handle the dead entity had while alive, and `IsAlive` is false for it. If the generation were bumped on free instead, `GetEntity` on a dead slot would return the handle of whatever entity is created in that slot *next*: a forged handle that starts out dead and later comes alive for an entity the caller never saw. Bumping on reuse closes that hole, which is why the debug layer no longer needs to throw on `GetEntity` for dead slots.

`Entity.Null` is `default(Entity)`, i.e. `(0, 0)`. Generations start at 1 and wrap back to 1, never 0, so `Entity.Null` never matches a real entity in any world.

`World.Clear()` increments the generation of every slot below `_nextIndex` (alive or not), then resets `_nextIndex` and the free stack to zero. Every handle issued before `Clear` therefore fails the generation check afterwards, even if its slot is immediately reused.

## Component type registry

`ComponentType<T>.Index` is a `static readonly int` initialized from `ComponentType.Register(typeof(T))` on first touch of the generic instantiation. Registration is process-wide, guarded by a lock, and stores the reverse map `index -> Type`, so `ComponentType.TypeOf(int)` and `ComponentType.NameOf(int)` resolve any index issued so far. Indices are dense and assigned in first-touch order; they are not stable across runs and must not be persisted.

Because indices are process-wide and not per-world, every `World` in the process shares the same numbering. A world only allocates a pool for a type when `Pool<T>()` is first called on it; `_pools`, the three filter tables and the mask words are sized by the highest type index the world has seen.

## Component pools

`ComponentPool<T>` is a sparse set per component type:

```
_sparse[entityIndex]      -> dense index, or -1
_denseEntities[dense]     -> entityIndex          (ComponentPoolBase)
_denseData[dense]         -> T                     (ComponentPool<T>)
_count                    -> number of live entries; dense arrays have no gaps
```

`Has(int)` is `entityIndex < _sparse.Length && _sparse[entityIndex] != -1`. `Get(int)` is two array reads. Iterating `RawData`/`RawEntities` from `0` to `Count` walks every component of the type contiguously.

### Add

`Add(int entityIndex, T value)` throws if the entity already has the component (in every build), grows the sparse array to cover `entityIndex` and the dense arrays if full, writes the three arrays, increments `_count`, and then notifies `World.OnComponentAdded(entityIndex, TypeIndex)`. After the world has updated masks, counts, filters and world listeners, the pool's own `IComponentListener<T>.OnAdded` callbacks run against the stored value, and the method returns a `ref` into `_denseData`.

The returned `ref` is valid until the next `Add` of the same type (the dense array may be reallocated) or until that component is removed (swap-remove moves another component into its slot).

### Remove and swap-remove

`Remove(int entityIndex)` is a no-op when the component is absent. Otherwise, in order:

1. `IComponentListener<T>.OnRemoved` runs first, before any index is read, because a listener may remove other components of the same type and shift the dense layout. The component data is still intact here.
2. If `T` implements `IAutoReset<T>`, `AutoReset` runs on the component being removed.
3. Swap-remove: the last dense entry (`_count - 1`) is moved into the removed slot, and its owner's sparse entry is repointed. The vacated tail slot is set to `default(T)`, not AutoReset: after the move, the tail is a bitwise duplicate of the live component that was moved, so running `AutoReset` on it would clear reference fields the live copy still uses.
4. `_sparse[entityIndex] = -1`, `_count--`.
5. `World.OnComponentRemoved(entityIndex, TypeIndex)`: mask, count, filters, world listeners, auto-destroy.

The `HasAutoReset` branch is a `static readonly bool` per generic instantiation; the JIT folds it into a constant and drops the dead half of `Remove`.

### AutoReset and AutoCopy bridges

`IAutoReset<T>.AutoReset(ref T)` and `IAutoCopy<T>.AutoCopy(ref T)` are invoked through delegates created once in the pool constructor: `Delegate.CreateDelegate` closed over one boxed `default(T)` and the target method taken from `typeof(T).GetInterfaceMap(...)`. This is one boxing allocation per implemented bridge per pool, no allocation per call, works for explicit interface implementations, and avoids runtime generic instantiation of a value-type bridge, which is not AOT-safe under IL2CPP.

### Growth

Sparse arrays grow to `max(length * 2, entityIndex + 1)` and are filled with -1; dense arrays grow to `max(length * 2, needed)`. Starting sizes come from `WorldConfig.InitialPoolSparseCapacity` and `InitialPoolDenseCapacity`. A pool's sparse array only grows when a high-index entity actually receives the component, so a type that lives on few entities does not pay for the whole world.

### ComponentPoolBase vs ComponentPool<T>

`ComponentPoolBase` holds everything that does not depend on `T`: the sparse array, `_denseEntities`, `_count`, `TypeIndex`, `ComponentType`, `Has`, `Remove` (abstract), the introspection properties (`SparseCapacity`, `DenseCapacity`, `ComponentSize`, `AllocatedBytes`) and the internal `AddDefault`, `Clear`, `CopyTo`. `World` stores every pool as this base (`_pools[typeIndex]`) so that `DestroyEntity`, `CopyEntity`, `Warmup` and `Clear` can walk an entity's mask and call into pools without knowing `T`.

`ComponentPool<T>` is `sealed` so that the fast path in `World.Pool<T>()`, `pools[typeIdx] is ComponentPool<T> pool`, compiles to a single method-table comparison.

`Clear`, `AddDefault` and `CopyTo` are `internal` because each is one step of a larger world-level operation that maintains the surrounding invariants:

- `Clear` empties the pool without notifying `World`; only `World.Clear` calls it, and `World.Clear` resets masks, counts and filters itself. Called on its own it would leave masks and filters pointing at components that no longer exist.
- `AddDefault` exists for `Warmup`, which runs it on a temporary entity with events suppressed.
- `CopyTo` is the per-pool step of `CopyEntity`, which allocates the destination slot first and dispatches a single `OnEntityCreated` after every pool has copied.

`Remove` stays public because it notifies `World` and is safe to call directly (that is how `World.Remove<T>` and `OneFrameCleanup<T>` call it).

## Component bitmasks

The mask is stored word-major: `_componentMasks[word]` is a `ulong[]` indexed by entity, and the world keeps `_maskWordCount` such arrays. Type index `t` lives in word `t >> 6`, bit `t & 63`. Two things grow it:

- `GrowEntityCapacity` resizes every word array when a new slot is allocated past the current capacity.
- `EnsureMaskWords` allocates a fresh `ulong[entityCapacity]` for each new word when a pool is created for a type in a new 64-type block, or when a filter is registered that constrains such a block.

Word-major layout means a single-word filter test reads one array, and adding a new 64-type block never touches existing arrays. With about 1000 component types the mask is 16 words, i.e. 128 bytes per entity slot.

### Has<T>

`World.Has<T>(entity)` reads the mask only:

```csharp
int typeIdx = ComponentType<T>.Index;
int word = typeIdx >> 6;
if (word >= _maskWordCount) {
    return false;
}
ulong[] maskWord = _componentMasks[word];
if ((uint)entity.Index >= (uint)maskWord.Length) {
    return false;
}
return (maskWord[entity.Index] & (1UL << (typeIdx & 63))) != 0;
```

It never creates a pool and never touches pool memory.

### DestroyEntity and CopyEntity walk the mask

Both iterate only the set bits of the entity's words:

```csharp
for (int w = 0; w < _maskWordCount; w++) {
    ulong mask = _componentMasks[w][idx];
    while (mask != 0) {
        int bit = TrailingZeroCount(mask);
        int typeIdx = (w << 6) | bit;
        _pools[typeIdx].Remove(idx);      // or .CopyTo(srcIdx, dstIdx)
        mask &= mask - 1;
    }
}
```

`TrailingZeroCount` is `BitOperations.TrailingZeroCount` on .NET 5+ and a De Bruijn table lookup elsewhere (Unity, netstandard2.1). The cost is proportional to the number of mask words plus the number of components on the entity, not to the number of registered types. `GetComponentTypes` uses the same walk.

## Filters

A `Filter` is a sparse set of entity indices with no payload, kept up to date by `World` on every structural change.

### Constraints and precomputed masks

The builder produces three sorted, deduplicated `int[]` of type indices: `IncludedTypeIndices`, `ExcludedTypeIndices`, `AnyTypeIndices`. The constructor turns them into three `ulong[]` masks sized to the highest constrained type index, plus:

- `ActiveWords`: the words in which any of the three masks has a bit, so matching skips words the filter does not constrain.
- `SingleWord` / `SingleIncludeMask` / `SingleExcludeMask` / `SingleAnyMask`: when exactly one word is active, matching reads these scalar fields instead of walking arrays. `SingleWord` is -1 for multi-word filters.

A filter is single-word when all of its constrained types fall in the same 64-type block. With many component types this depends on first-touch registration order, not on how many types the filter names.

### The all-ones Any mask trick

The single-word test is:

```csharp
ulong entityWord = _componentMasks[w][entityIndex];
return (entityWord & include) == include
    && (entityWord & exclude) == 0
    && (entityWord & any) != 0;
```

When the filter has no `Any` constraint, `SingleAnyMask` is `ulong.MaxValue`. `End()` guarantees that a filter without `Any` has at least one `Inc`, so a word that passes the include test is non-zero, and `(entityWord & ulong.MaxValue) != 0` is true. The Any test therefore costs one AND per match with no branch on `HasAny`. The multi-word path accumulates `anyHit` across `ActiveWords` and returns `anyHit || !filter.HasAny`.

### Sparse set with a 1-based dense array and a slot-0 sentinel

```
_denseEntities[0]        = FreeSlot (-1), permanent terminator
_denseEntities[1.._count] = entity indices, no gaps
_denseEntities[_count+1..] = FreeSlot
sparse[entity]           = dense slot (1-based), or -1
```

`Entities` is `new ReadOnlySpan<int>(_denseEntities, 1, _count)`. `First()`/`Single()` read slot 1.

The sparse side is paged: `_sparsePages[entityIndex >> 10][entityIndex & 1023]`, with 1024-entry pages allocated on first touch and filled with -1. Iteration never reads the sparse side, so the extra indirection is not on the hot path, and a filter matching a few entities in a world with many slots pays only for the pages it touches instead of an `int` per entity slot. `Contains(int)` returns false for an index whose page was never allocated.

`AddEntity` returns early if the entity is already present. This matters because the world may test the same filter twice for one change (for example a type that is in a filter's `Any` list and a different type in its `Inc` list), and because `UpdateFilterForEntity` calls `AddEntity` without knowing the current membership.

`RemoveEntity` swap-removes: the entity in the last slot moves into the freed slot, the freed last slot is written with `FreeSlot`, the sparse entry becomes -1, `_count--`.

### The reverse enumerator

`Filter.Enumerator` is a `ref struct` holding the filter, a `Span<int>` over `_denseEntities`, `_index` and `_current`. It starts at `_count + 1` and walks down:

```csharp
public bool MoveNext() {
    int i = _index - 1;
    int entity = _entities[i];
    if (entity == FreeSlot) {
        Filter filter = _filter;
        _entities = filter._denseEntities;
        int count = filter._count;
        if (i > count) {
            i = count;
        }
        if (i == 0) {
            _index = 1;
            return false;
        }
        entity = _entities[i];
    }

    _index = i;
    _current = entity;
    return true;
}
```

The normal step is one span load, one compare against `FreeSlot`, two field writes. Reading `FreeSlot` means one of three things, all handled by the same slow path:

1. The terminator at slot 0: normal end of iteration. `_index` is parked at 1 so extra `MoveNext` calls keep probing the terminator instead of reading out of range.
2. The live range shrank below the cursor because several entities were removed in one loop step: re-read `_count`, clamp `i` to it, continue from there.
3. The cached span points at a retired array (see growth below): re-cache and continue at the same slot, which holds the same entity in the new array.

Why the hot path is call-free: `MoveNext` makes no method calls on `this` and reads nothing from the heap except the element. Passing a struct by reference to a helper exposes its address, which prevents the JIT from promoting the enumerator's fields to registers; keeping the slow path inline avoids that. Using a `Span<int>` rather than the raw array keeps the length inside the promoted struct, so the bounds check on `_entities[i]` does not become a heap load of `array.Length` after a re-cache. No per-step version or count check is needed because the sentinel doubles as the end-of-range check.

Why reverse: swap-remove fills a freed slot with the entity from the *highest* live slot. Iterating from high to low, that entity has already been visited, so removing the current entity (or anything at or above the cursor) never causes a double visit. New entities are appended above the cursor and are not visited in the current loop.

### Poisoning the retired array on growth

`GrowDense` allocates the bigger array, copies, fills the new tail with `FreeSlot`, then fills the *old* array entirely with `FreeSlot`:

```csharp
_denseEntities = grown;
Array.Fill(old, FreeSlot, 0, old.Length);
```

An enumerator still holding a span over the old array lands on the `FreeSlot` path on its next step and re-caches the current array. Without poisoning it would keep reading stale entity indices from the retired array.

### Per-type filter lists and which are tested on add vs. remove

`World` keeps three jagged tables indexed by type index: `_includeFilters[t]`, `_excludeFilters[t]`, `_anyFilters[t]`. `RegisterFilter` appends the filter to the list of every type it constrains, under the matching kind. On a structural change of type `t` only those lists are visited:

| Event | Inc filters of `t` | Exc filters of `t` | Any filters of `t` |
|---|---|---|---|
| component `t` added | full mask test, `AddEntity` on match | `RemoveEntity` without a test (an excluded type appeared, the entity cannot match) | full mask test, `AddEntity` on match |
| component `t` removed | `RemoveEntity` without a test (a required type is gone) | full mask test, `AddEntity` on match | full test, `AddEntity` or `RemoveEntity` (another Any type may still satisfy it) |

Adding `t` can only move an entity *into* filters that require `t` and *out of* filters that exclude it; removing `t` only the reverse. Half of the updates therefore skip the mask test entirely. The tables are arrays, not lists, because this loop runs on every `Add` and `Remove`.

`EntityMatchesFilter` performs no bounds check against `_maskWordCount`: `RegisterFilter` calls `EnsureMaskWords` for every word the filter constrains, so the mask arrays it reads always exist, and an unregistered type's word simply reads zeros.

### Registration, deduplication, PopulateFilter

`FilterBuilder.End()`:

1. Throws if there is neither an `Inc` nor an `Any` constraint.
2. Throws if a type is in both `Inc` and `Exc`, or in both `Any` and `Exc`.
3. Drops any `Any` type that is also in `Inc` (redundant: a required type always satisfies "at least one of").
4. Sorts all three lists and calls `World.RegisterFilter`.

`RegisterFilter` compares the three sorted arrays against every registered filter and returns the existing instance on a match. Order of `Inc<A>().Inc<B>()` versus `Inc<B>().Inc<A>()` does not matter. Static specs (`world.Filter<Inc<A, B>, Exc<C>>()`) go through the same builder, so they deduplicate against builder-made filters. The comparison is linear in the number of registered filters, which is why filters belong in `Init`, not `Run`.

For a new filter, `PopulateFilter` first checks that every `Inc` type has a pool; if one does not, nothing can match yet and the scan is skipped. Otherwise it walks every slot below `_nextIndex`, tests alive entities against the filter and adds the matches. Filters are never unregistered; they live until `World.Destroy`.

## Structural change flow

### `world.Add<T>(entity, value)`

1. Under `KENSEI_DEBUG`, `ValidateHandle`: the handle must be alive, or the slot must be mid-destroy (see the debug section).
2. `Pool<T>()`: fast path type check on `_pools[typeIdx]`; on first use `CreatePool<T>` grows `_pools` and the mask words to cover the type index and allocates the pool.
3. `pool.Add(entity.Index, value)`:
   - Under `KENSEI_DEBUG`, the slot must be alive or dying.
   - Throws `InvalidOperationException` if the component is already present.
   - Grows sparse/dense arrays as needed, stores the value, `_count++`.
   - `World.OnComponentAdded`: `_componentCounts[e]++`, sets the mask bit, then visits `_includeFilters[t]` (test, add), `_excludeFilters[t]` (remove), `_anyFilters[t]` (test, add). Each `AddEntity`/`RemoveEntity` synchronously fires `IFilterListener` callbacks. Then every `IWorldEventListener.OnComponentAdded(e, t)` runs, unless events are suppressed by `Warmup`.
   - `IComponentListener<T>.OnAdded(e, ref data)` for the pool's listeners.
   - Returns `ref _denseData[dense]`.

`CreateEntity<T>(T)` is `CreateEntityInternal()` (allocate a live, empty slot), then this `Add`, then `OnEntityCreated` to world listeners. `CopyEntity` is `CreateEntityInternal()`, then `CopyTo` per set mask bit of the source (each of which goes through `pool.Add` and the flow above), then a single `OnEntityCreated`.

### `world.Remove<T>(entity)`

1. Under `KENSEI_DEBUG`, `ValidateHandle`.
2. If no pool exists for `T`, return. `Remove` never creates a pool.
3. `pool.Remove(entity.Index)`:
   - Return if the component is absent.
   - `IComponentListener<T>.OnRemoved(e, ref data)` with the data intact.
   - `AutoReset` on the removed component, if implemented.
   - Swap-remove, tail defaulted, sparse cleared, `_count--`.
   - `World.OnComponentRemoved`: decrements `_componentCounts[e]` (only if the entity is alive; see the drain loop below), clears the mask bit, visits `_includeFilters[t]` (remove), `_excludeFilters[t]` (test, add), `_anyFilters[t]` (test, add or remove), then dispatches `OnComponentRemoved(e, t)` to world listeners while the entity is still alive.
   - Auto-destroy: if `_componentCounts[e] == 0` and the entity is alive, `DestroyEntityInternal(e)` runs before `Remove` returns.

### `world.DestroyEntity(entity)`

```csharp
public void DestroyEntity(Entity entity) {
    if (!IsAlive(entity)) {
        return;
    }
    DestroyEntityInternal(entity.Index);
}
```

`DestroyEntityInternal(idx)`:

1. `_alive[idx] = false` before anything else. A listener that calls `DestroyEntity` on the same entity re-entrantly hits the `IsAlive` check and becomes a no-op; a listener that calls `Remove` on it does not decrement the count (see step 3).
2. `OnEntityDestroyed(idx)` to world listeners. The entity is dead (`IsAlive` false) but its components are still readable.
3. `DrainComponents(idx)`, in a `finally` so it runs even if a listener threw:

   ```csharp
   do {
       _componentCounts[idx] = 0;
       for (int w = 0; w < _maskWordCount; w++) {
           ulong mask = _componentMasks[w][idx];
           if (mask == 0) {
               continue;
           }
           _componentMasks[w][idx] = 0;
           while (mask != 0) {
               int bit = TrailingZeroCount(mask);
               _pools[(w << 6) | bit].Remove(idx);
               mask &= mask - 1;
           }
       }
   } while (_componentCounts[idx] != 0);
   ```

   Each `Remove` runs the full pool flow (pool listeners, AutoReset, filter updates, world `OnComponentRemoved`). Listeners may re-add components to the dying entity. `OnComponentAdded` increments `_componentCounts` unconditionally, while `OnComponentRemoved` skips the decrement for a dead entity, so after a pass a non-zero count means "something was re-added" and the loop runs again, reading one `int` instead of rescanning every mask word. The mask word is zeroed before its bits are iterated, so a component re-added during the pass sets a fresh bit that the next pass sees. Under `KENSEI_DEBUG` more than 1000 passes throws.
4. `ReleaseSlot(idx)`, in an inner `finally`: count and all mask words zeroed, `_aliveCount--`, slot pushed onto the free stack. Under `KENSEI_DEBUG` the debug name is dropped and `_destroyDepth` is decremented.

`Warmup` uses the same machinery on a temporary entity with `_suppressEvents` set, so world listeners and the profiler never see it.

## Event ordering and exception safety

Ordering follows directly from the flows above:

- On `Add`: filter listeners (per filter, in table order), then `IWorldEventListener.OnComponentAdded`, then `IComponentListener<T>.OnAdded`. For `CreateEntity`/`CopyEntity`, `OnEntityCreated` comes last, after every component is in place, so listeners never observe an entity without components.
- On `Remove`: `IComponentListener<T>.OnRemoved` (data intact), then `AutoReset`, then the swap-remove, then filter listeners, then `IWorldEventListener.OnComponentRemoved` with the entity still alive, then auto-destroy if it was the last component.
- On `DestroyEntity`: `OnEntityDestroyed` first, with the dead flag set and components still readable; then one `Remove` flow per component.
- `Warmup` and `Clear` fire no events: `Warmup` sets `_suppressEvents`; `Clear` never calls `Remove` at all, it resets pools and filters directly.

Listener lists (`World`, `Filter`, `ComponentPool<T>`) are copy-on-write arrays. A dispatch loops over the array it captured at its start, so a listener that adds or removes listeners mid-dispatch neither shifts the loop nor gets skipped or invoked twice.

Exception safety:

- A world listener or `AutoReset` that throws during `DestroyEntity` propagates after the `finally` chain has drained what it could and released the slot. Components whose `Remove` did not run stay in their pools and in filters (the pool's sparse entry and filter membership were never cleared for them), while the slot's mask and count are reset. This is a degraded state that a throwing listener is expected to be fixed for, not a supported control flow; before the fix the slot was never released at all.
- A world listener that throws during `Add` runs after the component and mask are stored and the filters are updated; the world is consistent, only the pool listeners are skipped.
- A pool listener that throws during `Remove` runs before anything is modified; the component stays.
- `SystemsRunner.Init` records progress in `_initProgress`; if a system's `Init` throws, the runner stays uninitialized and the next `Init` resumes with that system. `Run` cleans OneFrame components in a `finally`, so a throwing system cannot leave events to be processed twice. `Destroy` resets the initialized flag before calling `IDestroySystem.Destroy` in reverse order.
- `CommandBuffer.Playback` clears the buffer in a `finally`; a throwing command discards the rest.

## Built on the core

- `CommandBuffer` records `(Op, PendingId, Entity, TypeIndex, PayloadIndex)` structs and stores payloads in per-type `PayloadStore<T>` arrays indexed by type index, so nothing is boxed. `Playback` resolves `PendingEntity` ids through `world.CreateEntity`, skips commands whose `Entity` is dead, and applies `Add` via `pool.Add` (throws on duplicate), `Set` via overwrite-or-add, `Remove` via `world.GetPool(typeIndex)?.Remove` (no pool creation) and `Destroy` via `world.DestroyEntity`. After the first frame the arrays are reused; `Clear` only wipes payload arrays for types that contain references, so the GC can collect them.
- `EventBuffer<T>` is a component holding a `List<T>` rented from a static per-type `ListPool<T>`; its `AutoReset` returns the list, so `OneFrame<EventBuffer<T>>` allocates nothing after warmup. `world.AddEvent` appends to an existing buffer or adds a new one.
- `Listeners<T>` is a component holding a `List<T>` of interface implementations; `Subscribe` adds the component on first use, `Unsubscribe` keeps it even when empty so the entity is not auto-destroyed by unsubscribing, and `HasListeners` reports false for an empty list.
- `SystemsRunner` keeps separate lists of `IInitSystem`, `IRunSystem` and `IDestroySystem` so `Run` does no type checks. A named child runner is registered as an init/destroy participant but excluded from `_runSystems`, which is what makes it a separate phase; an unnamed child is an ordinary `IRunSystem` in the parent's list. Only the root's parameterless `Run()` advances the world tick. `OneFrameCleanup<T>` walks `pool.RawEntities` from `Count - 1` down to 0 calling `pool.Remove`, which is safe with swap-remove; `DelHere<T>` registers the same object as a run system at that position. On Unity every run system is wrapped in a `ProfilerMarker`; under `KENSEI_DEBUG` `Stopwatch` timings are recorded per system.

## The KENSEI_DEBUG layer

Everything in this section is inside `#if KENSEI_DEBUG` and does not exist in a release build: no fields, no checks, no `Dispose` on the enumerator, no profiler hooks. The cost in release is zero.

What is checked:

| Where | Check |
|---|---|
| `World.Add/Get/Has/Remove` | `ValidateHandle`: the handle must be alive, or its slot must be mid-destroy (`_destroyDepth > 0`) with a matching generation. Dead and stale handles throw. |
| `World.CopyEntity`, `GetComponentTypes`, `GetComponentCount`, `SetName` | Throw on a dead entity (release returns `Entity.Null` / reads the slot). |
| `World.DrainComponents` | Throws after 1000 passes when a listener keeps re-adding components to a dying entity. |
| `ComponentPool<T>.Add(int)` | `IsSlotAcceptingComponents`: the slot must be below `_nextIndex` and alive or dying. An `int` carries no generation, so this is the strongest check possible. |
| `ComponentPool<T>.Get(int)` | Throws when the component is absent. |
| `WorldListenerExtensions.Subscribe/Unsubscribe` | Throw on a dead entity. |
| `Filter` | The iteration guard below. |
| `SystemsRunner` | `Add` after `Init`, `Run` before `Init`, `Init`/`Run` with a different `World` or explicitly different `SharedData`, nested runner with a different `World`/`SharedData`, unknown names in `SetActive`/`IsActive`/`GetRunner`. Per-system `LastRunMs`/`PeakRunMs`. |
| Tooling | `EcsProfiler` hooks on create/destroy/add/remove, `WorldDebugView` as `DebuggerTypeProxy` for `World`, `GetRaw`/`SetRaw` on pools, entity debug names. |

### The iteration guard

Each `Filter` keeps `_debugCursors` (one slot per live enumerator, innermost last) and `_debugIterators` (depth). The enumerator constructor records its depth and writes its starting cursor (`_count + 1`); every successful `MoveNext` writes the slot it is on; `Dispose` decrements the depth. `foreach` calls `Dispose` on a `ref struct` enumerator through pattern-based disposal, so the guard is released on normal exit, `break` and exceptions.

`RemoveEntity` checks every live cursor before the swap:

```csharp
if (denseIdx < cursor && lastIdx >= cursor) {
    throw new InvalidOperationException(...);
}
```

Reverse iteration has visited every slot above the cursor and is currently on the cursor. Removing the entity at `denseIdx` moves the entity from `lastIdx` into `denseIdx`. The move is harmful only when the destination has not been visited yet (`denseIdx < cursor`) and the moved entity has (`lastIdx >= cursor`): it would be yielded again. Every other combination is safe: removing the current entity (`denseIdx == cursor`) or a visited one, or removing an unvisited entity when the last slot is also unvisited (`lastIdx < cursor`), which happens after the loop has already shrunk the live range. Nested loops over the same filter are covered because every enumerator's cursor is checked.

## Performance characteristics

`W` is the number of mask words (`ceil(types / 64)`), `C` the number of components on the entity, `F(t)` the number of filters that constrain type `t`, `L` the number of listeners that fire.

| Operation | Cost | Notes |
|---|---|---|
| `CreateEntity<T>(T)` | O(1) amortized + one `Add` | Pops the free stack or bumps the high-water mark; may grow every slot array and mask word. |
| `Add<T>` | O(1) + O(F(t) + L) | Each filter test is one or a few `ulong` operations. Sparse/dense growth is amortized. |
| `Remove<T>` | O(1) + O(F(t) + L) | Swap-remove. Plus a `DestroyEntity` if it was the last component. |
| `Get<T>` / `pool.Get(int)` | O(1) | Sparse read, then dense read. |
| `Has<T>` | O(1) | One mask word read; no pool access. |
| `pool.Has(int)` | O(1) | Sparse read. |
| `IsAlive`, `GetEntity(int)`, `GetComponentCount` | O(1) | |
| `DestroyEntity` | O(W + C × (F + L)) | Mask walk over set bits only; each component pays its own `Remove`. |
| `CopyEntity` | O(W + C) + C × `Add` | Same walk; `AutoCopy` per component that implements it. |
| `GetComponentTypes` | O(W + C) | |
| `foreach (int e in filter)` | O(matches) | One span load per step, zero allocation. |
| `Filter.Count/IsEmpty/Contains/First/Single` | O(1) | |
| `Filter().…End()`, `Filter<Spec>()` | O(registered filters) + O(entity slots) | Dedup scan, then `PopulateFilter`. Allocates. Build in `Init`. |
| `Pool<T>()` | O(1) | Type check on `_pools[typeIdx]`; first call allocates the pool. |
| `GetSingleton<T>` | O(1) | `pool.RawData[0]` after a count check. |
| `CommandBuffer.Playback` | O(commands) | Each command pays the corresponding world operation. |
| `SystemsRunner.Run` | O(systems) + Σ OneFrame pool counts | |
| `Warmup` | O(registered pools) × (`Add` + `Remove`) | |
| `Clear` | O(high-water slots × W) + Σ pool counts + Σ filter counts | Pools and filters reset through their dense lists, not their sparse arrays. |

What scales with the number of component types versus the entity's own components:

- Per-frame operations (`Add`, `Remove`, `Get`, `Has`, iteration, `DestroyEntity`, `CopyEntity`) never loop over registered types. `DestroyEntity` and `CopyEntity` touch `W` words and then only set bits.
- `Warmup`, `World.Clear`, `World.Destroy` and `ActivePools` loop over `_pools`, i.e. over registered types. They are setup and teardown operations.
- Filter matching is O(active words), at most `W`; single-word filters read three scalars.

Memory, per element:

- Entity slot: 4 bytes generation, 1 byte alive, 4 bytes count, 8 × W bytes of masks.
- Pool: 4 bytes per sparse slot (grows to cover the highest entity index that ever held the component, starting at `InitialPoolSparseCapacity`), plus (4 + `sizeof(T)`) per dense slot (starts at `InitialPoolDenseCapacity`, doubles with the component count). One boxed `default(T)` per implemented bridge interface (`IAutoReset<T>`, `IAutoCopy<T>`). `ComponentPoolBase.AllocatedBytes` reports the array sizes.
- Filter: 4 bytes per dense slot plus one, 4 KB per touched 1024-entity page, three mask arrays of `ceil(maxConstrainedType / 64)` words. `Filter.AllocatedBytes` reports it.
- Registry: `_pools` and the three filter tables hold one reference per type index up to the highest seen.

## Known limitations and design decisions

**No archetypes.** Storage is one sparse set per component type. Structural changes are O(1) and never move an entity's other components; a `ref T` stays valid across adds of *other* types; filters are cheap sparse sets updated incrementally; there is no combinatorial explosion of archetypes when many optional components exist. The price is iteration: reading two components per entity is two sparse-to-dense indirections into two unrelated arrays, which loses to chunked archetype layouts on pure iteration (see the numbers in the README) and wins on structural changes and mixed frames.

**`CreateEntity` requires a component.** The invariant is "alive if and only if it has at least one component". An empty entity would be invisible to every filter, invisible to world listeners (`OnEntityCreated` fires after the first component), and would be destroyed by the first `Remove` anyway. Requiring the component at creation removes the empty state entirely, so there is no leak class to detect and no "empty entity" check to run. `CommandBuffer.CreateEntity<T>(T)` mirrors the rule.

**Auto-destroy on last component removal.** Same invariant from the other side. It makes "event entities" free: an entity whose only component is a one-frame component disappears in the cleanup. The one place where this surprised users, unsubscribing the last listener, is handled by keeping the empty `Listeners<T>` component.

**Single-threaded.** `World`, pools and filters have no synchronization. Reading `RawData`/`RawEntities` from several threads is safe only while no structural change happens anywhere. `ComponentType<T>.Index` registration is thread-safe. `EventBuffer<T>`'s list pool is a static per-type stack shared by all worlds in the process and is not thread-safe either.

**Iteration order is unspecified** and changes with every swap-remove. Removing a not-yet-visited entity from the filter being iterated can double-visit an already visited entity (thrown under `KENSEI_DEBUG`); defer such changes with a `CommandBuffer`.

**Exclude-only and empty filters are rejected.** Membership is driven solely by add/remove of constrained types, and `PopulateFilter` needs at least one required or alternative type to reason about; add an `Inc` for a component the target entities always have.

**`ref T` invalidation.** A `ref` from `Get`/`Add` is valid until the next `Add` of the same type or the removal of that component.

**Type indices are process-wide** and depend on first-touch order. They are not stable across runs and must not be serialized. Every world's registries are sized by the highest type index it has seen, not by how many types it uses.

**`Entity` carries no world id.** Using a handle from one world on another is undefined; the generation check catches it only by accident.

**Filters cannot be unregistered.** They live until `World.Destroy`. Static specs are limited to `Inc` 1-6, `Exc` 1-4, `Any` 2-4 types; the builder has no limit.
