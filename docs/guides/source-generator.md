# Generated Init

The package ships a Roslyn source generator (`Plugins/KenseiECS.Generators.dll`, picked up by Unity 2021.2+ and by any .NET project that references the generator). Mark the fields of a **partial** system class and the generator writes `Init` for you:

<<< @/snippets/Guides/SourceGenerator.cs#system{csharp}

The generated partial adds `IInitSystem` (unless the class already declares it), so the runner calls the generated `Init` like any other:

<<< @/snippets/Guides/SourceGenerator.cs#usage{csharp}

## What gets generated

The generated `Init` fills the marked fields in declaration order, then calls `OnInit`. For `MovementSystem` it is equivalent to:

<<< @/snippets/Guides/SourceGenerator.cs#equivalent{csharp}

| Attribute | Field type | Injected with |
|---|---|---|
| `[Inc(...)]`, `[Exc(...)]`, `[Any(...)]` | `Filter` | `world.Filter().Inc<...>().Exc<...>().Any<...>().End()` |
| `[Pool]` | `ComponentPool<T>` | `world.Pool<T>()` |
| `[Group]` | `Group<...>` | `world.Group<...>()` |
| `[Shared]` / `[Shared("key")]` | any | `shared.Get<T>()` / `shared.Get<T>("key")` — see [SharedData](../concepts/systems.md) |

`[Inc]`, `[Exc]` and `[Any]` take a list of component types and combine on one field. `OnInit` is optional: if you do not implement it, the call is removed by the compiler. Static fields are ignored.

Nested system classes work as long as every containing type is a partial class or struct (not a record or interface):

<<< @/snippets/Guides/SourceGenerator.cs#nested{csharp}

## Diagnostics

All diagnostics are errors.

| Id | Cause | Fix |
|---|---|---|
| KECS001 | The class has injected fields but is not `partial`. | Add the `partial` modifier. |
| KECS002 | The class declares `Init(World, SharedData)` next to injected fields, either as a method or as an explicit `void IInitSystem.Init(...)`. | Remove `Init`; put custom setup into `partial void OnInit(World world, SharedData shared)`. |
| KECS003 | An attribute is on a field of the wrong type: `[Inc]`/`[Exc]`/`[Any]` not on `Filter`, `[Pool]` not on `ComponentPool<T>`, `[Group]` not on `Group<...>`, `[Shared]` on a value type (struct, `int`). Nullable annotations such as `Filter?` are fine. | Change the field type or the attribute. |
| KECS004 | A filter field has `[Exc]` without `[Inc]` or `[Any]`; exclude-only filters are not supported. | Add `[Inc]` or `[Any]`. |
| KECS005 | The system class is nested in a type that is not `partial`. | Add `partial` to every containing type. |
| KECS006 | A class with injected fields derives from a class that has injected fields or implements `IInitSystem`, and one of the two `Init` methods would never run: the derived class does not list `IInitSystem` among its own bases while the base implements it, or the base has injected fields and `OnInit` does not call `base.Init`. | Keep the injected fields and `Init` in one class, or list `IInitSystem` in the derived class's bases and call `base.Init(world, shared)` from `OnInit`. |
| KECS007 | A filter field lists no component types in `[Inc]` or `[Any]` (for example an empty `[Inc]`). | Add at least one required or alternative type. |
| KECS008 | A type in `[Inc]`, `[Exc]` or `[Any]` is not a struct implementing `IComponent`. | List component types only. |
| KECS009 | An injected field is `readonly`; the generated `Init` assigns it. | Remove `readonly`. |
| KECS010 | A `record` or `struct` has injected fields; injection is generated only for classes. | Make the system a `partial class`. |
| KECS011 | The system class is nested in a `record` or an `interface`. | Move it into a class or struct, or to namespace level. |
