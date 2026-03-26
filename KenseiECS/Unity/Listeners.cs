#if UNITY_2018_1_OR_NEWER
using System;
using System.Collections.Generic;

namespace KenseiECS {
    /// <summary>
    /// Component that holds a list of listeners implementing interface T.
    /// Used as a bridge between ECS and Unity MonoBehaviours.
    ///
    /// Usage:
    ///   world.Add(entity, new Listeners<IDamageListener> { Values = new List<IDamageListener> { view } });
    ///
    ///   foreach (int e in _filter) {
    ///       ref var listeners = ref _pool.Get(e);
    ///       listeners.Notify(l => l.OnDamage(damage));
    ///   }
    /// </summary>
    public struct Listeners<T> : IComponent where T : class {
        public List<T> Values;

        /// <summary>
        /// Invoke an action on all listeners.
        /// Iterates in reverse — safe if a listener removes itself during callback.
        /// </summary>
        public void Notify(Action<T> action) {
            if (Values == null) {
                return;
            }

            for (int i = Values.Count - 1; i >= 0; i--) {
                action(Values[i]);
            }
        }

        /// <summary> Add a listener. Creates list if needed. </summary>
        public void Add(T listener) {
            Values ??= new List<T>();
            Values.Add(listener);
        }

        /// <summary> Remove a listener. </summary>
        public void Remove(T listener) {
            Values?.Remove(listener);
        }
    }
}
#endif
