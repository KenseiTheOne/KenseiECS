using System;
using System.Runtime.CompilerServices;
#if ENABLE_IL2CPP
using Unity.IL2CPP.CompilerServices;
#endif

namespace KenseiECS {
    /// <summary>
    /// Cached query result — holds a dense list of entity indices
    /// that match the Include/Exclude/Any component constraints.
    ///
    /// Updated reactively by World when components are added/removed.
    /// Iteration is zero-allocation via struct enumerator.
    ///
    /// Internally a sparse set (dense + sparse) without data — only entity indices.
    /// </summary>
#if ENABLE_IL2CPP
    [Il2CppSetOption(Option.NullChecks, false)]
    [Il2CppSetOption(Option.ArrayBoundsChecks, false)]
#endif
    public class Filter {
        // Freed dense slots hold this sentinel so an enumerator detects structural
        // changes from the element value it loads anyway — a per-step version or
        // count check would cost an extra heap load in the hottest loop.
        private const int FreeSlot = -1;

        // Owning world, set by World.RegisterFilter: it counts, per component type, the filters
        // that have listeners, and under KENSEI_DEBUG it gets the growth-after-Reserve signal.
        internal World _world;

#if KENSEI_DEBUG
        // ToString() builds a new string per call; growth reports name the filter with this copy.
        private string _debugName;
        private string DebugName => _debugName ??= ToString();
#endif

        // Constraints
        internal int[] IncludedTypeIndices;
        internal int[] ExcludedTypeIndices;
        internal int[] AnyTypeIndices;

        // Precomputed multi-word bitmasks for O(1) filter matching
        internal ulong[] IncludeMask;
        internal ulong[] ExcludeMask;
        internal ulong[] AnyMask;
        internal bool HasAny;

        // Word indices where any mask has bits — matching
        // skips the all-zero words a sparse filter never constrains.
        internal int[] ActiveWords;

        // Most filters constrain a single mask word — matching then reads
        // scalar fields instead of walking ActiveWords and the mask arrays.
        // SingleWord is -1 when the filter spans multiple words.
        // SingleAnyMask is all-ones when the filter has no Any constraint:
        // Inc is then non-empty, so a matching word is never zero and the
        // Any test passes without a branch.
        internal int SingleWord;
        internal ulong SingleIncludeMask;
        internal ulong SingleExcludeMask;
        internal ulong SingleAnyMask;

        // Sparse set of matching entity indices.
        // Dense slot 0 is a permanent FreeSlot terminator and entities occupy
        // slots 1.._count, so the reverse enumerator stops on the same sentinel
        // probe it already does — no separate end-of-range check per step.
        //
        // The sparse side is paged: iteration never reads it, so the extra
        // indirection costs nothing on the hot path, and a filter over a few
        // entities in a world of thousands of types and slots only pays for the
        // pages it touches instead of an int per entity slot.
        private const int PageShift = 10;
        private const int PageSize = 1 << PageShift;
        private const int PageMask = PageSize - 1;

        private int[][] _sparsePages;  // [entityIndex >> PageShift][entityIndex & PageMask] → dense slot (1-based), -1 = not in filter
        private int[] _denseEntities;  // dense[slot] → entityIndex; free slots hold FreeSlot
        private int _count;

        // Copy-on-write, null when nobody listens.
        private IFilterListener[] _listeners;

        // The highest count passed to Reserve, and the reserve taken from the reserves of the filter's
        // component types (World.DerivedFilterReserve). The filter is reserved for the larger one. Read
        // when the filter or one of its types is reserved and when the world reserves entity slots,
        // never when an entity enters or leaves.
        private int _explicitReserve;
        private int _derivedReserve;

#if KENSEI_DEBUG
        // Structural-change guard. Each live enumerator records the slot it is
        // on (innermost last). Reverse iteration has visited every slot above
        // the cursor, so a swap-remove that moves an entity from slot >= cursor
        // into a slot < cursor makes that enumerator visit it twice.
        private int[] _debugCursors = new int[4];
        private int _debugIterators;
#endif

        /// <summary> Number of entities currently matching. </summary>
        public int Count => _count;

        /// <summary> True when no entity matches. </summary>
        public bool IsEmpty => _count == 0;

