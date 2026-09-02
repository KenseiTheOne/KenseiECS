using System;
using System.Runtime.CompilerServices;
#if ENABLE_IL2CPP
using Unity.IL2CPP.CompilerServices;
#endif

namespace KenseiECS {
    /// <summary>
    /// Owning group: keeps the dense arrays of its pools aligned so that every
    /// entity holding all owned components sits at the same dense index in each
    /// pool, packed at the front. Iteration then reads the component arrays
    /// directly — no sparse lookups, one bounds check per span.
    ///
    /// A pool can be owned by one group only. Entities gain and lose membership
    /// as components are added and removed; the group is always exact.
    ///
    /// Usage:
    ///   var group = world.Group<Position, Velocity>();
    ///   var pos = group.Data1;
    ///   var vel = group.Data2;
    ///   for (int i = 0; i < pos.Length; i++) {
    ///       pos[i].X += vel[i].X;
    ///   }
    /// Iterate in reverse when destroying entities or removing owned components
    /// inside the loop: the last member is swapped into the freed slot.
    /// </summary>
#if ENABLE_IL2CPP
    [Il2CppSetOption(Option.NullChecks, false)]
    [Il2CppSetOption(Option.ArrayBoundsChecks, false)]
#endif
    public abstract class Group {
        private readonly ComponentPoolBase[] _pools;
        private int _count;

        /// <summary> Number of entities holding every owned component. </summary>
        public int Count => _count;

        /// <summary> Entity indices of the members, aligned with the data spans. </summary>
        public ReadOnlySpan<int> Entities => new(_pools[0].RawEntities, 0, _count);

        /// <summary> Type indices of the owned components. </summary>
        public int PoolCount => _pools.Length;

        public ComponentPoolBase GetPool(int index) {
            return _pools[index];
        }

        private protected Group(ComponentPoolBase[] pools) {
            _pools = pools;
        }

        internal void Populate() {
            var smallest = _pools[0];
            for (int i = 1; i < _pools.Length; i++) {
                if (_pools[i].Count < smallest.Count) {
                    smallest = _pools[i];
                }
            }

            // Members are swapped to the front as they are found; walking the
            // dense array forward still visits each entity once because a swap
            // only moves an already-visited non-member behind the cursor.
            var entities = smallest.RawEntities;
            for (int i = 0; i < smallest.Count; i++) {
                OnAdded(entities[i]);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool Contains(int entityIndex) {
            var first = _pools[0];
            return first.Has(entityIndex) && first.GetDenseIndex(entityIndex) < _count;
        }

        // Called by an owned pool right after it stored a component for the entity.
        internal void OnAdded(int entityIndex) {
            var pools = _pools;
            for (int i = 0; i < pools.Length; i++) {
                if (!pools[i].Has(entityIndex)) {
                    return;
                }
            }
            if (pools[0].GetDenseIndex(entityIndex) < _count) {
                return;
            }

            int slot = _count++;
            for (int i = 0; i < pools.Length; i++) {
                var pool = pools[i];
                pool.SwapDense(pool.GetDenseIndex(entityIndex), slot);
            }
        }

        // Called by an owned pool before it swap-removes the entity's component,
        // while the entity still holds every owned component.
        internal void OnRemoving(int entityIndex) {
            if (!Contains(entityIndex)) {
                return;
            }

            int last = --_count;
            var pools = _pools;
            for (int i = 0; i < pools.Length; i++) {
                var pool = pools[i];
                pool.SwapDense(pool.GetDenseIndex(entityIndex), last);
            }
        }

        internal void Clear() {
            _count = 0;
        }

        internal bool Owns(ComponentPoolBase pool) {
            return Array.IndexOf(_pools, pool) >= 0;
        }

        internal bool OwnsExactly(ComponentPoolBase[] pools) {
            if (pools.Length != _pools.Length) {
                return false;
            }
            for (int i = 0; i < pools.Length; i++) {
                if (_pools[i] != pools[i]) {
                    return false;
                }
            }
            return true;
        }
    }

    public sealed class Group<T1, T2> : Group
        where T1 : struct, IComponent
        where T2 : struct, IComponent {
        private readonly ComponentPool<T1> _p1;
        private readonly ComponentPool<T2> _p2;

        internal Group(ComponentPool<T1> p1, ComponentPool<T2> p2) : base(new ComponentPoolBase[] { p1, p2 }) {
            _p1 = p1;
            _p2 = p2;
        }

        public Span<T1> Data1 => new(_p1.RawData, 0, Count);
        public Span<T2> Data2 => new(_p2.RawData, 0, Count);
    }

    public sealed class Group<T1, T2, T3> : Group
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent {
        private readonly ComponentPool<T1> _p1;
        private readonly ComponentPool<T2> _p2;
        private readonly ComponentPool<T3> _p3;

        internal Group(ComponentPool<T1> p1, ComponentPool<T2> p2, ComponentPool<T3> p3) : base(new ComponentPoolBase[] { p1, p2, p3 }) {
            _p1 = p1;
            _p2 = p2;
            _p3 = p3;
        }

        public Span<T1> Data1 => new(_p1.RawData, 0, Count);
        public Span<T2> Data2 => new(_p2.RawData, 0, Count);
        public Span<T3> Data3 => new(_p3.RawData, 0, Count);
    }

    public sealed class Group<T1, T2, T3, T4> : Group
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent {
        private readonly ComponentPool<T1> _p1;
        private readonly ComponentPool<T2> _p2;
        private readonly ComponentPool<T3> _p3;
        private readonly ComponentPool<T4> _p4;

        internal Group(ComponentPool<T1> p1, ComponentPool<T2> p2, ComponentPool<T3> p3, ComponentPool<T4> p4) : base(new ComponentPoolBase[] { p1, p2, p3, p4 }) {
            _p1 = p1;
            _p2 = p2;
            _p3 = p3;
            _p4 = p4;
        }

        public Span<T1> Data1 => new(_p1.RawData, 0, Count);
        public Span<T2> Data2 => new(_p2.RawData, 0, Count);
        public Span<T3> Data3 => new(_p3.RawData, 0, Count);
        public Span<T4> Data4 => new(_p4.RawData, 0, Count);
    }
}
