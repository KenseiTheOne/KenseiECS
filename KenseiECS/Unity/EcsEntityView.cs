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
    /// <code>
    ///   var view = Instantiate(prefab).GetComponent&lt;EcsEntityView&gt;();
    ///   view.Bind(world, entity);        // link to an existing entity
    ///   var entity = view.Spawn(world);  // or create one from the providers
    /// </code>
    /// </summary>
    [DisallowMultipleComponent]
    public class EcsEntityView : MonoBehaviour {
        // Spawn can run again before it returns: a listener on a component a provider adds may
        // Spawn another view. Each nesting depth gets its own provider list, reused by every later
        // Spawn at that depth, so a nested Spawn does not overwrite the outer one's providers and
        // Spawn allocates no list once a depth has been reached. Main thread only, like the
        // GetComponents call that fills it.
        private static List<EcsComponentProvider>[] _providerBuffers = new List<EcsComponentProvider>[4];
        private static int _spawnDepth;

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
        /// A listener the new components trigger may Spawn another view before this call returns.
        /// If such a listener destroys the entity, the remaining providers are skipped (their
        /// components would land in the freed slot), the view is bound to the dead entity and its
        /// handle is returned: check <see cref="IsAlive"/>.
        /// </summary>
        public Entity Spawn(World world) {
            int depth = _spawnDepth;
            if (depth == _providerBuffers.Length) {
                Array.Resize(ref _providerBuffers, depth * 2);
            }
            var providers = _providerBuffers[depth] ??= new List<EcsComponentProvider>();

            GetComponents(providers);
            if (providers.Count == 0) {
                throw new InvalidOperationException(
                    $"EcsEntityView.Spawn on '{name}': no EcsComponentProvider on the GameObject. An entity needs at least one component — add a provider (a subclass of EcsComponentProvider<T>) next to the view");
            }

            Entity entity;
            _spawnDepth = depth + 1;
            try {
                entity = providers[0].Create(world);
                for (int i = 1; i < providers.Count; i++) {
                    // Checked by handle: an entity the listener created may have taken the freed slot.
                    if (!world.IsAlive(entity)) {
                        break;
                    }
                    providers[i].Apply(world, entity);
                }
            } finally {
                // Even when a listener throws: the list holds no providers between calls, and
                // a caller that catches the exception spawns at the right depth again.
                providers.Clear();
                _spawnDepth = depth;
            }

            if (!string.IsNullOrEmpty(_entityName) && world.IsAlive(entity)) {
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
