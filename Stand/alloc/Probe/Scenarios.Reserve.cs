using System;
using KenseiECS;
using KenseiStand.Alloc.Gen;

namespace KenseiStand.Alloc.Probe {
    // ==================================================================
    // First growth of N pools without Reserve (one-time bytes)
    // ==================================================================

    /// <summary>
    /// N generated component types, pools created at load, their Add/Remove paths run once on
    /// entity 0 (no growth). Measured once: adding each type to entity 299, which grows every pool's
    /// sparse array from 256 to 512 ints. Nothing was reserved, so World.Reserve's growth watch must
    /// not run. Expected bytes are the arrays alone: N x (24 + 512 x 4). The calibration measures the
    /// first typeof(T).Name of a fresh generated type in the same process, to tell a name string
    /// apart from anything else.
    /// </summary>
    public sealed class FirstGrowthNoReserve : Scenario {
        public override string Id => "growth.first_n_pools";

        public const int Pools = 100;
        public const int HighEntity = 299;

        private struct GrowAll : IAllocOp {
            public World W;
            public Entity E;
            public int N;
            public void Invoke() {
                for (int t = 0; t < N; t++) {
                    GenTypes.AddAt(W, E, t);
                }
            }
        }

        private struct Cycle : IAllocOp {
            public World W;
            public Entity E;
            public int N;
            public int T;
            public void Invoke() {
                int t = T;
                if (++T == N) {
                    T = 0;
                }
                GenTypes.RemoveAt(W, E, t);
                GenTypes.AddAt(W, E, t);
            }
        }

        private struct NameOp : IAllocOp {
            public Type Type;
            public void Invoke() {
                Sink.Value = Type.Name;
            }
        }

        public override void Run(Settings s, Result r) {
            int n = Math.Min(Pools, GenTypes.Count - 2);
            var world = Registry.CarrierWorld(HighEntity + 1, out var entities);
            for (int t = 0; t < n; t++) {
                GenTypes.CreatePool(world, t);
            }
            var low = entities[0];
            for (int t = 0; t < n; t++) {
                GenTypes.AddAt(world, low, t);
                GenTypes.RemoveAt(world, low, t);
            }
            // Calibration on two types no scenario touches: the first and second Name of a fresh type.
            var name = new NameOp { Type = GenTypes.TypeOf(GenTypes.Count - 1) };
            long firstName = AllocMeter.Once(ref name);
            long secondName = AllocMeter.Once(ref name);

            var grow = new GrowAll { W = world, E = entities[HighEntity], N = n };
            r.FirstBytes = AllocMeter.Once(ref grow);

            long arrays = 0;
            int grown = 0;
            foreach (var pool in world.ActivePools) {
                if (pool.ComponentType.Namespace == typeof(GenTypes).Namespace) {
                    if (pool.SparseCapacity > 256) {
                        grown++;
                        arrays += 24 + 4L * pool.SparseCapacity;
                    }
                }
            }
            r.Set("pools", n);
            r.Set("pools_grown", grown);
            r.Set("expected_array_bytes", arrays);
            r.Set("extra_bytes", r.FirstBytes.Value - arrays);
            r.Set("extra_bytes_per_pool", grown > 0 ? (double)(r.FirstBytes.Value - arrays) / grown : 0.0);
            r.Set("type_name_first_call_bytes", firstName);
            r.Set("type_name_second_call_bytes", secondName);
            r.Set("type_name_length", GenTypes.NameLength);
            if (Compat.GrowthsAfterReserve != null) {
                r.Set("growths_after_reserve", Compat.GrowthsAfterReserve(world));
            }
            var cycle = new Cycle { W = world, E = entities[HighEntity], N = n };
            Steady(s, r, ref cycle);
        }
    }

    // ==================================================================
    // First growth of N filters without Reserve (one-time bytes)
    // ==================================================================

    /// <summary>
    /// The first growth of N filters without Reserve: N filters Inc&lt;G_t&gt; over generated types that
    /// no other scenario uses, created at load and empty. Their pools are grown and emptied first, and
    /// every code path ran once on a throwaway world, so inside the window only the filters grow: adding
    /// each type to 65 entities creates each filter's first sparse page and grows its dense array once
    /// (65 -> 130 slots). Nothing was reserved, so World.Reserve's growth watch must not run and the
    /// expected bytes are the arrays alone. The calibration measures what Filter.ToString() (the
    /// filter's description, which a growth report names it by) allocates in this process, to tell a
    /// description string per filter apart from anything else.
    /// </summary>
    public sealed class FirstGrowthNoReserveFilters : Scenario {
        public override string Id => "growth.first_n_filters";

