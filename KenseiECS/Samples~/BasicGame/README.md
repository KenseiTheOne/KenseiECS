# Basic Game

Entities bounce around a rectangular arena. The sample shows:

- `EcsBootstrap` — `GameBootstrap` owns the world and registers systems per phase
  (Update: movement and the event consumer; FixedUpdate: wall bounces; LateUpdate: transform sync).
- `OneFrame<BounceEvent>` — an event raised in FixedUpdate and consumed in Update.
- `EcsComponentProvider<T>` — `PositionProvider` and `VelocityProvider` author component values in the inspector.
- `EcsEntityView.Spawn` — `Spawner` turns prefab instances into entities.
- `SharedData` — the arena size travels from the bootstrap's inspector to the systems.

## Setup

The sample ships scripts only; the scene takes a minute to build:

1. Import it from Package Manager → KenseiECS → Samples → Basic Game.
2. Create an empty GameObject `Bootstrap` and add `GameBootstrap`. Leave `Warmup On Start` on.
3. Build the ball prefab:
   - `GameObject → 3D Object → Sphere`, scale 0.3, remove the collider.
   - Add `EcsEntityView`, `PositionProvider` and `VelocityProvider`.
   - Set Velocity to something like X = 3, Y = 2. The spawner randomizes the direction and keeps the speed.
   - Optionally fill in `Entity Name` on the view. With Debug Mode on, the name shows in the editor windows and in the bounce log.
   - Drag the object into the Project window to make a prefab, then delete it from the scene.
4. Create an empty GameObject `Spawner`, add `Spawner`, assign the prefab and set `Count`.
5. Point the camera at the arena: position (0, 0, -10), Orthographic, Size 5.
   The arena is 16 x 9 units by default (`Arena Half Size` on the bootstrap).
6. Press Play.

## Things to try

- `KenseiECS → Systems` shows the runner tree. Untick `BounceLogSystem` to stop the console output,
  or the `fixed` phase to stop the bouncing.
- Turn on `KenseiECS → Debug Mode`, then open `KenseiECS → World Inspector`: the Entities tab lists the
  balls with their names and editable components, the Filters and Pools tabs show memory usage.
- Add `EcsProfiler.Enable(bootstrap.World)` to a script's `Start` and open `KenseiECS → Profiler`
  to watch the `BounceEvent` components come and go every frame.
