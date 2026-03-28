#if UNITY_2018_1_OR_NEWER
using System.Collections.Generic;

namespace KenseiECS {
    /// <summary>
    /// Component that holds a list of listeners implementing interface T.
    /// Used as a bridge between ECS and Unity MonoBehaviours.
    ///
    /// Usage:
    ///   // In system:
    ///   foreach (int e in _damageFilter) {
    ///       ref var listeners = ref _listenersPool.Get(e);
    ///       for (int i = listeners.Values.Count - 1; i >= 0; i--) {
    ///           listeners.Values[i].OnDamage(damage);
    ///       }
    ///   }
    /// </summary>
    public struct Listeners<T> : IComponent, IAutoReset<Listeners<T>> where T : class {
        public List<T> Values;

        /// <summary> Add a listener. Creates list if needed. </summary>
        public void Add(T listener) {
            Values ??= new List<T>();
            Values.Add(listener);
        }

        /// <summary> Remove a listener. </summary>
        public void Remove(T listener) {
            Values?.Remove(listener);
        }

        public void AutoReset(ref Listeners<T> c) {
            c.Values?.Clear();
            c.Values = null;
        }
    }
}
#endif
