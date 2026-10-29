<p align="center"><img src="docs/public/logo.png" width="128" alt="KenseiECS logo: a samurai kabuto helmet"></p>

# KenseiECS

[![CI](https://github.com/KenseiTheOne/KenseiECS/actions/workflows/ci.yml/badge.svg)](https://github.com/KenseiTheOne/KenseiECS/actions/workflows/ci.yml)

Lightweight, sparse-set Entity Component System for Unity and .NET.

**Documentation: <https://kenseitheone.github.io/KenseiECS/>**

**Live demo: [Horde](https://kenseitheone.github.io/KenseiECS/guide/demo)** — a survivors-like game on KenseiECS running in the browser (.NET WebAssembly), up to 20,000+ entities.

## Features

- **Sparse Set storage** — O(1) component access, dense arrays for cache-friendly iteration
- **Generational entities** — 8-byte Entity (int Index + int Generation) with aliasing protection
- **Reactive filters** — cached query results, updated automatically on component changes
- **Zero-allocation iteration** — struct enumerator, reverse iteration safe for structural changes
- **Auto-destroy** — entities without components are destroyed automatically
- **IAutoReset** — custom cleanup on component remove
- **IAutoCopy** — custom deep-copy logic for CopyEntity
- **SharedData** — typed container for shared services, no reflection
- **OneFrame components** — auto-removed event components, end-of-frame or positional (`DelHere`)
- **Nested system runners** — separate groups for Update/FixedUpdate/LateUpdate
- **Named systems** — enable/disable systems and phases at runtime
- **World events** — IWorldEventListener for lifecycle notifications, type indices resolve to `Type`
- **Exception-safe lifecycle** — a throwing listener or system releases entity slots and restores runner and buffer state; only a throw inside `DestroyEntity` or a snapshot `Load` can leave components behind, and `Clear()` recovers
- **Debug validation** — dead/stale handle misuse throws under KENSEI_DEBUG, zero cost in release
- **Listener bridge** — clean ECS <-> Unity MonoBehaviour communication, no delegates
- **Editor tools** — World Inspector, Profiler, EcsEntityView with navigation (under KENSEI_DEBUG)
- **Scales to thousands of component types** — multi-word bitmasks, per-type filter lists

## Install

**Unity (2021.3+)** — Package Manager -> Add package from git URL:

```
https://github.com/KenseiTheOne/KenseiECS.git?path=/KenseiECS
```

Pin a version with `#v2.4.0`.

**.NET (netstandard2.1 / net8.0)** — reference `KenseiECS.NET/KenseiECS.csproj` to build from source, plus `KenseiECS/Plugins/KenseiECS.Generators.dll` as an analyzer for the [generated `Init`](https://kenseitheone.github.io/KenseiECS/guides/source-generator).

Details: [Installation](https://kenseitheone.github.io/KenseiECS/guide/installation).

## Performance

10,000 entities, .NET 8 x64 RyuJIT, BenchmarkDotNet, zero allocations at runtime once pools, filters and buffers have grown to their peak capacity (one machine; the other frameworks measured in September 2026, KenseiECS 2.4.0 scaled from the same session by an October 2026 comparison with 2.0, see [BENCHMARKS](BENCHMARKS.md)):

| Operation | KenseiECS | LeoEcsLite | Arch |
|---|---:|---:|---:|
| Iteration (2 comp), filter | 13.8 us | 14.0 us | **5.4 us** |
| Iteration (2 comp), owning group | 5.5 us | — | **5.4 us** |
| Entity creation (2 comp) | 292 us | 344 us | **208 us** |
| Structural changes (add+remove) | 87 us | **73 us** | 590 us |
| Game loop (mixed frame) | **35 us** | 40 us | 78 us |

**Bold** = best in row. Unity's Mono and IL2CPP backends and ARM devices were not measured, and no scenario covers `EventBuffer<T>` or `OneFrame<T>`. [Full benchmarks with analysis and caveats](BENCHMARKS.md) ([on the site](https://kenseitheone.github.io/KenseiECS/benchmarks)). Run them yourself with `dotnet run -c Release --project Benchmark`.

## Quick Start

```csharp
using System;
using KenseiECS;

struct Position : IComponent { public float X, Y; }
struct Velocity : IComponent { public float X, Y; }

class MovementSystem : IInitSystem, IRunSystem {
    Filter _filter;
    ComponentPool<Position> _positions;
    ComponentPool<Velocity> _velocities;

    public void Init(World world, SharedData shared) {
        _filter = world.Filter().Inc<Position>().Inc<Velocity>().End();
        _positions = world.Pool<Position>();
        _velocities = world.Pool<Velocity>();
    }

    public void Run(World world) {
        foreach (int e in _filter) {
            ref var pos = ref _positions.Get(e);
            ref var vel = ref _velocities.Get(e);
            pos.X += vel.X;
            pos.Y += vel.Y;
        }
    }
}

static class Program {
    static void Main() {
        var world = new World();
        var systems = new SystemsRunner(world)
            .Add(new MovementSystem());

        systems.Init();

        var entity = world.CreateEntity(new Position());
        world.Add(entity, new Velocity { X = 1, Y = 2 });
        systems.Run();

        ref var pos = ref world.Get<Position>(entity);
        Console.WriteLine($"Position: ({pos.X}, {pos.Y})");   // Position: (1, 2)

        systems.Destroy();
    }
}
```

Walkthrough: [Quick Start](https://kenseitheone.github.io/KenseiECS/guide/quick-start).

## Documentation

- **Getting started** — [Introduction](https://kenseitheone.github.io/KenseiECS/guide/introduction) · [Installation](https://kenseitheone.github.io/KenseiECS/guide/installation) · [Quick Start](https://kenseitheone.github.io/KenseiECS/guide/quick-start) · [Live demo](https://kenseitheone.github.io/KenseiECS/guide/demo) · [Rules for AI agents](https://kenseitheone.github.io/KenseiECS/guide/ai-agents) ([`AGENTS.md`](KenseiECS/AGENTS.md), shipped with the package)
- **Core concepts** — [Entities](https://kenseitheone.github.io/KenseiECS/concepts/entities) · [Components](https://kenseitheone.github.io/KenseiECS/concepts/components) · [Filters](https://kenseitheone.github.io/KenseiECS/concepts/filters) · [Systems & SharedData](https://kenseitheone.github.io/KenseiECS/concepts/systems) · [SystemsRunner](https://kenseitheone.github.io/KenseiECS/concepts/runner)
- **Features** — [Groups](https://kenseitheone.github.io/KenseiECS/guides/groups) · [Change tracking](https://kenseitheone.github.io/KenseiECS/guides/change-tracking) · [CommandBuffer](https://kenseitheone.github.io/KenseiECS/guides/command-buffer) · [Singletons](https://kenseitheone.github.io/KenseiECS/guides/singletons) · [OneFrame components](https://kenseitheone.github.io/KenseiECS/guides/one-frame) · [Component listeners](https://kenseitheone.github.io/KenseiECS/guides/component-listeners) · [World events](https://kenseitheone.github.io/KenseiECS/guides/world-events) · [World lifecycle](https://kenseitheone.github.io/KenseiECS/guides/world-lifecycle) · [Snapshots](https://kenseitheone.github.io/KenseiECS/guides/snapshots) · [Generated Init](https://kenseitheone.github.io/KenseiECS/guides/source-generator) · [Threading](https://kenseitheone.github.io/KenseiECS/guides/threading) · [WorldConfig](https://kenseitheone.github.io/KenseiECS/guides/world-config) · [Release vs. KENSEI_DEBUG](https://kenseitheone.github.io/KenseiECS/guides/debug-mode)
- **Unity** — [Bootstrap & authoring](https://kenseitheone.github.io/KenseiECS/unity/bootstrap) · [EcsEntityView](https://kenseitheone.github.io/KenseiECS/unity/entity-view) · [Listener bridge](https://kenseitheone.github.io/KenseiECS/unity/listener-bridge) · [Debug tools](https://kenseitheone.github.io/KenseiECS/unity/debug-tools) · [Sample](https://kenseitheone.github.io/KenseiECS/unity/sample)
- **Reference** — [Architecture](https://kenseitheone.github.io/KenseiECS/architecture) · [Benchmarks](https://kenseitheone.github.io/KenseiECS/benchmarks) · [Comparison with other ECS](https://kenseitheone.github.io/KenseiECS/guide/comparison) · [Migrating from LeoEcsLite](https://kenseitheone.github.io/KenseiECS/migration-from-leoecslite) · [FAQ](https://kenseitheone.github.io/KenseiECS/faq) · [Changelog](https://kenseitheone.github.io/KenseiECS/changelog) · [Contributing](https://kenseitheone.github.io/KenseiECS/contributing) (repository layout, tests)
- **API reference** — [All public types](https://kenseitheone.github.io/KenseiECS/api/) (generated from the XML doc comments)

## License

MIT
