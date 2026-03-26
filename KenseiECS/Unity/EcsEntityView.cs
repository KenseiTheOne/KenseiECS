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
    public class EcsEntityView : MonoBehaviour {
        [HideInInspector] public World World;
        [HideInInspector] public Entity Entity;

        /// <summary> Whether this view is bound to a live entity. </summary>
        public bool IsAlive => World != null && World.IsAlive(Entity);

        /// <summary> Bind this view to a world and entity. </summary>
        public void Bind(World world, Entity entity) {
            World = world;
            Entity = entity;
        }

        /// <summary> Unbind and optionally destroy the entity. </summary>
        public void Unbind(bool destroyEntity = false) {
            if (destroyEntity && IsAlive) {
                World.DestroyEntity(Entity);
            }

            World = null;
            Entity = Entity.Null;
        }
    }
}
#endif
