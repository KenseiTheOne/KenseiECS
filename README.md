# KenseiECS

Lightweight, sparse-set Entity Component System for Unity and .NET.

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
- **Exception-safe lifecycle** — a throwing listener or system never leaves the world inconsistent
- **Debug validation** — dead/stale handle misuse throws under KENSEI_DEBUG, zero cost in release
- **Listener bridge** — clean ECS <-> Unity MonoBehaviour communication, no delegates
- **Editor tools** — World Inspector, Profiler, EcsEntityView with navigation (under KENSEI_DEBUG)
- **Scales to thousands of component types** — multi-word bitmasks, per-type filter lists

## Install

**Unity (2021.3+)** — Package Manager -> Add package from git URL:

```
https://github.com/KenseiTheOne/KenseiECS.git?path=/KenseiECS
```

Pin a version with `#v2.0.0`. The package ships two assembly definitions, `KenseiECS` and `KenseiECS.Editor`; both are auto-referenced, so your code in `Assembly-CSharp` sees the framework without extra setup. Dropping the `KenseiECS/` folder into `Assets/` works too.

**.NET** — reference `KenseiECS.NET/KenseiECS.csproj` (netstandard2.1) or compile the `KenseiECS/Core` and `KenseiECS/Systems` sources directly, as the test project does.

## Performance

10,000 entities, .NET 8, BenchmarkDotNet — zero allocations at runtime:

| Operation | KenseiECS | LeoEcsLite | Arch |
|---|---:|---:|---:|
| Iteration (2 comp) | 13.9 us | 14.1 us | **5.5 us** |
| Entity creation (2 comp) | **232 us** | 346 us | 208 us |
| Structural changes (add+remove) | 90.8 us | **74.7 us** | 592.9 us |
| Game loop (mixed frame) | **31.3 us** | 39.8 us | 74.1 us |

**Bold** = best in row. [Full benchmarks with analysis](BENCHMARKS.md). Run them yourself with `dotnet run -c Release --project Benchmark`.

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

### Handles vs. indices

Filters yield `int` slot indices; `World` methods take `Entity` handles. An `Entity` carries a generation and stays safe forever: once its entity is destroyed, `IsAlive` is false, and after the slot is reused by another entity it still does not match. An `int` index has no generation: it is valid only until the end of the current iteration. Store `Entity` handles, never `int`s.

```csharp
foreach (int e in filter) {
    var handle = world.GetEntity(e);     // convert inside the loop
    target.Enemy = handle;               // store the handle, not e
}
```

`GetEntity(int)` on a dead slot returns the handle of the entity that last lived there; `IsAlive` is false for it. Once the slot is reused, the same index names the new entity.

## Components

All components must be structs implementing IComponent:

```csharp
struct Health : IComponent { public float Value; }

world.Add(entity, new Health { Value = 100 });   // throws if already present
ref var hp = ref world.Get<Health>(entity);      // by ref, no copy
bool has = world.Has<Health>(entity);            // does not create the pool
world.Remove<Health>(entity);                    // no-op if absent

// Pool access (cache in Init)
var pool = world.Pool<Health>();
ref var hp = ref pool.Get(entity.Index);
```

A `ref T` from `Get` points into the pool's dense array. It is valid until the next `Add` of the same component type (the array may grow) or until that component is removed (swap-remove moves the last element into its slot). Re-acquire the ref after structural changes.

```csharp
ref var hp = ref world.Get<Health>(e);
world.Add(other, new Health());   // may reallocate the Health pool
hp.Value = 10;                    // may write into the old array — re-Get instead
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

Components without IAutoReset are reset to default(T) automatically. AutoReset is called for the removed component only; the component moved into its slot by swap-remove is untouched. `Warmup` and `Clear` also call it, so it must handle `default(T)`.

The bridge is a cached delegate, AOT/IL2CPP-safe: one boxing allocation per pool at construction, zero allocations per Remove. Explicit interface implementations are supported.

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

### Component types

Every component type gets a process-wide index (`ComponentType<T>.Index`). The index resolves back to the type:

```csharp
var types = new List<int>();
world.GetComponentTypes(entity, types);
foreach (int typeIndex in types) {
    Debug.Log(ComponentType.NameOf(typeIndex));
    var pool = world.GetPool(typeIndex);   // ComponentPoolBase
}
```

Indices depend on first-touch order and are not stable across runs; do not persist them.

## Filters

```csharp
var filter = world.Filter()
    .Inc<Position>()
    .Inc<Velocity>()
    .Exc<Frozen>()
    .End();

