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

## Inheritance

A system with injected fields can derive from another one. Both classes are `partial`, and each gets its own fields filled and its own `OnInit` called: the base first, then the derived class.

<<< @/snippets/Guides/SourceGenerator.cs#inheritance{csharp}

The base's generated `Init` becomes `virtual` (only when a subclass in the same compilation overrides it, so systems without subclasses keep the plain `Init`; a hand-written subclass with `public override void Init` counts too, also through hand-written intermediate classes that override `Init` and call `base.Init`), and the derived one overrides it and calls `base.Init` before its own fields:

<<< @/snippets/Guides/SourceGenerator.cs#inheritance-generated{csharp}

The chain works through any number of levels and through plain intermediate classes without injected fields; generic bases (`PoolSystem<T>`) work too. Registering the derived system is enough: the runner calls `Init` through `IInitSystem`, and the override runs the whole chain.

A hand-written base that implements `IInitSystem` is chained the same way when its `Init` is `public virtual` (or `abstract`, then the generated override has no `base.Init` call):

<<< @/snippets/Guides/SourceGenerator.cs#inheritance-handwritten{csharp}

The generated override always calls `base.Init(world, shared)` itself (except over an `abstract` base `Init`), so the class must not: any `base.Init` written in its own declarations, in `OnInit` or elsewhere, is `KECS012`, because it would run the base `Init` (fields and `OnInit`) a second time. The check is purely syntactic: every `base.Init` counts, whatever overload it would bind to; a `base.Init` inside a nested type belongs to that type and does not count. A hand-written class without injected fields, such as an intermediate `public override void Init` between two generated systems, calls `base.Init` as usual.

`KECS006` reports the setups that cannot be chained: a base `Init` that is not virtual, sealed, not public or implemented explicitly (`void IInitSystem.Init`), a class in between that declares its own non-override `Init` (including another overload such as `Init(World, object)` or `Init(World, SharedData, int x = 0)`: C# would bind the generated `base.Init(world, shared)` to it and the base `Init` would never run; rename it), and a base with injected fields compiled in another assembly (its generated `Init` is not virtual there unless that assembly has generated subclasses of it; keep such hierarchies in one assembly). Over a base `Init` that cannot be overridden, the 2.1 layout still works: list `IInitSystem` among the derived class's own bases and call `base.Init(world, shared)` from `OnInit`; the generated `Init` then re-implements the interface (declared `new`), and the base runs only through your call.

### Upgrading from 2.1

On 2.1 a derived system called the base `Init` itself from `OnInit` (often with `IInitSystem` listed again). Where the generator now chains the base (a generated base in the same compilation, or a hand-written `public virtual` `Init`), that call is `KECS012`: delete the line, the generated `Init` makes the call before the derived fields. The order stays the same, the base `Init` runs first.

```diff
 public partial class PoisonSystem : DamageSystemBase {
     [Inc(typeof(Health), typeof(Poisoned))] private Filter _poisoned;

     partial void OnInit(World world, SharedData shared) {
-        base.Init(world, shared);
         // derived setup
     }
 }
```

An `OnInit` that held only that call can be removed. Listing `IInitSystem` again is harmless.

## Diagnostics

All diagnostics are errors.

| Id | Cause | Fix |
|---|---|---|
| KECS001 | The class has injected fields but is not `partial`. | Add the `partial` modifier. |
| KECS002 | The class declares `Init(World, SharedData)` next to injected fields, either as a method or as an explicit `void IInitSystem.Init(...)`. | Remove `Init`; put custom setup into `partial void OnInit(World world, SharedData shared)`. |
| KECS003 | An attribute is on a field of the wrong type: `[Inc]`/`[Exc]`/`[Any]` not on `Filter`, `[Pool]` not on `ComponentPool<T>`, `[Group]` not on `Group<...>`, `[Shared]` on a value type (struct, `int`). Nullable annotations such as `Filter?` are fine. | Change the field type or the attribute. |
| KECS004 | A filter field has `[Exc]` without `[Inc]` or `[Any]`; exclude-only filters are not supported. | Add `[Inc]` or `[Any]`. |
| KECS005 | The system class is nested in a type that is not `partial`. | Add `partial` to every containing type. |
| KECS006 | A class with injected fields derives from a class whose `Init` the generated one cannot override and call (see [Inheritance](#inheritance)): the base `Init` is not `virtual`/`abstract`, is `sealed`, is not `public` or is an explicit `IInitSystem.Init`; a class in between declares its own `Init` that does not override it, or another `Init` overload that the generated `base.Init(world, shared)` would bind to; or the base has injected fields and comes from another assembly where its generated `Init` is not virtual. The message names the reason. | Make the base `Init` `public virtual`, remove the hiding `Init`, rename the overload, or keep systems that inherit injected fields in one assembly. Otherwise list `IInitSystem` in the derived class's bases and call `base.Init(world, shared)` from `OnInit`. |
| KECS007 | A filter field lists no component types in `[Inc]` or `[Any]` (for example an empty `[Inc]`). | Add at least one required or alternative type. |
| KECS008 | A type in `[Inc]`, `[Exc]` or `[Any]` is not a struct implementing `IComponent`. | List component types only. |
| KECS009 | An injected field is `readonly`; the generated `Init` assigns it. | Remove `readonly`. |
| KECS010 | A `record` or `struct` has injected fields; injection is generated only for classes. | Make the system a `partial class`. |
| KECS011 | The system class is nested in a `record` or an `interface`. | Move it into a class or struct, or to namespace level. |
| KECS012 | A derived class with injected fields writes `base.Init` (in `OnInit` or any other member of its own declarations) while its generated `Init` overrides the base `Init` and already calls it first (see [Inheritance](#inheritance)). Recognised by syntax, whatever overload the call binds to. | Remove the `base.Init` call; the base `Init` still runs first. Rename a hand-written base helper that is also called `Init`. |
