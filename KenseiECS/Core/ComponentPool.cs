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

        // Auto-reset delegate — cached at construction, null if T doesn't implement IAutoReset
        private delegate void AutoResetHandler(ref T component);
        private readonly AutoResetHandler _autoReset;

        // static readonly per generic instantiation — RyuJIT folds it into a constant
        // on tier-1 and eliminates the dead Remove branch entirely.
        private static readonly bool HasAutoReset = typeof(IAutoReset<T>).IsAssignableFrom(typeof(T));

        // Auto-copy delegate — cached at construction, null if T doesn't implement IAutoCopy
        private delegate void AutoCopyHandler(ref T component);
        private readonly AutoCopyHandler _autoCopy;

        // Copy-on-write, null when nobody listens.
        private IComponentListener<T>[] _listeners;

        // Change version per dense slot, null unless TrackChanges() was called.
        private int[] _changedVersions;

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
            if (HasAutoReset) {
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
        /// The ref is valid until the next Add of this component type (the dense array may grow).
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

        /// <summary> Add component. O(1). Returns ref to the added component. Throws if the entity already has it. </summary>
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
                GrowSparse(entityIndex);
            }

            int denseIdx = _count;
            if (denseIdx == _denseData.Length) {
                GrowDense(denseIdx + 1);
            }

            _sparse[entityIndex] = denseIdx;
            _denseEntities[denseIdx] = entityIndex;
            _denseData[denseIdx] = value;
            _count++;

            var versions = _changedVersions;
            if (versions != null) {
                versions[denseIdx] = _world.NextChangeVersion();
            }

            _world.OnComponentAdded(entityIndex, TypeIndex);
#if KENSEI_DEBUG
            EcsProfiler.OnComponentAdded(_world, _world.Tick, entityIndex, typeof(T).Name);
#endif

            var listeners = _listeners;
            if (listeners != null) {
                NotifyAdded(listeners, entityIndex, denseIdx);
            }

            return ref _denseData[denseIdx];
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void NotifyAdded(IComponentListener<T>[] listeners, int entityIndex, int denseIdx) {
            for (int i = 0; i < listeners.Length; i++) {
                listeners[i].OnAdded(entityIndex, ref _denseData[denseIdx]);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void NotifyRemoved(IComponentListener<T>[] listeners, int entityIndex) {
            for (int i = 0; i < listeners.Length; i++) {
                listeners[i].OnRemoved(entityIndex, ref _denseData[_sparse[entityIndex]]);
            }
        }

        internal override void AddDefault(int entityIndex) {
            Add(entityIndex, default);
        }

        /// <summary>
        /// Remove component. O(1) via swap-remove. No-op if the entity does not have it.
        /// Last dense element moves to the removed slot, keeping the array dense.
        /// </summary>
        public override void Remove(int entityIndex) {
            if (!Has(entityIndex)) {
                return;
            }

            // Listeners run before any index is read: they may remove other
            // components of this type and shift the dense layout.
            var listeners = _listeners;
            if (listeners != null) {
                NotifyRemoved(listeners, entityIndex);
            }

            int denseIdx = _sparse[entityIndex];
            int lastDenseIdx = _count - 1;

            var versions = _changedVersions;
            if (versions != null) {
                versions[denseIdx] = versions[lastDenseIdx];
            }

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

        internal override void CopyTo(int srcEntityIndex, int dstEntityIndex) {
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
        public override object GetRaw(int entityIndex) {
            return Get(entityIndex);
        }

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
