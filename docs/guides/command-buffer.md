# CommandBuffer

Records structural changes and applies them later, in order. Use it inside filter loops and nested loops where changing other entities is unsafe.

<<< @/snippets/Guides/CommandBuffer.cs#collisions{csharp}

Keep one buffer per system: payloads are stored per component type without boxing, and after warmup the buffer allocates nothing.

## Commands

| Command | At playback |
|---|---|
| `CreateEntity<T>(T)` | Creates an entity with that component. Returns a `PendingEntity` usable as a target for later commands in the same buffer. |
| `Add<T>(entity, T)` | Throws if the component exists, like `World.Add`. |
| `Set<T>(entity, T)` | Adds or overwrites. |
| `Remove<T>(entity)` | No-op if the component is absent. |
| `DestroyEntity(entity)` | No-op if the entity is already dead. |

Every command except `CreateEntity` accepts either an `Entity` or a `PendingEntity`.

<<< @/snippets/Guides/CommandBuffer.cs#pending{csharp}

- Commands whose `Entity` is dead at playback are skipped, so "destroy if still alive" needs no check.
- If a command throws, the rest of the buffer is discarded.
- `Playback` clears the buffer; `Clear()` discards recorded commands without applying them, and `Count` returns the number of recorded commands.