        /// <summary>
        /// Matching entity indices as a span; order is unspecified. Valid until the next structural
        /// change that adds an entity to the filter or removes one from it, and until a reserve that
        /// grows the filter (<see cref="Reserve"/>, <see cref="ComponentPoolBase.Reserve"/> of one of its
        /// types, a <see cref="World.ReserveEntities"/> that raises the reserve it takes from its types, the
        /// obsolete <see cref="World.Reserve(int, int)"/>): growth moves the entities to a new
        /// array and fills the old one with -1, so a span taken before it reads -1 from then on. Take a
        /// new span afterwards.
        /// </summary>
        public ReadOnlySpan<int> Entities => new(_denseEntities, 1, _count);

        /// <summary> Type indices required by this filter. </summary>
        public ReadOnlySpan<int> IncludedTypes => IncludedTypeIndices;

        /// <summary> Type indices excluded by this filter. </summary>
        public ReadOnlySpan<int> ExcludedTypes => ExcludedTypeIndices;

        /// <summary> Type indices of which at least one is required. </summary>
        public ReadOnlySpan<int> AnyTypes => AnyTypeIndices;

        /// <summary> Capacity of the dense entity array. </summary>
        public int DenseCapacity => _denseEntities.Length - 1;

        /// <summary>
        /// Entities this filter is reserved for, 0 while it is not reserved: the larger of the highest
        /// count passed to <see cref="Reserve"/> and the reserve it takes from its component types. A filter
        /// whose Inc types are all reserved (<see cref="ComponentPoolBase.Reserve"/>) takes the smallest of
        /// their reserves, since a matching entity has every one of them; when its Any types are all reserved
        /// too, at most the sum of theirs. A filter without Inc types takes the sum of its Any types' reserves
        /// when they are all reserved. Once the world reserved entity slots, the reserve taken from the types
        /// is at most <see cref="World.ReservedEntities"/>: no filter holds more entities than are alive.
        /// This holds for a filter created after its types were reserved as well. The reserve taken from
        /// the types follows their reserves as they are now, whatever the order of the calls, so a later
        /// <see cref="World.ReserveEntities"/> can also bring it down; the arrays keep the larger size. A
        /// reserved filter has room for that many entities and covers <see cref="World.ReservedEntities"/>
        /// with its sparse pages, extended by a later <see cref="World.ReserveEntities"/>.
        /// </summary>
        public int ReservedCount => _explicitReserve > _derivedReserve ? _explicitReserve : _derivedReserve;

        // The reserve taken from the component types, for World.
        internal int DerivedReserve => _derivedReserve;

        /// <summary> Bytes held by the sparse and dense arrays. </summary>
        public long AllocatedBytes {
            get {
                long bytes = (long)_denseEntities.Length * sizeof(int) + (long)_sparsePages.Length * IntPtr.Size;
                for (int i = 0; i < _sparsePages.Length; i++) {
                    if (_sparsePages[i] != null) {
                        bytes += PageSize * sizeof(int);
                    }
                }
                return bytes;
            }
        }

        internal Filter(int[] included, int[] excluded, int[] any, int denseCapacity) {
            IncludedTypeIndices = included;
            ExcludedTypeIndices = excluded;
            AnyTypeIndices = any;
            HasAny = any.Length > 0;

            int maxIdx = 0;
            foreach (int idx in included) {
                if (idx > maxIdx) maxIdx = idx;
            }
            foreach (int idx in excluded) {
                if (idx > maxIdx) maxIdx = idx;
            }
            foreach (int idx in any) {
                if (idx > maxIdx) maxIdx = idx;
            }

            int wordCount = (maxIdx >> 6) + 1;
            IncludeMask = new ulong[wordCount];
            ExcludeMask = new ulong[wordCount];
            AnyMask = new ulong[wordCount];

            foreach (int idx in included) {
                IncludeMask[idx >> 6] |= 1UL << (idx & 63);
            }
            foreach (int idx in excluded) {
                ExcludeMask[idx >> 6] |= 1UL << (idx & 63);
            }
            foreach (int idx in any) {
                AnyMask[idx >> 6] |= 1UL << (idx & 63);
            }

            int activeCount = 0;
            for (int w = 0; w < wordCount; w++) {
                if ((IncludeMask[w] | ExcludeMask[w] | AnyMask[w]) != 0) {
                    activeCount++;
                }
            }
            ActiveWords = new int[activeCount];
            int active = 0;
            for (int w = 0; w < wordCount; w++) {
                if ((IncludeMask[w] | ExcludeMask[w] | AnyMask[w]) != 0) {
                    ActiveWords[active++] = w;
                }
            }

            if (ActiveWords.Length == 1) {
                int word = ActiveWords[0];
                SingleWord = word;
                SingleIncludeMask = IncludeMask[word];
                SingleExcludeMask = ExcludeMask[word];
                SingleAnyMask = HasAny ? AnyMask[word] : ulong.MaxValue;
            } else {
                SingleWord = -1;
            }

            _sparsePages = new int[4][];

            _denseEntities = new int[denseCapacity + 1];
            Array.Fill(_denseEntities, FreeSlot);
            _count = 0;
        }

