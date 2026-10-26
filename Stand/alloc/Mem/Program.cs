using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using KenseiECS;
using KenseiStand.Alloc.MemGen;

namespace KenseiStand.Alloc.Mem {
    /// <summary>
    /// Memory of a world with N component types and E entities, one layout per process:
    /// <list type="bullet">
    /// <item>organic_rare: no Reserve; every entity holds M0; each other type sits on one of the
    /// first 16 entities (singletons and rare types, the usual case);</item>
    /// <item>organic_spread: no Reserve; each other type sits on one of the last 16 entities (the
    /// worst case for organic sparse growth);</item>
    /// <item>reserve_rare / reserve_spread: the same after World.Reserve(E) (2.2.0+, through
    /// reflection), called before the pools exist, so every pool starts with E sparse slots;</item>
    /// <item>reserve_late_rare: Reserve(E) after the pools and filters exist (systems created them at
    /// load, then a level sets its entity cap), so every existing sparse array is resized to E.</item>
    /// </list>
    /// Reported: bytes allocated by this thread and by the process (GC.GetTotalAllocatedBytes(true))
    /// while the world was built, the live heap growth (GC.GetTotalMemory(true)), and what the pools
    /// themselves report (AllocatedBytes, SparseCapacity).
    /// </summary>
    public static class Program {
#if KENSEI_DEBUG
        private const bool KenseiDebug = true;
#else
        private const bool KenseiDebug = false;
#endif

        public static int Main(string[] args) {
            int types = 100, entities = 50000, filters = 0;
            string mode = "organic_rare";
            for (int i = 0; i < args.Length; i++) {
                switch (args[i]) {
                    case "--types": types = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--entities": entities = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--layout": mode = args[++i]; break;
                    case "--filters": filters = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    default:
                        Console.Error.WriteLine("unknown argument " + args[i]);
                        return 2;
                }
            }
            if (types > GenTypes.Count) {
                Console.Error.WriteLine("only " + GenTypes.Count + " generated types");
                return 2;
            }
            var reserveMethod = typeof(World).GetMethod("Reserve", BindingFlags.Public | BindingFlags.Instance, null,
                new[] { typeof(int), typeof(int) }, null);
            var reserve = reserveMethod == null ? null
                : (Action<World, int, int>)Delegate.CreateDelegate(typeof(Action<World, int, int>), reserveMethod);
            bool wantsReserve = mode.StartsWith("reserve", StringComparison.Ordinal);
            bool spread = mode.EndsWith("spread", StringComparison.Ordinal);
            bool late = mode.Contains("_late");
            var self = AllocMeter.SelfTest(1000);
            string head = "\"runtime\":\"" + RuntimeInformation.FrameworkDescription + "\",\"kensei_debug\":" + (KenseiDebug ? "true" : "false")
                + ",\"selftest_ok\":" + (self.Ok ? "true" : "false")
                + ",\"layout\":\"" + mode + "\",\"types\":" + types + ",\"entities\":" + entities + ",\"filters\":" + filters;
            if (wantsReserve && reserve == null) {
                Console.WriteLine("MEM {" + head + ",\"available\":false,\"reason\":\"not in this version: World.Reserve(entities, components)\"}");
                return 0;
            }

            // Every generic instantiation, static constructor and code path once, on a throwaway world.
            {
                var w0 = new World();
                reserve?.Invoke(w0, 64, 0);
                for (int t = 0; t < types; t++) {
                    GenTypes.CreatePool(w0, t);
                }
                var e0 = w0.CreateEntity(new M0000());
                for (int t = 1; t < types; t++) {
                    GenTypes.AddAt(w0, e0, t);
                }
                for (int f = 0; f < filters; f++) {
                    GenTypes.IncFilter(w0, 1 + f % (types - 1));
                }
                w0.DestroyEntity(e0);
                w0.Destroy();
            }

            long live0 = Live();
            long total0 = GC.GetTotalAllocatedBytes(true);
            long thread0 = GC.GetAllocatedBytesForCurrentThread();

            var world = new World();
            long reserveCall = 0;
            if (wantsReserve && !late) {
                long b = GC.GetAllocatedBytesForCurrentThread();
                reserve(world, entities, 0);
                reserveCall = GC.GetAllocatedBytesForCurrentThread() - b;
            }
            for (int t = 0; t < types; t++) {
                GenTypes.CreatePool(world, t);
            }
            // Filters over the rare types, created at load like a system's Init.
            var keep = new Filter[filters];
            for (int f = 0; f < filters; f++) {
                keep[f] = GenTypes.IncFilter(world, 1 + f % (types - 1));
            }
            if (wantsReserve && late) {
                long b = GC.GetAllocatedBytesForCurrentThread();
                reserve(world, entities, 0);
                reserveCall = GC.GetAllocatedBytesForCurrentThread() - b;
            }
            var es = new Entity[entities];
            for (int i = 0; i < entities; i++) {
                es[i] = world.CreateEntity(new M0000 { V = i });
            }
            for (int t = 1; t < types; t++) {
                var e = spread ? es[entities - 1 - (t % 16)] : es[t % 16];
                GenTypes.AddAt(world, e, t);
            }

            long thread1 = GC.GetAllocatedBytesForCurrentThread();
            long total1 = GC.GetTotalAllocatedBytes(true);
            long live1 = Live();

            long filterBytes = 0;
            foreach (var f in keep) {
                filterBytes += f.AllocatedBytes;
            }
            long poolBytes = 0, sparseInts = 0;
            int pools = 0;
            foreach (var p in world.ActivePools) {
                pools++;
                poolBytes += p.AllocatedBytes;
                sparseInts += p.SparseCapacity;
            }
            // The entity array is the scenario's own (E handles), not the library's.
            long handles = 24 + (long)entities * Marshal.SizeOf<Entity>();
            Console.WriteLine("MEM {" + head + ",\"available\":true"
                + ",\"alloc_thread\":" + (thread1 - thread0)
                + ",\"alloc_total\":" + (total1 - total0)
                + ",\"live\":" + (live1 - live0)
                + ",\"scenario_handles\":" + handles
                + ",\"reserve_call\":" + reserveCall
                + ",\"pools\":" + pools
                + ",\"pool_allocated_bytes\":" + poolBytes
                + ",\"pool_sparse_ints\":" + sparseInts
                + ",\"filter_allocated_bytes\":" + filterBytes
                + ",\"alive\":" + world.EntityCount + "}");
            GC.KeepAlive(world);
            GC.KeepAlive(es);
            GC.KeepAlive(keep);
            return 0;
        }

        private static long Live() {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            return GC.GetTotalMemory(true);
        }
    }
}
