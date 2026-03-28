using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace KenseiECS {
    /// <summary>
    /// World — ECS entry point.
    /// Owns entity lifecycle, component pools, and filter registry.
    ///
    /// Filters are updated reactively: when a component is added or removed,
    /// World checks all filters that depend on that component type
    /// and adds/removes the entity as needed.
    /// </summary>
#if KENSEI_DEBUG
    [DebuggerTypeProxy(typeof(WorldDebugView))]
#endif
    public class World {
        private static int _nextWorldId;

        // --- World identity ---
        /// <summary> Unique world ID. </summary>
        public readonly int Id;

        // --- Config ---
        private readonly WorldConfig _config;

        // --- Entity storage ---
        internal int[] _generations;       // _generations[index] = current slot generation
        internal bool[] _alive;            // _alive[index] = true if entity is alive
        internal int[] _componentCounts;   // _componentCounts[index] = number of components on entity
        internal ulong[] _componentMasks;  // _componentMasks[index] = bitmask of component types (bits 0..63)
        private bool _hasHighIndexPools;   // true if any component type has index >= 64
        internal Stack<int> _freeIndices;  // free slot stack, O(1) push/pop
        internal int _nextIndex;           // next unused index
        private int _aliveCount;

        // --- Component storage ---
        // Indexed by ComponentType<T>.Index for O(1) access
        internal IComponentPool[] _pools;

        // --- Filter registry ---
        private readonly List<Filter> _allFilters = new();

        // typeIndex → list of filters that include or exclude this type.
        // Array indexed by typeIndex for O(1) lookup. Grows as needed.
        private List<Filter>[] _filtersByType;

        // --- World event listeners ---
        private readonly List<IWorldEventListener> _eventListeners = new();

        // --- Tick counter ---
        private int _tick;

        public int EntityCount => _aliveCount;
        public bool IsDestroyed => _alive == null;

        /// <summary> Current tick number. Starts at 0, incremented by NextTick(). First run = tick 1. </summary>
        public int Tick => _tick;

        /// <summary> Advance the tick counter. Call once per frame before systems run. </summary>
        public void NextTick() {
            _tick++;
        }

        /// <summary> Register a world event listener. </summary>
        public void AddEventListener(IWorldEventListener listener) {
            _eventListeners.Add(listener);
        }

        /// <summary> Unregister a world event listener. </summary>
        public void RemoveEventListener(IWorldEventListener listener) {
            _eventListeners.Remove(listener);
        }

        // =====================================================================
        // Enumeration — safe, zero-allocation iteration for debug/editor tools
        // =====================================================================

        /// <summary> Enumerate all alive entities. Zero-allocation. </summary>
        public AliveEntityEnumerable AliveEntities => new(this);

        /// <summary> Enumerate all registered (non-null) component pools. Zero-allocation. </summary>
        public ActivePoolEnumerable ActivePools => new(this);

        public readonly struct AliveEntityEnumerable {
            private readonly World _world;

            internal AliveEntityEnumerable(World world) {
                _world = world;
            }

            public Enumerator GetEnumerator() {
                return new Enumerator(_world);
            }

            public struct Enumerator {
                private readonly World _world;
                private readonly int _snapshotCount;
                private int _index;

                internal Enumerator(World world) {
                    _world = world;
                    _snapshotCount = world._nextIndex;
                    _index = -1;
                }

                public Entity Current => _world.GetEntity(_index);

                public bool MoveNext() {
                    while (++_index < _snapshotCount) {
                        if (_world._alive[_index]) {
                            return true;
                        }
                    }
                    return false;
                }
            }
        }

        public readonly struct ActivePoolEnumerable {
            private readonly World _world;

            internal ActivePoolEnumerable(World world) {
                _world = world;
            }

            public Enumerator GetEnumerator() {
                return new Enumerator(_world);
            }

            public struct Enumerator {
                private readonly World _world;
                private int _index;

                internal Enumerator(World world) {
                    _world = world;
                    _index = -1;
                }

                public IComponentPool Current => _world._pools[_index];

                public bool MoveNext() {
                    var pools = _world._pools;
                    while (++_index < pools.Length) {
                        if (pools[_index] != null) {
                            return true;
                        }
                    }
                    return false;
                }
            }
        }

        public World() : this(WorldConfig.Default()) { }

        public World(WorldConfig config) {
            _config = config;
            Id = _nextWorldId++;
            _generations = new int[_config.InitialEntityCapacity];
            Array.Fill(_generations, 1);
            _alive = new bool[_config.InitialEntityCapacity];
            _componentCounts = new int[_config.InitialEntityCapacity];
            _componentMasks = new ulong[_config.InitialEntityCapacity];
            _freeIndices = new Stack<int>(_config.InitialEntityCapacity / 4);
            _pools = new IComponentPool[_config.InitialPoolCount];
            _filtersByType = new List<Filter>[_config.InitialPoolCount];
            _nextIndex = 0;
            _aliveCount = 0;
        }

        // =====================================================================
        // Entity lifecycle
        // =====================================================================

        /// <summary>
        /// Reset the world to a clean state.
        /// All entities destroyed, all pools and filters emptied.
        /// Pools, filters, and their registrations are preserved —
        /// only data is cleared. No reallocation needed on next use.
        /// </summary>
        public void Clear() {
            // Increment generations for ALL used slots to invalidate stale handles.
            // Both alive and dead slots need incrementing — a dead slot's stale reference
            // could otherwise collide with a new entity created after Clear().
            for (int i = 0; i < _nextIndex; i++) {
                _generations[i]++;
            }

            Array.Clear(_alive, 0, _nextIndex);
            Array.Clear(_componentCounts, 0, _nextIndex);
            Array.Clear(_componentMasks, 0, _nextIndex);
            _freeIndices.Clear();
            _nextIndex = 0;
            _aliveCount = 0;
            _tick = 0;

            for (int i = 0; i < _pools.Length; i++) {
                _pools[i]?.Clear();
            }

            for (int i = 0; i < _allFilters.Count; i++) {
                _allFilters[i].Clear();
            }
        }

        /// <summary>
        /// Fully destroy the world. Nulls all internal references so GC can collect.
        /// World instance should not be used after this call.
        /// </summary>
        public void Destroy() {
            Clear();

            _generations = null;
            _alive = null;
            _componentCounts = null;
            _componentMasks = null;
            _freeIndices = null;
            _pools = null;
            _allFilters.Clear();
            _filtersByType = null;
        }

        // =====================================================================
        // Warmup
        // =====================================================================

        /// <summary>
        /// Pre-touch pools and filters to trigger JIT compilation and memory allocation.
        /// Creates a temporary entity, adds all registered component types,
        /// exercises all filter paths, then clears everything.
        /// Call once before gameplay starts (e.g. during loading screen).
        /// </summary>
        public void Warmup() {
            var dummy = CreateEntityInternal();

            for (int i = 0; i < _pools.Length; i++) {
                var pool = _pools[i];
                if (pool == null) {
                    continue;
                }

                pool.Has(dummy.Index);
            }

            DestroyEntity(dummy);
            Clear();
        }

        /// <summary>
        // Create a raw entity (no components). Internal use only — Warmup, CopyEntity, CreateEntity<T>.
        private Entity CreateEntityInternal() {
            int index;

            if (_freeIndices.Count > 0) {
                index = _freeIndices.Pop();
            } else {
                index = _nextIndex++;
                EnsureEntityCapacity(index);
            }

            _alive[index] = true;
            _componentCounts[index] = 0;
            _componentMasks[index] = 0;
            _aliveCount++;

            var entity = new Entity(index, _generations[index]);
#if KENSEI_DEBUG
            EcsProfiler.OnEntityCreated(_tick, index, entity.Generation);
#endif
            for (int i = 0; i < _eventListeners.Count; i++) {
                _eventListeners[i].OnEntityCreated(index);
            }

            return entity;
        }

        // Create entity with one initial component.
        // Always require at least one component — empty entities are memory leaks.
        public Entity CreateEntity<T>(T component) where T : struct, IComponent {
            var entity = CreateEntityInternal();
            Add(entity, component);
            return entity;
        }

        /// <summary>
        /// Destroy an entity. O(number of component types).
        /// Removes all components, increments generation, returns slot to free list.
        /// If generation overflows (12-bit wrap-around), the slot is burned
        /// and never reused — prevents aliasing with ancient stale references.
        /// Also called automatically when last component is removed.
        /// </summary>
        public void DestroyEntity(Entity entity) {
            if (!IsAlive(entity)) {
                return;
            }

            int idx = entity.Index;

#if KENSEI_DEBUG
            EcsProfiler.OnEntityDestroyed(_tick, idx, entity.Generation);
#endif
            for (int i = 0; i < _eventListeners.Count; i++) {
                _eventListeners[i].OnEntityDestroyed(idx);
            }

            // Mark as dead first — prevents re-entrant auto-destroy
            // when Remove triggers OnComponentRemoved with count reaching 0
            _alive[idx] = false;
            _componentCounts[idx] = 0;

            // Fast path: remove only components the entity actually has (via bitmask)
            ulong mask = _componentMasks[idx];
            _componentMasks[idx] = 0;

            while (mask != 0) {
                int typeIdx = TrailingZeroCount(mask);
                _pools[typeIdx]?.Remove(idx);
                mask &= mask - 1; // clear lowest set bit
            }

            // Slow path: check pools beyond bitmask range (type index >= 64)
            if (_hasHighIndexPools) {
                for (int i = 64; i < _pools.Length; i++) {
                    _pools[i]?.Remove(idx);
                }
            }

            _generations[idx]++;
            _aliveCount--;
            _freeIndices.Push(idx);
        }

        /// <summary> Check if entity is alive. O(1). </summary>
        public bool IsAlive(Entity entity) {
            int idx = entity.Index;
            return idx < _nextIndex
                && _alive[idx]
                && _generations[idx] == entity.Generation;
        }

        /// <summary> Get the current Entity for a given slot index. </summary>
        public Entity GetEntity(int entityIndex) {
            return new Entity(entityIndex, _generations[entityIndex]);
        }

        /// <summary>
        /// Copy an entity — creates a new entity with copies of all components.
        /// Returns the new entity.
        /// </summary>
        public Entity CopyEntity(Entity source) {
            if (!IsAlive(source)) {
                return Entity.Null;
            }

            var copy = CreateEntityInternal();
            int srcIdx = source.Index;
            int dstIdx = copy.Index;

            // Fast path: copy only components tracked by bitmask
            ulong mask = _componentMasks[srcIdx];
            while (mask != 0) {
                int typeIdx = TrailingZeroCount(mask);
                _pools[typeIdx].CopyTo(srcIdx, dstIdx);
                mask &= mask - 1;
            }

            // Slow path: pools beyond bitmask range
            if (_hasHighIndexPools) {
                for (int i = 64; i < _pools.Length; i++) {
                    var pool = _pools[i];
                    if (pool != null && pool.Has(srcIdx)) {
                        pool.CopyTo(srcIdx, dstIdx);
                    }
                }
            }

            return copy;
        }

        // =====================================================================
        // Component access
        // =====================================================================

        /// <summary> Get (or lazily create) a typed component pool. </summary>
        public ComponentPool<T> Pool<T>() where T : struct, IComponent {
            int typeIdx = ComponentType<T>.Index;
            EnsurePoolCapacity(typeIdx);

            if (_pools[typeIdx] == null) {
                _pools[typeIdx] = new ComponentPool<T>(this, _config.InitialPoolSparseCapacity, _config.InitialPoolDenseCapacity);
            }

            return (ComponentPool<T>)_pools[typeIdx];
        }

        /// <summary> Add a component to an entity. </summary>
        public ref T Add<T>(Entity entity, T component) where T : struct, IComponent {
            return ref Pool<T>().Add(entity.Index, component);
        }

        /// <summary> Get a component by ref. </summary>
        public ref T Get<T>(Entity entity) where T : struct, IComponent {
            return ref Pool<T>().Get(entity.Index);
        }

        /// <summary> Check if entity has a component. </summary>
        public bool Has<T>(Entity entity) where T : struct, IComponent {
            return Pool<T>().Has(entity.Index);
        }

        /// <summary> Remove a component from an entity. </summary>
        public void Remove<T>(Entity entity) where T : struct, IComponent {
            Pool<T>().Remove(entity.Index);
        }

        // =====================================================================
        // Filter API
        // =====================================================================

        /// <summary> Start building a new filter. </summary>
        public FilterBuilder Filter() {
            return new FilterBuilder(this);
        }

        /// <summary>
        /// Register a filter. If identical constraints already exist, returns existing filter.
        /// Populates the filter with all currently matching entities.
        /// </summary>
        internal Filter RegisterFilter(int[] includes, int[] excludes) {
            foreach (var existing in _allFilters) {
                if (ArraysEqual(existing.IncludedTypeIndices, includes)
                    && ArraysEqual(existing.ExcludedTypeIndices, excludes)) {
                    return existing;
                }
            }

            var filter = new Filter(includes, excludes, _config.InitialEntityCapacity, _config.InitialPoolDenseCapacity);
            _allFilters.Add(filter);

            foreach (int typeIdx in includes) {
                EnsureFiltersByTypeCapacity(typeIdx);
                if (_filtersByType[typeIdx] == null) {
                    _filtersByType[typeIdx] = new List<Filter>();
                }
                _filtersByType[typeIdx].Add(filter);
            }

            foreach (int typeIdx in excludes) {
                EnsureFiltersByTypeCapacity(typeIdx);
                if (_filtersByType[typeIdx] == null) {
                    _filtersByType[typeIdx] = new List<Filter>();
                }
                _filtersByType[typeIdx].Add(filter);
            }

            PopulateFilter(filter);

            return filter;
        }

        // =====================================================================
        // Filter notifications — called by ComponentPool on Add/Remove
        // =====================================================================

        /// <summary>
        /// Called by ComponentPool after a component is added.
        /// Increments component count and updates relevant filters.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void OnComponentAdded(int entityIndex, int typeIndex) {
            _componentCounts[entityIndex]++;

            if (typeIndex < 64) {
                _componentMasks[entityIndex] |= 1UL << typeIndex;
            } else {
                _hasHighIndexPools = true;
            }

            if (typeIndex < _filtersByType.Length) {
                var filters = _filtersByType[typeIndex];
                if (filters != null) {
                    for (int i = 0; i < filters.Count; i++) {
                        UpdateFilterForEntity(filters[i], entityIndex);
                    }
                }
            }

            for (int i = 0; i < _eventListeners.Count; i++) {
                _eventListeners[i].OnComponentAdded(entityIndex, typeIndex);
            }
        }

        /// <summary>
        /// Called by ComponentPool after a component is removed.
        /// Decrements component count, updates filters,
        /// and auto-destroys the entity if no components remain.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void OnComponentRemoved(int entityIndex, int typeIndex) {
            _componentCounts[entityIndex]--;

            if (typeIndex < 64) {
                _componentMasks[entityIndex] &= ~(1UL << typeIndex);
            }

            if (typeIndex < _filtersByType.Length) {
                var filters = _filtersByType[typeIndex];
                if (filters != null) {
                    for (int i = 0; i < filters.Count; i++) {
                        UpdateFilterForEntity(filters[i], entityIndex);
                    }
                }
            }

            for (int i = 0; i < _eventListeners.Count; i++) {
                _eventListeners[i].OnComponentRemoved(entityIndex, typeIndex);
            }

            if (_componentCounts[entityIndex] == 0 && _alive[entityIndex]) {
                DestroyEntity(GetEntity(entityIndex));
            }
        }

        // =====================================================================
        // Private
        // =====================================================================

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void UpdateFilterForEntity(Filter filter, int entityIndex) {
            if (EntityMatchesFilter(filter, entityIndex)) {
                filter.AddEntity(entityIndex);
            } else {
                filter.RemoveEntity(entityIndex);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool EntityMatchesFilter(Filter filter, int entityIndex) {
            // Fast path: single bitmask comparison when all type indices < 64
            if (filter.UseMask) {
                ulong mask = _componentMasks[entityIndex];
                return (mask & filter.IncludeMask) == filter.IncludeMask
                    && (mask & filter.ExcludeMask) == 0;
            }

            // Slow path: per-pool Has() check for filters with type indices >= 64
            var includes = filter.IncludedTypeIndices;
            var excludes = filter.ExcludedTypeIndices;

            for (int i = 0; i < includes.Length; i++) {
                int typeIdx = includes[i];
                if (typeIdx >= _pools.Length || _pools[typeIdx] == null || !_pools[typeIdx].Has(entityIndex)) {
                    return false;
                }
            }

            for (int i = 0; i < excludes.Length; i++) {
                int typeIdx = excludes[i];
                if (typeIdx < _pools.Length && _pools[typeIdx] != null && _pools[typeIdx].Has(entityIndex)) {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Fill a newly created filter with all currently matching entities.
        /// Iterates alive entities and checks full match.
        /// Runs once per filter creation, not per frame.
        /// </summary>
        private void PopulateFilter(Filter filter) {
            for (int i = 0; i < filter.IncludedTypeIndices.Length; i++) {
                int typeIdx = filter.IncludedTypeIndices[i];
                if (typeIdx >= _pools.Length || _pools[typeIdx] == null) {
                    return;
                }
            }

            for (int i = 0; i < _nextIndex; i++) {
                if (_alive[i] && EntityMatchesFilter(filter, i)) {
                    filter.AddEntity(i);
                }
            }
        }

        private static bool ArraysEqual(int[] a, int[] b) {
            if (a.Length != b.Length) {
                return false;
            }

            for (int i = 0; i < a.Length; i++) {
                if (a[i] != b[i]) {
                    return false;
                }
            }

            return true;
        }

        private void EnsureEntityCapacity(int index) {
            if (index < _generations.Length) {
                return;
            }

            int oldSize = _generations.Length;
            int newSize = Math.Max(oldSize * 2, index + 1);
            Array.Resize(ref _generations, newSize);
            Array.Fill(_generations, 1, oldSize, newSize - oldSize);
            Array.Resize(ref _alive, newSize);
            Array.Resize(ref _componentCounts, newSize);
            Array.Resize(ref _componentMasks, newSize);
        }

        private void EnsurePoolCapacity(int typeIndex) {
            if (typeIndex < _pools.Length) {
                return;
            }

            int newSize = Math.Max(_pools.Length * 2, typeIndex + 1);
            Array.Resize(ref _pools, newSize);
        }

        private void EnsureFiltersByTypeCapacity(int typeIndex) {
            if (typeIndex < _filtersByType.Length) {
                return;
            }

            int newSize = Math.Max(_filtersByType.Length * 2, typeIndex + 1);
            Array.Resize(ref _filtersByType, newSize);
        }

        // De Bruijn trailing zero count — branchless, single multiply + lookup.
        // Compiles to ~5 instructions vs 12-15 for conditional cascade.
        // Only called when value != 0 (guaranteed by while(mask != 0) callers).
        private static readonly int[] DeBruijnTable = {
            0,  1,  2, 53,  3,  7, 54, 27,  4, 38, 41,  8, 34, 55, 48, 28,
           62,  5, 39, 46, 44, 42, 22,  9, 24, 35, 59, 56, 49, 18, 29, 11,
           63, 52,  6, 26, 37, 40, 33, 47, 61, 45, 43, 21, 23, 58, 17, 10,
           51, 25, 36, 32, 60, 20, 57, 16, 50, 31, 19, 15, 30, 14, 13, 12
        };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int TrailingZeroCount(ulong value) {
            return DeBruijnTable[((value & (ulong)-(long)value) * 0x022FDD63CC95386DUL) >> 58];
        }
    }
}
