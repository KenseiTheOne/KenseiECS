# FAQ

Short answers. The mechanisms behind them are in [architecture.md](architecture.md); the API is in the [root README](../README.md).

### Why no archetypes?

KenseiECS stores each component type in its own sparse set. Adding or removing a component is O(1) and touches only that type's pool, filters are sparse sets updated incrementally, a `ref T` survives changes to other component types, and the number of optional components does not multiply storage layouts. Archetype ECSs (Arch, Unity Entities) keep entities with identical component sets in contiguous chunks, which makes pure iteration faster and every structural change more expensive, since the entity's whole component set moves between chunks.

The README benchmark shows the trade: on 10,000 entities Arch iterates two components in 5.5 us against KenseiECS's 13.9 us, while KenseiECS does add+remove in 90.8 us against Arch's 592.9 us and wins the mixed game-loop frame. Gameplay code with one-frame components and event entities is structural-change heavy, and that is what the framework is built for.

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
