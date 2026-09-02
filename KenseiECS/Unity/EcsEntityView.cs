#if UNITY_2018_1_OR_NEWER
using System;
using System.Collections.Generic;
using UnityEngine;

namespace KenseiECS {
    /// <summary>
    /// MonoBehaviour that links a GameObject to an ECS entity.
    /// Either bind it to an existing entity, or author the entity with
    /// EcsComponentProvider components on the same GameObject and call Spawn.
    ///
    /// Usage:
    ///   var view = Instantiate(prefab).GetComponent<EcsEntityView>();
    ///   view.Bind(world, entity);        // link to an existing entity
    ///   var entity = view.Spawn(world);  // or create one from the providers
    /// </summary>
    [DisallowMultipleComponent]
    public class EcsEntityView : MonoBehaviour {
        private static readonly List<EcsComponentProvider> _providerBuffer = new();

        [SerializeField] private bool _destroyEntityWithGameObject;
        [SerializeField] private string _entityName;

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

        /// <summary> Debug name Spawn gives the entity (KENSEI_DEBUG only). Set it before Spawn to name entities from code. </summary>
        public string EntityName { get => _entityName; set => _entityName = value; }

        /// <summary> Bind this view to a world and entity. </summary>
        public void Bind(World world, Entity entity) {
            _world = world;
            _entity = entity;
        }

        /// <summary>
        /// Create an entity from the EcsComponentProvider components on this GameObject
        /// (in component order), bind this view to it and return it.
        /// Under KENSEI_DEBUG the entity gets the serialized Entity Name as its debug name.
        /// </summary>
        public Entity Spawn(World world) {
            GetComponents(_providerBuffer);
            if (_providerBuffer.Count == 0) {
                throw new InvalidOperationException(
                    $"EcsEntityView.Spawn on '{name}': no EcsComponentProvider on the GameObject. An entity needs at least one component — add a provider (a subclass of EcsComponentProvider<T>) next to the view");
            }

            var entity = _providerBuffer[0].Create(world);
            for (int i = 1; i < _providerBuffer.Count; i++) {
                _providerBuffer[i].Apply(world, entity);
            }
            _providerBuffer.Clear();

            if (!string.IsNullOrEmpty(_entityName)) {
                world.SetName(entity, _entityName);
            }

            Bind(world, entity);
            return entity;
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
