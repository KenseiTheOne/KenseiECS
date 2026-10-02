using System.Collections.Generic;

namespace KenseiECS {
    /// <summary>
    /// Component holding several events of type T for one entity within a frame,
    /// for cases where a plain OneFrame component (one per entity) is not enough.
    /// Lists are pooled: AutoReset returns the list on remove, so a
    /// OneFrame&lt;EventBuffer&lt;T&gt;&gt; registration allocates nothing after warmup.
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
    public struct EventBuffer<T> : IComponent, IAutoReset<EventBuffer<T>> where T : struct {
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
    }

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
        /// <summary> Append an event to the entity's EventBuffer&lt;T&gt;, adding the component if needed. </summary>
        public static void AddEvent<T>(this World world, Entity entity, T value) where T : struct {
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
