using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using KenseiECS;
using Stand.Contracts.Probe;

namespace Stand.Contracts {
    /// <summary>
    /// Promises of 2.3.0 outside the matrix: the reserve a filter takes from its component types
    /// (Filter.ReservedCount: the smallest Inc reserve, the sum of the Any reserves, capped at
    /// World.ReservedEntities, the same in any order of the calls, the cap moved by the obsolete World.Reserve)
    /// and World.UnsubscribeLater (applied when the outermost SystemsRunner.Run returns, not by a nested runner,
    /// by a phase run on its own, at the next NextTick outside a Run; skipped for a destroyed entity, dropped by
    /// Clear, a no-op on a destroyed world, accepted while the entity's own DestroyEntity runs).
    /// The API is reached through reflection (the stand builds against 2.1.1 and 2.2.x too); on a tree without it
    /// every probe is not-available. Each probe prints one line; its C# (Code) prints the same line, for the repro.
    /// </summary>
    public static class Diagnostics230 {
        private const BindingFlags Inst = BindingFlags.Public | BindingFlags.Instance;

        private static readonly MethodInfo ReserveEntitiesM = typeof(World).GetMethod("ReserveEntities", Inst, null, new[] { typeof(int) }, null);
        private static readonly MethodInfo WorldReserveM = typeof(World).GetMethod("Reserve", Inst, null, new[] { typeof(int), typeof(int) }, null);
        private static readonly PropertyInfo ReservedCountP = typeof(Filter).GetProperty("ReservedCount", Inst);
        private static readonly MethodInfo AddPhaseM = typeof(SystemsRunner).GetMethod("AddPhase", Inst, null, new[] { typeof(string), typeof(SystemsRunner) }, null);
        private static readonly MethodInfo UnsubscribeLaterM = typeof(WorldListenerExtensions).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "UnsubscribeLater" && m.IsGenericMethodDefinition && m.GetParameters().Length == 3)?.MakeGenericMethod(typeof(IProbeListener));

        public static void Run(DiagResult d) {
            Reserve(d);
            Unsubscribe(d);
        }

        private static Exception Unwrap(Exception e) => e is TargetInvocationException t && t.InnerException != null ? t.InnerException : e;

        private static string Observe(Func<string> probe, out bool threw) {
            try {
                threw = false;
                return probe();
            } catch (Exception e) {
                threw = true;
                return "ex:" + Unwrap(e).GetType().Name;
            }
        }

        private static void Add(List<ContractProbe> list, string id, string area, string expected, string expectedLine, string code, Func<string> probe) {
            string observed = Observe(probe, out bool threw);
            list.Add(new ContractProbe {
                Id = id, Area = area, Expected = expected + " (prints: " + expectedLine + ")", Observed = observed, Code = code,
                Status = !threw && observed == expectedLine ? "ok" : "violated"
            });
        }

        // =====================================================================
        // Filter.ReservedCount from the component types
        // =====================================================================

        private static void ReserveEntities(World w, int n) => ReserveEntitiesM.Invoke(w, new object[] { n });

        private static void PoolReserve(object pool, int n) {
            var m = pool.GetType().GetMethod("Reserve", Inst, null, new[] { typeof(int) }, null);
            m.Invoke(pool, new object[] { n });
        }

        private static int RC(Filter f) => (int)ReservedCountP.GetValue(f);

