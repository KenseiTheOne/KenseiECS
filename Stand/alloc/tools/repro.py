"""Minimal C# repro per scenario: a complete console program against the KenseiECS sources.

Every scenario of the probe has a template here. A rendered repro is a folder with Program.cs and a
.csproj that compiles the KenseiECS sources of the tree under test (LangVersion 9, like Unity), so
`dotnet run -c Release` reproduces the numbers without the stand. Windows hold nothing but the
measured code: no Assert, no boxing, no string formatting between the two counter reads (an
Assert.That(allocated, Is.EqualTo(0)) in an optimized build would put the box of its 0 inside the
window and add 24 bytes). The program prints one line, REPRO {json}.

check() builds and runs rendered repros on the stand's runtimes and compares their numbers with the
stand's own measurement, so a repro that does not reproduce is reported as such.
"""
import json
import os
import re

import standlib as S

# ---------------------------------------------------------------------------
# shared C# pieces
# ---------------------------------------------------------------------------

VIEW = """public interface IView { void OnHit(int damage); }
public sealed class View : IView { public long Sum; public void OnHit(int damage) { Sum += damage; } }
"""

RUNNER_SYSTEMS = """public struct Mark : IComponent { public int V; }
public struct Mark2 : IComponent { public int V; }
public sealed class MoveSystem : IInitSystem, IRunSystem {
    Filter _filter; ComponentPool<Pos> _positions; ComponentPool<Vel> _velocities;
    public void Init(World world, SharedData shared) {
        _filter = world.Filter().Inc<Pos>().Inc<Vel>().End();
        _positions = world.Pool<Pos>();
        _velocities = world.Pool<Vel>();
    }
    public void Run(World world) {
        foreach (int e in _filter) { ref var p = ref _positions.Get(e); ref var v = ref _velocities.Get(e); p.X += v.X; p.Y += v.Y; }
    }
}
public sealed class MarkProducer<T> : IRunSystem where T : struct, IComponent {
    readonly Entity[] _targets; readonly int _count;
    public MarkProducer(Entity[] targets, int count) { _targets = targets; _count = count; }
    public void Run(World world) { for (int i = 0; i < _count; i++) world.Add(_targets[i], default(T)); }
}
public sealed class CountSystem : IRunSystem { public long Runs; public void Run(World world) { Runs++; } }
"""

RUNNER_WORLD = """var w = new World();
var e = new Entity[256];
for (int i = 0; i < e.Length; i++) { e[i] = w.CreateEntity(new Pos { X = i }); w.Add(e[i], new Vel { X = 1 }); }
"""

CARRIER_WORLD = """W = new World();
E = new Entity[{count}];
for (int i = 0; i < E.Length; i++) E[i] = W.CreateEntity(new Carrier {{ V = i }});
"""

# ---------------------------------------------------------------------------
# templates
#   types   extra type declarations          fields  static fields of class Repro
#   setup   statements of Main before the windows
#   first   body measured once (one-time bytes)
#   steady  body of Op(), measured warmup + ops times (bytes per operation)
#   spans   long run of Op() in three geometric spans (see AllocMeter.Spans); done = runs before
#   threads "all" counts every thread (GC.GetTotalAllocatedBytes(true))
#   scale   the stand ran `scale` times as many units as the repro (growth.first_n_pools)
# ---------------------------------------------------------------------------

T = {}


def add_remove(kind, comp, value, types="", prep="", fields="", note=""):
    T["add_remove." + kind] = dict(
        note=note or "Expected: 0 bytes per Add + Remove once the pool reached its peak; the first pass grows the pool.",
        types=types,
        fields="    static World W;\n    static Entity[] E;\n    static int I;\n" + fields,
        setup=CARRIER_WORLD.format(count=1024) + "W.Pool<%s>();\n%s" % (comp, prep)
        + "Op();   // code paths once (Add + Remove on one entity), outside the windows",
        first="for (int i = 0; i < E.Length; i++) W.Add(E[i], %s);\nfor (int i = 0; i < E.Length; i++) W.Remove<%s>(E[i]);" % (value, comp),
        steady="var e = E[I];\nif (++I == E.Length) I = 0;\nW.Add(e, %s);\nW.Remove<%s>(e);" % (value, comp),
        warmup=4000, ops=2000)


T["create_destroy"] = dict(
    note="Expected: the first pass grows the world from its defaults to 4096 entity slots; 0 bytes per create + destroy afterwards.",
    fields="    static World W;\n    static readonly Entity[] Buf = new Entity[4096];\n",
    setup="W = new World();\nW.Pool<Pos>();\nOp();   // code paths once, outside the windows",
    first="for (int i = 0; i < Buf.Length; i++) Buf[i] = W.CreateEntity(new Pos { X = i });\nfor (int i = 0; i < Buf.Length; i++) W.DestroyEntity(Buf[i]);",
    steady="W.DestroyEntity(W.CreateEntity(new Pos { X = 1 }));",
    warmup=4000, ops=2000)

add_remove("plain", "Pos", "default(Pos)")
add_remove("tag", "Tag", "default(Tag)")
add_remove("big", "Big", "new Big { A = 1, H = 8 }", types="public struct Big : IComponent { public long A, B, C, D, E, F, G, H; }\n")
add_remove("managed", "Managed", "new Managed { Ref = Shared, V = 1 }",
           types="public struct Managed : IComponent { public object Ref; public int V; }\n",
           fields="    static readonly object Shared = new object();\n")
add_remove("autoreset", "Resettable", "new Resettable { Ref = Shared, V = 1 }",
           types="public struct Resettable : IComponent, IAutoReset<Resettable> {\n"
                 "    public int V; public object Ref;\n"
                 "    public void AutoReset(ref Resettable c) { c.V = 0; c.Ref = null; }\n}\n",
           fields="    static readonly object Shared = new object();\n",
           note="Expected (docs/concepts/components.md): zero allocations per Remove; the IAutoReset bridge is created with the pool.")
add_remove("autocopy", "Copyable", "new Copyable { V = 1 }",
           types="public struct Copyable : IComponent, IAutoCopy<Copyable> {\n"
                 "    public int V; public int Copies;\n"
                 "    public void AutoCopy(ref Copyable c) { c.Copies++; }\n}\n")
add_remove("tracked", "Tracked", "new Tracked { V = 1 }", types="public struct Tracked : IComponent { public int V; }\n",
           prep="W.Pool<Tracked>().TrackChanges();\n")
add_remove("listener", "Listened", "new Listened { V = 1 }",
           types="public struct Listened : IComponent { public int V; }\n"
                 "public sealed class CountingListener : IComponentListener<Listened> {\n"
                 "    public long Count;\n"
                 "    public void OnAdded(int entityIndex, ref Listened c) { Count++; }\n"
                 "    public void OnRemoved(int entityIndex, ref Listened c) { Count--; }\n}\n",
           prep="W.Pool<Listened>().AddListener(new CountingListener());\n")
add_remove("group", "GroupA", "new GroupA { V = 1 }",
           types="public struct GroupA : IComponent { public int V; }\npublic struct GroupB : IComponent { public int V; }\n",
           prep="for (int i = 0; i < E.Length; i++) W.Add(E[i], new GroupB { V = i });\nW.Group<GroupA, GroupB>();   // owning group\n")
add_remove("filtered", "Filtered", "new Filtered { V = 1 }",
           types="public struct Filtered : IComponent { public int V; }\npublic struct Tag2 : IComponent { }\n",
           fields="    static readonly List<Filter> Keep = new List<Filter>();\n",
           prep="Keep.Add(W.Filter().Inc<Filtered>().End());\nKeep.Add(W.Filter().Inc<Filtered>().Inc<Carrier>().End());\n"
                "Keep.Add(W.Filter().Inc<Carrier>().Exc<Filtered>().End());\nKeep.Add(W.Filter().Any<Filtered>().Any<Tag2>().End());\n")

