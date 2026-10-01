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

Nested system classes work as long as every containing type is partial too:

<<< @/snippets/Guides/SourceGenerator.cs#nested{csharp}

## Diagnostics

All diagnostics are errors.

| Id | Cause | Fix |
|---|---|---|
| KECS001 | The class has injected fields but is not `partial`. | Add the `partial` modifier. |
| KECS002 | The class declares `Init(World, SharedData)` next to injected fields. | Remove `Init`; put custom setup into `partial void OnInit(World world, SharedData shared)`. |
| KECS003 | An attribute is on a field of the wrong type: `[Inc]`/`[Exc]`/`[Any]` not on `Filter`, `[Pool]` not on `ComponentPool<T>`, `[Group]` not on `Group<...>`. | Change the field type or the attribute. |
| KECS004 | A filter field has `[Exc]` without `[Inc]` or `[Any]`; exclude-only filters are not supported. | Add `[Inc]` or `[Any]`. |
| KECS005 | The system class is nested in a type that is not `partial`. | Add `partial` to every containing type. |
