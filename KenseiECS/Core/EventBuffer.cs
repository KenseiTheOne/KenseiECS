using System.Collections.Generic;

namespace KenseiECS {
    /// <summary>
    /// Component holding several events of type T for one entity within a frame,
    /// for cases where a plain OneFrame component (one per entity) is not enough.
    /// Lists are pooled: AutoReset returns the list on remove (OneFrame cleanup,
    /// DestroyEntity, World.Clear), so once the pool holds as many lists as are alive
    /// at the peak, a OneFrame&lt;EventBuffer&lt;T&gt;&gt; registration allocates nothing.
    /// World.Warmup does not fill this pool: it adds buffers without lists. The pool is
    /// static, one per event type, and shared by all worlds in the process.
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
        private static readonly Stack<List<T>> _free = new();

        public static List<T> Rent() {
            return _free.Count > 0 ? _free.Pop() : new List<T>();
        }

        public static void Return(List<T> list) {
            list.Clear();
            _free.Push(list);
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
