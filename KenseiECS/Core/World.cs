using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
#if ENABLE_IL2CPP
using Unity.IL2CPP.CompilerServices;
#endif

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
#if ENABLE_IL2CPP
    [Il2CppSetOption(Option.NullChecks, false)]
    [Il2CppSetOption(Option.ArrayBoundsChecks, false)]
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
        // Multi-word bitmask: _componentMasks[wordIndex][entityIndex]
        // Tracks which component types each entity has, for O(1) filter matching.
        internal ulong[][] _componentMasks;
        private int _maskWordCount;
        internal Stack<int> _freeIndices;  // free slot stack, O(1) push/pop
        internal int _nextIndex;           // next unused index
        private int _aliveCount;

        // --- Component storage ---
        // Indexed by ComponentType<T>.Index for O(1) access
        internal IComponentPool[] _pools;

        // --- Filter registry ---
        private readonly List<Filter> _allFilters = new();

        // typeIndex → filters that include or exclude this type.
        // Jagged arrays instead of List<Filter> — the notification hot path
        // iterates them on every structural change, and array indexing skips
        // the List size check and _items indirection. Rebuilt on registration,
        // which is rare.
        private Filter[][] _filtersByType;

        // --- World event listeners ---
        private readonly List<IWorldEventListener> _eventListeners = new();

        // --- Tick counter ---
        private int _tick;

#if KENSEI_DEBUG
        // Depth of nested DestroyEntity calls. While > 0, listeners legitimately
        // operate on the dying entity (dead flag set, generation not yet bumped),
        // so handle validation must not reject it.
        private int _destroyDepth;
