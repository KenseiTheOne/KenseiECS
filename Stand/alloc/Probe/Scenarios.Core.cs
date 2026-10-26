using System;
using System.Collections.Generic;
using KenseiECS;

namespace KenseiStand.Alloc.Probe {
    public static partial class Registry {
        public static List<Scenario> All() {
            var list = new List<Scenario> {
                new CreateDestroy(),
                new AddRemove<Pos>("plain", default),
                new AddRemove<Tag>("tag", default),
                new AddRemove<Big>("big", new Big { A = 1, H = 8 }),
                new AddRemove<Managed>("managed", new Managed { Ref = SharedRef, V = 1 }),
                new AddRemove<Resettable>("autoreset", new Resettable { Ref = SharedRef, V = 1 }),
                new AddRemove<Copyable>("autocopy", new Copyable { V = 1 }),
                new AddRemove<Tracked>("tracked", new Tracked { V = 1 }, track: true),
                new AddRemove<Listened>("listener", new Listened { V = 1 }, listener: true),
                new AddRemove<GroupA>("group", new GroupA { V = 1 }, group: true),
                new AddRemove<Filtered>("filtered", new Filtered { V = 1 }, filters: true),
                new AddRemoveEventBuffer(),
                new AddRemoveListeners(),
                new EventsFirstFrame(prewarm: true),
                new EventsFirstFrame(prewarm: false),
                new EventsStableFrame(),
                new EventsWriteBack(),
                new InvokeScenario(InvokeKind.StaticLambda),
                new InvokeScenario(InvokeKind.StaticLambdaOnError),
                new InvokeScenario(InvokeKind.MethodGroup),
                new InvokeScenario(InvokeKind.CapturingLambda),
                new InvokeScenario(InvokeKind.ManualLoop),
                new InvokeScenario(InvokeKind.DelegateOverhead),
                new InvokeRatio(),
                new RunnerInlineGroups(),
                new RunnerNamedGroup(),
                new RunnerCleanWhenDisabled(),
                new CommandBufferPlayback(),
                new CopyEntityScenario(CopyKind.AutoCopy),
                new CopyEntityScenario(CopyKind.EventBuffer),
                new CopyEntityScenario(CopyKind.Listeners),
                new ForeachFilter(),
                new ForeachNested(),
                new ForeachAliveEntities(),
                new ParallelRun(),
                new FirstEntityOfProcess(),
                new FirstGrowthNoReserve(),
                new FirstGrowthNoReserveFilters(),
                new FilterDenseCapacity(),
                new AccessWorldGetHas(),
                new AccessTrackedPool(),
                new AccessSingleton(),
                new AccessGroupData(),
                new AccessFilterContains(),
                new ReserveCreateDestroy(firstEntityOfProcess: false),
                new ReserveCreateDestroy(firstEntityOfProcess: true),
                new ReserveLatePool(createdAtLoad: false),
                new ReserveLatePool(createdAtLoad: true),
                new ReserveEntitiesHotTypes(),
                new ReserveUnreservedTypeGrows(),
            };
            return list;
        }

        public static readonly object SharedRef = new object();

        /// <summary> Closed generic component types the scenarios use (for --pretouch-type-names). </summary>
        public static readonly Type[] ClosedGenericComponents = {
            typeof(EventBuffer<EvAddRemove>), typeof(EventBuffer<EvPrewarm>), typeof(EventBuffer<EvNoPrewarm>),
            typeof(EventBuffer<EvCopy>), typeof(EventBuffer<EvWriteBack>), typeof(EventBuffer<EvWriteBackControl>),
            typeof(Listeners<IView>),
        };

        /// <summary> A world with <paramref name="count"/> entities that each hold a Carrier. </summary>
        public static World CarrierWorld(int count, out Entity[] entities) {
            var world = new World();
            entities = new Entity[count];
            for (int i = 0; i < count; i++) {
                entities[i] = world.CreateEntity(new Carrier { V = i });
            }
            return world;
        }
    }

    // ==================================================================
    // create / destroy
    // ==================================================================

