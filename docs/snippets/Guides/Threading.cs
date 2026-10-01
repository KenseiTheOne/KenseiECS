namespace Docs.Snippets.Threading {
    using System;
    using KenseiECS;

    struct Position : IComponent { public float X, Y; }
    struct Velocity : IComponent { public float X, Y; }

    // #region job
    struct MoveJob : IRangeJob {
        public Filter Filter;
        public ComponentPool<Position> Positions;
        public ComponentPool<Velocity> Velocities;

        public void Execute(int start, int end) {
            var entities = Filter.Entities;
            for (int i = start; i < end; i++) {
                int e = entities[i];
                Positions.Get(e).X += Velocities.Get(e).X;
            }
        }
    }
    // #endregion job

    // #region system
    class ParallelMovementSystem : IInitSystem, IRunSystem, IDestroySystem {
        ParallelRunner _runner;
        Filter _moving;
        ComponentPool<Position> _positions;
        ComponentPool<Velocity> _velocities;

        public void Init(World world, SharedData shared) {
            _runner = new ParallelRunner();          // ProcessorCount - 1 workers; pass 0 for inline execution
            _moving = world.Filter().Inc<Position>().Inc<Velocity>().End();
            _positions = world.Pool<Position>();
            _velocities = world.Pool<Velocity>();
        }

        public void Run(World world) {
            _runner.Run(new MoveJob { Filter = _moving, Positions = _positions, Velocities = _velocities }, _moving.Count, chunkSize: 512);
        }

        public void Destroy(World world) {
            _runner.Dispose();                       // stops the worker threads
        }
    }
    // #endregion system

    // #region group-job
    struct MoveGroupJob : IRangeJob {
        public Group<Position, Velocity> Group;

        public void Execute(int start, int end) {
            var pos = Group.Data1;
            var vel = Group.Data2;
            for (int i = start; i < end; i++) {
                pos[i].X += vel[i].X;
                pos[i].Y += vel[i].Y;
            }
        }
    }
    // #endregion group-job

    class GroupRun {
        void Run(ParallelRunner runner, Group<Position, Velocity> group) {
            // #region group-run
            runner.Run(new MoveGroupJob { Group = group }, group.Count);   // chunkSize defaults to 1024
            // #endregion group-run
        }
    }
}