        /// <summary> Check if entity is currently in this filter. O(1). </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Contains(int entityIndex) {
            int pageIdx = entityIndex >> PageShift;
            var pages = _sparsePages;
            if ((uint)pageIdx >= (uint)pages.Length) {
                return false;
            }
            var page = pages[pageIdx];
            return page != null && page[entityIndex & PageMask] != -1;
        }

        /// <summary> Describes the filter constraints by component name, e.g. <c>Filter Inc&lt;A, B&gt;</c>. </summary>
        public override string ToString() {
            var sb = new System.Text.StringBuilder("Filter");
            AppendTypes(sb, " Inc<", IncludedTypeIndices);
            AppendTypes(sb, " Exc<", ExcludedTypeIndices);
            AppendTypes(sb, " Any<", AnyTypeIndices);
            return sb.ToString();
        }

        private static void AppendTypes(System.Text.StringBuilder sb, string prefix, int[] types) {
            if (types.Length == 0) {
                return;
            }
            sb.Append(prefix);
            for (int i = 0; i < types.Length; i++) {
                if (i > 0) {
                    sb.Append(", ");
                }
                sb.Append(ComponentType.NameOf(types[i]));
            }
            sb.Append('>');
        }

        /// <summary> Index of some matching entity. Throws when the filter is empty. </summary>
        public int First() {
            if (_count == 0) {
                ThrowEmpty();
            }
            return _denseEntities[1];
        }

        /// <summary> Index of some matching entity, or false when the filter is empty. </summary>
        public bool TryGetFirst(out int entityIndex) {
            if (_count == 0) {
                entityIndex = -1;
                return false;
            }
            entityIndex = _denseEntities[1];
            return true;
        }

        /// <summary> Index of the only matching entity. Throws unless exactly one entity matches. </summary>
        public int Single() {
            if (_count != 1) {
                ThrowNotSingle();
            }
            return _denseEntities[1];
        }

        /// <summary> Register a listener notified when entities enter or leave this filter. </summary>
        public void AddListener(IFilterListener listener) {
            if (_listeners == null && _world != null) {
                _world.OnFilterListening(this, 1);
            }
            var old = _listeners ?? Array.Empty<IFilterListener>();
            var grown = new IFilterListener[old.Length + 1];
            Array.Copy(old, grown, old.Length);
            grown[old.Length] = listener;
            _listeners = grown;
        }

        /// <summary> Unregister a filter listener. </summary>
        public void RemoveListener(IFilterListener listener) {
            var old = _listeners;
            if (old == null) {
                return;
            }
            int idx = Array.IndexOf(old, listener);
            if (idx < 0) {
                return;
            }
            if (old.Length == 1) {
                _listeners = null;
                if (_world != null) {
                    _world.OnFilterListening(this, -1);
                }
                return;
            }

            var shrunk = new IFilterListener[old.Length - 1];
            Array.Copy(old, 0, shrunk, 0, idx);
            Array.Copy(old, idx + 1, shrunk, idx, old.Length - idx - 1);
            _listeners = shrunk;
        }

        /// <summary>
        /// Add entity to filter. Called by World when entity matches. O(1).
        /// Returns true when the entity entered and listeners were notified (user code ran).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool AddEntity(int entityIndex) {
            var page = GetOrCreatePage(entityIndex);
            ref int slotRef = ref page[entityIndex & PageMask];
            if (slotRef != -1) {
                return false;
            }

            if (_count + 2 > _denseEntities.Length) {
#if KENSEI_DEBUG
                ReportGrowth("filter dense", WatchedDense(), _denseEntities.Length - 1,
                    Math.Max(_denseEntities.Length * 2, _count + 2) - 1);
#endif
                GrowDense(_count + 2);
            }

