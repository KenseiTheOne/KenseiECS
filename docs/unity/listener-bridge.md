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

`Listeners<T>` is an ordinary component, so a system can filter on it and fetch it from a pool like any other:

<<< @/snippets/Guides/ListenerBridge.cs#system{csharp}

::: tip Iterate in reverse
Walking `Values` from the end lets a listener unsubscribe itself from inside the callback without skipping the next one.
:::

## Reference

| API | Behavior |
|---|---|
| `world.Subscribe<T>(entity, listener)` | Adds `listener` to the entity's `Listeners<T>`, creating the component if it is missing. |
| `world.Unsubscribe<T>(entity, listener)` | Removes `listener`. Does nothing if the entity has no `Listeners<T>`. The component stays on the entity even when empty, so unsubscribing the last listener never auto-destroys the entity; call `Remove<Listeners<T>>` to drop it explicitly. |
| `world.HasListeners<T>(entity)` | True when the entity has a `Listeners<T>` with at least one listener. |
| `world.CreateWithListener<T>(listener)` | Shortcut for `CreateEntity` + `Subscribe`. |
| `Listeners<T>.Values` | The `List<T>` of listeners. |
| `Listeners<T>.Count` | Number of listeners (0 when the list was never created). |

`T` must be a reference type (`where T : class`). Under `KENSEI_DEBUG`, `Subscribe` and `Unsubscribe` throw `InvalidOperationException` for a dead entity. When the component is removed (or the entity destroyed), its auto-reset clears and drops the list, so a recycled slot starts with no stale listeners.

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
