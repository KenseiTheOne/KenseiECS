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

        // Highest entity count passed to ReserveEntities (or the obsolete Reserve): the world's own
        // per-entity arrays cover it, and so does the sparse side of every reserved pool and filter,
        // which follows it when it grows. Unreserved pools and filters never read it.
        private int _reservedEntities;

        // The obsolete Reserve(entities, components) sizes every pool and filter, the ones created
        // later included: its highest arguments. 0 while it was never called.
        private int _everyPoolEntities;
        private int _everyPoolComponents;

#if KENSEI_DEBUG
        // Storage growths in a dimension that Reserve covered (see GrowthsAfterReserve),
        // and the first of them (see FirstGrowthAfterReserve).
        private int _growthsAfterReserve;
        private StorageGrowth _firstGrowthAfterReserve;
#endif

        // --- Entity storage ---
        internal int[] _generations;       // _generations[index] = current slot generation
        internal bool[] _alive;            // _alive[index] = true if entity is alive
        internal int[] _componentCounts;   // _componentCounts[index] = number of components on entity
        // Multi-word bitmask: _componentMasks[wordIndex][entityIndex]
        // Tracks which component types each entity has, for O(1) filter matching.
        internal ulong[][] _componentMasks;
        private int _maskWordCount;
        // Per slot, the mask words from 1 on that may hold a component of the entity: bit w for word
        // w (1 to 62), bit 63 for every word from 63 on. An Add sets the bit of its word when the word
        // was zero, so a clear bit means a zero word; only releasing the slot and Clear reset it (a
        // word emptied by Remove keeps its bit). DestroyEntity and CopyEntity read word 0 and then only
        // the words whose bit is set. Null while the world has a single mask word.
        internal ulong[] _usedWords;
        // Set when an interrupted destroy (a throwing listener or AutoReset) released a slot with bits
        // still set: their components stay orphaned in the pools. Removing an orphan later lowers the
        // count of the entity that took the slot without clearing a bit of it, so the count no longer
        // bounds the entity's bits and DestroyEntity reads every word instead of stopping at the
        // count. Clear, which empties the pools, resets it.
        private bool _orphans;
        private int[] _freeIndices;        // free slot stack
        private int _freeCount;
        internal int _nextIndex;           // next unused index
        private int _aliveCount;

        // --- Component storage ---
        // Indexed by ComponentType<T>.Index for O(1) access
        internal ComponentPoolBase[] _pools;

        // --- Filter registry ---
        private readonly List<Filter> _allFilters = new();

        // --- Owning groups ---
        private readonly List<Group> _groups = new();

        // typeIndex → filters constrained by this type, split by constraint kind.
        // Adding T can only make an entity enter Inc/Any filters and leave Exc
        // filters; removing T only the reverse. So half of the updates skip the
        // mask test entirely. One entry per type (null when no filter mentions
        // it) keeps a structural change on an unfiltered type at a single branch.
        private sealed class TypeFilters {
            public Filter[] Include = Array.Empty<Filter>();
            public Filter[] Exclude = Array.Empty<Filter>();
            public Filter[] Any = Array.Empty<Filter>();
            // How many of these filters have IFilterListener subscribers (a filter listed twice counts
            // twice): while 0, adding the type runs no filter listener.
            public int Listening;
        }

        private TypeFilters[] _filtersByType;

        // --- World event listeners ---
        // Copy-on-write: dispatch iterates the array it captured, so a listener
        // that subscribes or unsubscribes mid-dispatch never shifts the loop.
        private IWorldEventListener[] _eventListeners = Array.Empty<IWorldEventListener>();

        // Warmup and snapshot restore run the full Add/Remove machinery;
        // listeners and the profiler must not observe it.
        internal bool _suppressEvents;

        // _eventListeners.Length > 0 && !_suppressEvents, kept in sync so the
        // structural hot path tests one bool.
        private bool _dispatch;

        // Incremented by Clear: a DestroyEntity that a listener interrupted with Clear stops (the
        // slot is no longer the dying entity's).
        private int _clears;

        // --- Deferred listener removals (WorldListenerExtensions.UnsubscribeLater) ---
        // Nesting depth of SystemsRunner.Run on this world: the queued removals are applied when the
        // outermost Run returns, where no system is iterating a listener list.
        private int _runDepth;
        private readonly List<IPendingUnsubscribes> _pendingUnsubscribes = new();
        private bool _hasPendingUnsubscribes;

        // Incremented whenever a slot is released (DestroyEntity) and by Clear: CopyEntity checks
        // the handles of the copy and the source only after it changed.
        private int _releases;

        // --- Tick counter ---
        private int _tick;

        // Monotonic counter behind change tracking: every Add or Modify on a
        // tracking pool takes the next value, so "changed since I last ran" is
        // exact regardless of system order within a frame.
        private int _changeVersion;

        /// <summary>
        /// Current change version. Capture it at the end of a system's Run and pass it to
        /// pool.ChangedSince next time to see only components modified since then.
        /// </summary>
        public int ChangeVersion => _changeVersion;

        internal int NextChangeVersion() {
            return ++_changeVersion;
        }

#if KENSEI_DEBUG
        // Slots whose DestroyEntity is running, innermost last. Listeners legitimately operate
        // on a dying entity (dead flag set, generation unchanged), so handle validation accepts
        // these slots, and only these: a stale handle stays rejected while another entity dies.
        private int[] _dying = new int[4];
        private int _dyingCount;

        private readonly Dictionary<int, string> _names = new();
