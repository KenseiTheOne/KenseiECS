using System;
using System.Collections.Generic;
using KenseiECS;

// Not shown on the site. Compiles every API named in the side-by-side tables of
// docs/migration-from-leoecslite.md (KenseiECS column), so a rename breaks the build
// instead of leaving a stale row. The LeoEcsLite column is in MappingChecksLite.cs.

namespace Docs.Snippets.Reference.Migration.MappingChecks {
    public struct Position : IComponent { public float X, Y; }
    public struct Velocity : IComponent { public float X, Y; }
    public struct Health : IComponent { public float Value; }
    public struct Shield : IComponent { }
    public struct Frozen : IComponent { }
    public struct Hit : IComponent { }

    public struct Inventory : IComponent, IAutoReset<Inventory>, IAutoCopy<Inventory> {
        public List<int> Items;
        public void AutoReset(ref Inventory c) { c.Items = null; }
        public void AutoCopy(ref Inventory c) { c.Items = c.Items == null ? null : new List<int>(c.Items); }
    }

    sealed class HealthListener : IComponentListener<Health> {
        public void OnAdded(int entityIndex, ref Health component) { }
        public void OnRemoved(int entityIndex, ref Health component) { }
    }

    sealed class FilterListener : IFilterListener {
        public void OnEntityAdded(Filter filter, int entityIndex) { }
        public void OnEntityRemoved(Filter filter, int entityIndex) { }
    }

    sealed class WorldListener : IWorldEventListener {
        public void OnEntityCreated(int entityIndex) { }
        public void OnEntityDestroyed(int entityIndex) { }
        public void OnComponentAdded(int entityIndex, int typeIndex) { }
        public void OnComponentRemoved(int entityIndex, int typeIndex) { }
    }

    interface IDamageListener { void OnDamage(float value); }
    sealed class TimeService { }
    sealed class SpawnConfig { }

    sealed class S : IInitSystem, IRunSystem, IDestroySystem {
        public void Init(World world, SharedData shared) { }
        public void Run(World world) { }
        public void Destroy(World world) { }
    }

    static class Rows {
        static void WorldAndEntities() {
            var world = new World();
            var configured = new World(new WorldConfig { InitialPoolSparseCapacity = 64 });
            Entity e = world.CreateEntity(new Position());
            Entity copy = world.CopyEntity(e);
            bool alive = world.IsAlive(e);
            Entity h = world.GetEntity(e.Index);
            int generation = e.Generation;
            int count = world.GetComponentCount(e);
            var types = new List<int>();
            world.GetComponentTypes(e, types);
            Type type = ComponentType.TypeOf(types[0]);
            int entities = world.EntityCount;
            bool found = world.TryGetEntity(e.Index, out Entity entity);
            world.DestroyEntity(e);
            world.Clear();
            world.Destroy();
            configured.Destroy();
        }

        static void PoolsAndComponents(World world, Entity e) {
            ComponentPool<Health> pool = world.Pool<Health>();
            ref var hp = ref world.Add(e, new Health { Value = 100 });
            ref var hp2 = ref world.Get<Health>(e);
            ref var hp3 = ref pool.Get(e.Index);
            bool has = world.Has<Health>(e) && pool.Has(e.Index);
            world.Remove<Health>(e);
            pool.Add(e.Index, new Health());
            pool.Remove(e.Index);
            Health[] data = pool.RawData;
            int[] owners = pool.RawEntities;
            int count = pool.Count;
            int typeIndex = ComponentType<Health>.Index;
            int same = pool.TypeIndex;
            ComponentPoolBase byIndex = world.GetPool(typeIndex);
            pool.AddListener(new HealthListener());
            pool.TrackChanges();
            int version = world.ChangeVersion;
            pool.Add(e.Index, new Health());
            pool.Modify(e.Index).Value = 1;
            pool.MarkChanged(e.Index);
            bool changed = pool.ChangedSince(e.Index, version);
        }

        static void Filters(World world) {
            Filter built = world.Filter().Inc<Position>().Inc<Velocity>().Exc<Frozen>().End();
            Filter spec = world.Filter<Inc<Position, Velocity>, Exc<Frozen>>();
            Filter any = world.Filter().Any<Health>().Any<Shield>().End();
            Filter anySpec = world.Filter<Any<Health, Shield>>();
            foreach (int e in built) { }
            int count = built.Count;
            bool empty = built.IsEmpty;
            ReadOnlySpan<int> raw = built.Entities;
            bool contains = built.Contains(0);
            if (!built.IsEmpty) {
                int first = built.First();
                int single = built.Single();
            }
            bool one = built.TryGetFirst(out int firstEntity);
            built.AddListener(new FilterListener());
            var group = world.Group<Position, Velocity>();
            Span<Position> d1 = group.Data1;
            Span<Velocity> d2 = group.Data2;
            ReadOnlySpan<int> members = group.Entities;
        }

        static void Systems(World world, SharedData shared, SystemsRunner fixedRunner) {
            var systems = new SystemsRunner(world, shared);
            systems.Add(new S()).Add(new S(), "name").Add(fixedRunner, "fixed");
            systems.OneFrame<Hit>().DelHere<Frozen>();
            systems.Init();
            systems.Run();
            systems.GetRunner("fixed").Run();
            systems.SetActive("name", false);
            bool active = systems.IsActive("name");
            int n = systems.SystemCount;
            SystemsRunner.SystemInfo info = systems.GetSystemInfo(0);
            systems.SetActive(0, true);
            systems.Destroy();
            systems.Warmup();

            shared.Add(new TimeService());
            shared.Add(new SpawnConfig(), "enemies");
            TimeService time = shared.Get<TimeService>();
            SpawnConfig enemies = shared.Get<SpawnConfig>("enemies");
            bool hasTime = shared.TryGet(out TimeService maybe);
        }

        static void Events(World world, Entity e, IDamageListener listener) {
            var l = new WorldListener();
            world.AddEventListener(l);
            world.RemoveEventListener(l);
            int filters = world.FilterCount;
            Filter f = world.GetFilter(0);
            bool destroyed = world.IsDestroyed;

            var buffer = new CommandBuffer();
            PendingEntity pending = buffer.CreateEntity(new Position());
            buffer.Add(pending, new Velocity());
            buffer.Playback(world);
            ref var singleton = ref world.GetSingleton<Position>();
            Entity singletonEntity = world.GetSingletonEntity<Position>();
            bool hasSingleton = world.HasSingleton<Position>();
            world.AddEvent(e, 5);
            world.Subscribe<IDamageListener>(e, listener);
            world.Unsubscribe<IDamageListener>(e, listener);
            bool hasListeners = world.HasListeners<IDamageListener>(e);
        }
    }
}