T["add_remove.eventbuffer"] = dict(
    note="Expected: 0 bytes per AddEvent + Remove once the static list pool holds the lists (EventBuffer.cs).",
    types="public struct Ev { public int V; }\n",
    fields="    static World W;\n    static Entity[] E;\n    static int I;\n",
    setup=CARRIER_WORLD.format(count=1024) + "W.Pool<EventBuffer<Ev>>();\nOp();   // code paths once, outside the windows",
    first="for (int i = 0; i < E.Length; i++) W.AddEvent(E[i], new Ev { V = i });\nfor (int i = 0; i < E.Length; i++) W.Remove<EventBuffer<Ev>>(E[i]);",
    steady="var e = E[I];\nif (++I == E.Length) I = 0;\nW.AddEvent(e, new Ev { V = 1 });\nW.Remove<EventBuffer<Ev>>(e);",
    warmup=4000, ops=2000)

ACCESS_WORLD = """W = new World();
E = new Entity[256];
for (int i = 0; i < E.Length; i++) { E[i] = W.CreateEntity(new Pos { X = i }); if (i % 2 == 0) W.Add(E[i], new Vel { X = 1 }); }
"""
ACCESS_FIELDS = "    static World W;\n    static Entity[] E;\n    static int I;\n    static long Sink;\n"
ACCESS_NOTE = ("Expected (README, introduction: zero allocations at runtime once pools, filters and buffers have grown to their "
               "peak capacity): 0 bytes per call, in release and under KENSEI_DEBUG.")

T["access.world_get_has"] = dict(
    note=ACCESS_NOTE, fields=ACCESS_FIELDS,
    setup=ACCESS_WORLD + "for (int i = 0; i < E.Length; i++) Op();   // code paths once, outside the windows",
    steady="var e = E[I];\nif (++I == E.Length) I = 0;\nref var p = ref W.Get<Pos>(e);\np.X += 1;\nif (W.Has<Vel>(e)) Sink++;",
    warmup=4000, ops=2000)
T["access.modify_mark_changed"] = dict(
    note=ACCESS_NOTE, types="public struct AccessTracked : IComponent { public int V; }\n",
    fields=ACCESS_FIELDS + "    static ComponentPool<AccessTracked> P;\n    static int Since;\n",
    setup=ACCESS_WORLD + "P = W.Pool<AccessTracked>();\nP.TrackChanges();\nfor (int i = 0; i < E.Length; i++) W.Add(E[i], new AccessTracked { V = i });\n"
          "Since = W.ChangeVersion;\nfor (int i = 0; i < E.Length; i++) Op();   // code paths once, outside the windows",
    steady="int idx = E[I].Index;\nif (++I == E.Length) I = 0;\nP.Modify(idx).V++;\nP.MarkChanged(idx);\nif (P.ChangedSince(idx, Since)) Sink++;",
    warmup=4000, ops=2000)
T["access.get_singleton"] = dict(
    note=ACCESS_NOTE, types="public struct AccessConfig : IComponent { public int V; }\n", fields=ACCESS_FIELDS,
    setup=ACCESS_WORLD + "W.Add(E[3], new AccessConfig { V = 1 });\nfor (int i = 0; i < 64; i++) Op();   // code paths once, outside the windows",
    steady="W.GetSingleton<AccessConfig>().V++;\nSink += W.GetSingletonEntity<AccessConfig>().Index;",
    warmup=4000, ops=2000)
T["access.group_data"] = dict(
    note=ACCESS_NOTE, types="public struct AccessGA : IComponent { public int V; }\npublic struct AccessGB : IComponent { public int V; }\n",
    fields=ACCESS_FIELDS + "    static Group<AccessGA, AccessGB> G;\n",
    setup=ACCESS_WORLD + "G = W.Group<AccessGA, AccessGB>();\nfor (int i = 0; i < E.Length; i++) { W.Add(E[i], new AccessGA { V = i }); W.Add(E[i], new AccessGB { V = 1 }); }\n"
          "for (int i = 0; i < 8; i++) Op();   // code paths once, outside the windows",
    steady="var entities = G.Entities;\nvar a = G.Data1;\nvar b = G.Data2;\nlong sum = 0;\nfor (int i = 0; i < G.Count; i++) { a[i].V += b[i].V; sum += entities[i]; }\nSink += sum;",
    warmup=400, ops=200)
T["access.filter_contains"] = dict(
    note=ACCESS_NOTE, fields=ACCESS_FIELDS + "    static Filter F;\n",
    setup=ACCESS_WORLD + "F = W.Filter().Inc<Pos>().Inc<Vel>().End();\nfor (int i = 0; i < 8; i++) Op();   // code paths once, outside the windows",
    steady="int n = 0;\nfor (int i = 0; i < E.Length + 64; i++) if (F.Contains(i)) n++;\nSink += n;",
    warmup=400, ops=200)

T["add_remove.listeners"] = dict(
    note="Documented (Listeners.cs): removing the component clears the list and drops it, so every new subscription creates a list.",
    types=VIEW,
    fields="    static World W;\n    static Entity[] E;\n    static int I;\n    static readonly IView ViewInstance = new View();\n",
    setup=CARRIER_WORLD.format(count=1024) + "W.Pool<Listeners<IView>>();\nfor (int i = 0; i < E.Length; i++) Op();",
    steady="var e = E[I];\nif (++I == E.Length) I = 0;\nW.Subscribe<IView>(e, ViewInstance);\nW.Remove<Listeners<IView>>(e);",
    warmup=4000, ops=2000)


def events_first(prewarm):
    return dict(
        note=("Expected (EventBuffer.Prewarm): after Prewarm(64, 8) the first peak frame allocates nothing."
              if prewarm else "Documented (EventBuffer.cs): without Prewarm the first peak frame creates the lists; later frames allocate nothing."),
        types="public struct Ev { public int V; }\n",
        fields="    static World W;\n    static SystemsRunner Systems;\n    static Entity[] Targets;\n",
        setup="""W = new World();
Targets = new Entity[64];
for (int i = 0; i < Targets.Length; i++) Targets[i] = W.CreateEntity(new Carrier { V = i });
Systems = new SystemsRunner(W).OneFrame<EventBuffer<Ev>>();
Systems.Init();
for (int i = 0; i < Targets.Length; i++) W.Add(Targets[i], new EventBuffer<Ev>());   // grows the buffer pool, rents no lists
Systems.Run();
W.AddEvent(Targets[0], new Ev());   // every code path once, with a single list
Systems.Run();
""" + ("EventBuffer<Ev>.Prewarm(64, 8);" if prewarm else "// no Prewarm"),
        first="for (int i = 0; i < Targets.Length; i++) for (int k = 0; k < 8; k++) W.AddEvent(Targets[i], new Ev());\nSystems.Run();",
        steady="for (int i = 0; i < Targets.Length; i++) for (int k = 0; k < 8; k++) W.AddEvent(Targets[i], new Ev());\nSystems.Run();",
        warmup=80, ops=40)


T["events.first_frame_prewarm"] = events_first(True)
T["events.first_frame_no_prewarm"] = events_first(False)

T["events.stable_frame"] = dict(
    note="Expected (EventBuffer.cs): once the pool holds the lists alive at the peak, a OneFrame<EventBuffer<T>> frame allocates nothing.",
    types="public struct Ev { public int V; }\n",
    fields="    static World W;\n    static SystemsRunner Systems;\n    static Entity[] Targets;\n",
    setup="""W = new World();
Targets = new Entity[64];
for (int i = 0; i < Targets.Length; i++) Targets[i] = W.CreateEntity(new Carrier { V = i });
Systems = new SystemsRunner(W).OneFrame<EventBuffer<Ev>>();
Systems.Init();
Op(); Op();   // the peak twice: the lists are created and return to the pool""",
    steady="for (int i = 0; i < Targets.Length; i++) for (int k = 0; k < 8; k++) W.AddEvent(Targets[i], new Ev());\nSystems.Run();",
    warmup=80, ops=40)