foreach (int e in filter) {
    ref var pos = ref positions.Get(e);
    world.DestroyEntity(world.GetEntity(e));  // OK: current entity
}
```

Identical filter constraints return the same Filter instance. Build filters in `Init`, not in `Run`: `Filter()` allocates a builder.

`End()` throws InvalidOperationException for filters without a single `Inc<T>` (exclude-only and empty filters are not supported) and when the same component type is in both `Inc<T>` and `Exc<T>`.

### Iteration contract

- Iteration order is unspecified and changes with structural modifications. Do not rely on it.
- Safe inside `foreach`: destroying the current entity, adding/removing components on the current entity, creating new entities. New entities that match the filter are not visited in the current loop.
- Destroying or removing components from a **not-yet-visited** entity is a known limitation: the swap-remove may cause an already-visited entity to be visited again. Defer such changes to after the loop (collect indices, or use a OneFrame tag).
- `foreach` does not lock the filter. An exception inside the loop leaves the world consistent.

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
    .DelHere<HitEvent>()                     // remove HitEvent here, mid-pipeline
    .Add(new RenderSystem())
    .OneFrame<DamageEvent>();                // remove DamageEvent at end of Run

systems.Init();
systems.Run();
systems.Destroy();

// Enable/disable at runtime
systems.SetActive("movement", false);
systems.SetActive("movement", true);
```

### Lifecycle contract

- `Init` runs each `IInitSystem` once, in registration order. If one throws, the runner stays uninitialized and the next `Init` resumes with the system that failed. `Add` after `Init` throws under KENSEI_DEBUG.
- `Run` executes enabled systems in order, then removes OneFrame components. Cleanup runs even if a system throws (systems after the failing one are skipped that frame). `Run` before `Init` throws under KENSEI_DEBUG.
- `Destroy` runs `IDestroySystem` in **reverse** registration order, is a no-op before `Init`, and resets the runner so `Init` can run again (scene reload).

### Nested Runners

Update-phase systems live in the root runner. `root.Run()` advances the world tick, runs the root's systems and cleans the root's OneFrame components. A **named** child runner is a separate phase (FixedUpdate/LateUpdate): it is excluded from the parent's `Run()` and driven explicitly via `GetRunner(name).Run()`, which runs the child's systems and cleans the child's OneFrame components without ticking. An **unnamed** child runner is an inline group executed as part of the parent's `Run()`. `Init()`/`Destroy()` cascade from root to all children; a child constructed without `SharedData` inherits the parent's.

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

// Pause a whole phase
root.SetActive("fixed", false);

// On shutdown:
root.Destroy();
```

Under KENSEI_DEBUG, adding, initializing or running a child constructed with a different World (or an explicitly passed different SharedData) throws instead of silently ignoring it, and unknown names passed to `SetActive`, `IsActive` or `GetRunner` throw.

## OneFrame Components

```csharp
struct DamageEvent : IComponent { public float Value; }

systems.OneFrame<DamageEvent>();

// In a system — create event
world.Add(entity, new DamageEvent { Value = 10 });
// All systems later in the pipeline see it this frame
// Removed automatically at end of Run()
```

`OneFrame<T>` removes at the end of `Run`, so a producer must run **before** its consumers; an event created after the consumer ran is removed unseen. Use `DelHere<T>()` to put the cleanup at a specific point of the pipeline. An entity whose only component is a one-frame component is auto-destroyed by the cleanup, which makes "event entities" free.

## WorldConfig

```csharp
var config = new WorldConfig {
    InitialEntityCapacity = 512,       // entity slots, mask words, filter sparse arrays
    InitialPoolSparseCapacity = 512,   // per-pool sparse array
    InitialPoolDenseCapacity = 128,    // per-pool and per-filter dense arrays
    InitialPoolCount = 64              // pool registry size
};
var world = new World(config);
```

Every array grows on demand; the config only sets starting sizes.

## Listeners (Unity Bridge)

```csharp
public interface IDamageListener { void OnDamage(float damage); }

// Subscribe
world.Subscribe<IDamageListener>(entity, enemyView);

// Iterate listeners directly — no delegates, zero allocation.
// Reverse so a listener may unsubscribe itself from inside the callback.
ref var listeners = ref world.Pool<Listeners<IDamageListener>>().Get(entity.Index);
for (int i = listeners.Values.Count - 1; i >= 0; i--) {
    listeners.Values[i].OnDamage(10f);
}

// Unsubscribe — the (now empty) Listeners component stays, the entity stays alive
world.Unsubscribe<IDamageListener>(entity, enemyView);
bool any = world.HasListeners<IDamageListener>(entity);   // false when empty

