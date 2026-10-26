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

A system with injected fields can derive from another one. Both classes are `partial`, and each gets its own fields filled and its own `OnInit` called. The derived fields are filled first, then the base `Init` runs (the base fields and the base `OnInit`), then the derived `OnInit`: the base `OnInit`, and any virtual method it calls, already sees the derived fields, and the derived `OnInit` sees them all.

<<< @/snippets/Guides/SourceGenerator.cs#inheritance{csharp}

The generated `Init` is `public virtual` in every class that is not `sealed` (a `sealed` class keeps the plain `public void Init`), whether anything derives from it or not, so a subclass in another assembly, or one compiled only under a define such as `UNITY_EDITOR`, finds the same `Init` in every build. The derived class's generated `Init` overrides it and calls `base.Init` between its own fields and its `OnInit`:

<<< @/snippets/Guides/SourceGenerator.cs#inheritance-generated{csharp}

The chain works through any number of levels, through plain intermediate classes without injected fields and across assemblies; generic bases (`PoolSystem<T>`) work too. Registering the derived system is enough: the runner calls `Init` through `IInitSystem`, and the override runs the whole chain.

A hand-written base that implements `IInitSystem` (directly or through an interface that extends it, such as `IGameSystem : IInitSystem`) is chained the same way when its `Init` is `public virtual` (or `abstract`, then the generated override has no `base.Init` call):

<<< @/snippets/Guides/SourceGenerator.cs#inheritance-handwritten{csharp}

The generated override always calls `base.Init(world, shared)` itself (except over an `abstract` base `Init`), so the class must not: any `base.Init` written in its own declarations, in `OnInit` or elsewhere, is `KECS012`, because it would run the base `Init` (fields and `OnInit`) a second time. The check is purely syntactic: every `base.Init` counts, whatever overload it would bind to; a `base.Init` inside a nested type belongs to that type and does not count. A hand-written class without injected fields, such as an intermediate `public override void Init` between two generated systems, calls `base.Init` as usual: an override of a generated `Init` without that call leaves the base fields `null` and skips the base `OnInit`, which is the warning `KECS013` (also syntactic; for a base in another assembly it is recognised only by injected fields visible from the derived assembly, `protected` or `public`).

