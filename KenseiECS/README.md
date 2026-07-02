# KenseiECS

Lightweight, Sparse Set-based Entity Component System for Unity.

## Features

- **Sparse Set storage** — O(1) component access, dense arrays for cache-friendly iteration
- **Generational entities** — 8-byte Entity (int Index + int Generation) with aliasing protection
- **Reactive filters** — cached query results, updated automatically on component changes
- **Zero-allocation iteration** — struct enumerator, reverse iteration safe for structural changes
- **Auto-destroy** — entities without components are destroyed automatically
- **IAutoReset** — custom cleanup on component remove
- **IAutoCopy** — custom deep-copy logic for CopyEntity
- **SharedData** — typed container for shared services, no reflection
- **OneFrame components** — auto-removed event components
- **Nested system runners** — separate groups for Update/FixedUpdate/LateUpdate
- **Named systems** — enable/disable systems at runtime
- **World events** — IWorldEventListener for lifecycle notifications
- **Debug validation** — dead/stale handle misuse throws under KENSEI_DEBUG, zero cost in release
- **Listener bridge** — clean ECS <-> Unity MonoBehaviour communication
- **Editor tools** — World Inspector, Profiler, EcsEntityView with navigation (under KENSEI_DEBUG)

## Performance

10,000 entities, .NET 8, BenchmarkDotNet — zero allocations at runtime:

| Operation | KenseiECS | LeoEcsLite | Arch |
|---|---:|---:|---:|
| Iteration (2 comp) | 13.9 us | 14.1 us | **5.5 us** |
| Entity creation (2 comp) | **232 us** | 346 us | 208 us |
| Structural changes (add+remove) | 90.8 us | **74.7 us** | 592.9 us |
| Game loop (mixed frame) | **31.3 us** | 39.8 us | 74.1 us |

