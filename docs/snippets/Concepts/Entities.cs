using KenseiECS;

namespace Docs.Snippets.Entities {
    struct Position : IComponent { public float X, Y; }
    struct Velocity : IComponent { public float X, Y; }
    struct Health : IComponent { public float Value; }
    struct Target : IComponent { public Entity Enemy; }

    static class Lifecycle {
        static void Example(World world, Entity source) {
            // #region create
            // Always create with at least one component
            var entity = world.CreateEntity(new Position { X = 1 });

            // Add more components manually
            world.Add(entity, new Velocity());
            world.Add(entity, new Health { Value = 100 });

            bool alive = world.IsAlive(entity);
            world.DestroyEntity(entity);

            // Copy entity with all components
            var copy = world.CopyEntity(source);
            // #endregion create
        }
    }

    static class Handles {
        static void Example(World world, Filter enemies, Entity hunter) {
            // #region handles
            ref var target = ref world.Get<Target>(hunter);
            foreach (int e in enemies) {
                var handle = world.GetEntity(e);     // convert inside the loop
                target.Enemy = handle;               // store the handle, not e
            }

            // Frames later: the handle tells whether its entity still exists
            if (world.IsAlive(target.Enemy)) {
                ref var enemyHealth = ref world.Get<Health>(target.Enemy);
            }
            // #endregion handles
        }

        static void TryGet(World world, int index) {
            // #region try-get
            if (world.TryGetEntity(index, out Entity entity)) {
                // the slot holds an alive entity; entity is its handle
            }
            // #endregion try-get
        }
    }

    static class Names {
        static void Example(World world, Entity player) {
            // #region names
            world.SetName(player, "Player");       // compiles out without KENSEI_DEBUG
            string name = world.GetName(player);   // null unless named
            // #endregion names
        }
    }
}
