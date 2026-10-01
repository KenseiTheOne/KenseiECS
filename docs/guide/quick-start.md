# Quick Start

A complete console program: two components, one system that moves every entity with a `Position` and a `Velocity`, and a runner that drives it. It runs as-is on .NET once the project references KenseiECS (see [Installation](./installation.md)).

<<< @/snippets/Concepts/QuickStart.cs#quickstart{csharp}

Output:

```
Position: (1, 2)
```

## What happens here

1. **Components** are plain structs implementing `IComponent`. See [Components](../concepts/components.md).
2. **The system** builds a filter and caches the two pools in `Init`, then walks the filter in `Run`. The filter yields `int` entity indices; `ComponentPool<T>.Get` returns the component by `ref`, so the writes land in the pool. See [Filters](../concepts/filters.md) and [Systems](../concepts/systems.md).
3. **The runner** calls `Init` once, `Run` every frame and `Destroy` on shutdown. See [SystemsRunner](../concepts/runner.md).
4. **The entity** is created with its first component; an entity always has at least one. See [Entities](../concepts/entities.md).

In Unity, derive from `EcsBootstrap` instead of writing `Main`: it owns the world and drives the runners from `Update`, `FixedUpdate` and `LateUpdate`. See [Bootstrap & Authoring](../unity/bootstrap.md).

::: tip
Writing `Init` by hand is optional: mark the fields and let the [source generator](../guides/source-generator.md) fill them.
:::
