# Inside the Demo

You played [Horde](./demo.md); this page walks through the code behind it. It shows how a frame is put together and then a handful of systems, each section picked for one KenseiECS idea, with the code taken straight from the game's sources. The whole game is in [`Demo/Game`](https://github.com/KenseiTheOne/KenseiECS/tree/main/Demo/Game): components in `Components.cs`, wiring in `HordeGame.cs`, one file per system in `Systems/`.

## How a frame runs

Everything systems share that is not a component (config, the spatial grid, the sprite buffer, the random generator) goes into one [`SharedData`](../concepts/systems.md#shareddata):

<<< @/../Demo/Game/HordeGame.cs#shared-data{csharp}

The 22 systems are split into two named phases, `sim` and `render`, that hang off one root [`SystemsRunner`](../concepts/runner.md). The root owns `SharedData`. The demo calls only `Warmup`, which runs `Init` (cascading to both phases) and then pre-touches pools and filters so the first frames allocate less:

<<< @/../Demo/Game/HordeGame.cs#runner{csharp}

Each frame the host calls `HordeGame.Tick`. Input and the frame time go into [singletons](../guides/singletons.md) on the game entity, `sim` runs only while the game is playing, and `render` runs every frame, so the level-up screen still draws the frozen horde. Because the phases are driven directly rather than through the root's `Run`, the tick advances `World.NextTick` itself ([nested runners](../concepts/runner.md#nested-runners)):

<<< @/../Demo/Game/HordeGame.cs#tick{csharp}

**Try it in the demo:** open the inspector (<kbd>&#96;</kbd> or the **ECS** button) and untick the `render` phase. The sprites disappear while the simulation and the camera keep going.

## `integrate`: an owning group

**In the game:** moves everything that has a velocity: the player, every enemy, bolts, gems flying to the player, sparks. With 20,000 entities on screen this is the loop that has to be cheap.

**The idea:** an owning [`Group<Position, Velocity>`](../guides/groups.md). The group keeps the dense arrays of both pools aligned, so entity *i* of the group is at index *i* in both. The loop walks two plain spans, with no filter and no sparse lookups. `[Group]` is filled by the [generated `Init`](../guides/source-generator.md).

<<< @/../Demo/Game/Systems/IntegrateSystem.cs#integrate{csharp}

Membership follows the components: when `magnet` adds a `Velocity` to a gem, the gem joins the group and `integrate` starts moving it, with no change to this system.

**Try it in the demo:** untick `integrate`. The horde and the player freeze: only `integrate` turns velocity into movement. Blades and the nova ring, which are placed directly, keep going.

## `bolt-fire`: structural changes through a CommandBuffer

**In the game:** whenever the cooldown is up, fires a volley of bolts at the nearest enemy.

**The idea:** the system creates entities while it iterates the `_shooters` filter. Calling `World.CreateEntity` inside the loop would be safe here: the [iteration contract](../concepts/filters.md#iteration-contract) allows creating entities in a `foreach`, and new bolts would not match `_shooters` anyway. The system records the spawns in a [`CommandBuffer`](../guides/command-buffer.md) instead to batch them: all of them are applied in one `Playback` after the loop. `CreateEntity` on the buffer returns a `PendingEntity` that later `Add` commands of the same buffer can target, so several components can be added to an entity that does not exist yet. The buffer is a field: one buffer per system, which stops allocating once its capacity has settled. A buffer becomes necessary when a loop changes *other* entities, ones the iteration has not visited yet.

<<< @/../Demo/Game/Systems/BoltWeaponSystem.cs#bolt-fire{csharp}

[`loot`](https://github.com/KenseiTheOne/KenseiECS/blob/main/Demo/Game/Systems/LootSystem.cs) uses the same pattern for the opposite change: while iterating the dead enemies it records the gem, the sparks and the `DestroyEntity` of the enemy, then plays the buffer back once after the loop.

**Try it in the demo:** untick `bolt-fire`. No new bolts appear; the ones already in flight finish their path. Untick `loot` instead and enemies keep taking hits but nothing dies or drops gems.

## `bolt-hit` → `damage` + `knockback`: events with two consumers

**In the game:** a bolt that touches an enemy deals damage and pushes the enemy back. One enemy can be hit by several bolts, a blade and the nova in the same frame.

**The idea:** a plain component holds one value per entity, so hits are collected in an `EventBuffer<Hit>` with `world.AddEvent`, which appends any number of events to an entity in one frame ([several events per entity](../guides/one-frame.md#several-events-per-entity-per-frame)). `bolt-hit`, `blades`, `nova` and `contact` all produce `Hit` events; `contact` sends zero-damage hits that only carry knockback.

Inside `bolt-hit`, the loop walks the spatial grid `g` around each bolt; every enemy `j` that overlaps the bolt gets a `Hit`, with knockback along the bolt's velocity `v`:

<<< @/../Demo/Game/Systems/BoltHitSystem.cs#add-event{csharp}

Two systems read the same events and know nothing about each other or about the producers. `damage` sums the damage and marks the dead with `Died`:

<<< @/../Demo/Game/Systems/DamageSystem.cs#consume-hits{csharp}

`knockback` adds up the push, but only for light enemies: tanks and elites carry the `Heavy` tag, and the [`[Exc]`](../concepts/filters.md#building-a-filter) filter simply leaves them out:

<<< @/../Demo/Game/Systems/KnockbackSystem.cs#knockback{csharp}

**When they are cleared:** `EventBuffer<Hit>`, `Died` and `XpGained` are registered as [OneFrame](../guides/one-frame.md) on the `sim` runner. The runner removes them at the end of its `Run`, after `lifetime`, the last system, so every consumer between the producers and the end of the phase sees them, and nobody sees them the next frame. The pooled event lists are recycled, not reallocated. This is why the order in the runner matters: producers first, consumers after them.

<<< @/../Demo/Game/HordeGame.cs#one-frame{csharp}

**Try it in the demo:** untick `knockback`. Hits still kill, but light enemies are no longer shoved back. Untick `damage` instead: bolts still hit and push enemies, but nothing dies.

## `player-move`: a generated `Init`

**In the game:** turns the input singleton into the player's velocity; `integrate` does the actual moving.

**The idea:** the system is a `partial` class with marked fields and no `Init`. The [source generator](../guides/source-generator.md) writes `Init` for it: the `[Inc]` field gets a filter built with `world.Filter()`, each `[Pool]` field gets `world.Pool<T>()`, and the runner calls the generated `Init` like any hand-written one.

<<< @/../Demo/Game/Systems/PlayerMoveSystem.cs#player-move{csharp}

`[Shared]` fields are filled the same way from the runner's `SharedData`. `grid`, for example, receives the spatial grid and the config it was given in [How a frame runs](#how-a-frame-runs):

<<< @/../Demo/Game/Systems/GridBuildSystem.cs#fields{csharp}

**Try it in the demo:** hold a direction and untick `player-move` while moving. The player keeps drifting the same way: `Velocity` was last written by `player-move`, and `integrate` keeps applying it.

## Things worth stealing

- **Entity handles, not int indices, for stored references.** [`Bolt.LastHit`](https://github.com/KenseiTheOne/KenseiECS/blob/main/Demo/Game/Components.cs) remembers the enemy a piercing bolt hit last, across frames. It is an `Entity`: an int index could name a newer enemy once the slot is reused. Indices are fine within the current iteration. See [handles vs. indices](../concepts/entities.md#handles-vs-indices).
- **A spatial grid as a shared service.** [`SpatialGrid`](https://github.com/KenseiTheOne/KenseiECS/blob/main/Demo/Game/Shared/SpatialGrid.cs) is a plain class in `SharedData`, rebuilt once a frame by [`grid`](https://github.com/KenseiTheOne/KenseiECS/blob/main/Demo/Game/Systems/GridBuildSystem.cs) with a counting sort and queried by `steer`, `bolt-fire`, `bolt-hit`, `blades`, `nova` and `contact`. It stores a copy of each enemy's position and radius, so neighbour queries never touch the component pools.
- **Zero allocations in steady state.** Capacities are set up front in [`WorldConfig`](../guides/world-config.md), `Warmup` pre-touches pools and filters, every system keeps its own `CommandBuffer`, and events reuse pooled lists. Once pool, filter and buffer capacities have settled, a frame allocates nothing; [`Demo/Headless`](https://github.com/KenseiTheOne/KenseiECS/tree/main/Demo/Headless) measures it.
- **Recycle instead of destroy and create.** Enemies and gems left far behind are moved ahead of the player instead of being destroyed and spawned again ([`GridBuildSystem.cs`](https://github.com/KenseiTheOne/KenseiECS/blob/main/Demo/Game/Systems/GridBuildSystem.cs), [`MagnetSystem.cs`](https://github.com/KenseiTheOne/KenseiECS/blob/main/Demo/Game/Systems/MagnetSystem.cs)).
- **One system, one idea.** Each of the 22 systems is short and does one thing, so any of them can be switched off live from the inspector to see what it does. The [system table](./demo.md#what-to-look-at) lists the feature each one shows.