#endif

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
            var defaults = WorldConfig.Default();
            if (config.InitialEntityCapacity <= 0) config.InitialEntityCapacity = defaults.InitialEntityCapacity;
            if (config.InitialPoolSparseCapacity <= 0) config.InitialPoolSparseCapacity = defaults.InitialPoolSparseCapacity;
            if (config.InitialPoolDenseCapacity <= 0) config.InitialPoolDenseCapacity = defaults.InitialPoolDenseCapacity;
            if (config.InitialPoolCount <= 0) config.InitialPoolCount = defaults.InitialPoolCount;
            _config = config;
            Id = Interlocked.Increment(ref _nextWorldId) - 1;
            _generations = new int[_config.InitialEntityCapacity];
            Array.Fill(_generations, 1);
            _alive = new bool[_config.InitialEntityCapacity];
            _componentCounts = new int[_config.InitialEntityCapacity];
            _maskWordCount = 1;
            _componentMasks = new ulong[1][];
            _componentMasks[0] = new ulong[_config.InitialEntityCapacity];
            _freeIndices = new Stack<int>(_config.InitialEntityCapacity / 4);
            _pools = new IComponentPool[_config.InitialPoolCount];
            _filtersByType = new Filter[_config.InitialPoolCount][];
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
                if (_generations[i] == 0) _generations[i] = 1;
            }

            Array.Clear(_alive, 0, _nextIndex);
            Array.Clear(_componentCounts, 0, _nextIndex);
            for (int w = 0; w < _maskWordCount; w++) {
                Array.Clear(_componentMasks[w], 0, _nextIndex);
            }
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
        /// Creates a temporary entity, adds a default component of every registered type
        /// (exercises Add paths, component masks, and filter insertion), then destroys it
        /// (exercises Remove paths and filter removal).
        /// Existing entities and their data are not touched.
        /// Call once before gameplay starts (e.g. during loading screen).
        /// </summary>
        public void Warmup() {
            var dummy = CreateEntityInternal();

            for (int i = 0; i < _pools.Length; i++) {
                _pools[i]?.AddDefault(dummy.Index);
            }

            DestroyEntity(dummy);
        }

        /// <summary>
        // Create a raw entity (no components). Internal use only — Warmup, CopyEntity, CreateEntity<T>.
        private Entity CreateEntityInternal() {
            int index;

            if (_freeIndices.Count > 0) {
                index = _freeIndices.Pop();
            } else {
                index = _nextIndex++;
                if (index >= _generations.Length) {
                    GrowEntityCapacity(index);
                }
            }

            // Mask words are not zeroed here: a free slot's mask is guaranteed
            // zero by DestroyEntity's drain loop (exits only when every word
            // reads 0), by Clear, and by fresh allocation of new slots/words.
            _alive[index] = true;
            _componentCounts[index] = 0;
            _aliveCount++;

            var entity = new Entity(index, _generations[index]);
#if KENSEI_DEBUG
            EcsProfiler.OnEntityCreated(this, _tick, index, entity.Generation);
#endif
            // Reverse with a per-step bounds check — listeners may remove
            // themselves or others from the list during dispatch.
            for (int i = _eventListeners.Count - 1; i >= 0; i--) {
                if (i < _eventListeners.Count) {
                    _eventListeners[i].OnEntityCreated(index);
                }
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
        /// Generation is a full int — ~2 billion reuses per slot before overflow.
        /// Also called automatically when last component is removed.
        /// </summary>
        public void DestroyEntity(Entity entity) {
            if (!IsAlive(entity)) {
                return;
            }

            int idx = entity.Index;

            // Dead before anything else — listeners can call DestroyEntity or Remove
            // on this entity re-entrantly, and the IsAlive check above turns that into a no-op.
            _alive[idx] = false;

#if KENSEI_DEBUG
            _destroyDepth++;
            try {
            EcsProfiler.OnEntityDestroyed(this, _tick, idx, entity.Generation);
#endif

            // Reverse with a per-step bounds check — listeners may remove
            // themselves or others from the list during dispatch.
            for (int i = _eventListeners.Count - 1; i >= 0; i--) {
                if (i < _eventListeners.Count) {
                    _eventListeners[i].OnEntityDestroyed(idx);
                }
            }

            // Listeners may re-add components mid-removal, so re-read the live mask
            // and keep removing until it stays empty.
#if KENSEI_DEBUG
            int cleanupPasses = 0;
#endif
            bool hasComponents = true;
            while (hasComponents) {
#if KENSEI_DEBUG
                if (++cleanupPasses > 1000) {
                    throw new InvalidOperationException(
                        $"DestroyEntity({idx}) cannot finish: a listener keeps re-adding components to the dying entity on every removal pass");
                }
#endif
                hasComponents = false;
                for (int w = 0; w < _maskWordCount; w++) {
                    ulong mask = _componentMasks[w][idx];
                    if (mask == 0) {
                        continue;
                    }

                    hasComponents = true;
                    _componentMasks[w][idx] = 0;

                    while (mask != 0) {
                        int bit = TrailingZeroCount(mask);
                        int typeIdx = (w << 6) | bit;
                        _pools[typeIdx]?.Remove(idx);
                        mask &= mask - 1;
                    }
                }
            }

            _componentCounts[idx] = 0;
            _generations[idx]++;
            if (_generations[idx] == 0) _generations[idx] = 1;
            _aliveCount--;
            _freeIndices.Push(idx);
#if KENSEI_DEBUG
            } finally {
                _destroyDepth--;
            }
#endif
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
#if KENSEI_DEBUG
            if ((entityIndex >= _nextIndex || !_alive[entityIndex]) && _destroyDepth == 0) {
                throw new InvalidOperationException(
                    $"GetEntity({entityIndex}) on dead slot — the generation is already incremented, this would forge a handle for a future entity");
            }
#endif
            return new Entity(entityIndex, _generations[entityIndex]);
        }

        /// <summary>
        /// Copy an entity — creates a new entity with copies of all components.
        /// Returns the new entity.
        /// </summary>
        public Entity CopyEntity(Entity source) {
            if (!IsAlive(source)) {
#if KENSEI_DEBUG
                throw new InvalidOperationException(
                    $"CopyEntity on dead entity Entity({source.Index}, gen {source.Generation})");
#else
                return Entity.Null;
#endif
            }

            var copy = CreateEntityInternal();
            int srcIdx = source.Index;
            int dstIdx = copy.Index;

            for (int w = 0; w < _maskWordCount; w++) {
                ulong mask = _componentMasks[w][srcIdx];
                while (mask != 0) {
                    int bit = TrailingZeroCount(mask);
                    int typeIdx = (w << 6) | bit;
                    _pools[typeIdx].CopyTo(srcIdx, dstIdx);
                    mask &= mask - 1;
                }
            }

            return copy;
        }

        // =====================================================================
        // Component access
        // =====================================================================

        /// <summary> Get (or lazily create) a typed component pool. </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ComponentPool<T> Pool<T>() where T : struct, IComponent {
            int typeIdx = ComponentType<T>.Index;
            var pools = _pools;
            if ((uint)typeIdx < (uint)pools.Length && pools[typeIdx] is ComponentPool<T> pool) {
                return pool;
            }

            return CreatePool<T>(typeIdx);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private ComponentPool<T> CreatePool<T>(int typeIdx) where T : struct, IComponent {
            EnsurePoolCapacity(typeIdx);
            EnsureMaskCapacity(typeIdx);

            var pool = new ComponentPool<T>(this, _config.InitialPoolSparseCapacity, _config.InitialPoolDenseCapacity);
            _pools[typeIdx] = pool;
            return pool;
        }

        /// <summary> Add a component to an entity. </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T Add<T>(Entity entity, T component) where T : struct, IComponent {
#if KENSEI_DEBUG
            ValidateHandle<T>(entity, "Add");
#endif
            return ref Pool<T>().Add(entity.Index, component);
        }

        /// <summary> Get a component by ref. </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T Get<T>(Entity entity) where T : struct, IComponent {
#if KENSEI_DEBUG
            ValidateHandle<T>(entity, "Get");
#endif
            return ref Pool<T>().Get(entity.Index);
        }

        /// <summary> Check if entity has a component. </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Has<T>(Entity entity) where T : struct, IComponent {
