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

`Spawn` creates the entity from the providers in component order, binds the view to it and returns it. It throws `InvalidOperationException` when the GameObject has no provider. A listener that the new components trigger (a pool listener's `OnAdded`, a world listener's `OnEntityCreated`) may `Spawn` another view before the outer call returns, for example the weapon of an enemy that is being spawned: every call works on its own list of providers, so both entities get all their components. Up to 2.2.1 the nested call reused the outer call's list, and the outer entity lost the components of its remaining providers. A listener that destroys the entity being spawned (a spawn limit, a rule that destroys entities without health) ends the `Spawn`: the remaining providers are skipped, the view is bound to the dead entity, and its handle is returned, so check `IsAlive`. Up to 2.2.1 their components went into the freed slot, where the next entity inherited them, and under `KENSEI_DEBUG` the next provider threw. If **Entity Name** is set, the entity gets it as its [debug name](../concepts/entities.md#debug-names) — without `KENSEI_DEBUG` `SetName` is an empty method, so in release the name costs one empty call: the call is not compiled out, but the name is a serialized string, so nothing is built or allocated for it.

## API

| Member | Description |
|---|---|
| `Bind(World world, Entity entity)` | Bind the view to a world and an entity. |
| `Spawn(World world)` | Create an entity from the providers on this GameObject, bind the view to it and return it. Can be called again from a listener the new components trigger. |
| `Unbind(bool destroyEntity = false)` | Unbind; with `destroyEntity: true` also destroys the entity if it is still alive. |
| `World` | The world the view is bound to. |
| `Entity` | The entity the view is bound to (`Entity.Null` after `Unbind`). |
| `IsAlive` | True when the view is bound, the world is not destroyed and the entity is alive. |
| `DestroyEntityWithGameObject` | Destroy the bound entity when the GameObject is destroyed. Off by default; inspector checkbox **Destroy Entity With Game Object**. |
| `EntityName` | Debug name `Spawn` gives the entity (`KENSEI_DEBUG` only). Set it before `Spawn` to name entities from code; inspector field **Entity Name**. |

Only one view per GameObject is allowed (`[DisallowMultipleComponent]`).

## Inspector

With [Debug Mode](./debug-tools.md#debug-mode) on, the `EcsEntityView` inspector shows the bound entity's components with editable fields, nested structs and lists. `Entity` fields are clickable: they navigate to that entity, with **Back** / **Forward** history and a **Home** button that returns to the view's own entity. Dead entities are shown greyed out as `[Not Alive]`. Without a bound world the inspector shows "No World bound."
