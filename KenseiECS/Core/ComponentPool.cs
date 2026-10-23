using System;
using System.Reflection;
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
    // Sealed so the Pool<T>() fast-path type check compiles to a single
    // method-table comparison instead of a cast helper call.
    public sealed class ComponentPool<T> : ComponentPoolBase where T : struct, IComponent {
        private T[] _denseData;

        // Auto-reset delegate — cached at construction, null if T doesn't implement IAutoReset.
        // Hot paths test this instance field rather than a static readonly flag:
        // IL2CPP does not fold static readonly into a constant and would pay a
        // class-init check plus an RGCTX load on every Remove.
        private delegate void AutoResetHandler(ref T component);
        private readonly AutoResetHandler _autoReset;

        // Auto-copy delegate — cached at construction, null if T doesn't implement IAutoCopy
        private delegate void AutoCopyHandler(ref T component);
        private readonly AutoCopyHandler _autoCopy;

        // Copy-on-write, null when nobody listens.
        private IComponentListener<T>[] _listeners;

        // Change version per dense slot, null unless TrackChanges() was called.
        private int[] _changedVersions;

        // Listeners, owning group or change tracking present. One flag keeps
        // the common Add/Remove path at a single extra branch.
        private bool _hasHooks;

        // Entity whose OnRemoved notification is in progress, and the next listener
        // to notify. A listener that removes the same component again (directly or
        // through DestroyEntity) continues the notification from there instead of
        // restarting it, so every listener sees OnRemoved exactly once.
        private int _removingEntity = -1;
        private int _removingNextListener;

        // Target of the ref returned by Add when a listener removed the component
        // again before Add returned: writes through it land nowhere.
        private static T _orphan;

#if KENSEI_DEBUG
        // typeof(T).Name allocates a new string per call on Mono — cache it for the profiler.
        private string _typeName;
        private string TypeName => _typeName ??= typeof(T).Name;
#endif

        private static readonly int Size = MeasureSize();

        /// <summary> Whether this pool records a change version per component. </summary>
        public bool TracksChanges => _changedVersions != null;

        /// <summary>
        /// Start recording change versions. Existing components count as changed now.
        /// Costs one int per component and one store per Add, Remove and Modify.
        /// </summary>
        public void TrackChanges() {
            if (_changedVersions != null) {
                return;
            }
            _changedVersions = new int[_denseData.Length];
            int version = _world.NextChangeVersion();
            for (int i = 0; i < _count; i++) {
                _changedVersions[i] = version;
            }
            _hasHooks = true;
        }

        internal override void SetOwnerGroup(Group group) {
            _ownerGroup = group;
            RefreshHooks();
        }

        private void RefreshHooks() {
            _hasHooks = _listeners != null || _ownerGroup != null || _changedVersions != null;
        }

        /// <summary>
        /// Get component by ref and mark it changed. Use instead of Get when writing
        /// to a tracked component. The ref has the same validity as Get.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T Modify(int entityIndex) {
#if KENSEI_DEBUG
            if (!Has(entityIndex)) {
                ThrowMissing(entityIndex);
            }
#endif
            int denseIdx = _sparse[entityIndex];
            var versions = _changedVersions;
            if (versions != null) {
                versions[denseIdx] = _world.NextChangeVersion();
            }
            return ref _denseData[denseIdx];
        }

        /// <summary> Mark the component changed without reading it. </summary>
        public void MarkChanged(int entityIndex) {
#if KENSEI_DEBUG
            if (!Has(entityIndex)) {
                ThrowMissing(entityIndex);
            }
#endif
            var versions = _changedVersions;
            if (versions != null) {
                versions[_sparse[entityIndex]] = _world.NextChangeVersion();
            }
        }

        /// <summary> Change version recorded for the component (from Add, Modify or MarkChanged). Requires TrackChanges. </summary>
        public int ChangedVersion(int entityIndex) {
            if (_changedVersions == null) {
                ThrowNotTracking();
            }
#if KENSEI_DEBUG
            if (!Has(entityIndex)) {
                ThrowMissing(entityIndex);
            }
#endif
            return _changedVersions[_sparse[entityIndex]];
        }

        /// <summary> True when the component was added or modified after the given world.ChangeVersion. </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ChangedSince(int entityIndex, int version) {
            return ChangedVersion(entityIndex) > version;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowNotTracking() {
            throw new InvalidOperationException(
                $"Pool<{typeof(T).Name}> does not track changes — call TrackChanges() once (e.g. in Init) before using ChangedSince");
        }

        /// <summary> Dense data array — for linear iteration in systems. Valid range is 0..Count. </summary>
        public T[] RawData => _denseData;

        /// <inheritdoc/>
        public override int ComponentSize => Size;

        private static int MeasureSize() {
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) {
                return 0;
            }
            try {
                return System.Runtime.InteropServices.Marshal.SizeOf<T>();
            } catch (ArgumentException) {
                return 0;
            }
        }

        /// <summary> Register a typed listener notified on Add (after filters update) and Remove (before AutoReset). </summary>
        public void AddListener(IComponentListener<T> listener) {
            var old = _listeners ?? Array.Empty<IComponentListener<T>>();
            var grown = new IComponentListener<T>[old.Length + 1];
            Array.Copy(old, grown, old.Length);
            grown[old.Length] = listener;
            _listeners = grown;
            _hasHooks = true;
        }

        /// <summary> Unregister a typed listener. </summary>
        public void RemoveListener(IComponentListener<T> listener) {
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
                RefreshHooks();
                return;
            }

            var shrunk = new IComponentListener<T>[old.Length - 1];
            Array.Copy(old, 0, shrunk, 0, idx);
            Array.Copy(old, idx + 1, shrunk, idx, old.Length - idx - 1);
            _listeners = shrunk;
        }

        internal ComponentPool(World world, int sparseCapacity, int denseCapacity)
            : base(world, ComponentType<T>.Index, typeof(T), sparseCapacity, denseCapacity) {
            _denseData = new T[denseCapacity];

            // Closed delegates over one boxed default(T) instead of MakeGenericType —
            // runtime generic instantiation of a value-type bridge is not AOT-safe
            // (ExecutionEngineException on IL2CPP). One boxing allocation per pool.
            // The interface map resolves explicit implementations too.
            if (typeof(IAutoReset<T>).IsAssignableFrom(typeof(T))) {
                var method = InterfaceMethod(typeof(IAutoReset<T>));
                _autoReset = (AutoResetHandler)Delegate.CreateDelegate(typeof(AutoResetHandler), default(T), method);
            }

            if (typeof(IAutoCopy<T>).IsAssignableFrom(typeof(T))) {
                var method = InterfaceMethod(typeof(IAutoCopy<T>));
                _autoCopy = (AutoCopyHandler)Delegate.CreateDelegate(typeof(AutoCopyHandler), default(T), method);
            }
        }

        private static MethodInfo InterfaceMethod(Type interfaceType) =>
            typeof(T).GetInterfaceMap(interfaceType).TargetMethods[0];

        /// <summary>
        /// Get component by ref. O(1).
        /// ref return is critical for struct components — avoids copying.
        /// The ref is valid until the next Add or Remove of this component type on any entity:
        /// Add may grow the dense array, Remove swaps the last component into the freed slot.
        /// If this pool belongs to an owning group, an Add or Remove of any of the group's
        /// types also invalidates it — the group reorders the dense arrays of all its pools.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T Get(int entityIndex) {