        public const int Filters = 100;
        public const int FirstType = 128;
        public const int Matching = 65;

        private struct GrowAll : IAllocOp {
            public World W;
            public Entity[] E;
            public int First;
            public int N;
            public int Count;
            public void Invoke() {
                for (int t = First; t < First + N; t++) {
                    for (int i = 0; i < Count; i++) {
                        GenTypes.AddAt(W, E[i], t);
                    }
                }
            }
        }

        private struct Cycle : IAllocOp {
            public World W;
            public Entity E;
            public int First;
            public int N;
            public int T;
            public void Invoke() {
                int t = First + T;
                if (++T == N) {
                    T = 0;
                }
                GenTypes.RemoveAt(W, E, t);
                GenTypes.AddAt(W, E, t);
            }
        }

        private struct DescribeOp : IAllocOp {
            public Filter F;
            public void Invoke() {
                Sink.Value = F.ToString();
            }
        }

        public override void Run(Settings s, Result r) {
            int n = Math.Min(Filters, GenTypes.Count - FirstType - 4);
            // Every code path once on a throwaway world: Add/Remove of each type, and a filter's first page and
            // dense growth on a filter over one more type that is not measured (so every measured filter is
            // alike). JIT and type loading stay out of the window, as on IL2CPP.
            {
                var scratch = Registry.CarrierWorld(Matching, out var se);
                for (int t = FirstType; t <= FirstType + n; t++) {
                    GenTypes.CreatePool(scratch, t);
                }
                GenTypes.IncFilter(scratch, FirstType + n);
                for (int t = FirstType; t <= FirstType + n; t++) {
                    for (int i = 0; i < Matching; i++) {
                        GenTypes.AddAt(scratch, se[i], t);
                    }
                }
                for (int t = FirstType; t <= FirstType + n; t++) {
                    for (int i = 0; i < Matching; i++) {
                        GenTypes.RemoveAt(scratch, se[i], t);
                    }
                }
                scratch.Destroy();
            }

            var world = Registry.CarrierWorld(Matching, out var entities);
            for (int t = FirstType; t < FirstType + n; t++) {
                GenTypes.CreatePool(world, t);
            }
            // The pools grow their dense arrays now and are emptied again: inside the window only filters grow.
            for (int t = FirstType; t < FirstType + n; t++) {
                for (int i = 0; i < Matching; i++) {
                    GenTypes.AddAt(world, entities[i], t);
                }
            }
            for (int t = FirstType; t < FirstType + n; t++) {
                for (int i = 0; i < Matching; i++) {
                    GenTypes.RemoveAt(world, entities[i], t);
                }
            }
            var filters = new Filter[n];
            var before = new long[n];
            var capBefore = new int[n];
            for (int k = 0; k < n; k++) {
                filters[k] = GenTypes.IncFilter(world, FirstType + k);
                before[k] = filters[k].AllocatedBytes;
                capBefore[k] = filters[k].DenseCapacity;
            }
            // Calibration on a filter over a type no scenario touches: its description, first and second time.
            var describe = new DescribeOp { F = GenTypes.IncFilter(world, GenTypes.Count - 2) };
            long describeFirst = AllocMeter.Once(ref describe);
            long describeSecond = AllocMeter.Once(ref describe);

            var grow = new GrowAll { W = world, E = entities, First = FirstType, N = n, Count = Matching };
            r.FirstBytes = AllocMeter.Once(ref grow);

            long expected = 0;
            int grown = 0, paged = 0;
            for (int k = 0; k < n; k++) {
                long delta = filters[k].AllocatedBytes - before[k];
                int capAfter = filters[k].DenseCapacity;
                long denseDelta = 4L * (capAfter - capBefore[k]);
                long pageBytes = delta - denseDelta;
                if (capAfter != capBefore[k]) {
                    // The new dense array: header + (capacity + 1) ints (slot 0 is the terminator).
                    expected += 24 + 4L * (capAfter + 1);
                    grown++;
                }
                if (pageBytes > 0) {
                    expected += 24 + pageBytes;
                    paged++;
                }
            }
            r.Set("filters", n);
            r.Set("matching", Matching);
            r.Set("filters_grown", grown);
            r.Set("filters_paged", paged);
            r.Set("expected_array_bytes", expected);
            r.Set("extra_bytes", r.FirstBytes.Value - expected);
            r.Set("extra_bytes_per_filter", n > 0 ? (double)(r.FirstBytes.Value - expected) / n : 0.0);
            r.Set("filter_description_first_bytes", describeFirst);
            r.Set("filter_description_second_bytes", describeSecond);
            if (Compat.GrowthsAfterReserve != null) {
                r.Set("growths_after_reserve", Compat.GrowthsAfterReserve(world));
            }
            var cycle = new Cycle { W = world, E = entities[0], First = FirstType, N = n };
            Steady(s, r, ref cycle);
            GC.KeepAlive(filters);
        }
    }

