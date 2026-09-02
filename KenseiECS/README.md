# KenseiECS

Lightweight, sparse-set Entity Component System for Unity and .NET.

This folder is the Unity package (`com.kensei.ecs`). Full documentation, benchmarks and examples live in the repository root:
https://github.com/KenseiTheOne/KenseiECS

## Install

Package Manager -> Add package from git URL:

```
https://github.com/KenseiTheOne/KenseiECS.git?path=/KenseiECS
```

Pin a version with `#v2.0.0`.

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

See [CHANGELOG.md](CHANGELOG.md) for release notes.