// Create with listener
var entity = world.CreateWithListener<IDamageListener>(enemyView);
```

`Listeners<T>` lives in Core and has no Unity dependency.

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

- `OnEntityCreated` fires after the first component was added (`CreateEntity`) or after all components were copied (`CopyEntity`); `OnComponentAdded` for that first component fires before it.
- `OnComponentRemoved` fires while the entity is still alive. If it was the last component, auto-destroy (`OnEntityDestroyed`) follows.
- `OnEntityDestroyed` fires before components are removed; the entity is already dead (`IsAlive` false) but its components are still readable.
- Listeners may add or remove listeners during dispatch; the dispatch continues over the set captured at its start. Listeners may modify the world, including the entity being destroyed.
- If a listener throws, the exception propagates after the operation is brought to a consistent state: the entity slot is released, and components not yet removed at that point stay in their pools until the slot is reused.
- `Warmup` and `Clear` fire no events.
- `typeIndex` resolves via `ComponentType.TypeOf(typeIndex)` / `ComponentType.NameOf(typeIndex)`.

## World Lifecycle

```csharp
world.Clear();    // reset data, preserve allocations, invalidate all handles
world.Destroy();  // null everything for GC
systems.Warmup(); // Init + JIT pre-touch + memory pre-alloc
```

Warmup calls Init, then creates a temporary entity, adds a default component of every registered type and destroys it — exercising Add/Remove paths and filter updates. Existing entities and their data are not touched, and listeners do not observe the temporary entity. Call once before gameplay starts (e.g. during a loading screen).

## Threading

`World`, pools and filters are single-threaded. Reading `RawData`/`RawEntities` of pools from several threads is safe only while no structural change (Add/Remove/Create/Destroy) happens on any thread. Component type registration (`ComponentType<T>.Index`) is thread-safe.

## Release vs. KENSEI_DEBUG

| Situation | Release | KENSEI_DEBUG |
|---|---|---|
| `Add` of a component the entity already has | throws | throws |
| `Add`/`Get`/`Has`/`Remove` with a dead or stale `Entity` | undefined (may corrupt another entity) | throws |
| Pool `Add(int)` on a dead slot | undefined | throws |
| Pool `Get(int)` without the component | reads garbage | throws |
| `Remove` of a missing component | no-op | no-op |
| `DestroyEntity` of a dead entity | no-op | no-op |
| `CopyEntity` of a dead entity | returns `Entity.Null` | throws |
| `GetEntity` on a dead slot | dead handle | dead handle |
| Runner: `Run` before `Init`, `Add` after `Init`, unknown name | silent | throws |
| Nested runner with a different World / SharedData | silently ignored | throws |

Validation code is not compiled in release; the cost is zero. Toggle via **KenseiECS -> Debug Mode** (sets `KENSEI_DEBUG` for all build targets) or add the define to your test project.

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

Menu: **KenseiECS -> Debug Mode** — toggles the KENSEI_DEBUG define.

When enabled:
- **KenseiECS -> World Inspector** — all entities with editable components
- **KenseiECS -> Profiler** — lifecycle events with call stacks
- **EcsEntityView** inspector with entity navigation
- **Validation** — see the table above

World is auto-discovered via `IEcsWorldProvider` on any MonoBehaviour in the scene.

```csharp
EcsProfiler.Enable(world);
```

## Repository Layout

```
KenseiECS/            Unity package (com.kensei.ecs)
├── Core/             Entity, World, ComponentPool, Filter, Listeners, ...
├── Systems/          ISystem, SystemsRunner
├── Unity/            EcsEntityView, IEcsWorldProvider
├── DevTools/         EcsProfiler, WorldDebugView (KENSEI_DEBUG)
├── Editor/           Inspector, Profiler window, Debug Mode toggle
├── package.json, KenseiECS.asmdef, CHANGELOG.md
KenseiECS.NET/        .NET project (netstandard2.1) compiling the package sources
KenseiECS.Tests/      NUnit tests (compile Core + Systems directly)
Benchmark/            BenchmarkDotNet suite vs LeoEcsLite / Arch
Example/              Console game using the framework
BENCHMARKS.md         Benchmark results and analysis
```

## Tests

```
dotnet test KenseiECS.Tests -c Release
dotnet test KenseiECS.Tests -c Release -p:KenseiDebug=true
```

The `KenseiDebug` flag builds with `KENSEI_DEBUG` and additionally covers the debug validation layer. Both configurations run in CI on every push.

## License

MIT
