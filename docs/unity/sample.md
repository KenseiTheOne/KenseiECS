# BasicGame Sample

Entities bounce around a rectangular arena. The sample is small, but touches most of the Unity layer:

- [`EcsBootstrap`](./bootstrap.md) — `GameBootstrap` owns the world and registers systems per phase (Update: movement and the event consumer; FixedUpdate: wall bounces; LateUpdate: transform sync).
- [`OneFrame<BounceEvent>`](../guides/one-frame.md) — an event raised in FixedUpdate and consumed in Update.
- [`EcsComponentProvider<T>`](./bootstrap.md#authoring-components-in-the-inspector) — `PositionProvider` and `VelocityProvider` author component values in the inspector.
- [`EcsEntityView.Spawn`](./entity-view.md#spawning-from-providers) — `Spawner` turns prefab instances into entities.
- [`SharedData`](../concepts/systems.md) — the arena size travels from the bootstrap's inspector to the systems.

## Importing

Open **Window -> Package Manager**, select **KenseiECS**, open the **Samples** tab and click **Import** next to **Basic Game**. Unity copies the scripts into `Assets/Samples/KenseiECS/<version>/Basic Game`. The source lives in the package under `Samples~/BasicGame`.

## Scene setup

The sample ships scripts only; the scene takes a minute to build:

1. Import it as described above.
2. Create an empty GameObject `Bootstrap` and add `GameBootstrap`. Leave **Warmup On Start** on.
3. Build the ball prefab:
   - **GameObject -> 3D Object -> Sphere**, scale 0.3, remove the collider.
   - Add `EcsEntityView`, `PositionProvider` and `VelocityProvider`.
   - Set Velocity to something like X = 3, Y = 2. The spawner randomizes the direction and keeps the speed.
   - Optionally fill in **Entity Name** on the view. With Debug Mode on, the name shows in the editor windows and in the bounce log.
   - Drag the object into the Project window to make a prefab, then delete it from the scene.
4. Create an empty GameObject `Spawner`, add `Spawner`, assign the prefab and set **Count** (20 by default).
5. Point the camera at the arena: position (0, 0, -10), Orthographic, Size 5. The arena is 16 x 9 units by default (**Arena Half Size** on the bootstrap, 8 x 4.5).
6. Press Play.

## Scripts

| File | Contents |
|---|---|
| `Components.cs` | `Position` and `Velocity` (`[Serializable]`, a `Vector2` each, so providers can edit them), `BounceEvent` (one-frame event carrying the wall `Normal`), `TransformRef` (links an entity to the `Transform` that displays it). |
| `PositionProvider.cs`, `VelocityProvider.cs` | One-line `EcsComponentProvider<Position>` / `EcsComponentProvider<Velocity>` subclasses. |
| `GameBootstrap.cs` | `ArenaConfig` (the arena `HalfSize`) and the `EcsBootstrap` subclass. |
| `Spawner.cs` | Instantiates the prefab and spawns entities. |
| `Systems.cs` | The four systems. |

### GameBootstrap

```csharp
public sealed class GameBootstrap : EcsBootstrap {
    [SerializeField] private Vector2 _arenaHalfSize = new(8f, 4.5f);

    protected override void Configure(SystemsRunner update, SystemsRunner fixedUpdate, SystemsRunner lateUpdate, SharedData shared) {
        shared.Add(new ArenaConfig { HalfSize = _arenaHalfSize });

        // Each runner removes its OneFrame components at the end of its own Run.
        // BounceSystem raises the event in FixedUpdate and BounceLogSystem reads it
        // in Update, so the cleanup belongs to the update runner.
        update
            .Add(new MovementSystem())
            .Add(new BounceLogSystem())
            .OneFrame<BounceEvent>();

        fixedUpdate.Add(new BounceSystem());

        lateUpdate.Add(new SyncTransformSystem());
    }
}
```

### Spawner

In `Start`, `Spawner` finds the `GameBootstrap`, reads the world and `ArenaConfig` from it, then `Count` times:

```csharp
var view = Instantiate(_prefab, transform);
var entity = view.Spawn(world);

world.Get<Position>(entity).Value = new Vector2(
    Random.Range(-halfSize.x, halfSize.x),
    Random.Range(-halfSize.y, halfSize.y));

ref var velocity = ref world.Get<Velocity>(entity);
velocity.Value = Quaternion.Euler(0f, 0f, Random.Range(0f, 360f)) * velocity.Value;

world.Add(entity, new TransformRef { Value = view.transform });
```

The prefab therefore needs an `EcsEntityView`, a `PositionProvider` and a `VelocityProvider`. Reading `World` and `Shared` from `Start` is safe because the bootstrap creates them in `Awake`.

### Systems

| System | Phase | What it does |
|---|---|---|
| `MovementSystem` | Update | Integrates `Velocity` into `Position` using `Time.deltaTime`. |
| `BounceLogSystem` | Update | Logs every `BounceEvent` raised since the last frame, with the entity's debug name when it has one. |
| `BounceSystem` | FixedUpdate | Clamps entities to the arena from `ArenaConfig`, reflects their velocity off the walls and adds a `BounceEvent` with the wall normal. |
| `SyncTransformSystem` | LateUpdate | Copies `Position` into the linked `Transform`. |

`BounceEvent` is added in FixedUpdate, read by `BounceLogSystem` in the next Update and removed at the end of that Update by the update runner's `OneFrame<BounceEvent>()`.

## Things to try

- **KenseiECS -> Systems** shows the runner tree. Untick `BounceLogSystem` to stop the console output, or the `fixed` phase to stop the bouncing.
- Turn on **KenseiECS -> Debug Mode**, then open **KenseiECS -> World Inspector**: the Entities tab lists the balls with their names and editable components, the Filters and Pools tabs show memory usage.
- Add `EcsProfiler.Enable(bootstrap.World)` (inside `#if KENSEI_DEBUG`) to a script's `Start` and open **KenseiECS -> Profiler** to watch the `BounceEvent` components come and go every frame.

See [Debug Tools](./debug-tools.md) for what each window shows.
