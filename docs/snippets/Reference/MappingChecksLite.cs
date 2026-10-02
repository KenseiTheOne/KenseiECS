using System;
using Leopotam.EcsLite;

// Not shown on the site. Compiles every LeoEcsLite core API named in the side-by-side
// tables of docs/migration-from-leoecslite.md against Leopotam.EcsLite 1.0.1. Rows that
// need an optional define (LEOECSLITE_WORLD_EVENTS, LEOECSLITE_FILTER_EVENTS) or an
// extension package are not checked here.

namespace Docs.Snippets.Reference.Migration.MappingChecksLite {
    public struct Position { public float X, Y; }
    public struct Velocity { public float X, Y; }
    public struct Health { public float Value; }
    public struct Frozen { }

    public struct Resettable : IEcsAutoReset<Resettable> {
        public void AutoReset(ref Resettable c) { }
    }

    public struct Copyable : IEcsAutoCopy<Copyable> {
        public void AutoCopy(ref Copyable src, ref Copyable dst) { dst = src; }
    }

    sealed class S : IEcsPreInitSystem, IEcsInitSystem, IEcsRunSystem, IEcsDestroySystem, IEcsPostDestroySystem {
        public void PreInit(IEcsSystems systems) { }
        public void Init(IEcsSystems systems) { }
        public void Run(IEcsSystems systems) { }
        public void Destroy(IEcsSystems systems) { }
        public void PostDestroy(IEcsSystems systems) { }
    }

    sealed class GameShared { }

    static class Rows {
        static void WorldAndEntities() {
            var world = new EcsWorld();
            var config = new EcsWorld.Config { RecycledEntities = 512, PoolRecycledSize = 512 };
            var configured = new EcsWorld(in config);
            int e = world.NewEntity();
            world.GetPool<Position>().Add(e);
            int dst = world.NewEntity();
            world.CopyEntity(e, dst);
            EcsPackedEntity packed = world.PackEntity(e);
            bool alive = packed.Unpack(world, out int unpacked);
            EcsPackedEntityWithWorld withWorld = world.PackEntityWithWorld(e);
            short gen = world.GetEntityGen(e);
            int count = world.GetComponentsCount(e);
            Type[] types = null;
            world.GetComponentTypes(e, ref types);
            int entities = world.GetEntitiesCount();
            world.DelEntity(e);
            world.Destroy();
            configured.Destroy();
        }

        static void PoolsAndComponents(EcsWorld world, int e, int dst) {
            EcsPool<Health> pool = world.GetPool<Health>();
            ref var hp = ref pool.Add(e);
            hp.Value = 100;
            ref var got = ref pool.Get(e);
            bool has = pool.Has(e);
            pool.Copy(e, dst);
            pool.Del(e);
            Health[] dense = pool.GetRawDenseItems();
            int[] sparse = pool.GetRawSparseItems();
            int id = pool.GetId();
            IEcsPool byId = world.GetPoolById(id);
            IEcsPool byType = world.GetPoolByType(typeof(Health));
        }

        static void Filters(EcsWorld world) {
            EcsFilter filter = world.Filter<Position>().Inc<Velocity>().Exc<Frozen>().End();
            foreach (int e in filter) { }
            int count = filter.GetEntitiesCount();
            int[] raw = filter.GetRawEntities();
            bool member = filter.GetSparseIndex()[0] > 0;
        }

        static void Systems(EcsWorld world, EcsWorld events) {
            IEcsSystems systems = new EcsSystems(world, new GameShared());
            systems.AddWorld(events, "events");
            systems.Add(new S());
            systems.Init();
            systems.Run();
            EcsWorld main = systems.GetWorld();
            EcsWorld named = systems.GetWorld("events");
            GameShared shared = systems.GetShared<GameShared>();
            var all = systems.GetAllSystems();
            systems.Destroy();
        }
    }
}
