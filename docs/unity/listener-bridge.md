# Listener Bridge

Systems often need to push results out to views — a health bar, a hit animation, a sound. The listener bridge lets a `MonoBehaviour` (or any class) subscribe to an entity through an interface, and systems call it directly.

`Listeners<T>` lives in Core and has no Unity dependency, so the examples below compile in plain .NET too.

## Subscribing

Declare a listener interface and implement it on the view:

<<< @/snippets/Guides/ListenerBridge.cs#interface{csharp}

Subscribe, iterate, unsubscribe:

<<< @/snippets/Guides/ListenerBridge.cs#subscribe{csharp}

Create an entity with a listener already attached:

<<< @/snippets/Guides/ListenerBridge.cs#create{csharp}

## Calling listeners from a system

`Listeners<T>` is an ordinary component, so a system can filter on it and fetch it from a pool like any other, and call every listener with `Invoke`:

<<< @/snippets/Guides/ListenerBridge.cs#system{csharp}

`Invoke` walks a snapshot of the list taken when it starts, in subscription order (first subscribed, first called), and calls each listener at most once:

- A listener **unsubscribed** during the dispatch before its turn — by itself, by another callback or by a nested `Invoke` — is **skipped**.
- A listener **subscribed** during the dispatch is not called by it; the next dispatch sees it. One that is unsubscribed and subscribed again before its turn is still subscribed, and is called once.
- Removing the `Listeners<T>` component or destroying the entity during the dispatch clears the list, so the remaining listeners are skipped.
- "Still subscribed" uses the same comparison as `Remove` (`Equals`), and the instance found in the list is called, so a struct listener behind the interface that is removed and added again also counts as subscribed. A listener added several times is matched by value, not per copy: while one copy is in the list, every copy in the snapshot is called.

Callbacks may therefore subscribe, unsubscribe (themselves or others) and destroy the entity.

::: warning Object.Destroy is deferred
`Object.Destroy` runs `OnDestroy` at the end of the frame, not inside the callback. A view that unsubscribes in `OnDestroy` and is destroyed by an earlier callback is still in the list (and still alive) for the rest of this dispatch, so it is called. It is skipped only if it leaves the list before its turn: call `Unsubscribe` in the callback that destroys it, or destroy it with `DestroyImmediate`.
:::

While the list is unchanged the "still subscribed" check is one reference comparison per listener. After a change the check searches outward from where the previous listener was found, back and forward in turn, so a listener that unsubscribes itself (or any removal or insertion before the current one) costs a few comparisons and the dispatch stays linear; only a listener removed by another callback is searched for in the whole list, about what its `Remove` already cost. The snapshot is rented from `ArrayPool<T>.Shared` and returned cleared, nested `Invoke` calls work, and with the `TArg` overload and a `static` lambda a dispatch allocates nothing; a lambda that captures variables allocates a closure per call.

By default an exception from a listener propagates at once and the remaining listeners are not called. Pass `onError` to report it and continue:

<<< @/snippets/Guides/ListenerBridge.cs#on-error{csharp}

If `onError` itself throws, that exception propagates and the dispatch stops there; the snapshot is still cleared and returned.

::: info Changed in 2.2.0
Code that walked `Values` by hand usually went from the end, so the last subscriber was called first; `Invoke` calls the first subscriber first. If listeners depend on the order, check it when switching.
:::

::: tip Iterating Values directly
`Values` is a plain `List<T>`. Walking it from the end lets a listener unsubscribe itself from inside the callback, but a listener that unsubscribes another one with a lower index shifts the list: someone is called twice and someone is skipped. `Invoke` has no such case.
:::

## Reference

| API | Behavior |
|---|---|
| `world.Subscribe<T>(entity, listener)` | Adds `listener` to the entity's `Listeners<T>`, creating the component if it is missing. |
| `world.Unsubscribe<T>(entity, listener)` | Removes `listener`. Does nothing if the entity has no `Listeners<T>`. The component stays on the entity even when empty, so unsubscribing the last listener never auto-destroys the entity; call `Remove<Listeners<T>>` to drop it explicitly. |
| `world.HasListeners<T>(entity)` | True when the entity has a `Listeners<T>` with at least one listener. |
| `world.CreateWithListener<T>(listener)` | Shortcut for `CreateEntity` + `Subscribe`. |
| `Listeners<T>.Invoke(call, onError)` | Calls `call(listener)` for a snapshot of the listeners, in subscription order, skipping those unsubscribed before their turn. `onError == null`: an exception propagates; otherwise `onError(listener, exception)` and the dispatch continues. No-op when `Values` is `null`. |
| `Listeners<T>.Invoke(arg, call, onError)` | Same, calling `call(listener, arg)`; `onError(listener, arg, exception)` also gets the argument, so `static` lambdas for both allocate nothing. |
| `Listeners<T>.Values` | The `List<T>` of listeners. |
| `Listeners<T>.Count` | Number of listeners (0 when the list was never created). |

`T` must be a reference type (`where T : class`). Under `KENSEI_DEBUG`, `Subscribe` and `Unsubscribe` throw `InvalidOperationException` for a dead entity. When the component is removed (or the entity destroyed), its auto-reset clears and drops the list, so a recycled slot starts with no stale listeners. `CopyEntity` gives the copy its own list with the same listeners.

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
