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
    ///
    /// Not thread-safe. All access to a World, its pools and filters must happen
    /// on one thread (or be externally synchronized).
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
        private int[] _freeIndices;        // free slot stack
        private int _freeCount;
        internal int _nextIndex;           // next unused index
        private int _aliveCount;

        // --- Component storage ---
        // Indexed by ComponentType<T>.Index for O(1) access
        internal ComponentPoolBase[] _pools;

        // --- Filter registry ---
        private readonly List<Filter> _allFilters = new();

        // typeIndex → filters constrained by this type, split by constraint kind.
        // Adding T can only make an entity enter Inc/Any filters and leave Exc
        // filters; removing T only the reverse. So half of the updates skip the
        // mask test entirely. Jagged arrays instead of List<Filter> — the
        // notification hot path iterates them on every structural change.
        private Filter[][] _includeFilters;
        private Filter[][] _excludeFilters;
        private Filter[][] _anyFilters;

        // --- World event listeners ---
        // Copy-on-write: dispatch iterates the array it captured, so a listener
        // that subscribes or unsubscribes mid-dispatch never shifts the loop.
        private IWorldEventListener[] _eventListeners = Array.Empty<IWorldEventListener>();

        // Warmup runs the full Add/Remove machinery on a dummy entity; listeners
        // and the profiler must not observe it.
        internal bool _suppressEvents;

        // --- Tick counter ---
        private int _tick;

