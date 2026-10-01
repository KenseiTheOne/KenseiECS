using System;
using System.Collections.Generic;
using KenseiECS;

namespace Docs.Snippets.Components {
    // #region declare
    struct Health : IComponent { public float Value; }
    // #endregion declare

    static class Access {
        static void Example(World world, Entity entity) {
            // #region access
            world.Add(entity, new Health { Value = 100 });   // throws if already present
            ref var hp = ref world.Get<Health>(entity);      // by ref, no copy
            bool has = world.Has<Health>(entity);            // does not create the pool
            world.Remove<Health>(entity);                    // no-op if absent
            // #endregion access
        }

        static void Pool(World world, Entity entity) {
            // #region pool
            // Pool access (cache in Init)
            var pool = world.Pool<Health>();
            ref var hp = ref pool.Get(entity.Index);
            // #endregion pool
        }

        static void StaleRef(World world, Entity entity, Entity other) {
            // #region stale-ref
            ref var hp = ref world.Get<Health>(entity);
            world.Add(other, new Health());   // may reallocate the Health pool
            hp.Value = 10;                    // may write into the old array — re-Get instead

            hp = ref world.Get<Health>(entity);   // re-acquired after the structural change
            hp.Value = 10;                        // safe
            // #endregion stale-ref
        }
    }

    class AutoResetExample {
        // #region auto-reset
        struct Inventory : IComponent, IAutoReset<Inventory> {
            public List<int> Items;
            public void AutoReset(ref Inventory c) {
                c.Items?.Clear();
                c.Items = null;
            }
        }
        // #endregion auto-reset
    }

    class AutoCopyExample {
        // #region auto-copy
        struct Inventory : IComponent, IAutoCopy<Inventory> {
            public List<int> Items;
            public void AutoCopy(ref Inventory c) {
                c.Items = c.Items != null ? new List<int>(c.Items) : null;
            }
        }
        // #endregion auto-copy
    }

    static class Types {
        static void Example(World world, Entity entity) {
            // #region component-types
            var types = new List<int>();
            world.GetComponentTypes(entity, types);
            foreach (int typeIndex in types) {
                Console.WriteLine(ComponentType.NameOf(typeIndex));
                var pool = world.GetPool(typeIndex);   // ComponentPoolBase
            }

            int healthIndex = ComponentType<Health>.Index;
            Type healthType = ComponentType.TypeOf(healthIndex);   // typeof(Health)
            // #endregion component-types
        }
    }
}
