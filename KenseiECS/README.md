# KenseiECS

Lightweight, sparse-set Entity Component System for Unity and .NET.

This folder is the Unity package (`com.kensei.ecs`).

**Documentation:** https://kenseitheone.github.io/KenseiECS/ — source, benchmarks and examples: https://github.com/KenseiTheOne/KenseiECS

## Install

Package Manager -> Add package from git URL:

```
https://github.com/KenseiTheOne/KenseiECS.git?path=/KenseiECS
```

Pin a version with `#v2.2.2`.

## Quick Start

```csharp
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
```

## Good to know

- `systems.Warmup()` (and `world.Warmup()`) creates a temporary entity, adds a default component of every registered type and destroys it. World event listeners do not see it, but pool listeners (`IComponentListener<T>`) and filter listeners (`IFilterListener`) — including those subscribed in a system's `Init` — get the add and remove callbacks for it. Recognise that entity or subscribe after `Warmup`. See [World lifecycle](https://kenseitheone.github.io/KenseiECS/guides/world-lifecycle#warmup).
- `Warmup` does not size storage for the peak: `world.Reserve(entities, components)` grows entity storage and every pool and filter (also those created later), `EventBuffer<T>.Prewarm(lists, capacity)` fills the event list pool. See [World config](https://kenseitheone.github.io/KenseiECS/guides/world-config#reserving-capacity-later).
- Nested runners: `AddGroup(name, runner)` runs inside the parent's `Run` and can be toggled by name or with `group.SetActive(bool)`; `AddPhase(name, runner)` is a separate phase driven by `GetRunner(name).Run()`. `Add(runner, name)` also makes a phase and is obsolete. A runner can be registered only once, in one slot of one parent. Names are case-sensitive, and an unknown name is ignored silently in release. See [SystemsRunner](https://kenseitheone.github.io/KenseiECS/concepts/runner).

See [CHANGELOG.md](CHANGELOG.md) for release notes.
