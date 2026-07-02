using System;
using System.Runtime.CompilerServices;
#if ENABLE_IL2CPP
using Unity.IL2CPP.CompilerServices;
#endif

namespace KenseiECS {
    /// <summary>
    /// Cached query result — holds a dense list of entity indices
    /// that match the Include/Exclude component constraints.
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

        // Precomputed multi-word bitmasks for O(1) filter matching
        internal ulong[] IncludeMask;
        internal ulong[] ExcludeMask;

        // Word indices where IncludeMask or ExcludeMask has bits — matching
        // skips the all-zero words a sparse filter never constrains.
        internal int[] ActiveWords;

        // Most filters constrain a single mask word — matching then reads
        // three scalar fields instead of walking ActiveWords and both mask
        // arrays. SingleWord is -1 when the filter spans multiple words.
        internal int SingleWord;
        internal ulong SingleIncludeMask;
        internal ulong SingleExcludeMask;

        // Sparse set of matching entity indices.
        // Dense slot 0 is a permanent FreeSlot terminator and entities occupy
        // slots 1.._count, so the reverse enumerator stops on the same sentinel
        // probe it already does — no separate end-of-range check per step.
        private int[] _sparse;         // entityIndex → dense slot (1-based), -1 = not in filter
        private int[] _denseEntities;  // dense[slot] → entityIndex; free slots hold FreeSlot
        private int _count;

        public int Count => _count;

        internal Filter(int[] included, int[] excluded, int sparseCapacity, int denseCapacity) {
            IncludedTypeIndices = included;
            ExcludedTypeIndices = excluded;

            int maxIdx = 0;
            foreach (int idx in included) {
                if (idx > maxIdx) maxIdx = idx;
            }
            foreach (int idx in excluded) {
                if (idx > maxIdx) maxIdx = idx;
            }

            int wordCount = (maxIdx >> 6) + 1;
            IncludeMask = new ulong[wordCount];
            ExcludeMask = new ulong[wordCount];

            foreach (int idx in included) {
                IncludeMask[idx >> 6] |= 1UL << (idx & 63);
            }
            foreach (int idx in excluded) {
                ExcludeMask[idx >> 6] |= 1UL << (idx & 63);
            }

            int activeCount = 0;
            for (int w = 0; w < wordCount; w++) {
                if ((IncludeMask[w] | ExcludeMask[w]) != 0) {
                    activeCount++;
                }
            }
            ActiveWords = new int[activeCount];
            int active = 0;
            for (int w = 0; w < wordCount; w++) {
                if ((IncludeMask[w] | ExcludeMask[w]) != 0) {
                    ActiveWords[active++] = w;
                }
            }

            if (ActiveWords.Length == 1) {
                int word = ActiveWords[0];
                SingleWord = word;
                SingleIncludeMask = IncludeMask[word];
                SingleExcludeMask = ExcludeMask[word];
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
        }

        /// <summary> Remove entity from filter via swap-remove. O(1). </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void RemoveEntity(int entityIndex) {
            if (!Contains(entityIndex)) {
                return;
            }

            int denseIdx = _sparse[entityIndex];
            int lastIdx = _count;

            if (denseIdx != lastIdx) {
                int lastEntity = _denseEntities[lastIdx];
                _denseEntities[denseIdx] = lastEntity;
                _sparse[lastEntity] = denseIdx;
            }

            _denseEntities[lastIdx] = FreeSlot;
            _sparse[entityIndex] = -1;
            _count--;
        }

        /// <summary> Remove all entities from filter. Used by World.Clear(). </summary>
        internal void Clear() {
            Array.Fill(_sparse, -1, 0, _sparse.Length);
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

        public ref struct Enumerator {
            private readonly Filter _filter;
            // Span instead of the raw array: its length lives in the promoted
            // struct (a register), so re-caching after a resize does not turn
            // the per-step bounds check into a heap load of array.Length.
            private Span<int> _entities;
            private int _index;
            private int _current;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal Enumerator(Filter filter) {
                _filter = filter;
                _entities = filter._denseEntities;
                _index = filter._count + 1;  // start past the last 1-based slot
                _current = 0;
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
                return true;
            }
        }

        // =================================================================
        // Private
        // =================================================================

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
