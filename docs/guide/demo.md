<script setup>
import { ref } from 'vue'
import { withBase } from 'vitepress'

const playing = ref(false)
const frame = ref(null)
const demoUrl = withBase('/demo/')
const embedUrl = withBase('/demo/?embed=1')

function focusFrame() {
  // Keyboard input goes to the focused document: hand it to the game.
  frame.value?.focus()
}
</script>

# Live Demo

**Horde** is a small survivors-like game written on KenseiECS and running in your browser on .NET WebAssembly. Move, the weapons fire on their own; the horde grows wave after wave until thousands of enemies chase you at once. The STRESS slider multiplies the spawn rate to push the world past 20,000 live entities, and the ECS inspector shows every system's time per frame.

<div class="horde-demo">
  <iframe
    v-if="playing"
    ref="frame"
    :src="embedUrl"
    title="Horde — KenseiECS live demo"
    allow="fullscreen; gamepad"
    allowfullscreen
    @load="focusFrame"
  ></iframe>
  <button v-else type="button" class="horde-demo__poster" @click="playing = true">
    <span class="horde-demo__play" aria-hidden="true">▶</span>
    <span class="horde-demo__title">Play Horde</span>
    <span class="horde-demo__note">Loads the .NET WebAssembly runtime and the game — about 2 MB compressed. Nothing is downloaded until you click.</span>
  </button>
</div>

<p class="horde-demo__links">
  <a :href="demoUrl" target="_blank" rel="noopener">Open fullscreen ↗</a>
</p>

## Controls