T["events.writeback_frames"] = dict(
    note=("The write-back that docs/guides/command-buffer.md documents (var b = world.Get<EventBuffer<Hit>>(e); b.Add(h); cb.Set(e, b);) "
          "with OneFrame in an inline group and Playback at the end of the frame. Expected (EventBuffer.cs): 0 bytes once the pool holds "
          "the lists alive at the peak (9 entities). Measured in three geometric spans; the control without the write-back stays at 0."),
    types="""public struct Ev { public int V; }
public sealed class EventProducer : IRunSystem {
    readonly Entity[] _targets; public int Active;
    public EventProducer(Entity[] targets, int active) { _targets = targets; Active = active; }
    public void Run(World world) { for (int i = 0; i < Active; i++) world.AddEvent(_targets[i], default(Ev)); }
}
public sealed class AppendViaSet : IInitSystem, IRunSystem {
    readonly CommandBuffer _cb; Filter _filter;
    public AppendViaSet(CommandBuffer cb) { _cb = cb; }
    public void Init(World world, SharedData shared) { _filter = world.Filter().Inc<EventBuffer<Ev>>().End(); }
    public void Run(World world) {
        foreach (int i in _filter) {
            var e = world.GetEntity(i);
            var b = world.Get<EventBuffer<Ev>>(e);   // a copy: the same List
            b.Add(default(Ev));
            _cb.Set(e, b);                          // documented write-back, applied at Playback
        }
    }
}
public sealed class PlaybackSystem : IRunSystem {
    readonly CommandBuffer _cb;
    public PlaybackSystem(CommandBuffer cb) { _cb = cb; }
    public void Run(World world) { _cb.Playback(world); }
}
""",
    fields="    static SystemsRunner Root;\n    static EventProducer Producer;\n    static ComponentPool<EventBuffer<Ev>> Buffers;\n    static Entity[] Targets;\n",
    setup="""var w = new World();
Targets = new Entity[9];
for (int i = 0; i < Targets.Length; i++) Targets[i] = w.CreateEntity(new Carrier { V = i });
var cb = new CommandBuffer();
Producer = new EventProducer(Targets, 8);
var combat = new SystemsRunner(w).Add(Producer).Add(new AppendViaSet(cb)).OneFrame<EventBuffer<Ev>>();
Root = new SystemsRunner(w).Add(combat).Add(new PlaybackSystem(cb));
Root.Init();
Buffers = w.Pool<EventBuffer<Ev>>();
Op(); Op();
Producer.Active = 9;   // the 9th entity starts receiving events""",
    steady="Root.Run();",
    spans=True, done=2, warmup="{span_warmup}",
    after="""int shared = 0;
for (int i = 0; i < Targets.Length; i++)
    for (int j = i + 1; j < Targets.Length; j++)
        if (Buffers.Has(Targets[i].Index) && Buffers.Has(Targets[j].Index)
            && Buffers.Get(Targets[i].Index).Values != null
            && ReferenceEquals(Buffers.Get(Targets[i].Index).Values, Buffers.Get(Targets[j].Index).Values)) shared++;""",
    extra_print=[("shared_pairs", "shared")])


def invoke(kind, body, note):
    T["invoke." + kind] = dict(
        note=note, types=VIEW, first_bcl=True,
        fields="    static ComponentPool<Listeners<IView>> P;\n    static int Index;\n    static int Damage = 3;\n    static Exception LastError;\n",
        setup="""var w = new World();
var entity = w.CreateEntity(new Carrier());
for (int i = 0; i < 8; i++) w.Subscribe<IView>(entity, new View());
P = w.Pool<Listeners<IView>>();
Index = entity.Index;""",
        first=body, steady=body, warmup=4000, ops=2000,
        methods="    static void OnHit(IView view, int damage) => view.OnHit(damage);\n")


invoke("static_lambda", "P.Get(Index).Invoke(3, static (v, d) => v.OnHit(d));",
       "Expected (Listeners.cs): with the TArg overload and a static lambda the call allocates nothing.")
invoke("static_lambda_onerror", "P.Get(Index).Invoke(3, static (v, d) => v.OnHit(d), static (v, d, ex) => LastError = ex);",
       "Expected (docs/unity/listener-bridge.md): static lambdas for both call and onError allocate nothing.")
invoke("method_group", "P.Get(Index).Invoke(3, OnHit);   // C# 9: a new Action<IView, int> on every call",
       "C# 9 (Unity) converts a method group to a new delegate on every call; C# 11+ caches it for a static method. "
       "The docs promise 0 bytes only for a static lambda.")
invoke("capturing_lambda", "int damage = Damage;\nP.Get(Index).Invoke(v => v.OnHit(damage));   // closure + delegate per call",
       "Documented (Listeners.cs): a capturing lambda allocates a closure per call.")
invoke("manual_loop", "var values = P.Get(Index).Values;\nfor (int i = 0; i < values.Count; i++) values[i].OnHit(3);",
       "A hand-written loop over Listeners.Values: allocates nothing.")


def runner(kind, build, note):
    T["runner." + kind] = dict(
        note=note, types=RUNNER_SYSTEMS,
        fields="    static SystemsRunner Root;\n",
        setup=RUNNER_WORLD + build,
        first="Root.Run();", steady="Root.Run();", warmup=400, ops=200)


runner("inline_groups", """var child = new SystemsRunner(w).Add(new CountSystem()).Add(new MoveSystem());
var off = new SystemsRunner(w).Add(new CountSystem()).Add(new MoveSystem());
Root = new SystemsRunner(w)
    .Add(new MoveSystem())
    .Add(new MarkProducer<Mark>(e, 32))
    .Add(child)                // unnamed inline group
    .Add(new CountSystem())    // slot 3, switched off below
    .Add(off)                  // slot 4: an unnamed inline group, switched off below
    .Add(new CountSystem())
    .OneFrame<Mark>();
Root.Init();
Root.SetActive(3, false);
Root.SetActive(4, false);""", "Expected: 0 bytes per Run once pools and filters have grown (one-frame Mark added to 32 entities per frame).")
runner("named_group", """var group = new SystemsRunner(w).Add(new CountSystem()).Add(new MoveSystem());
Root = new SystemsRunner(w)
    .Add(new MoveSystem())
    .Add(new MarkProducer<Mark>(e, 32))
    .AddGroup("group", group)
    .Add(new CountSystem())
    .OneFrame<Mark>();
Root.Init();
Root.SetActive("group", false);""", "Expected: 0 bytes per Run with a named inline group switched off.")
runner("clean_when_disabled", """var group = new SystemsRunner(w).Add(new CountSystem()).OneFrame<Mark2>(true);
Root = new SystemsRunner(w)
    .Add(new MarkProducer<Mark2>(e, 32))
    .Add(group)                // slot 1, switched off: its cleanup still removes Mark2
    .Add(new MoveSystem());
Root.Init();
Root.SetActive(1, false);""", "Expected: 0 bytes per Run while the disabled group's cleanWhenDisabled cleanup removes 32 markers per frame.")

