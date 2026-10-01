# Systems & SharedData

## Systems

A system is a class implementing any combination of three interfaces. The [runner](./runner.md) detects which ones it implements when you `Add` it:

<<< @/snippets/Concepts/Systems.cs#system{csharp}

| Interface | Method | Called |
|---|---|---|
| `IInitSystem` | `Init(World, SharedData)` | once, from `SystemsRunner.Init()` |
| `IRunSystem` | `Run(World)` | every frame, from `SystemsRunner.Run()` |
| `IDestroySystem` | `Destroy(World)` | once, from `SystemsRunner.Destroy()` |

Do the setup work in `Init`: build [filters](./filters.md), cache [pools](./components.md#access), fetch services from `SharedData`. `Run` then only iterates.

::: tip Generated Init
Mark the fields of a `partial` system class with `[Inc]`, `[Exc]`, `[Any]`, `[Pool]`, `[Group]` or `[Shared]` and the source generator writes `Init` for you. See [Generated Init](../guides/source-generator.md).
:::

## SharedData

Typed container for shared services. No reflection, explicit access. Register instances before the runner initializes and pass the container to the root runner:

<<< @/snippets/Concepts/Systems.cs#shared-setup{csharp}

Systems receive it in `Init`:

<<< @/snippets/Concepts/Systems.cs#shared-get{csharp}

- Entries are keyed by type and an optional string key, so several instances of one type can coexist (`"enemies"`, `"pickups"`). Adding again under the same type and key replaces the instance.
- The type is the generic argument of `Add<T>`, not the runtime type of the instance: an object added as `Add<IService>(impl)` is found by `Get<IService>()`, not by `Get<Impl>()`.
- `Get` throws `KeyNotFoundException` when nothing is registered; `TryGet` returns false instead.
- Values must be reference types (`where T : class`).
- A nested runner constructed without `SharedData` inherits its parent's (see [nested runners](./runner.md#nested-runners)).