    /// <summary> CreateEntity + DestroyEntity. First pass grows the world from its defaults to 4096 slots. </summary>
    public sealed class CreateDestroy : Scenario {
        public override string Id => "create_destroy";

        private struct Many : IAllocOp {
            public World W;
            public Entity[] Buf;
            public void Invoke() {
                var b = Buf;
                for (int i = 0; i < b.Length; i++) {
                    b[i] = W.CreateEntity(new Pos { X = i });
                }
                for (int i = 0; i < b.Length; i++) {
                    W.DestroyEntity(b[i]);
                }
            }
        }

        private struct One : IAllocOp {
            public World W;
            public void Invoke() {
                var e = W.CreateEntity(new Pos { X = 1 });
                W.DestroyEntity(e);
            }
        }

        public override void Run(Settings s, Result r) {
            const int peak = 4096;
            var world = new World();
            world.Pool<Pos>();
            var one = new One { W = world };
            // Code paths once (and, under KENSEI_DEBUG, the profiler's static state) outside the windows.
            AllocMeter.Warm(ref one, 1);
            var many = new Many { W = world, Buf = new Entity[peak] };
            r.FirstBytes = Census.Once(ref many, r, world);
            r.Set("entities", peak);
            Steady(s, r, ref one);
            Time(s, r, ref one);
        }
    }

    // ==================================================================
    // add / remove of every kind of component
    // ==================================================================

    /// <summary>
    /// Add + Remove of one component kind on entities that keep a Carrier (so they stay alive).
    /// First pass: add to all 1024 entities, then remove from all (the pool grows from its defaults).
    /// </summary>
    public sealed class AddRemove<T> : Scenario where T : struct, IComponent {
        private readonly string _kind;
        private readonly T _value;
        private readonly bool _track, _listener, _group, _filters;

        public AddRemove(string kind, T value, bool track = false, bool listener = false, bool group = false, bool filters = false) {
            _kind = kind;
            _value = value;
            _track = track;
            _listener = listener;
            _group = group;
            _filters = filters;
        }

        public override string Id => "add_remove." + _kind;

        private struct All : IAllocOp {
            public World W;
            public Entity[] E;
            public T Value;
            public void Invoke() {
                var e = E;
                for (int i = 0; i < e.Length; i++) {
                    W.Add(e[i], Value);
                }
                for (int i = 0; i < e.Length; i++) {
                    W.Remove<T>(e[i]);
                }
            }
        }

        private struct One : IAllocOp {
            public World W;
            public Entity[] E;
            public int I;
            public T Value;
            public void Invoke() {
                var e = E[I];
                if (++I == E.Length) {
                    I = 0;
                }
                W.Add(e, Value);
                W.Remove<T>(e);
            }
        }

        public override void Run(Settings s, Result r) {
            const int count = 1024;
            var world = Registry.CarrierWorld(count, out var entities);
            var pool = world.Pool<T>();
            var keep = new List<Filter>();
            if (_track) {
                pool.TrackChanges();
            }
            if (_listener) {
                pool.AddListener(new CountingListener<T>());
            }
            if (_group) {
                for (int i = 0; i < count; i++) {
                    world.Add(entities[i], new GroupB { V = i });
                }
                world.Group<GroupA, GroupB>();
            }
            if (_filters) {
                keep.Add(world.Filter().Inc<T>().End());
                keep.Add(world.Filter().Inc<T>().Inc<Carrier>().End());
                keep.Add(world.Filter().Inc<Carrier>().Exc<T>().End());
                keep.Add(world.Filter().Any<T>().Any<Tag2>().End());
            }
            var one = new One { W = world, E = entities, Value = _value };
            AllocMeter.Warm(ref one, 1);
            var all = new All { W = world, E = entities, Value = _value };
            r.FirstBytes = Census.Once(ref all, r, world);
            r.Set("entities", count);
            r.Set("filters", keep.Count);
            Steady(s, r, ref one);
            if (_kind == "plain" || _kind == "filtered" || _kind == "group") {
                Time(s, r, ref one);
            }
            GC.KeepAlive(keep);
        }
    }

