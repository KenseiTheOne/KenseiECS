using System;
using System.Runtime.CompilerServices;
#if ENABLE_IL2CPP
using Unity.IL2CPP.CompilerServices;
#endif

namespace KenseiECS {
    /// <summary>
    /// Sparse Set — storage for components of a single type.
    ///
    /// Layout:
    ///   sparse[entityIndex] → denseIndex       (O(1) lookup by entity)
    ///   dense[denseIndex]   → entityIndex       (reverse mapping)
    ///   data[denseIndex]    → T                 (component data)
    ///
    /// Dense array has no gaps — ideal for linear iteration.
    /// Removal via swap-remove: last element takes the place of the removed one.
    /// Notifies World on Add/Remove so filters stay up to date.
    /// </summary>
#if ENABLE_IL2CPP
    [Il2CppSetOption(Option.NullChecks, false)]
    [Il2CppSetOption(Option.ArrayBoundsChecks, false)]
#endif
    public class ComponentPool<T> : IComponentPool where T : struct, IComponent {
        // sparse: entityIndex → denseIndex. -1 means "no component".
        // Grows to accommodate the maximum entityIndex.
        private int[] _sparse;

        // Dense arrays — no gaps
        private int[] _denseEntities;  // dense[i] → entityIndex
        private T[] _denseData;        // dense[i] → component

        private int _count;

        // Back-reference to World for filter notifications
        private readonly World _world;

        // Auto-reset delegate — cached at construction, null if T doesn't implement IAutoReset
        private delegate void AutoResetHandler(ref T component);
        private readonly AutoResetHandler _autoReset;

        // static readonly per generic instantiation — RyuJIT folds it into a constant
        // on tier-1 and eliminates the dead Remove branch entirely.
        private static readonly bool HasAutoReset = typeof(IAutoReset<T>).IsAssignableFrom(typeof(T));

        // Auto-copy delegate — cached at construction, null if T doesn't implement IAutoCopy
        private delegate void AutoCopyHandler(ref T component);
        private readonly AutoCopyHandler _autoCopy;

        public int TypeIndex { get; }
        public int Count => _count;

        /// <summary> Dense data array — for linear iteration in systems. </summary>
        public T[] RawData => _denseData;

        /// <summary> Dense entity index array — parallel to RawData. </summary>
        public int[] RawEntities => _denseEntities;

        internal ComponentPool(World world, int sparseCapacity, int denseCapacity) {
            _world = world;
            TypeIndex = ComponentType<T>.Index;

            _sparse = new int[sparseCapacity];
            Array.Fill(_sparse, -1);

            _denseEntities = new int[denseCapacity];
            _denseData = new T[denseCapacity];
            _count = 0;

            // Closed delegates over one boxed default(T) instead of MakeGenericType —
            // runtime generic instantiation of a value-type bridge is not AOT-safe
            // (ExecutionEngineException on IL2CPP). One boxing allocation per pool.
            if (HasAutoReset) {
                var method = typeof(T).GetMethod(nameof(IAutoReset<T>.AutoReset));
                _autoReset = (AutoResetHandler)Delegate.CreateDelegate(typeof(AutoResetHandler), default(T), method);
            }

            if (typeof(IAutoCopy<T>).IsAssignableFrom(typeof(T))) {
                var method = typeof(T).GetMethod(nameof(IAutoCopy<T>.AutoCopy));
                _autoCopy = (AutoCopyHandler)Delegate.CreateDelegate(typeof(AutoCopyHandler), default(T), method);
            }
        }

        /// <summary> Check if entity has this component. O(1). </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Has(int entityIndex) {
            return entityIndex < _sparse.Length
                && _sparse[entityIndex] != -1;
        }