`KECS006` reports the setups that cannot be chained: a base `Init` that is not virtual, sealed, not public or implemented explicitly (`void IInitSystem.Init`, also when the class has a `public virtual void Init` next to it that the explicit one does not call: the runner calls the explicit one; hand-written overrides of that public `Init` in classes between do not change this, 2.2.1 took them for chainable and left the fields `null`; a derived class that lists `IInitSystem` itself is still chained over them, since the interface then maps to its generated `Init`), a class in between that declares its own non-override `Init` (including another overload such as `Init(World, object)`, `Init(World, SharedData, int x = 0)` or `int Init(World, SharedData)`: C# would bind the generated `base.Init(world, shared)` to it and the base `Init` would never run; rename it) or a member named `Init` that is not a method, and a base from another assembly whose `Init` is not virtual (a hand-written one, or one generated by KenseiECS 2.2.1 or older, which made it virtual only when that assembly had generated subclasses; rebuild that assembly). A generic helper such as `Init<T>(World, SharedData)` whose type parameter the two arguments cannot infer does not take the call and is allowed. When the chained base `Init` is itself a hand-written `override` (or a generated one), C# looks the call up at the class that first declares that `Init`: an overload on the chained base or on any class up to that one takes the call too and is `KECS006` as well (2.2.1 checked only the classes between and let the call go to the overload); an overload in the class with the first declaration loses to it and is allowed.

A base whose explicit `IInitSystem.Init` does a part of its own and then calls its `public virtual void Init` (`Init(world, shared)` or `this.Init(world, shared)` in the explicit one) is chained like a base with only the public one, in either member order: the runner calls the explicit `Init`, and its call reaches the generated override, so the order is the base's own part, the derived fields, the base `Init`, the derived `OnInit`. 2.2.0 and 2.2.1 chained it only when the public `Init` came first in the class. The call is read from source, so for a base in another assembly it is still `KECS006`, also with hand-written overrides of the public `Init` between it and the derived class.

Over a base `Init` that cannot be overridden, the 2.1 layout still works: list `IInitSystem` (or an interface that extends it) among the derived class's own bases and call `base.Init(world, shared)` from `OnInit`; the generated `Init` then re-implements the interface (declared `new`), and the base runs only through your call. A class in this layout without a `base.Init` call is `KECS006` when the base `Init` would never run: when that `Init` is the one the runner reaches through `IInitSystem.Init` (also a `sealed` or `protected` override in a class between, which 2.2.1 missed, and a `new Init` whose class lists `IInitSystem` itself), or when a base has injected fields (of a base in another assembly only the `protected` and `public` ones are seen, as for `KECS013`, so a base there with only `private` injected fields is not reported). Not reported: an explicit base `IInitSystem.Init`, which `base.Init` cannot reach anyway, and a `new Init` in between that does not implement `IInitSystem.Init`, with no injected fields above it. Such an `Init` is taken for an unrelated method: the generated `Init` replaces the interface's `Init` without a diagnostic, and the `Init` it hides does not run (a `base.Init` in `OnInit` would call the `new` one, not the hidden one).

### A base with its own `OnInit`

A hand-written base may already have an `OnInit(World, SharedData)` of its own, typically a virtual hook that its `Init` calls. The generated part then declares no `partial void OnInit` over it (that would hide the hook). Override the hook, and it runs once, after the derived fields are filled: from the base `Init`, when the generated `Init` calls one that can call the hook (the hook is declared in that base or in one of its bases), and from the generated `Init` otherwise — over a base that is not a system, over an `abstract` base `Init`, or when the hook is declared in a hand-written class between a generated base and the derived class. 2.2.1 did not compile those three cases (CS0111). In the 2.1 layout (the derived class lists `IInitSystem` over a base `Init` that cannot be overridden) an override of a hook that this base `Init` can call is `KECS006`: the generated `Init` would call it, and the `base.Init` that `OnInit` must call would run it again; make the base `Init` `public virtual`, or put the setup into `partial void OnInit` instead of overriding the hook.

<<< @/snippets/Guides/SourceGenerator.cs#inheritance-hook{csharp}

A `partial void OnInit` written in the derived class anyway is still declared and runs after the base `Init`, next to the hook. An `OnInit(World world, SharedData shared)` written without `partial` (in any class with injected fields) is called by the generated `Init` like the partial one, unless it overrides a hook that the chained base `Init` calls; a generated base can offer such a `protected virtual` hook to its subclasses.

### Upgrading from 2.1

On 2.1 a derived system called the base `Init` itself from `OnInit` (often with `IInitSystem` listed again). Where the generator now chains the base (a generated base, or a hand-written `public virtual` `Init`), that call is `KECS012`: delete the line. The generated `Init` makes the call after the derived fields and before the derived `OnInit`, so the base `Init` still sees the derived fields, as it did when the call stood at the top of `OnInit`.

```diff
 public partial class PoisonSystem : DamageSystemBase {
     [Inc(typeof(Health), typeof(Poisoned))] private Filter _poisoned;

     partial void OnInit(World world, SharedData shared) {
-        base.Init(world, shared);
         // derived setup
     }
 }
```

An `OnInit` that held only that call can be removed. Code that stood in `OnInit` before the call now runs after the base `Init`; what the base needs earlier belongs in the constructor or in a virtual method the base calls. Listing `IInitSystem` again is harmless. Over a base `Init` that cannot be overridden (not virtual, or from an assembly built by 2.2.1 or older) keep the call: that is the 2.1 layout, and without the call it is `KECS006`.

### Upgrading from 2.2.0 and 2.2.1

- A derived system in another assembly than its generated base is now chained there too (the base `Init` is virtual), so a `base.Init` call kept in its `OnInit` for the 2.1 layout is `KECS012`: delete it.
- The derived fields are filled before the base `Init` instead of after it, and the base `OnInit` sees them. A `[Shared]` field of the derived class is therefore read before the base `Init` runs: an object that the base `OnInit` adds to `SharedData` is not there yet, add it when building `SharedData`.
- The `Init` of a class that is not `sealed` is `virtual` even without subclasses: a hand-written subclass that declares `public void Init(World, SharedData)` without `override` or `new` gets CS0114 instead of CS0108 (both warnings; it still hides the base `Init`).

## Diagnostics

All diagnostics are errors, except the warning KECS013.

| Id | Cause | Fix |
|---|---|---|
| KECS001 | The class has injected fields but is not `partial`. | Add the `partial` modifier. |
| KECS002 | The class declares `Init(World, SharedData)` next to injected fields, either as a method or as an explicit `void IInitSystem.Init(...)`. | Remove `Init`; put custom setup into `partial void OnInit(World world, SharedData shared)`. |
| KECS003 | An attribute is on a field of the wrong type: `[Inc]`/`[Exc]`/`[Any]` not on `Filter`, `[Pool]` not on `ComponentPool<T>`, `[Group]` not on `Group<...>`, `[Shared]` on a value type (struct, `int`). Nullable annotations such as `Filter?` are fine. | Change the field type or the attribute. |
| KECS004 | A filter field has `[Exc]` without `[Inc]` or `[Any]`; exclude-only filters are not supported. | Add `[Inc]` or `[Any]`. |
| KECS005 | The system class is nested in a type that is not `partial`. | Add `partial` to every containing type. |
| KECS006 | A class with injected fields derives from a class whose `Init` the generated one cannot override and call (see [Inheritance](#inheritance)): the base `Init` is not `virtual`/`abstract`, is `sealed`, is not `public` or is an explicit `IInitSystem.Init` (also next to a `public virtual void Init` that it does not call, with or without hand-written overrides of that `Init` in between); a class in between declares its own `Init` that does not override it, another `Init` overload that the generated `base.Init(world, shared)` would bind to (on a class in between, or on the chained base and above it up to the class that first declares the overridden `Init`), or a member named `Init` that is not a method; or the base comes from another assembly where its `Init` is not virtual (generated there by 2.2.1 or older without subclasses). Also a class that lists `IInitSystem` itself over such a base (the 2.1 layout) and has no `base.Init` call, so the base `Init` would never run (injected fields of a base in another assembly count only when `protected` or `public`), or that overrides a base `OnInit` hook this base `Init` can call. The message names the reason. | Make the base `Init` `public virtual`, remove the hiding `Init` or the explicit implementation, rename the overload or member, or rebuild the other assembly with this version. Otherwise list `IInitSystem` in the derived class's bases and call `base.Init(world, shared)` from `OnInit`. |
| KECS007 | A filter field lists no component types in `[Inc]` or `[Any]` (for example an empty `[Inc]`). | Add at least one required or alternative type. |
| KECS008 | A type in `[Inc]`, `[Exc]` or `[Any]` is not a struct implementing `IComponent`. | List component types only. |
| KECS009 | An injected field is `readonly`; the generated `Init` assigns it. | Remove `readonly`. |
| KECS010 | A `record` or `struct` has injected fields; injection is generated only for classes. | Make the system a `partial class`. |
| KECS011 | The system class is nested in a `record` or an `interface`. | Move it into a class or struct, or to namespace level. |
| KECS012 | A derived class with injected fields writes `base.Init` (in `OnInit` or any other member of its own declarations) while its generated `Init` overrides the base `Init` and already calls it, between the derived fields and `OnInit` (see [Inheritance](#inheritance)). Recognised by syntax, whatever overload the call binds to. | Remove the `base.Init` call; the base `Init` still runs before `OnInit`. Rename a hand-written base helper that is also called `Init`. |
| KECS013 | Warning. A hand-written class overrides `Init(World, SharedData)` of a generated base (directly or through hand-written overriding classes) and has no `base.Init` in its own declarations, so the generated `Init` never runs: the base fields stay `null` and the base `OnInit` is skipped. Recognised by syntax; a generated base in another assembly is recognised only by its `protected` or `public` injected fields. | Call `base.Init(world, shared)` from the override. If replacing the base `Init` is intended (a test double, for example), suppress the warning with `#pragma warning disable KECS013`. |
