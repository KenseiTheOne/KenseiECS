#if UNITY_2018_1_OR_NEWER
using UnityEngine;

namespace KenseiECS {
    /// <summary>
    /// MonoBehaviour that links a GameObject to an ECS entity.
    /// Attach to a prefab and call Bind() after spawning.
    ///
    /// Usage:
    ///   var view = Instantiate(prefab).GetComponent<EcsEntityView>();
    ///   view.Bind(world, entity);
    /// </summary>
    [DisallowMultipleComponent]
    public class EcsEntityView : MonoBehaviour {
        [SerializeField] private bool _destroyEntityWithGameObject;

        private World _world;
        private Entity _entity;

        /// <summary> The World this view is bound to. </summary>
        public World World => _world;

        /// <summary> The Entity this view is bound to. </summary>
        public Entity Entity => _entity;

        /// <summary> Whether this view is bound to a live entity. </summary>
        public bool IsAlive => _world != null && !_world.IsDestroyed && _world.IsAlive(_entity);

        /// <summary> Whether to destroy the bound entity when this GameObject is destroyed. Off by default. </summary>
        public bool DestroyEntityWithGameObject { get => _destroyEntityWithGameObject; set => _destroyEntityWithGameObject = value; }

        /// <summary> Bind this view to a world and entity. </summary>
        public void Bind(World world, Entity entity) {
            _world = world;
            _entity = entity;
        }

        /// <summary> Unbind and optionally destroy the entity. </summary>
        public void Unbind(bool destroyEntity = false) {
            if (destroyEntity && IsAlive) {
                _world.DestroyEntity(_entity);
            }

            _world = null;
            _entity = Entity.Null;
        }

        private void OnDestroy() {
            if (_destroyEntityWithGameObject && IsAlive) {
                _world.DestroyEntity(_entity);
            }
        }
    }
}
#endif
