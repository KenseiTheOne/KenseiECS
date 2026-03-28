using System;
using System.Reflection;
using System.Runtime.CompilerServices;

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

            if (typeof(IAutoReset<T>).IsAssignableFrom(typeof(T))) {
                var method = typeof(AutoResetBridge<>).MakeGenericType(typeof(T))
                    .GetMethod("Invoke", BindingFlags.Public | BindingFlags.Static);
                _autoReset = (AutoResetHandler)Delegate.CreateDelegate(typeof(AutoResetHandler), method);
            }

            if (typeof(IAutoCopy<T>).IsAssignableFrom(typeof(T))) {
                var method = typeof(AutoCopyBridge<>).MakeGenericType(typeof(T))
                    .GetMethod("Invoke", BindingFlags.Public | BindingFlags.Static);
                _autoCopy = (AutoCopyHandler)Delegate.CreateDelegate(typeof(AutoCopyHandler), method);
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
            int denseIdx = _sparse[entityIndex];
            return ref _denseData[denseIdx];
        }

        /// <summary> Add component. O(1). Returns ref to the added component. </summary>
        public ref T Add(int entityIndex, T value) {
#if DEBUG
            if (Has(entityIndex)) {
                throw new InvalidOperationException(
                    $"Entity {entityIndex} already has component {typeof(T).Name}");
            }
#endif

            EnsureSparseCapacity(entityIndex);
            EnsureDenseCapacity(_count + 1);

            int denseIdx = _count;
            _sparse[entityIndex] = denseIdx;
            _denseEntities[denseIdx] = entityIndex;
            _denseData[denseIdx] = value;
            _count++;

            _world.OnComponentAdded(entityIndex, TypeIndex);
#if KENSEI_DEBUG
            EcsProfiler.OnComponentAdded(_world.Tick, entityIndex, typeof(T).Name);
#endif

            return ref _denseData[denseIdx];
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

            if (denseIdx != lastDenseIdx) {
                int lastEntity = _denseEntities[lastDenseIdx];

                _denseEntities[denseIdx] = lastEntity;
                _denseData[denseIdx] = _denseData[lastDenseIdx];
                _sparse[lastEntity] = denseIdx;
            }

            _sparse[entityIndex] = -1;
            _count--;

            if (_autoReset != null) {
                _autoReset(ref _denseData[_count]);
            } else {
                _denseData[_count] = default;
            }

            _world.OnComponentRemoved(entityIndex, TypeIndex);
#if KENSEI_DEBUG
            EcsProfiler.OnComponentRemoved(_world.Tick, entityIndex, typeof(T).Name);
#endif
        }

        /// <summary> Boxing access for debug and inspector. Not for runtime. </summary>
        public object GetRaw(int entityIndex) {
            return Get(entityIndex);
        }

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

    internal static class AutoResetBridge<T> where T : struct, IComponent, IAutoReset<T> {
        public static void Invoke(ref T c) {
            c.AutoReset(ref c);
        }
    }

    internal static class AutoCopyBridge<T> where T : struct, IComponent, IAutoCopy<T> {
        public static void Invoke(ref T c) {
            c.AutoCopy(ref c);
        }
    }
}
