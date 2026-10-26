using System;
using KenseiECS;

namespace KenseiStand.Alloc.Probe {
    // ==================================================================
    // EventBuffer + OneFrame: first peak frame and stable frames
    // ==================================================================

    /// <summary>
    /// A OneFrame&lt;EventBuffer&lt;T&gt;&gt; frame at the peak: 64 entities x 8 events, then Run.
    /// Setup follows the 2.2.0 test: the buffer pool's dense side is grown beforehand (with
    /// list-less buffers, so the static list pool stays empty) and one small frame runs every code
    /// path with a single list. With prewarm, EventBuffer&lt;T&gt;.Prewarm(64, 8) follows (2.2.0+).
    /// The first peak frame is the one-time number; repeating the frame is the steady state.
    /// </summary>
    public sealed class EventsFirstFrame : Scenario {
        private readonly bool _prewarm;

        public EventsFirstFrame(bool prewarm) {
            _prewarm = prewarm;
        }

        public override string Id => _prewarm ? "events.first_frame_prewarm" : "events.first_frame_no_prewarm";

        public const int Entities = 64;
        public const int EventsPerEntity = 8;

        public struct Frame<TEv> : IAllocOp where TEv : struct {
            public World W;
            public SystemsRunner Systems;
            public Entity[] Targets;
            public int PerEntity;
            public TEv Event;
            public void Invoke() {
                var t = Targets;
                for (int i = 0; i < t.Length; i++) {
                    for (int k = 0; k < PerEntity; k++) {
                        W.AddEvent(t[i], Event);
                    }
                }
                Systems.Run();
            }
        }

        public override void Run(Settings s, Result r) {
            if (_prewarm) {
                if (Compat.Prewarm<EvPrewarm>() == null) {
                    r.Available = false;
                    r.Reason = Compat.Missing("EventBuffer<T>.Prewarm");
                    return;
                }
                Measure<EvPrewarm>(s, r, Compat.Prewarm<EvPrewarm>());
            } else {
                Measure<EvNoPrewarm>(s, r, null);
            }
        }

        private static void Measure<TEv>(Settings s, Result r, Action<int, int> prewarm) where TEv : struct {
            var world = Registry.CarrierWorld(Entities, out var targets);
            var systems = new SystemsRunner(world).OneFrame<EventBuffer<TEv>>();
            systems.Init();
            // Grow the buffer pool's dense arrays without renting lists: a default EventBuffer has none.
            for (int i = 0; i < Entities; i++) {
                world.Add(targets[i], new EventBuffer<TEv>());
            }
            systems.Run();
            // One small frame runs every code path once, with a single list.
            var small = new Frame<TEv> { W = world, Systems = systems, Targets = new[] { targets[0] }, PerEntity = 1 };
            AllocMeter.Warm(ref small, 1);
            prewarm?.Invoke(Entities, EventsPerEntity);

            var frame = new Frame<TEv> { W = world, Systems = systems, Targets = targets, PerEntity = EventsPerEntity };
            r.FirstBytes = Census.Once(ref frame, r, world, typeof(TEv));
            r.Set("entities", Entities);
            r.Set("events_per_entity", EventsPerEntity);
            r.Set("prewarm", prewarm != null);
            Steady(s, r, ref frame, 50);
        }
    }

    /// <summary> Stable frames of the same OneFrame&lt;EventBuffer&gt; setup without Prewarm (every version). </summary>
    public sealed class EventsStableFrame : Scenario {
        public override string Id => "events.stable_frame";

        public override void Run(Settings s, Result r) {
            var world = Registry.CarrierWorld(EventsFirstFrame.Entities, out var targets);
            var systems = new SystemsRunner(world).OneFrame<EventBuffer<EvAddRemove>>();
            systems.Init();
            var frame = new EventsFirstFrame.Frame<EvAddRemove> {
                W = world, Systems = systems, Targets = targets, PerEntity = EventsFirstFrame.EventsPerEntity
            };
            // The peak once outside the windows: lists are created and return to the pool.
            AllocMeter.Warm(ref frame, 2);
            r.Set("entities", EventsFirstFrame.Entities);
            r.Set("events_per_entity", EventsFirstFrame.EventsPerEntity);
            Steady(s, r, ref frame, 50);
            Time(s, r, ref frame);
        }
    }

    // ==================================================================
    // EventBuffer: the documented write-back of a modified copy
    // ==================================================================

