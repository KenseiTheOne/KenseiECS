# Entities

An entity is an 8-byte handle, `Entity`: an `int Index` (the slot) plus an `int Generation`. It owns no data itself; its components live in per-type pools.

## Creating and destroying

<<< @/snippets/Concepts/Entities.cs#create{csharp}

**Auto-destroy:** entities are destroyed when their last component is removed. That is also why `CreateEntity` takes a component: an entity is alive if and only if it has at least one.

`DestroyEntity` of a dead entity is a no-op. `CopyEntity` creates a new entity with copies of all components of the source (by value, or through [`IAutoCopy`](./components.md#iautocopy)); on a dead source it returns `Entity.Null` in release and throws under `KENSEI_DEBUG`. When a [listener](../guides/component-listeners.md) of a copied component destroys the copy, copying stops and the returned handle is dead, so check `IsAlive` if your listeners can do that; the same holds for `CreateEntity` when a listener of its component destroys the new entity. A source that has no components left, which a world or filter listener of the removal of its last component sees before the auto-destroy, is not copied: `CopyEntity` returns `Entity.Null` in every build (up to 2.2.1 it created an alive entity without components, which no filter saw and only an explicit `DestroyEntity` removed).

## Handles vs. indices

[Filters](./filters.md) yield `int` slot indices; `World` methods take `Entity` handles. An `Entity` carries a generation and stays safe forever: once its entity is destroyed, `IsAlive` is false, and after the slot is reused by another entity it still does not match. An `int` index has no generation: it is valid only until the end of the current iteration.

::: warning
Store `Entity` handles, never `int`s.
:::

<<< @/snippets/Concepts/Entities.cs#handles{csharp}

`GetEntity(int)` on a dead slot returns the handle of the entity that last lived there; `IsAlive` is false for it. Once the slot is reused, the same index names the new entity.

When you are not sure an index refers to a live entity, `TryGetEntity` checks first; it is safe for any index:

<<< @/snippets/Concepts/Entities.cs#try-get{csharp}

## Debug names

<<< @/snippets/Concepts/Entities.cs#names{csharp}

Names show up in the World Inspector and Profiler (see [Debug Tools](../unity/debug-tools.md)), and are dropped when the entity is destroyed. Without `KENSEI_DEBUG`, `SetName` does nothing and `GetName` always returns `null`. The call is not removed: its arguments are still evaluated, so `world.SetName(world.CreateEntity(...), "Player")` creates the entity either way.
