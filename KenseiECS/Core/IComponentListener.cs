namespace KenseiECS {
    /// <summary>
    /// Typed observer for one component pool. Register via pool.AddListener().
    /// OnAdded runs after the component is stored and filters are updated;
    /// OnRemoved runs before AutoReset, so the component data is still intact.
    /// </summary>
    public interface IComponentListener<T> where T : struct, IComponent {
        void OnAdded(int entityIndex, ref T component);
        void OnRemoved(int entityIndex, ref T component);
    }
}