    /// <summary>
    /// The write-back of a modified EventBuffer copy that docs/guides/command-buffer.md documents
    /// ("writing back a modified copy (var b = world.Get&lt;EventBuffer&lt;Hit&gt;&gt;(e); b.Add(h);
    /// cb.Set(e, b);) keeps its list"). Frame: an inline group with a producer (AddEvent on 8
    /// entities), the write-back system and OneFrame&lt;EventBuffer&lt;T&gt;&gt;, then Playback of the
    /// CommandBuffer at the end of the root frame. A 9th entity starts receiving events in the third
    /// frame. Measured as a long run in geometric spans: a frame that settles allocates nothing in
    /// every span, whatever its length. The control runs the same pipeline without the write-back.
    /// Evidence outside the windows: pairs of the 9 entities whose buffers hold one and the same List
    /// (EventBuffer.Values is public).
    /// </summary>
    public sealed class EventsWriteBack : Scenario {
        public override string Id => "events.writeback_frames";

        public const int Targets = 8;
        public const int Warmup = 64;

        public override void Run(Settings s, Result r) {
            // Control first: the same frames without the write-back, in its own world and event type.
            var control = new Result { Id = r.Id };
            Measure<EvWriteBackControl>(s, control, writeBack: false, out int controlShared);
            r.Set("control_span_bytes", string.Join(",", control.SpanBytes));
            r.Set("control_shared_pairs", controlShared);

            Measure<EvWriteBack>(s, r, writeBack: true, out int shared);
            r.Set("targets", Targets + 1);
            r.Set("shared_pairs", shared);
            r.Set("last_span_bytes_per_frame", (double)r.SpanBytes[2] / r.SpanRuns[2]);
        }

        private static void Measure<T>(Settings s, Result r, bool writeBack, out int sharedPairs) where T : struct {
            var world = Registry.CarrierWorld(Targets + 1, out var targets);
            var cb = new CommandBuffer();
            var producer = new EventProducer<T>(targets, Targets);
            var combat = new SystemsRunner(world).Add(producer);
            if (writeBack) {
                combat.Add(new AppendViaSet<T>(cb));
            } else {
                combat.Add(new ReadViaGet<T>());
            }
            combat.OneFrame<EventBuffer<T>>();
            var root = new SystemsRunner(world).Add(combat).Add(new PlaybackSystem(cb));
            root.Init();
            var frame = new RunOp { Root = root };
            AllocMeter.Warm(ref frame, 2);
            producer.Active = Targets + 1;
            Spans(s, r, ref frame, 2, Warmup);

            sharedPairs = 0;
            var pool = world.Pool<EventBuffer<T>>();
            for (int i = 0; i < targets.Length; i++) {
                for (int j = i + 1; j < targets.Length; j++) {
                    if (pool.Has(targets[i].Index) && pool.Has(targets[j].Index)) {
                        var a = pool.Get(targets[i].Index).Values;
                        if (a != null && ReferenceEquals(a, pool.Get(targets[j].Index).Values)) {
                            sharedPairs++;
                        }
                    }
                }
            }
        }
    }

    // ==================================================================
    // Listeners<T>.Invoke against a hand-written loop
    // ==================================================================

    public enum InvokeKind { StaticLambda, StaticLambdaOnError, MethodGroup, CapturingLambda, ManualLoop, DelegateOverhead }

    /// <summary>
    /// One dispatch to 8 listeners of one entity. Invoke (2.2.0+) is called through a delegate bound
    /// once by reflection; "delegate_overhead" measures that extra indirect call alone, so the
    /// Invoke/loop ratio can be read with and without it.
    /// </summary>
    public sealed class InvokeScenario : Scenario {
        public const int ListenerCount = 8;
        private readonly InvokeKind _kind;

        public InvokeScenario(InvokeKind kind) {
            _kind = kind;
        }

        public override string Id => _kind switch {
            InvokeKind.StaticLambda => "invoke.static_lambda",
            InvokeKind.StaticLambdaOnError => "invoke.static_lambda_onerror",
            InvokeKind.MethodGroup => "invoke.method_group",
            InvokeKind.CapturingLambda => "invoke.capturing_lambda",
            InvokeKind.ManualLoop => "invoke.manual_loop",
            _ => "invoke.delegate_overhead",
        };

        internal static void OnHit(IView view, int damage) {
            view.OnHit(damage);
        }

        internal static void Nop(ref Listeners<IView> self, int arg, Action<IView, int> call, Action<IView, int, Exception> onError) { }