| Action | Input |
|---|---|
| Start | Click the game, or press <kbd>Enter</kbd> / <kbd>Space</kbd> |
| Move | <kbd>W</kbd> <kbd>A</kbd> <kbd>S</kbd> <kbd>D</kbd> or arrow keys; on touch screens, drag anywhere for a virtual stick |
| Pick an upgrade on level-up | Click / tap a card, or press <kbd>1</kbd> <kbd>2</kbd> <kbd>3</kbd> |
| Pause | <kbd>Esc</kbd> or <kbd>P</kbd> |
| Restart after game over | Click / tap **Restart**, or press <kbd>R</kbd> / <kbd>Enter</kbd> |
| ECS inspector | <kbd>`</kbd> or the **ECS** button: entity count, frame time, filters, pools and systems; untick a system to switch it off live |
| Push the ECS | **STRESS** slider in the inspector (×0.25 to ×25 spawn rate and enemy cap); **God mode** keeps you alive while you watch |

Weapons fire automatically: a bolt at the nearest enemy, orbiting blades and a periodic nova pulse once you pick them. Enemies drop XP gems; walking near pulls them in. Every level offers up to three different upgrades — damage, fire rate, extra projectiles, pierce, move speed, magnet radius, max HP, or a new weapon — and the panel under the health bar shows the level of each one you have taken. Recovery (heal 50%) only appears in the last slot once fewer than three upgrades are left to max; when everything is maxed, each level-up heals you without stopping the run.

## What to look at

The game is 22 systems in two named phases of a [`SystemsRunner`](../concepts/runner.md). Each one is short and shows one KenseiECS feature; the inspector lists them with their timings, and you can switch any of them off to see what it does. The sources are in [`Demo/Game/Systems/`](https://github.com/KenseiTheOne/KenseiECS/tree/main/Demo/Game/Systems). For a guided tour of the code behind some of them, see [Inside the Demo](./inside-the-demo.md).

### Simulation phase (`sim`)

Runs only while the game is playing; a level-up or game over pauses it.

| System | What it does | KenseiECS feature |
|---|---|---|
| `clock` | Advances game time | The smallest possible system: a hand-written `IRunSystem` with no `Init`, a [singleton](../guides/singletons.md) read by `ref` |
| `player-move` | Applies input to the player | [Generated `Init`](../guides/source-generator.md) (`[Inc]` and `[Pool]` fields), `GetSingleton<PlayerInput>` |
| `spawner` | Ramping waves, walls of fast enemies, elites | `World.CreateEntity` + `Add` outside iteration, a [`[Shared]`](../concepts/systems.md#shareddata) config, a tag component (`Heavy`) |
| `grid` | Rebuilds the spatial grid around the player; enemies left far behind reappear ahead | [SharedData](../concepts/systems.md#shareddata) holding a mutable service object; filter plus direct pool reads and writes, zero allocations |
| `steer` | Chase plus soft separation from neighbours | Reaching components by entity index through [pools](../concepts/components.md#access): sparse-set O(1) `Get` |
| `integrate` | Moves everything that has a velocity | An owning [`Group<Position, Velocity>`](../guides/groups.md): the hot loop walks two aligned spans with no lookups |
| `bolt-fire` | Fires bolts at the nearest enemy | [`CommandBuffer`](../guides/command-buffer.md) and `PendingEntity`: spawns are recorded during iteration and created on playback |
| `bolt-hit` | Bolts hit enemies | `EventBuffer<Hit>` + `world.AddEvent` ([several events per entity per frame](../guides/one-frame.md#several-events-per-entity-per-frame)); destroying the current entity inside `foreach` |
| `blades` | Orbiting blades | A [filter](../concepts/filters.md) that starts matching the moment the upgrade adds `BladeWeapon` |
| `nova` | Expanding ring that hits everything it crosses | Two filters in one system; an entity created through a `CommandBuffer` and destroyed in place |
| `contact` | Contact damage, invulnerability frames | Singletons written by `ref`; a `Hit` event used purely as a knockback carrier |
| `knockback` | Pushes light enemies back | An [`[Exc(Heavy)]` filter](../concepts/filters.md#building-a-filter); a second consumer of the same `Hit` events |
| `damage` | Applies hits, marks the dead | Consuming a [OneFrame](../guides/one-frame.md) `EventBuffer<Hit>`; adding the OneFrame `Died` to the entity being iterated |
| `loot` | Drops gems and sparks, removes the enemy | `CommandBuffer` playback after the iteration; past the gem cap, XP is folded into existing gems through `RawData` |
| `magnet` | Pulls gems to the player; gems left far behind are moved ahead | `[Exc(Magnetized)]`; adding `Velocity` moves a gem into the owning group; [event entities](../guides/one-frame.md#event-entities) for `XpGained` |
| `level-up` | Collects XP, pauses and offers upgrades | Event entities destroyed by the OneFrame cleanup at the end of the phase |
| `lifetime` | Expires bolts and particles | Destroying the current entity during reverse iteration — see the [iteration contract](../concepts/filters.md#iteration-contract) |

### Render phase (`render`)

A second named runner that fills the sprite buffer the page draws. It keeps running while `sim` is paused, so the level-up screen still shows the frozen horde. Everything outside the camera is culled.

| System | KenseiECS feature |
|---|---|
| `render-gems` | A separate named phase toggled independently of `sim` ([nested runners](../concepts/runner.md#nested-runners)) |
| `render-enemies` | Linear iteration over a pool's dense `RawData` / `RawEntities` — no filter needed when one component already identifies the set |
| `render-projectiles` | An [`[Any(Bolt, Blade, NovaRing)]`](../concepts/filters.md#any) filter |
| `render-particles` | A filter over a tag component plus data components |
| `render-player` | `GetSingletonEntity` |

::: tip Things to try
- Raise **STRESS** to ×20 with **God mode** on and watch the entity counter pass 20,000 (**Max out** is ×25, capped at 30,000 enemies). `steer` is the most expensive system; `grid` and `render-enemies` follow.
- Switch off `integrate`: the horde freezes, because nothing else moves positions.
- Switch off `steer`: enemies stop chasing and coast on their last velocity. Walk away and `grid` keeps bringing the stragglers back ahead of you.
- Switch off `knockback`: tanks and elites never were affected (they are `Heavy`), now nothing is.
- Switch off the whole `render` phase: the sprites disappear while the simulation and the camera keep going.
:::

## How it's built

- **Plain .NET, no Unity.** The game is a `net8.0` class library ([`Demo/Game`](https://github.com/KenseiTheOne/KenseiECS/tree/main/Demo/Game)) referencing the same `KenseiECS.NET` project and source generator as any .NET app. A thin browser host ([`Demo/Web`](https://github.com/KenseiTheOne/KenseiECS/tree/main/Demo/Web), `Microsoft.NET.Sdk.WebAssembly`, `net10.0`) calls `HordeGame.Tick` every animation frame and draws the sprite buffer on a canvas. A headless runner ([`Demo/Headless`](https://github.com/KenseiTheOne/KenseiECS/tree/main/Demo/Headless)) plays the same game in CI.
- **Built with `KENSEI_DEBUG`.** The deployed build defines [`KENSEI_DEBUG`](../guides/debug-mode.md), so the per-system timings in the inspector are real (`SystemInfo.LastRunMs` / `PeakRunMs`) and stale-handle misuse would throw. A release build is somewhat faster.
- **Interpreted, single-threaded.** The browser build runs on the .NET WebAssembly interpreter (no AOT, no `wasm-tools` workload) — tens of times slower than desktop .NET, where a 10,000-enemy frame takes about 0.5 ms. `Environment.ProcessorCount` is 1 in the browser, so a [`ParallelRunner`](../guides/threading.md#parallelrunner) would have no workers and run its jobs inline; the game does not use one.
- **No garbage in steady state.** After warm-up a frame allocates nothing; allocations happen only when a pool, filter or buffer doubles its capacity.
- **Small.** The published site is about 4.5 MB (about 1.8 MB gzipped on the wire): the runtime, a trimmed BCL and the game, with no globalization data. KenseiECS and the game assembly are kept untrimmed (`TrimmerRootAssembly`), because component-interface dispatch uses reflection the trimmer cannot see.

<style>
.horde-demo {
  position: relative;
  margin: 24px 0 8px;
  aspect-ratio: 16 / 10;
  border-radius: 12px;
  overflow: hidden;
  background: #0b0d12;
  border: 1px solid var(--vp-c-divider);
}
.horde-demo iframe {
  display: block;
  width: 100%;
  height: 100%;
  border: 0;
}
.horde-demo__poster {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 10px;
  width: 100%;
  height: 100%;
  padding: 16px;
  color: #e8eaf0;
  cursor: pointer;
  background:
    radial-gradient(circle at 50% 50%, rgba(80, 200, 255, 0.18), transparent 22%),
    radial-gradient(circle at 30% 35%, rgba(255, 70, 90, 0.35) 0 3px, transparent 4px) 0 0 / 46px 46px,
    radial-gradient(circle at 70% 60%, rgba(255, 150, 60, 0.3) 0 4px, transparent 5px) 0 0 / 61px 61px,
    #0b0d12;
}
.horde-demo__play {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 72px;
  height: 72px;
  padding-left: 6px;
  border-radius: 50%;
  font-size: 30px;
  background: var(--vp-c-brand-1);
  color: #fff;
  transition: transform 0.15s ease;
}
.horde-demo__poster:hover .horde-demo__play,
.horde-demo__poster:focus-visible .horde-demo__play {
  transform: scale(1.08);
}
.horde-demo__title {
  font-size: 22px;
  font-weight: 600;
}
.horde-demo__note {
  max-width: 420px;
  font-size: 13px;
  line-height: 1.5;
  text-align: center;
  opacity: 0.75;
}
.horde-demo__links {
  margin-top: 0;
  text-align: right;
}
</style>