    // ==================================================================
    // Dense capacity of filters against the request (memory)
    // ==================================================================

    /// <summary>
    /// The dense capacity of a filter against what was asked for. Cases, each on a fresh world:
    /// organic/N — N entities enter a filter, no Reserve (doubling growth, like pools);
    /// filter_reserve/N — Filter.Reserve(N) on a filter at its default capacity (2.2.0+);
    /// world_reserve_existing/N — World.Reserve(N, N) with the filter created before it (2.2.0+);
    /// world_reserve_new/N — the filter created after World.Reserve(N, N);
    /// filter_reserve_after_world/N — World.Reserve(N - 1, N - 1), then Filter.Reserve(N): one more slot.
    /// The pool of the filter's type gets the same request (the same Adds, ComponentPool.Reserve or
    /// World.Reserve), sized exactly: the reference for "what was asked for". Capacities only, no window.
    /// </summary>
    public sealed class FilterDenseCapacity : Scenario {
        public override string Id => "filter.dense_capacity";

        public override void Run(Settings s, Result r) {
            var cases = new System.Text.StringBuilder();
            void Add(string kind, int requested, Filter f, ComponentPoolBase p) {
                if (cases.Length > 0) {
                    cases.Append(';');
                }
                cases.Append(kind).Append('/').Append(requested).Append('/').Append(f.DenseCapacity).Append('/').Append(p.DenseCapacity);
            }

            foreach (int count in new[] { 65, 129, 1000, 5000 }) {
                var w = Registry.CarrierWorld(count, out var es);
                var f = w.Filter().Inc<DenseProbe>().End();
                var pool = w.Pool<DenseProbe>();
                for (int i = 0; i < count; i++) {
                    w.Add(es[i], new DenseProbe { V = i });
                }
                Add("organic", count, f, pool);
            }
            var reserveWorld = Compat.WorldReserve;
            var reserveFilter = Compat.FilterReserve;
            var reservePool = Compat.PoolReserve<DenseProbe>();
            if (reserveWorld == null || reserveFilter == null || reservePool == null) {
                r.Set("reserve_cases", Compat.Missing("World.Reserve, Filter.Reserve, ComponentPool.Reserve"));
            } else {
                foreach (int count in new[] { 65, 100, 1000 }) {
                    var w = new World();
                    var f = w.Filter().Inc<DenseProbe>().End();
                    var pool = w.Pool<DenseProbe>();
                    reserveFilter(f, count);
                    reservePool(pool, count);
                    Add("filter_reserve", count, f, pool);
                }
                foreach (int count in new[] { 65, 100, 1000, 5000 }) {
                    var w = new World();
                    var f = w.Filter().Inc<DenseProbe>().End();
                    var pool = w.Pool<DenseProbe>();
                    reserveWorld(w, count, count);
                    Add("world_reserve_existing", count, f, pool);
                }
                foreach (int count in new[] { 100, 5000 }) {
                    var w = new World();
                    reserveWorld(w, count, count);
                    var f = w.Filter().Inc<DenseProbe>().End();
                    var pool = w.Pool<DenseProbe>();
                    Add("world_reserve_new", count, f, pool);
                }
                {
                    const int count = 5001;
                    var w = new World();
                    var f = w.Filter().Inc<DenseProbe>().End();
                    var pool = w.Pool<DenseProbe>();
                    reserveWorld(w, count - 1, count - 1);
                    reserveFilter(f, count);
                    reservePool(pool, count);
                    Add("filter_reserve_after_world", count, f, pool);
                }
            }
            r.Set("cases", cases.ToString());
        }
    }

    // ==================================================================
    // The first CreateEntity of the process (every version)
    // ==================================================================

