# FAQ

Short answers. The mechanisms behind them are in [architecture.md](architecture.md); the API is in the [root README](../README.md).

### Why no archetypes?

KenseiECS stores each component type in its own sparse set. Adding or removing a component is O(1) and touches only that type's pool, filters are sparse sets updated incrementally, a `ref T` survives changes to other component types, and the number of optional components does not multiply storage layouts. Archetype ECSs (Arch, Unity Entities) keep entities with identical component sets in contiguous chunks, which makes pure iteration faster and every structural change more expensive, since the entity's whole component set moves between chunks.

The README's benchmark table shows the trade on 10,000 entities: Arch iterates two components through a filter roughly two and a half times faster than KenseiECS, while KenseiECS does add+remove several times faster than Arch and wins the mixed game-loop frame. Gameplay code with one-frame components and event entities is structural-change heavy, and that is what the framework is built for. Where a hot loop does need contiguous data, an owning group (`world.Group<Position, Velocity>()`) gives it for a chosen set of types; in the same table the group loop runs at archetype speed.

### Why must `CreateEntity` take a component?

Because an entity is alive if and only if it has at least one component. An empty entity would be invisible to every filter and every listener, and would be destroyed by the first `Remove` anyway. Requiring the first component at creation removes that state instead of detecting it later:

```csharp
Entity e = world.CreateEntity(new Position { X = 1f });
world.Add(e, new Velocity());
```

`OnEntityCreated` fires after the first component is stored, so listeners never observe an entity without components. `CommandBuffer.CreateEntity<T>(T)` follows the same rule.

### Why auto-destroy?

Same invariant from the other side: removing the last component destroys the entity. Nothing leaks, and event entities (an entity whose only component is a one-frame component) disappear in the one-frame cleanup with no bookkeeping. If you need an entity to outlive its data, give it a tag component. The one API where this was surprising, `Unsubscribe<T>`, keeps the empty `Listeners<T>` component so the entity stays alive.

### Is it thread-safe?

No. `World`, pools, filters, `CommandBuffer` and `SystemsRunner` must be used from one thread. Reading `RawData`/`RawEntities` from several threads is safe only while no structural change (`Add`, `Remove`, `CreateEntity`, `DestroyEntity`, `Clear`) happens on any thread. `ComponentType<T>.Index` registration is thread-safe. `EventBuffer<T>` uses a static per-type list pool shared by all worlds in the process, which is not thread-safe either.

### How do I store a reference to another entity in a component?

Store an `Entity`, never an `int`. Filters yield `int` slot indices, which are valid only until the end of the current iteration; the slot can be reused by a different entity later. `Entity` carries a generation and stays safe forever:

```csharp
public struct Target : IComponent {
    public Entity Enemy;
}

foreach (int e in _seekers) {
    ref var target = ref _targets.Get(e);
    target.Enemy = world.GetEntity(enemyIndex);   // convert inside the loop
}

// later
if (world.IsAlive(target.Enemy)) {
    ref var hp = ref world.Get<Health>(target.Enemy);
}
```

Use `Entity.Null` for "no entity"; it never matches a real entity. `Entity` supports `==`, `!=` and `IEquatable<Entity>`.

### How do I safely destroy entities inside a loop?

Destroying the **current** entity is safe; iteration is reverse and swap-remove only moves already visited entities:

```csharp
foreach (int e in _expired) {
    world.DestroyEntity(world.GetEntity(e));
}
```

Destroying or removing a required component from a **not-yet-visited** entity of the filter you are iterating is not safe (an already visited entity may be visited again; `KENSEI_DEBUG` throws). Record such changes in a `CommandBuffer` and play it back after the loop:

