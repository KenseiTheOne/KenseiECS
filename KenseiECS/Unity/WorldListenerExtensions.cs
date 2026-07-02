#if UNITY_2018_1_OR_NEWER
#if KENSEI_DEBUG
using System;
#endif

namespace KenseiECS {
    /// <summary>
    /// Extension methods for listener management on World.
    /// Wraps Listeners<T> component — user doesn't interact with it directly.
    ///
    /// Usage:
    ///   world.Subscribe<IDamageListener>(entity, view);
    ///   world.Unsubscribe<IDamageListener>(entity, view);
    /// </summary>
    public static class WorldListenerExtensions {
        /// <summary>
        /// Subscribe a listener to an entity.
        /// Creates Listeners<T> component automatically if not present.
        /// </summary>
        public static void Subscribe<T>(this World world, Entity entity, T listener) where T : class {
#if KENSEI_DEBUG
            if (!world.IsAlive(entity)) {
                throw new InvalidOperationException($"Subscribe<{typeof(T).Name}>: entity {entity} is not alive");
            }
#endif
            var pool = world.Pool<Listeners<T>>();
            int idx = entity.Index;

            if (pool.Has(idx)) {
                ref var listeners = ref pool.Get(idx);
                listeners.Add(listener);
            } else {
                var listeners = new Listeners<T>();
                listeners.Add(listener);
                pool.Add(idx, listeners);
            }
        }

        /// <summary>
        /// Unsubscribe a listener from an entity.
        /// If no listeners remain, removes the Listeners<T> component.
        /// </summary>
        public static void Unsubscribe<T>(this World world, Entity entity, T listener) where T : class {
#if KENSEI_DEBUG
            if (!world.IsAlive(entity)) {
                throw new InvalidOperationException($"Unsubscribe<{typeof(T).Name}>: entity {entity} is not alive");
            }
#endif
            var pool = world.Pool<Listeners<T>>();
            int idx = entity.Index;

            if (!pool.Has(idx)) {
                return;
            }

            ref var listeners = ref pool.Get(idx);
            listeners.Remove(listener);

            if (listeners.Values == null || listeners.Values.Count == 0) {
                pool.Remove(idx);
            }
        }

        /// <summary> Check if entity has any listeners of type T. </summary>
        public static bool HasListeners<T>(this World world, Entity entity) where T : class {
            return world.Pool<Listeners<T>>().Has(entity.Index);
        }

        /// <summary>
        /// Create a new entity with a Listeners<T> component and an initial listener.
        /// Shortcut for CreateEntity + Subscribe.
        /// </summary>
        public static Entity CreateWithListener<T>(this World world, T listener) where T : class {
            var listeners = new Listeners<T>();
            listeners.Add(listener);
            return world.CreateEntity(listeners);
        }
    }
}
#endif