T["commandbuffer.playback"] = dict(
    note="Expected (CommandBuffer.cs): after the first frame the buffer allocates nothing.",
    types="public struct Tag2 : IComponent { }\n",
    fields="    static World W;\n    static CommandBuffer Cb;\n    static Entity A, B;\n    static readonly PendingEntity[] P = new PendingEntity[8];\n    static int N;\n",
    setup="""W = new World();
W.Pool<Pos>(); W.Pool<Vel>(); W.Pool<Tag>(); W.Pool<Tag2>();
A = W.CreateEntity(new Pos());
B = W.CreateEntity(new Carrier());
Cb = new CommandBuffer();
Op();                      // the code paths once, on a throwaway buffer
Cb = new CommandBuffer();  // the measured buffer starts empty""",
    first="""for (int i = 0; i < P.Length; i++) { P[i] = Cb.CreateEntity(new Pos { X = i }); Cb.Add(P[i], new Tag()); Cb.Set(P[i], new Vel { X = i }); }
Cb.Set(A, new Pos { X = ++N });
Cb.Add(B, new Tag2());
Cb.Remove<Tag2>(B);
for (int i = 0; i < P.Length; i++) Cb.DestroyEntity(P[i]);
Cb.Playback(W);""",
    steady="""for (int i = 0; i < P.Length; i++) { P[i] = Cb.CreateEntity(new Pos { X = i }); Cb.Add(P[i], new Tag()); Cb.Set(P[i], new Vel { X = i }); }
Cb.Set(A, new Pos { X = ++N });
Cb.Add(B, new Tag2());
Cb.Remove<Tag2>(B);
for (int i = 0; i < P.Length; i++) Cb.DestroyEntity(P[i]);
Cb.Playback(W);""",
    warmup=400, ops=200)


def copy_entity(kind, types, prep, note):
    T["copy_entity." + kind] = dict(
        note=note, types=types,
        fields="    static World W;\n    static Entity Source;\n",
        setup="W = new World();\nSource = W.CreateEntity(new Pos { X = 1 });\nW.Add(Source, new Tag());\n" + prep,
        first="W.DestroyEntity(W.CopyEntity(Source));", steady="W.DestroyEntity(W.CopyEntity(Source));",
        warmup=4000, ops=2000)


copy_entity("autocopy", "public struct Copyable : IComponent, IAutoCopy<Copyable> {\n    public int V; public int Copies;\n"
                        "    public void AutoCopy(ref Copyable c) { c.Copies++; }\n}\n",
            "W.Add(Source, new Copyable { V = 7 });", "Expected: 0 bytes per CopyEntity + DestroyEntity with an IAutoCopy that allocates nothing.")
copy_entity("eventbuffer", "public struct Ev { public int V; }\n",
            "W.AddEvent(Source, new Ev { V = 1 });\nW.AddEvent(Source, new Ev { V = 2 });",
            "Expected: the copy rents its own list from the static pool, destroying it returns the list: 0 bytes per copy afterwards.")
copy_entity("listeners", VIEW, "for (int i = 0; i < 3; i++) W.Subscribe<IView>(Source, new View());",
            "Documented (Listeners.cs): CopyEntity gives the copy its own list.")

T["foreach.filter"] = dict(
    note="Expected (Filter.cs): iteration is zero-allocation via struct enumerator.",
    fields="    static Filter F;\n    static ComponentPool<Pos> P;\n    static long Sum;\n",
    setup="""var w = new World();
for (int i = 0; i < 4096; i++) { var e = w.CreateEntity(new Pos { X = 1 }); w.Add(e, new Vel { X = 1 }); }
F = w.Filter().Inc<Pos>().Inc<Vel>().End();
P = w.Pool<Pos>();""",
    first="float sum = 0;\nforeach (int e in F) sum += P.Get(e).X;\nSum += (long)sum;",
    steady="float sum = 0;\nforeach (int e in F) sum += P.Get(e).X;\nSum += (long)sum;",
    warmup=40, ops=20)

T["foreach.nested5"] = dict(
    note="Expected (Filter.cs): zero-allocation iteration, nested loops included.",
    fields="    static Filter F;\n    static long Sum;\n",
    setup="var w = new World();\nfor (int i = 0; i < 4; i++) w.CreateEntity(new Pos { X = i });\nF = w.Filter().Inc<Pos>().End();",
    first="long n = 0;\nforeach (int a in F) foreach (int b in F) foreach (int c in F) foreach (int d in F) foreach (int e in F) n += a + b + c + d + e;\nSum += n;",
    steady="long n = 0;\nforeach (int a in F) foreach (int b in F) foreach (int c in F) foreach (int d in F) foreach (int e in F) n += a + b + c + d + e;\nSum += n;",
    warmup=40, ops=20)

T["foreach.alive_entities"] = dict(
    note="Expected (World.cs): enumerating AliveEntities is zero-allocation.",
    fields="    static World W;\n    static Entity[] E;\n    static long Sum;\n",
    setup=CARRIER_WORLD.format(count=2048),
    first="long n = 0;\nforeach (var e in W.AliveEntities) n += e.Index;\nSum += n;",
    steady="long n = 0;\nforeach (var e in W.AliveEntities) n += e.Index;\nSum += n;",
    warmup=40, ops=20)

T["create.first_entity_of_process"] = dict(
    note="The first CreateEntity of a fresh process, nothing created before. Under KENSEI_DEBUG it also initialises the library's debug statics.",
    fields="    static World W;\n",
    setup="W = new World();\nW.Pool<Pos>();   // nothing else ran: the window holds the first CreateEntity of the process",
    first="W.DestroyEntity(W.CreateEntity(new Pos { X = 1 }));", steady="W.DestroyEntity(W.CreateEntity(new Pos { X = 1 }));",
    warmup=4000, ops=2000)

_G = 8
T["growth.first_n_pools"] = dict(
    note=("First growth of %d pools without Reserve: each Add grows one pool's sparse array from 256 to 512 ints "
          "(24 + 512 * 4 = 2072 bytes). Nothing was reserved, so nothing else is expected. The stand runs 100 pools; "
          "compare bytes per pool." % _G),
    types="".join("public struct G%d : IComponent { public int V; }\n" % i for i in range(_G)),
    fields="    static World W;\n    static Entity Low, High;\n    const int N = %d;\n" % _G,
    setup="""W = new World();   // defaults: 256 sparse slots per pool
var e = new Entity[300];
for (int i = 0; i < e.Length; i++) e[i] = W.CreateEntity(new Carrier { V = i });
Low = e[0];
High = e[299];
for (int t = 0; t < N; t++) CreatePool(t);
for (int t = 0; t < N; t++) { AddAt(Low, t); RemoveAt(Low, t); }   // code paths once, no growth""",
    first="for (int t = 0; t < N; t++) AddAt(High, t);",
    methods=("    static void CreatePool(int t) { switch (t) { %s } }\n" % " ".join("case %d: W.Pool<G%d>(); break;" % (i, i) for i in range(_G))
             + "    static void AddAt(Entity e, int t) { switch (t) { %s } }\n" % " ".join("case %d: W.Add(e, new G%d()); break;" % (i, i) for i in range(_G))
             + "    static void RemoveAt(Entity e, int t) { switch (t) { %s } }\n" % " ".join("case %d: W.Remove<G%d>(e); break;" % (i, i) for i in range(_G))),
    extra_print=[("per_pool", "first / N"), ("arrays_per_pool", "24 + 512 * 4")],
    per_unit=_G)