#endif

        /// <summary>
        /// Attach a debug name to an entity, shown by the inspector and profiler.
        /// Does nothing unless KenseiECS is compiled with KENSEI_DEBUG.
        /// </summary>
        /// <remarks>
        /// The call itself is never removed, so its arguments are always evaluated:
        /// <c>world.SetName(world.CreateEntity(...), "Player")</c> creates the entity in every
        /// configuration. It is deliberately not <c>[Conditional]</c>, which would drop the
        /// arguments with the call and silently skip their side effects without the define.
        /// A name built per call (string interpolation at spawn) is still built in release;
        /// wrap such a call in <c>#if KENSEI_DEBUG</c> if that allocation matters.
        /// </remarks>
        public void SetName(Entity entity, string name) {
#if KENSEI_DEBUG
            if (!IsAlive(entity)) {
                throw new InvalidOperationException(
                    $"SetName on dead entity Entity({entity.Index}, gen {entity.Generation})");
            }
            if (name == null) {
                _names.Remove(entity.Index);
            } else {
                _names[entity.Index] = name;
            }
#endif
        }

        /// <summary> Debug name of an entity, or null. Always null unless KENSEI_DEBUG is defined. </summary>
        public string GetName(Entity entity) {
#if KENSEI_DEBUG
            return IsAlive(entity) && _names.TryGetValue(entity.Index, out var name) ? name : null;
#else
            return null;
#endif
        }

        /// <summary> Number of currently alive entities. </summary>
        public int EntityCount => _aliveCount;
        /// <summary> True after <see cref="Destroy"/> has been called. </summary>
        public bool IsDestroyed => _alive == null;

        /// <summary> Current tick number. Starts at 0, incremented by NextTick(). First run = tick 1. </summary>
        public int Tick => _tick;

        /// <summary>
        /// Advance the tick counter. Call once per frame before systems run. Outside a
        /// <see cref="SystemsRunner.Run(World)"/> it also applies the listener removals queued by
        /// <see cref="WorldListenerExtensions.UnsubscribeLater{T}"/>, for a world whose systems are not
        /// driven by a runner.
        /// </summary>
        public void NextTick() {
            _tick++;
            if (_runDepth == 0) {
                ApplyPendingUnsubscribes();
            }
        }

        // SystemsRunner.Run brackets its work with these. A system can run another runner's Run
        // inside its own, so only the outermost one applies the deferred removals.
        internal void EnterRun() {
            _runDepth++;
        }

        internal void ExitRun() {
            if (--_runDepth == 0) {
                ApplyPendingUnsubscribes();
            }
        }

        // ExitRun while a system's exception is on its way out of Run: a listener Equals that throws while
        // the queue is applied must not replace that exception, so its own is dropped here.
        internal void ExitRunAfterFailure() {
            if (--_runDepth == 0) {
                try {
                    ApplyPendingUnsubscribes();
                } catch {
                    // The system's exception is the one Run reports.
                }
            }
        }

        private void ApplyPendingUnsubscribes() {
            if (!_hasPendingUnsubscribes || IsDestroyed) {
                return;
            }
            for (int i = 0; i < _pendingUnsubscribes.Count; i++) {
                _pendingUnsubscribes[i].Apply(this);
            }
            // After the loop: if an Equals threw, the queues not reached yet go at the next Run.
            _hasPendingUnsubscribes = false;
        }

        // The removal queue of listener type T, created on first use; marks the world as having
        // removals to apply at the end of the outermost Run.
        internal PendingUnsubscribes<T> UnsubscribeQueue<T>() where T : class {
            _hasPendingUnsubscribes = true;
            for (int i = 0; i < _pendingUnsubscribes.Count; i++) {
                if (_pendingUnsubscribes[i] is PendingUnsubscribes<T> found) {
                    return found;
                }
            }
            var created = new PendingUnsubscribes<T>();
            _pendingUnsubscribes.Add(created);
            return created;
        }

        /// <summary> Register a world event listener. </summary>
        public void AddEventListener(IWorldEventListener listener) {
            var old = _eventListeners;
            var grown = new IWorldEventListener[old.Length + 1];
            Array.Copy(old, grown, old.Length);
            grown[old.Length] = listener;
            _eventListeners = grown;
            RefreshDispatch();
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
            RefreshDispatch();
        }

        internal void SuppressEvents(bool suppress) {
            _suppressEvents = suppress;
            RefreshDispatch();
        }

        private void RefreshDispatch() {
            _dispatch = _eventListeners.Length > 0 && !_suppressEvents;
        }

        // =====================================================================
        // Enumeration — safe, zero-allocation iteration for debug/editor tools
        // =====================================================================

        /// <summary> Enumerate all alive entities. Zero-allocation. </summary>
        public AliveEntityEnumerable AliveEntities => new(this);

        /// <summary> Enumerate all registered (non-null) component pools. Zero-allocation. </summary>
        public ActivePoolEnumerable ActivePools => new(this);

        /// <summary> Zero-allocation enumerable over alive entities, returned by <see cref="AliveEntities"/>. </summary>
        public readonly struct AliveEntityEnumerable {
            private readonly World _world;

            internal AliveEntityEnumerable(World world) {
                _world = world;
            }

            /// <summary> Returns the enumerator; used by <c>foreach</c>. </summary>
            public Enumerator GetEnumerator() {
                return new Enumerator(_world);
            }

            /// <summary> Struct enumerator — no allocation. </summary>
            public struct Enumerator {
                private readonly World _world;
                private readonly int _snapshotCount;
                private int _index;

                internal Enumerator(World world) {
                    _world = world;
                    _snapshotCount = world._nextIndex;
                    _index = -1;
                }

                /// <summary> Entity at the current position. </summary>
                public Entity Current => _world.GetEntity(_index);

                /// <summary> Advance to the next alive entity. Returns false when no more remain. </summary>
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

        /// <summary> Zero-allocation enumerable over registered component pools, returned by <see cref="ActivePools"/>. </summary>
        public readonly struct ActivePoolEnumerable {
            private readonly World _world;

            internal ActivePoolEnumerable(World world) {
                _world = world;
            }

            /// <summary> Returns the enumerator; used by <c>foreach</c>. </summary>
            public Enumerator GetEnumerator() {
                return new Enumerator(_world);
            }

            /// <summary> Struct enumerator — no allocation. </summary>
            public struct Enumerator {
                private readonly World _world;
                private int _index;

                internal Enumerator(World world) {
                    _world = world;
                    _index = -1;
                }

                /// <summary> Pool at the current position. </summary>
                public ComponentPoolBase Current => _world._pools[_index];

                /// <summary> Advance to the next non-null pool. Returns false when no more remain. </summary>
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

        /// <summary> Create a world with <see cref="WorldConfig.Default"/> settings. </summary>
        public World() : this(WorldConfig.Default()) { }

        /// <summary> Create a world with the given capacities. Non-positive values fall back to <see cref="WorldConfig.Default"/>. </summary>
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
            _filtersByType = new TypeFilters[_config.InitialPoolCount];
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
        /// <exception cref="ObjectDisposedException"> When the world was destroyed. </exception>
        public void Clear() {
            if (IsDestroyed) {
                throw new ObjectDisposedException(nameof(World), "World.Clear after World.Destroy: a destroyed world has nothing to clear");
            }
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
            if (_usedWords != null) {
                Array.Clear(_usedWords, 0, _nextIndex);
            }
            _freeCount = 0;
            _nextIndex = 0;
            _aliveCount = 0;
            _tick = 0;
            _clears++;
            _releases++;
#if KENSEI_DEBUG
            _names.Clear();
#endif

            for (int i = 0; i < _pools.Length; i++) {
                _pools[i]?.Clear();
            }
            _orphans = false;

            for (int i = 0; i < _allFilters.Count; i++) {
                _allFilters[i].Clear();
            }

            for (int i = 0; i < _groups.Count; i++) {
                _groups[i].Clear();
            }

            // Every entity is gone, so queued listener removals have nothing left to remove.
            for (int i = 0; i < _pendingUnsubscribes.Count; i++) {
                _pendingUnsubscribes[i].Clear();
            }
            _hasPendingUnsubscribes = false;
        }

        /// <summary>
        /// Fully destroy the world. Nulls all internal references so GC can collect.
        /// World instance should not be used after this call. Calling it again is a no-op;
        /// <see cref="Clear"/>, <see cref="ReserveEntities"/>, every Reserve and filter creation throw
        /// <see cref="ObjectDisposedException"/>, and so do CreateEntity and Pool under KENSEI_DEBUG.
        /// </summary>
        public void Destroy() {
            if (IsDestroyed) {
                return;
            }
            Clear();
#if KENSEI_DEBUG
            EcsProfiler.OnWorldDestroyed(this);
#endif
            _eventListeners = Array.Empty<IWorldEventListener>();
            _dispatch = false;
            _generations = null;
            _alive = null;
            _componentCounts = null;
            _componentMasks = null;
            _usedWords = null;
            _freeIndices = null;
            _pools = null;
            _allFilters.Clear();
            _filtersByType = null;
            // Groups reference their pools: kept, they would hold every owned pool's arrays
            // for as long as the destroyed world object is reachable.
            _groups.Clear();
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
        /// and the profiler do not observe the temporary entity, but typed pool listeners
        /// (IComponentListener) and filter listeners (IFilterListener) do: they get
        /// OnAdded/OnEntityAdded and then OnRemoved/OnEntityRemoved for it, with default
        /// component values. If a listener destroys the temporary entity (a rule that destroys
        /// entities with no health fires on the default value), the remaining types go to a new
        /// temporary entity, never to the freed slot or to an entity the listener created in it.
        /// A type a listener already added to the temporary entity is skipped. If a listener
        /// throws, the temporary entity is still destroyed.
        /// The EventBuffer list pool is not filled (the added components are default,
        /// without lists): use <see cref="EventBuffer{T}.Prewarm"/> for that, and
        /// <see cref="ReserveEntities"/> and <see cref="ComponentPoolBase.Reserve"/> to size storage for the peak.
        /// Call once before gameplay starts (e.g. during loading screen).
        /// </summary>
        public void Warmup() {
            SuppressEvents(true);
            var dummy = Entity.Null;
            try {
                dummy = CreateEntityInternal();

                for (int i = 0; i < _pools.Length; i++) {
                    var pool = _pools[i];
                    if (pool == null) {
                        continue;
                    }
                    // Checked by handle: a listener may have destroyed the temporary entity and
                    // created another one in its slot, which must not get the remaining types.
                    if (!IsAlive(dummy)) {
                        dummy = CreateEntityInternal();
                    } else if (pool.Has(dummy.Index)) {
                        continue;
                    }
                    pool.AddDefault(dummy.Index);
                }
            } finally {
                try {
                    DestroyEntity(dummy);
                } finally {
                    SuppressEvents(false);
                }
            }
        }

        // =====================================================================
        // Capacity
        // =====================================================================

        /// <summary>
        /// Reserve the world's own per-entity storage for <paramref name="entities"/> entity slots
        /// (indices 0 to entities - 1): generations, alive flags, component counts, every mask word (and,
        /// with more than one mask word, the summary of the words in use) and the free-slot list,
        /// sized exactly, so that up to <paramref name="entities"/> entities alive at once never grow
        /// them. Pools and filters are not touched, except the reserved ones: a pool
        /// reserved with <see cref="ComponentPoolBase.Reserve"/> and a filter with a reserve
        /// (<see cref="KenseiECS.Filter.ReservedCount"/> above 0) cover the reserved slots with their
        /// sparse (entity-indexed) side, extended here and by every later call. A filter's reserve taken
        /// from its types is capped at the slots, so it moves with them and its dense array can grow (a
        /// <see cref="KenseiECS.Filter.Entities"/> span taken before is then invalid). No array shrinks; a
        /// repeated call only raises <see cref="ReservedEntities"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The world, the hot pools and the hot filters, nothing else: with this call and
        /// <c>Pool&lt;T&gt;().Reserve(count)</c> for every component type whose count changes during
        /// play, creating and destroying entities (at most <paramref name="entities"/> alive at once),
        /// adding and removing those components (at most their reserve each) and entering and leaving the
        /// filters made of them allocates nothing. A filter whose Inc types are all reserved is reserved
        /// by them (see <see cref="KenseiECS.Filter.ReservedCount"/>), a filter created later included, so
        /// the filters systems create in Init need nothing. A pool or filter that was not reserved keeps
        /// its size and grows on demand. Creating a pool, a filter or a type in a new block of 64 type
        /// indices (one mask word per slot) allocates too: let the systems create them at load.
        /// </para>
        /// <para>
        /// The cost is the world's per-entity arrays (about 13 bytes plus 8 bytes per 64 component types
        /// per slot, and 8 more once a type index of 64 or above is in use) and 4 bytes per slot for each
        /// reserved pool and filter.
        /// The obsolete <see cref="Reserve(int, int)"/> charged every pool and filter for the slots instead, singletons
        /// and the ones created later included.
        /// </para>
        /// <para>
        /// Under KENSEI_DEBUG a later growth of the world's slots or free-slot list past the reserve, or of
        /// a reserved pool's or filter's arrays past theirs, is counted in <see cref="GrowthsAfterReserve"/>
        /// and, while <c>EcsProfiler</c> records this world, logged as a
        /// <c>ProfileEventType.StorageGrown</c> event. While the profiler records, the growth of a pool or
        /// filter that nothing reserved is logged too, marked "not reserved", and not counted: that is how
        /// a forgotten hot type shows.
        /// </para>
        /// <para>
        /// Everything is allocated before it replaces the old storage, and <see cref="ReservedEntities"/>
        /// is raised last: if an allocation fails (<see cref="OutOfMemoryException"/>), the world stays
        /// consistent and usable, some arrays possibly grown, and reserves made afterwards do not try the
        /// failed size.
        /// </para>
        /// <para>Call it at load time, like <see cref="Warmup"/>; it allocates.</para>
        /// </remarks>
        /// <param name="entities"> Number of entity slots to have room for. 0 does nothing. </param>
        /// <exception cref="ArgumentOutOfRangeException"> When <paramref name="entities"/> is negative. </exception>
        /// <exception cref="ObjectDisposedException"> When the world was destroyed. </exception>
        /// <exception cref="OutOfMemoryException">
        /// When an array cannot be allocated; the world stays usable, see the remarks.
        /// </exception>
        public void ReserveEntities(int entities) {
            if (IsDestroyed) {
                throw new ObjectDisposedException(nameof(World), "World.ReserveEntities after World.Destroy: a destroyed world has no storage to grow");
            }
            if (entities < 0) {
                throw new ArgumentOutOfRangeException(nameof(entities), entities, "Must not be negative");
            }
            // Exact size: the call states the need, doubling would overshoot it.
            if (entities > _generations.Length) {
                ResizeEntityStorage(entities);
            }
            // Every slot may be free at once: destroying all reserved entities
            // must not resize the free list.
            if (entities > _freeIndices.Length) {
                Array.Resize(ref _freeIndices, entities);
            }

            // Only the reserved pools and filters follow the slots; every other one keeps its size.
            var pools = _pools;
            for (int i = 0; i < pools.Length; i++) {
                var pool = pools[i];
                if (pool != null && pool.ReservedComponents != 0) {
                    pool.ReserveSparse(entities);
                }
            }
            // A bound from the types moves with the slot count that caps it.
            int slots = entities > _reservedEntities ? entities : _reservedEntities;
            for (int i = 0; i < _allFilters.Count; i++) {
                DeriveReserve(_allFilters[i], slots);
            }

            // Raised only now: a pool or filter reserved later sizes its sparse side from it.
            if (entities > _reservedEntities) {
                _reservedEntities = entities;
            }
        }

        /// <summary>
        /// Entity slots reserved so far: the highest value passed to <see cref="ReserveEntities"/> (or to
        /// the obsolete <see cref="Reserve(int, int)"/>), 0 while nothing was reserved. The world's
        /// per-entity arrays and the sparse side of every reserved pool and filter cover this many slots.
        /// </summary>
        public int ReservedEntities => _reservedEntities;

        /// <summary>
        /// Obsolete: grows the world's storage and every pool and filter, including the ones created
        /// later. Entity side: <paramref name="entities"/> entity slots (as <see cref="ReserveEntities"/>),
        /// and the sparse arrays of every pool and filter cover them. Component side: the dense arrays of
        /// every pool and filter hold at least <paramref name="components"/> entries. Never shrinks.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Every pool pays: about 4 bytes × <paramref name="entities"/> and <paramref name="components"/>
        /// slots of its type, singletons and rarely used types included, and so does every pool and filter
        /// created later (1000 types at 50,000 entities: about 200 MB of pool sparse arrays). Use
        /// <see cref="ReserveEntities"/> for the world and <see cref="ComponentPoolBase.Reserve"/> for the
        /// types whose count changes during play; filters over reserved types follow.
        /// </para>
        /// <para>
        /// It does not reserve the pools (<see cref="ComponentPoolBase.ReservedComponents"/> stays 0), so
        /// a later <see cref="ReserveEntities"/> does not extend them. Under KENSEI_DEBUG growth past
        /// either argument is counted in <see cref="GrowthsAfterReserve"/> for every pool and filter.
        /// Allocation failures leave the world consistent, as with <see cref="ReserveEntities"/>.
        /// </para>
        /// <para>Removed in 3.0.</para>
        /// </remarks>
        /// <param name="entities"> Number of entity slots to have room for. </param>
        /// <param name="components">
        /// Dense capacity for every pool and filter of this world. 0 leaves the dense arrays as they are.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException"> When an argument is negative. </exception>
        /// <exception cref="ObjectDisposedException"> When the world was destroyed. </exception>
        /// <exception cref="OutOfMemoryException">
        /// When an array cannot be allocated, also for a size no array can have (<c>int.MaxValue</c>
        /// components with a filter in the world); the world stays usable, see the remarks.
        /// </exception>
        [Obsolete("World.Reserve sizes every pool and filter, singletons and the ones created later included (about 4 bytes x entities and `components` slots per type). " +
            "Use ReserveEntities(entities) for the world and Pool<T>().Reserve(count) for the types whose count changes during play; filters over reserved types follow. " +
            "Removed in 3.0.")]
        public void Reserve(int entities, int components = 0) {
            if (IsDestroyed) {
                throw new ObjectDisposedException(nameof(World), "World.Reserve after World.Destroy: a destroyed world has no storage to grow");
            }
            if (entities < 0) {
                throw new ArgumentOutOfRangeException(nameof(entities), entities, "Must not be negative");
            }
            if (components < 0) {
                throw new ArgumentOutOfRangeException(nameof(components), components, "Must not be negative");
            }
            // Each step allocates what it needs before it replaces anything, so an
            // OutOfMemoryException leaves every array consistent with its neighbours.
            if (entities > _generations.Length) {
                GrowEntityCapacity(entities - 1);
            }
            if (entities > _freeIndices.Length) {
                Array.Resize(ref _freeIndices, entities);
            }

            var pools = _pools;
            for (int i = 0; i < pools.Length; i++) {
                if (pools[i] != null) {
                    pools[i].ReserveSparse(entities);
                    pools[i].ReserveDense(components);
                }
            }
            for (int i = 0; i < _allFilters.Count; i++) {
                _allFilters[i].ReserveSparse(entities);
                _allFilters[i].ReserveDense(components);
            }
            // The slot count caps the reserve filters take from their types, as in ReserveEntities.
            int slots = entities > _reservedEntities ? entities : _reservedEntities;
            for (int i = 0; i < _allFilters.Count; i++) {
                DeriveReserve(_allFilters[i], slots);
            }

            // Recorded only now: pools and filters created later are sized from these, and after a
            // failed Reserve each of them would try (and fail) to allocate the reserve again.
            if (entities > _reservedEntities) {
                _reservedEntities = entities;
            }
            if (entities > _everyPoolEntities) {
                _everyPoolEntities = entities;
            }
            if (components > _everyPoolComponents) {
                _everyPoolComponents = components;
            }
        }

        // ComponentPoolBase.Reserve changed the pool's reserve (or took it back after a failure): the
        // filters over its type take their reserve from the types again (DerivedFilterReserve).
        internal void OnPoolReserved(ComponentPoolBase pool) {
            var table = _filtersByType;
            int typeIndex = pool.TypeIndex;
            if (table == null || (uint)typeIndex >= (uint)table.Length) {
                return;
            }
            var typeFilters = table[typeIndex];
            if (typeFilters == null) {
                return;
            }
            ReserveFromTypes(typeFilters.Include);
            ReserveFromTypes(typeFilters.Any);
        }

        private void ReserveFromTypes(Filter[] filters) {
            for (int i = 0; i < filters.Length; i++) {
                DeriveReserve(filters[i], _reservedEntities);
            }
        }

        // The one place a filter's reserve from its types is set: from the reserves of its types now,
        // capped at entitySlots, so the result does not depend on the order of the Reserve calls.
        private void DeriveReserve(Filter filter, int entitySlots) {
            filter.SetDerivedReserve(DerivedFilterReserve(filter, entitySlots), entitySlots);
        }

        // The reserve a filter takes from the reserves of its component types. An entity in the filter has
        // every Inc type, so with all of them reserved the filter never holds more than the smallest of
        // their reserves; it has at least one Any type, so with all of those reserved it never holds more
        // than their sum. 0 while an Inc type (or, without Inc types, an Any type) is not reserved: entering
        // the filter then involves a type that may grow anyway, and the filter grows on demand too.
        // When entity slots are reserved, no filter holds more entities than there are slots: the bound
        // is capped at entitySlots (0: no cap).
        internal int DerivedFilterReserve(Filter filter, int entitySlots) {
            long bound;
            var include = filter.IncludedTypeIndices;
            if (include.Length > 0) {
                bound = long.MaxValue;
                for (int i = 0; i < include.Length; i++) {
                    int reserved = ReservedComponentsOf(include[i]);
                    if (reserved == 0) {
                        return 0;
                    }
                    if (reserved < bound) {
                        bound = reserved;
                    }
                }
                long anyBound = AnyReserveSum(filter.AnyTypeIndices);
                if (anyBound != 0 && anyBound < bound) {
                    bound = anyBound;
                }
            } else {
                bound = AnyReserveSum(filter.AnyTypeIndices);
            }
            if (entitySlots > 0 && bound > entitySlots) {
                bound = entitySlots;
            }
            return bound > int.MaxValue ? int.MaxValue : (int)bound;
        }

        // Sum of the reserves of the Any types, 0 when there are none or one of them is not reserved.
        private long AnyReserveSum(int[] any) {
            long sum = 0;
            for (int i = 0; i < any.Length; i++) {
                int reserved = ReservedComponentsOf(any[i]);
                if (reserved == 0) {
                    return 0;
                }
                sum += reserved;
            }
            return sum;
        }

        private int ReservedComponentsOf(int typeIndex) {
            var pools = _pools;
            return (uint)typeIndex < (uint)pools.Length && pools[typeIndex] != null
                ? pools[typeIndex].ReservedComponents
                : 0;
        }

        /// <summary>
        /// Number of times a storage array grew past the size a reserve stated for it: the world's
        /// entity slots or free-slot list past <see cref="ReservedEntities"/>; a reserved pool's dense
        /// arrays past its <see cref="ComponentPoolBase.ReservedComponents"/> and its sparse array past
        /// <see cref="ReservedEntities"/> (once entity slots were reserved); a reserved filter's dense
        /// array past its <see cref="KenseiECS.Filter.ReservedCount"/> and its sparse pages past
        /// <see cref="ReservedEntities"/>; with the obsolete <see cref="Reserve(int, int)"/>, every pool
        /// and filter in the dimensions it was given. A pool or filter that nothing reserved grows on
        /// demand and is not counted. Non-zero means a reserve is too small for the peak, and that frame
        /// allocated. Always 0 unless KenseiECS is compiled with KENSEI_DEBUG.
        /// </summary>
        public int GrowthsAfterReserve {
            get {
#if KENSEI_DEBUG
                return _growthsAfterReserve;
#else
                return 0;
#endif
            }
        }

        /// <summary>
        /// The first growth counted by <see cref="GrowthsAfterReserve"/>: which array of which pool,
        /// filter or world outgrew its reserve, its sizes and the tick — the place to look when the
        /// count is not 0, without recording the world in <c>EcsProfiler</c>. Later growths do not
        /// replace it, and <see cref="Clear"/> resets neither it nor the count. Recording it allocates nothing beyond the
        /// owner's name (a pool's type name or a filter's description, built once per pool or filter).
        /// Empty (<see cref="StorageGrowth.IsEmpty"/>) while nothing outgrew a reserve, and always
        /// unless KenseiECS is compiled with KENSEI_DEBUG.
        /// </summary>
        public StorageGrowth FirstGrowthAfterReserve {
            get {
#if KENSEI_DEBUG
                return _firstGrowthAfterReserve;
#else
                return default;
#endif
            }
        }

#if KENSEI_DEBUG
        // Sizes the obsolete Reserve gave every pool and filter: their growth past them is counted.
        internal int EveryPoolEntities => _everyPoolEntities;
        internal int EveryPoolComponents => _everyPoolComponents;

        // A pool or filter that nothing reserved grew: logged (not counted) while the profiler records
        // this world after its entity slots were reserved, so a hot type that was forgotten shows in the
        // Grow filter. Checked before the owner's name is built: off, it costs nothing.
        internal bool LogsUnreservedGrowth => _reservedEntities != 0 && EcsProfiler.IsRecording(this);

        // Called by organic (non-Reserve) growth of an array a reserve covered; reserved is the size it
        // stated. owner and array are constants or the cached name of the pool or filter, so the counter
        // itself allocates nothing.
        internal void OnStorageGrown(string owner, string array, int oldSize, int newSize, int reserved) {
            if (_growthsAfterReserve == 0) {
                _firstGrowthAfterReserve = new StorageGrowth(owner, array, oldSize, newSize, reserved, _tick);
            }
            _growthsAfterReserve++;
            EcsProfiler.OnStorageGrown(this, _tick, owner, array, oldSize, newSize, reserved);
        }

        internal void OnUnreservedGrowth(string owner, string array, int oldSize, int newSize) {
            EcsProfiler.OnStorageGrown(this, _tick, owner, array, oldSize, newSize, 0);
        }

        // World slots and the free-slot list: counted once entity slots were reserved.
        private void OnEntityStorageGrown(string array, int oldSize, int newSize) {
            if (_reservedEntities != 0) {
                OnStorageGrown(nameof(World), array, oldSize, newSize, _reservedEntities);
            }
        }

        // Debug-only guard for operations that would otherwise fail with a NullReferenceException
        // on a destroyed world.
        private void ThrowIfDestroyed(string operation) {
            if (IsDestroyed) {
                throw new ObjectDisposedException(nameof(World), $"World.{operation} after World.Destroy");
            }
        }
#endif

        // Snapshot restore: claim a specific slot with a specific generation.
        // Only valid on an empty world; FinishRestore rebuilds the free list.
        internal void RestoreEntity(int index, int generation) {
            if (index >= _generations.Length) {
                GrowEntityCapacity(index);
            }
            if (_alive[index]) {
                throw new InvalidOperationException($"Snapshot restores entity slot {index} twice");
            }

            _generations[index] = generation;
            _alive[index] = true;
            _componentCounts[index] = 0;
            _aliveCount++;
            if (index >= _nextIndex) {
                _nextIndex = index + 1;
            }
        }

        internal void FinishRestore(int tick) {
            _freeCount = 0;
            for (int i = _nextIndex - 1; i >= 0; i--) {
                if (!_alive[i]) {
                    if (_freeCount == _freeIndices.Length) {
                        Array.Resize(ref _freeIndices, _freeIndices.Length * 2);
                    }
                    _freeIndices[_freeCount++] = i;
                }
            }
            _tick = tick;
        }

        // Allocate a live slot with no components. Callers must add at least one
        // component before dispatching OnEntityCreated.
        private Entity CreateEntityInternal() {
#if KENSEI_DEBUG
            if (_alive == null) {
                ThrowIfDestroyed("CreateEntity");
            }
#endif
            int index;
            int generation;

            if (_freeCount > 0) {
                index = _freeIndices[--_freeCount];
                // Generation changes when a slot is reused, not when it is freed:
                // GetEntity on a dead slot then yields the dead entity's own handle
                // instead of forging the handle of whatever lives there next.
                generation = _generations[index] + 1;
                if (generation == 0) {
                    generation = 1;
                }
                _generations[index] = generation;
            } else {
                index = _nextIndex;
                if (index >= _generations.Length) {
#if KENSEI_DEBUG
                    OnEntityStorageGrown("entity slots", _generations.Length, Math.Max(_generations.Length * 2, index + 1));
#endif
                    GrowEntityCapacity(index);
                }
                generation = _generations[index];
                // Claimed only once the slot exists: on a destroyed world the access above throws
                // first, and _nextIndex stays 0, so IsAlive and DestroyEntity keep returning
                // false and doing nothing instead of reading the nulled arrays.
                _nextIndex = index + 1;
            }

            // Neither the mask words nor the component count are zeroed here: a free slot's
            // are guaranteed zero by DestroyEntity (every removal clears its bit, a completed
            // drain leaves the count at zero, and an interrupted destroy clears both when it
            // releases the slot), by Clear, and by fresh allocation of new slots/words.
            _alive[index] = true;
            _aliveCount++;

            var entity = new Entity(index, generation);
#if KENSEI_DEBUG
            EcsProfiler.OnEntityCreated(this, _tick, index, generation);
#endif
            return entity;
        }

        /// <summary>
        /// Create an entity with one initial component.
        /// OnEntityCreated fires after the component is added, so listeners never
        /// observe an entity without components. If a listener of the component destroys
        /// the entity, OnEntityCreated does not fire and the returned handle is dead.
        /// </summary>
        public Entity CreateEntity<T>(T component) where T : struct, IComponent {
            var entity = CreateEntityInternal();
            Add(entity, component);
            DispatchEntityCreated(entity);
            return entity;
        }

        /// <summary>
        /// Destroy an entity. O(number of component types on the entity).
        /// Removes all components and returns the slot to the free list; the slot's
        /// generation changes when it is reused.
        /// Also called automatically when the last component is removed.
        /// Listeners of a component removed here see the components not removed yet
        /// (Has, Get). A listener that calls <see cref="Clear"/> ends the destroy: Clear has
        /// already removed everything and released the slot.
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
            // A listener may call Clear, which resets the slot (and may hand it to an entity
            // the listener creates next): from then on nothing here is this entity's.
            int clears = _clears;

#if KENSEI_DEBUG
            if (_dyingCount == _dying.Length) {
                Array.Resize(ref _dying, _dyingCount * 2);
            }
            _dying[_dyingCount++] = idx;
            EcsProfiler.OnEntityDestroyed(this, _tick, idx, _generations[idx]);
#endif
            bool drained = false;
            try {
                DispatchEntityDestroyed(idx);
            } finally {
                try {
                    if (_clears == clears) {
                        DrainComponents(idx, clears);
                    }
                    drained = true;
                } finally {
                    if (_clears == clears) {
                        ReleaseSlot(idx, drained);
                    }
#if KENSEI_DEBUG
                    // Nested destroys finish first, so this one is on top.
                    _dyingCount--;
#endif
                }
            }
        }

        // Listeners may re-add components mid-removal. OnComponentAdded bumps the
        // count unconditionally and OnComponentRemoved leaves it alone for a dead
        // entity, so a non-zero count after a pass means "something was re-added":
        // one int read instead of re-scanning every mask word. The mask is not cleared
        // ahead of the removals: each one clears its own bit, so the listeners of a
        // removed component still see the others (Has), and OnComponentRemoved keeps the
        // dying entity out of filters those components would let it into.
        //
        // A pass stops as soon as it has met as many bits as the count it started from, when
        // nothing was added meanwhile (the count is still 0): no bit is left in the words after.
        // The count is never below the number of set bits: it equals it while the entity is alive,
        // an Add raises both, and a removal from the dying entity lowers only the bits. So the
        // bits met, all set when the pass started (nothing was added), are every bit there was,
        // and each was removed (a bit set again means an Add, which the count shows). A component
        // removed by a listener before its bit is reached keeps the pass from stopping early, and
        // it reads every word as before. An auto-destroy (count 0) reads no word at all.
        // The one exception is an orphan (_orphans): a component an interrupted destroy left in its
        // pool without a bit. Its removal lowers the count of the entity that took the slot, so the
        // count no longer bounds the bits, and every pass reads every word until Clear.
        // After word 0 the pass reads only the words whose bit is set in _usedWords (NextUsedWord),
        // a clear bit meaning a zero word. The mask arrays and the word count are read once and
        // again only after a word whose removals ran user code (a listener may grow them), and so
        // is the summary, before each next word: an empty word costs the load of its array and of
        // the slot's bits. The loop is bounded by _maskWordCount, not by the outer array's length:
        // an EnsureMaskWords that ran out of memory may leave words past the count unallocated.
        private void DrainComponents(int idx, int clears) {
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
                int remaining = _componentCounts[idx];
                _componentCounts[idx] = 0;
                if (_orphans) {
                    // The count does not bound the bits: no early stop in this pass.
                    remaining = int.MaxValue;
                } else if (remaining == 0) {
                    // No bit is set (the count is never below them), and none was added.
                    return;
                }
                var masks = _componentMasks;
                int words = _maskWordCount;
                for (int w = 0; w < words; w = NextUsedWord(idx, w)) {
                    ulong mask = masks[w][idx];
                    if (mask == 0) {
                        continue;
                    }
                    do {
                        if (_clears != clears) {
                            // A listener of the last removal called Clear: the slot is not this
                            // entity's any more, an entity the listener created may hold it.
                            return;
                        }
                        int bit = TrailingZeroCount(mask);
                        _pools[(w << 6) | bit].Remove(idx);
                        mask &= mask - 1;
                        remaining--;
                    } while (mask != 0);
                    if (remaining <= 0 && _componentCounts[idx] == 0) {
                        // Every bit the pass started with is gone and none was added. After a
                        // Clear (count reset) nothing is removed any more either way.
                        return;
                    }
                    masks = _componentMasks;
                    words = _maskWordCount;
                }
                // After a Clear the count is another entity's (or 0): no further pass.
            } while (_componentCounts[idx] != 0 && _clears == clears);
        }

        // The next mask word after w that may hold a component of the slot's entity, int.MaxValue when
        // none does. Read from _usedWords every time: user code between two calls may have added a
        // component in a new word (or created _usedWords, growing the world past one word). Without
        // _usedWords, and from word 63 on (one bit stands for all of them), every word is next.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int NextUsedWord(int idx, int w) {
            var usedWords = _usedWords;
            if (usedWords == null || w >= 63) {
                return w + 1;
            }
            ulong above = usedWords[idx] >> (w + 1);
            return above != 0 ? w + 1 + TrailingZeroCount(above) : int.MaxValue;
        }

        // A completed drain leaves every mask word and the count zero; only an interrupted one
        // (a throwing listener or AutoReset) needs the slot scrubbed here.
        private void ReleaseSlot(int idx, bool drained) {
#if KENSEI_DEBUG
            _names.Remove(idx);
#endif
            if (!drained) {
                _componentCounts[idx] = 0;
                for (int w = 0; w < _maskWordCount; w++) {
                    ref ulong word = ref _componentMasks[w][idx];
                    if (word != 0) {
                        // The components of these bits stay in their pools: see _orphans.
                        _orphans = true;
                        word = 0;
                    }
                }
            }
            var usedWords = _usedWords;
            if (usedWords != null) {
                usedWords[idx] = 0;
            }
            _aliveCount--;
            _releases++;

            int free = _freeCount;
            var freeIndices = _freeIndices;
            if (free == freeIndices.Length) {
                freeIndices = GrowFreeIndices();
            }
            freeIndices[free] = idx;
            _freeCount = free + 1;
        }

        // Out of ReleaseSlot: on IL2CPP the Array.Resize call would add a metadata initialization
        // check to every DestroyEntity.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private int[] GrowFreeIndices() {
#if KENSEI_DEBUG
            OnEntityStorageGrown("free-slot list", _freeIndices.Length, _freeIndices.Length * 2);
#endif
            Array.Resize(ref _freeIndices, _freeIndices.Length * 2);
            return _freeIndices;
        }

        /// <summary> Check if entity is alive. O(1). </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Entity GetEntity(int entityIndex) {
            return new Entity(entityIndex, _generations[entityIndex]);
        }

        /// <summary> Handle for a slot index if that slot currently holds an alive entity. Safe for any index. </summary>
        public bool TryGetEntity(int entityIndex, out Entity entity) {
            if ((uint)entityIndex < (uint)_nextIndex && _alive[entityIndex]) {
                entity = new Entity(entityIndex, _generations[entityIndex]);
                return true;
            }
            entity = Entity.Null;
            return false;
        }

        /// <summary>
        /// Copy an entity — creates a new entity with copies of all components.
        /// Returns the new entity. OnEntityCreated fires after all components are copied.
        /// If a listener of a copied component destroys the copy, copying stops, OnEntityCreated
        /// does not fire and the returned handle is dead (the slot may already hold an entity the
        /// listener created). If a listener destroys the source, copying stops there and the copy
        /// keeps what it got. A source with no components left (inside the removal of its last
        /// component: an IWorldEventListener.OnComponentRemoved or an IFilterListener.OnEntityRemoved
        /// of it, which runs before the auto-destroy) is not copied: CopyEntity returns Entity.Null
        /// and creates nothing, in every build.
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

            // A copy without components would be an entity that no filter sees and nothing destroys.
            if (_componentCounts[source.Index] == 0) {
                return Entity.Null;
            }

            var copy = CreateEntityInternal();
            int srcIdx = source.Index;
            int dstIdx = copy.Index;

            int releases = _releases;
            // Word 0 and the words whose bit is set in _usedWords, as in DrainComponents; the mask
            // arrays and the word count are re-read only after a word whose copies ran user code (a
            // listener may grow them). Bounded by _maskWordCount, as in DrainComponents.
            var masks = _componentMasks;
            int words = _maskWordCount;
            for (int w = 0; w < words; w = NextUsedWord(srcIdx, w)) {
                ulong mask = masks[w][srcIdx];
                if (mask == 0) {
                    continue;
                }
                do {
                    // A listener of a copied component destroyed an entity: if it was the copy or the
                    // source (by handle: an entity it created may have taken the freed slot), stop.
                    if (_releases != releases) {
                        if (!IsAlive(copy) || !IsAlive(source)) {
                            DispatchEntityCreated(copy);
                            return copy;
                        }
                        releases = _releases;
                    }
                    int bit = TrailingZeroCount(mask);
                    _pools[(w << 6) | bit].CopyTo(srcIdx, dstIdx);
                    mask &= mask - 1;
                } while (mask != 0);
                masks = _componentMasks;
                words = _maskWordCount;
            }

            DispatchEntityCreated(copy);
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
#if KENSEI_DEBUG
            if (pools == null) {
                ThrowIfDestroyed("Pool");
            }