#if KENSEI_DEBUG
            ValidateHandle<T>(entity, "Has");
#endif
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
        }

        /// <summary> Remove a component from an entity. </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Remove<T>(Entity entity) where T : struct, IComponent {
#if KENSEI_DEBUG
            ValidateHandle<T>(entity, "Remove");
#endif
            Pool<T>().Remove(entity.Index);
        }

#if KENSEI_DEBUG
        private void ValidateHandle<T>(Entity entity, string operation) where T : struct, IComponent {
            int idx = entity.Index;
            if (idx >= 0 && idx < _nextIndex && _generations[idx] == entity.Generation) {
                // A dead slot with a matching generation only exists mid-destroy
                // (generation is bumped when destruction completes) — listeners
                // legitimately touch the dying entity there.
                if (_alive[idx] || _destroyDepth > 0) {
                    return;
                }
            }

            throw new InvalidOperationException(
                $"{operation}<{typeof(T).Name}> on dead entity Entity({idx}, gen {entity.Generation})");
        }
#endif

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

            // Pre-allocate mask words for every word this filter constrains,
            // so EntityMatchesFilter never has to bounds-check against
            // _maskWordCount — an unregistered type's word just reads zeros.
            EnsureMaskWords(filter.IncludeMask.Length);

            foreach (int typeIdx in includes) {
                AddFilterToType(typeIdx, filter);
            }

            foreach (int typeIdx in excludes) {
                AddFilterToType(typeIdx, filter);
            }

            PopulateFilter(filter);

            return filter;
        }

        private void AddFilterToType(int typeIndex, Filter filter) {
            EnsureFiltersByTypeCapacity(typeIndex);
            var filters = _filtersByType[typeIndex];
            if (filters == null) {
                _filtersByType[typeIndex] = new[] { filter };
                return;
            }

            Array.Resize(ref filters, filters.Length + 1);
            filters[filters.Length - 1] = filter;
            _filtersByType[typeIndex] = filters;
        }

        // =====================================================================
        // Filter notifications — called by ComponentPool on Add/Remove
        // =====================================================================

        /// <summary>
        /// Called by ComponentPool after a component is added.
        /// Increments component count and updates relevant filters.
        /// </summary>
        internal void OnComponentAdded(int entityIndex, int typeIndex) {
            _componentCounts[entityIndex]++;
            _componentMasks[typeIndex >> 6][entityIndex] |= 1UL << (typeIndex & 63);

            var filtersByType = _filtersByType;
            if ((uint)typeIndex < (uint)filtersByType.Length) {
                var filters = filtersByType[typeIndex];
                if (filters != null) {
                    for (int i = 0; i < filters.Length; i++) {
                        UpdateFilterForEntity(filters[i], entityIndex);
                    }
                }
            }

            // Reverse with a per-step bounds check — listeners may remove
            // themselves or others from the list during dispatch.
            for (int i = _eventListeners.Count - 1; i >= 0; i--) {
                if (i < _eventListeners.Count) {
                    _eventListeners[i].OnComponentAdded(entityIndex, typeIndex);
                }
            }
        }

        /// <summary>
        /// Called by ComponentPool after a component is removed.
        /// Decrements component count, updates filters,
        /// and auto-destroys the entity if no components remain.
        /// </summary>
        internal void OnComponentRemoved(int entityIndex, int typeIndex) {
            if (_alive[entityIndex]) {
                _componentCounts[entityIndex]--;
            }
            _componentMasks[typeIndex >> 6][entityIndex] &= ~(1UL << (typeIndex & 63));

            var filtersByType = _filtersByType;
            if ((uint)typeIndex < (uint)filtersByType.Length) {
                var filters = filtersByType[typeIndex];
                if (filters != null) {
                    for (int i = 0; i < filters.Length; i++) {
                        UpdateFilterForEntity(filters[i], entityIndex);
                    }
                }
            }

            // Reverse with a per-step bounds check — listeners may remove
            // themselves or others from the list during dispatch.
            for (int i = _eventListeners.Count - 1; i >= 0; i--) {
                if (i < _eventListeners.Count) {
                    _eventListeners[i].OnComponentRemoved(entityIndex, typeIndex);
                }
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

        // No bounds check against _maskWordCount here: RegisterFilter
        // pre-allocates mask words for every word a registered filter
        // constrains, so _componentMasks[w] always exists.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool EntityMatchesFilter(Filter filter, int entityIndex) {
            int w = filter.SingleWord;
            if (w >= 0) {
                ulong entityWord = _componentMasks[w][entityIndex];
                ulong include = filter.SingleIncludeMask;
                return (entityWord & include) == include
                    && (entityWord & filter.SingleExcludeMask) == 0;
            }

            return EntityMatchesFilterMultiWord(filter, entityIndex);
        }

        private bool EntityMatchesFilterMultiWord(Filter filter, int entityIndex) {
            var includeMask = filter.IncludeMask;
            var excludeMask = filter.ExcludeMask;
            var activeWords = filter.ActiveWords;

            for (int i = 0; i < activeWords.Length; i++) {
                int w = activeWords[i];
                ulong entityWord = _componentMasks[w][entityIndex];
                if ((entityWord & includeMask[w]) != includeMask[w]) {
                    return false;
                }
                if ((entityWord & excludeMask[w]) != 0) {
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

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void GrowEntityCapacity(int index) {
            int oldSize = _generations.Length;
            int newSize = Math.Max(oldSize * 2, index + 1);
            Array.Resize(ref _generations, newSize);
            Array.Fill(_generations, 1, oldSize, newSize - oldSize);
            Array.Resize(ref _alive, newSize);
            Array.Resize(ref _componentCounts, newSize);
            for (int w = 0; w < _maskWordCount; w++) {
                Array.Resize(ref _componentMasks[w], newSize);
            }
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

        private void EnsureMaskCapacity(int typeIndex) {
            EnsureMaskWords((typeIndex >> 6) + 1);
        }

        private void EnsureMaskWords(int needed) {
            if (needed <= _maskWordCount) {
                return;
            }

            int entityCapacity = _generations.Length;
            Array.Resize(ref _componentMasks, needed);
            for (int w = _maskWordCount; w < needed; w++) {
                _componentMasks[w] = new ulong[entityCapacity];
            }
            _maskWordCount = needed;
        }

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