    /// <summary> AddEvent + Remove of EventBuffer: rents a pooled list and returns it. </summary>
    public sealed class AddRemoveEventBuffer : Scenario {
        public override string Id => "add_remove.eventbuffer";

        private struct All : IAllocOp {
            public World W;
            public Entity[] E;
            public void Invoke() {
                var e = E;
                for (int i = 0; i < e.Length; i++) {
                    W.AddEvent(e[i], new EvAddRemove { V = i });
                }
                for (int i = 0; i < e.Length; i++) {
                    W.Remove<EventBuffer<EvAddRemove>>(e[i]);
                }
            }
        }

        private struct One : IAllocOp {
            public World W;
            public Entity[] E;
            public int I;
            public void Invoke() {
                var e = E[I];
                if (++I == E.Length) {
                    I = 0;
                }
                W.AddEvent(e, new EvAddRemove { V = 1 });
                W.Remove<EventBuffer<EvAddRemove>>(e);
            }
        }

        public override void Run(Settings s, Result r) {
            const int count = 1024;
            var world = Registry.CarrierWorld(count, out var entities);
            world.Pool<EventBuffer<EvAddRemove>>();
            var one = new One { W = world, E = entities };
            AllocMeter.Warm(ref one, 1);
            var all = new All { W = world, E = entities };
            r.FirstBytes = Census.Once(ref all, r, world, typeof(EvAddRemove));
            r.Set("entities", count);
            Steady(s, r, ref one);
        }
    }

    /// <summary>
    /// Subscribe + Remove of Listeners: documented to drop the list on remove, so every new
    /// subscription creates one (Listeners.cs: "Removing the component ... clears the list and drops it").
    /// </summary>
    public sealed class AddRemoveListeners : Scenario {
        public override string Id => "add_remove.listeners";

        private struct One : IAllocOp {
            public World W;
            public Entity[] E;
            public int I;
            public IView View;
            public void Invoke() {
                var e = E[I];
                if (++I == E.Length) {
                    I = 0;
                }
                W.Subscribe(e, View);
                W.Remove<Listeners<IView>>(e);
            }
        }

        public override void Run(Settings s, Result r) {
            const int count = 1024;
            var world = Registry.CarrierWorld(count, out var entities);
            world.Pool<Listeners<IView>>();
            var one = new One { W = world, E = entities, View = new View() };
            AllocMeter.Warm(ref one, count);
            r.FirstBytes = null;
            r.Set("entities", count);
            Steady(s, r, ref one);
        }
    }

    // ==================================================================
    // access: reading and writing components in a steady frame
    // ==================================================================
    // The calls a system makes every frame on components that exist: frame-zero (README/introduction: "zero
    // allocations at runtime once pools, filters and buffers have grown to their peak capacity") holds for them in
    // every build, KENSEI_DEBUG included, whose handle validation must not allocate either.

    internal static class AccessSetup {
        public const int Entities = 256;

        public static World World(out Entity[] entities) {
            var world = new World();
            entities = new Entity[Entities];
            for (int i = 0; i < Entities; i++) {
                entities[i] = world.CreateEntity(new Pos { X = i });
                if (i % 2 == 0) {
                    world.Add(entities[i], new Vel { X = 1 });
                }
            }
            return world;
        }
    }

    /// <summary> world.Get&lt;T&gt;(Entity) (a read-modify-write through the ref) and world.Has&lt;T&gt;(Entity), one entity per op. </summary>
    public sealed class AccessWorldGetHas : Scenario {
        public override string Id => "access.world_get_has";

        private struct Op : IAllocOp {
            public World W;
            public Entity[] E;
            public int I;
            public void Invoke() {
                var e = E[I];
                if (++I == E.Length) {
                    I = 0;
                }
                ref var p = ref W.Get<Pos>(e);
                p.X += 1;
                if (W.Has<Vel>(e)) {
                    Sink.Number++;
                }
            }
        }

