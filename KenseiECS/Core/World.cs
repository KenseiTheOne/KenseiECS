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
    [DebuggerTypeProxy(typeof(WorldDebugView))]
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

        public World() : this(WorldConfig.Default()) { }

        public World(WorldConfig config) {
            _config = config;
            Id = _nextWorldId++;
            _generations = new int[_config.InitialEntityCapacity];
            _alive = new bool[_config.InitialEntityCapacity];
            _componentCounts = new int[_config.InitialEntityCapacity];
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
            Array.Clear(_generations, 0, _nextIndex);
            Array.Clear(_alive, 0, _nextIndex);
            Array.Clear(_componentCounts, 0, _nextIndex);
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
            var dummy = CreateEntity();

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
        /// Create a new entity. O(1).
        /// Reuses a slot from the free list, or allocates a new one.
        /// </summary>
        public Entity CreateEntity() {
            int index;

            if (_freeIndices.Count > 0) {
                index = _freeIndices.Pop();
            } else {
                index = _nextIndex++;
                EnsureEntityCapacity(index);
            }

            _alive[index] = true;
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

        /// <summary> Create entity with one initial component. </summary>
        public Entity CreateEntity<T>(T component) where T : struct, IComponent {
            var entity = CreateEntity();
            Add(entity, component);
            return entity;
        }

        /// <summary> Create entity with two initial components. </summary>
        public Entity CreateEntity<T1, T2>(T1 c1, T2 c2)
            where T1 : struct, IComponent
            where T2 : struct, IComponent {
            var entity = CreateEntity();
            Add(entity, c1);
            Add(entity, c2);
            return entity;
        }

        /// <summary> Create entity with three initial components. </summary>
        public Entity CreateEntity<T1, T2, T3>(T1 c1, T2 c2, T3 c3)
            where T1 : struct, IComponent
            where T2 : struct, IComponent
            where T3 : struct, IComponent {
            var entity = CreateEntity();
            Add(entity, c1);
            Add(entity, c2);
            Add(entity, c3);
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

            for (int i = 0; i < _pools.Length; i++) {
                _pools[i]?.Remove(idx);
            }

            _generations[idx]++;
            _aliveCount--;

            // Only recycle slot if generation fits in 12 bits.
            // If it overflows (wraps to 0), the slot is permanently retired
            // to prevent generation collision with old Entity snapshots.
            const int maxGeneration = (1 << 12) - 1; // 4095
            if (_generations[idx] <= maxGeneration) {
                _freeIndices.Push(idx);
            }
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

            var copy = CreateEntity();
            int srcIdx = source.Index;
            int dstIdx = copy.Index;

            for (int i = 0; i < _pools.Length; i++) {
                var pool = _pools[i];
                if (pool != null && pool.Has(srcIdx)) {
                    pool.CopyTo(srcIdx, dstIdx);
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

            int newSize = Math.Max(_generations.Length * 2, index + 1);
            Array.Resize(ref _generations, newSize);
            Array.Resize(ref _alive, newSize);
            Array.Resize(ref _componentCounts, newSize);
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
    }
}
