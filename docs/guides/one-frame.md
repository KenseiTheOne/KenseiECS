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
Each runner cleans its own `OneFrame` components at the end of its own `Run`. See [nested runners](../concepts/runner.md).
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

The `Values` lists are pooled: removing the component returns its list to the pool, so a `OneFrame<EventBuffer<T>>` registration allocates nothing after warmup.
