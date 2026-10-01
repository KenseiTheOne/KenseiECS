namespace Docs.Snippets.Groups {
    using KenseiECS;

    struct Position : IComponent { public float X, Y; }
    struct Velocity : IComponent { public float X, Y; }

    // #region moving
    class MovementSystem : IInitSystem, IRunSystem {
        Group<Position, Velocity> _moving;

        public void Init(World world, SharedData shared) {
            _moving = world.Group<Position, Velocity>();
        }

        public void Run(World world) {
            var pos = _moving.Data1;
            var vel = _moving.Data2;
            for (int i = 0; i < pos.Length; i++) {
                pos[i].X += vel[i].X;
                pos[i].Y += vel[i].Y;
            }
        }
    }
    // #endregion moving

    class ReverseExample {
        Group<Position, Velocity> _moving;

        void Run(World world) {
            // #region reverse
            var entities = _moving.Entities;
            for (int i = _moving.Count - 1; i >= 0; i--) {
                if (_moving.Data1[i].Y < -100f) {
                    world.DestroyEntity(world.GetEntity(entities[i]));
                }
            }
            // #endregion reverse
        }
    }
}
