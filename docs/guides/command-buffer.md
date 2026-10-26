# CommandBuffer

Records structural changes and applies them later, in order. Use it inside filter loops and nested loops where changing other entities is unsafe.

<<< @/snippets/Guides/CommandBuffer.cs#collisions{csharp}

Keep one buffer per system: payloads are stored per component type without boxing, and after warmup the buffer allocates nothing.

## Commands

| Command | At playback |
|---|---|
| `CreateEntity<T>(T)` | Creates an entity with that component. Returns a `PendingEntity` usable as a target for later commands in the same buffer. |
| `Add<T>(entity, T)` | Throws if the component exists, like `World.Add`. |
| `Set<T>(entity, T)` | Adds or overwrites. An overwrite is a plain write that [change tracking](change-tracking.md) counts as a change; `AutoReset` does not run on the old value, so writing back a modified copy that shares the old value's references keeps them. Typed listeners see `OnAdded` only when the component was missing. Not for adding events, see below. |
| `Remove<T>(entity)` | No-op if the component is absent. |
| `DestroyEntity(entity)` | No-op if the entity is already dead. |

Every command except `CreateEntity` accepts either an `Entity` or a `PendingEntity`.

<<< @/snippets/Guides/CommandBuffer.cs#pending{csharp}

- Commands whose `Entity` is dead at playback are skipped, so "destroy if still alive" needs no check.
- If a command throws, the rest of the buffer is discarded.
- `Playback` clears the buffer; `Clear()` discards recorded commands without applying them, and `Count` returns the number of recorded commands.

::: warning Events and Set
Do not record events as `var b = world.Get<EventBuffer<Hit>>(e); b.Add(h); cb.Set(e, b);` (2.1.1 suggested it). The copy shares the entity's list, so the event is added at once, not at playback; and if the buffer is removed before `Playback` — a OneFrame cleanup of a nested runner, a `DelHere`, a `Remove` recorded earlier in the same buffer — the copy is stale and its events are gone. `Set` then stores an empty buffer, or keeps the entity's current one, rather than a list that went back to the pool (which 2.2.1 and earlier did, handing one list to two entities). Call `world.AddEvent(e, h)` directly; to defer it, keep the `(Entity, Hit)` pairs yourself and call `AddEvent` after `Playback`. See [copies of a buffer](./one-frame.md#several-events-per-entity-per-frame).
:::
