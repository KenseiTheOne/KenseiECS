# EcsEntityView

`EcsEntityView` is a `MonoBehaviour` that links a GameObject to an entity. Either bind it to an entity you already created, or author the entity with [`EcsComponentProvider`](./bootstrap.md#authoring-components-in-the-inspector) components on the same GameObject and call `Spawn`.

## Binding

```csharp
var view = Instantiate(prefab).GetComponent<EcsEntityView>();
view.Bind(world, entity);

// Optional: destroy the bound entity in OnDestroy (off by default).
// Also available as a checkbox in the inspector.
view.DestroyEntityWithGameObject = true;
```

With the flag enabled, `OnDestroy` destroys the entity only if the world is still alive and the entity handle is valid.

## Spawning from providers

```csharp
var view = Instantiate(prefab);          // prefab: EcsEntityView + EcsComponentProvider<T> components
view.EntityName = "Enemy";               // optional, overrides the inspector value
Entity entity = view.Spawn(world);       // create the entity from the providers and bind the view
```

`Spawn` creates the entity from the providers in component order, binds the view to it and returns it. It throws `InvalidOperationException` when the GameObject has no provider. If **Entity Name** is set, the entity gets it as its [debug name](../concepts/entities.md#debug-names) — `SetName` is compiled out without `KENSEI_DEBUG`, so the name costs nothing in release.

## API

| Member | Description |
|---|---|
| `Bind(World world, Entity entity)` | Bind the view to a world and an entity. |
| `Spawn(World world)` | Create an entity from the providers on this GameObject, bind the view to it and return it. |
| `Unbind(bool destroyEntity = false)` | Unbind; with `destroyEntity: true` also destroys the entity if it is still alive. |
| `World` | The world the view is bound to. |
| `Entity` | The entity the view is bound to (`Entity.Null` after `Unbind`). |
| `IsAlive` | True when the view is bound, the world is not destroyed and the entity is alive. |
| `DestroyEntityWithGameObject` | Destroy the bound entity when the GameObject is destroyed. Off by default; inspector checkbox **Destroy Entity With Game Object**. |
| `EntityName` | Debug name `Spawn` gives the entity (`KENSEI_DEBUG` only). Set it before `Spawn` to name entities from code; inspector field **Entity Name**. |

Only one view per GameObject is allowed (`[DisallowMultipleComponent]`).

## Inspector

With [Debug Mode](./debug-tools.md#debug-mode) on, the `EcsEntityView` inspector shows the bound entity's components with editable fields, nested structs and lists. `Entity` fields are clickable: they navigate to that entity, with **Back** / **Forward** history and a **Home** button that returns to the view's own entity. Dead entities are shown greyed out as `[Not Alive]`. Without a bound world the inspector shows "No World bound."