            int slot = _count + 1;
            slotRef = slot;
            _denseEntities[slot] = entityIndex;
            _count++;

            var listeners = _listeners;
            if (listeners != null) {
                NotifyAdded(listeners, entityIndex);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Remove entity from filter via swap-remove. O(1).
        /// Returns true when the entity left and listeners were notified (user code ran).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool RemoveEntity(int entityIndex) {
            int pageIdx = entityIndex >> PageShift;
            var pages = _sparsePages;
            if ((uint)pageIdx >= (uint)pages.Length) {
                return false;
            }
            var page = pages[pageIdx];
            if (page == null) {
                return false;
            }

            ref int slotRef = ref page[entityIndex & PageMask];
            int denseIdx = slotRef;
            if (denseIdx == -1) {
                return false;
            }
            int lastIdx = _count;

#if KENSEI_DEBUG
            if (_debugIterators > 0) {
                CheckRemovalDuringIteration(entityIndex, denseIdx, lastIdx);
            }
#endif

            if (denseIdx != lastIdx) {
                int lastEntity = _denseEntities[lastIdx];
                _denseEntities[denseIdx] = lastEntity;
                pages[lastEntity >> PageShift][lastEntity & PageMask] = denseIdx;
            }

            _denseEntities[lastIdx] = FreeSlot;
            slotRef = -1;
            _count--;

            var listeners = _listeners;
            if (listeners != null) {
                NotifyRemoved(listeners, entityIndex);
                return true;
            }
            return false;
        }

        /// <summary> Remove all entities from filter. Used by World.Clear(). Fires no listener events. </summary>
        internal void Clear() {
            var entities = _denseEntities;
            var pages = _sparsePages;
            for (int i = 1; i <= _count; i++) {
                int entity = entities[i];
                pages[entity >> PageShift][entity & PageMask] = -1;
            }
            Array.Fill(_denseEntities, FreeSlot, 1, _count);
            _count = 0;
        }

        // =================================================================
        // Zero-allocation reverse iteration via struct enumerator.
        // Iterating from end to start is safe for structural changes:
        // swap-remove replaces removed element with the last one,
        // which was already processed (we started from the end).
        // =================================================================

        /// <summary> Get enumerator for foreach. No heap allocation. </summary>
        public Enumerator GetEnumerator() {
            return new Enumerator(this);
        }

        // Il2CppSetOption does not propagate to nested types, so the hottest
        // loop needs its own attributes.
        /// <summary>
        /// Allocation-free enumerator over the matching entity indices, walking the
        /// dense array from the end. Removing the current entity during iteration is safe.
        /// </summary>
#if ENABLE_IL2CPP
        [Il2CppSetOption(Option.NullChecks, false)]
        [Il2CppSetOption(Option.ArrayBoundsChecks, false)]
#endif
        public ref struct Enumerator {
            private readonly Filter _filter;
            // Span instead of the raw array: its length lives in the promoted
            // struct (a register), so re-caching after a resize does not turn
            // the per-step bounds check into a heap load of array.Length.
            private Span<int> _entities;
            private int _index;
            private int _current;
#if KENSEI_DEBUG
            private readonly int _debugDepth;
#endif

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal Enumerator(Filter filter) {
                _filter = filter;
                _entities = filter._denseEntities;
                _index = filter._count + 1;  // start past the last 1-based slot
                _current = 0;
#if KENSEI_DEBUG
                _debugDepth = filter._debugIterators++;
                if (_debugDepth == filter._debugCursors.Length) {
                    Array.Resize(ref filter._debugCursors, _debugDepth * 2);
                }
                filter._debugCursors[_debugDepth] = _index;
#endif
            }

            /// <summary> Entity index at the current position. </summary>
            public int Current {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => _current;
            }

            // Hot path has no method calls on `this` (address exposure kills
            // struct promotion) and no heap loads beyond the element itself.
            // Hitting FreeSlot means the terminator at slot 0 (normal end),
            // the live range shrank below the cursor (several removals per
            // loop step), or the cached span was replaced by growth (the old
            // array is poisoned with FreeSlot on resize).
            /// <summary> Advances to the previous dense slot. </summary>
            /// <returns> False when all entities have been visited. </returns>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
                        // Stay at slot 1 so extra MoveNext calls keep probing
                        // the terminator instead of reading out of range.
                        _index = 1;
                        return false;
                    }
                    entity = _entities[i];
                }

                _index = i;
                _current = entity;