        internal struct StaticLambdaOp : IAllocOp {
            public ComponentPool<Listeners<IView>> P;
            public int Index;
            public InvokeArgFn<IView, int> Fn;
            public void Invoke() {
                ref var l = ref P.Get(Index);
                Fn(ref l, 3, static (v, d) => v.OnHit(d), null);
            }
        }

        internal struct StaticLambdaOnErrorOp : IAllocOp {
            public ComponentPool<Listeners<IView>> P;
            public int Index;
            public InvokeArgFn<IView, int> Fn;
            public void Invoke() {
                ref var l = ref P.Get(Index);
                Fn(ref l, 3, static (v, d) => v.OnHit(d), static (v, d, ex) => Sink.Value = ex);
            }
        }

        internal struct MethodGroupOp : IAllocOp {
            public ComponentPool<Listeners<IView>> P;
            public int Index;
            public InvokeArgFn<IView, int> Fn;
            public void Invoke() {
                ref var l = ref P.Get(Index);
                // C# 9 (Unity's language version, LangVersion of the stand): a method group
                // converts to a new delegate on every call.
                Fn(ref l, 3, OnHit, null);
            }
        }

        internal struct CapturingLambdaOp : IAllocOp {
            public ComponentPool<Listeners<IView>> P;
            public int Index;
            public InvokeFn<IView> Fn;
            public int Damage;
            public void Invoke() {
                ref var l = ref P.Get(Index);
                int damage = Damage;
                Fn(ref l, v => v.OnHit(damage), null);
            }
        }

        internal struct ManualLoopOp : IAllocOp {
            public ComponentPool<Listeners<IView>> P;
            public int Index;
            public void Invoke() {
                var values = P.Get(Index).Values;
                for (int i = 0; i < values.Count; i++) {
                    values[i].OnHit(3);
                }
            }
        }

        internal struct OverheadOp : IAllocOp {
            public ComponentPool<Listeners<IView>> P;
            public int Index;
            public InvokeArgFn<IView, int> Fn;
            public void Invoke() {
                ref var l = ref P.Get(Index);
                Fn(ref l, 3, static (v, d) => v.OnHit(d), null);
            }
        }

