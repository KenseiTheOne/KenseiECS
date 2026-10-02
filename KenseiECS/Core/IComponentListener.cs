namespace KenseiECS {
    /// <summary>
    /// Typed observer for one component pool. Register via pool.AddListener().
    /// OnAdded runs after the component is stored and filters are updated;
    /// OnRemoved runs before AutoReset, so the component data is still intact.
    /// </summary>
    public interface IComponentListener<T> where T : struct, IComponent {
        /// <summary> Called after the component is added; <paramref name="component"/> refers to the stored value. </summary>
        void OnAdded(int entityIndex, ref T component);
        /// <summary> Called before the component is removed and reset; <paramref name="component"/> refers to the stored value. </summary>
        void OnRemoved(int entityIndex, ref T component);
    }
}