#if KENSEI_DEBUG
            if (!Has(entityIndex)) {
                ThrowMissing(entityIndex);
            }
#endif
            int denseIdx = _sparse[entityIndex];
            return ref _denseData[denseIdx];
        }

        /// <summary>
        /// Add component. O(1). Returns ref to the added component. Throws if the entity already has it.
        /// The ref has the same validity as Get. If a listener removes the component again
        /// before Add returns, the ref points at a scratch value and writes through it are discarded.
        /// </summary>
        public ref T Add(int entityIndex, T value) {
#if KENSEI_DEBUG
            if (!_world.IsSlotAcceptingComponents(entityIndex)) {
                ThrowDeadSlot(entityIndex);
            }
#endif
            if (Has(entityIndex)) {
                ThrowAlreadyHas(entityIndex);
            }

            if (entityIndex >= _sparse.Length) {
#if KENSEI_DEBUG
                // TypeName is an argument, so it is built before OnStorageGrown could ignore an
                // unwatched growth: check first, or every pool pays for its name on its first growth.
                if (_world.WatchesGrowth(false)) {
                    _world.OnStorageGrown(false, TypeName, "pool sparse", _sparse.Length, Math.Max(_sparse.Length * 2, entityIndex + 1));
                }
#endif
                GrowSparse(entityIndex);
            }

            int denseIdx = _count;
            if (denseIdx == _denseData.Length) {
#if KENSEI_DEBUG
                if (_world.WatchesGrowth(true)) {
                    _world.OnStorageGrown(true, TypeName, "pool dense", _denseData.Length, Math.Max(_denseData.Length * 2, denseIdx + 1));
                }
#endif
                GrowDense(denseIdx + 1);
            }

            _sparse[entityIndex] = denseIdx;
            _denseEntities[denseIdx] = entityIndex;
            _denseData[denseIdx] = value;
            _count++;

            if (_hasHooks) {
                return ref AddHooked(entityIndex, denseIdx);
            }

            _world.OnComponentAdded(entityIndex, TypeIndex);
#if KENSEI_DEBUG
            if (EcsProfiler.IsEnabled) {
                EcsProfiler.OnComponentAdded(_world, _world.Tick, entityIndex, TypeName);
            }
#endif
            // Filter and world listeners run inside OnComponentAdded and may have
            // added or removed this type elsewhere, moving the component.
            return ref RefAfterListeners(entityIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ref T RefAfterListeners(int entityIndex) {
            int denseIdx = _sparse[entityIndex];
            if (denseIdx < 0) {
                _orphan = default;
                return ref _orphan;
            }
            return ref _denseData[denseIdx];
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private ref T AddHooked(int entityIndex, int denseIdx) {
            var versions = _changedVersions;
            if (versions != null) {
                versions[denseIdx] = _world.NextChangeVersion();
            }

            var group = _ownerGroup;
            if (group != null) {
                group.OnAdded(entityIndex);
            }

            _world.OnComponentAdded(entityIndex, TypeIndex);
#if KENSEI_DEBUG
            if (EcsProfiler.IsEnabled) {
                EcsProfiler.OnComponentAdded(_world, _world.Tick, entityIndex, TypeName);
            }
#endif

            // Every callback may move the component or remove it again: the dense
            // index is re-read before each typed listener and before returning.
            var listeners = _listeners;
            if (listeners != null) {
                for (int i = 0; i < listeners.Length; i++) {
                    denseIdx = _sparse[entityIndex];
                    if (denseIdx < 0) {
                        break;
                    }
                    listeners[i].OnAdded(entityIndex, ref _denseData[denseIdx]);
                }
            }

            return ref RefAfterListeners(entityIndex);
        }

        // Listeners run before any index is read: they may remove other
        // components of this type and shift the dense layout. The group swap
        // and the version move follow, right before the swap-remove itself.
        // A listener may also remove this very component (OnRemoved destroying its
        // own entity): the nested Remove finishes the notification and the removal,
        // and the outer Remove sees the component gone and stops.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void RemoveHooks(int entityIndex) {
            var listeners = _listeners;
            if (listeners != null) {
                int outerEntity = _removingEntity;
                int outerNext = _removingNextListener;
                int i = outerEntity == entityIndex ? outerNext : 0;
                _removingEntity = entityIndex;
                try {
                    for (; i < listeners.Length; i++) {
                        _removingNextListener = i + 1;
                        listeners[i].OnRemoved(entityIndex, ref _denseData[_sparse[entityIndex]]);
                        if (!Has(entityIndex)) {
                            return;
                        }
                    }
                } finally {
                    _removingEntity = outerEntity;
                    _removingNextListener = outerNext;
                }
            }

            var group = _ownerGroup;
            if (group != null) {
                group.OnRemoving(entityIndex);
            }

            var versions = _changedVersions;
            if (versions != null) {
                versions[_sparse[entityIndex]] = versions[_count - 1];
            }
        }

        internal override void AddDefault(int entityIndex) {
            Add(entityIndex, default);
        }

        /// <summary>
        /// Remove component. O(1) via swap-remove. No-op if the entity does not have it.
        /// Last dense element moves to the removed slot, keeping the array dense.
        /// A typed listener may remove the same component again from OnRemoved (for example
        /// by destroying its entity): the component is removed once and every listener
        /// still gets exactly one OnRemoved.
        /// </summary>
        public override void Remove(int entityIndex) {
            if (!Has(entityIndex)) {
                return;
            }

            if (_hasHooks) {
                RemoveHooks(entityIndex);
                // A listener removed it re-entrantly; that Remove did all the work.
                if (!Has(entityIndex)) {
                    return;
                }
            }

            int denseIdx = _sparse[entityIndex];
            int lastDenseIdx = _count - 1;

            var autoReset = _autoReset;
            if (autoReset != null) {
                autoReset(ref _denseData[denseIdx]);

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
            if (EcsProfiler.IsEnabled) {
                EcsProfiler.OnComponentRemoved(_world, _world.Tick, entityIndex, TypeName);
            }
#endif
        }

        // Replace an existing component; the write is marked changed. The old value is
        // not reset: CommandBuffer.Set often stores a modified copy of it that still
        // shares its references (an EventBuffer list), and a reset would clear them.
        internal void Overwrite(int entityIndex, T value) {
            Modify(entityIndex) = value;
        }

        internal override void CopyTo(int srcEntityIndex, int dstEntityIndex) {
            if (!Has(srcEntityIndex)) {
                return;
            }

            var value = Get(srcEntityIndex);
            if (_autoCopy != null) {
                _autoCopy(ref value);
            }

            if (Has(dstEntityIndex)) {
                // AutoCopy gave value its own references, so the old ones can be
                // returned; without it the old value may share them with the source.
                ref T slot = ref Modify(dstEntityIndex);
                if (_autoCopy != null && _autoReset != null) {
                    _autoReset(ref slot);
                }
                slot = value;
            } else {
                Add(dstEntityIndex, value);
            }
        }

        // Unmanaged components are written as raw bytes; anything else needs a
        // registered IComponentFormatter<T>.
        private static readonly bool IsBlittable = !RuntimeHelpers.IsReferenceOrContainsReferences<T>();

        internal override void WriteComponents(System.IO.BinaryWriter writer, object formatter) {
            var typed = formatter as IComponentFormatter<T>;
            if (typed == null && !IsBlittable) {
                ThrowNoFormatter();
            }

            var entities = _denseEntities;
            var data = _denseData;
            for (int i = 0; i < _count; i++) {
                writer.Write(entities[i]);
                if (typed != null) {
                    typed.Write(writer, ref data[i]);
                } else {
                    writer.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                        System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref data[i], 1)));
                }
            }
        }

        internal override void ReadComponent(System.IO.BinaryReader reader, int entityIndex, object formatter) {
            var typed = formatter as IComponentFormatter<T>;
            if (typed == null && !IsBlittable) {
                ThrowNoFormatter();
            }

            T value;
            if (typed != null) {
                typed.Read(reader, out value);
            } else {
                value = default;
                var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                    System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref value, 1));
                int read = 0;
                while (read < bytes.Length) {
                    int n = reader.Read(bytes.Slice(read));
                    if (n <= 0) {
                        throw new System.IO.EndOfStreamException($"Snapshot ended inside a {typeof(T).Name} component");
                    }
                    read += n;
                }
            }
            Add(entityIndex, value);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowNoFormatter() {
            throw new InvalidOperationException(
                $"{typeof(T).Name} contains references and cannot be serialized bit-for-bit — register an IComponentFormatter<{typeof(T).Name}> on the WorldSerializer");
        }

        // Does not notify World — only World.Clear calls this, and it resets
        // masks, counts and filters itself.
        internal override void Clear() {
            var entities = _denseEntities;
            for (int i = 0; i < _count; i++) {
                _sparse[entities[i]] = -1;
            }
            if (_autoReset != null) {
                for (int i = 0; i < _count; i++) {
                    _autoReset(ref _denseData[i]);
                }
            }
            Array.Clear(_denseData, 0, _count);
            _count = 0;
        }