#endif
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

            // A new pool is not reserved: only the obsolete Reserve sizes every pool, the later ones too.
            int sparseCapacity = Math.Max(_config.InitialPoolSparseCapacity, _everyPoolEntities);
            int denseCapacity = Math.Max(_config.InitialPoolDenseCapacity, _everyPoolComponents);
            var pool = new ComponentPool<T>(this, sparseCapacity, denseCapacity);
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
        /// The ref is valid until the next Add or Remove of the same component type on any entity
        /// (Add may grow the pool, Remove swaps another component into the freed slot).
        /// When the type belongs to an owning group, an Add or Remove of any of the group's
        /// types invalidates it too. Changes to other component types do not affect it.
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
#if KENSEI_DEBUG
            if (pools == null) {
                ThrowIfDestroyed("Pool");
            }
#endif
            if ((uint)typeIdx < (uint)pools.Length && pools[typeIdx] is ComponentPool<T> pool) {
                pool.Remove(entity.Index);
            }
        }

        // =====================================================================
        // Owning groups
        // =====================================================================

        /// <summary> Number of registered owning groups. </summary>
        public int GroupCount => _groups.Count;

        /// <summary> Registered group by position, for tooling. </summary>
        public Group GetGroup(int index) {
            return _groups[index];
        }

        /// <summary>
        /// Owning group over two component types. Their pools' dense arrays are kept
        /// aligned so members can be iterated through Data1/Data2 without lookups.
        /// Each pool can belong to one group only; calling again with the same types
        /// returns the existing group.
        /// </summary>
        public Group<T1, T2> Group<T1, T2>()
            where T1 : struct, IComponent
            where T2 : struct, IComponent {
            var p1 = Pool<T1>();
            var p2 = Pool<T2>();
            var pools = new ComponentPoolBase[] { p1, p2 };
            if (FindGroup(pools) is Group<T1, T2> existing) {
                return existing;
            }
            return RegisterGroup(new Group<T1, T2>(p1, p2), pools);
        }

        /// <summary>
        /// Owning group over three component types. Their pools' dense arrays are kept
        /// aligned so members can be iterated through Data1..Data3 without lookups.
        /// Each pool can belong to one group only; calling again with the same types
        /// returns the existing group.
        /// </summary>
        public Group<T1, T2, T3> Group<T1, T2, T3>()
            where T1 : struct, IComponent
            where T2 : struct, IComponent
            where T3 : struct, IComponent {
            var p1 = Pool<T1>();
            var p2 = Pool<T2>();
            var p3 = Pool<T3>();
            var pools = new ComponentPoolBase[] { p1, p2, p3 };
            if (FindGroup(pools) is Group<T1, T2, T3> existing) {
                return existing;
            }
            return RegisterGroup(new Group<T1, T2, T3>(p1, p2, p3), pools);
        }

        /// <summary>
        /// Owning group over four component types. Their pools' dense arrays are kept
        /// aligned so members can be iterated through Data1..Data4 without lookups.
        /// Each pool can belong to one group only; calling again with the same types
        /// returns the existing group.
        /// </summary>
        public Group<T1, T2, T3, T4> Group<T1, T2, T3, T4>()
            where T1 : struct, IComponent
            where T2 : struct, IComponent
            where T3 : struct, IComponent
            where T4 : struct, IComponent {
            var p1 = Pool<T1>();
            var p2 = Pool<T2>();
            var p3 = Pool<T3>();
            var p4 = Pool<T4>();
            var pools = new ComponentPoolBase[] { p1, p2, p3, p4 };
            if (FindGroup(pools) is Group<T1, T2, T3, T4> existing) {
                return existing;
            }
            return RegisterGroup(new Group<T1, T2, T3, T4>(p1, p2, p3, p4), pools);
        }

        private Group FindGroup(ComponentPoolBase[] pools) {
            for (int i = 0; i < _groups.Count; i++) {
                if (_groups[i].OwnsExactly(pools)) {
                    return _groups[i];
                }
            }
            return null;
        }

        private TGroup RegisterGroup<TGroup>(TGroup group, ComponentPoolBase[] pools) where TGroup : Group {
            for (int i = 0; i < pools.Length; i++) {
                if (pools[i]._ownerGroup != null) {
                    throw new InvalidOperationException(
                        $"Pool<{pools[i].ComponentType.Name}> is already owned by another group — a component type can belong to one owning group only");
                }
                for (int j = 0; j < i; j++) {
                    if (pools[j] == pools[i]) {
                        throw new InvalidOperationException(
                            $"Group lists {pools[i].ComponentType.Name} twice");
                    }
                }
            }

            for (int i = 0; i < pools.Length; i++) {
                pools[i].SetOwnerGroup(group);
            }
            _groups.Add(group);
            group.Populate();
            return group;
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
        internal void ValidateHandle<T>(Entity entity, string operation) where T : struct, IComponent {
            int idx = entity.Index;
            if (idx >= 0 && idx < _nextIndex && _generations[idx] == entity.Generation) {
                // A dead slot with a matching generation is either mid-destroy
                // (listeners legitimately touch the dying entity) or a stale handle
                // whose slot has not been reused yet.
                if (_alive[idx] || IsDying(idx)) {
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
                && (_alive[entityIndex] || IsDying(entityIndex));
        }

        // The listener extensions' guard: a live entity, or one whose own DestroyEntity is running (its
        // listeners may unsubscribe from it then, as ValidateHandle lets them touch it).
        internal bool IsAliveOrDying(Entity entity) {
            int idx = entity.Index;
            return idx >= 0 && idx < _nextIndex && _generations[idx] == entity.Generation
                && (_alive[idx] || IsDying(idx));
        }

        // The slot's own DestroyEntity is running (not merely some entity's).
        private bool IsDying(int entityIndex) {
            for (int i = _dyingCount - 1; i >= 0; i--) {
                if (_dying[i] == entityIndex) {
                    return true;
                }
            }
            return false;
        }
#endif

        // =====================================================================
        // Filter API
        // =====================================================================

        /// <summary> Number of registered filters. </summary>
        public int FilterCount => _allFilters.Count;

        /// <summary> Registered filter by position, for tooling. </summary>
        public Filter GetFilter(int index) {
            return _allFilters[index];
        }

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
            if (IsDestroyed) {
                throw new ObjectDisposedException(nameof(World), "World.Filter after World.Destroy");
            }
            foreach (var existing in _allFilters) {
                if (ArraysEqual(existing.IncludedTypeIndices, includes)
                    && ArraysEqual(existing.ExcludedTypeIndices, excludes)
                    && ArraysEqual(existing.AnyTypeIndices, any)) {
                    return existing;
                }
            }

            var filter = new Filter(includes, excludes, any,
                Math.Max(_config.InitialPoolDenseCapacity, _everyPoolComponents));
            filter._world = this;
            if (_everyPoolEntities > 0) {
                filter.ReserveSparse(_everyPoolEntities);
            }
            // A filter over reserved types is reserved by them from the start, so populating it and
            // the play that follows do not grow it. Sized before it is registered: if the allocation
            // fails, the world never sees a filter that was not populated.
            DeriveReserve(filter, _reservedEntities);
            _allFilters.Add(filter);

            // Pre-allocate mask words for every word this filter constrains,
            // so EntityMatchesFilter never has to bounds-check against
            // _maskWordCount — an unregistered type's word just reads zeros.
            EnsureMaskWords(filter.IncludeMask.Length);

            foreach (int typeIdx in includes) {
                Append(ref TypeFiltersFor(typeIdx).Include, filter);
            }

            foreach (int typeIdx in excludes) {
                Append(ref TypeFiltersFor(typeIdx).Exclude, filter);
            }

            foreach (int typeIdx in any) {
                Append(ref TypeFiltersFor(typeIdx).Any, filter);
            }

            PopulateFilter(filter);

            return filter;
        }

        // Filter.AddListener/RemoveListener: the filter got its first listener (+1) or lost its last
        // one (-1). Every type it constrains counts it, so adding one of them takes the notifying path.
        internal void OnFilterListening(Filter filter, int delta) {
            if (_filtersByType == null) {
                // Destroyed world: no structural change happens any more.
                return;
            }
            CountListening(filter.IncludedTypeIndices, delta);
            CountListening(filter.ExcludedTypeIndices, delta);
            CountListening(filter.AnyTypeIndices, delta);
        }

        private void CountListening(int[] types, int delta) {
            for (int i = 0; i < types.Length; i++) {
                TypeFiltersFor(types[i]).Listening += delta;
            }
        }

        private TypeFilters TypeFiltersFor(int typeIndex) {
            if (typeIndex >= _filtersByType.Length) {
                Array.Resize(ref _filtersByType, Math.Max(_filtersByType.Length * 2, typeIndex + 1));
            }
            return _filtersByType[typeIndex] ??= new TypeFilters();
        }

        private static void Append(ref Filter[] filters, Filter filter) {
            Array.Resize(ref filters, filters.Length + 1);
            filters[filters.Length - 1] = filter;
        }

        // =====================================================================
        // Filter notifications — called by ComponentPool on Add/Remove
        // =====================================================================

        // OnComponentAdded results: no filter or world listener ran, so the component is where the
        // pool stored it; listeners ran and the entity still has it (it may have moved in the dense
        // arrays); or a listener removed it again or destroyed the entity, whose slot may hold
        // another entity now.
        internal const int AddedQuietly = 0;
        internal const int AddedKept = 1;
        internal const int AddedLost = 2;

        /// <summary>
        /// Called by ComponentPool after a component is added.
        /// Increments component count and updates relevant filters.
        /// Returns <see cref="AddedQuietly"/>, <see cref="AddedKept"/> or <see cref="AddedLost"/>.
        /// </summary>
        // Inlined into the pool's Add: a type no filter mentions (the common event or tag type without
        // a filter of its own, with no world listener) costs the count, the mask bit and one table
        // read, without a call. Types with filters continue in OnComponentAddedFiltered.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal int OnComponentAdded(int entityIndex, int typeIndex) {
            _componentCounts[entityIndex]++;
            int word = typeIndex >> 6;
            ref ulong bits = ref _componentMasks[word][entityIndex];
            ulong before = bits;
            bits = before | (1UL << (typeIndex & 63));
            if (before == 0 && word != 0) {
                // The word was empty: from now on DestroyEntity and CopyEntity read it. A world with a
                // second mask word has _usedWords (EnsureMaskWords made both).
                _usedWords[entityIndex] |= 1UL << (word < 63 ? word : 63);
            }

            var table = _filtersByType;
            if ((uint)typeIndex < (uint)table.Length) {
                var typeFilters = table[typeIndex];
                if (typeFilters != null) {
                    return OnComponentAddedFiltered(typeFilters, entityIndex, typeIndex);
                }
            }

            if (!_dispatch) {
                return AddedQuietly;
            }
            return OnComponentAddedNotifying(null, entityIndex, typeIndex);
        }

        // OnComponentAdded for a type that filters mention. No filter of this type has listeners and
        // nothing is dispatched: no user code runs here, so the filters are updated without
        // re-checks and the component stays put.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private int OnComponentAddedFiltered(TypeFilters typeFilters, int entityIndex, int typeIndex) {
            if (!_dispatch && typeFilters.Listening == 0) {
                UpdateFiltersOnAddQuietly(typeFilters, entityIndex);
                return AddedQuietly;
            }
            return OnComponentAddedNotifying(typeFilters, entityIndex, typeIndex);
        }

        // No user code runs here (no filter listener, AddEntity/RemoveEntity call none), so the mask
        // arrays cannot be replaced during the loops: they are read once instead of once per filter.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void UpdateFiltersOnAddQuietly(TypeFilters typeFilters, int entityIndex) {
            var masks = _componentMasks;
            var include = typeFilters.Include;
            for (int i = 0; i < include.Length; i++) {
                var filter = include[i];
                if (EntityMatchesFilter(masks, filter, entityIndex)) {
                    filter.AddEntity(entityIndex);
                }
            }

            var exclude = typeFilters.Exclude;
            for (int i = 0; i < exclude.Length; i++) {
                exclude[i].RemoveEntity(entityIndex);
            }

            var any = typeFilters.Any;
            for (int i = 0; i < any.Length; i++) {
                var filter = any[i];
                if (EntityMatchesFilter(masks, filter, entityIndex)) {
                    filter.AddEntity(entityIndex);
                }
            }
        }

        // OnComponentAdded when filter or world listeners may run.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private int OnComponentAddedNotifying(TypeFilters typeFilters, int entityIndex, int typeIndex) {
            // Read before any listener runs: a listener may destroy the entity and let a new one
            // take the slot, which only the generation tells apart.
            int generation = _generations[entityIndex];
            bool ran = typeFilters != null && UpdateFiltersOnAdd(typeFilters, entityIndex, typeIndex, generation);
            if (ran && !StillHas(entityIndex, typeIndex, generation)) {
                // The filter listener's Remove has already dispatched OnComponentRemoved: an
                // OnComponentAdded after it would report a component the entity no longer has.
                return AddedLost;
            }

            if (_dispatch) {
                var listeners = _eventListeners;
                for (int i = 0; i < listeners.Length; i++) {
                    listeners[i].OnComponentAdded(entityIndex, typeIndex);
                }
                return StillHas(entityIndex, typeIndex, generation) ? AddedKept : AddedLost;
            }
            return ran ? AddedKept : AddedQuietly;
        }

        // Adding T moves the entity into Inc/Any filters of T and out of its Exc filters. A filter
        // listener may remove T again, directly or by destroying the entity (whose slot a new entity
        // may then take): the nested Remove already put the entity back into the Exc filters and
        // updated the rest, so the update stops there. Only a filter whose listeners ran can cause
        // that (AddEntity/RemoveEntity return true then), so only then is the entity re-checked.
        // Returns true when a filter listener ran.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool UpdateFiltersOnAdd(TypeFilters typeFilters, int entityIndex, int typeIndex, int generation) {
            bool ran = false;
            var include = typeFilters.Include;
            for (int i = 0; i < include.Length; i++) {
                var filter = include[i];
                if (EntityMatchesFilter(filter, entityIndex) && filter.AddEntity(entityIndex)) {
                    ran = true;
                    if (!StillHas(entityIndex, typeIndex, generation)) {
                        return true;
                    }
                }
            }

            var exclude = typeFilters.Exclude;
            for (int i = 0; i < exclude.Length; i++) {
                if (exclude[i].RemoveEntity(entityIndex)) {
                    ran = true;
                    if (!StillHas(entityIndex, typeIndex, generation)) {
                        return true;
                    }
                }
            }

            var any = typeFilters.Any;
            for (int i = 0; i < any.Length; i++) {
                var filter = any[i];
                if (EntityMatchesFilter(filter, entityIndex) && filter.AddEntity(entityIndex)) {
                    ran = true;
                }
            }
            return ran;
        }

        // The entity that got the component (same generation) still has it. The mask arrays are
        // re-read: a listener may have grown them.
        private bool StillHas(int entityIndex, int typeIndex, int generation) {
            return _generations[entityIndex] == generation
                && (_componentMasks[typeIndex >> 6][entityIndex] & (1UL << (typeIndex & 63))) != 0;
        }

        /// <summary>
        /// Called by ComponentPool after a component is removed.
        /// Decrements component count, updates filters,
        /// and auto-destroys the entity if no components remain.
        /// Listeners observe the entity while it is still alive; when the removed
        /// component was the last one, auto-destroy follows the dispatch.
        /// </summary>
        // Inlined into the pool's Remove: for a type no filter mentions, with no world listener, no
        // user code runs between the decrement and the auto-destroy check, so the count it decremented
        // decides without reading the count and the alive flag again. Types with filters continue in
        // OnComponentRemovedFiltered.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void OnComponentRemoved(int entityIndex, int typeIndex) {
            if (_dispatch) {
                OnComponentRemovedNotifying(entityIndex, typeIndex);
                return;
            }

            var table = _filtersByType;
            if ((uint)typeIndex < (uint)table.Length) {
                var typeFilters = table[typeIndex];
                if (typeFilters != null) {
                    OnComponentRemovedFiltered(typeFilters, entityIndex, typeIndex);
                    return;
                }
            }

            _componentMasks[typeIndex >> 6][entityIndex] &= ~(1UL << (typeIndex & 63));
            // A dying entity (DestroyEntity removes its components one by one) keeps its count.
            if (_alive[entityIndex] && --_componentCounts[entityIndex] == 0) {
                DestroyEntityInternal(entityIndex);
            }
        }

        // True when removing a component of this type runs no callback in the world: no world listener
        // and no filter of the type with listeners (the auto-destroy then dispatches nothing either).
        internal bool RemovesQuietly(int typeIndex) {
            if (_dispatch) {
                return false;
            }
            var table = _filtersByType;
            if ((uint)typeIndex < (uint)table.Length) {
                var typeFilters = table[typeIndex];
                if (typeFilters != null && typeFilters.Listening != 0) {
                    return false;
                }
            }
            return true;
        }

        // OnComponentRemoved without world listeners, for a type that filters mention.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void OnComponentRemovedFiltered(TypeFilters typeFilters, int entityIndex, int typeIndex) {
            // A dying entity (DestroyEntity removes its components one by one) keeps its count.
            bool alive = _alive[entityIndex];
            int count = 0;
            if (alive) {
                count = --_componentCounts[entityIndex];
            }
            _componentMasks[typeIndex >> 6][entityIndex] &= ~(1UL << (typeIndex & 63));

            if (typeFilters.Listening == 0) {
                // No filter listener can run: nothing changes the entity between the decrement and
                // the auto-destroy check.
                if (alive) {
                    UpdateFiltersOnRemoveQuietly(typeFilters, entityIndex);
                    if (count == 0) {
                        DestroyEntityInternal(entityIndex);
                    }
                } else {
                    UpdateFiltersOfDying(typeFilters, entityIndex, typeIndex);
                }
                return;
            }

            // A filter listener may subscribe the first world listener. Like one subscribed during an
            // Add, it gets this removal.
            int generation = _generations[entityIndex];
            if (alive) {
                UpdateFiltersOnRemove(typeFilters, entityIndex, typeIndex);
            } else {
                UpdateFiltersOfDying(typeFilters, entityIndex, typeIndex);
            }
            if (generation != 0 && _dispatch) {
                DispatchRemovedAfterFilters(entityIndex, typeIndex, generation);
            }

            // A filter listener may have changed the entity (re-added components, destroyed it) or
            // handed the slot to a new entity: read again.
            if (_componentCounts[entityIndex] == 0 && _alive[entityIndex]) {
                DestroyEntityInternal(entityIndex);
            }
        }

        // OnComponentRemoved for world listeners that a filter listener of this removal subscribed:
        // the same checks as in OnComponentRemovedNotifying.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void DispatchRemovedAfterFilters(int entityIndex, int typeIndex, int generation) {
            if (_generations[entityIndex] != generation
                || (_componentMasks[typeIndex >> 6][entityIndex] & (1UL << (typeIndex & 63))) != 0) {
                return;
            }
            var listeners = _eventListeners;
            for (int i = 0; i < listeners.Length; i++) {
                listeners[i].OnComponentRemoved(entityIndex, typeIndex);
            }
        }

        // OnComponentRemoved when world listeners are dispatched to. A filter listener may add the
        // component back, whose Add tells the world listeners, or destroy the entity and let a new one
        // take the slot (another generation; so does a Clear, which fires no events): a removal
        // reported after that would contradict what they were told, or name a component the new
        // entity never had, so it is left out. Without filter listeners on this type nothing runs
        // between the removal and the dispatch.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void OnComponentRemovedNotifying(int entityIndex, int typeIndex) {
            bool alive = _alive[entityIndex];
            if (alive) {
                _componentCounts[entityIndex]--;
            }
            _componentMasks[typeIndex >> 6][entityIndex] &= ~(1UL << (typeIndex & 63));

            var table = _filtersByType;
            TypeFilters typeFilters = null;
            if ((uint)typeIndex < (uint)table.Length) {
                typeFilters = table[typeIndex];
            }

            // Read before any filter listener runs; 0 (no slot has it) when none can run.
            int generation = 0;
            if (typeFilters != null) {
                if (typeFilters.Listening != 0) {
                    generation = _generations[entityIndex];
                }
                if (alive) {
                    UpdateFiltersOnRemove(typeFilters, entityIndex, typeIndex);
                } else {
                    UpdateFiltersOfDying(typeFilters, entityIndex, typeIndex);
                }
            }

            if (generation == 0
                || (_generations[entityIndex] == generation
                    && (_componentMasks[typeIndex >> 6][entityIndex] & (1UL << (typeIndex & 63))) == 0)) {
                // A filter listener may have unsubscribed every world listener: the array is empty then.
                var listeners = _eventListeners;
                for (int i = 0; i < listeners.Length; i++) {
                    listeners[i].OnComponentRemoved(entityIndex, typeIndex);
                }
            }

            if (_componentCounts[entityIndex] == 0 && _alive[entityIndex]) {
                DestroyEntityInternal(entityIndex);
            }
        }

        // OnComponentRemoved for an entity whose DestroyEntity is removing its components: the
        // components not removed yet are still in the mask, so it leaves filters as they go and
        // enters none (they would let it into Exc filters of this type). Not inlined on IL2CPP, where
        // a second copy of the filter loops in OnComponentRemoved slows the Remove of live entities
        // with filter listeners (about 5%); the .NET JIT handles the inlined copy better than the call.
