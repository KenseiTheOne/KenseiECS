using System;
using System.Runtime.CompilerServices;

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
    public class Filter {
        // Constraints
        internal int[] IncludedTypeIndices;
        internal int[] ExcludedTypeIndices;

        // Sparse set of matching entity indices
        private int[] _sparse;         // entityIndex → dense position, -1 = not in filter
        private int[] _denseEntities;  // dense[i] → entityIndex
        private int _count;

        public int Count => _count;

        internal Filter(int[] included, int[] excluded, int sparseCapacity, int denseCapacity) {
            IncludedTypeIndices = included;
            ExcludedTypeIndices = excluded;

            _sparse = new int[sparseCapacity];
            Array.Fill(_sparse, -1);

            _denseEntities = new int[denseCapacity];
            _count = 0;
        }

        /// <summary> Check if entity is currently in this filter. O(1). </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Contains(int entityIndex) {
            return entityIndex < _sparse.Length
                && _sparse[entityIndex] != -1;
        }

        /// <summary> Add entity to filter. Called by World when entity matches. O(1). </summary>
        internal void AddEntity(int entityIndex) {
            if (Contains(entityIndex)) {
                return;
            }

            EnsureSparseCapacity(entityIndex);
            EnsureDenseCapacity(_count + 1);

            _sparse[entityIndex] = _count;
            _denseEntities[_count] = entityIndex;
            _count++;
        }

        /// <summary> Remove entity from filter via swap-remove. O(1). </summary>
        internal void RemoveEntity(int entityIndex) {
            if (!Contains(entityIndex)) {
                return;
            }

            int denseIdx = _sparse[entityIndex];
            int lastIdx = _count - 1;

            if (denseIdx != lastIdx) {
                int lastEntity = _denseEntities[lastIdx];
                _denseEntities[denseIdx] = lastEntity;
                _sparse[lastEntity] = denseIdx;
            }

            _sparse[entityIndex] = -1;
            _count--;
        }

        /// <summary> Remove all entities from filter. Used by World.Clear(). </summary>
        internal void Clear() {
            Array.Fill(_sparse, -1, 0, _sparse.Length);
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
            private readonly int[] _entities;
            private int _index;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal Enumerator(Filter filter) {
                _entities = filter._denseEntities;
                _index = filter._count;  // start past the end
            }

            public int Current {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => _entities[_index];
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool MoveNext() {
                return --_index >= 0;
            }
        }

        // =================================================================
        // Private
        // =================================================================

        private void EnsureSparseCapacity(int entityIndex) {
            if (entityIndex < _sparse.Length) {
                return;
            }

            int newSize = Math.Max(_sparse.Length * 2, entityIndex + 1);
            int oldSize = _sparse.Length;
            Array.Resize(ref _sparse, newSize);
            Array.Fill(_sparse, -1, oldSize, newSize - oldSize);
        }

        private void EnsureDenseCapacity(int needed) {
            if (needed <= _denseEntities.Length) {
                return;
            }

            int newSize = _denseEntities.Length * 2;
            Array.Resize(ref _denseEntities, newSize);
        }
    }
}