        public override void Run(Settings s, Result r) {
            var world = new World();
            var entity = world.CreateEntity(new Carrier());
            for (int i = 0; i < ListenerCount; i++) {
                world.Subscribe<IView>(entity, new View());
            }
            var pool = world.Pool<Listeners<IView>>();
            int index = entity.Index;
            r.Set("listeners", ListenerCount);

            var invokeArg = Compat.InvokeArg<IView, int>();
            var invoke = Compat.Invoke<IView>();
            switch (_kind) {
                case InvokeKind.ManualLoop: {
                    var op = new ManualLoopOp { P = pool, Index = index };
                    r.FirstBytes = AllocMeter.Once(ref op);
                    Steady(s, r, ref op);
                    Time(s, r, ref op);
                    return;
                }
                case InvokeKind.DelegateOverhead: {
                    var fn = (InvokeArgFn<IView, int>)Delegate.CreateDelegate(typeof(InvokeArgFn<IView, int>),
                        typeof(InvokeScenario).GetMethod(nameof(Nop), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static));
                    var op = new OverheadOp { P = pool, Index = index, Fn = fn };
                    r.FirstBytes = AllocMeter.Once(ref op);
                    Steady(s, r, ref op);
                    Time(s, r, ref op);
                    return;
                }
                case InvokeKind.CapturingLambda: {
                    if (invoke == null) {
                        r.Available = false;
                        r.Reason = Compat.Missing("Listeners<T>.Invoke(Action<T>, Action<T, Exception>)");
                        return;
                    }
                    var op = new CapturingLambdaOp { P = pool, Index = index, Fn = invoke, Damage = 3 };
                    r.FirstBytes = AllocMeter.Once(ref op);
                    Steady(s, r, ref op);
                    Time(s, r, ref op);
                    return;
                }
            }
            if (invokeArg == null) {
                r.Available = false;
                r.Reason = Compat.Missing("Listeners<T>.Invoke<TArg>(TArg, Action<T, TArg>, Action<T, TArg, Exception>)");
                return;
            }
            switch (_kind) {
                case InvokeKind.StaticLambda: {
                    var op = new StaticLambdaOp { P = pool, Index = index, Fn = invokeArg };
                    r.FirstBytes = AllocMeter.Once(ref op);
                    Steady(s, r, ref op);
                    Time(s, r, ref op);
                    break;
                }
                case InvokeKind.StaticLambdaOnError: {
                    var op = new StaticLambdaOnErrorOp { P = pool, Index = index, Fn = invokeArg };
                    r.FirstBytes = AllocMeter.Once(ref op);
                    Steady(s, r, ref op);
                    Time(s, r, ref op);
                    break;
                }
                case InvokeKind.MethodGroup: {
                    var op = new MethodGroupOp { P = pool, Index = index, Fn = invokeArg };
                    r.FirstBytes = AllocMeter.Once(ref op);
                    Steady(s, r, ref op);
                    Time(s, r, ref op);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// ns per operation of Invoke against the hand-written loop, measured in pairs: windows of the loop,
    /// Invoke(static lambda), Invoke(method group) and the stand's empty delegate call alternate in one
    /// process, and every round gives its own ratio. Load from other processes changes slowly against a
    /// window of a few milliseconds, so it hits both sides of a round alike; the median of the rounds is
    /// the ratio. Timing pass only (nothing is measured with --no-timing).
    /// </summary>
    public sealed class InvokeRatio : Scenario {
        public override string Id => "invoke.ratio_paired";

        public override void Run(Settings s, Result r) {
            var invokeArg = Compat.InvokeArg<IView, int>();
            if (invokeArg == null) {
                r.Available = false;
                r.Reason = Compat.Missing("Listeners<T>.Invoke<TArg>(TArg, Action<T, TArg>, Action<T, TArg, Exception>)");
                return;
            }
            if (!s.Timing) {
                return;
            }
            var world = new World();
            var entity = world.CreateEntity(new Carrier());
            for (int i = 0; i < InvokeScenario.ListenerCount; i++) {
                world.Subscribe<IView>(entity, new View());
            }
            var pool = world.Pool<Listeners<IView>>();
            int index = entity.Index;
            var nop = (InvokeArgFn<IView, int>)Delegate.CreateDelegate(typeof(InvokeArgFn<IView, int>),
                typeof(InvokeScenario).GetMethod(nameof(InvokeScenario.Nop), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static));
            var loop = new InvokeScenario.ManualLoopOp { P = pool, Index = index };
            var stat = new InvokeScenario.StaticLambdaOp { P = pool, Index = index, Fn = invokeArg };
            var group = new InvokeScenario.MethodGroupOp { P = pool, Index = index, Fn = invokeArg };
            var over = new InvokeScenario.OverheadOp { P = pool, Index = index, Fn = nop };
            // Warm-up and batch sizes: each batch about TimeWindowMs.
            var tmp = new double[1];
            int nLoop = AllocMeter.Time(ref loop, s.TimeWarmMs, s.TimeWindowMs, tmp);
            int nStat = AllocMeter.Time(ref stat, s.TimeWarmMs, s.TimeWindowMs, tmp);
            int nGroup = AllocMeter.Time(ref group, s.TimeWarmMs, s.TimeWindowMs, tmp);
            int nOver = AllocMeter.Time(ref over, s.TimeWarmMs, s.TimeWindowMs, tmp);
            int rounds = s.TimeWindows * 2;
            var rs = new double[rounds];
            var rsNet = new double[rounds];
            var rg = new double[rounds];
            double tickNs = 1e9 / System.Diagnostics.Stopwatch.Frequency;
            for (int i = 0; i < rounds; i++) {
                double l = AllocMeter.Ticks(ref loop, nLoop) * tickNs / nLoop;
                double st = AllocMeter.Ticks(ref stat, nStat) * tickNs / nStat;
                double g = AllocMeter.Ticks(ref group, nGroup) * tickNs / nGroup;
                double o = AllocMeter.Ticks(ref over, nOver) * tickNs / nOver;
                rs[i] = st / l;
                rsNet[i] = (st - o) / l;
                rg[i] = g / l;
            }
            Array.Sort(rs);
            Array.Sort(rsNet);
            Array.Sort(rg);
            r.Set("rounds", rounds);
            r.Set("ratio_static_median", rs[rounds / 2]);
            r.Set("ratio_static_min", rs[0]);
            r.Set("ratio_static_max", rs[rounds - 1]);
            r.Set("ratio_static_without_stand_delegate_median", rsNet[rounds / 2]);
            r.Set("ratio_method_group_median", rg[rounds / 2]);
            r.Set("ratio_method_group_min", rg[0]);
            r.Set("ratio_method_group_max", rg[rounds - 1]);
        }
    }
}