#if ENABLE_IL2CPP
        [MethodImpl(MethodImplOptions.NoInlining)]
#else
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#endif
        private void UpdateFiltersOfDying(TypeFilters typeFilters, int entityIndex, int typeIndex) {
            var include = typeFilters.Include;
            for (int i = 0; i < include.Length; i++) {
                if (include[i].RemoveEntity(entityIndex)
                    && (_componentMasks[typeIndex >> 6][entityIndex] & (1UL << (typeIndex & 63))) != 0) {
                    break;
                }
            }

            var any = typeFilters.Any;
            for (int i = 0; i < any.Length; i++) {
                any[i].RemoveEntity(entityIndex);
            }
        }

        // UpdateFiltersOnRemove when no filter of the type has listeners: no user code runs, so the
        // entity cannot get the component back during the loops (no mask check after RemoveEntity) and
        // the mask arrays are read once instead of once per filter.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void UpdateFiltersOnRemoveQuietly(TypeFilters typeFilters, int entityIndex) {
            var include = typeFilters.Include;
            for (int i = 0; i < include.Length; i++) {
                include[i].RemoveEntity(entityIndex);
            }

            var masks = _componentMasks;
            var exclude = typeFilters.Exclude;
            for (int i = 0; i < exclude.Length; i++) {
                var filter = exclude[i];
                if (EntityMatchesFilter(masks, filter, entityIndex)) {
                    filter.AddEntity(entityIndex);
                }
            }

            var any = typeFilters.Any;
            for (int i = 0; i < any.Length; i++) {
                var filter = any[i];
                if (EntityMatchesFilter(masks, filter, entityIndex)) {
                    filter.AddEntity(entityIndex);
                } else {
                    filter.RemoveEntity(entityIndex);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void UpdateFiltersOnRemove(TypeFilters typeFilters, int entityIndex, int typeIndex) {
            // A filter listener (OnEntityRemoved) may add the component back; that
            // nested Add already put the entity into every matching filter, so the
            // remaining Inc filters must keep it. Only a filter whose listeners ran
            // (RemoveEntity returns true) runs user code, so the mask is checked after
            // those alone, re-read each time: the listener may also grow the mask arrays.
            var include = typeFilters.Include;
            for (int i = 0; i < include.Length; i++) {
                if (include[i].RemoveEntity(entityIndex)
                    && (_componentMasks[typeIndex >> 6][entityIndex] & (1UL << (typeIndex & 63))) != 0) {
                    break;
                }
            }

            var exclude = typeFilters.Exclude;
            for (int i = 0; i < exclude.Length; i++) {
                var filter = exclude[i];
                if (EntityMatchesFilter(filter, entityIndex)) {
                    filter.AddEntity(entityIndex);
                }
            }

            var any = typeFilters.Any;
            for (int i = 0; i < any.Length; i++) {
                UpdateFilterForEntity(any[i], entityIndex);
            }
        }

        // =====================================================================
        // Private
        // =====================================================================

        // Not for an entity a listener of its components destroyed while it was being made: it never
        // existed for world listeners, and its slot may hold another entity already.
        // The _dispatch test is inlined into the callers (CreateEntity, CopyEntity, DestroyEntity), so a
        // world without listeners pays no call for it.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DispatchEntityCreated(Entity entity) {
            if (_dispatch) {
                NotifyEntityCreated(entity);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void NotifyEntityCreated(Entity entity) {
            if (!IsAlive(entity)) {
                return;
            }
            var listeners = _eventListeners;
            for (int i = 0; i < listeners.Length; i++) {
                listeners[i].OnEntityCreated(entity.Index);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DispatchEntityDestroyed(int entityIndex) {
            if (_dispatch) {
                NotifyEntityDestroyed(entityIndex);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void NotifyEntityDestroyed(int entityIndex) {
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
            return EntityMatchesFilter(_componentMasks, filter, entityIndex);
        }

        // The same test over mask arrays the caller read: for loops in which no user code runs, so the
        // arrays cannot be replaced between filters.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool EntityMatchesFilter(ulong[][] masks, Filter filter, int entityIndex) {
            int w = filter.SingleWord;
            if (w >= 0) {
                ulong entityWord = masks[w][entityIndex];
                ulong include = filter.SingleIncludeMask;
                return (entityWord & include) == include
                    && (entityWord & filter.SingleExcludeMask) == 0
                    && (entityWord & filter.SingleAnyMask) != 0;
            }

            return EntityMatchesFilterMultiWord(masks, filter, entityIndex);
        }

        private static bool EntityMatchesFilterMultiWord(ulong[][] masks, Filter filter, int entityIndex) {
            var includeMask = filter.IncludeMask;
            var excludeMask = filter.ExcludeMask;
            var anyMask = filter.AnyMask;
            var activeWords = filter.ActiveWords;
            bool anyHit = false;

            for (int i = 0; i < activeWords.Length; i++) {
                int w = activeWords[i];
                ulong entityWord = masks[w][entityIndex];
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

        // Every slot array is allocated before any is replaced: running out of memory leaves all
        // of them at the old size instead of some grown and some not.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void GrowEntityCapacity(int index) {
            ResizeEntityStorage(Math.Max(_generations.Length * 2, index + 1));
        }

        private void ResizeEntityStorage(int newSize) {
            int oldSize = _generations.Length;
            var generations = new int[newSize];
            var alive = new bool[newSize];
            var counts = new int[newSize];
            var masks = new ulong[_maskWordCount][];
            for (int w = 0; w < masks.Length; w++) {
                masks[w] = new ulong[newSize];
            }
            var usedWords = _usedWords != null ? new ulong[newSize] : null;

            Array.Copy(_generations, generations, oldSize);
            Array.Fill(generations, 1, oldSize, newSize - oldSize);
            Array.Copy(_alive, alive, oldSize);
            Array.Copy(_componentCounts, counts, oldSize);
            for (int w = 0; w < masks.Length; w++) {
                Array.Copy(_componentMasks[w], masks[w], oldSize);
            }
            if (usedWords != null) {
                Array.Copy(_usedWords, usedWords, oldSize);
            }

            _generations = generations;
            _alive = alive;
            _componentCounts = counts;
            for (int w = 0; w < masks.Length; w++) {
                _componentMasks[w] = masks[w];
            }
            _usedWords = usedWords;
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
            // The second word brings the summary of the words in use; every slot's words from 1 on are
            // new, and zero.
            if (_usedWords == null) {
                _usedWords = new ulong[entityCapacity];
            }
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
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int TrailingZeroCount(ulong value) {
            return PortableTrailingZeroCount(value);
        }
#endif

        // Trailing zeros of a nonzero value without a lookup table (Unity has no BitOperations): a static
        // table would give World a static constructor, and IL2CPP then checks class initialization and loads
        // the static fields on every bit of DestroyEntity, CopyEntity and GetComponentTypes. ~value &
        // (value - 1) keeps exactly the zeros below the lowest set bit, and their population count is the
        // answer; clang folds this population count of that mask into a single count-trailing-zeros (rbit +
        // clz on ARM64). 64 for 0, as BitOperations.TrailingZeroCount.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int PortableTrailingZeroCount(ulong value) {
            ulong x = ~value & (value - 1);
            x -= (x >> 1) & 0x5555555555555555UL;
            x = (x & 0x3333333333333333UL) + ((x >> 2) & 0x3333333333333333UL);
            x = (x + (x >> 4)) & 0x0F0F0F0F0F0F0F0FUL;
            return (int)((x * 0x0101010101010101UL) >> 56);
        }
    }
}