#if KENSEI_DEBUG
        /// <inheritdoc/>
        public override object GetRaw(int entityIndex) {
            return Get(entityIndex);
        }

        /// <inheritdoc/>
        public override void SetRaw(int entityIndex, object value) {
            int denseIdx = _sparse[entityIndex];
            _denseData[denseIdx] = (T)value;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowMissing(int entityIndex) {
            throw new InvalidOperationException(
                $"Get<{typeof(T).Name}> on entity {entityIndex} without this component");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowDeadSlot(int entityIndex) {
            throw new InvalidOperationException(
                $"Add<{typeof(T).Name}> on entity slot {entityIndex} that is not alive — an int index from a filter is only valid until the end of the current iteration; store Entity handles instead");
        }
#endif

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowAlreadyHas(int entityIndex) {
            throw new InvalidOperationException(
                $"Entity {entityIndex} already has component {typeof(T).Name}");
        }

        internal override void SwapDense(int denseA, int denseB) {
            if (denseA == denseB) {
                return;
            }

            int entityA = _denseEntities[denseA];
            int entityB = _denseEntities[denseB];
            _denseEntities[denseA] = entityB;
            _denseEntities[denseB] = entityA;

            var data = _denseData;
            var tmp = data[denseA];
            data[denseA] = data[denseB];
            data[denseB] = tmp;

            var versions = _changedVersions;
            if (versions != null) {
                int v = versions[denseA];
                versions[denseA] = versions[denseB];
                versions[denseB] = v;
            }

            _sparse[entityA] = denseB;
            _sparse[entityB] = denseA;
        }

        /// <summary>
        /// Grow the pool so that <paramref name="count"/> components fit without further growth:
        /// the dense entity and data arrays (and the change versions, when tracked) to at least
        /// <paramref name="count"/> slots, and the sparse array to cover entity indices below
        /// <paramref name="count"/>. Never shrinks. For the world's own per-entity storage see
        /// <see cref="World.Reserve"/>. Allocates; call at load time.
        /// </summary>
        /// <param name="count"> Number of components (and entity indices) to have room for. </param>
        /// <exception cref="ArgumentOutOfRangeException"> When <paramref name="count"/> is negative. </exception>
        public void Reserve(int count) {
            if (count < 0) {
                throw new ArgumentOutOfRangeException(nameof(count), count, "Must not be negative");
            }
            ReserveSparse(count);
            ReserveDense(count);
        }

        internal override void ReserveDense(int count) {
            if (count > _denseData.Length) {
                // Exact size: Reserve states the need, doubling would overshoot it.
                Array.Resize(ref _denseEntities, count);
                Array.Resize(ref _denseData, count);
                if (_changedVersions != null) {
                    Array.Resize(ref _changedVersions, count);
                }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void GrowDense(int needed) {
            int newSize = Math.Max(_denseData.Length * 2, needed);
            Array.Resize(ref _denseEntities, newSize);
            Array.Resize(ref _denseData, newSize);
            if (_changedVersions != null) {
                Array.Resize(ref _changedVersions, newSize);
            }
        }
    }
}
