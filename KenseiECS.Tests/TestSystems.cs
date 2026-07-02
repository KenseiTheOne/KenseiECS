namespace KenseiECS.Tests {
    internal class TestMovementSystem : IInitSystem, IRunSystem {
        private Filter _filter;
        private ComponentPool<Position> _positions;
        private ComponentPool<Velocity> _velocities;

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

    internal class TestDestroySystem : IDestroySystem {
        public bool Destroyed;

        public void Destroy(World world) {
            Destroyed = true;
        }
    }

    internal class TestService {
        public int Value;
    }

    internal class TestSharedDataSystem : IInitSystem {
        public int ReceivedValue;

        public void Init(World world, SharedData shared) {
            var service = shared.Get<TestService>();
            ReceivedValue = service.Value;
        }
    }
}