_F = 8
# Type names as long as the stand's generated ones (G0128...), so a name-sized allocation compares per filter;
# index _F is the extra type of the throwaway world's filter.
_FN = ["G%04d" % (128 + i) for i in range(_F + 1)]
T["growth.first_n_filters"] = dict(
    note=("First growth of %d filters without Reserve: each filter is empty at load; adding its type to 65 entities creates "
          "its first sparse page (1024 ints: 24 + 4096 bytes) and grows its dense array once (65 -> 130 ints: 24 + 520 bytes). "
          "Nothing was reserved, so nothing else is expected: 4664 bytes per filter. The stand runs 100 filters; compare "
          "bytes per filter." % _F),
    types="".join("public struct %s : IComponent { public int V; }\n" % _FN[i] for i in range(_F + 1)),
    fields="    static World W;\n    static Entity[] E;\n    const int N = %d;\n    const int Matching = 65;\n" % _F,
    setup="""// Every code path once on a throwaway world (no JIT inside the window, as on IL2CPP).
var scratch = new World();
var se = new Entity[Matching];
for (int i = 0; i < Matching; i++) se[i] = scratch.CreateEntity(new Carrier { V = i });
for (int t = 0; t <= N; t++) CreatePool(scratch, t);
IncFilter(scratch, N);   // a filter over one more type, not measured: its page and dense growth run here
for (int t = 0; t <= N; t++) for (int i = 0; i < Matching; i++) AddAt(scratch, se[i], t);
for (int t = 0; t <= N; t++) for (int i = 0; i < Matching; i++) RemoveAt(scratch, se[i], t);
scratch.Destroy();

W = new World();
E = new Entity[Matching];
for (int i = 0; i < Matching; i++) E[i] = W.CreateEntity(new Carrier { V = i });
for (int t = 0; t < N; t++) CreatePool(W, t);
for (int t = 0; t < N; t++) for (int i = 0; i < Matching; i++) AddAt(W, E[i], t);   // the pools grow now, not in the window
for (int t = 0; t < N; t++) for (int i = 0; i < Matching; i++) RemoveAt(W, E[i], t);
for (int t = 0; t < N; t++) IncFilter(W, t);   // empty filters: no sparse page yet, 64 + 1 dense slots""",
    first="for (int t = 0; t < N; t++) for (int i = 0; i < Matching; i++) AddAt(W, E[i], t);",
    methods=("    static void CreatePool(World w, int t) { switch (t) { %s } }\n" % " ".join("case %d: w.Pool<%s>(); break;" % (i, _FN[i]) for i in range(_F + 1))
             + "    static void AddAt(World w, Entity e, int t) { switch (t) { %s } }\n" % " ".join("case %d: w.Add(e, new %s()); break;" % (i, _FN[i]) for i in range(_F + 1))
             + "    static void RemoveAt(World w, Entity e, int t) { switch (t) { %s } }\n" % " ".join("case %d: w.Remove<%s>(e); break;" % (i, _FN[i]) for i in range(_F + 1))
             + "    static Filter IncFilter(World w, int t) { switch (t) { %s } return null; }\n" % " ".join("case %d: return w.Filter().Inc<%s>().End();" % (i, _FN[i]) for i in range(_F + 1))),
    extra_print=[("per_filter", "first / N"), ("arrays_per_filter", "(24 + 1024 * 4) + (24 + 130 * 4)")],
    per_unit=_F)

T["filter.dense_capacity"] = dict(
    note=("A filter's dense capacity after Filter.Reserve and World.Reserve against the request; the pool of its type, asked "
          "the same, is sized exactly. Prints every case and the largest overshoot over the reserve cases (4 bytes per slot)."),
    types="public struct D : IComponent { public int V; }\n",
    fields="    static long Worst = -1;\n    static string WorstCase = \"\";\n",
    setup="""foreach (int n in new[] { 65, 100, 1000 }) {
    var w = new World(); var f = w.Filter().Inc<D>().End(); var p = w.Pool<D>();
    f.Reserve(n); p.Reserve(n);
    Case("filter_reserve", n, f, p);
}
foreach (int n in new[] { 65, 100, 1000, 5000 }) {
    var w = new World(); var f = w.Filter().Inc<D>().End(); var p = w.Pool<D>();
    w.Reserve(n, n);
    Case("world_reserve_existing", n, f, p);
}
{
    var w = new World(); var f = w.Filter().Inc<D>().End(); var p = w.Pool<D>();
    w.Reserve(5000, 5000); f.Reserve(5001); p.Reserve(5001);
    Case("filter_reserve_after_world", 5001, f, p);
}""",
    methods="""    static void Case(string kind, int requested, Filter f, ComponentPoolBase p) {
        Console.WriteLine("CASE " + kind + "(" + requested + "): filter " + f.DenseCapacity + " slots, pool " + p.DenseCapacity);
        long extra = 4L * (f.DenseCapacity - requested);
        if (extra > Worst) { Worst = extra; WorstCase = kind + "(" + requested + ")"; }
    }
""",
    custom_print='"\\"worst_extra_bytes\\":" + Worst + ",\\"worst_case\\":\\"" + WorstCase + "\\""')



def reserve_cd(first_of_process):
    return dict(
        note=("Expected (World.Reserve): after Reserve(n, n), creating and destroying up to n entities with the world's "
              "components, entering its filters, allocates nothing." + (" Fresh process: no entity was created before the window."
                                                                        if first_of_process else "")),
        types="public struct Frozen : IComponent { }\n",
        fields="    static World W;\n    static readonly Entity[] Buf = new Entity[5000];\n",
        setup="""const int n = 5000;
W = new World();
W.Filter().Inc<Pos>().Inc<Vel>().End();
W.Pool<Pos>();
W.Reserve(n, n);
W.Pool<Vel>();
W.Pool<Frozen>();
W.Filter().Inc<Pos>().Exc<Frozen>().End();
""" + ("// a fresh process: the window holds the first CreateEntity" if first_of_process else
       "var x = W.CreateEntity(new Pos()); W.Add(x, new Vel()); W.DestroyEntity(x);   // code paths once"),
        first="""for (int i = 0; i < Buf.Length; i++) { Buf[i] = W.CreateEntity(new Pos { X = i }); W.Add(Buf[i], new Vel { X = 1 }); }
for (int i = 0; i < Buf.Length; i++) W.DestroyEntity(Buf[i]);""",
        steady="""for (int i = 0; i < Buf.Length; i++) { Buf[i] = W.CreateEntity(new Pos { X = i }); W.Add(Buf[i], new Vel { X = 1 }); }
for (int i = 0; i < Buf.Length; i++) W.DestroyEntity(Buf[i]);""",
        warmup=4, ops=2)


T["reserve.create_destroy_n"] = reserve_cd(False)
T["reserve.first_entities_of_process"] = reserve_cd(True)


def reserve_late(at_load):
    comp = "LateLoad" if at_load else "Late"
    return dict(
        note=(("After Reserve(n, n) at load, a pool created at load with world.Pool<T>(): the first Add in a frame is expected to allocate nothing."
               if at_load else
               "After Reserve(n, n) at load, a filter over Late is created at load (a filter does not create pools), and the first Add "
               "of Late happens in a frame: the pool is created there, with arrays of the reserved size.")),
        types="public struct Late : IComponent { public int V; public float W; }\npublic struct LateLoad : IComponent { public int V; public float W; }\n",
        fields="    static World W;\n    static Entity Target;\n",
        setup="""const int n = 5000;
// The code paths once on a throwaway world (no JIT inside the window, as on IL2CPP).
var scratch = new World();
var se = scratch.CreateEntity(new Carrier());
scratch.Filter().Inc<Late>().End();
scratch.Filter().Inc<LateLoad>().End();
scratch.Add(se, new Late { V = 1 });
scratch.Add(se, new LateLoad { V = 1 });
scratch.Destroy();

W = new World();
W.Pool<Carrier>();
W.Reserve(n, n);   // load time
var e = new Entity[n / 2];
for (int i = 0; i < e.Length; i++) e[i] = W.CreateEntity(new Carrier { V = i });
Target = e[e.Length - 1];
W.Filter().Inc<%s>().End();   // a system's Init
%s""" % (comp, "W.Pool<LateLoad>();   // created at load, after Reserve" if at_load else "// Pool<Late> does not exist yet"),
        first="W.Add(Target, new %s { V = 1 });" % comp,
        extra_print=[("pool_sparse_capacity", "W.Pool<%s>().SparseCapacity" % comp), ("pool_dense_capacity", "W.Pool<%s>().DenseCapacity" % comp)])


