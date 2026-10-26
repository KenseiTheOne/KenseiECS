# Listener Bridge

Systems often need to push results out to views — a health bar, a hit animation, a sound. In KenseiECS the system calls the view directly; there are two ways to find it.

## One view per entity: a component and a filter

When an entity has one view of a kind, keep a reference to it in a component and let a system find it with a filter, like any other data:

<<< @/snippets/Guides/ListenerBridge.cs#view-ref{csharp}

The event (`Damaged`) is a [one-frame component](../guides/one-frame.md): it lives for the frame, and every system that cares reads it. Nothing needs subscribing or unsubscribing: the view component goes away with the entity.

## Any number of listeners: `Listeners<T>`

When the subscribers are not known in advance — a HUD and a camera shake both following the player — `Listeners<T>` keeps a list of them on the entity. It lives in Core and has no Unity dependency, so the examples below compile in plain .NET too.

Declare a listener interface and implement it on the view:

<<< @/snippets/Guides/ListenerBridge.cs#interface{csharp}

Subscribe and unsubscribe:

<<< @/snippets/Guides/ListenerBridge.cs#subscribe{csharp}

Create an entity with a listener already attached:

<<< @/snippets/Guides/ListenerBridge.cs#create{csharp}

### Calling listeners from a system

`Listeners<T>` is an ordinary component and holds data only. A system filters on it, fetches it from its pool and calls every listener itself:

<<< @/snippets/Guides/ListenerBridge.cs#system{csharp}

The listeners are called in subscription order (first subscribed, first called), with no delegate and no allocation.

**A callback must not change the list it is called from**: no subscribing or unsubscribing on that entity, no removing its `Listeners<T>` and no destroying it. The loop does not check this: a listener that breaks the rule makes the next one be skipped, or called twice. Changes to other entities and to the rest of the world are fine.

::: details Why `for` and not `foreach`
`foreach` over the `List<T>` would turn a broken rule into an `InvalidOperationException` on the next step, since its enumerator checks the list's version on every step. Under IL2CPP that costs 1.5 to 3 times as much as the `for` loop (the enumerator of a list of interfaces runs through shared generic code and is not inlined): 23 ns against 14 ns for 8 listeners, 6 ns against 2 ns for one. It can serve as a check while the rule is new to a project.
:::

### Leaving from inside a callback

A listener that stops listening from its own callback (a one-time hint, a tutorial step) calls `UnsubscribeLater`:

<<< @/snippets/Guides/ListenerBridge.cs#unsubscribe-later{csharp}

The removal is queued and applied when the outermost `SystemsRunner.Run` of the world returns, after its systems and its OneFrame cleanup, also when a system threw; until then the listener stays in the list. In `EcsBootstrap` the update runner and its `fixed` and `late` phases each run on their own, so each applies the removals queued during it. Called while no runner runs, the removal is applied at the next `World.NextTick`, which the root runner calls at the start of its `Run`, before any system (a world driven without a runner calls `NextTick` itself). It is applied to the list as it is then, so a listener that subscribed again in the same tick is removed as well. A removal whose entity was destroyed by then is skipped, even if a new entity took the slot; `World.Clear` drops the queue, and on a destroyed world the call does nothing. A listener may call it while its entity is being destroyed. The queue is kept per listener type and reused, so it stops allocating at its peak.

::: tip Unity views
`Object.Destroy` runs `OnDestroy` at the end of the frame, outside every system, so a view that unsubscribes in `OnDestroy` (the example at the end of this page) needs nothing more.
:::

### Errors

An exception from a listener leaves the system's loop, and the remaining listeners are not called. To report it and go on, catch it around the call:

<<< @/snippets/Guides/ListenerBridge.cs#on-error{csharp}

::: info Changed in 2.3.0
`Listeners<T>.Invoke`, `Add` and `Remove` are obsolete (warning CS0618) and are removed in 3.0. `Invoke` walked a snapshot of the list so that callbacks could change it; a view has no need to, and the direct loop needs no delegate and is about four times as fast under IL2CPP (14 ns against 53 ns for 8 listeners). The one change a listener does need, leaving from its own callback, is `UnsubscribeLater`.

| 2.2 | 2.3 |
|---|---|
| `l.Invoke(x => x.OnDeath())` | `var v = l.Values; for (int i = 0; v != null && i < v.Count; i++) v[i].OnDeath();` |
| `l.Invoke(d, static (x, a) => x.OnDamage(a))` | `var v = l.Values; for (int i = 0; v != null && i < v.Count; i++) v[i].OnDamage(d);` |
| `onError` | `try`/`catch` around the call in the loop |
| `listeners.Add(x)`, `listeners.Remove(x)` | `world.Subscribe<IDeath>(entity, x)`, `world.Unsubscribe<IDeath>(entity, x)` |
| a callback that unsubscribes | `world.UnsubscribeLater<IDeath>(entity, this)` |

Read `Values` once into a local, as in the table: `l` is often a `ref` into the pool, and a callback that destroys another entity can move the component under it. `Values` is `null` on a `Listeners<T>` added without a list (`Invoke` skipped it). Give the listener type explicitly: with `this`, C# infers the view's own class and the call goes to a `Listeners` of that class.

The loop calls the first subscriber first, like `Invoke`. Code that walked `Values` from the end called the last one first; check views that depend on the order.
:::

## Reference

| API | Behavior |
|---|---|
| `world.Subscribe<T>(entity, listener)` | Adds `listener` to the entity's `Listeners<T>`, creating the component if it is missing. |
| `world.Unsubscribe<T>(entity, listener)` | Removes `listener` at once; not from inside a callback of that list. Does nothing if the entity has no `Listeners<T>`. The component stays on the entity even when empty, so unsubscribing the last listener never auto-destroys the entity; call `Remove<Listeners<T>>` to drop it explicitly. |
| `world.UnsubscribeLater<T>(entity, listener)` | Removes `listener` when the outermost `Run` of the world returns; the way to leave from inside a callback. |
| `world.HasListeners<T>(entity)` | True when the entity has a `Listeners<T>` with at least one listener. |
| `world.CreateWithListener<T>(listener)` | Shortcut for `CreateEntity` + `Subscribe`. |
| `Listeners<T>.Values` | The `List<T>` of listeners, in subscription order. |
| `Listeners<T>.Count` | Number of listeners (0 when the list was never created). |
| `Listeners<T>.Invoke`, `Add`, `Remove` | Obsolete since 2.3.0, removed in 3.0. |

`T` must be a reference type (`where T : class`). Under `KENSEI_DEBUG`, `Subscribe`, `Unsubscribe` and `UnsubscribeLater` throw `InvalidOperationException` for a dead entity. When the component is removed (or the entity destroyed), its auto-reset clears and drops the list, so a recycled slot starts with no stale listeners. `CopyEntity` gives the copy its own list with the same listeners.

On the Unity side the listener is typically the view that was [bound](./entity-view.md) to the entity:

```csharp
public sealed class EnemyView : MonoBehaviour, IDamageListener {
    [SerializeField] private EcsEntityView _view;

    private void Start() {
        _view.World.Subscribe<IDamageListener>(_view.Entity, this);
    }

    private void OnDestroy() {
        if (_view.IsAlive) {
            _view.World.Unsubscribe<IDamageListener>(_view.Entity, this);
        }
    }

    public void OnDamage(float damage) {
        Debug.Log($"{name} took {damage} damage");
    }
}
```
