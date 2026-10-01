namespace Docs.Snippets.QuickStart {
    // #region quickstart
    using System;
    using KenseiECS;

    // #region quickstart-system
    struct Position : IComponent { public float X, Y; }
    struct Velocity : IComponent { public float X, Y; }

    class MovementSystem : IInitSystem, IRunSystem {
        Filter _filter;
        ComponentPool<Position> _positions;
        ComponentPool<Velocity> _velocities;

        public void Init(World world, SharedData shared) {
            _filter = world.Filter().Inc<Position>().Inc<Velocity>().End();
            _positions = world.Pool<Position>();
            _velocities = world.Pool<Velocity>();
        }

        public void Run(World world) {
            foreach (int e in _filter) {
                ref var pos = ref _positions.Get(e);
                ref var vel = ref _velocities.Get(e);
                pos.X += vel.X;
                pos.Y += vel.Y;
            }
        }
    }
    // #endregion quickstart-system

    static class Program {
        static void Main() {
            var world = new World();
            var systems = new SystemsRunner(world)
                .Add(new MovementSystem());

            systems.Init();

            var entity = world.CreateEntity(new Position());
            world.Add(entity, new Velocity { X = 1, Y = 2 });
            systems.Run();

            ref var pos = ref world.Get<Position>(entity);
            Console.WriteLine($"Position: ({pos.X}, {pos.Y})");   // Position: (1, 2)

            systems.Destroy();
        }
    }
    // #endregion quickstart
}
