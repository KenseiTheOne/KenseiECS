using System;
using System.Runtime.CompilerServices;
#if ENABLE_IL2CPP
using Unity.IL2CPP.CompilerServices;
#endif

namespace KenseiECS {
    /// <summary>
    /// Untyped part of a component pool: the sparse set of entity indices.
    /// World stores pools of every component type as this base so it can
    /// walk masks and remove components without knowing T.
    /// Operations that bypass World bookkeeping (Clear, AddDefault, CopyTo)
    /// are internal — calling them directly would desynchronize masks and filters.
    /// </summary>
#if ENABLE_IL2CPP
    [Il2CppSetOption(Option.NullChecks, false)]
    [Il2CppSetOption(Option.ArrayBoundsChecks, false)]
#endif
    public abstract class ComponentPoolBase {
        // sparse: entityIndex → denseIndex. -1 means "no component".
        // Grows to accommodate the maximum entityIndex.
        private protected int[] _sparse;

        // dense[i] → entityIndex, parallel to the typed data array in the derived pool.
        private protected int[] _denseEntities;

        private protected int _count;

        private protected readonly World _world;

        // Owning group, null for most pools. Checked on every Add/Remove.
        internal Group _ownerGroup;

        /// <summary> Unique type index of the component stored in this pool. </summary>
        public int TypeIndex { get; }

        /// <summary> Component type stored in this pool. </summary>
        public Type ComponentType { get; }

        /// <summary> Number of components in the pool. </summary>
        public int Count => _count;

        /// <summary> Dense entity index array — parallel to the typed data array. Valid range is 0..Count. </summary>
        public int[] RawEntities => _denseEntities;

        /// <summary> Length of the sparse array (grows with the highest entity index that ever had this component). </summary>
        public int SparseCapacity => _sparse.Length;

        /// <summary> Length of the dense arrays. </summary>
        public int DenseCapacity => _denseEntities.Length;

        /// <summary> Size of one component in bytes, or 0 when it holds references and cannot be measured. </summary>
        public abstract int ComponentSize { get; }

        /// <summary> Approximate bytes held by this pool's arrays. </summary>
        public long AllocatedBytes =>
            (long)_sparse.Length * sizeof(int) + (long)_denseEntities.Length * (sizeof(int) + ComponentSize);

        private protected ComponentPoolBase(World world, int typeIndex, Type componentType, int sparseCapacity, int denseCapacity) {
            _world = world;
            TypeIndex = typeIndex;
            ComponentType = componentType;

            _sparse = new int[sparseCapacity];
            Array.Fill(_sparse, -1);

            _denseEntities = new int[denseCapacity];
            _count = 0;
        }

        /// <summary> Check if entity has this component. O(1). </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Has(int entityIndex) {
            return entityIndex < _sparse.Length
                && _sparse[entityIndex] != -1;
        }

        /// <summary> Remove component. O(1) via swap-remove. No-op if the entity does not have it. </summary>
        public abstract void Remove(int entityIndex);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal int GetDenseIndex(int entityIndex) {
            return _sparse[entityIndex];
        }

        // Swap two dense slots (entities, data and any parallel arrays) and fix
        // the sparse entries. Used by owning groups.
        internal abstract void SwapDense(int denseA, int denseB);

        internal abstract void SetOwnerGroup(Group group);

        internal abstract void AddDefault(int entityIndex);

        internal abstract void Clear();

        internal abstract void CopyTo(int srcEntityIndex, int dstEntityIndex);

        internal abstract void WriteComponents(System.IO.BinaryWriter writer, object formatter);

        internal abstract void ReadComponent(System.IO.BinaryReader reader, int entityIndex, object formatter);

#if KENSEI_DEBUG
        /// <summary> Boxing access for debug and inspector. Not for runtime. </summary>
        public abstract object GetRaw(int entityIndex);

        /// <summary> Unboxing write for inspector editing. Not for runtime. </summary>
        public abstract void SetRaw(int entityIndex, object value);
#endif

        [MethodImpl(MethodImplOptions.NoInlining)]
        private protected void GrowSparse(int entityIndex) {
            int newSize = Math.Max(_sparse.Length * 2, entityIndex + 1);
            int oldSize = _sparse.Length;
            Array.Resize(ref _sparse, newSize);
            Array.Fill(_sparse, -1, oldSize, newSize - oldSize);
        }
    }
}