    /// <summary>
    /// No entity was created in the process before: a world with its pool created at load, then the
    /// first CreateEntity + DestroyEntity of the process inside the window. The world's arrays do
    /// not grow (one entity), so in release nothing is expected; under KENSEI_DEBUG the bytes are
    /// whatever static state that first call initialises.
    /// </summary>
    public sealed class FirstEntityOfProcess : Scenario {
        public override string Id => "create.first_entity_of_process";

        private struct One : IAllocOp {
            public World W;
            public void Invoke() {
                var e = W.CreateEntity(new Pos { X = 1 });
                W.DestroyEntity(e);
            }
        }

        public override void Run(Settings s, Result r) {
            var world = new World();
            world.Pool<Pos>();
            var op = new One { W = world };
            r.FirstBytes = AllocMeter.Once(ref op);
            r.Set("second_call_bytes", AllocMeter.Once(ref op));
            Steady(s, r, ref op);
        }
    }

    // ==================================================================
    // World.Reserve(n, n) (2.2.0+)
    // ==================================================================

    /// <summary>
    /// The World.Reserve contract: after Reserve(n, n), creating and destroying up to n entities with
    /// the world's components, entering its filters, allocates nothing. Pools and filters are
    /// created at load. firstEntityOfProcess: no entity was created in the process before the
    /// measured pass (the very first CreateEntity runs inside it).
    /// </summary>
    public sealed class ReserveCreateDestroy : Scenario {
        private readonly bool _first;

        public ReserveCreateDestroy(bool firstEntityOfProcess) {
            _first = firstEntityOfProcess;
        }

        public override string Id => _first ? "reserve.first_entities_of_process" : "reserve.create_destroy_n";

        public const int N = 5000;

        private struct CreateDestroyAll : IAllocOp {
            public World W;
            public Entity[] Buf;
            public void Invoke() {
                var b = Buf;
                for (int i = 0; i < b.Length; i++) {
                    b[i] = W.CreateEntity(new Pos { X = i });
                    W.Add(b[i], new Vel { X = 1 });
                }
                for (int i = 0; i < b.Length; i++) {
                    W.DestroyEntity(b[i]);
                }
            }
        }

        public override void Run(Settings s, Result r) {
            var reserve = Compat.WorldReserve;
            if (reserve == null) {
                r.Available = false;
                r.Reason = Compat.Missing("World.Reserve(entities, components)");
                return;
            }
            var world = new World();
            var early = world.Filter().Inc<Pos>().Inc<Vel>().End();
            world.Pool<Pos>();
            reserve(world, N, N);
            world.Pool<Vel>();
            world.Pool<Frozen>();
            var late = world.Filter().Inc<Pos>().Exc<Frozen>().End();
            var op = new CreateDestroyAll { W = world, Buf = new Entity[N] };
            if (!_first) {
                // The code paths once with one entity, like the 2.2.0 test.
                var e = world.CreateEntity(new Pos());
                world.Add(e, new Vel());
                world.DestroyEntity(e);
            }
            r.FirstBytes = Census.Once(ref op, r, world);
            r.Set("n", N);
            r.Set("matched_early_filter_after", early.Count);
            r.Set("matched_late_filter_after", late.Count);
            if (Compat.GrowthsAfterReserve != null) {
                r.Set("growths_after_reserve", Compat.GrowthsAfterReserve(world));
            }
            Steady(s, r, ref op, 1000);
        }
    }

    /// <summary>
    /// A pool that first appears with its first Add in a frame, after Reserve(n, n) at load, with a
    /// filter over its type created at load (a filter does not create pools). createdAtLoad: the
    /// same with world.Pool&lt;T&gt;() called at load after Reserve (the documented remedy).
    /// The bytes are split into the reserve-sized arrays the new pool starts with and the rest.
    /// </summary>
    public sealed class ReserveLatePool : Scenario {
        private readonly bool _atLoad;

        public ReserveLatePool(bool createdAtLoad) {
            _atLoad = createdAtLoad;
        }

        public override string Id => _atLoad ? "reserve.pool_created_at_load" : "reserve.pool_first_add_in_frame";

        public const int N = 5000;

        private struct FirstAddLate : IAllocOp {
            public World W;
            public Entity E;
            public void Invoke() {
                W.Add(E, new Late { V = 1 });
            }
        }

        private struct FirstAddLateLoad : IAllocOp {
            public World W;
            public Entity E;
            public void Invoke() {
                W.Add(E, new LateLoad { V = 1 });
            }
        }