        public override void Run(Settings s, Result r) {
            var world = AccessSetup.World(out var entities);
            var op = new Op { W = world, E = entities };
            AllocMeter.Warm(ref op, entities.Length);
            r.Set("entities", entities.Length);
            Steady(s, r, ref op);
        }
    }

    /// <summary> pool.Modify, pool.MarkChanged and pool.ChangedSince on a pool with TrackChanges, one entity per op. </summary>
    public sealed class AccessTrackedPool : Scenario {
        public override string Id => "access.modify_mark_changed";

        private struct Op : IAllocOp {
            public ComponentPool<AccessTracked> P;
            public Entity[] E;
            public int I;
            public int Since;
            public void Invoke() {
                int idx = E[I].Index;
                if (++I == E.Length) {
                    I = 0;
                }
                P.Modify(idx).V++;
                P.MarkChanged(idx);
                if (P.ChangedSince(idx, Since)) {
                    Sink.Number++;
                }
            }
        }

        public override void Run(Settings s, Result r) {
            var world = AccessSetup.World(out var entities);
            var pool = world.Pool<AccessTracked>();
            pool.TrackChanges();
            for (int i = 0; i < entities.Length; i++) {
                world.Add(entities[i], new AccessTracked { V = i });
            }
            var op = new Op { P = pool, E = entities, Since = world.ChangeVersion };
            AllocMeter.Warm(ref op, entities.Length);
            r.Set("entities", entities.Length);
            Steady(s, r, ref op);
        }
    }

    /// <summary> world.GetSingleton&lt;T&gt;() (and its entity) of a component exactly one entity has. </summary>
    public sealed class AccessSingleton : Scenario {
        public override string Id => "access.get_singleton";

        private struct Op : IAllocOp {
            public World W;
            public void Invoke() {
                W.GetSingleton<AccessConfig>().V++;
                Sink.Number += W.GetSingletonEntity<AccessConfig>().Index;
            }
        }

        public override void Run(Settings s, Result r) {
            var world = AccessSetup.World(out var entities);
            world.Add(entities[3], new AccessConfig { V = 1 });
            var op = new Op { W = world };
            AllocMeter.Warm(ref op, 64);
            Steady(s, r, ref op);
        }
    }

    /// <summary> A loop over an owning group's Entities, Data1 and Data2 (256 members), one loop per op. </summary>
    public sealed class AccessGroupData : Scenario {
        public override string Id => "access.group_data";

        private struct Op : IAllocOp {
            public Group<AccessGA, AccessGB> G;
            public void Invoke() {
                var entities = G.Entities;
                var a = G.Data1;
                var b = G.Data2;
                long sum = 0;
                for (int i = 0; i < G.Count; i++) {
                    a[i].V += b[i].V;
                    sum += entities[i];
                }
                Sink.Number += sum;
            }
        }

        public override void Run(Settings s, Result r) {
            var world = AccessSetup.World(out var entities);
            var group = world.Group<AccessGA, AccessGB>();
            for (int i = 0; i < entities.Length; i++) {
                world.Add(entities[i], new AccessGA { V = i });
                world.Add(entities[i], new AccessGB { V = 1 });
            }
            var op = new Op { G = group };
            AllocMeter.Warm(ref op, 8);
            r.Set("members", group.Count);
            Steady(s, r, ref op, 100);
        }
    }

    /// <summary> filter.Contains(index) for every slot of the world (matching or not), one loop per op. </summary>
    public sealed class AccessFilterContains : Scenario {
        public override string Id => "access.filter_contains";

        private struct Op : IAllocOp {
            public Filter F;
            public int Slots;
            public void Invoke() {
                int n = 0;
                for (int i = 0; i < Slots; i++) {
                    if (F.Contains(i)) {
                        n++;
                    }
                }
                Sink.Number += n;
            }
        }

        public override void Run(Settings s, Result r) {
            var world = AccessSetup.World(out var entities);
            var filter = world.Filter().Inc<Pos>().Inc<Vel>().End();
            var op = new Op { F = filter, Slots = entities.Length + 64 };
            AllocMeter.Warm(ref op, 8);
            r.Set("matches", filter.Count);
            Steady(s, r, ref op, 100);
        }
    }