#if KENSEI_DEBUG
        // Depth of nested DestroyEntity calls. While > 0, listeners legitimately
        // operate on the dying entity (dead flag set, generation unchanged),
        // so handle validation must not reject it.
        internal int _destroyDepth;
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
            var old = _eventListeners;
            var grown = new IWorldEventListener[old.Length + 1];
            Array.Copy(old, grown, old.Length);
            grown[old.Length] = listener;
            _eventListeners = grown;
        }

        /// <summary> Unregister a world event listener. </summary>
        public void RemoveEventListener(IWorldEventListener listener) {
            var old = _eventListeners;
            int idx = Array.IndexOf(old, listener);
            if (idx < 0) {
                return;
            }

            var shrunk = new IWorldEventListener[old.Length - 1];
            Array.Copy(old, 0, shrunk, 0, idx);
            Array.Copy(old, idx + 1, shrunk, idx, old.Length - idx - 1);
            _eventListeners = shrunk;
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

                public ComponentPoolBase Current => _world._pools[_index];

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
            _freeIndices = new int[Math.Max(16, _config.InitialEntityCapacity / 4)];
            _freeCount = 0;
            _pools = new ComponentPoolBase[_config.InitialPoolCount];
            _includeFilters = new Filter[_config.InitialPoolCount][];
            _excludeFilters = new Filter[_config.InitialPoolCount][];
            _anyFilters = new Filter[_config.InitialPoolCount][];
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
        /// Does not fire world events.
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
            _freeCount = 0;
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
#if KENSEI_DEBUG
            EcsProfiler.OnWorldDestroyed(this);
#endif
            _eventListeners = Array.Empty<IWorldEventListener>();
            _generations = null;
            _alive = null;
            _componentCounts = null;
            _componentMasks = null;
            _freeIndices = null;
            _pools = null;
            _allFilters.Clear();
            _includeFilters = null;
            _excludeFilters = null;
            _anyFilters = null;
        }

        // =====================================================================
        // Warmup
        // =====================================================================

        /// <summary>
        /// Pre-touch pools and filters to trigger JIT compilation and memory allocation.
        /// Creates a temporary entity, adds a default component of every registered type
        /// (exercises Add paths, component masks, and filter insertion), then destroys it
        /// (exercises Remove paths and filter removal).
        /// Existing entities and their data are not touched. World event listeners
        /// and the profiler do not observe the temporary entity.
        /// Call once before gameplay starts (e.g. during loading screen).
        /// </summary>
        public void Warmup() {
            _suppressEvents = true;
            try {
                var dummy = CreateEntityInternal();

                for (int i = 0; i < _pools.Length; i++) {
                    _pools[i]?.AddDefault(dummy.Index);
                }

                DestroyEntity(dummy);
            } finally {
                _suppressEvents = false;
            }
        }

        // Allocate a live slot with no components. Callers must add at least one
        // component before dispatching OnEntityCreated.
        private Entity CreateEntityInternal() {
            int index;

            if (_freeCount > 0) {
                index = _freeIndices[--_freeCount];
                // Generation changes when a slot is reused, not when it is freed:
                // GetEntity on a dead slot then yields the dead entity's own handle
                // instead of forging the handle of whatever lives there next.
                int generation = _generations[index] + 1;
                if (generation == 0) {
                    generation = 1;
                }
                _generations[index] = generation;
            } else {
                index = _nextIndex++;
                if (index >= _generations.Length) {
                    GrowEntityCapacity(index);
                }
            }

            // Mask words are not zeroed here: a free slot's mask is guaranteed
            // zero by DestroyEntity (masks are cleared when the slot is released),
            // by Clear, and by fresh allocation of new slots/words.
            _alive[index] = true;
            _componentCounts[index] = 0;
            _aliveCount++;

            var entity = new Entity(index, _generations[index]);
#if KENSEI_DEBUG
            EcsProfiler.OnEntityCreated(this, _tick, index, entity.Generation);
#endif
            return entity;
        }

        /// <summary>
        /// Create an entity with one initial component.
        /// OnEntityCreated fires after the component is added, so listeners never
        /// observe an entity without components.
        /// </summary>
        public Entity CreateEntity<T>(T component) where T : struct, IComponent {
            var entity = CreateEntityInternal();
            Add(entity, component);
            DispatchEntityCreated(entity.Index);
            return entity;
        }

        /// <summary>
        /// Destroy an entity. O(number of component types on the entity).
        /// Removes all components and returns the slot to the free list; the slot's
        /// generation changes when it is reused.
        /// Also called automatically when the last component is removed.
        /// If a listener or AutoReset throws, the slot is still released, but
        /// components not yet removed stay orphaned in their pools and filters.
        /// </summary>
        public void DestroyEntity(Entity entity) {
            if (!IsAlive(entity)) {
                return;
            }

            DestroyEntityInternal(entity.Index);
        }

        private void DestroyEntityInternal(int idx) {
            // Dead before anything else — listeners can call DestroyEntity or Remove
            // on this entity re-entrantly, and the IsAlive check turns that into a no-op.
            _alive[idx] = false;

#if KENSEI_DEBUG
            _destroyDepth++;
            EcsProfiler.OnEntityDestroyed(this, _tick, idx, _generations[idx]);
#endif
            try {
                DispatchEntityDestroyed(idx);
            } finally {
                try {
                    DrainComponents(idx);
                } finally {
                    ReleaseSlot(idx);
#if KENSEI_DEBUG
                    _destroyDepth--;
#endif
                }
            }
        }

        // Listeners may re-add components mid-removal. OnComponentAdded bumps the
        // count unconditionally and OnComponentRemoved leaves it alone for a dead
        // entity, so a non-zero count after a pass means "something was re-added":
        // one int read instead of re-scanning every mask word.
        private void DrainComponents(int idx) {
#if KENSEI_DEBUG
            int cleanupPasses = 0;
#endif
            do {
#if KENSEI_DEBUG
                if (++cleanupPasses > 1000) {
                    throw new InvalidOperationException(
                        $"DestroyEntity({idx}) cannot finish: a listener keeps re-adding components to the dying entity on every removal pass");
                }
#endif
                _componentCounts[idx] = 0;
                for (int w = 0; w < _maskWordCount; w++) {
                    ulong mask = _componentMasks[w][idx];
                    if (mask == 0) {
                        continue;
                    }

                    _componentMasks[w][idx] = 0;

                    while (mask != 0) {
                        int bit = TrailingZeroCount(mask);
                        int typeIdx = (w << 6) | bit;
                        _pools[typeIdx].Remove(idx);
                        mask &= mask - 1;
                    }
                }
            } while (_componentCounts[idx] != 0);
        }

        private void ReleaseSlot(int idx) {
            _componentCounts[idx] = 0;
            for (int w = 0; w < _maskWordCount; w++) {
                _componentMasks[w][idx] = 0;
            }
            _aliveCount--;

            if (_freeCount == _freeIndices.Length) {
                Array.Resize(ref _freeIndices, _freeIndices.Length * 2);
            }
            _freeIndices[_freeCount++] = idx;
        }

        /// <summary> Check if entity is alive. O(1). </summary>
        public bool IsAlive(Entity entity) {
            int idx = entity.Index;
            return idx < _nextIndex
                && _alive[idx]
                && _generations[idx] == entity.Generation;
        }

        /// <summary>
        /// Get the Entity handle for a slot index.
        /// For a dead slot this returns the handle of the entity that last lived
        /// there (IsAlive is false for it). An int index from a filter is only
        /// valid until the end of the current iteration — once the slot is reused
        /// the same int names a different entity.
        /// </summary>
        public Entity GetEntity(int entityIndex) {
            return new Entity(entityIndex, _generations[entityIndex]);
        }

        /// <summary>
        /// Copy an entity — creates a new entity with copies of all components.
        /// Returns the new entity. OnEntityCreated fires after all components are copied.
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

            DispatchEntityCreated(dstIdx);
            return copy;
        }

        /// <summary>
        /// Append the type indices of all components on the entity to result.
        /// Returns the number of components. Resolve names via ComponentType.TypeOf.
        /// </summary>
        public int GetComponentTypes(Entity entity, List<int> result) {
#if KENSEI_DEBUG
            if (!IsAlive(entity)) {
                throw new InvalidOperationException(
                    $"GetComponentTypes on dead entity Entity({entity.Index}, gen {entity.Generation})");
            }
#endif
            int idx = entity.Index;
            int added = 0;
            for (int w = 0; w < _maskWordCount; w++) {
                ulong mask = _componentMasks[w][idx];
                while (mask != 0) {
                    int bit = TrailingZeroCount(mask);
                    result.Add((w << 6) | bit);
                    added++;
                    mask &= mask - 1;
                }
            }
            return added;
        }

        /// <summary> Number of components on the entity. O(1). </summary>
        public int GetComponentCount(Entity entity) {
#if KENSEI_DEBUG
            if (!IsAlive(entity)) {
                throw new InvalidOperationException(
                    $"GetComponentCount on dead entity Entity({entity.Index}, gen {entity.Generation})");
            }
#endif
            return _componentCounts[entity.Index];
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

        /// <summary> Untyped pool for a type index, or null if no component of that type was ever added. </summary>
        public ComponentPoolBase GetPool(int typeIndex) {
            var pools = _pools;
            return (uint)typeIndex < (uint)pools.Length ? pools[typeIndex] : null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private ComponentPool<T> CreatePool<T>(int typeIdx) where T : struct, IComponent {
            EnsurePoolCapacity(typeIdx);
            EnsureMaskCapacity(typeIdx);

            var pool = new ComponentPool<T>(this, _config.InitialPoolSparseCapacity, _config.InitialPoolDenseCapacity);
            _pools[typeIdx] = pool;
            return pool;
        }

        /// <summary> Add a component to an entity. Throws if the entity already has it. </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T Add<T>(Entity entity, T component) where T : struct, IComponent {
#if KENSEI_DEBUG
            ValidateHandle<T>(entity, "Add");
#endif
            return ref Pool<T>().Add(entity.Index, component);
        }

        /// <summary>
        /// Get a component by ref.
        /// The ref is valid until the next Add of the same component type (the pool may grow).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T Get<T>(Entity entity) where T : struct, IComponent {
#if KENSEI_DEBUG
            ValidateHandle<T>(entity, "Get");
#endif
            return ref Pool<T>().Get(entity.Index);
        }

        /// <summary> Check if entity has a component. Does not create the pool. </summary>
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

        /// <summary>
        /// Remove a component from an entity. No-op if the entity does not have it;
        /// does not create the pool. Auto-destroys the entity if it was the last component.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Remove<T>(Entity entity) where T : struct, IComponent {
