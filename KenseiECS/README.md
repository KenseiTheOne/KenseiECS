# KenseiECS

Lightweight, Sparse Set-based Entity Component System for Unity.

## Features

- **Sparse Set storage** — O(1) component access, dense arrays for cache-friendly iteration
- **Generational entities** — 4-byte Entity with aliasing protection, overflow-safe
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
- **Listener bridge** — clean ECS <-> Unity MonoBehaviour communication
- **Editor tools** — World Inspector, Profiler, EcsEntityView with navigation (under KENSEI_DEBUG)

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

var entity = world.CreateEntity(new Position(), new Velocity { X = 1, Y = 2 });
systems.Run();
systems.Destroy();
```

## Entity

```csharp
var entity = world.CreateEntity();
var entity = world.CreateEntity(new Position { X = 1 });
var entity = world.CreateEntity(new Position(), new Velocity());
var entity = world.CreateEntity(new Position(), new Velocity(), new Health());

bool alive = world.IsAlive(entity);
world.DestroyEntity(entity);

// Copy entity with all components
var copy = world.CopyEntity(source);
```

Auto-destroy: entities are destroyed when their last component is removed.
Generation overflow: slots are permanently retired after 4096 reuses.

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

Components without IAutoCopy are copied by value (shallow copy).

## Filters

```csharp
var filter = world.Filter()
    .Inc<Position>()
    .Inc<Velocity>()
    .Exc<Frozen>()
    .End();

foreach (int e in filter) {
    ref var pos = ref positions.Get(e);
    // Structural changes inside foreach are safe (reverse iteration)
    world.DestroyEntity(world.GetEntity(e));  // OK
}
```

Identical filter constraints return the same Filter instance.

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

```csharp
var updateSystems = new SystemsRunner(world, shared)
    .Add(new MovementSystem());

var fixedSystems = new SystemsRunner(world, shared)
    .Add(new PhysicsSystem());

var root = new SystemsRunner(world, shared)
    .Add(updateSystems, "update")
    .Add(fixedSystems, "fixed");

root.Init();

// In MonoBehaviour:
void Update()      => root.GetRunner("update").Run();
void FixedUpdate() => root.GetRunner("fixed").Run();
```

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

// Notify
world.Notify<IDamageListener>(entity, l => l.OnDamage(10f));

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

## EcsEntityView (Unity)

```csharp
var view = Instantiate(prefab).GetComponent<EcsEntityView>();
view.Bind(world, entity);
```

## Debug Tools

Menu: **KenseiECS -> Debug Mode** — toggles KENSEI_DEBUG define.

When enabled:
- **KenseiECS -> World Inspector** — all entities with editable components
- **KenseiECS -> Profiler** — lifecycle events with call stacks
- **EcsEntityView** inspector with entity navigation

```csharp
WorldInspectorWindow.TargetWorld = world;
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
