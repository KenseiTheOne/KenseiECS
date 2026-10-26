# KenseiECS: rules for AI agents

You are writing game code on KenseiECS. To give these rules to an agent in a game project, add a line to the project's `CLAUDE.md` or `AGENTS.md` that points to this file (or to its page on the site), or copy it there. These rules are what agents most often get wrong when they carry object-oriented habits into an ECS. Follow them without being asked; the [documentation](https://kenseitheone.github.io/KenseiECS/) has the details behind each one.

## 1. A marker component switches a system on, not an `if` in `Run`

When a system has work only while something holds (a wave is running, a screen is open, a unit has a target), express that as a tag component on an entity and filter on it. No tag: the filter is empty, the system's loop runs zero times and costs nothing.

```csharp
public struct WaveActive : IComponent { }          // the condition, as data

[Inc(typeof(WaveActive), typeof(Spawner))] private Filter _spawners;

public void Run(World world) {
    foreach (int e in _spawners) { /* only while a wave runs */ }
}
```

Do not write `if (!state.WaveActive) return;` or poll flags every frame. The systems that change the condition add and remove the tag; a one-time reaction is a [one-frame component](https://kenseitheone.github.io/KenseiECS/guides/one-frame). `Exc<...>` expresses "not in this state".

## 2. Components are data; systems do the work

A component holds fields and nothing else: no methods with game logic, no dispatchers, no hidden buffers or state machines. A behaviour that "belongs to" a component is a system that filters on it.

## 3. Events are components

When something happens (damage, a death, a pickup), do not call the systems that care and do not raise a flag: add a component that marks the event. The systems that care filter on it; a registered cleanup removes it.

```csharp
public struct Damaged : IComponent { public float Amount; }

update.Add(new CombatSystem())        // world.Add(target, new Damaged { Amount = 10 })
      .Add(new HealthBarSystem())     // [Inc(typeof(Damaged), typeof(HealthBarRef))]
      .Add(new HitSoundSystem())      // [Inc(typeof(Damaged))]
      .OneFrame<Damaged>();
```

Who sees the event depends on where the cleanup is, so decide it when you add an event type:

- `OneFrame<T>()` removes it at the end of that runner's `Run`. The systems after the producer see it in the same frame; a system registered before the producer never sees it, because the event is gone before that system runs again.
- `DelHere<T>()` removes it at that point of the pipeline. An event created after the point lives until the point in the next frame, so the systems before the producer see it then, one frame late.
- An event created outside that `Run` (a Unity callback, another phase such as `fixed` or `late`) lives until the next cleanup, usually into the next frame.

For several events on one entity in a frame use `EventBuffer<T>` with `world.AddEvent`.

## 4. Views are called directly by a system

- One view per entity: keep a reference in a component (`struct HealthBarRef : IComponent { public IHealthBar View; }`) and call it from a system that filters on the event and that component.
- Any number of subscribers (a HUD following the player): `world.Subscribe<IListener>(entity, view)`, and the system walks the list itself:

```csharp
var listeners = _listeners.Get(e).Values;
for (int i = 0; listeners != null && i < listeners.Count; i++) {
    listeners[i].OnDamage(damage);
}
```

Walk such a `List<T>` with `for`: `foreach` over it allocates nothing either, but under IL2CPP its enumerator of a list of interfaces runs through shared generic code and costs 1.5 to 3 times as much. A callback must not change the list it is called from (no subscribing or unsubscribing on that entity, no removing the component, no destroying the entity). To leave from inside a callback, call `world.UnsubscribeLater<IListener>(entity, this)` with the listener type given explicitly; it applies at the end of the tick. `Listeners<T>.Invoke`, `Add` and `Remove` are obsolete.

## 5. Store `Entity`, never an `int` index

Filters yield `int` slot indices that are valid for the current loop. Anything kept across frames (a target, a parent, a link) is an `Entity` handle, and code checks `world.IsAlive(entity)` before using it: a slot is reused by a new entity after a destroy.

## 6. Structural changes inside a filter loop

Inside `foreach` over a filter it is safe to destroy the current entity, add or remove components on it and create entities. Destroying or changing *other* entities of the same filter can make the loop visit an entity twice: record those changes in a `CommandBuffer` and `Playback` it after the loop.

## 7. A `ref` lives until the next Add or Remove of that type

`ref var p = ref pool.Get(e)` is valid until the next `Add` or `Remove` of the same component type on any entity (destroying an entity removes its components). Do not keep it across such calls; take it again.

## 8. Hot paths do not allocate

No LINQ, lambdas that capture, `params`, boxing of structs or `new` of reference types in `Run`. `foreach` over a filter is the normal loop: its enumerator is a struct and allocates nothing. Reserve storage for the peak at load (`world.ReserveEntities(n)` and `world.Pool<T>().Reserve(count)` for the types whose count changes during play, in `EcsBootstrap.Configure`), and let systems create their pools and filters in `Init`, not during play.

## 9. Develop with `KENSEI_DEBUG`, ship without it

`KENSEI_DEBUG` (KenseiECS -> Debug Mode) checks handles, re-entrancy and growth past a reserve, and names entities for the debug windows. A release build with it fails on purpose; keep it on while writing and testing systems.

## 10. Inject with the generator

Mark the fields of a `partial` system with `[Inc]`, `[Exc]`, `[Any]`, `[Pool]`, `[Group]` and `[Shared]` instead of writing `Init` by hand; put the rest of the setup in `partial void OnInit`.