T["reserve.pool_first_add_in_frame"] = reserve_late(False)
T["reserve.pool_created_at_load"] = reserve_late(True)

T["reserve.entities_hot_types"] = dict(
    note=("Expected (2.3.0, world-config.md): after ReserveEntities(n) and Pool<T>().Reserve(n) for the hot types, a frame that "
          "creates and destroys up to n entities with them and enters the filters made of them allocates nothing. The filters "
          "are created after the reserve, as systems create theirs in Init after EcsBootstrap's Configure."),
    types="public struct Frozen : IComponent { }\n",
    fields="    static World W;\n    static readonly Entity[] Buf = new Entity[5000];\n",
    setup="""const int n = 5000;
W = new World();
W.ReserveEntities(n);            // Configure
W.Pool<Pos>().Reserve(n);
W.Pool<Vel>().Reserve(n);
W.Pool<Frozen>();
W.Filter().Inc<Pos>().Inc<Vel>().End();   // Init: the systems' filters, reserved by their types
W.Filter().Inc<Pos>().Exc<Frozen>().End();
W.Filter().Inc<Vel>().End();
var x = W.CreateEntity(new Pos()); W.Add(x, new Vel()); W.DestroyEntity(x);   // code paths once""",
    first="""for (int i = 0; i < Buf.Length; i++) { Buf[i] = W.CreateEntity(new Pos { X = i }); W.Add(Buf[i], new Vel { X = 1 }); }
for (int i = 0; i < Buf.Length; i++) W.DestroyEntity(Buf[i]);""",
    steady="""for (int i = 0; i < Buf.Length; i++) { Buf[i] = W.CreateEntity(new Pos { X = i }); W.Add(Buf[i], new Vel { X = 1 }); }
for (int i = 0; i < Buf.Length; i++) W.DestroyEntity(Buf[i]);""",
    warmup=4, ops=2,
    extra_print=[("growths_after_reserve", "W.GrowthsAfterReserve")])

T["reserve.unreserved_type_grows"] = dict(
    note=("2.3.0 (world-config.md): after ReserveEntities(n) and the hot pools, a type that is not reserved grows on demand; "
          "under KENSEI_DEBUG that growth is not counted in GrowthsAfterReserve (always 0 in release)."),
    types="public struct Cold : IComponent { public int V; }\n",
    fields="    static World W;\n    static readonly Entity[] E = new Entity[5000];\n",
    setup="""const int n = 5000;
var scratch = new World();   // the code paths once (no JIT inside the window, as on IL2CPP)
scratch.Filter().Inc<Cold>().End();
var se = scratch.CreateEntity(new Pos()); scratch.Add(se, new Cold());
scratch.Destroy();

W = new World();
W.ReserveEntities(n);
W.Pool<Pos>().Reserve(n);
W.Pool<Cold>();                          // not reserved
W.Filter().Inc<Pos>().End();
W.Filter().Inc<Cold>().End();            // not reserved: Cold is not
for (int i = 0; i < n; i++) E[i] = W.CreateEntity(new Pos { X = i });""",
    first="for (int i = 0; i < E.Length; i++) W.Add(E[i], new Cold { V = i });",
    extra_print=[("growths_after_reserve", "W.GrowthsAfterReserve")])

T["parallel.run"] = dict(
    note="Expected (ParallelRunner.cs): no allocations per Run after the first call for a job type. Counted over every thread.",
    types="""public struct AddOneJob : IRangeJob {
    public float[] Data;
    public void Execute(int start, int end) { var d = Data; for (int i = start; i < end; i++) d[i] += 1f; }
}
""",
    fields="    static ParallelRunner Runner;\n    static AddOneJob Job;\n",
    setup="Runner = new ParallelRunner(3);\nJob = new AddOneJob { Data = new float[65536] };",
    first="Runner.Run(Job, 65536, 1024);", steady="Runner.Run(Job, 65536, 1024);",
    warmup=200, ops=100, threads="all", after="Runner.Dispose();")

_M = 8
T["mem.reserve_entities"] = dict(
    note=("Memory of Reserve(entities) against organic growth with %d component types and 50 000 entities, each type on one "
          "entity. Reserve gives every pool a sparse array of `entities` ints (4 bytes x 50 000 = 200 000 bytes per pool), "
          "singletons included; organic growth sizes a pool's sparse array to the highest entity index holding the type. "
          "The stand runs 100/300/1000 types; compare the sparse bytes per pool." % _M),
    types="".join("public struct M%d : IComponent { public int V; }\n" % i for i in range(_M)),
    fields="    const int N = %d;\n    const int Entities = 50000;\n" % _M,
    methods=("    static void CreatePool(World w, int t) { switch (t) { %s } }\n" % " ".join("case %d: w.Pool<M%d>(); break;" % (i, i) for i in range(_M))
             + "    static void AddAt(World w, Entity e, int t) { switch (t) { %s } }\n" % " ".join("case %d: w.Add(e, new M%d()); break;" % (i, i) for i in range(_M))
             + """    static long Build(bool reserve, out long sparseBytes) {
        long before = GC.GetTotalAllocatedBytes(true);
        var w = new World();
        if (reserve) w.Reserve(Entities);
        for (int t = 0; t < N; t++) CreatePool(w, t);
        var es = new Entity[Entities];
        for (int i = 0; i < Entities; i++) es[i] = w.CreateEntity(new Carrier { V = i });
        for (int t = 0; t < N; t++) AddAt(w, es[t % 16], t);   // rare types on the first 16 entities
        long bytes = GC.GetTotalAllocatedBytes(true) - before;
        sparseBytes = 0;
        foreach (var p in w.ActivePools) sparseBytes += 4L * p.SparseCapacity;
        GC.KeepAlive(es);
        return bytes;
    }
"""),
    setup="""Build(true, out _); Build(false, out _);   // every code path once
long organic = Build(false, out long organicSparse);
long reserved = Build(true, out long reservedSparse);""",
    custom_print=('"\\"organic_bytes\\":" + organic + ",\\"reserve_bytes\\":" + reserved + ",\\"sparse_per_pool_reserve\\":" + '
                  '(reservedSparse / (N + 1)) + ",\\"sparse_per_pool_organic\\":" + (organicSparse / (N + 1))'))

# Not reproducible outside the stand: the stand's own calibration of its reflection-bound delegate.
NOT_APPLICABLE = {"invoke.delegate_overhead": "the stand's own calibration (the delegate it calls Invoke through), not package API",
                  "invoke.ratio_paired": "timing only, measures no bytes"}


# ---------------------------------------------------------------------------
# rendering
# ---------------------------------------------------------------------------

HEADER = """// Minimal repro: {id} - {title}
// Tree: {flavor} ({config}). Measured by the stand: {measured}
// Parameters: {params}. Deterministic, seed {seed}.
// {note}
// Build and run from the repository root: dotnet run -c Release --project {csproj} (it compiles the
//   KenseiECS sources of {src}; -p:KenseiSrc=<KenseiECS folder> takes another tree, -p:KenseiDebug=true
//   builds with KENSEI_DEBUG).
//   LangVersion 9 like Unity: a method group converts to a new delegate on every call, a static lambda is cached.
// Prints one line: REPRO {{json}}. Windows hold only the measured code (no Assert, boxing or formatting inside).
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using KenseiECS;

public struct Pos : IComponent {{ public float X, Y; }}
public struct Vel : IComponent {{ public float X, Y; }}
public struct Carrier : IComponent {{ public int V; }}
public struct Tag : IComponent {{ }}
"""

