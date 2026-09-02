using System.Collections.Generic;

namespace KenseiECS {
    /// <summary>
    /// Component holding several events of type T for one entity within a frame,
    /// for cases where a plain OneFrame component (one per entity) is not enough.
    /// Lists are pooled: AutoReset returns the list on remove, so a
    /// OneFrame<EventBuffer<T>> registration allocates nothing after warmup.
    ///
    /// Usage:
    ///   world.AddEvent(entity, new DamageEvent { Value = 10 });
    ///   world.AddEvent(entity, new DamageEvent { Value = 5 });
    ///
    ///   foreach (int e in _damaged) {
    ///       var events = _buffers.Get(e).Values;
    ///       for (int i = 0; i < events.Count; i++) { ... }
    ///   }
    ///
    ///   systems.OneFrame<EventBuffer<DamageEvent>>();
    /// </summary>
    public struct EventBuffer<T> : IComponent, IAutoReset<EventBuffer<T>> where T : struct {
        public List<T> Values;

        public int Count => Values?.Count ?? 0;

        public void Add(T value) {
            Values ??= ListPool<T>.Rent();
            Values.Add(value);
        }

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

    public static class WorldEventBufferExtensions {
        /// <summary> Append an event to the entity's EventBuffer<T>, adding the component if needed. </summary>
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
