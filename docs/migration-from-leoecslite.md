# Migrating from LeoEcsLite

A practical guide for teams moving code from [LeoEcsLite](https://github.com/Leopotam/ecslite) to KenseiECS. Both are sparse-set frameworks with `int` entity indices in filters, pools per component type, and `Init`/`Run`/`Destroy` systems, so most code maps line by line. This document lists the mapping, then the places where behavior differs, then a worked migration of a typical system.

The LeoEcsLite side describes the core package (`Leopotam.EcsLite`). Where a feature comes from an extension package (`ecslite-extendedsystems`, `ecslite-di`, `ecslite-threads`, `ecslite-unityeditor`) it is called out.

## Side-by-side mapping

### World and entities

| Concern | LeoEcsLite | KenseiECS |
|---|---|---|
| Create a world | `var world = new EcsWorld();` / `new EcsWorld(in EcsWorld.Config)` | `var world = new World();` / `new World(new WorldConfig { ... })` |
| Tear down | `world.Destroy();` | `world.Destroy();` (also `world.Clear()` to reset without freeing) |
| Create an entity | `int e = world.NewEntity();` then `pool.Add(e)` | `Entity e = world.CreateEntity(new Position());` |
| Destroy an entity | `world.DelEntity(e);` | `world.DestroyEntity(e);` |
| Copy an entity | `world.CopyEntity(src, dst);` onto an existing `dst` | `Entity copy = world.CopyEntity(src);` creates the copy |
| Alive check | `packed.Unpack(world, out int e)` | `world.IsAlive(entity)` |
| Entity handle for storage | `EcsPackedEntity p = world.PackEntity(e);` | `Entity h = world.GetEntity(e);` |
| Handle with world | `EcsPackedEntityWithWorld` / `world.PackEntityWithWorld(e)` | none; store the `World` reference alongside the `Entity` |
| Generation | `world.GetEntityGen(e)` (`short`) | `entity.Generation` (`int`) |
| Component count | `world.GetComponentsCount(e)` | `world.GetComponentCount(entity)` |
| Component types on an entity | `world.GetComponentTypes(e, ref Type[] list)` | `world.GetComponentTypes(entity, List<int> result)` then `ComponentType.TypeOf(index)` |
| Entity count | `world.GetEntitiesCount()` | `world.EntityCount` |
| Index to handle, safe for any `int` | none | `world.TryGetEntity(index, out Entity entity)` |
| Save / load | none in core | `WorldSerializer.Save(world, stream)` / `Load(world, stream)`; `IComponentFormatter<T>` for components with references |

### Pools and components

| Concern | LeoEcsLite | KenseiECS |
|---|---|---|
| Component declaration | `struct Health { public float Value; }` | `struct Health : IComponent { public float Value; }` |
| Get a pool | `EcsPool<Health> pool = world.GetPool<Health>();` | `ComponentPool<Health> pool = world.Pool<Health>();` |
| Add | `ref var hp = ref pool.Add(e); hp.Value = 100;` | `ref var hp = ref world.Add(e, new Health { Value = 100 });` or `pool.Add(e.Index, value)` |
| Get | `ref var hp = ref pool.Get(e);` | `ref var hp = ref world.Get<Health>(e);` or `pool.Get(e.Index)` |
| Has | `pool.Has(e)` | `world.Has<Health>(e)` or `pool.Has(e.Index)` |
| Remove | `pool.Del(e);` | `world.Remove<Health>(e);` or `pool.Remove(e.Index)` |
| Copy one component | `pool.Copy(src, dst);` | none per pool; `world.CopyEntity` copies all |
| Raw arrays | `pool.GetRawDenseItems()`, `GetRawSparseItems()` | `pool.RawData`, `pool.RawEntities`, `pool.Count` |
| Type id | `pool.GetId()`, `world.GetPoolById(id)`, `world.GetPoolByType(type)` | `ComponentType<T>.Index`, `pool.TypeIndex`, `world.GetPool(typeIndex)` |
| Reset hook | `IEcsAutoReset<T>.AutoReset(ref T c)`, called on `Add` and on `Del` | `IAutoReset<T>.AutoReset(ref T c)`, called on `Remove` (and by `Warmup`/`Clear`) only |
| Copy hook | `IEcsAutoCopy<T>.AutoCopy(ref T src, ref T dst)`, replaces the default copy | `IAutoCopy<T>.AutoCopy(ref T c)`, runs on a shallow copy already made |
| Pool events | none | `IComponentListener<T>` via `pool.AddListener` |
| Change tracking | none | `pool.TrackChanges()`, `pool.Modify(e)`, `pool.MarkChanged(e)`, `pool.ChangedSince(e, version)`, `world.ChangeVersion` |

### Filters

| Concern | LeoEcsLite | KenseiECS |
|---|---|---|
| Build | `world.Filter<Position>().Inc<Velocity>().Exc<Frozen>().End()` | `world.Filter().Inc<Position>().Inc<Velocity>().Exc<Frozen>().End()` |
| Static form | none | `world.Filter<Inc<Position, Velocity>, Exc<Frozen>>()` |
| Any-of | none | `.Any<Health>().Any<Shield>()` or `Any<Health, Shield>` spec |
| Iterate | `foreach (int e in filter)` | `foreach (int e in filter)` |
| Count | `filter.GetEntitiesCount()` | `filter.Count`, `filter.IsEmpty` |
| Raw entities | `filter.GetRawEntities()` | `filter.Entities` (`ReadOnlySpan<int>`) |
| Membership test | `filter.GetSparseIndex()[e] > 0` | `filter.Contains(e)` |
| One match | manual | `filter.First()`, `TryGetFirst`, `Single()` |
| Enter/leave events | `LEOECSLITE_FILTER_EVENTS` define + `IEcsFilterEventListener` via `filter.AddEventListener` | `IFilterListener` via `filter.AddListener`, always available |
| Identical constraints | same instance | same instance (dedup includes static specs and generated `Init`) |
| Aligned iteration without sparse lookups | none | `world.Group<Position, Velocity>()` with `Data1`/`Data2`/`Entities` spans |

### Systems

| Concern | LeoEcsLite | KenseiECS |
|---|---|---|
| Container | `IEcsSystems systems = new EcsSystems(world, shared);` | `var systems = new SystemsRunner(world, sharedData);` |
| Register | `systems.Add(new S());` | `systems.Add(new S());` or `systems.Add(new S(), "name")` |
| Lifecycle | `systems.Init(); systems.Run(); systems.Destroy();` | `systems.Init(); systems.Run(); systems.Destroy();` |
| Fluent init | `systems.Add(...).Init();` | `Add` chains, `Init()` returns `void`; call it on its own line |
| Init system | `IEcsInitSystem.Init(IEcsSystems systems)` | `IInitSystem.Init(World world, SharedData shared)` |
| Run system | `IEcsRunSystem.Run(IEcsSystems systems)` | `IRunSystem.Run(World world)` |
| Destroy system | `IEcsDestroySystem.Destroy(IEcsSystems systems)` | `IDestroySystem.Destroy(World world)` |
| Pre-init / post-destroy | `IEcsPreInitSystem`, `IEcsPostDestroySystem` | none; order systems explicitly (`Destroy` runs in reverse registration order) |
| World inside a system | `systems.GetWorld()` | the `World` parameter |
| Shared data | one object: `systems.GetShared<GameShared>()` | typed container: `shared.Get<TimeService>()`, `shared.Get<SpawnConfig>("enemies")`, `shared.TryGet` |
| Several worlds | `systems.AddWorld(w, "events")`, `systems.GetWorld("events")` | none; one `World` per runner, pass other worlds through `SharedData` |
| One-frame components | manual cleanup system, or `DelHere<T>()` from `ecslite-extendedsystems` | `systems.OneFrame<T>()` (end of `Run`) and `systems.DelHere<T>()` (positional), built in |
| Named / toggleable groups | `AddGroup(...)` from `ecslite-extendedsystems`, toggled by an `EcsGroupSystemState` event | `Add(system, "name")`, `SetActive("name", bool)`, `IsActive("name")`; nested `SystemsRunner` |
| Update vs FixedUpdate | two independent `EcsSystems` instances | named child runner: `root.Add(fixedRunner, "fixed")`, `root.GetRunner("fixed").Run()` |
| Introspection | `systems.GetAllSystems()` | `systems.SystemCount`, `systems.GetSystemInfo(i)`, `systems.SetActive(i, bool)` |
| Field injection | `ecslite-di`: `EcsPoolInject<T>`, `EcsFilterInject<Inc<A>, Exc<B>>`, `EcsSharedInject<T>`, `EcsWorldInject` wrapper fields, filled by `systems.Inject(...)` at runtime | source generator: `[Pool]`, `[Inc]`/`[Exc]`/`[Any]`, `[Group]`, `[Shared]` on fields of a `partial` system class; `Init` is generated at compile time |
| Threads | `ecslite-threads` | none |
| Warm-up | none | `systems.Warmup()` |
| Unity entry point | a `MonoBehaviour` that creates `EcsWorld` and `EcsSystems` in `Start`, runs in `Update`, destroys in `OnDestroy` | subclass `EcsBootstrap`, override `Configure(update, fixedUpdate, lateUpdate, shared)` |
| Inspector-authored entities | `ecslite-unityeditor` templates and monitoring | `EcsComponentProvider<T>` subclasses next to an `EcsEntityView`, `view.Spawn(world)` |

### World events

| LeoEcsLite (`LEOECSLITE_WORLD_EVENTS` define) | KenseiECS (always on) |
|---|---|
| `IEcsWorldEventListener.OnEntityCreated(int entity)` | `IWorldEventListener.OnEntityCreated(int entityIndex)` |
| `OnEntityChanged(int entity)` | `OnComponentAdded(int entityIndex, int typeIndex)` and `OnComponentRemoved(int entityIndex, int typeIndex)` |
| `OnEntityDestroyed(int entity)` | `OnEntityDestroyed(int entityIndex)` |
| `OnFilterCreated`, `OnWorldResized`, `OnWorldDestroyed` | none (`world.FilterCount`/`GetFilter(i)` and `world.IsDestroyed` cover the tooling cases) |
| `world.AddEventListener(listener)` | `world.AddEventListener(listener)` / `RemoveEventListener` |

### Things KenseiECS has that Lite core does not

`CommandBuffer` with `PendingEntity`, `GetSingleton<T>()`/`GetSingletonEntity<T>()`/`HasSingleton<T>()`, `EventBuffer<T>` with `world.AddEvent`, `Listeners<T>` with `Subscribe`/`Unsubscribe`, `IComponentListener<T>`, `Any` filters, static filter specs, owning groups (`world.Group<T1, T2>()`), change tracking (`TrackChanges`/`Modify`/`ChangedSince`), snapshots (`WorldSerializer`), the source generator for `Init`, `EcsBootstrap`, `EcsComponentProvider<T>` with `EcsEntityView.Spawn`, the Systems window, and the World Inspector and Profiler windows under `KENSEI_DEBUG`, plus per-system Unity `ProfilerMarker`s.

### Things Lite has that KenseiECS does not

`EcsPackedEntityWithWorld`, named multi-world support inside one systems group, `IEcsPreInitSystem`/`IEcsPostDestroySystem`, `pool.Copy(src, dst)` for a single component, `EcsWorld.Config.RecycledEntities`/`PoolRecycledSize` tuning, runtime injection of arbitrary instances by type (`ecslite-di`'s `EcsCustomInject<T>` / `systems.Inject(instance)`; KenseiECS routes those through `SharedData` and `[Shared]`), the threading extension, and the broader third-party ecosystem.

## Behavior differences

### An entity must be created with a component

Lite lets `NewEntity()` return an empty entity that you populate afterwards; an entity with no components is a leak Lite's `DEBUG` build reports later. KenseiECS has no empty state: `CreateEntity<T>(T)` allocates the slot and adds the first component in one call, and world listeners see `OnEntityCreated` only after that component is in place.

```csharp
// LeoEcsLite
int e = world.NewEntity();
ref var pos = ref positions.Add(e);
pos.X = 10f;
velocities.Add(e);

// KenseiECS
Entity e = world.CreateEntity(new Position { X = 10f });
world.Add(e, new Velocity());
```

### Auto-destroy on last component removal

Same rule in both frameworks: removing the last component destroys the entity. Two consequences in KenseiECS worth knowing:

- `OneFrame<T>` / `DelHere<T>` cleanup auto-destroys entities whose only component was the one-frame component, so "event entities" need no cleanup code.
- `world.Unsubscribe<T>` keeps the (empty) `Listeners<T>` component so that unsubscribing the last listener does not destroy the entity.

### No lock counter on iteration

Lite's `EcsFilter.Enumerator` locks the filter; membership changes made during the loop are queued and applied when the outermost `foreach` ends. The dense array does not change under the loop, so a not-yet-visited entity that you destroyed is still yielded later in the same loop (and its components are then gone), while a newly matching entity is not visited.

KenseiECS applies changes immediately and iterates in reverse over a swap-removed array:

- Safe inside `foreach`: destroying the current entity, adding/removing components on the current entity, creating entities. New matches are not visited in the current loop, as in Lite. A destroyed entity is never yielded afterwards.
- Not safe: destroying or removing a required component from a **not-yet-visited** entity of the filter being iterated. The swap-remove can move an already visited entity below the cursor and visit it twice. Under `KENSEI_DEBUG` this throws at the moment it would happen. Defer such changes with a `CommandBuffer`.

```csharp
// LeoEcsLite: allowed, applied after the loop
foreach (int e in _projectiles) {
    foreach (int t in _targets) {
        if (Hits(e, t)) {
            _world.DelEntity(e);
            _damage.Add(t);
        }
    }
}

// KenseiECS: record, then play back
foreach (int e in _projectiles) {
    foreach (int t in _targets) {
        if (Hits(e, t)) {
            _buffer.DestroyEntity(world.GetEntity(e));
            _buffer.Add(world.GetEntity(t), new DamageEvent { Value = 10 });
        }
    }
}
_buffer.Playback(world);
```

Keep one `CommandBuffer` per system; it allocates nothing after the first frame.

### `Remove` of a missing component is a no-op, `Add` of a duplicate throws

`Remove<T>` on an entity without `T` does nothing and does not create the pool, like Lite's `Del`. `Add<T>` on an entity that already has `T` throws `InvalidOperationException` in every build, where Lite checks only under `DEBUG`. Use `Get` to overwrite an existing component, or `CommandBuffer.Set` for add-or-overwrite.

### `Add` takes a value

Lite's `pool.Add(e)` returns a `ref` to a fresh component that you then fill (or that `IEcsAutoReset` initialized). KenseiECS's `Add(entity, value)` stores the value you pass and returns a `ref` to it. Initialization moves into the argument:

```csharp
// LeoEcsLite
ref var hp = ref healths.Add(e);
hp.Value = 100;

// KenseiECS
world.Add(e, new Health { Value = 100 });
```

### AutoReset runs on remove only

Lite calls `IEcsAutoReset<T>.AutoReset` both for new components on `Add` and for removed ones on `Del`, so code often allocates lists there and relies on `Add` returning a ready component. KenseiECS calls `IAutoReset<T>.AutoReset` for the removed component only (plus `Warmup` and `Clear`, so it must accept `default(T)`), and the moved component after a swap-remove is untouched. Components without the interface are reset to `default(T)`. Move allocation to the `Add` call site:

```csharp
// LeoEcsLite
struct Inventory : IEcsAutoReset<Inventory> {
    public List<int> Items;
    public void AutoReset(ref Inventory c) {
        c.Items ??= new List<int>();
        c.Items.Clear();
    }
}
ref var inv = ref inventories.Add(e);   // Items is ready

// KenseiECS
struct Inventory : IComponent, IAutoReset<Inventory> {
    public List<int> Items;
    public void AutoReset(ref Inventory c) {
        c.Items?.Clear();
        c.Items = null;
    }
}
world.Add(e, new Inventory { Items = new List<int>() });
```

### AutoCopy signature

Lite's `AutoCopy(ref T src, ref T dst)` replaces the default copy entirely. KenseiECS makes a shallow copy first and hands it to `AutoCopy(ref T c)` for deep-copying reference fields; value fields are already copied.

### Generation semantics

Lite stores a 16-bit generation per slot and invalidates it when the entity is deleted. KenseiECS stores a 32-bit generation that changes when the slot is **reused**, not when it is freed. `world.GetEntity(index)` on a dead slot therefore returns the same handle the entity had while alive (`IsAlive` is false for it), and a stored handle stays unequal to every future occupant of the slot. The practical rule is unchanged: keep `Entity` handles, never `int` indices, and check `IsAlive` before use.

```csharp
// LeoEcsLite
struct Target { public EcsPackedEntity Entity; }
target.Entity = world.PackEntity(enemy);
if (target.Entity.Unpack(world, out int enemy)) {
    ref var hp = ref healths.Get(enemy);
}

// KenseiECS
struct Target : IComponent { public Entity Entity; }
target.Entity = world.GetEntity(enemy);
if (world.IsAlive(target.Entity)) {
    ref var hp = ref world.Get<Health>(target.Entity);
}
```

`Entity` is `IEquatable<Entity>` with `==`/`!=`; `Entity.Null` is the "no entity" value.

### Exception safety

KenseiECS documents what happens when user code throws inside the framework:

- A world listener or `AutoReset` that throws during `DestroyEntity` still results in the slot being released; the exception propagates afterwards.
- A system that throws in `Run` skips the remaining systems for that frame, but OneFrame cleanup still runs.
- A system that throws in `Init` leaves the runner uninitialized; the next `Init` resumes with that system.
- A command that throws in `CommandBuffer.Playback` discards the remaining commands.

Do not assume the same in Lite; check its source for the paths you rely on.

### Debug layer: `KENSEI_DEBUG` instead of `DEBUG`

Lite's sanity checks compile in `DEBUG` builds (in Unity, the editor and development builds). KenseiECS uses its own define, `KENSEI_DEBUG`, independent of the build configuration: toggle it from the Unity menu **KenseiECS -> Debug Mode** (applies to all build targets), or add it to `DefineConstants` in a .NET project. Without the define no validation code exists in the assembly. What each configuration checks is tabulated in the README under "Release vs. KENSEI_DEBUG".

### Filters need an `Inc` or `Any`

Both frameworks reject exclude-only filters (Lite by requiring a type argument on `Filter<T>()`, KenseiECS by throwing from `End()`). KenseiECS additionally rejects a type in both `Inc` and `Exc`, or in both `Any` and `Exc`.

### `ref` validity

Same hazard in both: a `ref T` from `Get`/`Add` points into the pool's dense array and is invalidated by the next `Add` of that type (growth) or removal of that component (swap-remove). Re-acquire after structural changes.

### Destroy order of systems

KenseiECS runs `IDestroySystem.Destroy` in reverse registration order and makes the runner re-initializable afterwards. If your Lite code depends on a particular destroy order, verify it against Lite's implementation before relying on either.

### Groups: aligned iteration (no Lite equivalent)

Lite iterates a filter and reads each component through the pool's sparse array. KenseiECS has the same path, plus owning groups: `world.Group<Position, Velocity>()` keeps the two pools' dense arrays aligned so members sit at the same index in each, packed at the front, and exposes them as spans.

```csharp
// LeoEcsLite
foreach (int e in _moving) {
    ref var pos = ref _positions.Get(e);
    ref var vel = ref _velocities.Get(e);
    pos.X += vel.X;
}

// KenseiECS, with a group
var pos = _group.Data1;
var vel = _group.Data2;
for (int i = 0; i < pos.Length; i++) {
    pos[i].X += vel[i].X;
}
```

A component type can be owned by one group only; filters over the same types keep working. Groups are for the few hottest loops, not a replacement for filters. Iterate downward when destroying members inside the loop.

### Change tracking (no Lite equivalent)

Lite has no notion of "changed since". KenseiECS tracks per pool after `TrackChanges()`: `Modify(e)` and `MarkChanged(e)` stamp a component with a world-wide version, `ChangedSince(e, version)` compares against a bookmark taken from `world.ChangeVersion`. `Get` does not mark, so code ported from Lite that writes through `Get` must switch to `Modify` where a consumer relies on the flag.

### Snapshots (no Lite equivalent)

Lite core has no save/load. `WorldSerializer.Save(world, stream)` writes every alive entity and component; `Load(world, stream)` restores them into an empty world with the same indices and generations, so `Entity` fields inside components stay valid. Unmanaged components are written bit-for-bit; a component with reference fields needs an `IComponentFormatter<T>` registered on the serializer.

### Generated Init instead of ecslite-di

`ecslite-di` fills wrapper fields by reflection when `systems.Inject(...)` runs. KenseiECS ships a Roslyn source generator that writes `Init` at compile time from attributes on plain fields. The class must be `partial`; custom setup goes into `partial void OnInit`.

```csharp
// LeoEcsLite + ecslite-di
public sealed class MovementSystem : IEcsRunSystem {
    private readonly EcsFilterInject<Inc<Position, Velocity>, Exc<Frozen>> _moving = default;
    private readonly EcsPoolInject<Position> _positions = default;
    private readonly EcsPoolInject<Velocity> _velocities = default;
    private readonly EcsSharedInject<GameShared> _shared = default;

    public void Run(IEcsSystems systems) {
        foreach (int e in _moving.Value) {
            ref Position pos = ref _positions.Value.Get(e);
            ref Velocity vel = ref _velocities.Value.Get(e);
            pos.X += vel.X * _shared.Value.DeltaTime;
        }
    }
}
// bootstrap: systems.Inject().Init();

// KenseiECS + generator
public sealed partial class MovementSystem : IRunSystem {
    [Inc(typeof(Position), typeof(Velocity))] [Exc(typeof(Frozen))]
    private Filter _moving;
    [Pool] private ComponentPool<Position> _positions;
    [Pool] private ComponentPool<Velocity> _velocities;
    [Shared] private GameShared _shared;

    public void Run(World world) {
        foreach (int e in _moving) {
            ref Position pos = ref _positions.Get(e);
            ref Velocity vel = ref _velocities.Get(e);
            pos.X += vel.X * _shared.DeltaTime;
        }
    }
}
// bootstrap: nothing extra; the generated Init runs in systems.Init()
```

Differences: the fields are the real `Filter`/`ComponentPool<T>` types, not wrappers with `.Value`; there is no runtime `Inject` step and no reflection; there is no `EcsWorldInject` because `World` is a parameter of `Run`; keyed shared data is `[Shared("key")]`. The generator DLL sits in `Plugins/` with the `RoslynAnalyzer` label and is picked up by Unity 2021.2+; a .NET project references the generator as an analyzer. Misuse is a compile error (KECS001-KECS005), not a runtime failure.

### EcsBootstrap as the Unity entry point

Lite's README shows a `MonoBehaviour` that creates the world and systems in `Start` and runs them in `Update`. KenseiECS provides that class:

```csharp
// LeoEcsLite
public sealed class Startup : MonoBehaviour {
    private EcsWorld _world;
    private IEcsSystems _systems;

    private void Start() {
        _world = new EcsWorld();
        _systems = new EcsSystems(_world, new GameShared());
        _systems
            .Add(new MovementSystem())
            .Init();
    }

    private void Update() {
        _systems?.Run();
    }

    private void OnDestroy() {
        _systems?.Destroy();
        _world?.Destroy();
    }
}

// KenseiECS
public sealed class GameBootstrap : EcsBootstrap {
    protected override void Configure(SystemsRunner update, SystemsRunner fixedUpdate, SystemsRunner lateUpdate, SharedData shared) {
        shared.Add(new GameShared());
        update.Add(new MovementSystem());
        fixedUpdate.Add(new PhysicsSystem());
        lateUpdate.Add(new SyncTransformSystem());
    }
}
```

`EcsBootstrap` creates the `World`, the `SharedData` and three runners in `Awake` (so other scripts can use `World` and `Shared` from their `Start`), calls `Warmup` or `Init` in `Start`, drives the runners from `Update`/`FixedUpdate`/`LateUpdate`, and destroys systems then world in `OnDestroy`. Two `EcsSystems` instances for Update and FixedUpdate become the `update` and `fixedUpdate` runners of one bootstrap. The editor windows find it through `IEcsWorldProvider`/`IEcsSystemsProvider`.

## Step-by-step migration

1. **Install** the package (Unity: git URL with `?path=/KenseiECS`; .NET: reference `KenseiECS.NET/KenseiECS.csproj`). Replace `using Leopotam.EcsLite;` with `using KenseiECS;`.
2. **Mark components** with `IComponent`. Convert `IEcsAutoReset<T>` to `IAutoReset<T>` and move any allocation it did on add to the `Add` call sites. Convert `IEcsAutoCopy<T>` to `IAutoCopy<T>` with the single-argument signature.
3. **Bootstrap**: `EcsWorld` -> `World`; `EcsSystems` -> `SystemsRunner`; wrap your shared object in a `SharedData` container. In Unity, subclass `EcsBootstrap` instead of writing the `MonoBehaviour` yourself.
4. **Systems**: change interfaces and signatures. Replace `systems.GetWorld()` with the `World` parameter and `systems.GetShared<T>()` with `shared.Get<T>()` in `Init`. Cache pools and filters in `Init` as before, or make the class `partial`, mark the fields with `[Inc]`/`[Exc]`/`[Any]`/`[Pool]`/`[Group]`/`[Shared]` and delete `Init`; the generator writes it. `ecslite-di` wrapper fields map one to one.
5. **Entity creation**: replace `NewEntity()` + first `Add` with `CreateEntity(firstComponent)`. Subsequent adds become `world.Add(entity, value)`.
6. **Stored references**: `EcsPackedEntity` -> `Entity`, `PackEntity` -> `GetEntity`, `Unpack` -> `IsAlive`.
7. **Filters**: `Filter<A>().Inc<B>()` -> `Filter().Inc<A>().Inc<B>()` or `Filter<Inc<A, B>>()`.
8. **One-frame components**: delete cleanup systems and register `OneFrame<T>()` or `DelHere<T>()` on the runner.
9. **Loops that modify other entities**: route through a `CommandBuffer`.
10. **Enable `KENSEI_DEBUG`** and run the game. The debug layer throws on stale handles, duplicate adds, unsafe removals during iteration and misuse of the runner, which surfaces most migration mistakes at their source.

### Before: a LeoEcsLite system

```csharp
using System.Collections.Generic;
using Leopotam.EcsLite;

public struct Position { public float X, Y; }
public struct Velocity { public float X, Y; }
public struct Frozen { }
public struct Expired { }

public sealed class GameShared {
    public float DeltaTime;
}

public sealed class MovementSystem : IEcsInitSystem, IEcsRunSystem {
    private EcsWorld _world;
    private EcsFilter _moving;
    private EcsPool<Position> _positions;
    private EcsPool<Velocity> _velocities;
    private EcsPool<Expired> _expired;
    private GameShared _shared;

    public void Init(IEcsSystems systems) {
        _world = systems.GetWorld();
        _moving = _world.Filter<Position>().Inc<Velocity>().Exc<Frozen>().End();
        _positions = _world.GetPool<Position>();
        _velocities = _world.GetPool<Velocity>();
        _expired = _world.GetPool<Expired>();
        _shared = systems.GetShared<GameShared>();
    }

    public void Run(IEcsSystems systems) {
        foreach (int e in _moving) {
            ref Position pos = ref _positions.Get(e);
            ref Velocity vel = ref _velocities.Get(e);
            pos.X += vel.X * _shared.DeltaTime;
            pos.Y += vel.Y * _shared.DeltaTime;
            if (pos.Y < 0f) {
                _expired.Add(e);
            }
        }
    }
}

public sealed class ExpiredCleanupSystem : IEcsRunSystem {
    public void Run(IEcsSystems systems) {
        EcsWorld world = systems.GetWorld();
        EcsFilter filter = world.Filter<Expired>().End();
        foreach (int e in filter) {
            world.DelEntity(e);
        }
    }
}

public sealed class Bootstrap {
    private EcsWorld _world;
    private IEcsSystems _systems;

    public void Start() {
        _world = new EcsWorld();
        _systems = new EcsSystems(_world, new GameShared { DeltaTime = 1f / 60f });
        _systems
            .Add(new MovementSystem())
            .Add(new ExpiredCleanupSystem())
            .Init();

        int e = _world.NewEntity();
        ref Position pos = ref _world.GetPool<Position>().Add(e);
        pos.X = 1f;
        ref Velocity vel = ref _world.GetPool<Velocity>().Add(e);
        vel.Y = -1f;
    }

    public void Update() {
        _systems.Run();
    }

    public void Stop() {
        _systems.Destroy();
        _world.Destroy();
    }
}
```

### After: the same system in KenseiECS

```csharp
using KenseiECS;

public struct Position : IComponent { public float X, Y; }
public struct Velocity : IComponent { public float X, Y; }
public struct Frozen : IComponent { }
public struct Expired : IComponent { }

public sealed class GameShared {
    public float DeltaTime;
}

public sealed class MovementSystem : IInitSystem, IRunSystem {
    private Filter _moving;
    private ComponentPool<Position> _positions;
    private ComponentPool<Velocity> _velocities;
    private GameShared _shared;

    public void Init(World world, SharedData shared) {
        _moving = world.Filter<Inc<Position, Velocity>, Exc<Frozen>>();
        _positions = world.Pool<Position>();
        _velocities = world.Pool<Velocity>();
        _shared = shared.Get<GameShared>();
    }

    public void Run(World world) {
        foreach (int e in _moving) {
            ref Position pos = ref _positions.Get(e);
            ref Velocity vel = ref _velocities.Get(e);
            pos.X += vel.X * _shared.DeltaTime;
            pos.Y += vel.Y * _shared.DeltaTime;
            if (pos.Y < 0f) {
                world.Add(world.GetEntity(e), new Expired());
            }
        }
    }
}

public sealed class ExpiredCleanupSystem : IInitSystem, IRunSystem {
    private Filter _expired;

    public void Init(World world, SharedData shared) {
        _expired = world.Filter<Inc<Expired>>();
    }

    public void Run(World world) {
        foreach (int e in _expired) {
            world.DestroyEntity(world.GetEntity(e));
        }
    }
}

public sealed class Bootstrap {
    private World _world;
    private SystemsRunner _systems;

    public void Start() {
        _world = new World();
        var shared = new SharedData();
        shared.Add(new GameShared { DeltaTime = 1f / 60f });

        _systems = new SystemsRunner(_world, shared)
            .Add(new MovementSystem())
            .Add(new ExpiredCleanupSystem());
        _systems.Init();

        Entity e = _world.CreateEntity(new Position { X = 1f });
        _world.Add(e, new Velocity { Y = -1f });
    }

    public void Update() {
        _systems.Run();
    }

    public void Stop() {
        _systems.Destroy();
        _world.Destroy();
    }
}
```

Points to notice in the diff:

- `Add` on the current entity inside the loop is fine in both frameworks; adding `Expired` does not touch the `_moving` filter's membership.
- `ExpiredCleanupSystem` destroys the current entity of the filter it iterates, which is safe. The filter is built once in `Init` because `End()` allocates and scans the registry; Lite tolerated building it per frame.
- If `Expired` were the only component of some entity, KenseiECS's `DelHere<Expired>()` would replace the whole cleanup system: removing the last component destroys the entity.
- `Init()` is not chainable on `SystemsRunner`; `Add` returns the runner, `Init` returns `void`.

### After, with the generator

The same `MovementSystem` without a hand-written `Init`:

```csharp
public sealed partial class MovementSystem : IRunSystem {
    [Inc(typeof(Position), typeof(Velocity))] [Exc(typeof(Frozen))]
    private Filter _moving;
    [Pool] private ComponentPool<Position> _positions;
    [Pool] private ComponentPool<Velocity> _velocities;
    [Shared] private GameShared _shared;

    public void Run(World world) {
        foreach (int e in _moving) {
            ref Position pos = ref _positions.Get(e);
            ref Velocity vel = ref _velocities.Get(e);
            pos.X += vel.X * _shared.DeltaTime;
            pos.Y += vel.Y * _shared.DeltaTime;
            if (pos.Y < 0f) {
                world.Add(world.GetEntity(e), new Expired());
            }
        }
    }
}
```

The generated part adds `IInitSystem` and an `Init` that builds the filter, fetches the pools and reads the shared object, then calls `partial void OnInit(World world, SharedData shared)` if you implement it. The bootstrap is unchanged.