        private static void Reserve(DiagResult d) {
            if (ReserveEntitiesM == null || ReservedCountP == null) {
                d.V230.Add(new ContractProbe { Id = "reserve/api", Area = "reserve-from-types", Status = "not-available", Note = "World.ReserveEntities and Filter.ReservedCount are not in this version" });
                return;
            }
            const string area = "reserve-from-types";

            Add(d.V230, "reserve/inc-smallest", area,
                "a filter whose Inc types are all reserved takes the smallest of their reserves; one with an Inc type that is not reserved is not reserved",
                "AB=40 | A=100 | AC=0",
                "var w = new World();\nw.Pool<CA>().Reserve(100);\nw.Pool<CB>().Reserve(40);\n"
                + "var ab = w.Filter().Inc<CA>().Inc<CB>().End();\nvar a = w.Filter().Inc<CA>().End();\nvar ac = w.Filter().Inc<CA>().Inc<CC>().End();\n"
                + "return \"AB=\" + ab.ReservedCount + \" | A=\" + a.ReservedCount + \" | AC=\" + ac.ReservedCount;\n",
                () => {
                    var w = new World();
                    PoolReserve(w.Pool<CA>(), 100);
                    PoolReserve(w.Pool<CB>(), 40);
                    var ab = w.Filter().Inc<CA>().Inc<CB>().End();
                    var a = w.Filter().Inc<CA>().End();
                    var ac = w.Filter().Inc<CA>().Inc<CC>().End();
                    return "AB=" + RC(ab) + " | A=" + RC(a) + " | AC=" + RC(ac);
                });

            Add(d.V230, "reserve/any-sum", area,
                "a filter without Inc types takes the sum of its Any types' reserves when they are all reserved; with Inc types, at most that sum",
                "anyBD=70 | A_anyBD=70 | A_anyBC=100 | anyBC=0",
                "var w = new World();\nw.Pool<CA>().Reserve(100);\nw.Pool<CB>().Reserve(40);\nw.Pool<CD>().Reserve(30);\n"
                + "var bd = w.Filter().Any<CB>().Any<CD>().End();\nvar abd = w.Filter().Inc<CA>().Any<CB>().Any<CD>().End();\n"
                + "var abc = w.Filter().Inc<CA>().Any<CB>().Any<CC>().End();\nvar bc = w.Filter().Any<CB>().Any<CC>().End();\n"
                + "return \"anyBD=\" + bd.ReservedCount + \" | A_anyBD=\" + abd.ReservedCount + \" | A_anyBC=\" + abc.ReservedCount + \" | anyBC=\" + bc.ReservedCount;\n",
                () => {
                    var w = new World();
                    PoolReserve(w.Pool<CA>(), 100);
                    PoolReserve(w.Pool<CB>(), 40);
                    PoolReserve(w.Pool<CD>(), 30);
                    var bd = w.Filter().Any<CB>().Any<CD>().End();
                    var abd = w.Filter().Inc<CA>().Any<CB>().Any<CD>().End();
                    var abc = w.Filter().Inc<CA>().Any<CB>().Any<CC>().End();
                    var bc = w.Filter().Any<CB>().Any<CC>().End();
                    return "anyBD=" + RC(bd) + " | A_anyBD=" + RC(abd) + " | A_anyBC=" + RC(abc) + " | anyBC=" + RC(bc);
                });

            Add(d.V230, "reserve/cap-reserved-entities", area,
                "once the world reserved entity slots, a filter's reserve from its types is at most ReservedEntities, and a later ReserveEntities moves the cap",
                "A=50 | anyBD=50 | after ReserveEntities(80): A=80 anyBD=80",
                "var w = new World();\nw.Pool<CA>().Reserve(100);\nw.Pool<CB>().Reserve(1000);\nw.Pool<CD>().Reserve(1000);\nw.ReserveEntities(50);\n"
                + "var a = w.Filter().Inc<CA>().End();\nvar bd = w.Filter().Any<CB>().Any<CD>().End();\n"
                + "var first = \"A=\" + a.ReservedCount + \" | anyBD=\" + bd.ReservedCount;\nw.ReserveEntities(80);\n"
                + "return first + \" | after ReserveEntities(80): A=\" + a.ReservedCount + \" anyBD=\" + bd.ReservedCount;\n",
                () => {
                    var w = new World();
                    PoolReserve(w.Pool<CA>(), 100);
                    PoolReserve(w.Pool<CB>(), 1000);
                    PoolReserve(w.Pool<CD>(), 1000);
                    ReserveEntities(w, 50);
                    var a = w.Filter().Inc<CA>().End();
                    var bd = w.Filter().Any<CB>().Any<CD>().End();
                    var first = "A=" + RC(a) + " | anyBD=" + RC(bd);
                    ReserveEntities(w, 80);
                    return first + " | after ReserveEntities(80): A=" + RC(a) + " anyBD=" + RC(bd);
                });

            // The same final reserves in four orders: the EcsBootstrap order (the reserve, then the filters the systems
            // create in Init), filters first, pools first, the entity slots between filters and pools.
            Add(d.V230, "reserve/any-order", area,
                "the reserve a filter takes from its types is the same whatever the order of ReserveEntities, Pool.Reserve and the filter's creation (a later ReserveEntities can bring it down)",
                "entities,pools,filters=40/60/60 | filters,pools,entities=40/60/60 | pools,filters,entities=40/60/60 | filters,entities,pools=40/60/60",
                "string Order(string order) {\n"
                + "    var w = new World();\n    Filter ab = null, bd = null, abd = null;\n"
                + "    foreach (var step in order.Split(',')) {\n"
                + "        if (step == \"entities\") { w.ReserveEntities(60); }\n"
                + "        else if (step == \"pools\") { w.Pool<CA>().Reserve(100); w.Pool<CB>().Reserve(40); w.Pool<CD>().Reserve(30); }\n"
                + "        else { ab = w.Filter().Inc<CA>().Inc<CB>().End(); bd = w.Filter().Any<CB>().Any<CD>().End(); abd = w.Filter().Inc<CA>().Any<CB>().Any<CD>().End(); }\n"
                + "    }\n"
                + "    return order + \"=\" + ab.ReservedCount + \"/\" + bd.ReservedCount + \"/\" + abd.ReservedCount;\n}\n"
                + "return Order(\"entities,pools,filters\") + \" | \" + Order(\"filters,pools,entities\") + \" | \" + Order(\"pools,filters,entities\") + \" | \" + Order(\"filters,entities,pools\");\n",
                () => {
                    string Order(string order) {
                        var w = new World();
                        Filter ab = null, bd = null, abd = null;
                        foreach (var step in order.Split(',')) {
                            if (step == "entities") {
                                ReserveEntities(w, 60);
                            } else if (step == "pools") {
                                PoolReserve(w.Pool<CA>(), 100);
                                PoolReserve(w.Pool<CB>(), 40);
                                PoolReserve(w.Pool<CD>(), 30);
                            } else {
                                ab = w.Filter().Inc<CA>().Inc<CB>().End();
                                bd = w.Filter().Any<CB>().Any<CD>().End();
                                abd = w.Filter().Inc<CA>().Any<CB>().Any<CD>().End();
                            }
                        }
                        return order + "=" + RC(ab) + "/" + RC(bd) + "/" + RC(abd);
                    }
                    return Order("entities,pools,filters") + " | " + Order("filters,pools,entities") + " | " + Order("pools,filters,entities") + " | " + Order("filters,entities,pools");
                });

            if (WorldReserveM != null) {
                Add(d.V230, "reserve/obsolete-moves-cap", area,
                    "the obsolete World.Reserve moves the cap of the reserve a filter takes from its types, as ReserveEntities does",
                    "A=10 | after World.Reserve(60, 0): A=60",
                    "var w = new World();\nw.Pool<CA>().Reserve(100);\nw.ReserveEntities(10);\nvar a = w.Filter().Inc<CA>().End();\n"
                    + "var first = \"A=\" + a.ReservedCount;\nw.Reserve(60, 0);\nreturn first + \" | after World.Reserve(60, 0): A=\" + a.ReservedCount;\n",
                    () => {
                        var w = new World();
                        PoolReserve(w.Pool<CA>(), 100);
                        ReserveEntities(w, 10);
                        var a = w.Filter().Inc<CA>().End();
                        var first = "A=" + RC(a);
                        WorldReserveM.Invoke(w, new object[] { 60, 0 });
                        return first + " | after World.Reserve(60, 0): A=" + RC(a);
                    });
            }
        }

