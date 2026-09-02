using System;
using System.Collections.Generic;

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

    internal class CountingInitSystem : IInitSystem {
        public int InitCalls;

        public void Init(World world, SharedData shared) {
            InitCalls++;
        }
    }

    internal class ThrowOnceInitSystem : IInitSystem {
        public bool Throw = true;
        public int InitCalls;

        public void Init(World world, SharedData shared) {
            InitCalls++;
            if (Throw) {
                Throw = false;
                throw new InvalidOperationException("init failed");
            }
        }
    }

    internal class ThrowingRunSystem : IRunSystem {
        public void Run(World world) =>
            throw new InvalidOperationException("run failed");
    }

    internal class CountingRunSystem : IRunSystem {
        public int Runs;

        public void Run(World world) {
            Runs++;
        }
    }

    internal class DamageCountingSystem : IInitSystem, IRunSystem {
        private ComponentPool<Damage> _damage;
        public int SeenLastRun;

        public void Init(World world, SharedData shared) {
            _damage = world.Pool<Damage>();
        }

        public void Run(World world) {
            SeenLastRun = _damage.Count;
        }
    }

    internal class OrderTrackingSystem : IInitSystem, IDestroySystem {
        private readonly string _name;
        private readonly List<string> _log;

        public OrderTrackingSystem(string name, List<string> log) {
            _name = name;
            _log = log;
        }

        public void Init(World world, SharedData shared) {
            _log.Add("init:" + _name);
        }

        public void Destroy(World world) {
            _log.Add("destroy:" + _name);
        }
    }

    internal class SharedCaptureSystem : IInitSystem {
        public SharedData Received;

        public void Init(World world, SharedData shared) {
            Received = shared;
        }
    }

    internal class TestDamageListener {
        public int Hits;
    }
}