        public override void Run(Settings s, Result r) {
            var reserve = Compat.WorldReserve;
            if (reserve == null) {
                r.Available = false;
                r.Reason = Compat.Missing("World.Reserve(entities, components)");
                return;
            }
            // The code paths of the window (pool creation by a first Add, entering a filter) once, on a
            // throwaway world: JIT and type loading stay out of the window, as on IL2CPP, where nothing
            // is compiled at run time. The measured world's pool is still created inside the window.
            var scratch = new World();
            var se = scratch.CreateEntity(new Carrier());
            scratch.Filter().Inc<Late>().End();
            scratch.Filter().Inc<LateLoad>().End();
            scratch.Add(se, new Late { V = 1 });
            scratch.Add(se, new LateLoad { V = 1 });
            scratch.Destroy();

            var world = new World();
            world.Pool<Carrier>();
            reserve(world, N, N);
            var entities = new Entity[N / 2];
            for (int i = 0; i < entities.Length; i++) {
                entities[i] = world.CreateEntity(new Carrier { V = i });
            }
            Filter filter;
            long bytes;
            ComponentPoolBase pool;
            if (_atLoad) {
                filter = world.Filter().Inc<LateLoad>().End();
                world.Pool<LateLoad>();
                var op = new FirstAddLateLoad { W = world, E = entities[entities.Length - 1] };
                bytes = Census.Once(ref op, r, world);
                pool = world.Pool<LateLoad>();
            } else {
                filter = world.Filter().Inc<Late>().End();
                var op = new FirstAddLate { W = world, E = entities[entities.Length - 1] };
                bytes = Census.Once(ref op, r, world);
                pool = world.Pool<Late>();
            }
            r.FirstBytes = bytes;
            r.Set("n", N);
            r.Set("pool_sparse_capacity", pool.SparseCapacity);
            r.Set("pool_dense_capacity", pool.DenseCapacity);
            r.Set("component_size", pool.ComponentSize);
            if (!_atLoad) {
                // Arrays a pool created after Reserve(n, n) starts with: sparse (int per entity slot),
                // dense entities (int) and dense data (T) for n components; the rest is the pool object.
                long arrays = (24 + 4L * pool.SparseCapacity) + (24 + 4L * pool.DenseCapacity)
                              + (24 + (long)pool.ComponentSize * pool.DenseCapacity);
                r.Set("reserve_sized_array_bytes", arrays);
                r.Set("other_bytes", bytes - arrays);
            }
            r.Set("filter_count_after", filter.Count);
            if (Compat.GrowthsAfterReserve != null) {
                r.Set("growths_after_reserve", Compat.GrowthsAfterReserve(world));
            }
        }
    }
}

namespace KenseiStand.Alloc.Probe {
    // ==================================================================
    // ReserveEntities + Pool<T>().Reserve for the hot types (2.3.0+)
    // ==================================================================

    /// <summary>
    /// The 2.3.0 way to reserve for the peak, in the EcsBootstrap order: the world reserves its entity slots and the
    /// pools of the hot types (Configure), then the systems create their filters (Init), which take their reserve from
    /// their types. Then the peak frame: N entities created with both hot types (entering every filter) and
    /// destroyed. The docs promise that this allocates nothing, in the first pass too. Frozen, an Exc type of one
    /// filter, is never added and not reserved (Exc types do not matter).
    /// </summary>
    public sealed class ReserveEntitiesHotTypes : Scenario {
        public override string Id => "reserve.entities_hot_types";

        public const int N = 5000;

        private struct Peak : IAllocOp {
            public World W;
            public Entity[] Buf;
            public void Invoke() {
                var b = Buf;
                for (int i = 0; i < b.Length; i++) {
                    b[i] = W.CreateEntity(new Pos { X = i });
                    W.Add(b[i], new Vel { X = 1 });
                }
                for (int i = 0; i < b.Length; i++) {
                    W.DestroyEntity(b[i]);
                }
            }
        }