    // ==================================================================
    // CommandBuffer
    // ==================================================================

    /// <summary>
    /// One frame of a CommandBuffer: 8 x (CreateEntity, Add, Set on the pending entity), Set and
    /// Add/Remove on existing entities, 8 x DestroyEntity of the pending ones, Playback.
    /// The first frame grows the buffer; CommandBuffer.cs promises nothing after it.
    /// </summary>
    public sealed class CommandBufferPlayback : Scenario {
        public override string Id => "commandbuffer.playback";

        private struct Frame : IAllocOp {
            public World W;
            public CommandBuffer Cb;
            public Entity A, B;
            public PendingEntity[] P;
            public int N;
            public void Invoke() {
                var cb = Cb;
                var p = P;
                for (int i = 0; i < p.Length; i++) {
                    p[i] = cb.CreateEntity(new Pos { X = i });
                    cb.Add(p[i], new Tag());
                    cb.Set(p[i], new Vel { X = i });
                }
                cb.Set(A, new Pos { X = ++N });
                cb.Add(B, new Tag2());
                cb.Remove<Tag2>(B);
                for (int i = 0; i < p.Length; i++) {
                    cb.DestroyEntity(p[i]);
                }
                cb.Playback(W);
            }
        }

        public override void Run(Settings s, Result r) {
            var world = new World();
            world.Pool<Pos>();
            world.Pool<Vel>();
            world.Pool<Tag>();
            world.Pool<Tag2>();
            var a = world.CreateEntity(new Pos());
            var b = world.CreateEntity(new Carrier());
            // The code paths once, on a throwaway buffer: the measured buffer starts empty.
            var warm = new Frame { W = world, Cb = new CommandBuffer(), A = a, B = b, P = new PendingEntity[8] };
            AllocMeter.Warm(ref warm, 1);
            var frame = new Frame { W = world, Cb = new CommandBuffer(), A = a, B = b, P = new PendingEntity[8] };
            r.FirstBytes = Census.Once(ref frame, r, world);
            r.Set("commands_per_frame", 8 * 4 + 3);
            Steady(s, r, ref frame, 10);
            Time(s, r, ref frame);
        }
    }

    // ==================================================================
    // CopyEntity
    // ==================================================================

    public enum CopyKind { AutoCopy, EventBuffer, Listeners }

    /// <summary> CopyEntity + DestroyEntity of the copy. </summary>
    public sealed class CopyEntityScenario : Scenario {
        private readonly CopyKind _kind;

        public CopyEntityScenario(CopyKind kind) {
            _kind = kind;
        }

        public override string Id => _kind switch {
            CopyKind.AutoCopy => "copy_entity.autocopy",
            CopyKind.EventBuffer => "copy_entity.eventbuffer",
            _ => "copy_entity.listeners",
        };

        private struct Copy : IAllocOp {
            public World W;
            public Entity Source;
            public void Invoke() {
                var c = W.CopyEntity(Source);
                W.DestroyEntity(c);
            }
        }

        public override void Run(Settings s, Result r) {
            var world = new World();
            var source = world.CreateEntity(new Pos { X = 1 });
            world.Add(source, new Tag());
            switch (_kind) {
                case CopyKind.AutoCopy:
                    world.Add(source, new Copyable { V = 7 });
                    break;
                case CopyKind.EventBuffer:
                    world.AddEvent(source, new EvCopy { V = 1 });
                    world.AddEvent(source, new EvCopy { V = 2 });
                    break;
                case CopyKind.Listeners:
                    world.Subscribe<IView>(source, new View());
                    world.Subscribe<IView>(source, new View());
                    world.Subscribe<IView>(source, new View());
                    break;
            }
            var op = new Copy { W = world, Source = source };
            r.FirstBytes = Census.Once(ref op, r, world, typeof(EvCopy));
            Steady(s, r, ref op);
        }
    }

    // ==================================================================
    // foreach
    // ==================================================================

