# Snapshots

`WorldSerializer` writes every alive entity and component to a stream and restores them into an empty world. Entities keep their index and generation, so `Entity` fields inside components remain valid; component types are identified by name, not by runtime index.

<<< @/snippets/Guides/Snapshots.cs#save-load{csharp}

`Save` leaves the stream open. `Load` requires an empty world (`EntityCount == 0`, e.g. after `world.Clear()`) and throws otherwise; it also throws when the stream is not a snapshot, has an unsupported version, or names a component type that is not loaded in the process. The world tick is saved and restored.

Component types are stored by assembly-qualified name. If that name no longer resolves (for example after an assembly version bump), `Load` falls back to the namespace-qualified name.

## Components with references

Unmanaged components are written bit-for-bit. A component that holds references (a `List`, a `string`, an object) needs an `IComponentFormatter<T>`, otherwise `Save` throws.

<<< @/snippets/Guides/Snapshots.cs#formatter{csharp}

`Write` receives the component by `ref`; `Read` must assign it through `out`. The formatter owns the binary layout, so keep `Write` and `Read` symmetric.

## Registering types

`Register<T>()` without a formatter lets `Load` reach the pool through a direct `Pool<T>()` call instead of reflection, which matters under IL2CPP for types that no code touches before loading.

<<< @/snippets/Guides/Snapshots.cs#register-unmanaged{csharp}

`Register(formatter)` does the same for the formatted type.

## Events during Load

`Load` (like [`Warmup`](./world-lifecycle.md#warmup)) fires no `IWorldEventListener` events and records nothing in the profiler; filter listeners and pool listeners do fire, since the components go through the normal `Add` path. Filters and groups fill normally.