CSPROJ = """<Project Sdk="Microsoft.NET.Sdk">
    <!-- Repro {id} of Stand/alloc. Compiles the KenseiECS sources of the tree under test, like the stand.
         Inside Stand/ the stand's Directory.Build.props applies too (output under Stand/out/bin). -->
    <PropertyGroup>
        <OutputType>Exe</OutputType>
        <TargetFramework>net8.0</TargetFramework>
        <LangVersion>9.0</LangVersion>
        <Nullable>disable</Nullable>
        <Optimize>true</Optimize>
        <InvariantGlobalization>true</InvariantGlobalization>
        <ServerGarbageCollection>false</ServerGarbageCollection>
        <ConcurrentGarbageCollection>false</ConcurrentGarbageCollection>
        <NoWarn>$(NoWarn);CS0618;CS1591;CS1573;CS1574;CS0219;CS0414</NoWarn>
        <KenseiSrc Condition="'$(KenseiSrc)' == ''">$(MSBuildThisFileDirectory){src}</KenseiSrc>
        <DefineConstants Condition="'$(KenseiDebug)' == 'true'">$(DefineConstants);KENSEI_DEBUG</DefineConstants>
    </PropertyGroup>
    <ItemGroup>
        <Compile Include="$(KenseiSrc)/Core/**/*.cs" LinkBase="Core" />
        <Compile Include="$(KenseiSrc)/Systems/**/*.cs" LinkBase="Systems" />
        <Compile Include="$(KenseiSrc)/DevTools/**/*.cs" LinkBase="DevTools" Condition="'$(KenseiDebug)' == 'true'" />
    </ItemGroup>
</Project>
"""


def _indent(text, n):
    pad = " " * n
    return "\n".join((pad + line) if line.strip() else "" for line in text.strip("\n").splitlines())


def available(scenario_id):
    return scenario_id in T


def _counter(t):
    return "GC.GetTotalAllocatedBytes(true)" if t.get("threads") == "all" else "GC.GetAllocatedBytesForCurrentThread()"


def render_program(scenario_id, title, flavor, config, measured, params, seed, src, csproj_name):
    t = T[scenario_id]
    mode = (params or {}).get("mode", "quick")
    span_warmup = 64 * (4 if mode == "full" else 1)
    counter = _counter(t)
    out = [HEADER.format(id=scenario_id, title=title, flavor=flavor, config=config, measured=measured,
                         params=json.dumps(params, ensure_ascii=False), seed=seed,
                         note=t.get("note", ""), csproj=csproj_name, src=src)]
    out.append(t.get("types", ""))
    out.append("public static class Repro {")
    out.append("    const MethodImplOptions Window = MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization;")
    out.append(t.get("fields", "").rstrip("\n"))
    out.append("")
    out.append("    public static void Main() {")
    out.append("        BclWarmup();")
    out.append("        PrimeStatics();")
    out.append(_indent(t.get("setup", ""), 8))
    prints = ['"\\"id\\":\\"%s\\""' % scenario_id]
    if t.get("first"):
        out.append("        long first = First();   // one-time bytes: the first pass")
        prints.append('",\\"first\\":" + first')
    if t.get("steady") and not t.get("spans"):
        out.append("        for (int i = 0; i < %d; i++) Op();   // warm-up, outside the window" % t["warmup"])
        out.append("        long steady = Steady(%d);" % t["ops"])
        prints.append('",\\"steady_bytes\\":" + steady + ",\\"ops\\":%d"' % t["ops"])
    if t.get("spans"):
        warm = str(t["warmup"]).replace("{span_warmup}", str(span_warmup))
        out.append("        for (int i = 0; i < %s; i++) Op();   // warm-up, outside the windows" % warm)
        out.append("        int position = %d + %s;" % (t.get("done", 0), warm))
        out.append("        var spans = new long[3];")
        out.append("        var runs = new int[3];")
        out.append("        for (int s = 0; s < 3; s++) {   // span s covers runs [P, 2.25 P), P counted from the first run")
        out.append("            int length = position + position / 4;")
        out.append("            spans[s] = Steady(length);")
        out.append("            runs[s] = length;")
        out.append("            position += length;")
        out.append("        }")
        prints.append('",\\"span_bytes\\":[" + spans[0] + "," + spans[1] + "," + spans[2] + "],\\"span_runs\\":[" + runs[0] + "," + runs[1] + "," + runs[2] + "]"')
    if t.get("after"):
        out.append(_indent(t["after"], 8))
    for key, expr in t.get("extra_print", []):
        prints.append('",\\"%s\\":" + (%s)' % (key, expr))
    if t.get("custom_print"):
        prints.append('"," + ' + t["custom_print"])
    out.append('        Console.WriteLine("REPRO {" + %s + "}");' % " + ".join(prints))
    out.append("    }")
    if t.get("first"):
        out.append("")
        out.append("    [MethodImpl(Window)]")
        out.append("    static long First() {")
        out.append("        long before = %s;" % counter)
        out.append(_indent(t["first"], 8))
        out.append("        return %s - before;" % counter)
        out.append("    }")
    if t.get("steady"):
        out.append("")
        out.append("    [MethodImpl(Window)]")
        out.append("    static void Op() {")
        out.append(_indent(t["steady"], 8))
        out.append("    }")
        out.append("")
        out.append("    [MethodImpl(Window)]")
        out.append("    static long Steady(int ops) {")
        out.append("        long before = %s;" % counter)
        out.append("        for (int i = 0; i < ops; i++) Op();")
        out.append("        return %s - before;" % counter)
        out.append("    }")
    if t.get("methods"):
        out.append("")
        out.append(t["methods"].rstrip("\n"))
    out.append("")
    out.append("    // The BCL's own first-call work (reflection, number formatting) outside the windows, as in the stand's")
    out.append("    // process: a window then counts the library and the code under test, not the runtime starting up.")
    out.append("    static object BclSink;")
    out.append("    static void BclWarmup() {")
    out.append("        BclSink = typeof(Repro).Name + typeof(Repro).GetMethods().Length + new System.Text.StringBuilder().Append(1.5).ToString();")
    out.append("    }")
    out.append("")
    out.append("    // The runtime keeps static fields in pinned object[] buckets (2 040 slots, then 4 080, ...) and allocates the")
    out.append("    // next one (16 344 B) in whatever code initializes a class when the bucket is full. As in the stand's process")
    out.append("    // (StaticsPrimer), classes of the repro's own are initialized until a bucket appears, so no window pays for it.")
    out.append("    static class Slot<T> { internal static object Value = null; }")
    out.append("    static void PrimeStatics() {")
    out.append("        var basics = new List<Type>();")
    out.append("        foreach (var t in typeof(object).Assembly.GetExportedTypes()) {")
    out.append("            if (t.IsClass && t.IsPublic && !t.IsAbstract && !t.IsGenericTypeDefinition && !t.ContainsGenericParameters) basics.Add(t);")
    out.append("            if (basics.Count == 80) break;")
    out.append("        }")
    out.append("        foreach (var a in basics) foreach (var b in basics) {")
    out.append("            var slot = typeof(Slot<>).MakeGenericType(typeof(Tuple<,>).MakeGenericType(a, b));")
    out.append("            long before = GC.GetAllocatedBytesForCurrentThread();")
    out.append("            RuntimeHelpers.RunClassConstructor(slot.TypeHandle);")
    out.append("            if (GC.GetAllocatedBytesForCurrentThread() - before >= 16000) return;")
    out.append("        }")
    out.append("    }")
    out.append("}")
    return "\n".join(x for x in out if x is not None) + "\n"


def safe_name(item_id):
    return re.sub(r"[^A-Za-z0-9]+", "_", item_id).strip("_")