        public override void Run(Settings s, Result r) {
            var reserveEntities = Compat.ReserveEntities;
            var reservePos = Compat.PoolReserve<Pos>();
            var reserveVel = Compat.PoolReserve<Vel>();
            if (reserveEntities == null || reservePos == null || reserveVel == null || Compat.FilterReservedCount == null) {
                r.Available = false;
                r.Reason = Compat.Missing("World.ReserveEntities, ComponentPoolBase.Reserve and filters reserved from their types (2.3.0)");
                return;
            }
            var world = new World();
            // Configure: the reserve.
            reserveEntities(world, N);
            reservePos(world.Pool<Pos>(), N);
            reserveVel(world.Pool<Vel>(), N);
            world.Pool<Frozen>();
            // Init: the systems' filters, created after the reserve.
            var moving = world.Filter().Inc<Pos>().Inc<Vel>().End();
            var free = world.Filter().Inc<Pos>().Exc<Frozen>().End();
            var velocities = world.Filter().Inc<Vel>().End();
            // The code paths once with one entity, outside the window.
            var e = world.CreateEntity(new Pos());
            world.Add(e, new Vel());
            world.DestroyEntity(e);

            var op = new Peak { W = world, Buf = new Entity[N] };
            r.FirstBytes = Census.Once(ref op, r, world);
            r.Set("n", N);
            r.Set("filter_reserved_moving", Compat.FilterReservedCount(moving));
            r.Set("filter_reserved_free", Compat.FilterReservedCount(free));
            r.Set("filter_reserved_velocities", Compat.FilterReservedCount(velocities));
            if (Compat.GrowthsAfterReserve != null) {
                r.Set("growths_after_reserve", Compat.GrowthsAfterReserve(world));
            }
            Steady(s, r, ref op, 1000);
        }
    }

    // ==================================================================
    // A type nothing reserved, in a world that reserved its hot types (2.3.0+)
    // ==================================================================

    /// <summary>
    /// The same reserve as reserve.entities_hot_types, plus a type nothing reserved (Cold, its pool created at load,
    /// and a filter over it created in Init, which its type does not reserve). N hot entities are alive; inside the
    /// window Cold is added to all of them: its pool and its filter grow on demand (sparse past 256, dense past 64),
    /// which the docs describe, and under KENSEI_DEBUG the growth is not counted in GrowthsAfterReserve (a pool or
    /// filter that nothing reserved is not counted). Measured once.
    /// </summary>
    public sealed class ReserveUnreservedTypeGrows : Scenario {
        public override string Id => "reserve.unreserved_type_grows";

        public const int N = 5000;

        private struct AddCold : IAllocOp {
            public World W;
            public Entity[] E;
            public void Invoke() {
                var e = E;
                for (int i = 0; i < e.Length; i++) {
                    W.Add(e[i], new Cold { V = i });
                }
            }
        }

        public override void Run(Settings s, Result r) {
            var reserveEntities = Compat.ReserveEntities;
            var reservePos = Compat.PoolReserve<Pos>();
            if (reserveEntities == null || reservePos == null || Compat.FilterReservedCount == null) {
                r.Available = false;
                r.Reason = Compat.Missing("World.ReserveEntities, ComponentPoolBase.Reserve and filters reserved from their types (2.3.0)");
                return;
            }
            // The code paths once on a throwaway world: JIT and type loading stay out of the window, as on IL2CPP.
            {
                var scratch = new World();
                scratch.Filter().Inc<Cold>().End();
                var se = scratch.CreateEntity(new Pos());
                scratch.Add(se, new Cold());
                scratch.Destroy();
            }
            var world = new World();
            reserveEntities(world, N);
            reservePos(world.Pool<Pos>(), N);
            world.Pool<Cold>();
            var hot = world.Filter().Inc<Pos>().End();
            var cold = world.Filter().Inc<Cold>().End();
            var entities = new Entity[N];
            for (int i = 0; i < N; i++) {
                entities[i] = world.CreateEntity(new Pos { X = i });
            }
            var pool = world.Pool<Cold>();
            int sparseBefore = pool.SparseCapacity, denseBefore = pool.DenseCapacity, filterBefore = cold.DenseCapacity;

            var op = new AddCold { W = world, E = entities };
            r.FirstBytes = AllocMeter.Once(ref op);
            r.Set("n", N);
            r.Set("hot_filter_reserved", Compat.FilterReservedCount(hot));
            r.Set("cold_filter_reserved", Compat.FilterReservedCount(cold));
            r.Set("cold_pool_sparse", sparseBefore + "->" + pool.SparseCapacity);
            r.Set("cold_pool_dense", denseBefore + "->" + pool.DenseCapacity);
            r.Set("cold_filter_dense", filterBefore + "->" + cold.DenseCapacity);
            r.Set("matched_cold_filter", cold.Count);
            if (Compat.GrowthsAfterReserve != null) {
                r.Set("growths_after_reserve", Compat.GrowthsAfterReserve(world));
            }
        }
    }
}