        /// <summary>
        /// Get component by ref. O(1).
        /// ref return is critical for struct components — avoids copying.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T Get(int entityIndex) {
#if KENSEI_DEBUG
            if (!Has(entityIndex)) {
                throw new InvalidOperationException(
                    $"Get<{typeof(T).Name}> on entity {entityIndex} without this component");
            }
#endif
            int denseIdx = _sparse[entityIndex];
            return ref _denseData[denseIdx];
        }

        /// <summary> Add component. O(1). Returns ref to the added component. </summary>
        public ref T Add(int entityIndex, T value) {
            if (Has(entityIndex)) {
                throw new InvalidOperationException(
                    $"Entity {entityIndex} already has component {typeof(T).Name}");
            }

            EnsureSparseCapacity(entityIndex);
            EnsureDenseCapacity(_count + 1);

            int denseIdx = _count;
            _sparse[entityIndex] = denseIdx;
            _denseEntities[denseIdx] = entityIndex;
            _denseData[denseIdx] = value;
            _count++;

            _world.OnComponentAdded(entityIndex, TypeIndex);
#if KENSEI_DEBUG
            EcsProfiler.OnComponentAdded(_world, _world.Tick, entityIndex, typeof(T).Name);
#endif

            return ref _denseData[denseIdx];
        }

        /// <summary> Add default(T) component. Used by World.Warmup(). </summary>
        public void AddDefault(int entityIndex) {
            Add(entityIndex, default);
        }

        /// <summary>
        /// Remove component. O(1) via swap-remove.
        /// Last dense element moves to the removed slot, keeping the array dense.
        /// </summary>
        public void Remove(int entityIndex) {
            if (!Has(entityIndex)) {
                return;
            }

            int denseIdx = _sparse[entityIndex];
            int lastDenseIdx = _count - 1;

            if (HasAutoReset) {
                _autoReset(ref _denseData[denseIdx]);

                if (denseIdx != lastDenseIdx) {
                    int lastEntity = _denseEntities[lastDenseIdx];

                    _denseEntities[denseIdx] = lastEntity;
                    _denseData[denseIdx] = _denseData[lastDenseIdx];
                    _sparse[lastEntity] = denseIdx;
                }

                // The freed tail slot is a bitwise duplicate of the moved live component
                // (or the already-reset removed one) — AutoReset here would corrupt
                // reference fields shared with the live copy, so plain default is used.
                _denseData[lastDenseIdx] = default;

                _sparse[entityIndex] = -1;
                _count--;
            } else {
                if (denseIdx != lastDenseIdx) {
                    int lastEntity = _denseEntities[lastDenseIdx];

                    _denseEntities[denseIdx] = lastEntity;
                    _denseData[denseIdx] = _denseData[lastDenseIdx];
                    _sparse[lastEntity] = denseIdx;
                }

                _sparse[entityIndex] = -1;
                _count--;
                _denseData[_count] = default;
            }

            _world.OnComponentRemoved(entityIndex, TypeIndex);
#if KENSEI_DEBUG
            EcsProfiler.OnComponentRemoved(_world, _world.Tick, entityIndex, typeof(T).Name);
#endif
        }

#if KENSEI_DEBUG
        /// <summary> Boxing access for debug and inspector. Not for runtime. </summary>
        public object GetRaw(int entityIndex) {
            return Get(entityIndex);
        }
#endif

        /// <summary> Copy component from src entity to dst entity. </summary>
        public void CopyTo(int srcEntityIndex, int dstEntityIndex) {
            if (!Has(srcEntityIndex)) {
                return;
            }

            var value = Get(srcEntityIndex);
            if (_autoCopy != null) {
                _autoCopy(ref value);
            }

            if (Has(dstEntityIndex)) {
                Get(dstEntityIndex) = value;
            } else {
                Add(dstEntityIndex, value);
            }
        }

#if KENSEI_DEBUG
        /// <summary> Unboxing write for inspector editing. Not for runtime. </summary>
        public void SetRaw(int entityIndex, object value) {
            int denseIdx = _sparse[entityIndex];
            _denseData[denseIdx] = (T)value;
        }
#endif

        /// <summary>
        /// Remove all components. Resets sparse and dense arrays.
        /// Does NOT notify World — called only from World.Clear()
        /// which handles filter reset separately.
        /// </summary>
        public void Clear() {
            Array.Fill(_sparse, -1, 0, _sparse.Length);
            if (_autoReset != null) {
                for (int i = 0; i < _count; i++) {
                    _autoReset(ref _denseData[i]);
                }
            }
            Array.Clear(_denseData, 0, _count);
            _count = 0;
        }

        /// <summary> Get the dense index for an entity. Used by Group for sorting. </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal int GetDenseIndex(int entityIndex) {
            return _sparse[entityIndex];
        }

        /// <summary>
        /// Swap two elements in dense arrays. Used by Group for sorting.
        /// Updates sparse array to maintain consistency.
        /// </summary>
        internal void SwapDense(int denseA, int denseB) {
            if (denseA == denseB) {
                return;
            }

            int entityA = _denseEntities[denseA];
            int entityB = _denseEntities[denseB];
            _denseEntities[denseA] = entityB;
            _denseEntities[denseB] = entityA;

            var tmp = _denseData[denseA];
            _denseData[denseA] = _denseData[denseB];
            _denseData[denseB] = tmp;

            _sparse[entityA] = denseB;
            _sparse[entityB] = denseA;
        }

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
            if (needed <= _denseData.Length) {
                return;
            }

            int newSize = Math.Max(_denseData.Length * 2, needed);
            Array.Resize(ref _denseEntities, newSize);
            Array.Resize(ref _denseData, newSize);
        }
    }

}
