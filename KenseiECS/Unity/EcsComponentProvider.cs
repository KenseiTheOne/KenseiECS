#if UNITY_2018_1_OR_NEWER
using UnityEngine;

namespace KenseiECS {
    /// <summary>
    /// Non-generic base that EcsEntityView.Spawn collects. Derive from EcsComponentProvider<T>, not from this.
    /// </summary>
    public abstract class EcsComponentProvider : MonoBehaviour {
        internal abstract Entity Create(World world);

        internal abstract void Apply(World world, Entity entity);
    }

    /// <summary>
    /// Authoring component holding one ECS component value edited in the inspector.
    /// Put providers next to an EcsEntityView and call EcsEntityView.Spawn to create
    /// the entity from them. T must be [Serializable] to show up in the inspector.
    ///
    /// Unity serializes the field of a generic base only through a non-generic
    /// subclass, so declare one per component type:
    ///   public sealed class HealthProvider : EcsComponentProvider<Health> { }
    /// </summary>
    public abstract class EcsComponentProvider<T> : EcsComponentProvider where T : struct, IComponent {
        [SerializeField] private T _value;

        /// <summary> The authored component value. </summary>
        public ref T Value => ref _value;

        internal sealed override Entity Create(World world) =>
            world.CreateEntity(_value);

        internal sealed override void Apply(World world, Entity entity) {
            world.Add(entity, _value);
        }
    }
}
#endif
