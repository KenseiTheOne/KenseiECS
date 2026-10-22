using System;
using System.Collections.Generic;

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
    public struct EventBuffer<T> : IComponent, IAutoReset<EventBuffer<T>>, IAutoCopy<EventBuffer<T>> where T : struct {
        /// <summary> Recorded events, in insertion order. Null until the first Add and after AutoReset. </summary>
        public List<T> Values;

        /// <summary> Number of recorded events; 0 when no list is rented. </summary>
        public int Count => Values?.Count ?? 0;

        /// <summary> Append an event. Rents a pooled list on first use. </summary>
        public void Add(T value) {
            Values ??= ListPool<T>.Rent();
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
        /// arguments allocates nothing.
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
            }
        }
    }

    // Static: one pool per element type, shared by every world.
    // A list must be returned once: returning it twice hands it to two owners.
    internal static class ListPool<T> {
        // Used as a stack (top = last element); a List rather than a Stack so
        // Prewarm can move the lists it prepares to the top without allocating.
        private static readonly List<List<T>> _free = new();

        public static List<T> Rent() {
            int last = _free.Count - 1;
            if (last < 0) {
                return new List<T>();
            }
            var list = _free[last];
            _free.RemoveAt(last);
            return list;
        }

        public static void Return(List<T> list) {
            list.Clear();
            _free.Add(list);
        }

        // Number of free lists; read by tests.
        public static int FreeCount => _free.Count;

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
                _free.Add(new List<T>(capacity));
            }
        }
    }

    /// <summary> World extensions for appending events to <see cref="EventBuffer{T}"/> components. </summary>
    public static class WorldEventBufferExtensions {
        /// <summary>
        /// Append an event to the entity's EventBuffer&lt;T&gt;, adding the component if needed.
        /// Under KENSEI_DEBUG a stale handle throws, whether or not the slot's new occupant has a buffer.
        /// </summary>
        public static void AddEvent<T>(this World world, Entity entity, T value) where T : struct {
#if KENSEI_DEBUG
            world.ValidateHandle<EventBuffer<T>>(entity, "AddEvent");
#endif
            var pool = world.Pool<EventBuffer<T>>();
            int idx = entity.Index;
            if (pool.Has(idx)) {
                pool.Get(idx).Add(value);
                return;
            }

            var buffer = new EventBuffer<T>();
            buffer.Add(value);
            world.Add(entity, buffer);
        }
    }
}
