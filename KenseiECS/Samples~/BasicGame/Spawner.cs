using UnityEngine;

namespace KenseiECS.Samples.BasicGame {
    /// <summary>
    /// Instantiates the prefab Count times and turns every instance into an entity
    /// with EcsEntityView.Spawn. The prefab needs an EcsEntityView, a PositionProvider
    /// and a VelocityProvider; the spawner randomizes the position and the direction.
    /// </summary>
    public sealed class Spawner : MonoBehaviour {
        [SerializeField] private EcsEntityView _prefab;
        [SerializeField] private int _count = 20;

        private void Start() {
#if UNITY_2023_1_OR_NEWER
            var bootstrap = FindFirstObjectByType<GameBootstrap>();
#else
            var bootstrap = FindObjectOfType<GameBootstrap>();
#endif
            var world = bootstrap.World;
            var halfSize = bootstrap.Shared.Get<ArenaConfig>().HalfSize;

            for (int i = 0; i < _count; i++) {
                var view = Instantiate(_prefab, transform);
                var entity = view.Spawn(world);

                world.Get<Position>(entity).Value = new Vector2(
                    Random.Range(-halfSize.x, halfSize.x),
                    Random.Range(-halfSize.y, halfSize.y));

                ref var velocity = ref world.Get<Velocity>(entity);
                velocity.Value = Quaternion.Euler(0f, 0f, Random.Range(0f, 360f)) * velocity.Value;

                world.Add(entity, new TransformRef { Value = view.transform });
            }
        }
    }
}