#if KENSEI_DEBUG
                _filter._debugCursors[_debugDepth] = i;
#endif
                return true;
            }

#if KENSEI_DEBUG
            /// <summary> Ends the iteration for the removal-during-iteration checks. Called by <c>foreach</c>. </summary>
            public void Dispose() {
                _filter._debugIterators--;
            }
#endif
        }

        // =================================================================
        // Private
        // =================================================================

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void NotifyAdded(IFilterListener[] listeners, int entityIndex) {
            for (int i = 0; i < listeners.Length; i++) {
                listeners[i].OnEntityAdded(this, entityIndex);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void NotifyRemoved(IFilterListener[] listeners, int entityIndex) {
            for (int i = 0; i < listeners.Length; i++) {
                listeners[i].OnEntityRemoved(this, entityIndex);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowEmpty() {
            throw new InvalidOperationException("Filter.First() on an empty filter");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ThrowNotSingle() {
            throw new InvalidOperationException($"Filter.Single() requires exactly one matching entity, found {_count}");
        }

#if KENSEI_DEBUG
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void CheckRemovalDuringIteration(int entityIndex, int denseIdx, int lastIdx) {
            for (int d = 0; d < _debugIterators; d++) {
                int cursor = _debugCursors[d];
                if (denseIdx < cursor && lastIdx >= cursor) {
                    throw new InvalidOperationException(
                        $"Entity {entityIndex} left a filter that is being iterated before the loop reached it, " +
                        $"and the swap-remove would move an already visited entity ({_denseEntities[lastIdx]}) into its place — " +
                        "that entity would be visited twice. Destroy or modify only the current entity inside foreach; " +
                        "defer other structural changes with a CommandBuffer");
                }
            }
        }
#endif

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int[] GetOrCreatePage(int entityIndex) {
            int pageIdx = entityIndex >> PageShift;
            var pages = _sparsePages;
            if ((uint)pageIdx < (uint)pages.Length) {
                var page = pages[pageIdx];
                if (page != null) {
                    return page;
                }
            }
#if KENSEI_DEBUG
            ReportGrowth("filter sparse page", WatchedSparse(), pageIdx * PageSize, (pageIdx + 1) * PageSize);
#endif
            return CreatePage(pageIdx);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private int[] CreatePage(int pageIdx) {
            if (pageIdx >= _sparsePages.Length) {
                Array.Resize(ref _sparsePages, Math.Max(_sparsePages.Length * 2, pageIdx + 1));
            }
            var page = new int[PageSize];
            Array.Fill(page, -1);
            _sparsePages[pageIdx] = page;
            return page;
        }

        /// <summary>
        /// Reserve this filter for <paramref name="count"/> matching entities: its dense entity array gets
        /// room for exactly <paramref name="count"/> (it does not round up), and its sparse pages cover
        /// <see cref="World.ReservedEntities"/> (or the indices below <paramref name="count"/>, when that is
        /// larger), also after a later <see cref="World.ReserveEntities"/>. Raises
        /// <see cref="ReservedCount"/>; never shrinks. Allocates; call at load time.
        /// </summary>
        /// <remarks>
        /// A filter whose Inc types are all reserved is reserved by them already (see
        /// <see cref="ReservedCount"/>): call this for a filter that needs more room than that, or one over a
        /// type that is not reserved. Safe during iteration: a running enumerator switches to the new array
        /// as after any other growth. A span from <see cref="Entities"/> taken before a Reserve that grows
        /// the array is invalid afterwards (it reads -1), as after any growth.
        /// </remarks>
        /// <param name="count"> Number of matching entities to have room for. 0 does nothing. </param>
        /// <exception cref="ArgumentOutOfRangeException"> When <paramref name="count"/> is negative. </exception>
        /// <exception cref="ObjectDisposedException"> When the filter's world was destroyed. </exception>
        /// <exception cref="OutOfMemoryException">
        /// When an array cannot be allocated, which includes <c>int.MaxValue</c> (the dense array needs one
        /// slot more than <paramref name="count"/>). The filter keeps its old arrays and its reserve.
        /// </exception>
        public void Reserve(int count) {
            if (count < 0) {
                throw new ArgumentOutOfRangeException(nameof(count), count, "Must not be negative");
            }
            if (_world.IsDestroyed) {
                throw new ObjectDisposedException(nameof(World), "Filter.Reserve after World.Destroy: a destroyed world has no storage to grow");
            }
            if (count == 0) {
                return;
            }
            ReserveDense(count);
            int entities = _world.ReservedEntities;
            ReserveSparse(count > entities ? count : entities);
            if (count > _explicitReserve) {
                _explicitReserve = count;
            }
        }

        // The filter's types bound it to count entities now (World.DerivedFilterReserve, 0: no bound):
        // room for them, and sparse pages for the reserved entity slots while the filter is reserved at
        // all. Set, not raised: the bound follows the current reserves whatever order they came in, so it
        // can go down (a later ReserveEntities caps it); arrays never shrink. Recorded only once
        // everything is allocated.
        internal void SetDerivedReserve(int count, int entities) {
            ReserveDense(count);
            int reserved = count > _explicitReserve ? count : _explicitReserve;
            if (reserved != 0) {
                ReserveSparse(reserved > entities ? reserved : entities);
            }
            _derivedReserve = count;
        }

#if KENSEI_DEBUG
        // The size a reserve stated for the dense array (entities), 0 when none did.
        private int WatchedDense() {
            int watched = _world.EveryPoolComponents;
            int reserved = ReservedCount;
            return reserved > watched ? reserved : watched;
        }

        // The size a reserve stated for the sparse pages (entity slots), 0 when none did: the reserved
        // entity slots for a reserved filter, the obsolete World.Reserve's entities for any.
        private int WatchedSparse() {
            int watched = _world.EveryPoolEntities;
            int reserved = ReservedCount;
            if (reserved != 0) {
                int entities = _world.ReservedEntities;
                if (entities != 0) {
                    int covered = entities > reserved ? entities : reserved;
                    if (covered > watched) {
                        watched = covered;
                    }
                }
            }
            return watched;
        }

        // A growth past what a reserve stated is counted; one that nothing reserved is only logged while
        // the profiler records. Both name the filter, so the name is built only then.
        private void ReportGrowth(string array, int watched, int oldSize, int newSize) {
            if (watched != 0) {
                _world.OnStorageGrown(DebugName, array, oldSize, newSize, watched);
            } else if (_world.LogsUnreservedGrowth) {
                _world.OnUnreservedGrowth(DebugName, array, oldSize, newSize);
            }
        }
#endif

        // Dense room for count matching entities. Used by Reserve, SetDerivedReserve and the obsolete World.Reserve.
        internal void ReserveDense(int count) {
            // Slot 0 is the terminator, entities occupy 1..count. Exact size: Reserve
            // states the need, doubling would overshoot it.
            if (count >= _denseEntities.Length) {
                if (count == int.MaxValue) {
                    // count + 1 would overflow into a negative size (OverflowException from the
                    // allocation); no array holds that many slots anyway.
                    ThrowDenseTooLarge(count);
                }
                ResizeDense(count + 1);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowDenseTooLarge(int count) {
            throw new OutOfMemoryException(
                $"A filter cannot hold {count} entities: its dense array needs {count} + 1 slots, more than an array can have");
        }

        // Allocate the sparse pages covering entity indices below entities, so adding any of them
        // never allocates a page. Used by Reserve, SetDerivedReserve, World.ReserveEntities and the
        // obsolete World.Reserve.
        internal void ReserveSparse(int entities) {
            if (entities <= 0) {
                return;
            }
            int lastPage = (entities - 1) >> PageShift;
            for (int p = 0; p <= lastPage; p++) {
                if (p >= _sparsePages.Length || _sparsePages[p] == null) {
                    CreatePage(p);
                }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void GrowDense(int needed) {
            ResizeDense(Math.Max(_denseEntities.Length * 2, needed));
        }

        private void ResizeDense(int newSize) {
            int[] old = _denseEntities;
            int[] grown = new int[newSize];
            Array.Copy(old, grown, old.Length);
            Array.Fill(grown, FreeSlot, old.Length, newSize - old.Length);
            _denseEntities = grown;

            // Poison the retired array so an enumerator still holding it lands
            // on the FreeSlot path and re-caches the current one.
            Array.Fill(old, FreeSlot, 0, old.Length);
        }
    }
}