    /// <summary> foreach over Filter Inc&lt;Pos, Vel&gt; with 4096 matches, reading Pos through the pool. </summary>
    public sealed class ForeachFilter : Scenario {
        public override string Id => "foreach.filter";

        private struct Iterate : IAllocOp {
            public Filter F;
            public ComponentPool<Pos> P;
            public void Invoke() {
                float sum = 0;
                foreach (int e in F) {
                    sum += P.Get(e).X;
                }
                Sink.Number += (long)sum;
            }
        }

        public override void Run(Settings s, Result r) {
            const int count = 4096;
            var world = new World();
            for (int i = 0; i < count; i++) {
                var e = world.CreateEntity(new Pos { X = 1 });
                world.Add(e, new Vel { X = 1 });
            }
            var filter = world.Filter().Inc<Pos>().Inc<Vel>().End();
            var op = new Iterate { F = filter, P = world.Pool<Pos>() };
            r.FirstBytes = AllocMeter.Once(ref op);
            r.Set("matches", filter.Count);
            Steady(s, r, ref op, 100);
            Time(s, r, ref op);
        }
    }

    /// <summary> Five nested foreach loops over a 4-entity filter (KENSEI_DEBUG keeps a cursor per live loop). </summary>
    public sealed class ForeachNested : Scenario {
        public override string Id => "foreach.nested5";

        private struct Nested : IAllocOp {
            public Filter F;
            public void Invoke() {
                long n = 0;
                foreach (int a in F) {
                    foreach (int b in F) {
                        foreach (int c in F) {
                            foreach (int d in F) {
                                foreach (int e in F) {
                                    n += a + b + c + d + e;
                                }
                            }
                        }
                    }
                }
                Sink.Number += n;
            }
        }

        public override void Run(Settings s, Result r) {
            var world = new World();
            for (int i = 0; i < 4; i++) {
                world.CreateEntity(new Pos { X = i });
            }
            var op = new Nested { F = world.Filter().Inc<Pos>().End() };
            r.FirstBytes = AllocMeter.Once(ref op);
            Steady(s, r, ref op, 100);
        }
    }

    /// <summary> foreach over World.AliveEntities (documented as zero-allocation). </summary>
    public sealed class ForeachAliveEntities : Scenario {
        public override string Id => "foreach.alive_entities";

        private struct Iterate : IAllocOp {
            public World W;
            public void Invoke() {
                long n = 0;
                foreach (var e in W.AliveEntities) {
                    n += e.Index;
                }
                Sink.Number += n;
            }
        }

        public override void Run(Settings s, Result r) {
            var world = Registry.CarrierWorld(2048, out _);
            var op = new Iterate { W = world };
            r.FirstBytes = AllocMeter.Once(ref op);
            Steady(s, r, ref op, 100);
        }
    }

    // ==================================================================
    // ParallelRunner
    // ==================================================================

    /// <summary>
    /// ParallelRunner.Run of a struct job over 65 536 indices in chunks of 1024, on 3 workers plus the
    /// caller. ParallelRunner.cs: "No allocations per Run after the first call for a job type". The
    /// workers allocate on their own threads, so the windows count every thread of the process.
    /// </summary>
    public sealed class ParallelRun : Scenario {
        public override string Id => "parallel.run";

        public const int Count = 65536;
        public const int Workers = 3;

        private struct RunJob : IAllocOp {
            public ParallelRunner Runner;
            public AddOneJob Job;
            public void Invoke() {
                Runner.Run(Job, Count, 1024);
            }
        }

        public override void Run(Settings s, Result r) {
            var runner = new ParallelRunner(Workers);
            try {
                var op = new RunJob { Runner = runner, Job = new AddOneJob { Data = new float[Count] } };
                r.FirstBytes = AllocMeter.BytesAllThreads(ref op, 1);
                r.Set("workers", Workers);
                r.Set("count", Count);
                SteadyAllThreads(s, r, ref op, 20);
                Time(s, r, ref op);
            } finally {
                runner.Dispose();
            }
        }
    }
}
