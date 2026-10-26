using System;
using System.Buffers;
using System.Collections.Generic;

namespace KenseiECS {
    /// <summary>
    /// Component that holds a list of listeners implementing interface T: the bridge from ECS to the
    /// views (MonoBehaviours) that want to hear about an entity. It is data only; a system calls the
    /// listeners itself:
    /// <code>
    ///   // In system:
    ///   foreach (int e in _damageFilter) {
    ///       var listeners = _listenersPool.Get(e).Values;
    ///       for (int i = 0; listeners != null &amp;&amp; i &lt; listeners.Count; i++) {
    ///           listeners[i].OnDamage(damage);
    ///       }
    ///   }
    /// </code>
    /// A callback must not change the list it is called from (subscribe or unsubscribe on that entity,
    /// remove the component or destroy the entity): the loop would skip or repeat a listener. A
    /// listener that leaves from inside its own callback uses
    /// <see cref="WorldListenerExtensions.UnsubscribeLater{T}"/>, applied at the end of the tick.
    /// Subscribe with <see cref="WorldListenerExtensions.Subscribe{T}"/>. (<c>foreach</c> over the
    /// <see cref="List{T}"/> turns a broken rule into an <see cref="InvalidOperationException"/>, but
    /// under IL2CPP it costs 1.5 to 3 times as much as the <c>for</c> loop.)
    ///
    /// Removing the component (or destroying the entity) clears the list and drops it.
    /// CopyEntity gives the copy its own list.
    /// </summary>
    public struct Listeners<T> : IComponent, IAutoReset<Listeners<T>>, IAutoCopy<Listeners<T>> where T : class {
        /// <summary>
        /// Registered listeners, in subscription order. Null until the first Subscribe (and on a
        /// component added by hand without a list).
        /// </summary>
        public List<T> Values;

        /// <summary> Number of listeners. </summary>
        public int Count => Values?.Count ?? 0;

        /// <summary> Add a listener. Creates list if needed. </summary>
        [Obsolete("Use World.Subscribe. Removed in 3.0.")]
        public void Add(T listener) {
            Values ??= new List<T>();
            Values.Add(listener);
        }

        /// <summary> Remove a listener. </summary>
        [Obsolete("Use World.Unsubscribe, or World.UnsubscribeLater from inside a callback of this list. Removed in 3.0.")]
        public void Remove(T listener) {
            Values?.Remove(listener);
        }