**Bold** = best in row. [Full benchmarks with analysis](https://github.com/KenseiTheOne/KenseiECS/blob/benchmarks/KenseiECS/BENCHMARKS.md)

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

var world = new World();
var systems = new SystemsRunner(world)
    .Add(new MovementSystem());

systems.Init();

var entity = world.CreateEntity(new Position());
world.Add(entity, new Velocity { X = 1, Y = 2 });
systems.Run();
systems.Destroy();
```

## Entity

```csharp
// Always create with at least one component
var entity = world.CreateEntity(new Position { X = 1 });

// Add more components manually
world.Add(entity, new Velocity());
world.Add(entity, new Health { Value = 100 });

bool alive = world.IsAlive(entity);
world.DestroyEntity(entity);

// Copy entity with all components
var copy = world.CopyEntity(source);
```

Auto-destroy: entities are destroyed when their last component is removed.

## Components

All components must be structs implementing IComponent:

```csharp
struct Health : IComponent { public float Value; }

world.Add(entity, new Health { Value = 100 });
ref var hp = ref world.Get<Health>(entity);    // by ref, no copy
bool has = world.Has<Health>(entity);
world.Remove<Health>(entity);

// Pool access (cache in Init)
var pool = world.Pool<Health>();
ref var hp = ref pool.Get(entity.Index);
```

### IAutoReset

Custom cleanup when component is removed:

```csharp
struct Inventory : IComponent, IAutoReset<Inventory> {
    public List<int> Items;
    public void AutoReset(ref Inventory c) {
        c.Items?.Clear();
        c.Items = null;
    }
}
```

Components without IAutoReset are reset to default(T) automatically.

AutoReset must be implemented implicitly (a public method) — an explicit interface implementation is not picked up. The bridge is a cached delegate, AOT/IL2CPP-safe: one boxing allocation per pool at construction, zero allocations per Remove.

### IAutoCopy

Custom deep-copy logic for CopyEntity:

```csharp
struct Inventory : IComponent, IAutoCopy<Inventory> {
    public List<int> Items;
    public void AutoCopy(ref Inventory c) {
        c.Items = c.Items != null ? new List<int>(c.Items) : null;
    }
}
```

Components without IAutoCopy are copied by value (shallow copy). Same constraint as IAutoReset: AutoCopy must be a public method, not an explicit interface implementation.

## Filters

```csharp
var filter = world.Filter()
    .Inc<Position>()
    .Inc<Velocity>()
    .Exc<Frozen>()
    .End();

foreach (int e in filter) {
    ref var pos = ref positions.Get(e);
    // Safe inside foreach: destroying/removing the CURRENT entity and creating
    // new entities. Destroying a NOT-YET-visited other entity may cause an
    // already-processed or newly created entity to be visited (known limitation).
    world.DestroyEntity(world.GetEntity(e));  // OK
}
```

Identical filter constraints return the same Filter instance.

`End()` throws InvalidOperationException for filters without a single `Inc<T>` (exclude-only and empty filters are not supported) and when the same component type is in both `Inc<T>` and `Exc<T>`.

## Systems

```csharp
class MySystem : IInitSystem, IRunSystem, IDestroySystem {
    public void Init(World world, SharedData shared) { }
    public void Run(World world) { }
    public void Destroy(World world) { }
}
```

## SharedData

Typed container for shared services. No reflection, explicit access:

```csharp
var shared = new SharedData();
shared.Add(new TimeService());
shared.Add(new SpawnConfig { Max = 100 }, "enemies");
shared.Add(new SpawnConfig { Max = 20 }, "pickups");

var systems = new SystemsRunner(world, shared)
    .Add(new MovementSystem());

// In system Init:
public void Init(World world, SharedData shared) {
    var time = shared.Get<TimeService>();
    var enemyConfig = shared.Get<SpawnConfig>("enemies");
}
```

## SystemsRunner

```csharp
var systems = new SystemsRunner(world, shared)
    .Add(new InputSystem())
    .Add(new MovementSystem(), "movement")   // named
    .Add(new DamageSystem())
    .OneFrame<DamageEvent>();

systems.Init();
systems.Run();
systems.Destroy();

// Enable/disable at runtime
systems.SetActive("movement", false);
systems.SetActive("movement", true);
```

### Nested Runners

Update-phase systems live in the root runner. `root.Run()` advances the world tick, runs the root's systems and cleans the root's OneFrame components. A **named** child runner is a separate phase (FixedUpdate/LateUpdate): it is excluded from the parent's `Run()` and driven explicitly via `GetRunner(name).Run()`, which runs the child's systems and cleans the child's OneFrame components without ticking. An **unnamed** child runner is an inline group executed as part of the parent's `Run()`. `Init()`/`Destroy()` cascade from root to all children, and the root's SharedData propagates to children through Init.

```csharp
var fixedSystems = new SystemsRunner(world)
    .Add(new PhysicsSystem());

var root = new SystemsRunner(world, shared)
    .Add(new MovementSystem())
    .Add(fixedSystems, "fixed");

root.Init();

// In MonoBehaviour:
void Update() =>
    root.Run();                     // ticks the world, runs root systems

void FixedUpdate() =>
    root.GetRunner("fixed").Run();  // separate phase, no tick

// On shutdown:
root.Destroy();
```

Under KENSEI_DEBUG, adding, initializing or running a child constructed with a different World (or an explicitly passed different SharedData) throws instead of silently ignoring it.

## OneFrame Components

```csharp
struct DamageEvent : IComponent { public float Value; }

systems.OneFrame<DamageEvent>();

// In a system — create event
world.Add(entity, new DamageEvent { Value = 10 });
// All systems see it this frame
// Removed automatically at end of Run()
```

## WorldConfig

```csharp
var config = new WorldConfig {
    InitialEntityCapacity = 512,
    InitialPoolSparseCapacity = 512,
    InitialPoolDenseCapacity = 128,
    InitialPoolCount = 64
};
var world = new World(config);
```

## Listeners (Unity Bridge)

```csharp
public interface IDamageListener { void OnDamage(float damage); }

// Subscribe
world.Subscribe<IDamageListener>(entity, enemyView);

// Iterate listeners directly — no delegates, zero allocation
ref var listeners = ref world.Pool<Listeners<IDamageListener>>().Get(entity.Index);
foreach (var l in listeners.Values) {
    l.OnDamage(10f);
}

// Unsubscribe
world.Unsubscribe<IDamageListener>(entity, enemyView);

// Create with listener
var entity = world.CreateWithListener<IDamageListener>(enemyView);
```

## World Events

```csharp
class MyListener : IWorldEventListener {
    public void OnEntityCreated(int entityIndex) { }
    public void OnEntityDestroyed(int entityIndex) { }
    public void OnComponentAdded(int entityIndex, int typeIndex) { }
    public void OnComponentRemoved(int entityIndex, int typeIndex) { }
}

world.AddEventListener(new MyListener());
world.RemoveEventListener(listener);
```

## World Lifecycle

```csharp
world.Clear();    // reset data, preserve allocations
world.Destroy();  // null everything for GC
systems.Warmup(); // Init + JIT pre-touch + memory pre-alloc
```

Warmup calls Init, then creates a temporary entity, adds a default component of every registered type and destroys it — exercising Add/Remove paths and filter updates. Existing entities and their data are not touched. Call once before gameplay starts (e.g. during a loading screen).

## EcsEntityView (Unity)

```csharp
var view = Instantiate(prefab).GetComponent<EcsEntityView>();
view.Bind(world, entity);

// Optional: destroy the bound entity in OnDestroy (off by default).
// Also available as a checkbox in the inspector.
view.DestroyEntityWithGameObject = true;
```

With the flag enabled, OnDestroy destroys the entity only if the world is still alive and the entity handle is valid.

## Debug Tools

Menu: **KenseiECS -> Debug Mode** — toggles KENSEI_DEBUG define.

When enabled:
- **KenseiECS -> World Inspector** — all entities with editable components
- **KenseiECS -> Profiler** — lifecycle events with call stacks
- **EcsEntityView** inspector with entity navigation
- **Validation** — Add/Get/Has/Remove with a dead or stale Entity handle, pool Get without the component, and GetEntity on a dead slot throw InvalidOperationException with a descriptive message

In release builds the validation code is not compiled — the cost is zero.

World is auto-discovered via `IEcsWorldProvider` on any MonoBehaviour in the scene.

```csharp
EcsProfiler.Enable(world);
```

## Project Structure

```
KenseiECS/
├── Core/
│   ├── Entity.cs, ComponentType.cs, IComponent.cs, IComponentPool.cs
│   ├── ComponentPool.cs, IAutoReset.cs, IAutoCopy.cs
│   ├── Filter.cs, FilterBuilder.cs
│   ├── SharedData.cs, WorldConfig.cs
│   ├── IWorldEventListener.cs, World.cs
├── Systems/
│   ├── ISystem.cs, SystemsRunner.cs
├── Unity/
│   ├── Listeners.cs, WorldListenerExtensions.cs, EcsEntityView.cs
├── DevTools/
│   ├── WorldDebugView.cs, EcsProfiler.cs
├── Editor/
│   ├── KenseiDebugToggle.cs, WorldInspectorWindow.cs
│   ├── EcsProfilerWindow.cs, EcsEntityViewInspector.cs
└── Tests/
    └── ValidationTests.cs
```

## License

MIT