#if KENSEI_DEBUG
            ValidateHandle<T>(entity, "Remove");
#endif
            int typeIdx = ComponentType<T>.Index;
            var pools = _pools;
            if ((uint)typeIdx < (uint)pools.Length && pools[typeIdx] is ComponentPool<T> pool) {
                pool.Remove(entity.Index);
            }
        }

        // =====================================================================
        // Singletons — a component type that exists on exactly one entity
        // =====================================================================

        /// <summary> True when exactly one entity has a component of type T. </summary>
        public bool HasSingleton<T>() where T : struct, IComponent {
            int typeIdx = ComponentType<T>.Index;
            var pools = _pools;
            return (uint)typeIdx < (uint)pools.Length && pools[typeIdx] is ComponentPool<T> pool && pool.Count == 1;
        }

        /// <summary> The only component of type T. Throws unless exactly one entity has it. </summary>
        public ref T GetSingleton<T>() where T : struct, IComponent {
            var pool = Pool<T>();
            if (pool.Count != 1) {
                ThrowNotSingleton<T>(pool.Count);
            }
            return ref pool.RawData[0];
        }

        /// <summary> The entity holding the only component of type T. Throws unless exactly one entity has it. </summary>
        public Entity GetSingletonEntity<T>() where T : struct, IComponent {
            var pool = Pool<T>();
            if (pool.Count != 1) {
                ThrowNotSingleton<T>(pool.Count);
            }
            return GetEntity(pool.RawEntities[0]);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowNotSingleton<T>(int count) {
            throw new InvalidOperationException(
                $"Singleton<{typeof(T).Name}> requires exactly one entity with the component, found {count}");
        }

#if KENSEI_DEBUG
        private void ValidateHandle<T>(Entity entity, string operation) where T : struct, IComponent {
            int idx = entity.Index;
            if (idx >= 0 && idx < _nextIndex && _generations[idx] == entity.Generation) {
                // A dead slot with a matching generation is either mid-destroy
                // (listeners legitimately touch the dying entity) or a stale handle
                // whose slot has not been reused yet.
                if (_alive[idx] || _destroyDepth > 0) {
                    return;
                }
            }

            throw new InvalidOperationException(
                $"{operation}<{typeof(T).Name}> on dead entity Entity({idx}, gen {entity.Generation})");
        }

        // Pool int-API guard: an int index carries no generation, so the only
        // thing that can be checked is that the slot is alive (or dying).
        internal bool IsSlotAcceptingComponents(int entityIndex) {
            return (uint)entityIndex < (uint)_nextIndex
                && (_alive[entityIndex] || _destroyDepth > 0);
        }
#endif

        // =====================================================================
        // Filter API
        // =====================================================================

        /// <summary> Start building a new filter. Build filters in Init, not per frame. </summary>
        public FilterBuilder Filter() {
            return new FilterBuilder(this);
        }

        /// <summary> Filter from a static spec: world.Filter&lt;Inc&lt;Position, Velocity&gt;&gt;(). </summary>
        public Filter Filter<TSpec>()
            where TSpec : struct, IFilterSpec {
            var builder = new FilterBuilder(this);
            default(TSpec).Apply(builder);
            return builder.End();
        }

        /// <summary> Filter from two static specs: world.Filter&lt;Inc&lt;Position&gt;, Exc&lt;Frozen&gt;&gt;(). </summary>
        public Filter Filter<TSpec1, TSpec2>()
            where TSpec1 : struct, IFilterSpec
            where TSpec2 : struct, IFilterSpec {
            var builder = new FilterBuilder(this);
            default(TSpec1).Apply(builder);
            default(TSpec2).Apply(builder);
            return builder.End();
        }

        /// <summary> Filter from three static specs: world.Filter&lt;Inc&lt;A&gt;, Exc&lt;B&gt;, Any&lt;C, D&gt;&gt;(). </summary>
        public Filter Filter<TSpec1, TSpec2, TSpec3>()
            where TSpec1 : struct, IFilterSpec
            where TSpec2 : struct, IFilterSpec
            where TSpec3 : struct, IFilterSpec {
            var builder = new FilterBuilder(this);
            default(TSpec1).Apply(builder);
            default(TSpec2).Apply(builder);
            default(TSpec3).Apply(builder);
            return builder.End();
        }

        /// <summary>
        /// Register a filter. If identical constraints already exist, returns existing filter.
        /// Populates the filter with all currently matching entities.
        /// </summary>
        internal Filter RegisterFilter(int[] includes, int[] excludes, int[] any) {
            foreach (var existing in _allFilters) {
                if (ArraysEqual(existing.IncludedTypeIndices, includes)
                    && ArraysEqual(existing.ExcludedTypeIndices, excludes)
                    && ArraysEqual(existing.AnyTypeIndices, any)) {
                    return existing;
                }
            }

            var filter = new Filter(includes, excludes, any, _config.InitialEntityCapacity, _config.InitialPoolDenseCapacity);
            _allFilters.Add(filter);

            // Pre-allocate mask words for every word this filter constrains,
            // so EntityMatchesFilter never has to bounds-check against
            // _maskWordCount — an unregistered type's word just reads zeros.
            EnsureMaskWords(filter.IncludeMask.Length);

            foreach (int typeIdx in includes) {
                AddFilterToType(ref _includeFilters, typeIdx, filter);
            }

            foreach (int typeIdx in excludes) {
                AddFilterToType(ref _excludeFilters, typeIdx, filter);
            }

            foreach (int typeIdx in any) {
                AddFilterToType(ref _anyFilters, typeIdx, filter);
            }

            PopulateFilter(filter);

            return filter;
        }

        private static void AddFilterToType(ref Filter[][] table, int typeIndex, Filter filter) {
            if (typeIndex >= table.Length) {
                Array.Resize(ref table, Math.Max(table.Length * 2, typeIndex + 1));
            }

            var filters = table[typeIndex];
            if (filters == null) {
                table[typeIndex] = new[] { filter };
                return;
            }

            Array.Resize(ref filters, filters.Length + 1);
            filters[filters.Length - 1] = filter;
            table[typeIndex] = filters;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Filter[] FiltersFor(Filter[][] table, int typeIndex) {
            return (uint)typeIndex < (uint)table.Length ? table[typeIndex] : null;
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

            var include = FiltersFor(_includeFilters, typeIndex);
            if (include != null) {
                for (int i = 0; i < include.Length; i++) {
                    var filter = include[i];
                    if (EntityMatchesFilter(filter, entityIndex)) {
                        filter.AddEntity(entityIndex);
                    }
                }
            }

            var exclude = FiltersFor(_excludeFilters, typeIndex);
            if (exclude != null) {
                for (int i = 0; i < exclude.Length; i++) {
                    exclude[i].RemoveEntity(entityIndex);
                }
            }

            var any = FiltersFor(_anyFilters, typeIndex);
            if (any != null) {
                for (int i = 0; i < any.Length; i++) {
                    var filter = any[i];
                    if (EntityMatchesFilter(filter, entityIndex)) {
                        filter.AddEntity(entityIndex);
                    }
                }
            }

            if (_suppressEvents) {
                return;
            }
            var listeners = _eventListeners;
            for (int i = 0; i < listeners.Length; i++) {
                listeners[i].OnComponentAdded(entityIndex, typeIndex);
            }
        }

        /// <summary>
        /// Called by ComponentPool after a component is removed.
        /// Decrements component count, updates filters,
        /// and auto-destroys the entity if no components remain.
        /// Listeners observe the entity while it is still alive; when the removed
        /// component was the last one, auto-destroy follows the dispatch.
        /// </summary>
        internal void OnComponentRemoved(int entityIndex, int typeIndex) {
            if (_alive[entityIndex]) {
                _componentCounts[entityIndex]--;
            }
            _componentMasks[typeIndex >> 6][entityIndex] &= ~(1UL << (typeIndex & 63));

            var include = FiltersFor(_includeFilters, typeIndex);
            if (include != null) {
                for (int i = 0; i < include.Length; i++) {
                    include[i].RemoveEntity(entityIndex);
                }
            }

            var exclude = FiltersFor(_excludeFilters, typeIndex);
            if (exclude != null) {
                for (int i = 0; i < exclude.Length; i++) {
                    var filter = exclude[i];
                    if (EntityMatchesFilter(filter, entityIndex)) {
                        filter.AddEntity(entityIndex);
                    }
                }
            }

            var any = FiltersFor(_anyFilters, typeIndex);
            if (any != null) {
                for (int i = 0; i < any.Length; i++) {
                    UpdateFilterForEntity(any[i], entityIndex);
                }
            }

            if (!_suppressEvents) {
                var listeners = _eventListeners;
                for (int i = 0; i < listeners.Length; i++) {
                    listeners[i].OnComponentRemoved(entityIndex, typeIndex);
                }
            }

            if (_componentCounts[entityIndex] == 0 && _alive[entityIndex]) {
                DestroyEntityInternal(entityIndex);
            }
        }

        // =====================================================================
        // Private
        // =====================================================================

        private void DispatchEntityCreated(int entityIndex) {
            if (_suppressEvents) {
                return;
            }
            var listeners = _eventListeners;
            for (int i = 0; i < listeners.Length; i++) {
                listeners[i].OnEntityCreated(entityIndex);
            }
        }

        private void DispatchEntityDestroyed(int entityIndex) {
            if (_suppressEvents) {
                return;
            }
            var listeners = _eventListeners;
            for (int i = 0; i < listeners.Length; i++) {
                listeners[i].OnEntityDestroyed(entityIndex);
            }
        }

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
                    && (entityWord & filter.SingleExcludeMask) == 0
                    && (entityWord & filter.SingleAnyMask) != 0;
            }

            return EntityMatchesFilterMultiWord(filter, entityIndex);
        }

        private bool EntityMatchesFilterMultiWord(Filter filter, int entityIndex) {
            var includeMask = filter.IncludeMask;
            var excludeMask = filter.ExcludeMask;
            var anyMask = filter.AnyMask;
            var activeWords = filter.ActiveWords;
            bool anyHit = false;

            for (int i = 0; i < activeWords.Length; i++) {
                int w = activeWords[i];
                ulong entityWord = _componentMasks[w][entityIndex];
                if ((entityWord & includeMask[w]) != includeMask[w]) {
                    return false;
                }
                if ((entityWord & excludeMask[w]) != 0) {
                    return false;
                }
                anyHit |= (entityWord & anyMask[w]) != 0;
            }

            return anyHit || !filter.HasAny;
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

#if NET5_0_OR_GREATER
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int TrailingZeroCount(ulong value) {
            return System.Numerics.BitOperations.TrailingZeroCount(value);
        }
#else
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
#endif
    }
}
