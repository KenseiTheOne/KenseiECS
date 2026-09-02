using UnityEngine;

namespace KenseiECS.Samples.BasicGame {
    /// <summary> Update: integrates velocity into position. </summary>
    public sealed class MovementSystem : IInitSystem, IRunSystem {
        private Filter _filter;
        private ComponentPool<Position> _positions;
        private ComponentPool<Velocity> _velocities;

        public void Init(World world, SharedData shared) {
            _filter = world.Filter().Inc<Position>().Inc<Velocity>().End();
            _positions = world.Pool<Position>();
            _velocities = world.Pool<Velocity>();
        }

        public void Run(World world) {
            float dt = Time.deltaTime;
            foreach (int e in _filter) {
                _positions.Get(e).Value += _velocities.Get(e).Value * dt;
            }
        }
    }

    /// <summary> FixedUpdate: reflects entities off the arena walls and raises BounceEvent on them. </summary>
    public sealed class BounceSystem : IInitSystem, IRunSystem {
        private Filter _filter;
        private ComponentPool<Position> _positions;
        private ComponentPool<Velocity> _velocities;
        private Vector2 _halfSize;

        public void Init(World world, SharedData shared) {
            _halfSize = shared.Get<ArenaConfig>().HalfSize;
            _filter = world.Filter().Inc<Position>().Inc<Velocity>().End();
            _positions = world.Pool<Position>();
            _velocities = world.Pool<Velocity>();
        }

        public void Run(World world) {
            foreach (int e in _filter) {
                ref var position = ref _positions.Get(e).Value;
                ref var velocity = ref _velocities.Get(e).Value;
                var normal = Vector2.zero;

                if (Mathf.Abs(position.x) > _halfSize.x) {
                    position.x = Mathf.Sign(position.x) * _halfSize.x;
                    velocity.x = -velocity.x;
                    normal.x = -Mathf.Sign(position.x);
                }

                if (Mathf.Abs(position.y) > _halfSize.y) {
                    position.y = Mathf.Sign(position.y) * _halfSize.y;
                    velocity.y = -velocity.y;
                    normal.y = -Mathf.Sign(position.y);
                }

                if (normal != Vector2.zero) {
                    world.Add(world.GetEntity(e), new BounceEvent { Normal = normal });
                }
            }
        }
    }

    /// <summary> Update: logs every BounceEvent raised since the last frame. Untick it in KenseiECS → Systems to silence it. </summary>
    public sealed class BounceLogSystem : IInitSystem, IRunSystem {
        private Filter _filter;
        private ComponentPool<BounceEvent> _events;

        public void Init(World world, SharedData shared) {
            _filter = world.Filter().Inc<BounceEvent>().End();
            _events = world.Pool<BounceEvent>();
        }

        public void Run(World world) {
            foreach (int e in _filter) {
                var entity = world.GetEntity(e);
                Debug.Log($"{world.GetName(entity) ?? entity.ToString()} bounced, wall normal {_events.Get(e).Normal}");
            }
        }
    }

    /// <summary> LateUpdate: copies Position into the linked Transform. </summary>
    public sealed class SyncTransformSystem : IInitSystem, IRunSystem {
        private Filter _filter;
        private ComponentPool<Position> _positions;
        private ComponentPool<TransformRef> _transforms;

        public void Init(World world, SharedData shared) {
            _filter = world.Filter().Inc<Position>().Inc<TransformRef>().End();
            _positions = world.Pool<Position>();
            _transforms = world.Pool<TransformRef>();
        }

        public void Run(World world) {
            foreach (int e in _filter) {
                _transforms.Get(e).Value.position = _positions.Get(e).Value;
            }
        }
    }
}
