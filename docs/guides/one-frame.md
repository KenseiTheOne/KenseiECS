# OneFrame Components

A one-frame component is an event: it is added during the frame and removed automatically at the end of the runner's `Run`.

<<< @/snippets/Guides/OneFrame.cs#event-type{csharp}

Register the type on the [runner](../concepts/runner.md):

<<< @/snippets/Guides/OneFrame.cs#register{csharp}

<<< @/snippets/Guides/OneFrame.cs#produce{csharp}

## Ordering

`OneFrame<T>` removes at the end of `Run`, so a producer must run **before** its consumers; an event created after the consumer ran is removed unseen. The cleanup runs even when a system throws.

Use `DelHere<T>()` to put the cleanup at a specific point of the pipeline. Systems registered before it see the components; systems after it do not.

<<< @/snippets/Guides/OneFrame.cs#del-here{csharp}

::: info Nested runners
Each runner cleans its own `OneFrame` components at the end of its own `Run`. See [nested runners](../concepts/runner.md#nested-runners). By default a paused runner — a group whose slot is disabled, or a disabled phase — skips that cleanup, so its one-frame components outlive the frame while it is paused. Register the type with `OneFrame<T>(cleanWhenDisabled: true)` to remove it in the paused frames too: at the skipped group's slot in the parent's pipeline, or in `Run()` of the disabled phase. See [disabled runners](../concepts/runner.md#disabled-runners-and-their-oneframe-components).
:::

## Event entities

An entity whose only component is a one-frame component is auto-destroyed by the cleanup, which makes "event entities" free.

<<< @/snippets/Guides/OneFrame.cs#event-entity{csharp}

## Several events per entity per frame

A plain component holds one value per entity, and `Add` throws on the second one. `EventBuffer<T>` collects any number:

<<< @/snippets/Guides/OneFrame.cs#add-event{csharp}

`AddEvent` appends to the entity's `EventBuffer<T>`, adding the component on the first call. `T` is any struct; it does not have to implement `IComponent`.

<<< @/snippets/Guides/OneFrame.cs#read-events{csharp}

<<< @/snippets/Guides/OneFrame.cs#register-buffer{csharp}

The `Values` lists are pooled: removing the component returns its list to the pool, so a `OneFrame<EventBuffer<T>>` registration stops allocating once the pool has reached its peak — as many lists as the most entities that held a buffer of that type at once, each grown to the most events one entity received.

### Prewarming the list pool

`World.Warmup` does not fill the pool: it adds a default `EventBuffer<T>`, whose `Values` is `null`, so it rents no list. Without prewarming, a frame with more buffered entities than ever before allocates new lists, and a list that receives more events than its capacity grows. `EventBuffer<T>.Prewarm(lists, capacity)` fills the pool up front:

<<< @/snippets/Guides/OneFrame.cs#prewarm{csharp}

Afterwards the next `lists` lists the pool hands out have `Capacity` of at least `capacity`: free lists already that large are used first, then the largest of the remaining free lists are grown, and missing ones are created. At most `lists` lists are grown, so a pool that once peaked at many more lists is not inflated as a whole. A repeated call with the same arguments does nothing. Lists rented by live buffers are not counted, so call it at load, before the first frame. With the pool prewarmed for the peak, a `OneFrame<EventBuffer<T>>` frame allocates nothing as long as no more than `lists` entities hold a buffer and none receives more than `capacity` events; past that the pool grows on demand as before.

::: warning The pool is static and shared
The pool is one per event type `T`, static, and shared by every world in the process: one `Prewarm` covers all worlds, and they compete for the same lists. It is not thread-safe. Lists come back in last-in, first-out order, so with lists of mixed capacity a small one can still be handed to an entity that receives many events and grow again. `Prewarm` puts the lists it prepared on top, so they are handed out first after the call.
:::

::: tip Replacing a warmup that created entities
`Prewarm` fills only the list pool. A warmup that spawned a crowd of entities with buffers also grew the world's entity storage and the pools and filters along the way; to keep that, add [`world.Reserve(entities, components)`](./world-config.md#reserving-capacity-later) next to the `Prewarm` call.
:::