        /// <summary>
        /// Call <paramref name="call"/> for every listener, in registration order (first subscribed,
        /// first called). The listeners registered when Invoke starts are taken as a snapshot; each
        /// snapshot entry is called at most once, and only if it is still in the list when its turn
        /// comes: a listener removed from the list during the dispatch (by itself or another callback)
        /// before its turn is skipped. A listener subscribed during the dispatch is not called by it.
        /// Removing the component (or destroying the entity) during the dispatch clears the list, so
        /// the remaining listeners are skipped. Nothing when <see cref="Values"/> is null.
        /// <para>
        /// "Still in the list" is tested with the same comparison as <see cref="Remove"/>
        /// (<see cref="EqualityComparer{T}.Default"/>), and the instance found in the list is called:
        /// a boxed struct listener removed and added again counts as still subscribed. A listener added
        /// several times is matched by value, not per copy: while at least one copy is in the list,
        /// every copy in the snapshot is called (removing one of two copies does not skip either).
        /// </para>
        /// <para>
        /// Unity: <c>Object.Destroy</c> runs <c>OnDestroy</c> at the end of the frame, so a view a
        /// callback destroys with it is still subscribed (and still alive) for the rest of this
        /// dispatch and is called. It is skipped only if it leaves the list before its turn: an
        /// explicit <see cref="Remove"/> in the callback, or <c>DestroyImmediate</c> with the
        /// unsubscribe in <c>OnDestroy</c>.
        /// </para>
        /// </summary>
        /// <remarks>
        /// The snapshot is rented from <see cref="ArrayPool{T}.Shared"/>, cleared and returned
        /// afterwards (also when a callback or <paramref name="onError"/> throws), so a dispatch
        /// allocates nothing once the shared pool has an array of that size; nested Invoke calls rent
        /// their own snapshot. While the list is unchanged the "still subscribed" test is one reference
        /// comparison per listener. After a change the lookup widens from where the previous listener
        /// was found, in both directions: a listener that unsubscribed itself, or any removal or
        /// insertion before the current position, costs about two steps per changed entry, and the
        /// dispatch stays linear.
        /// Only a listener removed by another callback is searched for in the whole list, which costs
        /// about as much as the <see cref="Remove"/> that removed it.
        /// A capturing lambda allocates a closure per call: pass state through
        /// <see cref="Invoke{TArg}(TArg, Action{T, TArg}, Action{T, TArg, Exception})"/> and a static lambda.
        /// <para>
        /// Obsolete since 2.3.0, removed in 3.0: a system iterates <see cref="Values"/> and calls the
        /// listeners directly, which is faster and needs no delegate; a listener leaves from inside a
        /// callback with <c>World.UnsubscribeLater</c>.
        /// </para>
        /// </remarks>
        /// <param name="call"> Called once per listener. </param>
        /// <param name="onError">
        /// Null: an exception from <paramref name="call"/> propagates at once and the remaining listeners
        /// are not called. Otherwise it receives the listener and the exception, and the dispatch continues.
        /// If <paramref name="onError"/> itself throws, that exception propagates and the dispatch stops.
        /// </param>
        [Obsolete("Iterate Values in the system: var values = listeners.Values; for (int i = 0; values != null && i < values.Count; i++) values[i].OnX(...). No delegate, nothing allocated. A callback must not change that list; it leaves with World.UnsubscribeLater. Removed in 3.0.")]
        public void Invoke(Action<T> call, Action<T, Exception> onError = null) {
            var values = Values;
            if (values == null || values.Count == 0) {
                return;
            }

            int count = values.Count;
            var snapshot = ArrayPool<T>.Shared.Rent(count);
            values.CopyTo(snapshot, 0);
            // Position in the live list where the next snapshot entry is expected.
            int cursor = 0;
            try {
                for (int i = 0; i < count; i++) {
                    var listener = snapshot[i];
                    // Unchanged list: the entry sits at the cursor, one reference comparison.
                    if (cursor < values.Count && ReferenceEquals(values[cursor], listener)) {
                        cursor++;
                    } else {
                        int at = FindChanged(values, listener, cursor);
                        if (at < 0) {
                            continue;
                        }
                        cursor = at + 1;
                        listener = values[at];
                    }
                    if (onError == null) {
                        call(listener);
                    } else {
                        CallGuarded(listener, call, onError);
                    }
                }
            } finally {
                // Cleared so the shared pool does not keep listeners (views) alive.
                Array.Clear(snapshot, 0, count);
                ArrayPool<T>.Shared.Return(snapshot);
            }
        }

