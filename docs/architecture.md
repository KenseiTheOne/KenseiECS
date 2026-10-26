# KenseiECS Architecture

How the framework works internally. Written for contributors and for users who want to reason about the cost of an operation before they write it. The user-facing API is documented in the [guide](guide/introduction.md) and the [core concepts](concepts/entities.md); this document explains the mechanisms behind that API.

All code discussed here lives in `KenseiECS/Core`, `KenseiECS/Systems`, `KenseiECS/Unity` and `KenseiECS.Generators`.

## Data model at a glance

```
World
├── entity slots     _generations[i]  _alive[i]  _componentCounts[i]      indexed by entity index
├── component masks  _componentMasks[word][i]                            ulong per (word, entity); 64 types per word
├── free list        _freeIndices[] / _freeCount, _nextIndex             slot recycling stack and high-water mark
├── pool registry    _pools[typeIndex]                                   ComponentPoolBase, created on first Pool<T>()
├── filter registry  _allFilters                                         plus _filtersByType[typeIndex] -> { Include[], Exclude[], Any[] } or null
├── groups           _groups                                             owning groups; each owned pool points back via _ownerGroup
├── world listeners  IWorldEventListener[]                               copy-on-write; _dispatch = listeners present and not suppressed
└── counters         _tick, _changeVersion

ComponentPool<T>     _sparse[entity] -> dense index      _denseEntities[d] -> entity      _denseData[d] -> T
                     _changedVersions[d] (opt-in)        _addHooks / _removeHooks (listeners, owner group, tracking; list owner / running frames)
Filter               paged sparse[entity] -> dense slot  _denseEntities[slot] -> entity   slot 0 = terminator
Group                _pools[]  _count                    members occupy dense 0.._count-1 of every owned pool, same order
```

Three kinds of identifiers appear throughout:

- `int` entity index: a slot number. Filters and groups yield these; pools are addressed by them.
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

<<< @/snippets/Reference/Architecture.cs#generation-on-reuse{csharp}

`DestroyEntity` clears `_alive[i]` and pushes the slot onto the free stack but leaves `_generations[i]` alone. The generation only advances when the slot is handed to a new entity. The consequence:

- `IsAlive(handle)` is `idx < _nextIndex && _alive[idx] && _generations[idx] == handle.Generation`. A handle to a destroyed entity fails on `_alive`; after the slot is reused it fails on the generation.
- `GetEntity(int)` is simply `new Entity(index, _generations[index])`. On a dead slot it returns exactly the handle the dead entity had while alive, and `IsAlive` is false for it. If the generation were bumped on free instead, `GetEntity` on a dead slot would return the handle of whatever entity is created in that slot *next*: a forged handle that starts out dead and later comes alive for an entity the caller never saw. Bumping on reuse closes that hole, which is why the debug layer no longer needs to throw on `GetEntity` for dead slots.
- `TryGetEntity(int, out Entity)` is the bounds-checked form for arbitrary integers: it returns true and the handle only when `index < _nextIndex` and the slot is alive, and `Entity.Null` otherwise. `GetEntity` has no bounds check and is meant for indices a filter, group or pool just yielded.

`Entity.Null` is `default(Entity)`, i.e. `(0, 0)`. Generations start at 1 and wrap back to 1, never 0, so `Entity.Null` never matches a real entity in any world.

`World.Clear()` increments the generation of every slot below `_nextIndex` (alive or not), then resets `_nextIndex` and the free stack to zero. Every handle issued before `Clear` therefore fails the generation check afterwards, even if its slot is immediately reused.

## Component type registry

`ComponentType<T>.Index` is a `static readonly int` initialized from `ComponentType.Register(typeof(T))` on first touch of the generic instantiation. Registration is process-wide, guarded by a lock, and stores the reverse map `index -> Type`, so `ComponentType.TypeOf(int)` and `ComponentType.NameOf(int)` resolve any index issued so far. Indices are dense and assigned in first-touch order; they are not stable across runs and must not be persisted. Snapshots identify types by name for this reason.

Because indices are process-wide and not per-world, every `World` in the process shares the same numbering. A world only allocates a pool for a type when `Pool<T>()` is first called on it; `_pools`, `_filtersByType` and the mask words are sized by the highest type index the world has seen.

## Component pools

`ComponentPool<T>` is a sparse set per component type:

```
_sparse[entityIndex]      -> dense index, or -1
_denseEntities[dense]     -> entityIndex          (ComponentPoolBase)
_denseData[dense]         -> T                     (ComponentPool<T>)
_changedVersions[dense]   -> int                   (ComponentPool<T>, null unless TrackChanges was called)
_count                    -> number of live entries; dense arrays have no gaps
```

`Has(int)` is `entityIndex < _sparse.Length && _sparse[entityIndex] != -1`. `Get(int)` is two array reads. Iterating `RawData`/`RawEntities` from `0` to `Count` walks every component of the type contiguously.

### Hooks and the `_addHooks` / `_removeHooks` fast path

