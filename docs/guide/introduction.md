# Introduction

KenseiECS is a lightweight, sparse-set Entity Component System for Unity and .NET. Each component type is stored in its own sparse set: component access is O(1), iteration walks dense arrays, and adding or removing a component touches only that type's pool and the filters that care about it.

The core (`World`, pools, filters, systems) has no Unity dependency and runs on plain .NET. In Unity, the package adds a bootstrap component, inspector authoring, a listener bridge to MonoBehaviours and editor tools.

- New here? Go to [Installation](./installation.md), then the [Quick Start](./quick-start.md).
- Coming from LeoEcsLite? See [Migrating from LeoEcsLite](../migration-from-leoecslite.md).
- Want the internals? Read the [Architecture](../architecture.md) page.

## Features

- **Sparse Set storage** — O(1) component access, dense arrays for cache-friendly iteration
- **Generational entities** — 8-byte `Entity` (int Index + int Generation) with aliasing protection
- **Reactive filters** — cached query results, updated automatically on component changes
- **Zero-allocation iteration** — struct enumerator, reverse iteration safe for structural changes
- **Auto-destroy** — entities without components are destroyed automatically
- **IAutoReset** — custom cleanup on component remove
- **IAutoCopy** — custom deep-copy logic for `CopyEntity`
- **SharedData** — typed container for shared services, no reflection
- **OneFrame components** — auto-removed event components, end-of-frame or positional (`DelHere`)
- **Nested system runners** — separate groups for Update/FixedUpdate/LateUpdate
- **Named systems** — enable/disable systems and phases at runtime
- **World events** — `IWorldEventListener` for lifecycle notifications, type indices resolve to `Type`
- **Exception-safe lifecycle** — a throwing listener or system never leaves the world inconsistent
- **Debug validation** — dead/stale handle misuse throws under `KENSEI_DEBUG`, zero cost in release
- **Listener bridge** — clean ECS <-> Unity MonoBehaviour communication, no delegates
- **Editor tools** — World Inspector, Profiler, EcsEntityView with navigation (under `KENSEI_DEBUG`)
- **Scales to thousands of component types** — multi-word bitmasks, per-type filter lists

## Performance

10,000 entities, .NET 8, BenchmarkDotNet, zero allocations at runtime (all numbers from one session on one machine, September 2026):

| Operation | KenseiECS | LeoEcsLite | Arch |
|---|---:|---:|---:|
| Iteration (2 comp), filter | 13.9 us | 14.0 us | **5.4 us** |
| Iteration (2 comp), owning group | **5.5 us** | — | 5.4 us |
| Entity creation (2 comp) | 294 us | 344 us | **208 us** |
| Structural changes (add+remove) | 100 us | **73 us** | 590 us |
| Game loop (mixed frame) | **34 us** | 40 us | 78 us |

**Bold** = best in row. The owning-group row measures KenseiECS [groups](../guides/groups.md), which keep the dense arrays of several pools aligned.

See the [full benchmarks with analysis](../benchmarks.md), including 1024 component types, fragmented worlds, filters observing the changed type and memory footprint. Run them yourself from the repository root:

```sh
dotnet run -c Release --project Benchmark
```