def write(item, out_dir, src, flavor, config):
    """Renders the repro folder of one finding or question. Returns (folder, Program.cs) or (None, reason)."""
    sid = item["scenario"]
    if sid in NOT_APPLICABLE or not available(sid):
        return None, NOT_APPLICABLE.get(sid, "no repro template")
    name = safe_name(item["id"])
    folder = os.path.join(out_dir, name)
    os.makedirs(folder, exist_ok=True)
    csproj = name + ".csproj"
    measured = json.dumps(item.get("runtimes"), ensure_ascii=False)
    text = render_program(sid, item.get("title", sid), flavor, config, measured, item.get("params", {}), item.get("seed", 0),
                          S.rel(src), S.rel(os.path.join(folder, csproj)))
    with open(os.path.join(folder, "Program.cs"), "w", encoding="utf-8") as f:
        f.write(text)
    with open(os.path.join(folder, csproj), "w", encoding="utf-8") as f:
        f.write(CSPROJ.format(id=item["id"], src=os.path.relpath(os.path.abspath(src), folder).replace(os.sep, "/")))
    return folder, os.path.join(folder, "Program.cs")


# ---------------------------------------------------------------------------
# check: build and run rendered repros, compare with the stand
# ---------------------------------------------------------------------------

def _stand_value(item, runtime):
    """The stand's number the repro should reproduce, and its key in the REPRO line."""
    d = (item.get("runtimes") or {}).get(runtime) or {}
    metric = item.get("metric")
    if metric == "long-run":
        return "span_bytes", d.get("span_bytes", d.get("target_span_bytes"))
    if metric == "bytes/op":
        return "steady_per_op", d.get("per_op")
    if metric == "memory":
        return "sparse_per_pool_reserve", d.get("sparse_bytes_per_pool")
    if metric == "capacity":
        return "worst_extra_bytes", d.get("worst_extra_bytes")
    if metric == "counter":
        return "growths_after_reserve", d.get("growths_after_reserve")
    return "first", d.get("first", d.get("target_first"))


def _repro_value(key, row, t):
    if key == "steady_per_op":
        if row.get("steady_bytes") is None:
            return None
        return row["steady_bytes"] / row["ops"]
    if key == "first" and t.get("per_unit"):
        return row.get("per_pool", row.get("per_filter"))
    return row.get(key)


def _fmt(v):
    if isinstance(v, float) and v.is_integer():
        return str(int(v))
    return str(v)


def _run_once(dll, rt, key, stand, t, extra_env):
    host, env = S.runtime_env(rt, extra_env)
    code, out, err = S.run([host, dll], env, timeout=600, check=False)
    rows = [json.loads(line[6:]) for line in out.splitlines() if line.startswith("REPRO ")]
    if code != 0 or not rows:
        return {"status": "run failed", "output": (out + err)[-1500:]}
    row = rows[0]
    got = _repro_value(key, row, t)
    status = "n/a"
    if stand is not None and got is not None:
        if isinstance(stand, list):
            same = len(stand) == len(got) and all((a > 0) == (b > 0) for a, b in zip(stand, got))
            status = "reproduces" if same else "differs"
        else:
            status = "reproduces" if abs(float(got) - float(stand)) < 0.5 else "differs"
    return {"status": status, "stand": stand, "repro": got, "key": key, "output": row, "env": extra_env}


def _build(csproj, name, src, flavor, debug):
    tag = "dbg" if debug else "rel"
    cmd = [S.sdk_host(), "build", csproj, "-c", "Release",
           "-p:KenseiSrc=" + os.path.abspath(src), "-p:StandFlavor=" + flavor + "-repro",
           "-p:KenseiDebug=" + ("true" if debug else "false"),
           "--disable-build-servers", "-nologo", "-v:q", "-clp:ErrorsOnly"]
    code, out, err = S.run(cmd, S.sdk_env(), timeout=900, check=False)
    if code != 0:
        return None, (out + err)[-2000:]
    return os.path.join(S.OUT, "bin", "%s-repro-%s" % (flavor, tag), name, "Release", "net8.0", name + ".dll"), None


def _units(item):
    """How many pools or filters the stand's growth scenario grew (the repro grows fewer)."""
    p = item.get("params") or {}
    return p.get("pools_grown") or p.get("filters") or p.get("pools") or 1


def _value(res):
    v = res.get("repro")
    if isinstance(v, list):
        return None
    return v


def check(items, folders, src, flavor, debug, runtimes, baseline=None):
    """Build each repro against `src` (release or KENSEI_DEBUG like the run) and run it on each runtime.
    A regression repro is built against the baseline tree too (`baseline` = (src, flavor)) and its claim
    is the difference target - baseline, which cancels whatever the repro process itself allocates.
    Fills item["repro_check"] = {runtime: {...}} and returns the number of runs that reproduced."""
    reproduced = 0
    for item in items:
        folder = folders.get(item["id"])
        if not folder:
            continue
        t = T[item["scenario"]]
        name = safe_name(item["id"])
        csproj = os.path.join(folder, name + ".csproj")
        dll, error = _build(csproj, name, src, flavor, debug)
        if not dll:
            item["repro_check"] = {"build": "failed", "output": error}
            continue
        delta_mode = item.get("class") == "regression" and baseline is not None and item.get("metric") in ("one-time bytes", "bytes/op")
        bdll = None
        if delta_mode:
            bdll, berror = _build(csproj, name, baseline[0], baseline[1], debug)
            if not bdll:
                delta_mode = False
        result = {}
        for rt in runtimes:
            key, stand = _stand_value(item, rt)
            if t.get("per_unit") and key == "first" and stand is not None:
                stand = stand / _units(item)
            if delta_mode:
                d = (item.get("runtimes") or {}).get(rt) or {}
                stand_delta = d.get("delta")
                if t.get("per_unit") and key == "first" and stand_delta is not None:
                    stand_delta = stand_delta / _units(item)
                rt_res = _run_once(dll, rt, key, None, t, None)
                rb_res = _run_once(bdll, rt, key, None, t, None)
                vt, vb = _value(rt_res), _value(rb_res)
                if vt is None or vb is None or stand_delta is None:
                    res = {"status": "n/a", "target": vt, "baseline": vb}
                else:
                    got = vt - vb
                    ok = abs(got - stand_delta) < 0.5
                    res = {"status": "reproduces" if ok else "differs", "repro": got, "stand": stand_delta,
                           "key": "delta " + key, "target_value": vt, "baseline_value": vb,
                           "note": "difference target - baseline (%s): %s - %s" % (baseline[1], _fmt(vt), _fmt(vb))}
            else:
                res = _run_once(dll, rt, key, stand, t, None)
                if res["status"] == "differs":
                    # The repro calls the API directly, the stand partly through delegates: on .NET 10 the JIT may
                    # inline a library method (methods with try/finally included) and put an object that no longer
                    # escapes on the stack. IL2CPP and Mono never do that; the stand's own windows avoid it.
                    again = _run_once(dll, rt, key, stand, t, {"DOTNET_JitObjectStackAllocation": "0"})
                    if again["status"] == "reproduces":
                        res = dict(again, status="reproduces with DOTNET_JitObjectStackAllocation=0",
                                   default_runtime=res.get("repro"),
                                   note="without the knob the %s JIT put some objects on the stack: %s instead of %s"
                                        % (rt, _fmt(res.get("repro")), _fmt(stand)))
                if key == "first" and t.get("first_bcl") and res["status"] == "differs":
                    res["status"] = "n/a: the first call initialises the BCL ArrayPool, which depends on the process state"
            result[rt] = res
            if res["status"].startswith("reproduces"):
                reproduced += 1
        item["repro_check"] = result
    return reproduced