```csharp
private readonly CommandBuffer _buffer = new CommandBuffer();

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

Commands targeting an entity that is dead at playback are skipped, so "destroy if still alive" needs no check.

### Why does `Add` throw on a duplicate but `Remove` is a no-op?

Adding a component that is already there is almost always a logic error (two systems both think they own it), and silently overwriting would hide it, so `Add` throws in every build. Overwrite deliberately with `Get`, or use `CommandBuffer.Set` for add-or-overwrite. Removing a component that is absent expresses an intent ("make sure this is gone") that is already satisfied, so it is idempotent, mirrors `DestroyEntity` on a dead entity, and does not even create the pool.

### How many component types can I have?

There is no fixed limit. Type indices are assigned on first touch; the mask grows one 64-bit word per 64 types when the first pool in that range is created or a filter constrains it; the pool registry and the per-type filter tables grow to the highest index seen. Per-frame operations never loop over registered types. With about 1000 types the mask is 16 words, 128 bytes per entity slot.

The cost to watch is per pool: every pool has a sparse `int` array that grows to cover the highest entity index that ever held that component (starting at `WorldConfig.InitialPoolSparseCapacity`, doubling), plus dense arrays proportional to the number of components. A type that lives on few, low-index entities stays small; a type touched by an entity at index N costs at least 4 × (N + 1) bytes of sparse array. `ComponentPoolBase.AllocatedBytes` and `World.ActivePools` show the actual numbers. Filters are cheaper: their sparse side is paged, 4 KB per touched block of 1024 entity slots.

### Does it work with IL2CPP? With Burst?

IL2CPP: yes. The hot types carry `Il2CppSetOption` to drop null and bounds checks, and the `IAutoReset`/`IAutoCopy` bridges use closed delegates over a boxed `default(T)` rather than runtime generic instantiation, which would fail under AOT. Nothing uses `Reflection.Emit` or runtime code generation.

Burst: no. `World`, pools and filters are managed classes over managed arrays, and a Burst-compiled job cannot touch them. To run a Burst job over component data, copy the relevant `RawData` range into a `NativeArray<T>`, run the job, and copy the results back on the main thread while no structural change happens.

### How do I run the tests?

```
dotnet test KenseiECS.Tests -c Release
dotnet test KenseiECS.Tests -c Release -p:KenseiDebug=true
```

The second command compiles with `KENSEI_DEBUG` and additionally runs the tests of the validation layer. CI runs both on every push.

### How do I enable debug validation?

Unity: menu **KenseiECS -> Debug Mode**. It adds the `KENSEI_DEBUG` scripting define to every build target, which turns on handle validation, the iteration guard, runner checks, the World Inspector, the Profiler window and per-system timings. Toggle it again to remove the define; release builds without it contain no validation code.

.NET: add the define to the project that compiles the sources.

```xml
<PropertyGroup>
  <DefineConstants>$(DefineConstants);KENSEI_DEBUG</DefineConstants>
</PropertyGroup>
```

If you compile `KenseiECS/Core` and `KenseiECS/Systems` directly instead of referencing `KenseiECS.NET/KenseiECS.csproj`, also include `KenseiECS/DevTools`; `World` references `EcsProfiler` when the define is set. The test project shows the pattern with its `KenseiDebug` property.

### When should I use a Group instead of a Filter?

Use a `Filter` by default. Use an owning group for a hot loop over a fixed set of two to four component types where the per-entity sparse lookups are the measurable cost. `world.Group<Position, Velocity>()` keeps those pools' dense arrays aligned, so the loop reads `Data1[i]` and `Data2[i]` directly:

```csharp
var pos = _group.Data1;
var vel = _group.Data2;
for (int i = 0; i < pos.Length; i++) {
    pos[i].X += vel[i].X;
}
```

The constraints that make it a deliberate choice: a component type can be owned by one group only (a second group over `Position` throws), `Group<A, B>` and `Group<B, A>` are different requests, membership is exactly "has all owned types" with no `Exc`/`Any`, and adding or removing an owned component on a member costs one swap per owned pool. Filters over grouped types keep working. Iterate downward when destroying members inside the loop.

### How do I know a component changed?

Turn tracking on for the pool once, write through `Modify` (or call `MarkChanged` after writing another way), and compare against a bookmark:

```csharp
public void Init(World world, SharedData shared) {
    _positions = world.Pool<Position>();
    _positions.TrackChanges();
}

public void Run(World world) {
    foreach (int e in _views) {
        if (_positions.ChangedSince(e, _lastSeen)) {
            SyncTransform(e);
        }
    }
    _lastSeen = world.ChangeVersion;
}

// producer
_positions.Modify(e).X += 1f;
```

`Get` never marks a change, so writes through `Get`, `RawData` or group spans are invisible unless you call `MarkChanged`. Versions come from one world-wide counter that every `Add`, `Modify` and `MarkChanged` advances, so the bookmark separates "before my last run" from "after" exactly, whatever the system order. A newly added component counts as changed. Tracking costs 4 bytes per component and one store per write; pools that do not call `TrackChanges` pay nothing.

### How do I save and load a world?

```csharp
var serializer = new WorldSerializer();
serializer.Register(new InventoryFormatter());   // only for components with reference fields

