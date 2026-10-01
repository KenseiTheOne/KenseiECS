# Bootstrap & Authoring

The Unity layer is a thin set of `MonoBehaviour`s on top of the Core. All of it compiles only inside Unity (`UNITY_2018_1_OR_NEWER`); the .NET build has none of it.

## EcsBootstrap

`EcsBootstrap` owns the world, the shared data and three runners bound to `Update`, `FixedUpdate` and `LateUpdate`. Subclass it, register systems in `Configure` and put the subclass on a GameObject:

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

It implements `IEcsWorldProvider` and `IEcsSystemsProvider`, so the World Inspector, Profiler and Systems windows find it automatically (see [Debug Tools](./debug-tools.md)).

### Lifecycle

| Unity message | What the bootstrap does |
|---|---|
| `Awake` | Creates the `World` (from `CreateConfig()`), the `SharedData` and the three runners, calls `Configure`, then attaches the fixed and late runners to the update runner as the named phases `"fixed"` and `"late"`. |
| `Start` | Calls `Systems.Warmup()`, or only `Systems.Init()` when **Warmup On Start** is unticked in the inspector (on by default). |
| `Update` / `FixedUpdate` / `LateUpdate` | Runs the matching runner, but only once `Systems.IsInitialized` is true. |
| `OnDestroy` | Destroys the systems, then the world. |

Because everything is created in `Awake`, `World` and `Shared` are already usable from other scripts' `Start`.

::: tip Why the IsInitialized guard
`Init` can throw part-way through. Without the guard the phases would run systems that never initialized every frame (or throw every frame under `KENSEI_DEBUG`). A failed `Init` resumes from the failing system on the next call — see the [lifecycle contract](../concepts/runner.md).
:::

The fixed and late runners are nested, named runners of the root one, so:

- `Init` and `Destroy` on the root cascade to them, while each phase is still run by its own Unity message;
- they are constructed without their own `SharedData` and inherit the root's — every system gets the same `shared`;
- only the root runner (driven by `Update`) advances the world tick;
- each runner removes its own `OneFrame` components at the end of its own `Run`. An event raised in `FixedUpdate` and read in `Update` must be registered with `update.OneFrame<T>()` — the [sample](./sample.md) does exactly that. See [OneFrame](../guides/one-frame.md).

### Members

| Member | Description |
|---|---|
| `World` | The world this bootstrap owns. Created in `Awake`. |
| `Systems` | Root runner, driven by `Update`. `FixedUpdateSystems` and `LateUpdateSystems` are its `"fixed"` and `"late"` phases. |
| `FixedUpdateSystems` | Runner driven by `FixedUpdate`. |
| `LateUpdateSystems` | Runner driven by `LateUpdate`. |
| `Shared` | Shared data passed to every system's `Init`. |
| `Configure(update, fixedUpdate, lateUpdate, shared)` | Abstract. Register systems, one-frame components and shared data. Called once from `Awake`. |
| `CreateConfig()` | Virtual. Returns `WorldConfig.Default()`; override to tune initial capacities (see [WorldConfig](../guides/world-config.md)). |

A GameObject can carry only one bootstrap (`[DisallowMultipleComponent]`).

A fuller version, with an inspector-assigned config, a tuned world capacity and a named system:

```csharp
public sealed class GameBootstrap : EcsBootstrap {
    [SerializeField] private GameConfig _config;

    protected override WorldConfig CreateConfig() {
        var config = WorldConfig.Default();   // or new WorldConfig { ... }: fields left at 0 fall back to their defaults
        config.InitialEntityCapacity = 4096;
        return config;
    }

    protected override void Configure(SystemsRunner update, SystemsRunner fixedUpdate, SystemsRunner lateUpdate, SharedData shared) {
        shared.Add(_config);

        update
            .Add(new InputSystem())
            .Add(new MovementSystem(), "movement")
            .OneFrame<DamageEvent>();

        fixedUpdate.Add(new PhysicsSystem());
        lateUpdate.Add(new SyncTransformSystem());
    }
}
```

### Without EcsBootstrap

The editor windows only need the two provider interfaces, so a hand-written entry point works too — implement them on any `MonoBehaviour`:

```csharp
public class MyBootstrap : MonoBehaviour, IEcsWorldProvider, IEcsSystemsProvider {
    public World World { get; private set; }
    public SystemsRunner Systems { get; private set; }
}
```

## Authoring components in the inspector

Components can be authored in the inspector:

1. Derive `EcsComponentProvider<T>` once per component type. Unity serializes the field of a generic base only through a non-generic subclass:

   ```csharp
   public sealed class HealthProvider : EcsComponentProvider<Health> { }
   ```

   `T` must be a `struct` implementing `IComponent`, and it must be `[Serializable]` to show up in the inspector. The authored value is exposed as `ref T Value`.

2. Put providers on a prefab next to an [`EcsEntityView`](./entity-view.md).
3. Call `view.Spawn(world)` to build the entity from them.

```csharp
var view = Instantiate(prefab);          // prefab: EcsEntityView
Entity entity = view.Spawn(world);       // creates the entity from the providers and binds the view
```

`Spawn` collects the providers on the GameObject in component order: the first one creates the entity, the rest add their components to it. It throws `InvalidOperationException` if the GameObject has no provider — an entity needs at least one component.

See `Samples~/BasicGame` (Package Manager -> Samples) and the [BasicGame Sample](./sample.md) page.
