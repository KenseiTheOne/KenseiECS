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

        // Ownership of a pooled list (EventBuffer<T>), null for every other type. Every store of a
        // value goes through it, so a copy that outlived its component cannot put a list that went
        // back to the pool, or that another entity holds, on this entity.
        private readonly PooledListOwner<T> _listOwner;

        // Copy-on-write, null when nobody listens.
        private IComponentListener<T>[] _listeners;

        // Change version per dense slot, null unless TrackChanges() was called.
        private int[] _changedVersions;

        // Add/Remove take the hooked path: listeners, owning group or change tracking present, plus a
        // list owner (Add only: Return releases the list on Remove) or a running frame (Remove only:
        // the frames must see every removal). One flag per direction keeps the common Add/Remove
        // path at a single extra branch.
        private bool _addHooks;
        private bool _removeHooks;

        // An Add or Remove of this pool whose typed listeners are running. Calls nested in those
        // listeners look here: a Remove of a component whose removal is in progress continues that
        // notification instead of starting a second one, and the removal of a component marks every
        // frame of its entity Done. A call whose frame is Done stops, so a component added to the slot
        // again (by the same entity or by a new entity in the reused slot) is never taken for its own.
        private struct HookFrame {
            public int Entity;
            public bool Removing;
            public bool Done;
            // Removing: the next listener to notify, and the listener array as the removal started
            // when it has been replaced since (null while _listeners is still that array), so a
            // listener that unsubscribes during the removal does not shift the others.
            public int Next;
            public IComponentListener<T>[] Listeners;
        }

        // The innermost running frame lives in these fields, so a call that does not nest touches no
        // array; a nested call moves it to _frames and back. _frames holds the frames below it,
        // outermost first, and is allocated with the first listener. While any frame runs
        // _removeHooks stays set, so every nested Remove of this pool takes the hooked path and keeps
        // the frames exact.
        private int _topEntity;
        private int _topNext;
        private bool _topRemoving;
        private bool _topDone;
        private IComponentListener<T>[] _topListeners;
        private HookFrame[] _frames;
        private int _frameCount;

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
            _addHooks = true;
            _removeHooks = true;
        }

        internal override void SetOwnerGroup(Group group) {
            _ownerGroup = group;
            RefreshHooks();
        }

        private void RefreshHooks() {
            bool hooks = _listeners != null || _ownerGroup != null || _changedVersions != null;
            _addHooks = hooks || _listOwner != null;
            _removeHooks = hooks || _frameCount > 0;
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
            KeepListenersOfRunningRemovals();
            _listeners = grown;
            _frames ??= new HookFrame[4];
            _addHooks = true;
            _removeHooks = true;
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
            KeepListenersOfRunningRemovals();
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

        // _listeners is about to be replaced: a removal in progress keeps notifying the array it started with.
        private void KeepListenersOfRunningRemovals() {
            if (_frameCount == 0) {
                return;
            }
            if (_topRemoving && _topListeners == null) {
                _topListeners = _listeners;
            }
            for (int i = 0; i < _frameCount - 1; i++) {
                if (_frames[i].Removing && _frames[i].Listeners == null) {
                    _frames[i].Listeners = _listeners;
                }
            }
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

            // The component names its owner class (a closed generic, so AOT compiles it with the type).
            if (typeof(IPooledListComponent<T>).IsAssignableFrom(typeof(T))) {
                _listOwner = ((IPooledListComponent<T>)(object)default(T)).CreateListOwner();
                _addHooks = true;
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
        /// The ref has the same validity as Get. If a listener removes the component again or destroys
        /// the entity before Add returns, the ref points at a scratch value and writes through it are
        /// discarded, even when a new entity has taken the slot; if a listener removed the component
        /// and added it to the same entity again, the ref points at that new component.
        /// </summary>
        public ref T Add(int entityIndex, T value) {
#if KENSEI_DEBUG
            if (!_world.IsSlotAcceptingComponents(entityIndex)) {
                ThrowDeadSlot(entityIndex);
            }
#endif
            // Has and the growth check share one length test: an index inside the sparse array is
            // checked for the component, one past it grows the array (and cannot have it).
            var sparse = _sparse;
            if (entityIndex < sparse.Length) {
                if (sparse[entityIndex] != -1) {
                    ThrowAlreadyHas(entityIndex);
                }
            } else {
#if KENSEI_DEBUG
                // ReportGrowth builds the type name only for a growth it reports: built before the
                // check, every pool would pay for its name on its first growth.
                ReportGrowth("pool sparse", WatchedSparse(), sparse.Length, Math.Max(sparse.Length * 2, entityIndex + 1));
#endif
                GrowSparse(entityIndex);
                sparse = _sparse;
            }

            int denseIdx = _count;
            var data = _denseData;
            if (denseIdx == data.Length) {
#if KENSEI_DEBUG
                ReportGrowth("pool dense", WatchedDense(), data.Length, Math.Max(data.Length * 2, denseIdx + 1));
#endif
                GrowDense(denseIdx + 1);
                data = _denseData;
            }

            sparse[entityIndex] = denseIdx;
            _denseEntities[denseIdx] = entityIndex;
            ref T slot = ref data[denseIdx];
            slot = value;
            _count = denseIdx + 1;

            if (_addHooks) {
                return ref AddHooked(entityIndex, denseIdx);
            }

            // Filter and world listeners run inside OnComponentAdded. When one ran, the component
            // may have moved or been removed, or the entity destroyed and its slot taken by another.
            int added = _world.OnComponentAdded(entityIndex, _typeIndex);
#if KENSEI_DEBUG
            if (EcsProfiler.IsEnabled) {
                EcsProfiler.OnComponentAdded(_world, _world.Tick, entityIndex, TypeName);
            }
#endif
            if (added == World.AddedQuietly) {
                // No callback ran: the component is in the slot it was written to, in the same array.
                return ref slot;
            }
            return ref RefAfterWorldListeners(entityIndex, added);
        }

        // The entity's component after filter or world listeners ran, or the scratch value when
        // they removed it or destroyed the entity.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private ref T RefAfterWorldListeners(int entityIndex, int added) {
            int denseIdx = _sparse[entityIndex];
            if (added == World.AddedLost || denseIdx < 0) {
                _orphan = default;
                return ref _orphan;
            }
            return ref _denseData[denseIdx];
        }

        // The entity's component once callbacks ran, or the scratch value when the component is gone
        // or the slot holds another entity now (generation read when Add stored the component).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ref T RefAfterListeners(int entityIndex, int generation) {
            int denseIdx = _sparse[entityIndex];
            if (denseIdx < 0 || _world._generations[entityIndex] != generation) {
                _orphan = default;
                return ref _orphan;
            }
            return ref _denseData[denseIdx];
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private ref T AddHooked(int entityIndex, int denseIdx) {
            var listOwner = _listOwner;
            if (listOwner != null) {
                listOwner.Adopt(ref _denseData[denseIdx], PoolId, entityIndex);
            }

            var versions = _changedVersions;
            if (versions != null) {
                versions[denseIdx] = _world.NextChangeVersion();
            }

            var group = _ownerGroup;
            if (group != null) {
                group.OnAdded(entityIndex);
            }

            // Without typed listeners when the Add starts no frame runs, and none is notified of this
            // Add: one that a filter or world listener subscribes during it starts with the next Add.
            var listeners = _listeners;
            if (listeners == null) {
                int added = _world.OnComponentAdded(entityIndex, _typeIndex);
#if KENSEI_DEBUG
                if (EcsProfiler.IsEnabled) {
                    EcsProfiler.OnComponentAdded(_world, _world.Tick, entityIndex, TypeName);
                }
#endif
                if (added == World.AddedQuietly) {
                    // The group swap may have moved it before.
                    return ref _denseData[_sparse[entityIndex]];
                }
                return ref RefAfterWorldListeners(entityIndex, added);
            }

            // Typed listeners run after the world update; telling a component re-added to this
            // entity from one of a new entity in the slot takes the generation from before them.
            int generation = _world._generations[entityIndex];

            // Every callback may move the component or remove it again (then the remaining listeners
            // get no OnAdded): the dense index is re-read before each typed listener. A component added
            // to the slot again is a new one, whose own Add notifies every listener, so the frame stops
            // the loop as soon as this one is removed. Nested calls run their own frames and leave this
            // one on top again before they return.
            int depth = PushFrame(entityIndex, false);
            try {
                _world.OnComponentAdded(entityIndex, _typeIndex);
#if KENSEI_DEBUG
                if (EcsProfiler.IsEnabled) {
                    EcsProfiler.OnComponentAdded(_world, _world.Tick, entityIndex, TypeName);
                }
#endif
                // The OnAdded dispatch starts here, after the filter and world listeners: one of them
                // may have subscribed or unsubscribed typed listeners, the last one included.
                listeners = _listeners;
                if (listeners != null) {
                    for (int i = 0; i < listeners.Length; i++) {
                        denseIdx = _sparse[entityIndex];
                        if (denseIdx < 0 || _topDone) {
                            break;
                        }
                        listeners[i].OnAdded(entityIndex, ref _denseData[denseIdx]);
                    }
                }
            } finally {
                PopFrame(depth);
            }
            return ref RefAfterListeners(entityIndex, generation);
        }

        // Remove on a pool with hooks. Without typed listeners (or running frames) no user code runs
        // before the swap-remove: only the group swap and the version move come first.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void RemoveHooked(int entityIndex) {
            if (_listeners != null || _frameCount != 0) {
                RemoveNotified(entityIndex);
                return;
            }

            var group = _ownerGroup;
            if (group != null) {
                group.OnRemoving(entityIndex);
            }

            var versions = _changedVersions;
            if (versions != null) {
                versions[_sparse[entityIndex]] = versions[_count - 1];
            }

            RemoveFromDense(entityIndex);
            _world.OnComponentRemoved(entityIndex, _typeIndex);
#if KENSEI_DEBUG
            if (EcsProfiler.IsEnabled) {
                EcsProfiler.OnComponentRemoved(_world, _world.Tick, entityIndex, TypeName);
            }
#endif
        }

        // Listeners run before any index is read: they may remove other components of this type and
        // shift the dense layout. The group swap and the version move follow, right before the
        // swap-remove itself (FinishRemove).
        // A listener may remove this very component again, directly or by destroying the entity, also
        // while another component of this type is being removed (two linked entities removing each
        // other): the nested Remove finds the removal in progress (JoinRemoval), continues its
        // notification from the next listener and removes the component, and the outer Remove sees
        // its frame Done and stops. Every listener gets one OnRemoved per component, and a cycle of
        // removals ends.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void RemoveNotified(int entityIndex) {
            if (_frameCount != 0 && JoinRemoval(entityIndex)) {
                return;
            }

            var listeners = _listeners;
            if (listeners == null) {
                // The last listener unsubscribed while frames run: no one to notify, but the
                // frames of this entity must still see the removal.
                FinishRemove(entityIndex);
                return;
            }

            int depth = PushFrame(entityIndex, true);
            try {
                for (int i = 0; i < listeners.Length; i++) {
                    // Nested calls leave this frame on top again before they return. One that joins
                    // the removal continues from Next and finishes it, so while the frame is not Done
                    // this loop's i is the frame's position.
                    _topNext = i + 1;
                    listeners[i].OnRemoved(entityIndex, ref _denseData[_sparse[entityIndex]]);
                    // Has is a guard only: while the frame is not Done the component is still here.
                    if (_topDone || !Has(entityIndex)) {
                        return;
                    }
                }
                FinishRemove(entityIndex);
            } finally {
                PopFrame(depth);
            }
        }

        // A Remove nested in a removal of the same component: continue that notification from its
        // next listener, with the listener array it started with, then remove the component. False
        // when no removal of entityIndex's component is in progress.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool JoinRemoval(int entityIndex) {
            if (_topRemoving && !_topDone && _topEntity == entityIndex) {
                // The innermost frame: it is on top again after every nested call returns.
                var listeners = _topListeners ?? _listeners;
                while (true) {
                    int i = _topNext;
                    if (i >= listeners.Length) {
                        break;
                    }
                    _topNext = i + 1;
                    listeners[i].OnRemoved(entityIndex, ref _denseData[_sparse[entityIndex]]);
                    if (_topDone || !Has(entityIndex)) {
                        return true;
                    }
                }
                FinishRemove(entityIndex);
                return true;
            }

            int frame = _frameCount - 2;
            while (frame >= 0 && (_frames[frame].Entity != entityIndex || !_frames[frame].Removing || _frames[frame].Done)) {
                frame--;
            }
            if (frame < 0) {
                return false;
            }

            // A frame below the top stays at its index in _frames; the array is re-read after every
            // callback, a nested call may have grown it.
            var frameListeners = _frames[frame].Listeners ?? _listeners;
            while (true) {
                int i = _frames[frame].Next;
                if (i >= frameListeners.Length) {
                    break;
                }
                _frames[frame].Next = i + 1;
                frameListeners[i].OnRemoved(entityIndex, ref _denseData[_sparse[entityIndex]]);
                if (_frames[frame].Done || !Has(entityIndex)) {
                    return true;
                }
            }
            FinishRemove(entityIndex);
            return true;
        }

        // Group swap, version move, swap-remove and world update of a Remove on a hooked pool.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void FinishRemove(int entityIndex) {
            var group = _ownerGroup;
            if (group != null) {
                group.OnRemoving(entityIndex);
            }

            var versions = _changedVersions;
            if (versions != null) {
                versions[_sparse[entityIndex]] = versions[_count - 1];
            }

            RemoveFromDense(entityIndex);
            if (_frameCount != 0) {
                MarkRemoved(entityIndex);
            }

            _world.OnComponentRemoved(entityIndex, _typeIndex);
