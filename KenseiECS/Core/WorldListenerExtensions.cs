#if KENSEI_DEBUG
using System;
#endif
using System.Collections.Generic;

namespace KenseiECS {
    /// <summary>
    /// Extension methods for listener management on World.
    /// Wraps Listeners&lt;T&gt; component — user doesn't interact with it directly.
    ///
    /// Usage:
    /// <code>
    ///   world.Subscribe&lt;IDamageListener&gt;(entity, view);
    ///   world.Unsubscribe&lt;IDamageListener&gt;(entity, view);
    ///   world.UnsubscribeLater&lt;IDamageListener&gt;(entity, this);   // from inside a callback
    /// </code>
    /// </summary>
    public static class WorldListenerExtensions {
        /// <summary>
        /// Subscribe a listener to an entity.
        /// Creates Listeners&lt;T&gt; component automatically if not present.
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
                (listeners.Values ??= new List<T>()).Add(listener);
            } else {
                pool.Add(idx, new Listeners<T> { Values = new List<T> { listener } });
            }
        }

        /// <summary>
        /// Unsubscribe a listener from an entity.
        /// The Listeners&lt;T&gt; component stays on the entity even when empty, so
        /// unsubscribing the last listener never auto-destroys the entity.
        /// Remove&lt;Listeners&lt;T&gt;&gt; explicitly to drop the component.
        /// Not from inside a callback of this list: the system walking it would skip or repeat a
        /// listener. Use <see cref="UnsubscribeLater{T}"/> there.
        /// </summary>
        public static void Unsubscribe<T>(this World world, Entity entity, T listener) where T : class {
#if KENSEI_DEBUG
            if (!world.IsAliveOrDying(entity)) {
                throw new InvalidOperationException($"Unsubscribe<{typeof(T).Name}>: entity {entity} is not alive");
            }
#endif
            var pool = world.Pool<Listeners<T>>();
            int idx = entity.Index;

            if (!pool.Has(idx)) {
                return;
            }

            pool.Get(idx).Values?.Remove(listener);
        }

        /// <summary>
        /// Unsubscribe a listener at the end of the tick: the removal is queued and applied when the
        /// outermost <see cref="SystemsRunner.Run(World)"/> of this world returns, after its systems and
        /// OneFrame cleanup (also when a system threw). This is how a listener leaves from inside a
        /// callback, while a system iterates the list it is in, which must not change during that
        /// iteration. The listener stays in the list, and is called by the dispatches that run, until then.
        /// </summary>
        /// <remarks>
        /// Removals are applied in the order they were queued, each as <see cref="Unsubscribe{T}"/>
        /// would, to the list as it is then: a listener subscribed again in the same tick is removed too.
        /// One whose entity was destroyed by then (even if a new entity took the slot), or whose entity
        /// has no Listeners&lt;T&gt; any more, is skipped. Called while no runner of this world runs, the
        /// removal is applied at the next <see cref="World.NextTick"/>, which the root runner's
        /// <see cref="SystemsRunner.Run()"/> calls before its systems (a world driven without a runner
        /// calls it itself).
        /// <see cref="World.Clear"/> drops the queued removals, and on a destroyed world the call does
        /// nothing. A listener may call it while its entity's own DestroyEntity runs. The queue is kept per
        /// listener type and reused, so it allocates nothing once it reached its peak.
        /// <para>
        /// Pass the listener type explicitly (<c>UnsubscribeLater&lt;IDamageListener&gt;(entity, this)</c>):
        /// with <c>this</c> C# would infer the view's own class, and the removal would look for a
        /// Listeners of that class.
        /// </para>
        /// </remarks>
        public static void UnsubscribeLater<T>(this World world, Entity entity, T listener) where T : class {
            if (world.IsDestroyed) {
                return;
            }
#if KENSEI_DEBUG
            if (!world.IsAliveOrDying(entity)) {
                throw new InvalidOperationException($"UnsubscribeLater<{typeof(T).Name}>: entity {entity} is not alive");
            }
#endif
            world.UnsubscribeQueue<T>().Add(entity, listener);
        }

        /// <summary> Check if entity has at least one listener of type T. </summary>
        public static bool HasListeners<T>(this World world, Entity entity) where T : class {
            var pool = world.Pool<Listeners<T>>();
            int idx = entity.Index;
            return pool.Has(idx) && pool.Get(idx).Count > 0;
        }

        /// <summary>
        /// Create a new entity with a Listeners&lt;T&gt; component and an initial listener.
        /// Shortcut for CreateEntity + Subscribe.
        /// </summary>
        public static Entity CreateWithListener<T>(this World world, T listener) where T : class {
            return world.CreateEntity(new Listeners<T> { Values = new List<T> { listener } });
        }
    }
}
