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
        private int[] _sparse;         // entityIndex → dense slot (1-based), -1 = not in filter
        private int[] _denseEntities;  // dense[slot] → entityIndex; free slots hold FreeSlot
        private int _count;

        // Copy-on-write, null when nobody listens.
        private IFilterListener[] _listeners;

#if KENSEI_DEBUG
        // Structural-change guard. Each live enumerator records the slot it is
        // on (innermost last). Reverse iteration has visited every slot above
        // the cursor, so a swap-remove that moves an entity from slot >= cursor
        // into a slot < cursor makes that enumerator visit it twice.
        private int[] _debugCursors = new int[4];
        private int _debugIterators;
#endif

        public int Count => _count;

        /// <summary> True when no entity matches. </summary>
        public bool IsEmpty => _count == 0;

        /// <summary>
        /// Matching entity indices as a span. Valid until the next structural change;
        /// order is unspecified.
        /// </summary>
        public ReadOnlySpan<int> Entities => new(_denseEntities, 1, _count);

        internal Filter(int[] included, int[] excluded, int[] any, int sparseCapacity, int denseCapacity) {
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

            _sparse = new int[sparseCapacity];
            Array.Fill(_sparse, -1);

            _denseEntities = new int[denseCapacity + 1];
            Array.Fill(_denseEntities, FreeSlot);
            _count = 0;
        }

        /// <summary> Check if entity is currently in this filter. O(1). </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Contains(int entityIndex) {
            return entityIndex < _sparse.Length
                && _sparse[entityIndex] != -1;
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
                return;
            }

            var shrunk = new IFilterListener[old.Length - 1];
            Array.Copy(old, 0, shrunk, 0, idx);
            Array.Copy(old, idx + 1, shrunk, idx, old.Length - idx - 1);
            _listeners = shrunk;
        }

        /// <summary> Add entity to filter. Called by World when entity matches. O(1). </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void AddEntity(int entityIndex) {
            if (Contains(entityIndex)) {
                return;
            }

            if (entityIndex >= _sparse.Length) {
                GrowSparse(entityIndex);
            }
            if (_count + 2 > _denseEntities.Length) {
                GrowDense(_count + 2);
            }

            int slot = _count + 1;
            _sparse[entityIndex] = slot;
            _denseEntities[slot] = entityIndex;
            _count++;

            var listeners = _listeners;
            if (listeners != null) {
                NotifyAdded(listeners, entityIndex);
            }
        }

        /// <summary> Remove entity from filter via swap-remove. O(1). </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void RemoveEntity(int entityIndex) {
            if (!Contains(entityIndex)) {
                return;
            }

            int denseIdx = _sparse[entityIndex];
            int lastIdx = _count;

#if KENSEI_DEBUG
            if (_debugIterators > 0) {
                CheckRemovalDuringIteration(entityIndex, denseIdx, lastIdx);
            }
#endif

            if (denseIdx != lastIdx) {
                int lastEntity = _denseEntities[lastIdx];
                _denseEntities[denseIdx] = lastEntity;
                _sparse[lastEntity] = denseIdx;
            }

            _denseEntities[lastIdx] = FreeSlot;
            _sparse[entityIndex] = -1;
            _count--;

            var listeners = _listeners;
            if (listeners != null) {
                NotifyRemoved(listeners, entityIndex);
            }
        }

        /// <summary> Remove all entities from filter. Used by World.Clear(). Fires no listener events. </summary>
        internal void Clear() {
            var entities = _denseEntities;
            for (int i = 1; i <= _count; i++) {
                _sparse[entities[i]] = -1;
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

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void GrowSparse(int entityIndex) {
            int newSize = Math.Max(_sparse.Length * 2, entityIndex + 1);
            int oldSize = _sparse.Length;
            Array.Resize(ref _sparse, newSize);
            Array.Fill(_sparse, -1, oldSize, newSize - oldSize);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void GrowDense(int needed) {
            int[] old = _denseEntities;
            int newSize = Math.Max(old.Length * 2, needed);
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
