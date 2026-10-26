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

        private static int _lastPoolId;

        // Nonzero and unique across worlds: names the pool that holds a pooled list (PooledList<T>)
        // without storing a reference to it.
        internal readonly int PoolId;

        // Owning group, null for most pools. Checked on every Add/Remove.
        internal Group _ownerGroup;

        // Highest count passed to Reserve, 0 while the pool is not reserved. Read only when the pool
        // is reserved, when the world reserves entity slots and when a filter over the type is created
        // or reserved, never on Add or Remove.
        private int _reservedComponents;

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

        /// <summary>
        /// Components this pool is reserved for: the highest count passed to <see cref="Reserve"/>, 0 while
        /// it was not reserved (the obsolete <see cref="World.Reserve(int, int)"/> does not count). A
        /// reserved pool covers <see cref="World.ReservedEntities"/> with its sparse array, and the filters
        /// whose Inc types are all reserved take their reserve from it (<see cref="Filter.ReservedCount"/>).
        /// </summary>
        public int ReservedComponents => _reservedComponents;

        /// <summary> Size of one component in bytes, or 0 when it holds references and cannot be measured. </summary>
        public abstract int ComponentSize { get; }

        /// <summary> Approximate bytes held by this pool's arrays. </summary>
        public long AllocatedBytes =>
            (long)_sparse.Length * sizeof(int) + (long)_denseEntities.Length * (sizeof(int) + ComponentSize);

        private protected ComponentPoolBase(World world, int typeIndex, Type componentType, int sparseCapacity, int denseCapacity) {
            _world = world;
            TypeIndex = typeIndex;
            ComponentType = componentType;
            // Pools may be created on several threads, one world each. 0 means "no pool" to a pooled
            // list, so it is skipped when the counter wraps around.
            int id = System.Threading.Interlocked.Increment(ref _lastPoolId);
            PoolId = id != 0 ? id : System.Threading.Interlocked.Increment(ref _lastPoolId);

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

        /// <summary>
        /// Reserve this pool for the peak of its type: <paramref name="count"/> components fit without
        /// further growth, on any entity slot below <see cref="World.ReservedEntities"/> (or below
        /// <paramref name="count"/>, when that is larger). The dense arrays (entities, data, change
        /// versions when tracked) get exactly <paramref name="count"/> slots and the sparse array covers
        /// the reserved entity slots — also after a later <see cref="World.ReserveEntities"/>, which extends it.
        /// Every filter whose Inc types are now all reserved is reserved with them
        /// (<see cref="Filter.ReservedCount"/>). Never shrinks; a repeated call only raises
        /// <see cref="ReservedComponents"/>. Allocates; call at load time.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Reserve the types whose count changes during play. A type that is not reserved costs nothing up
        /// front and grows on demand: its first component on a high entity index grows its sparse array to
        /// that index, a larger count grows its dense arrays. A singleton created at load needs no reserve.
        /// </para>
        /// <para>
        /// The cost is <paramref name="count"/> × (4 bytes + the component size, + 4 when change versions
        /// are tracked) for the dense side and 4 bytes per reserved entity slot for the sparse side, plus
        /// 4 bytes per slot and per matching entity for each filter it completes.
        /// </para>
        /// <para>
        /// Like growth during <c>Add</c>, a Reserve that grows the dense arrays moves the components to new
        /// arrays: a <c>ref</c> from <c>Get</c>/<c>Add</c>/<c>Modify</c> and the <c>RawData</c> and
        /// <see cref="RawEntities"/> arrays taken before it no longer reach the pool, and neither does a
        /// <see cref="Filter.Entities"/> span of a filter it grows. A running <c>foreach</c> over a filter is
        /// not affected. If an allocation fails (<see cref="OutOfMemoryException"/>), the pool keeps working
        /// (with arrays grown before the failure) and its reserve, and that of the filters over its type,
        /// is not raised. Under KENSEI_DEBUG growth past the reserve is
        /// counted in <see cref="World.GrowthsAfterReserve"/>.
        /// </para>
        /// </remarks>
        /// <param name="count"> Number of components to have room for. 0 does nothing. </param>
        /// <exception cref="ArgumentOutOfRangeException"> When <paramref name="count"/> is negative. </exception>
        /// <exception cref="ObjectDisposedException"> When the pool's world was destroyed. </exception>
        /// <exception cref="OutOfMemoryException">
        /// When an array of the pool or of a filter it reserves cannot be allocated; the pool keeps working
        /// and its reserve is not raised, so a repeated call tries again.
        /// </exception>
        public void Reserve(int count) {
            if (count < 0) {
                throw new ArgumentOutOfRangeException(nameof(count), count, "Must not be negative");
            }
            if (_world.IsDestroyed) {
                throw new ObjectDisposedException(nameof(World), "ComponentPool.Reserve after World.Destroy: a destroyed world has no storage to grow");
            }
            if (count == 0) {
                return;
            }
            int entities = _world.ReservedEntities;
            ReserveSparse(count > entities ? count : entities);
            ReserveDense(count);

            int previous = _reservedComponents;
            if (count <= previous) {
                return;
            }
            // Raised before the filters are sized (they read it), and taken back if one of them cannot be:
            // a repeated call then tries again instead of finding the pool reserved already.
            _reservedComponents = count;
            try {
                _world.OnPoolReserved(this);
            } catch {
                _reservedComponents = previous;
                // Filters sized before the failure take their reserve from the restored one again; that
                // only lowers their bounds and allocates nothing.
                _world.OnPoolReserved(this);
                throw;
            }
        }

        // Grow the dense arrays to hold count components. Used by Reserve and the obsolete World.Reserve.
        internal abstract void ReserveDense(int count);

        // Grow the sparse array to cover entity indices below entities. Used by Reserve,
        // World.ReserveEntities and the obsolete World.Reserve.
        internal void ReserveSparse(int entities) {
            if (entities > _sparse.Length) {
                int oldSize = _sparse.Length;
                Array.Resize(ref _sparse, entities);
                Array.Fill(_sparse, -1, oldSize, entities - oldSize);
            }
        }

#if KENSEI_DEBUG
        // The size a reserve stated for the sparse array, 0 when none did (its growth is then normal):
        // the reserved entity slots for a reserved pool, the obsolete World.Reserve's entities for any.
        private protected int WatchedSparse() {
            int watched = _world.EveryPoolEntities;
            if (_reservedComponents != 0) {
                int entities = _world.ReservedEntities;
                if (entities != 0) {
                    int covered = entities > _reservedComponents ? entities : _reservedComponents;
                    if (covered > watched) {
                        watched = covered;
                    }
                }
            }
            return watched;
        }

        // The size a reserve stated for the dense arrays, 0 when none did.
        private protected int WatchedDense() {
            int watched = _world.EveryPoolComponents;
            return _reservedComponents > watched ? _reservedComponents : watched;
        }
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