#if KENSEI_DEBUG
            if (EcsProfiler.IsEnabled) {
                EcsProfiler.OnComponentRemoved(_world, _world.Tick, entityIndex, TypeName);
            }
#endif
        }

        // The entity's component left the pool: every Add or Remove running for it is over.
        // Called only while frames run.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void MarkRemoved(int entityIndex) {
            if (_topEntity == entityIndex) {
                _topDone = true;
            }
            for (int i = _frameCount - 2; i >= 0; i--) {
                if (_frames[i].Entity == entityIndex) {
                    _frames[i].Done = true;
                }
            }
        }

        // Make a new innermost frame; the current one, if any, moves to _frames. Returns the new
        // frame's depth, which PopFrame takes back.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int PushFrame(int entityIndex, bool removing) {
            int depth = _frameCount;
            if (depth != 0) {
                SpillTop(depth - 1);
            }
            _topEntity = entityIndex;
            _topRemoving = removing;
            _topDone = false;
            _topNext = 0;
            _frameCount = depth + 1;
            return depth;
        }

        // The listener arrays of the frames are written only when set: on IL2CPP every store into a
        // reference field calls the GC write barrier, a store of null too, and these run on every hooked
        // Add and Remove. _topListeners and the Listeners of the slots in _frames above the running
        // frames are null unless the listener array was replaced while a removal ran.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void PopFrame(int depth) {
            _frameCount = depth;
            if (depth != 0) {
                RestoreTop(depth - 1);
                return;
            }
            if (_topListeners != null) {
                _topListeners = null;
            }
            // A listener may have removed the last listener: _removeHooks stayed set while the frames ran.
            if (_listeners == null) {
                RefreshHooks();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void SpillTop(int index) {
            if (_frames == null) {
                _frames = new HookFrame[4];
            } else if (index == _frames.Length) {
                Array.Resize(ref _frames, _frames.Length * 2);
            }
            ref var frame = ref _frames[index];
            frame.Entity = _topEntity;
            frame.Removing = _topRemoving;
            frame.Done = _topDone;
            frame.Next = _topNext;
            // The slot's Listeners is null (RestoreTop clears it).
            var listeners = _topListeners;
            if (listeners != null) {
                frame.Listeners = listeners;
                _topListeners = null;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void RestoreTop(int index) {
            ref var frame = ref _frames[index];
            _topEntity = frame.Entity;
            _topRemoving = frame.Removing;
            _topDone = frame.Done;
            _topNext = frame.Next;
            var listeners = frame.Listeners;
            if (listeners != null) {
                frame.Listeners = null;
            }
            if (!ReferenceEquals(_topListeners, listeners)) {
                _topListeners = listeners;
            }
        }

        internal override void AddDefault(int entityIndex) {
            Add(entityIndex, default);
        }

        /// <summary>
        /// Remove component. O(1) via swap-remove. No-op if the entity does not have it.
        /// Last dense element moves to the removed slot, keeping the array dense.
        /// A typed listener may remove the same component again from OnRemoved (for example
        /// by destroying its entity), also while removing another component of this type
        /// (linked entities that remove each other): the component is removed once and every
        /// listener still gets exactly one OnRemoved for it. A component added to the entity
        /// again from OnRemoved, or to a new entity that took the slot, is a new component:
        /// this Remove does not touch it.
        /// </summary>
        public override void Remove(int entityIndex) {
            // Has, with the dense index it reads kept for the swap-remove.
            var sparse = _sparse;
            if (entityIndex >= sparse.Length) {
                return;
            }
            int denseIdx = sparse[entityIndex];
            if (denseIdx == -1) {
                return;
            }

            // Without hooks no frames run (they keep _removeHooks set), so no nested call can be
            // waiting for this component and the plain swap-remove needs no bookkeeping.
            if (_removeHooks) {
                RemoveHooked(entityIndex);
                return;
            }

            // The same AutoReset and swap-remove as RemoveFromDense, written out: the JIT may not
            // inline a helper here once Remove itself is inlined into a caller. The arrays are read
            // once into locals (after AutoReset, the only call before the swap-remove).
            int lastDenseIdx = _count - 1;

            var autoReset = _autoReset;
            if (autoReset != null) {
                autoReset(ref _denseData[denseIdx]);
                sparse = _sparse;
            }

            var data = _denseData;
            if (denseIdx != lastDenseIdx) {
                var entities = _denseEntities;
                int lastEntity = entities[lastDenseIdx];

                entities[denseIdx] = lastEntity;
                data[denseIdx] = data[lastDenseIdx];
                sparse[lastEntity] = denseIdx;
            }

            // See RemoveFromDense: the freed tail slot gets plain default, not AutoReset.
            data[lastDenseIdx] = default;
            sparse[entityIndex] = -1;
            _count = lastDenseIdx;

            _world.OnComponentRemoved(entityIndex, _typeIndex);
#if KENSEI_DEBUG
            if (EcsProfiler.IsEnabled) {
                EcsProfiler.OnComponentRemoved(_world, _world.Tick, entityIndex, TypeName);
            }
#endif
        }

        // OneFrame cleanup (OneFrameCleanup<T>, DelHere): Remove of every component, from the last dense
        // slot down to the first, over the entity array and the count taken at the start.
        // When no callback can run during the removals (no hooks on this pool, no world listener, no
        // filter of this type with listeners), nothing else changes the pool, so each component to
        // remove is the last one: it is removed in place, without the lookup, the hook check and the
        // swap of Remove. Otherwise each component goes through Remove, which skips one a callback
        // has removed already.
        // AutoReset is the one user code the in-place path runs. It runs as in Remove: the indices are
        // read before it (the component's dense slot is the last one, as Remove would find), the
        // arrays after it. A reset that changes the world (an AutoReset should only reset the value)
        // is caught after its removal: when the pool's count or entity array moved, or a listener
        // was subscribed, the rest goes through Remove as the plain loop would.
        internal void RemoveAll() {
            int count = _count;
            if (count == 0) {
                return;
            }
            var entities = _denseEntities;
            if (_removeHooks || !_world.RemovesQuietly(_typeIndex)) {
                RemoveEach(entities, count - 1);
                return;
            }

            var world = _world;
            int typeIndex = _typeIndex;
            var autoReset = _autoReset;
            var sparse = _sparse;
            var data = _denseData;
            for (int i = count - 1; i >= 0; i--) {
                int entityIndex = entities[i];
                if (autoReset != null) {
                    autoReset(ref data[i]);
                    sparse = _sparse;
                    data = _denseData;
                }
                data[i] = default;
                sparse[entityIndex] = -1;
                _count = i;
                world.OnComponentRemoved(entityIndex, typeIndex);
#if KENSEI_DEBUG
                if (EcsProfiler.IsEnabled) {
                    EcsProfiler.OnComponentRemoved(world, world.Tick, entityIndex, TypeName);
                }
#endif
                if (autoReset != null
                    && (_count != i || _denseEntities != entities || _removeHooks || !world.RemovesQuietly(typeIndex))) {
                    RemoveEach(entities, i - 1);
                    return;
                }
            }
        }

        // The plain loop of RemoveAll from dense slot `from` down, over the entity array it started with.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void RemoveEach(int[] entities, int from) {
            for (int i = from; i >= 0; i--) {
                Remove(entities[i]);
            }
        }

        // AutoReset and swap-remove of the hooked paths: the last component moves into the freed
        // dense slot. Remove has its own copy for the plain path.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void RemoveFromDense(int entityIndex) {
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
        }

        // Replace an existing component; the write is marked changed. The old value is
        // not reset: CommandBuffer.Set often stores a modified copy of it that still
        // shares its references (an EventBuffer list), and a reset would clear them.
        // A pooled list is the exception (_listOwner): a copy holding the entity's own list
        // keeps it, a value with a new list of its own returns the old one to the pool, and a
        // stale copy (its list back in the pool or on another entity) leaves the component as it is.
        internal void Overwrite(int entityIndex, T value) {
            ref T slot = ref Modify(entityIndex);
            var listOwner = _listOwner;
            if (listOwner != null) {
                listOwner.Replace(ref slot, ref value, PoolId, entityIndex);
            }
            slot = value;
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
                var listOwner = _listOwner;
                if (listOwner != null) {
                    listOwner.Adopt(ref slot, PoolId, dstEntityIndex);
                }
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
            // World.Clear from a listener: the Add or Remove that called it must not go on.
            if (_frameCount != 0) {
                _topDone = true;
                for (int i = 0; i < _frameCount - 1; i++) {
                    _frames[i].Done = true;
                }
            }
        }

#if KENSEI_DEBUG
        /// <inheritdoc/>
        public override object GetRaw(int entityIndex) {
            return Get(entityIndex);
        }

        /// <inheritdoc/>
        public override void SetRaw(int entityIndex, object value) {
            int denseIdx = _sparse[entityIndex];
            var typed = (T)value;
            var listOwner = _listOwner;
            if (listOwner != null) {
                listOwner.Replace(ref _denseData[denseIdx], ref typed, PoolId, entityIndex);
            }
            _denseData[denseIdx] = typed;
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

#if KENSEI_DEBUG
        // A growth past what a reserve stated is counted; one that nothing reserved is only logged while
        // the profiler records. Both build the type name, so it is built only then.
        private void ReportGrowth(string array, int watched, int oldSize, int newSize) {
            if (watched != 0) {
                _world.OnStorageGrown(TypeName, array, oldSize, newSize, watched);
            } else if (_world.LogsUnreservedGrowth) {
                _world.OnUnreservedGrowth(TypeName, array, oldSize, newSize);
            }
        }
#endif

        internal override void ReserveDense(int count) {
            if (count > _denseData.Length) {
                // Exact size: Reserve states the need, doubling would overshoot it.
                ResizeDense(count);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void GrowDense(int needed) {
            ResizeDense(Math.Max(_denseData.Length * 2, needed));
        }

        // Every array is allocated before any is replaced: running out of memory leaves the
        // parallel dense arrays at their old, equal sizes instead of some grown and some not.
        private void ResizeDense(int newSize) {
            var entities = new int[newSize];
            var data = new T[newSize];
            var versions = _changedVersions != null ? new int[newSize] : null;
            Array.Copy(_denseEntities, entities, _denseEntities.Length);
            Array.Copy(_denseData, data, _denseData.Length);
            if (versions != null) {
                Array.Copy(_changedVersions, versions, _changedVersions.Length);
                _changedVersions = versions;
            }
            _denseEntities = entities;
            _denseData = data;
        }
    }
}
