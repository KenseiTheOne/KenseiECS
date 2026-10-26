using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace KenseiECS {
    /// <summary>
    /// Component holding several events of type T for one entity within a frame,
    /// for cases where a plain OneFrame component (one per entity) is not enough.
    /// Lists are pooled: AutoReset returns the list on remove (OneFrame cleanup,
    /// DestroyEntity, World.Clear), so once the pool holds as many lists as are alive
    /// at the peak, a OneFrame&lt;EventBuffer&lt;T&gt;&gt; registration allocates nothing.
    /// World.Warmup does not fill this pool: it adds buffers without lists; call
    /// <see cref="Prewarm"/> for that. The pool is static, one per event type, and
    /// shared by all worlds in the process.
    /// CopyEntity gives the copy its own list (IAutoCopy).
    ///
    /// A copy of the struct (<c>var b = pool.Get(e)</c>) shares the entity's list: <see cref="Add"/> on it
    /// changes the entity's buffer at once, and once the component is removed (OneFrame cleanup, DelHere,
    /// Remove, DestroyEntity, Clear) the copy is stale: its list is back in the pool and may already belong
    /// to another entity's buffer, so do not use it any more. A pooled list is held by
    /// one entity at most: storing a value through the pool (<c>Add</c>, <c>CommandBuffer.Add</c>/<c>Set</c>/<c>CreateEntity</c>,
    /// a snapshot load) checks it, and a value whose list went back to the pool or belongs to another entity
    /// is stored with an empty list of its own instead (an empty buffer, <see cref="Values"/> not null), or,
    /// when it replaces the entity's buffer, leaves that buffer as it is. <c>world.AddEvent</c> records the
    /// entity as the holder of every list it rents, also for a buffer the entity already has without a list.
    /// Every rent of a pooled list is numbered and the renting buffer keeps the number, so a new buffer
    /// filled before it is stored (<c>new EventBuffer&lt;T&gt;()</c>, then <see cref="Add"/>, for a
    /// <c>CommandBuffer.Add</c>) keeps its list when a stale copy holding the same list is stored first.
    /// Writing a copy directly into the pool's storage (through a <c>ref</c> or <c>RawData</c>) bypasses the
    /// check, and so does <c>pool.Get(e).Add(v)</c> on a buffer without a list (<see cref="Values"/> null): the
    /// list it rents has no recorded holder, and a copy of that buffer can give it to a second entity. Use
    /// <c>world.AddEvent</c> to add events.
    ///
    /// Usage:
    /// <code>
    ///   world.AddEvent(entity, new DamageEvent { Value = 10 });
    ///   world.AddEvent(entity, new DamageEvent { Value = 5 });
    ///
    ///   foreach (int e in _damaged) {
    ///       var events = _buffers.Get(e).Values;
    ///       for (int i = 0; i &lt; events.Count; i++) { ... }
    ///   }
    ///
    ///   systems.OneFrame&lt;EventBuffer&lt;DamageEvent&gt;&gt;();
    /// </code>
    /// </summary>
    public struct EventBuffer<T> : IComponent, IAutoReset<EventBuffer<T>>, IAutoCopy<EventBuffer<T>>, IPooledListComponent<EventBuffer<T>>
        where T : struct {
        /// <summary>
        /// Recorded events, in insertion order. Null until the first Add and after AutoReset, so also in a
        /// buffer added without events (<c>new EventBuffer&lt;T&gt;()</c>, <c>CommandBuffer.Set(e, default)</c>);
        /// <see cref="Count"/> is 0 then.
        /// </summary>
        public List<T> Values;

        // The lease under which this buffer rented Values from the pool (PooledList<T>.Lease). A copy
        // keeps it, so a stale copy, whose list went back to the pool and was rented again, is told
        // apart from the buffer that rented it now, also before that buffer is stored.
        internal int _lease;

        /// <summary> Number of recorded events; 0 when no list is rented. </summary>
        public int Count => Values?.Count ?? 0;

        /// <summary> Append an event. Rents a pooled list on first use. </summary>
        public void Add(T value) {
            if (Values == null) {
                var list = ListPool<T>.Rent();
                Values = list;
                _lease = list.Lease;
            }
            Values.Add(value);
        }

        /// <summary> Clear the list and return it to the pool, leaving <see cref="Values"/> null. </summary>
        public void AutoReset(ref EventBuffer<T> c) {
            if (c.Values != null) {
                ListPool<T>.Return(c.Values);
                c.Values = null;
            }
        }

        /// <summary>
        /// Fill the list pool for event type <typeparamref name="T"/> ahead of gameplay, so that
        /// up to <paramref name="lists"/> entities can hold a buffer of up to <paramref name="capacity"/>
        /// events each without allocating. Afterwards the next <paramref name="lists"/> lists the pool
        /// hands out have Capacity of at least <paramref name="capacity"/>: free lists that are already
        /// large enough are used first, then the largest remaining free lists are grown, and new lists
        /// are created only when the pool has fewer than <paramref name="lists"/> free lists. At most
        /// <paramref name="lists"/> lists are grown, so a pool that once peaked at many more free lists
        /// is not inflated. Lists currently rented by a buffer are not counted or touched, so call it
        /// while no buffers of this type exist (at load, before the first frame).
        /// </summary>
        /// <remarks>
        /// The pool is static: one per event type, shared by every world in the process, so one call
        /// covers all worlds, and lists prewarmed for one world can be taken by another. Like the rest
        /// of the pool it is not thread-safe. <see cref="World.Warmup"/> does not fill it.
        /// The call allocates the missing lists and their storage; repeating it with the same
        /// arguments allocates nothing. Choosing the lists takes O(<paramref name="lists"/> × free lists)
        /// time in the worst case (each prepared slot may search the free lists below it), which is fine
        /// at load time but not for a pool of many thousands of free lists every frame.
        /// </remarks>
        /// <param name="lists"> Minimum number of free lists in the pool. </param>
        /// <param name="capacity"> Minimum Capacity of each of those lists. </param>
        /// <exception cref="ArgumentOutOfRangeException"> When an argument is negative. </exception>
        public static void Prewarm(int lists, int capacity) {
            if (lists < 0) {
                throw new ArgumentOutOfRangeException(nameof(lists), lists, "Must not be negative");
            }
            if (capacity < 0) {
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Must not be negative");
            }
            ListPool<T>.Prewarm(lists, capacity);
        }

        /// <summary> Give the copy its own pooled list with the same events. </summary>
        public void AutoCopy(ref EventBuffer<T> c) {
            if (c.Values != null) {
                var copy = ListPool<T>.Rent();
                copy.AddRange(c.Values);
                c.Values = copy;
                c._lease = copy.Lease;
            }
        }

        PooledListOwner<EventBuffer<T>> IPooledListComponent<EventBuffer<T>>.CreateListOwner() {
            return new EventBufferListOwner<T>();
        }
    }

    // The list owner of EventBuffer<T> pools (see PooledListOwner<T>).
    internal sealed class EventBufferListOwner<T> : PooledListOwner<EventBuffer<T>> where T : struct {
        internal override void Adopt(ref EventBuffer<T> value, int pool, int entityIndex) {
            // A stale copy: its events went back to the pool with the list, or are another entity's. The
            // entity gets an empty list of its own, so a reader of Values sees no events instead of null.
            if (value.Values is PooledList<T> list && !list.TryAdopt(pool, entityIndex, value._lease)) {
                var own = ListPool<T>.Rent(pool, entityIndex);
                value.Values = own;
                value._lease = own.Lease;
            }
        }

        internal override void Replace(ref EventBuffer<T> current, ref EventBuffer<T> value, int pool, int entityIndex) {
            // A modified copy of the entity's own buffer (the same list, rented under the same lease)
            // needs nothing. A copy from an earlier lease of that list is stale, but its list holds the
            // entity's events: the entity keeps its buffer as it is.
            if (ReferenceEquals(value.Values, current.Values)) {
                if (value._lease != current._lease) {
                    value = current;
                }
                return;
            }
            if (value.Values is PooledList<T> list && !list.TryAdopt(pool, entityIndex, value._lease)) {
                // A stale copy: keep the entity's buffer and its events.
                value = current;
                return;
            }
            // The value brings a list of its own (or none): the entity's list goes back to the pool.
            if (current.Values != null) {
                ListPool<T>.Return(current.Values);
                current.Values = null;
            }
        }
    }

    // Implemented by a component whose value holds a list rented from ListPool<T> (EventBuffer<T>): its
    // pool asks it once for the list owner.
    internal interface IPooledListComponent<T> where T : struct {
        PooledListOwner<T> CreateListOwner();
    }

    // Kept by the pool of such a component, which calls it whenever it stores a value for an entity, so a
    // pooled list is held by one entity at most. Pools are named by ComponentPoolBase.PoolId. An abstract
    // class rather than an interface on the component: a virtual call is cheaper than an interface call on
    // a boxed struct, on IL2CPP above all.
    internal abstract class PooledListOwner<T> where T : struct {
        // value is about to be stored for entityIndex in pool: a list that is not value's to give is
        // replaced with a new empty one.
        internal abstract void Adopt(ref T value, int pool, int entityIndex);

        // value is about to replace current, the entity's stored value: keep current when value is stale,
        // return current's list to the pool when value brings another one.
        internal abstract void Replace(ref T current, ref T value, int pool, int entityIndex);
    }

    // A list handed out by ListPool<T>. It knows whether it is in the pool and which entity's
    // component holds it, so a struct copy of an EventBuffer<T> that outlived its component cannot
    // give the list to a second entity, and a list cannot be returned twice. The holder is a pool id and
    // an entity index, not a reference: on IL2CPP every store of a reference field pays a GC write
    // barrier, and these fields change on every rent and return.
    internal sealed class PooledList<T> : List<T> {
        internal bool InPool;
        // ComponentPoolBase.PoolId of the holder's pool; 0 while no entity holds the list.
        internal int OwnerPool;
        internal int OwnerEntity;
        // Incremented by every Rent. The buffer that rents the list records it (EventBuffer<T>._lease):
        // a copy of a buffer that held the list under an earlier lease is stale, even while the list
        // has no holder yet (rented by EventBuffer.Add for a buffer that is not stored yet).
        internal int Lease;

        public PooledList() {
        }

        public PooledList(int capacity) : base(capacity) {
        }

        // Claim the list for the entity's component, carried by a value that rented it under lease.
        // False when it is back in the pool, was rented again since (another lease) or is held by
        // another entity: the value carrying it is a stale copy.
        internal bool TryAdopt(int pool, int entityIndex, int lease) {
            if (InPool || lease != Lease) {
                return false;
            }
            if (OwnerPool == 0) {
                OwnerPool = pool;
                OwnerEntity = entityIndex;
                return true;
            }
            return OwnerPool == pool && OwnerEntity == entityIndex;
        }
    }

    // Static: one pool per element type, shared by every world.
    // A list must be returned once: returning it twice would hand it to two owners, so a second
    // Return of the same list is ignored.
    internal static class ListPool<T> {
        // Used as a stack (top = last element); a List rather than a Stack so
        // Prewarm can move the lists it prepares to the top without allocating.
        private static readonly List<PooledList<T>> _free = new();

        // A list without a holder; the buffer that rents it records its Lease.
        public static PooledList<T> Rent() {
            return RentPooled();
        }

        // A list held by the entity's component in that pool (see PooledList<T>.TryAdopt).
        public static PooledList<T> Rent(int pool, int entityIndex) {
            var list = RentPooled();
            list.OwnerPool = pool;
            list.OwnerEntity = entityIndex;
            return list;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static PooledList<T> RentPooled() {
            int last = _free.Count - 1;
            if (last < 0) {
                return new PooledList<T> { Lease = 1 };
            }
            var list = _free[last];
            _free.RemoveAt(last);
            list.InPool = false;
            // Wraps after 2^32 rents of one list; a copy that old is not around any more.
            list.Lease++;
            // A stale EventBuffer copy may have appended to the list while it was free.
            if (list.Count != 0) {
                list.Clear();
            }
            return list;
        }

        public static void Return(List<T> list) {
            list.Clear();
            // A list that did not come from the pool (assigned to Values by hand) is dropped.
            if (list is PooledList<T> pooled && !pooled.InPool) {
                pooled.InPool = true;
                pooled.OwnerPool = 0;
                _free.Add(pooled);
            }
        }

        // Number of free lists; read by tests.
        public static int FreeCount => _free.Count;

        // How many times the list is among the free lists; read by tests.
        public static int TimesFree(List<T> list) {
            int times = 0;
            for (int i = 0; i < _free.Count; i++) {
                if (ReferenceEquals(_free[i], list)) {
                    times++;
                }
            }
            return times;
        }

        // Capacity of the free list that Rent would hand out after skip others; read by tests.
        public static int FreeCapacityFromTop(int skip) => _free[_free.Count - 1 - skip].Capacity;

        // Make the top `lists` free lists (the next ones Rent hands out) have room for
        // `capacity` items. Each slot from the top takes a list that is already large
        // enough, else the largest one below it, which is grown; only those are touched,
        // so a pool left large by an earlier peak is not grown wholesale. Missing lists are
        // created on top. O(lists * free), load time only. A repeated call finds every top
        // slot ready and allocates nothing. Growing the backing List up front means returning
        // the prewarmed lists later never resizes it.
        public static void Prewarm(int lists, int capacity) {
            int free = _free.Count;
            int fromPool = Math.Min(lists, free);
            for (int k = 0; k < fromPool; k++) {
                int slot = free - 1 - k;
                if (_free[slot].Capacity >= capacity) {
                    continue;
                }
                int best = slot;
                for (int i = slot - 1; i >= 0; i--) {
                    int c = _free[i].Capacity;
                    if (c >= capacity) {
                        best = i;
                        break;
                    }
                    if (c > _free[best].Capacity) {
                        best = i;
                    }
                }
                if (best != slot) {
                    var swap = _free[slot];
                    _free[slot] = _free[best];
                    _free[best] = swap;
                }
                if (_free[slot].Capacity < capacity) {
                    _free[slot].Capacity = capacity;
                }
            }
            if (free >= lists) {
                return;
            }
            if (_free.Capacity < lists) {
                _free.Capacity = lists;
            }
            // New lists go on top, the grown ones stay right below them: all of them are
            // handed out before any untouched list.
            while (_free.Count < lists) {
                _free.Add(new PooledList<T>(capacity) { InPool = true });
            }
        }
    }

    /// <summary> World extensions for appending events to <see cref="EventBuffer{T}"/> components. </summary>
    public static class WorldEventBufferExtensions {
        /// <summary>
        /// Append an event to the entity's EventBuffer&lt;T&gt;, adding the component if needed. A list it
        /// rents, also for a buffer the entity has without one (<see cref="EventBuffer{T}.Values"/> null), is
        /// recorded as the entity's, so a copy of the buffer stored for another entity cannot share it.
        /// Under KENSEI_DEBUG a handle that is not alive throws (whether or not the slot's new occupant has a
        /// buffer), even while another entity is being destroyed; the entity being destroyed itself is
        /// accepted, like in the other entity operations.
        /// </summary>
        public static void AddEvent<T>(this World world, Entity entity, T value) where T : struct {
#if KENSEI_DEBUG
            world.ValidateHandle<EventBuffer<T>>(entity, "AddEvent");
#endif
            var pool = world.Pool<EventBuffer<T>>();
            int idx = entity.Index;
            if (pool.Has(idx)) {
                ref var existing = ref pool.Get(idx);
                // EventBuffer.Add would rent a list without a holder: a copy of the buffer could then
                // give it to a second entity.
                if (existing.Values == null) {
                    var list = ListPool<T>.Rent(pool.PoolId, idx);
                    existing.Values = list;
                    existing._lease = list.Lease;
                }
                existing.Values.Add(value);
                return;
            }

            var buffer = new EventBuffer<T>();
            buffer.Add(value);
            world.Add(entity, buffer);
        }
    }
}