        // =====================================================================
        // World.UnsubscribeLater
        // =====================================================================

        private static void Later(World w, Entity e, IProbeListener l) => UnsubscribeLaterM.Invoke(null, new object[] { w, e, l });

        private static string B(bool b) => b ? "True" : "False";

        private const string Setup =
            "var w = new World();\nvar l = new ProbeListener();\nvar e = w.CreateEntity(new CA { V = 1 });\nw.Subscribe<IProbeListener>(e, l);\n";

        private static (World, Entity, ProbeListener) Make() {
            var w = new World();
            var l = new ProbeListener();
            var e = w.CreateEntity(new CA { V = 1 });
            w.Subscribe<IProbeListener>(e, l);
            return (w, e, l);
        }

        private static void Unsubscribe(DiagResult d) {
            if (UnsubscribeLaterM == null) {
                d.V230.Add(new ContractProbe { Id = "unsub-later/api", Area = "unsubscribe-later", Status = "not-available", Note = "World.UnsubscribeLater is not in this version" });
                return;
            }
            const string area = "unsubscribe-later";

            Add(d.V230, "unsub-later/outermost-run", area,
                "a removal queued by a system is applied when the outermost SystemsRunner.Run returns; until then the listener stays in the list",
                "duringRun=True | afterRun=False",
                Setup + "bool during = false;\nvar r = new SystemsRunner(w);\n"
                + "r.Add(new HookSystem { OnRun = world => { world.UnsubscribeLater<IProbeListener>(e, l); during = ListenerChecks.Holds(world, e, l); } });\n"
                + "r.Init();\nr.Run();\nreturn \"duringRun=\" + during + \" | afterRun=\" + ListenerChecks.Holds(w, e, l);\n",
                () => {
                    var (w, e, l) = Make();
                    bool during = false;
                    var r = new SystemsRunner(w);
                    r.Add(new HookSystem { OnRun = world => { Later(world, e, l); during = ListenerChecks.Holds(world, e, l); } });
                    r.Init();
                    r.Run();
                    return "duringRun=" + B(during) + " | afterRun=" + B(ListenerChecks.Holds(w, e, l));
                });

            Add(d.V230, "unsub-later/nested-runner-defers", area,
                "a nested runner's Run does not apply the removals queued in it: the outermost Run does, when it returns",
                "afterNestedRun=True | afterOuterRun=False",
                Setup + "bool afterNested = false;\nvar inner = new SystemsRunner(w);\n"
                + "inner.Add(new HookSystem { OnRun = world => world.UnsubscribeLater<IProbeListener>(e, l) });\n"
                + "var r = new SystemsRunner(w);\nr.Add(inner);\n"
                + "r.Add(new HookSystem { OnRun = world => afterNested = ListenerChecks.Holds(world, e, l) });\n"
                + "r.Init();\nr.Run();\nreturn \"afterNestedRun=\" + afterNested + \" | afterOuterRun=\" + ListenerChecks.Holds(w, e, l);\n",
                () => {
                    var (w, e, l) = Make();
                    bool afterNested = false;
                    var inner = new SystemsRunner(w);
                    inner.Add(new HookSystem { OnRun = world => Later(world, e, l) });
                    var r = new SystemsRunner(w);
                    r.Add(inner);
                    r.Add(new HookSystem { OnRun = world => afterNested = ListenerChecks.Holds(world, e, l) });
                    r.Init();
                    r.Run();
                    return "afterNestedRun=" + B(afterNested) + " | afterOuterRun=" + B(ListenerChecks.Holds(w, e, l));
                });

            if (AddPhaseM != null) {
                Add(d.V230, "unsub-later/phase-applies", area,
                    "a phase run on its own (GetRunner(name).Run(), the fixed and late runners of EcsBootstrap) is the outermost Run of its step and applies the removals queued during it",
                    "duringPhase=True | afterPhaseRun=False",
                    Setup + "bool during = false;\nvar phase = new SystemsRunner(w);\n"
                    + "phase.Add(new HookSystem { OnRun = world => { world.UnsubscribeLater<IProbeListener>(e, l); during = ListenerChecks.Holds(world, e, l); } });\n"
                    + "var r = new SystemsRunner(w);\nr.AddPhase(\"fixed\", phase);\nr.Init();\nr.Run();\nphase.Run();\n"
                    + "return \"duringPhase=\" + during + \" | afterPhaseRun=\" + ListenerChecks.Holds(w, e, l);\n",
                    () => {
                        var (w, e, l) = Make();
                        bool during = false;
                        var phase = new SystemsRunner(w);
                        phase.Add(new HookSystem { OnRun = world => { Later(world, e, l); during = ListenerChecks.Holds(world, e, l); } });
                        var r = new SystemsRunner(w);
                        AddPhaseM.Invoke(r, new object[] { "fixed", phase });
                        r.Init();
                        r.Run();
                        phase.Run();
                        return "duringPhase=" + B(during) + " | afterPhaseRun=" + B(ListenerChecks.Holds(w, e, l));
                    });
            }

            Add(d.V230, "unsub-later/outside-run-next-tick", area,
                "called while no runner runs, the removal is applied at the next World.NextTick",
                "afterCall=True | afterNextTick=False",
                Setup + "w.UnsubscribeLater<IProbeListener>(e, l);\nvar afterCall = ListenerChecks.Holds(w, e, l);\nw.NextTick();\n"
                + "return \"afterCall=\" + afterCall + \" | afterNextTick=\" + ListenerChecks.Holds(w, e, l);\n",
                () => {
                    var (w, e, l) = Make();
                    Later(w, e, l);
                    var afterCall = ListenerChecks.Holds(w, e, l);
                    w.NextTick();
                    return "afterCall=" + B(afterCall) + " | afterNextTick=" + B(ListenerChecks.Holds(w, e, l));
                });

            Add(d.V230, "unsub-later/destroyed-entity-skipped", area,
                "a removal whose entity was destroyed by then is skipped, also when a new entity took the slot: the new entity keeps its listener",
                "sameSlot=True | newEntityKeeps=True",
                Setup + "w.UnsubscribeLater<IProbeListener>(e, l);\nw.DestroyEntity(e);\nvar e2 = w.CreateEntity(new CA { V = 2 });\n"
                + "w.Subscribe<IProbeListener>(e2, l);\nw.NextTick();\n"
                + "return \"sameSlot=\" + (e2.Index == e.Index) + \" | newEntityKeeps=\" + ListenerChecks.Holds(w, e2, l);\n",
                () => {
                    var (w, e, l) = Make();
                    Later(w, e, l);
                    w.DestroyEntity(e);
                    var e2 = w.CreateEntity(new CA { V = 2 });
                    w.Subscribe<IProbeListener>(e2, l);
                    w.NextTick();
                    return "sameSlot=" + B(e2.Index == e.Index) + " | newEntityKeeps=" + B(ListenerChecks.Holds(w, e2, l));
                });

            // World.Clear drops the queue: the queued listener is no longer referenced by the world (a control listener,
            // subscribed and not queued, must be collected too, or the probe says nothing).
            {
                string observed = Observe(() => {
                    var (world, queued, control) = ClearedWorldWithQueue();
                    for (int i = 0; i < 3; i++) {
                        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
                        GC.WaitForPendingFinalizers();
                    }
                    string line = "queuedCollected=" + B(!queued.IsAlive) + " | controlCollected=" + B(!control.IsAlive);
                    GC.KeepAlive(world);
                    return line;
                }, out bool threw);
                var p = new ContractProbe {
                    Id = "unsub-later/clear-drops", Area = area,
                    Expected = "World.Clear drops the queued removals: after Clear the world no longer references a queued listener (prints: queuedCollected=True | controlCollected=True)",
                    Observed = observed,
                    Code = "(World, WeakReference, WeakReference) Make() {\n"
                        + "    var w = new World();\n    var l = new ProbeListener();\n    var c = new ProbeListener();\n"
                        + "    var e = w.CreateEntity(new CA { V = 1 });\n    w.Subscribe<IProbeListener>(e, l);\n    w.Subscribe<IProbeListener>(e, c);\n"
                        + "    w.UnsubscribeLater<IProbeListener>(e, l);\n    w.Clear();\n    return (w, new WeakReference(l), new WeakReference(c));\n}\n"
                        + "var (world, queued, control) = Make();\n"
                        + "for (int i = 0; i < 3; i++) { GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers(); }\n"
                        + "var line = \"queuedCollected=\" + !queued.IsAlive + \" | controlCollected=\" + !control.IsAlive;\nGC.KeepAlive(world);\nreturn line;\n"
                };
                if (threw) {
                    p.Status = "violated";
                } else if (observed.EndsWith("controlCollected=False", StringComparison.Ordinal)) {
                    p.Status = "inconclusive";
                    p.Note = "the control failed: a listener that was only subscribed survived Clear and a full GC, so the probe cannot tell what keeps the queued one";
                } else {
                    p.Status = observed.StartsWith("queuedCollected=True", StringComparison.Ordinal) ? "ok" : "violated";
                }
                d.V230.Add(p);
            }

            Add(d.V230, "unsub-later/destroyed-world-noop", area,
                "on a destroyed world UnsubscribeLater does nothing",
                "call=ok",
                Setup + "w.Destroy();\ntry { w.UnsubscribeLater<IProbeListener>(e, l); return \"call=ok\"; } catch (Exception x) { return \"call=ex:\" + x.GetType().Name; }\n",
                () => {
                    var (w, e, l) = Make();
                    w.Destroy();
                    try {
                        Later(w, e, l);
                        return "call=ok";
                    } catch (Exception x) {
                        return "call=ex:" + Unwrap(x).GetType().Name;
                    }
                });

            Add(d.V230, "unsub-later/dying-entity", area,
                "a listener may call UnsubscribeLater while its entity's own DestroyEntity runs (accepted under KENSEI_DEBUG too)",
                "callDuringDestroy=ok | aliveAfter=False",
                Setup + "string call = \"not called\";\n"
                + "w.Pool<CA>().AddListener(new HookComponentListener { Removed = index => {\n"
                + "    try { w.UnsubscribeLater<IProbeListener>(e, l); call = \"ok\"; } catch (Exception x) { call = \"ex:\" + x.GetType().Name; }\n} });\n"
                + "w.DestroyEntity(e);\nw.NextTick();\nreturn \"callDuringDestroy=\" + call + \" | aliveAfter=\" + w.IsAlive(e);\n",
                () => {
                    var (w, e, l) = Make();
                    string call = "not called";
                    w.Pool<CA>().AddListener(new HookComponentListener {
                        Removed = index => {
                            try {
                                Later(w, e, l);
                                call = "ok";
                            } catch (Exception x) {
                                call = "ex:" + Unwrap(x).GetType().Name;
                            }
                        }
                    });
                    w.DestroyEntity(e);
                    w.NextTick();
                    return "callDuringDestroy=" + call + " | aliveAfter=" + B(w.IsAlive(e));
                });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static (World, WeakReference, WeakReference) ClearedWorldWithQueue() {
            var w = new World();
            var l = new ProbeListener();
            var c = new ProbeListener();
            var e = w.CreateEntity(new CA { V = 1 });
            w.Subscribe<IProbeListener>(e, l);
            w.Subscribe<IProbeListener>(e, c);
            Later(w, e, l);
            w.Clear();
            return (w, new WeakReference(l), new WeakReference(c));
        }
    }
}