using (var file = File.Create(path)) {
    serializer.Save(world, file);
}

world.Clear();
using (var file = File.OpenRead(path)) {
    serializer.Load(world, file);
}
```

`Save` writes every alive entity (index and generation) and every component, identifying types by name. `Load` needs an empty world (`Clear()` it or create a new one), restores the same indices and generations so stored `Entity` handles stay valid, and goes through the normal `Add` path, so filters and groups that exist before `Load` are filled; world event listeners and the profiler are not notified. Unmanaged components are written bit-for-bit; a component holding a `List`, `string` or object needs an `IComponentFormatter<T>` with `Write(BinaryWriter, ref T)` and `Read(BinaryReader, out T)`, otherwise `Save` throws. `Register<T>()` without a formatter pre-resolves the pool accessor, which matters under IL2CPP for a type no code touches before loading. Not stored: listeners, debug names, filter and group registrations.

### Do I have to write Init by hand?

No. Make the system class `partial`, put attributes on the fields, and the source generator (`Plugins/KenseiECS.Generators.dll`, picked up by Unity 2021.2+; reference the generator as an analyzer in .NET) writes `Init`:

```csharp
public sealed partial class MovementSystem : IRunSystem {
    [Inc(typeof(Position), typeof(Velocity))] [Exc(typeof(Frozen))]
    private Filter _moving;
    [Pool] private ComponentPool<Position> _positions;
    [Group] private Group<Position, Velocity> _group;
    [Shared("enemies")] private SpawnConfig _enemies;

    partial void OnInit(World world, SharedData shared) {
        // optional, runs after the fields are filled
    }

    public void Run(World world) { }
}
```

The generated part implements `IInitSystem`, builds the filter with the same `world.Filter()...End()` calls you would write (so it deduplicates like any other filter), fetches pools, groups and shared data, and calls `OnInit`. No reflection at runtime. Mistakes are compile errors: KECS001 class not `partial`, KECS002 a hand-written `Init` next to injected fields, KECS003 attribute on a field of the wrong type, KECS004 `[Exc]` without `[Inc]`/`[Any]`, KECS005 a containing type not `partial`. Hand-written `Init` keeps working for classes without injected fields.

### How do I set up a Unity scene?

1. Subclass `EcsBootstrap` and register systems in `Configure`; put the subclass on a GameObject:

   ```csharp
   public sealed class GameBootstrap : EcsBootstrap {
       protected override void Configure(SystemsRunner update, SystemsRunner fixedUpdate, SystemsRunner lateUpdate, SharedData shared) {
           shared.Add(new ArenaConfig());
           update.Add(new MovementSystem()).OneFrame<BounceEvent>();
           fixedUpdate.Add(new BounceSystem());
           lateUpdate.Add(new SyncTransformSystem());
       }
   }
   ```

   `Awake` creates the `World`, the `SharedData` and the three runners, so other scripts can read `bootstrap.World` and `bootstrap.Shared` from their `Start`. `Start` calls `Warmup()` (or `Init()` when "Warmup On Start" is off in the inspector). `Update`, `FixedUpdate` and `LateUpdate` drive the matching runner; `OnDestroy` destroys the systems, then the world.

2. To author entities in the inspector, declare one provider per component type and put providers on a prefab next to an `EcsEntityView`:

   ```csharp
   public sealed class PositionProvider : EcsComponentProvider<Position> { }
   ```

   The component struct must be `[Serializable]`. Spawn with `view.Spawn(world)`: the first provider creates the entity, the rest add their components in component order, the view's `Entity Name` field becomes the debug name under `KENSEI_DEBUG`, and the view is bound. Set `Destroy Entity With GameObject` on the view if the entity should die with the object.

3. Editor windows: **KenseiECS -> Systems** (runner tree, enable toggles, timings under `KENSEI_DEBUG`) works in any build; **World Inspector** and **Profiler** need **KenseiECS -> Debug Mode**. They find the bootstrap through `IEcsWorldProvider`/`IEcsSystemsProvider`.

`Samples~/BasicGame` (Package Manager -> Samples) is a complete scene built this way.
