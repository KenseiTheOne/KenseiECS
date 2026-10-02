namespace Docs.Snippets.Home {
    using KenseiECS;

    // #region components
    struct Position : IComponent { public float X, Y; }
    struct Velocity : IComponent { public float X, Y; }
    struct Frozen : IComponent { }
    // #endregion components

    // #region system
    partial class MoveSystem : IRunSystem {
        [Inc(typeof(Position), typeof(Velocity))] [Exc(typeof(Frozen))]
        Filter _moving;
        [Pool] ComponentPool<Position> _positions;
        [Pool] ComponentPool<Velocity> _velocities;

        public void Run(World world) {
            foreach (int e in _moving) {
                ref var pos = ref _positions.Get(e);
                ref var vel = ref _velocities.Get(e);
                pos.X += vel.X;
                pos.Y += vel.Y;
            }
        }
    }
    // #endregion system

    class Usage {
        void Run() {
            // #region run
            var world = new World();
            var systems = new SystemsRunner(world)
                .Add(new MoveSystem());
            systems.Init();               // generated Init fills the fields

            var entity = world.CreateEntity(new Position());
            world.Add(entity, new Velocity { X = 1, Y = 2 });

            systems.Run();                // once per frame
            // #endregion run
        }
    }
}