Three optional features hang off a pool, each represented by a nullable field: `IComponentListener<T>` listeners (`_listeners`), an owning group (`_ownerGroup`) and change tracking (`_changedVersions`). A fourth is fixed by the type: `_listOwner` is set for components that hold a pooled list (`EventBuffer<T>`, see [built on the core](#built-on-the-core)), and only `Add` needs it. One bool per direction is kept equal to "at least one of the three is set", plus the list owner for `_addHooks` and "a hooked call of this pool is running" for `_removeHooks`: `AddListener` and `TrackChanges` set both, the constructor sets `_addHooks` for a list owner, `SetOwnerGroup`, the `RemoveListener` that empties the list and the end of the outermost running call recompute them through `RefreshHooks`.

`Add` tests `_addHooks` and `Remove` tests `_removeHooks`. A pool with no hooks runs the plain path with one predictable branch and no further field reads; a pool with hooks calls the `NoInlining` helpers `AddHooked` / `RemoveHooked`, which examine the fields individually (`RemoveHooked` hands removals with typed listeners to `RemoveNotified`, which holds the `try`/`finally` of the hook frames, so a tracking or group pool does not pay for it). Most pools in a world have no hooks, so the common structural change stays at its minimum cost regardless of how many features exist.

### Re-entrant calls: hook frames

Typed listeners run user code in the middle of an `Add` or `Remove` of their own pool, and that code may call the pool again: remove the same component (an `OnRemoved` that destroys its entity), remove a linked entity's component that removes this one in turn, or remove the component and add it again, to the same entity or to a new one that takes the freed slot. A pool with listeners therefore keeps a small stack of frames, one per `Add` or `Remove` whose typed listeners are running: the entity, whether it is a removal, a `Done` flag and, for a removal, the next listener to notify. The innermost frame lives in plain fields (`_topEntity`, `_topNext`, `_topRemoving`, `_topDone`), so a call that does not nest touches no array; a nested call moves it into `_frames` (four entries, allocated by the first `AddListener`, doubled for deeper nesting) and back when it returns.

- A `Remove` that finds a frame for its entity that is a removal and not `Done` joins it: it continues the notification from the frame's next listener, over the listener array the removal started with, and performs the removal. Linked entities that remove each other (A removes B, B removes A) therefore end: the second `Remove` of A joins A's frame instead of starting a new notification, so every listener gets one `OnRemoved` per component and a `Has`-guarded cycle cannot recurse forever.
- The swap-remove marks every frame of its entity `Done`. A call whose frame is `Done` stops after the listener that returned: the component it was working on is gone, and whatever the slot holds now (the same entity's re-added component, or a new entity's) is a different component, announced by its own `Add`. The outer `Remove` does not remove it, and the outer `Add` gives the rest of its listeners no `OnAdded`. `World.Clear` from a listener marks every frame `Done` too, and ends a `DestroyEntity` in progress (see [`world.DestroyEntity`](#world-destroyentity-entity)).
- Listeners that unsubscribe during a removal do not shift it: `AddListener` and `RemoveListener` record the array being replaced in every running removal that has not recorded one yet, so the common case stores no array reference (and pays no GC write barrier).
- While a frame runs, `_removeHooks` stays set even if the last listener unsubscribed, so every nested `Remove` of the pool goes through the hooked path and keeps the frames exact.

A pool without listeners never pushes a frame: no user code runs inside its `Add`/`Remove` before the swap-remove, and filter and world listeners run after it.

### Add

`Add(int entityIndex, T value)` throws if the entity already has the component (in every build), grows the sparse array to cover `entityIndex` and the dense arrays if full, writes the three arrays and increments `_count`. Then:

- Without hooks: `World.OnComponentAdded(entityIndex, TypeIndex)` and return a `ref` to the component. `OnComponentAdded` tells whether a filter or world listener ran and, if one did, whether the entity still has the component: when none ran, nothing can have moved it and the `ref` is taken from the dense index `Add` wrote. While no filter of that type has listeners and no world listener is registered, no user code can run there: `OnComponentAdded` updates the filters without any re-check and reads nothing else; otherwise it reads the slot's generation first and re-checks the entity after each filter whose listeners ran. Each type's entry in `_filtersByType` counts the filters listing it that have listeners (`Filter.AddListener` and `RemoveListener` update every type of the filter when it gets its first or loses its last listener), so a listened filter of one type does not slow down the `Add` of the others.
- With hooks, `AddHooked`, in this order: for a list owner, check the stored value's list (below); stamp `_changedVersions[dense]` with `world.NextChangeVersion()` if tracking; `group.OnAdded(entityIndex)` if owned; with typed listeners, read the slot's generation and push an `Add` frame; `World.OnComponentAdded` (masks, counts, filters, world listeners); `IComponentListener<T>.OnAdded` with a `ref` to the stored value, over the listener array read after the world update, so a typed listener that a filter or world listener unsubscribed gets nothing and one it subscribed is notified; pop the frame; return the `ref`. A pool without typed listeners when the `Add` starts pushes no frame and sends no `OnAdded` for it: a typed listener that a filter or world listener subscribes during that `Add` gets its first `OnAdded` from the next one.

Every callback on the way (group, filter and world listeners, typed listeners) may move the component, by adding or removing this type elsewhere, or remove it again, even destroy the entity and let a new entity take its slot. So the dense index is re-read from `_sparse` before each typed listener and before returning the `ref`. Once the component is removed (the frame is `Done`), the remaining typed listeners get no `OnAdded`, also when the slot has the type again. `Add` returns a `ref` to a static scratch value (`_orphan`, reset to `default` each time) when the component is gone or the slot's generation changed, i.e. the entity was destroyed and the slot reused: writes through it land nowhere instead of on whichever entity took the freed slot. If a listener removed the component and added it to the same entity again, the `ref` points at the entity's new component.

The returned `ref` points at a dense slot, not at the entity. It is valid until the next `Add` of the same type to any entity (the dense array may be reallocated), or the next `Remove` of the same type from any entity (swap-remove moves the last component into the freed slot and clears the tail, so a `ref` to the entity that was last now points past `_count`). In a pool owned by a group, an `Add` or `Remove` of any of the group's types can also swap dense slots of every owned pool, for whichever entities occupy them.

### Remove and swap-remove

`Remove(int entityIndex)` is a no-op when the component is absent. Otherwise, in order:

1. With hooks, `RemoveHooked` (with typed listeners, `RemoveNotified`): `IComponentListener<T>.OnRemoved` runs first, before any index is read, because a listener may remove other components of the same type and shift the dense layout; the data is still intact here. The removal runs in a frame ([hook frames](#re-entrant-calls-hook-frames)): a listener that removes this very component again (an `OnRemoved` that destroys its own entity, or a linked entity that removes it back) joins the frame, continues the notification from the next listener and performs the removal; back in the outer `Remove`, the frame is `Done` and it stops. Every listener gets one `OnRemoved`, and the component is removed once. Then `group.OnRemoving(entityIndex)` if owned, which moves the entity to the end of the member range in every owned pool while it still holds every owned component. Then, if tracking, the version of the last dense entry is copied into the slot about to be freed (`versions[_sparse[e]] = versions[_count - 1]`), since that entry is the one the swap-remove will move there.
2. If `T` implements `IAutoReset<T>`, `AutoReset` runs on the component being removed.
3. Swap-remove: the last dense entry (`_count - 1`) is moved into the removed slot, and its owner's sparse entry is repointed. The vacated tail slot is set to `default(T)`, not AutoReset: after the move, the tail is a bitwise duplicate of the live component that was moved, so running `AutoReset` on it would clear reference fields the live copy still uses.
4. `_sparse[entityIndex] = -1`, `_count--`. With frames running, every frame of the entity is marked `Done`.
5. `World.OnComponentRemoved(entityIndex, TypeIndex)`: mask, count, filters, world listeners, auto-destroy.

The AutoReset branch tests the instance field `_autoReset` (the delegate bound at pool creation) for null. A `static readonly bool` would fold into a constant under the JIT, but IL2CPP turns each access into a class-init check plus a field load through the generic context.

### SwapDense

`SwapDense(int a, int b)` exchanges two dense slots in full: `_denseEntities`, `_denseData`, `_changedVersions` when present, and the two sparse entries. It is `internal` and used only by owning groups. It is a no-op when `a == b`.

### AutoReset and AutoCopy bridges

`IAutoReset<T>.AutoReset(ref T)` and `IAutoCopy<T>.AutoCopy(ref T)` are invoked through delegates created once in the pool constructor: `Delegate.CreateDelegate` closed over one boxed `default(T)` and the target method taken from `typeof(T).GetInterfaceMap(...)`. This is one boxing allocation per implemented bridge per pool, no allocation per call, works for explicit interface implementations, and avoids runtime generic instantiation of a value-type bridge, which is not AOT-safe under IL2CPP.

### Growth

Sparse arrays grow to `max(length * 2, entityIndex + 1)` and are filled with -1; dense arrays (entities, data and versions when present) grow to `max(length * 2, needed)`. `Reserve` sizes them exactly instead, for pools and filters alike: it states the need, and doubling would overshoot it. Starting sizes come from `WorldConfig.InitialPoolSparseCapacity` and `InitialPoolDenseCapacity`. A pool's sparse array only grows when a high-index entity actually receives the component, so a type that lives on few entities does not pay for the whole world.

Parallel arrays are allocated before any of them is replaced, both when a pool's dense arrays grow and when the world's slot arrays do (`GrowEntityCapacity`): an `OutOfMemoryException` leaves them all at the old size rather than some grown and some not. Growth moves the data to new arrays, so a `ref` or a `RawData`/`RawEntities` array taken before it no longer reaches the pool.

### ComponentPoolBase vs `ComponentPool<T>`

`ComponentPoolBase` holds everything that does not depend on `T`: the sparse array, `_denseEntities`, `_count`, `_ownerGroup`, `TypeIndex`, `ComponentType`, `Has`, `Remove` (abstract), the introspection properties (`SparseCapacity`, `DenseCapacity`, `ComponentSize`, `AllocatedBytes`) and the internal `GetDenseIndex`, `SwapDense`, `SetOwnerGroup`, `AddDefault`, `Clear`, `CopyTo`, `WriteComponents`, `ReadComponent`. `World`, `Group` and `WorldSerializer` work through this base so that `DestroyEntity`, `CopyEntity`, `Warmup`, `Clear`, group swaps and snapshots can operate on pools without knowing `T`.

`ComponentPool<T>` is `sealed` so that the fast path in `World.Pool<T>()`, `pools[typeIdx] is ComponentPool<T> pool`, compiles to a single method-table comparison.

`Clear`, `AddDefault`, `CopyTo`, `SwapDense`, `WriteComponents` and `ReadComponent` are `internal` because each is one step of a larger operation that maintains the surrounding invariants:

- `Clear` empties the pool without notifying `World`; only `World.Clear` calls it, and `World.Clear` resets masks, counts, filters and groups itself. Called on its own it would leave masks and filters pointing at components that no longer exist.
- `AddDefault` exists for `Warmup`, which runs it on a temporary entity with events suppressed.
- `CopyTo` is the per-pool step of `CopyEntity`, which allocates the destination slot first and dispatches a single `OnEntityCreated` after every pool has copied.
- `SwapDense` permutes dense slots; only a group knows which permutation keeps the alignment invariant.
- `WriteComponents`/`ReadComponent` are the per-pool steps of `WorldSerializer.Save`/`Load`, which own the stream format and the entity slots.

`Remove` stays public because it notifies `World` and is safe to call directly (that is how `World.Remove<T>`, `OneFrameCleanup<T>` and `CommandBuffer` call it).

## Component bitmasks

The mask is stored word-major: `_componentMasks[word]` is a `ulong[]` indexed by entity, and the world keeps `_maskWordCount` such arrays. Type index `t` lives in word `t >> 6`, bit `t & 63`. Two things grow it:

- `GrowEntityCapacity` resizes every word array when a new slot is allocated past the current capacity. `World.Reserve(n)` runs it ahead of time, sizes the free-slot list for `n`, grows the sparse array of every pool and allocates the sparse pages of every filter for indices below `n`; pools and filters created later get the same sparse size. That is about `4 × n` bytes per pool and 4 KB per 1024 indices per filter, whether or not the type ever reaches high indices (see [World config](guides/world-config.md#what-reserving-entities-costs)). The reserved sizes are recorded after everything was allocated, so a `Reserve` that fails does not make later pools and filters try the same allocation.
- `EnsureMaskWords` allocates a fresh `ulong[entityCapacity]` for each new word when a pool is created for a type in a new 64-type block, or when a filter is registered that constrains such a block.

Word-major layout means a single-word filter test reads one array, and adding a new 64-type block never touches existing arrays. With about 1000 component types the mask is 16 words, i.e. 128 bytes per entity slot.

### `Has<T>`

`World.Has<T>(entity)` reads the mask only:

<<< @/snippets/Reference/Architecture.cs#has{csharp}

It never creates a pool and never touches pool memory.

### DestroyEntity and CopyEntity walk the mask

Both iterate only the set bits of the entity's words:

<<< @/snippets/Reference/Architecture.cs#mask-walk{csharp}

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

<<< @/snippets/Reference/Architecture.cs#single-word-test{csharp}

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

<<< @/snippets/Reference/Architecture.cs#move-next{csharp}

The normal step is one span load, one compare against `FreeSlot`, two field writes. Reading `FreeSlot` means one of three things, all handled by the same slow path:

1. The terminator at slot 0: normal end of iteration. `_index` is parked at 1 so extra `MoveNext` calls keep probing the terminator instead of reading out of range.
2. The live range shrank below the cursor because several entities were removed in one loop step: re-read `_count`, clamp `i` to it, continue from there.
3. The cached span points at a retired array (see growth below): re-cache and continue at the same slot, which holds the same entity in the new array.

Why the hot path is call-free: `MoveNext` makes no method calls on `this` and reads nothing from the heap except the element. Passing a struct by reference to a helper exposes its address, which prevents the JIT from promoting the enumerator's fields to registers; keeping the slow path inline avoids that. Using a `Span<int>` rather than the raw array keeps the length inside the promoted struct, so the bounds check on `_entities[i]` does not become a heap load of `array.Length` after a re-cache. No per-step version or count check is needed because the sentinel doubles as the end-of-range check.

Why reverse: swap-remove fills a freed slot with the entity from the *highest* live slot. Iterating from high to low, that entity has already been visited, so removing the current entity (or anything at or above the cursor) never causes a double visit. New entities are appended above the cursor and are not visited in the current loop.

### Poisoning the retired array on growth

`GrowDense` allocates the bigger array, copies, fills the new tail with `FreeSlot`, then fills the *old* array entirely with `FreeSlot`:

<<< @/snippets/Reference/Architecture.cs#poison{csharp}

An enumerator still holding a span over the old array lands on the `FreeSlot` path on its next step and re-caches the current array. Without poisoning it would keep reading stale entity indices from the retired array.

### The per-type table and which lists are tested on add vs. remove

`World._filtersByType` is an array indexed by type index. Each entry is either `null`, for a type no filter mentions, or a `TypeFilters` object with three arrays: `Include`, `Exclude`, `Any`. `RegisterFilter` creates the entry on demand (`TypeFiltersFor`) and appends the filter to the array matching each constraint kind. Keeping the three lists in one object means a structural change looks up one entry and tests it for `null` once; for the many types no filter constrains, that single branch is the entire filter cost.

When the entry exists, `UpdateFiltersOnAdd` / `UpdateFiltersOnRemove` walk the three arrays:

| Event | `Include` filters of `t` | `Exclude` filters of `t` | `Any` filters of `t` |
|---|---|---|---|
| component `t` added | full mask test, `AddEntity` on match | `RemoveEntity` without a test (an excluded type appeared, the entity cannot match) | full mask test, `AddEntity` on match |
| component `t` removed | `RemoveEntity` without a test (a required type is gone) | full mask test, `AddEntity` on match | full test, `AddEntity` or `RemoveEntity` (another Any type may still satisfy it) |

Adding `t` can only move an entity *into* filters that require `t` and *out of* filters that exclude it; removing `t` only the reverse. Half of the updates therefore skip the mask test entirely. The lists are arrays, not `List<Filter>`, because this loop runs on every `Add` and `Remove`.

The untested half relies on `t` staying put, and a filter listener can break that: an `OnEntityRemoved` that adds `t` back during a removal, or an `OnEntityAdded`/`OnEntityRemoved` that removes `t` again (or destroys the entity, whose slot a new entity may then take) during an add. The nested call has already updated every filter for the entity, so the outer loop must not undo it. `AddEntity` and `RemoveEntity` return whether listeners ran; only then is the entity re-checked: on remove, the `Include` loop stops when `t` is back; on add, the update stops when the entity no longer has `t` or the slot's generation changed, before the `Exclude` filters would drop an entity that matches them. Without listeners the loops cost what they did before.

`EntityMatchesFilter` performs no bounds check against `_maskWordCount`: `RegisterFilter` calls `EnsureMaskWords` for every word the filter constrains, so the mask arrays it reads always exist, and an unregistered type's word simply reads zeros.

### Registration, deduplication, PopulateFilter

`FilterBuilder.End()`:

1. Throws if there is neither an `Inc` nor an `Any` constraint.
2. Throws if a type is in both `Inc` and `Exc`, or in both `Any` and `Exc`.
3. Drops any `Any` type that is also in `Inc` (redundant: a required type always satisfies "at least one of").
4. Sorts all three lists and calls `World.RegisterFilter`.

`RegisterFilter` compares the three sorted arrays against every registered filter and returns the existing instance on a match. Order of `Inc<A>().Inc<B>()` versus `Inc<B>().Inc<A>()` does not matter. Static specs (`world.Filter<Inc<A, B>, Exc<C>>()`) and generated `Init` code go through the same builder, so they deduplicate against builder-made filters. The comparison is linear in the number of registered filters, which is why filters belong in `Init`, not `Run`.

For a new filter, `PopulateFilter` first checks that every `Inc` type has a pool; if one does not, nothing can match yet and the scan is skipped. Otherwise it walks every slot below `_nextIndex`, tests alive entities against the filter and adds the matches. Filters are never unregistered; they live until `World.Destroy`.

## Owning groups

A filter finds entities; reading their components still goes through each pool's sparse array. An owning group removes that indirection for a fixed set of component types by rearranging the pools themselves. `world.Group<T1, T2>()` (also three and four types) returns a `Group<T1, T2>` whose `Data1`, `Data2` and `Entities` spans can be indexed in lockstep.

### The alignment invariant

A group holds its owned pools (`ComponentPoolBase[] _pools`) and a member count `_count`. The invariant it maintains at all times:

> In every owned pool, dense slots `0 .. _count-1` hold the components of exactly the entities that have *all* owned components, in the same entity order in every pool.

Members are packed at the front; entities that have some but not all of the owned types sit in the remaining slots `_count .. pool.Count-1` in no particular order. From this:

- `Entities` is `_pools[0].RawEntities[0 .. _count)`, `Data1` is `_p1.RawData[0 .. Count)`, and so on. `Data1[i]`, `Data2[i]` and `Entities[i]` belong to the same entity.
- Iteration is a plain loop over spans: no sparse lookup, no mask test, one bounds check per span access.
- `Contains(e)` (internal) is `pools[0].Has(e) && pools[0].GetDenseIndex(e) < _count`. Checking the first pool is enough because a member sits at the same slot in every pool.

Because the group permutes dense slots, the order of `RawData`/`RawEntities` in an owned pool is no longer insertion order: members come first. The two arrays stay parallel, and sparse entries are always repointed, so `pool.Get(e)` and filter loops are unaffected.

### OnAdded: joining the group

`AddHooked` calls `group.OnAdded(entityIndex)` immediately after the pool has stored the new component, before filters, world listeners and pool listeners run:

1. If any owned pool lacks the entity, return: not a member yet.
2. If `pools[0].GetDenseIndex(e) < _count`, return: already a member.
3. `slot = _count++`; for every owned pool, `pool.SwapDense(pool.GetDenseIndex(e), slot)`.

Step 3 moves the entity's component into the first non-member slot of each pool and moves whatever non-member was there into the entity's old slot. `AddHooked` then re-reads the dense index from `_sparse`, so the `ref` it returns and hands to pool listeners points at the moved component.

### OnRemoving: leaving the group

`RemoveHooked` calls `group.OnRemoving(entityIndex)` after pool listeners and before `AutoReset` and the swap-remove, while the entity still holds every owned component:

1. If not `Contains(e)`, return.
2. `last = --_count`; for every owned pool, `pool.SwapDense(pool.GetDenseIndex(e), last)`.

The entity now sits at slot `last` in every owned pool, which is the first non-member slot, and the member that used to be last has taken its place. The pool that is actually removing the component then swap-removes from slot `last` as usual: the pool's final element (a non-member, or the component itself) lands there, outside the member range. In the other owned pools the entity's components stay at slot `last` among the non-members, which is exactly where an entity that lacks one owned type belongs.

`DestroyEntity` drains components one pool at a time; the first owned pool's `Remove` ends the membership, the remaining owned pools see `!Contains` and do nothing.

### Populate

`RegisterGroup` throws if any pool already has an owner or if a type is listed twice, sets `_ownerGroup` on every pool (which sets `_addHooks` and `_removeHooks`), adds the group to `_groups` and calls `Populate()`. `Populate` picks the pool with the fewest components and calls `OnAdded` for each of its dense entities, walking forward. A hit swaps the member to slot `_count`, which is at or below the cursor; the non-member displaced to the cursor's slot was already visited, so the forward walk still sees every entity exactly once. Cost: O(smallest pool count × P), P being the number of owned pools.

### Interaction with filters, Clear, Warmup and snapshots

- Filters store entity indices and never look at dense order, so a filter over grouped types keeps working, and destroying entities from inside a filter loop keeps the group exact (`Group_FilterAndGroup_Coexist` in the tests).
- `World.Clear` sets every group's `_count` to 0 after emptying the pools.
- `Warmup` adds a default of every registered type to its temporary entity, which makes it a member of every group whose types are all registered; destroying it removes it again, so no group keeps the dummy. If a listener destroys the temporary entity, `Warmup` checks its handle before every type and continues on a fresh one, never on an entity the listener created in the freed slot; a type a listener already added is skipped.
- `WorldSerializer.Load` restores components through `pool.Add`, so groups that exist before `Load` fill normally.

### Why a pool belongs to one group

The invariant fixes the dense position of every member in the pool. Two groups owning the same pool would each demand that the array be packed by their own membership, and both orders cannot hold at once. `RegisterGroup` therefore rejects a pool that already has an owner. `world.Group<A, B>()` returns the existing group only for the same types in the same order (`OwnsExactly` compares position by position); asking for `Group<B, A>()` afterwards throws, because the pools are owned.

### Destroying members inside a group loop

Iterate downward. Destroying the member at index `i` swaps it with the last member (at an index `>= i`) and then swap-removes within the pool, which touches only slots `>= i`; slots below `i` are untouched, so the loop continues correctly. A forward loop would skip the member that was moved into slot `i`.

### Cost per structural change

- `Add` of an owned type: P `Has` checks; if the entity thereby gains all owned types, P `SwapDense` calls, each moving two `int`s, two `T` values, two version entries when tracked, and two sparse writes. `Add` of a type nobody owns: unchanged.
- `Remove` of an owned type from a member: P swaps plus the ordinary swap-remove. From a non-member: one `Contains`.
- Iteration: O(members), contiguous, no indirection.

## Change tracking

Change tracking is opt-in per pool. `TrackChanges()` allocates `_changedVersions`, an `int[]` parallel to the dense arrays (resized with them in `GrowDense`), stamps every existing component with one fresh version so they all count as "changed now", and sets `_addHooks` and `_removeHooks`. `TracksChanges` reports the state.

### Per-slot versions and the world-wide counter

`World._changeVersion` is a monotonic counter: `NextChangeVersion()` returns `++_changeVersion`, and `World.ChangeVersion` reads the current value. Three operations take the next value and write it at the component's dense slot:

- `Add` on a tracking pool (inside `AddHooked`, before the group swap so the value travels with the component).
- `Modify(entityIndex)`, which stamps and returns the `ref`.
- `MarkChanged(entityIndex)`, which stamps without returning anything.

`ChangedVersion(entityIndex)` reads the entry and throws `InvalidOperationException` if the pool does not track. `ChangedSince(entityIndex, version)` is `ChangedVersion(entityIndex) > version`.

The consumer pattern is to store `world.ChangeVersion` at the end of a system's `Run` and pass it to `ChangedSince` the next time. Because every write takes a distinct, increasing number, a bookmark taken at any point in a frame separates "written before" from "written after" exactly, whether the producer runs earlier or later in the pipeline than the consumer. A per-tick stamp could not tell a write made earlier in the same frame (already handled) from one made later (not yet handled). `World.Clear` resets the tick but not the change counter, so bookmarks stay meaningful across it.

### Versions follow the component

Versions are stored per dense slot, so every operation that moves components moves versions with them:

- `Remove`: `RemoveHooked` copies `versions[_count - 1]` (the entry of the last component, which the swap-remove is about to move into the freed slot) into the freed slot's entry. The removed component's own version is discarded with it; the stale value left at the tail is overwritten by the next `Add`.
- `SwapDense`: swaps the two version entries along with the data, so a group joining or leaving keeps each component's version (`Group_ChangeTracking_FollowsSwaps` in the tests).
- `GrowDense`: resizes the version array with the dense arrays.

### Why Get stays untracked

`Get` is the read path and by far the most frequent pool operation. Tracking it would put a counter increment and an array store on every read, and, since a `ref` return cannot tell a read from a write, would mark every read as a change and make `ChangedSince` meaningless. Writes therefore opt in: `Modify` for read-modify-write, `MarkChanged` after a write done some other way. Writes through `Get`, `RawData`, group `Data` spans or the inspector's `SetRaw` are invisible to tracking unless followed by `MarkChanged`. `CommandBuffer.Set` on an existing component and a `CopyEntity` that overwrites a component write through `Modify`, so they count as changes.

Cost: 4 bytes per dense slot on tracking pools; one store plus one counter increment per `Add`, `Modify` and `MarkChanged`; two reads per `ChangedSince`. Pools that do not track pay nothing beyond the shared `_addHooks` / `_removeHooks` branch.

## Structural change flow

### `world.Add<T>(entity, value)`

1. Under `KENSEI_DEBUG`, `ValidateHandle`: the handle must be alive, or be the entity whose `DestroyEntity` is running (see the debug section).
2. `Pool<T>()`: fast path type check on `_pools[typeIdx]`; on first use `CreatePool<T>` grows `_pools` and the mask words to cover the type index and allocates the pool.
3. `pool.Add(entity.Index, value)`:
   - Under `KENSEI_DEBUG`, the slot must be alive or dying.
   - Throws `InvalidOperationException` if the component is already present.
   - Grows sparse/dense arrays as needed, stores the value, `_count++`.
   - If `_addHooks`: list ownership check, version stamp, group `OnAdded` (may swap the component to the front), then the steps below, then `IComponentListener<T>.OnAdded`.
   - `World.OnComponentAdded`: `_componentCounts[e]++`, sets the mask bit, looks up `_filtersByType[t]`; if it exists, `Include` (test, add), `Exclude` (remove), `Any` (test, add), each `AddEntity`/`RemoveEntity` firing `IFilterListener` callbacks synchronously; after a filter whose listeners ran, the update stops if the entity lost `t` or the slot was reused. Then, if `_dispatch`, every `IWorldEventListener.OnComponentAdded(e, t)`, unless a filter listener already took `t` away (its `OnComponentRemoved` went out instead).
   - Returns `ref _denseData[dense]`, or the scratch value when the component is gone or the slot holds another entity.

`CreateEntity<T>(T)` is `CreateEntityInternal()` (allocate a live, empty slot), then this `Add`, then `OnEntityCreated` to world listeners. `CopyEntity` is `CreateEntityInternal()`, then `CopyTo` per set mask bit of the source (each of which goes through `pool.Add` and the flow above), then a single `OnEntityCreated`.

### `world.Remove<T>(entity)`

1. Under `KENSEI_DEBUG`, `ValidateHandle`.
2. If no pool exists for `T`, return. `Remove` never creates a pool.
3. `pool.Remove(entity.Index)`:
   - Return if the component is absent.
   - If `_removeHooks`: `IComponentListener<T>.OnRemoved` with the data intact, group `OnRemoving` (member moved to the end of the member range in every owned pool), version copy for the slot about to be freed.
   - `AutoReset` on the removed component, if implemented.
   - Swap-remove, tail defaulted, sparse cleared, `_count--`.
   - `World.OnComponentRemoved`: decrements `_componentCounts[e]` (only if the entity is alive; see the drain loop below), clears the mask bit, looks up `_filtersByType[t]`; if it exists, `Include` (remove), `Exclude` (test, add), `Any` (test, add or remove). For an entity whose `DestroyEntity` is draining it, only `Include` and `Any` (remove): its components not removed yet are still in the mask and would let it into `Exclude` filters of `t`. Then, if `_dispatch`, `OnComponentRemoved(e, t)` to world listeners while the entity is still alive, unless a filter listener added `t` back or destroyed the entity and let a new one take the slot (with filter listeners on `t`, the slot's generation is read before the filters): that `Add`, or the new entity's own events, already told the world listeners what holds now.
   - Auto-destroy: if `_componentCounts[e] == 0` and the entity is alive, `DestroyEntityInternal(e)` runs before `Remove` returns.

`_dispatch` is a single bool kept equal to `_eventListeners.Length > 0 && !_suppressEvents`; `AddEventListener`, `RemoveEventListener` and `SuppressEvents` recompute it, so the hot path tests one field instead of two.

### `world.DestroyEntity(entity)`

<<< @/snippets/Reference/Architecture.cs#destroy-entity{csharp}

`DestroyEntityInternal(idx)`:

1. `_alive[idx] = false` before anything else. A listener that calls `DestroyEntity` on the same entity re-entrantly hits the `IsAlive` check and becomes a no-op; a listener that calls `Remove` on it does not decrement the count (see step 3). Under `KENSEI_DEBUG` the slot is pushed onto a stack of dying slots, which handle validation accepts.
2. `OnEntityDestroyed(idx)` to world listeners. The entity is dead (`IsAlive` false) but its components are still readable.
3. `DrainComponents(idx)`, in a `finally` so it runs even if a listener threw:

   <<< @/snippets/Reference/Architecture.cs#drain{csharp}

   Each `Remove` runs the full pool flow (pool listeners, group swap, AutoReset, filter updates, world `OnComponentRemoved`). Listeners may re-add components to the dying entity. Such a component goes through the normal `Add` flow, so the dying entity enters the filters it matches together with the components not removed yet (filter listeners get `OnEntityAdded` for an entity whose `IsAlive` is false) and leaves them again when the next pass removes it. `OnComponentAdded` increments `_componentCounts` unconditionally, while `OnComponentRemoved` skips the decrement for a dead entity, so after a pass a non-zero count means "something was re-added" and the loop runs again, reading one `int` instead of rescanning every mask word. The mask is not cleared ahead of the removals: each `Remove` clears its own bit, so the listeners of one removal still see the components not removed yet (`world.Has` agrees with the pools, as in a plain `Remove`), and a component re-added during the pass sets its bit again for the next pass. Up to 2.2.1 each mask word was zeroed before its bits were iterated, so `Has` said `false` for components still in their pools, and components in later words could let the dying entity into `Exclude` filters. A listener that calls `World.Clear` changes `_clears`: the drain stops before the next removal, because the slot has been reset and may already hold an entity the listener created. Under `KENSEI_DEBUG` more than 1000 passes throws.
4. `ReleaseSlot(idx)`, in an inner `finally`: count zeroed (the mask words too, after an interrupted drain), `_aliveCount--`, `_releases++`, slot pushed onto the free stack. Skipped after a `World.Clear` from a listener, which has already released every slot. Under `KENSEI_DEBUG` the debug name is dropped and the slot leaves the stack of dying slots it joined in step 1.

`Warmup` uses the same machinery on a temporary entity with events suppressed, so world listeners and the profiler never see it.

## Event ordering and exception safety

Ordering follows directly from the flows above:

- On `Add`: version stamp and group swap (invisible to listeners), then filter listeners (per filter, in table order), then `IWorldEventListener.OnComponentAdded`, then `IComponentListener<T>.OnAdded`. For `CreateEntity`/`CopyEntity`, `OnEntityCreated` comes last, after every component is in place, so listeners never observe an entity without components; it does not come at all when a listener of one of the components destroyed the entity (`CopyEntity` stops copying then, and when the source is destroyed; it checks both handles whenever `_releases` changed during a copied component). A listener that removes the component again ends the `Add` for everyone after it: a filter listener's removal suppresses the world `OnComponentAdded` (world listeners see only the `OnComponentRemoved`), and typed listeners after the removal get no `OnAdded`. A filter listener that removes the component and adds it back right away is the exception for world listeners: the nested `Add` reports the new component, and the outer `Add` sends its `OnComponentAdded` too, because it checks the entity's mask bit, which is set again (so world listeners get the removal and then two additions; typed listeners get one `OnAdded`, from the nested `Add`). The same in 2.2.1.
- On `Remove`: `IComponentListener<T>.OnRemoved` (data intact, entity still a group member), then the group swap and version copy, then `AutoReset`, then the swap-remove, then filter listeners, then `IWorldEventListener.OnComponentRemoved` with the entity still alive, then auto-destroy if it was the last component. A filter listener that adds the component back suppresses the world `OnComponentRemoved` (world listeners see only the `OnComponentAdded`), and so does one that destroys the entity and creates one in its slot.
- On `DestroyEntity`: `OnEntityDestroyed` first, with the dead flag set and components still readable; then one `Remove` flow per component.
- `Warmup`, `Clear` and `WorldSerializer.Load` fire no world events and no profiler records: `Warmup` and `Load` call `SuppressEvents(true)`, which clears `_dispatch` and is honoured by `EcsProfiler`; `Clear` never calls `Remove` at all, it resets pools, filters and groups directly. Filter listeners and pool listeners are not suppressed: they hang off `Filter.AddEntity`/`RemoveEntity` and `AddHooked`/`RemoveHooked`, which `Load` and `Warmup` do exercise.

Listener lists (`World`, `Filter`, `ComponentPool<T>`) are copy-on-write arrays. A dispatch loops over the array it captured at its start, so a listener that adds or removes listeners mid-dispatch neither shifts the loop nor gets skipped or invoked twice. Each dispatch of a structural change starts when the ones before it in the order above are done: the `OnAdded` dispatch of an `Add` takes the typed listeners subscribed after the filter and world listeners ran (except on a pool that had none when the `Add` started, see [Add](#add)), and world listeners that a filter listener subscribes get the `OnComponentAdded` or `OnComponentRemoved` of that change.

Exception safety:

- A world listener or `AutoReset` that throws during `DestroyEntity` propagates after the `finally` chain has drained what it could and released the slot. Components whose `Remove` did not run stay in their pools and in filters (the pool's sparse entry and filter membership were never cleared for them), while the slot's mask and count are reset. This is a degraded state: treat the exception as fatal for that world or `Clear()` it. Before the `finally` chain existed, the slot was never released at all.
- A world listener that throws during `Add` runs after the component and mask are stored and the filters are updated; the world is consistent, only the pool listeners are skipped.
- A pool listener that throws during `Remove` runs before anything is modified; the component stays. Hook frames are popped in `finally`, so a throwing listener leaves no re-entrancy state behind.
- `SystemsRunner.Init` records progress in `_initProgress`; if a system's `Init` throws, the runner walks `_initSystems[0.._initProgress)` in reverse and destroys the systems there that implement `IDestroySystem` (a nested runner that completed its `Init` is walked system by system by the same rule; a system with only `IDestroySystem` is not in `_initSystems`, so no rollback reaches it), resets the progress and stays uninitialized, so the next `Init` starts over. Exceptions from those `Destroy` calls are collected into one `AggregateException` after the original one, marked in `Exception.Data` (`"KenseiECS.SystemsRunner.InitFailure"`), so that a parent whose own rollback throws as well merges its inner exceptions into its list instead of nesting it. `Run` cleans OneFrame components in a `finally`, so a throwing system cannot leave events to be processed twice. `Destroy` resets the initialized flag before calling `IDestroySystem.Destroy` in reverse order.
- `CommandBuffer.Playback` clears the buffer in a `finally`; a throwing command discards the rest.
- `WorldSerializer.Load` restores `SuppressEvents(false)` in a `finally`; a malformed stream leaves a partially restored world, which the caller should `Clear()`.

## Snapshots

`WorldSerializer` (`Core/Serialization`) writes every alive entity and component to a `Stream` and restores them into an empty world. Entities keep their index and generation, so `Entity` values stored in components, or held by the caller, are valid in the restored world; component types are identified by name, not by their runtime index.

### File layout (version 1)

Integers are written by `BinaryWriter` (little-endian); strings are `BinaryWriter` strings (length-prefixed UTF-8).

```
uint32   magic          0x5343454B, the bytes "KECS"
int32    version        1
int32    tick           world.Tick
int32    entityCount    number of alive entities
  entityCount × { int32 index; int32 generation; }        in ascending index order (AliveEntities)
int32    poolCount      number of pools with Count > 0
  poolCount × {
    string  assembly-qualified component type name
    int32   count
    count × { int32 entityIndex; payload }               in the pool's dense order
  }
```

`payload` is either what the registered `IComponentFormatter<T>.Write` produced, or, for a component type with no formatter, `sizeof(T)` raw bytes taken with `MemoryMarshal.AsBytes` over the dense element. Pools with no components are skipped. `Save` iterates `AliveEntities` (linear in the high-water mark) and `ActivePools` (linear in registered types), then each pool's dense array.

### Blittable path vs formatter path

`ComponentPool<T>` decides per type: `IsBlittable = !RuntimeHelpers.IsReferenceOrContainsReferences<T>()`. A registered formatter is always used when present, blittable or not. Without a formatter, a blittable type is written and read bit-for-bit, and a type that contains references throws `InvalidOperationException` from `WriteComponents`/`ReadComponent`. The raw form is the in-memory layout of the struct, padding included, so a snapshot written on one platform is readable only where `T` has the same layout and endianness. `Entity` is two `int`s and therefore round-trips raw. On read the pool fills a `default(T)` through a `Span<byte>` in a loop until `sizeof(T)` bytes arrived, throwing `EndOfStreamException` if the stream ends first.

### RestoreEntity and FinishRestore

`Load` refuses a world with `EntityCount != 0` (call `Clear()` first), validates magic and version, reads the tick and the entity count, then calls `SuppressEvents(true)` for the rest of the restore:

1. For each stored entity, `World.RestoreEntity(index, generation)`: grows the slot arrays if needed, throws if the slot is already alive (a corrupt stream restoring one slot twice), stamps the generation verbatim, marks the slot alive with zero components, and raises `_nextIndex` past it.
2. `World.FinishRestore(tick)`: rebuilds the free stack by pushing every dead slot below `_nextIndex` from the highest index down, so that the lowest gap is reused first, and sets `_tick`. Only these two methods write generations and `_nextIndex` directly; they are `internal` and valid only on an empty world.
3. For each stored pool: resolve the type name to a `ComponentPoolBase` (`ResolvePool`), then for each entry read the entity index and call `pool.ReadComponent`, which decodes the value and calls the ordinary `pool.Add(entityIndex, value)`.

Because step 3 is the normal `Add`, masks, component counts, filters, groups, change versions and pool listeners all update as they would for live code; because `_dispatch` is false, world listeners and the profiler observe nothing, and no `OnEntityCreated` is dispatched. `SuppressEvents(false)` runs in a `finally`.

Type resolution: `Type.GetType(assemblyQualifiedName)` first; if that fails (the assembly version embedded in the name changed), the namespace-qualified part before the first comma is looked up in every loaded assembly. A type that is not loaded throws `InvalidDataException`. Pools are reached through a per-type accessor: `Register<T>()` (with or without a formatter) installs a typed accessor that calls `world.Pool<T>()` directly, while a type that appears only in the stream gets a reflection accessor built with `MethodInfo.MakeGenericMethod`. Registering is what makes `Load` independent of reflection under IL2CPP for a type no other code touches.

Not part of a snapshot: debug names, listeners, filter and group registrations (they belong to the `World` object; those that exist before `Load` are filled), and the change counter (restored components on tracking pools are stamped as changed by `Add`).

## Built on the core

- `CommandBuffer` records `(Op, PendingId, Entity, TypeIndex, PayloadIndex)` structs and stores payloads in per-type `PayloadStore<T>` arrays indexed by type index, so nothing is boxed. `Playback` resolves `PendingEntity` ids through `world.CreateEntity`, skips commands whose `Entity` is dead, and applies `Add` via `pool.Add` (throws on duplicate), `Set` via overwrite-or-add, `Remove` via `world.GetPool(typeIndex)?.Remove` (no pool creation) and `Destroy` via `world.DestroyEntity`. After the first frame the arrays are reused; `Clear` only wipes payload arrays for types that contain references, so the GC can collect them.
- `EventBuffer<T>` is a component holding a `List<T>` rented from a static per-type `ListPool<T>`; its `AutoReset` returns the list, so `OneFrame<EventBuffer<T>>` stops allocating once the pool and its lists have grown to the peak load. A struct copy of the component shares the list, and a copy kept past the component's removal (a `var b = pool.Get(e)` written back with `Add` or `CommandBuffer.Set` after a OneFrame cleanup) would hand a pooled list to two entities. The pool hands out a `PooledList<T>` that records whether it is free and which entity holds it, as the holder pool's `PoolId` (unique across worlds) and the entity index rather than a reference: on IL2CPP every store of a reference field pays a GC write barrier, and these fields change on every rent and return. `EventBuffer<T>` implements the internal `IPooledListComponent<T>`, from which its pool takes a `PooledListOwner<T>` once (`_listOwner`, an abstract class: a virtual call is cheaper than an interface call on a boxed struct): every store of a value (`Add`, `CommandBuffer.Set`'s overwrite, `CopyEntity`, snapshot loads) claims a fresh list for the entity, replaces a list that is free or held by another entity with an empty list of its own (an overwrite with such a value keeps the entity's buffer instead), and an overwrite with a new list returns the replaced one. `Return` pools only its own lists and ignores a second return, and `Rent` clears a list a stale copy appended to while it was free. List owners take the hooked `Add`/`Remove` path; other pools pay nothing. A write straight into the pool's storage (through a `ref` from `Get`, or `RawData`) is not seen and is not supported for copies. `World.Warmup` does not fill the pool: its default `EventBuffer<T>` has a `null` list and rents nothing; `EventBuffer<T>.Prewarm(lists, capacity)` does: it prepares only the top `lists` free lists (the next ones `Rent` hands out). Each slot keeps a list that is already large enough or takes one from below; when none is, the largest remaining list is grown to `capacity`, so at most `lists` lists are grown and a pool left large by an earlier peak is not inflated wholesale. Missing lists are created on top, and the prepared lists end up at the top of the pool. `ListPool<T>` is static, so all worlds in the process share it. `world.AddEvent` appends to an existing buffer or adds a new one; for a buffer the entity has without a list (added empty, or set to `default`) it rents the list itself and records the entity as its holder, since `EventBuffer<T>.Add` cannot know the entity: a `pool.Get(e).Add(v)` on such a buffer rents a list without a holder, which a copy of the buffer can then give to a second entity.
- `Listeners<T>` is a component holding a `List<T>` of interface implementations; `Subscribe` adds the component on first use, `Unsubscribe` keeps it even when empty so the entity is not auto-destroyed by unsubscribing, and `HasListeners` reports false for an empty list. `Invoke` copies the list into an array rented from `ArrayPool<T>.Shared` and walks that snapshot, then clears and returns the array in `finally`. Before each call it checks that the listener is still in the list: a cursor holds the position where the next snapshot entry is expected, so an unchanged list costs one reference comparison per listener; otherwise it searches outward from the cursor in both directions at once (one step back for a removal before it, a step or two forward for an insertion), so an entry shifted by `d` positions is found in about `2d` comparisons, with `EqualityComparer<T>.Default` like `List.Remove`, and moves the cursor past the match. Listeners unsubscribed mid-dispatch are skipped, ones subscribed mid-dispatch are not called, and a removal or insertion before the cursor (a listener unsubscribing itself, a callback inserting at index 0) does not turn the rest of the dispatch into a search per listener. The `try/catch` for `onError` sits in a separate method, so the loop's locals stay in registers. The list itself is not pooled: `AutoReset` clears and drops it, because a struct copy of the component written back after a remove (`Add` or `CommandBuffer.Set`) would otherwise hand a pooled list to two entities.
- `SystemsRunner` keeps separate lists of `IInitSystem`, `IRunSystem` and `IDestroySystem` so `Run` does no type checks. A phase (`AddPhase`, or the obsolete `Add(runner, name)`) is registered as an init/destroy participant but excluded from `_runSystems`, which is what makes it a separate phase; an inline group (`AddGroup`, or an unnamed `Add(runner)`) is an ordinary `IRunSystem` in the parent's list, and its slot flag and its own `IsEnabled` are kept in sync. A runner has at most one parent (`_parent`, with its entry there in `_registration`): `AddCore` rejects, in every build and before anything is registered, a runner that already has a parent and one that is the parent itself or one of its ancestors, and `SetActive(bool)` on an inline group writes the parent's slot through `_registration.RunIndex`. Only the root's parameterless `Run()` advances the world tick. `OneFrameCleanup<T>` walks `pool.RawEntities` from `Count - 1` down to 0 calling `pool.Remove`, which is safe with swap-remove and with group swaps; `DelHere<T>` registers the same object as a run system at that position. On Unity every run system is wrapped in a `ProfilerMarker`; under `KENSEI_DEBUG` `Stopwatch` timings are recorded per system.
- The Unity layer (`KenseiECS/Unity`) is thin. `EcsBootstrap` is an abstract `MonoBehaviour` that creates the `World` (from `CreateConfig()`), a `SharedData`, a root `SystemsRunner` and two child runners, calls the subclass's `Configure(update, fixedUpdate, lateUpdate, shared)` in `Awake`, then registers the children as the named phases `"fixed"` and `"late"`. `Start` calls `Warmup()` or `Init()`; `Update`, `FixedUpdate` and `LateUpdate` each run their runner only while that runner's `IsInitialized` is true, so a failed `Init`, or an `Awake` that failed before the phases were registered, does not turn into an exception (or a world tick) per frame; under `KENSEI_DEBUG` `Awake` checks right after `Configure` that nothing in the update runner was registered under `"fixed"` or `"late"`; `OnDestroy` destroys the systems, then the world. It implements `IEcsWorldProvider` and `IEcsSystemsProvider`, which the editor windows use for discovery. `EcsComponentProvider<T>` is a `MonoBehaviour` holding one serialized `T`; `EcsEntityView.Spawn(world)` collects the providers on the GameObject in component order, creates the entity from the first (`CreateEntity`) and adds the rest (`Add`), assigns the serialized `EntityName` as the debug name (a no-op without `KENSEI_DEBUG`), and binds the view.

## The source generator

`KenseiECS.Generators/SystemInjectionGenerator.cs` is a Roslyn `ISourceGenerator` compiled against `Microsoft.CodeAnalysis.CSharp 3.8.0` for `netstandard2.0`, so it loads in the compiler shipped with Unity 2021.3 as well as newer ones. The built assembly is committed as `KenseiECS/Plugins/KenseiECS.Generators.dll`. Its `.meta` file carries the `RoslynAnalyzer` asset label and disables every platform: Unity's convention for an analyzer or generator, which is handed to the C# compiler rather than loaded as a runtime assembly. A .NET project uses it by referencing the generator project (or DLL) as an analyzer, as `KenseiECS.Generators.Tests` does.

The attributes it reads are ordinary runtime attributes in `Core/InjectAttributes.cs`: `[Inc(params Type[])]`, `[Exc(params Type[])]`, `[Any(params Type[])]` for `Filter` fields, `[Pool]` for `ComponentPool<T>` fields, `[Group]` for `Group<...>` fields, `[Shared]` / `[Shared("key")]` for anything registered in `SharedData`. All are field-only and single-use.

What it does, per compilation:

1. A syntax receiver flags every class, struct or record that declares a field carrying an attribute whose short name is `Inc`, `Exc`, `Any`, `Pool`, `Group` or `Shared` (with or without the `Attribute` suffix).
2. Each flagged class symbol is processed once (partial declarations are deduplicated). Its non-static fields are inspected and attributes are matched by full name (`KenseiECS.IncAttribute` and so on), so a same-named attribute from another namespace is ignored.
3. Each injected field yields one assignment line:
   - `[Inc]`/`[Exc]`/`[Any]` on a `KenseiECS.Filter` field: `this.f = world.Filter().Inc<A>().Inc<B>().Exc<C>().Any<D>().End();` with fully qualified type names. `KECS003` if the field is not a `Filter` (compared without the nullable annotation, so `Filter?` passes); `KECS004` if there is `[Exc]` without `[Inc]` or `[Any]`; `KECS007` if `[Inc]` and `[Any]` together list no type; `KECS008` if a listed type is not a struct implementing `IComponent` (an unresolved type, for example one produced by another generator, is left to the compiler).
   - `[Pool]` on a `ComponentPool<T>` field: `this.f = world.Pool<T>();`. `KECS003` otherwise.
   - `[Group]` on a `Group<...>` field: `this.f = world.Group<...>();`. `KECS003` otherwise.
   - `[Shared]` on a field of any reference type `T`: `this.f = shared.Get<T>();`, or `shared.Get<T>("key")` when a key is given. `KECS003` for a value type.
   - Any injected field declared `readonly`: `KECS009`.
4. A class with no injected fields gets nothing. A `record` or `struct` with injected fields gets `KECS010`. Otherwise: `KECS001` if the class is not `partial`; `KECS002` if the class itself declares a two-parameter `Init` in source or implements `IInitSystem.Init` explicitly; `KECS011` if a containing type is a `record` or an `interface`; `KECS005` if a containing type is not `partial`.
5. Inheritance is resolved after the per-class checks, base classes first (a base in the same compilation is analysed on demand when a derived class reaches it). A base that neither implements `IInitSystem` nor has injected fields anywhere up its chain is not a system: the generated `Init` is no override, and it is declared `new` exactly when it hides an accessible inherited member (an `Init(World, SharedData)` with any return type, static or not, or a non-method member named `Init`). Otherwise the walk up the base chain takes the first class that either has injected fields in this compilation (it must have generated successfully, otherwise the derived class is skipped silently, the base already reported its own error) or declares an `Init(World, SharedData)`: an explicit `IInitSystem.Init` first, whatever the member order, since the interface maps to it; else an instance `void Init(World, SharedData)` with by-value parameters, not generic and accessible from the derived class. The generated `Init` is an `override` that fills its own fields, calls `base.Init(world, shared)` (not when the base `Init` is abstract) and then `OnInit`, so the base `Init` and the virtual hooks it calls see the derived fields. `KECS006` if the found `Init` is explicit, not virtual, sealed, not public, or a non-override `Init` that is not what `IInitSystem.Init` maps to for its class (`FindImplementationForInterfaceMember`; a `new virtual Init` hiding the interface method is not). An explicit `IInitSystem.Init` that calls an ordinary `Init(World, SharedData)` of its class that can be overridden (`Init(...)` or `this.Init(...)` in its body, bound by the compiler; only a declaration in source can be read) is the one exception: that `Init` is chained instead, since the runner reaches it, and an override of it, through the explicit one. In that case only a class that lists `IInitSystem`, or an interface that extends it, among its own bases is accepted, with the 2.1 layout (its own `Init` re-implements the interface, declared `new` when it hides something, and its `OnInit` calls the base), and only when the class has a `base.Init` in its own declarations: without one the base `Init` would be skipped, which is `KECS006` when the base has injected fields or the runner reaches the found `Init` through `IInitSystem.Init`: it is what the interface maps to, the `Init` an explicit implementation calls, or an override of either (the interface maps to the first declaration of the slot, not to a `sealed` or `protected` override in a class that does not list the interface). An explicit implementation itself cannot be reached through `base.Init`, so it is exempt. `KECS006` also when a hand-written class between the derived class and the chained base declares a member named `Init` that is not a method (the override would fail with CS0505) or another accessible `Init` overload that a `(World, SharedData)` call could bind to (a method type parameter that neither argument can infer makes it inapplicable), since C# would pick it for the generated `base.Init`. When the generated `Init` calls the base, every `base.Init` member access in the class's own declarations (nested types excluded) is `KECS012`: a syntactic check with no overload resolution, because the user's call would run the base a second time. A hand-written class without injected fields whose `override` of `Init(World, SharedData)` ends up overriding a generated `Init` (directly or through hand-written overriding classes) and that has no `base.Init` in its own declarations gets the warning `KECS013`; a generated base in a referenced assembly is recognised only by injected fields visible from here, because private members of referenced assemblies are not imported.
6. `OnInit` is normally a `partial void` declaration plus a call at the end of `Init`: implement it in your part of the class to run code after the fields are filled (and after the base `Init`), or leave it out and the compiler drops the call. A class that declares an `OnInit(World, SharedData)` of its own that is not the implementation of that partial gets no declaration: a non-override one is called, and so is an override of a base hook, unless the generated `Init` calls a base `Init` (step 5) and the hook is first declared in that base or below it, where the base `Init` can call it. A hook declared between the two (in a hand-written class over a generated base), an abstract base `Init` or a base that is not a system leave nothing else to call it. In the 2.1 layout an override of a hook the replaced base `Init` can call is `KECS006`: the generated `Init` would call it, and the `base.Init` that `OnInit` must call would call it again. When a base has an accessible member named `OnInit` with that signature or a non-method one (the virtual hook of a hand-written base, or the private `OnInit` of a generated base that the class is nested in), the partial would hide it: it is declared, inside `#pragma warning disable CS0108, CS0114` because a partial method without an access modifier cannot be `new`, only when the class implements it, and otherwise left out together with its call.
7. The emitted file, `{FullName}.Injection.g.cs` (namespaces, containing types and the type name without `@`, with `_` for the type-parameter brackets), re-opens the namespace and every containing type as `partial` (keeping `static`, `class`/`struct` and type parameters; keywords in names are escaped as `@name`, and a `[Shared]` key is written as an escaped string literal), then declares:

   <<< @/snippets/Reference/Architecture.cs#generated{csharp}

   The `: IInitSystem` base is omitted when the class already implements it or overrides an inherited `Init`; `Init` is `public override` when it chains (step 5), otherwise `public virtual void` in a class that is not `sealed` and `public void` in a sealed one, with `new` when it hides an inherited member. The form never depends on which subclasses the compilation contains, so a subclass in another assembly, or one compiled only under a define, sees the same `Init`. Roslyn compares hint names without case and drops the whole generator output when two are equal, so names that differ only in case (`Game.AI.Move` and `Game.Ai.Move`) get a numeric segment (`Game.Ai.Move.2.Injection.g.cs`), assigned in ordinal order of the full names so that it does not depend on the order of the files; a type or namespace name cannot start with a digit, so the segment cannot meet another name.

The generated code calls the same public API a hand-written `Init` would. There is no reflection and nothing for IL2CPP to strip; filters built this way deduplicate against builder-made and spec-made filters like any other.

## The KENSEI_DEBUG layer

Everything in this section is inside `#if KENSEI_DEBUG` and does not exist in a release build: no fields, no checks, no `Dispose` on the enumerator, no profiler hooks. The cost in release is zero.

What is checked:

| Where | Check |
|---|---|
| `World.Add/Get/Has/Remove`, `AddEvent` | `ValidateHandle`: the handle must be alive, or be the entity whose `DestroyEntity` is running (its slot is on the stack of dying slots and the generation matches). Dead and stale handles throw, also while another entity is being destroyed. |
| `World.CopyEntity`, `GetComponentTypes`, `GetComponentCount`, `SetName` | Throw on a dead entity (release returns `Entity.Null` / reads the slot / ignores). |
| `World.DrainComponents` | Throws after 1000 passes when a listener keeps re-adding components to a dying entity. |
| `ComponentPool<T>.Add(int)` | `IsSlotAcceptingComponents`: the slot must be below `_nextIndex` and alive, or be the slot being destroyed. An `int` carries no generation, so this is the strongest check possible. |
| `ComponentPool<T>.Get(int)`, `Modify(int)`, `MarkChanged(int)`, `ChangedVersion(int)` | Throw when the component is absent. (`ChangedVersion` throws in every build when the pool does not track.) |
| `WorldListenerExtensions.Subscribe/Unsubscribe` | Throw on a dead entity. |
| `Filter` | The iteration guard below. |
| `SystemsRunner` | `Add` after `Init`, `Run` before `Init`, `Init`/`Run` with a different `World` or explicitly different `SharedData`, nested runner with a different `World`/`SharedData`, unknown names in `SetActive`/`IsActive`/`GetRunner`. Per-system `LastRunMs`/`PeakRunMs`. |
| Tooling | `EcsProfiler` hooks on create/destroy/add/remove (skipped while events are suppressed), `WorldDebugView` as `DebuggerTypeProxy` for `World`, `GetRaw`/`SetRaw` on pools, entity debug names (`SetName`, `EcsEntityView.EntityName`). |

Groups, change tracking and snapshots add no debug-only checks beyond the pool ones listed; their consistency errors (double ownership, non-empty world on `Load`, missing formatter, bad magic) throw in every build.

### The iteration guard

Each `Filter` keeps `_debugCursors` (one slot per live enumerator, innermost last) and `_debugIterators` (depth). The enumerator constructor records its depth and writes its starting cursor (`_count + 1`); every successful `MoveNext` writes the slot it is on; `Dispose` decrements the depth. `foreach` calls `Dispose` on a `ref struct` enumerator through pattern-based disposal, so the guard is released on normal exit, `break` and exceptions.

`RemoveEntity` checks every live cursor before the swap:

<<< @/snippets/Reference/Architecture.cs#iteration-guard{csharp}

Reverse iteration has visited every slot above the cursor and is currently on the cursor. Removing the entity at `denseIdx` moves the entity from `lastIdx` into `denseIdx`. The move is harmful only when the destination has not been visited yet (`denseIdx < cursor`) and the moved entity has (`lastIdx >= cursor`): it would be yielded again. Every other combination is safe: removing the current entity (`denseIdx == cursor`) or a visited one, or removing an unvisited entity when the last slot is also unvisited (`lastIdx < cursor`), which happens after the loop has already shrunk the live range. Nested loops over the same filter are covered because every enumerator's cursor is checked.

There is no equivalent guard for group loops; the reverse-iteration rule for destroying members is documented above and is the user's responsibility.

## Performance characteristics

`W` is the number of mask words (`ceil(types / 64)`), `C` the number of components on the entity, `F(t)` the number of filters that constrain type `t`, `P` the number of pools an owning group owns, `L` the number of listeners that fire.

| Operation | Cost | Notes |
|---|---|---|
| `CreateEntity<T>(T)` | O(1) amortized + one `Add` | Pops the free stack or bumps the high-water mark; may grow every slot array and mask word. |
| `Add<T>` | O(1) + O(F(t) + L) | One `_addHooks` branch. Each filter test is one or a few `ulong` operations. Sparse/dense growth is amortized. |
| `Add<T>` on an owned type | + O(P) | P `Has` checks; P `SwapDense` when the entity becomes a member. |
| `Add<T>` on a tracking pool | + O(1) | One counter increment, one store. |
| `Remove<T>` | O(1) + O(F(t) + L) | Swap-remove. Plus a `DestroyEntity` if it was the last component. |
| `Remove<T>` on an owned type | + O(P) when the entity was a member | Otherwise one `Contains`. |
| `Get<T>` / `pool.Get(int)` | O(1) | Sparse read, then dense read. Untracked. |
| `pool.Modify(int)`, `MarkChanged(int)` | O(1) | `Get` plus a counter increment and a store. |
| `pool.ChangedSince(int, int)` | O(1) | Sparse read, version read, compare. |
| `Has<T>` | O(1) | One mask word read; no pool access. |
| `pool.Has(int)` | O(1) | Sparse read. |
| `IsAlive`, `GetEntity(int)`, `TryGetEntity(int)`, `GetComponentCount` | O(1) | |
| `DestroyEntity` | O(W + C × (F + L + P)) | Mask walk over set bits only; each component pays its own `Remove`. |
| `CopyEntity` | O(W + C) + C × `Add` | Same walk; `AutoCopy` per component that implements it. |
| `GetComponentTypes` | O(W + C) | |
| `foreach (int e in filter)` | O(matches) | One span load per step, zero allocation; component access is a sparse lookup per pool. |
| Group loop over `Data1..DataN` | O(members) | Contiguous spans, no sparse lookups, no per-entity mask test. |
| `Filter.Count/IsEmpty/Contains/First/Single` | O(1) | |
| `Filter().…End()`, `Filter<Spec>()` | O(registered filters) + O(entity slots) | Dedup scan, then `PopulateFilter`. Allocates. Build in `Init`. |
| `Group<T1, T2>()` | O(registered groups) + O(smallest owned pool × P) | Dedup scan, then `Populate`. Allocates. Build in `Init`. |
| `pool.TrackChanges()` | O(pool count) once | Allocates the version array. |
| `Pool<T>()` | O(1) | Type check on `_pools[typeIdx]`; first call allocates the pool. |
| `GetSingleton<T>` | O(1) | `pool.RawData[0]` after a count check. |
| `CommandBuffer.Playback` | O(commands) | Each command pays the corresponding world operation. |
| `SystemsRunner.Run` | O(systems) + Σ OneFrame pool counts | |
| `WorldSerializer.Save` | O(high-water slots + registered types + Σ components) | Plus stream I/O. |
| `WorldSerializer.Load` | O(entities + high-water slots) + Σ components × `Add` | Plus one type resolution per stored pool. |
| `Warmup` | O(registered pools) × (`Add` + `Remove`) | |
| `Clear` | O(high-water slots × W) + Σ pool counts + Σ filter counts + groups | Pools and filters reset through their dense lists, not their sparse arrays. |

What scales with the number of component types versus the entity's own components:

- Per-frame operations (`Add`, `Remove`, `Get`, `Has`, iteration, `DestroyEntity`, `CopyEntity`) never loop over registered types. `DestroyEntity` and `CopyEntity` touch `W` words and then only set bits.
- `Warmup`, `World.Clear`, `World.Destroy`, `WorldSerializer.Save` and `ActivePools` loop over `_pools`, i.e. over registered types. They are setup, teardown and save operations.
- Filter matching is O(active words), at most `W`; single-word filters read three scalars.

Memory, per element:

- Entity slot: 4 bytes generation, 1 byte alive, 4 bytes count, 8 × W bytes of masks.
- Pool: 4 bytes per sparse slot (grows to cover the highest entity index that ever held the component, starting at `InitialPoolSparseCapacity`), plus (4 + `sizeof(T)`) per dense slot (starts at `InitialPoolDenseCapacity`, doubles with the component count), plus 4 bytes per dense slot once `TrackChanges` was called. One boxed `default(T)` per implemented bridge interface (`IAutoReset<T>`, `IAutoCopy<T>`). `ComponentPoolBase.AllocatedBytes` reports the array sizes (without the version array).
- Filter: 4 bytes per dense slot plus one, 4 KB per touched 1024-entity page, three mask arrays of `ceil(maxConstrainedType / 64)` words. `Filter.AllocatedBytes` reports it.
- Group: P pool references and a count; no arrays of its own.
- Registry: `_pools` and `_filtersByType` hold one reference per type index up to the highest seen; `TypeFilters` objects exist only for constrained types.

## Known limitations and design decisions

**No archetypes.** Storage is one sparse set per component type. Structural changes are O(1) and never move an entity's other components; a `ref T` stays valid across adds of *other* types; filters are cheap sparse sets updated incrementally; there is no combinatorial explosion of archetypes when many optional components exist. The price is iteration: reading two components per entity through a filter is two sparse-to-dense indirections into two unrelated arrays, which loses to chunked archetype layouts on pure iteration (see the [benchmarks](benchmarks.md)) and wins on structural changes and mixed frames. Owning groups recover contiguous iteration for a chosen set of types at the cost of one extra swap per owned pool when an entity joins or leaves, and of the one-owner-per-pool rule.

**`CreateEntity` requires a component.** The invariant is "alive if and only if it has at least one component". An empty entity would be invisible to every filter, invisible to world listeners (`OnEntityCreated` fires after the first component), and would be destroyed by the first `Remove` anyway. Requiring the component at creation removes the empty state entirely, so there is no leak class to detect and no "empty entity" check to run. `CommandBuffer.CreateEntity<T>(T)` and `EcsEntityView.Spawn` (which throws without a provider) mirror the rule.

**Auto-destroy on last component removal.** Same invariant from the other side. It makes "event entities" free: an entity whose only component is a one-frame component disappears in the cleanup. The one place where this surprised users, unsubscribing the last listener, is handled by keeping the empty `Listeners<T>` component.

**Single-threaded.** `World`, pools, filters and groups have no synchronization. Reading `RawData`/`RawEntities`/group spans from several threads is safe only while no structural change happens anywhere. `ComponentType<T>.Index` registration is thread-safe. `EventBuffer<T>`'s list pool is a static per-type stack shared by all worlds in the process and is not thread-safe either.

**Iteration order is unspecified** and changes with every swap-remove and every group swap. Removing a not-yet-visited entity from the filter being iterated can double-visit an already visited entity (thrown under `KENSEI_DEBUG`); defer such changes with a `CommandBuffer`. Group loops that destroy members must run downward.

**Exclude-only and empty filters are rejected.** Membership is driven solely by add/remove of constrained types, and `PopulateFilter` needs at least one required or alternative type to reason about; add an `Inc` for a component the target entities always have. The generator reports the same rule as `KECS004`.

**One owner per pool, order-sensitive.** A component type can be owned by one group, groups take two to four types, `Group<A, B>` and `Group<B, A>` are different requests (the second throws), and groups cannot be unregistered. Owning a pool changes the order of its `RawData`, which was never guaranteed.

**Change tracking is opt-in and write-side.** Only `Add`, `Modify` and `MarkChanged` stamp versions; writes through `Get`, raw arrays or group spans do not.

**Snapshots are layout-bound and partial.** Raw components are the writing platform's struct layout; only format version 1 exists; listeners, debug names, filter and group registrations are not stored; component types are found by name, so renaming or moving a type breaks old files.

**The generator injects fields only.** It needs a `partial` class (and `partial` containers), forbids a hand-written two-parameter `Init` next to injected fields, and targets the Roslyn 3.8 API.

**`ref T` invalidation.** A `ref` from `Get`/`Add`/`Modify` is valid until the next `Add` or `Remove` of the same type on any entity (including those inside `DestroyEntity`, `CopyEntity`, one-frame cleanup, `CommandBuffer.Playback` and listeners), or, in a group-owned pool, the next `Add` or `Remove` of any of the group's types, or a `Reserve` that grows the pool's dense arrays, or `Clear`/`Destroy` of the world.

**Type indices are process-wide** and depend on first-touch order. They are not stable across runs and must not be serialized (snapshots use names). Every world's registries are sized by the highest type index it has seen, not by how many types it uses.

**`Entity` carries no world id.** Using a handle from one world on another is undefined; the generation check catches it only by accident.

**Filters cannot be unregistered.** They live until `World.Destroy`. Static specs are limited to `Inc` 1-6, `Exc` 1-4, `Any` 2-4 types; the builder and the attributes have no limit.