        /// <summary>
        /// Call <paramref name="call"/> for every listener with <paramref name="arg"/>, with the same
        /// order, snapshot semantics (unsubscribed listeners are skipped, new ones are not called) and
        /// error handling as <see cref="Invoke(Action{T}, Action{T, Exception})"/>.
        /// With a static lambda (<c>Invoke(damage, static (l, d) =&gt; l.OnDamage(d))</c>) the call
        /// allocates nothing: the state travels in <paramref name="arg"/> instead of a closure.
        /// <paramref name="onError"/> gets <paramref name="arg"/> too, so a static error handler can
        /// log it (<c>static (l, offer, ex) =&gt; Log(offer.Serial, ex)</c>) without capturing it.
        /// Obsolete since 2.3.0, removed in 3.0: see <see cref="Invoke(Action{T}, Action{T, Exception})"/>.
        /// </summary>
        /// <param name="arg"> Passed to every call and to <paramref name="onError"/>. </param>
        /// <param name="call"> Called once per listener with <paramref name="arg"/>. </param>
        /// <param name="onError">
        /// Null: an exception from <paramref name="call"/> propagates at once and the remaining listeners
        /// are not called. Otherwise it receives the listener, <paramref name="arg"/> and the exception,
        /// and the dispatch continues. If <paramref name="onError"/> itself throws, that exception
        /// propagates and the dispatch stops.
        /// </param>
        [Obsolete("Iterate Values in the system: var values = listeners.Values; for (int i = 0; values != null && i < values.Count; i++) values[i].OnX(arg). No delegate, nothing allocated. A callback must not change that list; it leaves with World.UnsubscribeLater. Removed in 3.0.")]
        public void Invoke<TArg>(TArg arg, Action<T, TArg> call, Action<T, TArg, Exception> onError = null) {
            var values = Values;
            if (values == null || values.Count == 0) {
                return;
            }

            int count = values.Count;
            var snapshot = ArrayPool<T>.Shared.Rent(count);
            values.CopyTo(snapshot, 0);
            // Position in the live list where the next snapshot entry is expected.
            int cursor = 0;
            try {
                for (int i = 0; i < count; i++) {
                    var listener = snapshot[i];
                    // Unchanged list: the entry sits at the cursor, one reference comparison.
                    if (cursor < values.Count && ReferenceEquals(values[cursor], listener)) {
                        cursor++;
                    } else {
                        int at = FindChanged(values, listener, cursor);
                        if (at < 0) {
                            continue;
                        }
                        cursor = at + 1;
                        listener = values[at];
                    }
                    if (onError == null) {
                        call(listener, arg);
                    } else {
                        CallGuarded(listener, arg, call, onError);
                    }
                }
            } finally {
                Array.Clear(snapshot, 0, count);
                ArrayPool<T>.Shared.Return(snapshot);
            }
        }

        // The catch lives outside the dispatch loop: a handler inside it keeps the loop's locals
        // (cursor, list) on the stack, which made every listener measurably slower.
        private static void CallGuarded(T listener, Action<T> call, Action<T, Exception> onError) {
            try {
                call(listener);
            } catch (Exception ex) {
                onError(listener, ex);
            }
        }

        private static void CallGuarded<TArg>(T listener, TArg arg, Action<T, TArg> call,
            Action<T, TArg, Exception> onError) {
            try {
                call(listener, arg);
            } catch (Exception ex) {
                onError(listener, arg, ex);
            }
        }

        // Where a snapshot entry is in the live list after the list changed, or -1 if it was
        // unsubscribed. The search widens from the cursor in both directions at once (cursor-1,
        // cursor, cursor-2, cursor+1, ...): removals before the cursor shift the entry down (a
        // listener that unsubscribed itself: one step), insertions before it shift it up (two or
        // three steps), so an entry moved by d positions is found in about 2d steps either way.
        // Searching one side to its end first made "insert at 0 in every callback" quadratic. Once
        // one side runs out the other is scanned to its end, so a missing entry costs one full pass,
        // like the Remove that took it out. The cursor resynchronises at the match, so a single
        // change does not make the rest of the dispatch quadratic. Matching uses
        // EqualityComparer<T>.Default like List.Remove (UnityEngine.Object.Equals compares instance
        // ids, so a destroyed view still matches itself), so a boxed struct listener removed and
        // added again during the dispatch still counts as subscribed.
        private static int FindChanged(List<T> values, T listener, int cursor) {
            int n = values.Count;
            var comparer = EqualityComparer<T>.Default;
            int back = (cursor < n ? cursor : n) - 1;
            int ahead = cursor;
            while (back >= 0 || ahead < n) {
                if (back >= 0) {
                    if (comparer.Equals(values[back], listener)) {
                        return back;
                    }
                    back--;
                }
                if (ahead < n) {
                    if (comparer.Equals(values[ahead], listener)) {
                        return ahead;
                    }
                    ahead++;
                }
            }
            return -1;
        }

        /// <summary> Clear the list and drop it, leaving <see cref="Values"/> null. </summary>
        public void AutoReset(ref Listeners<T> c) {
            c.Values?.Clear();
            c.Values = null;
        }

        /// <summary> Give the copy its own list with the same listeners. </summary>
        public void AutoCopy(ref Listeners<T> c) {
            if (c.Values != null) {
                c.Values = new List<T>(c.Values);
            }
        }
    }
}
