namespace KenseiECS {
    /// <summary>
    /// Listener for World lifecycle events.
    /// Register via world.AddEventListener().
    ///
    /// Usage:
    ///   class MyListener : IWorldEventListener {
    ///       public void OnEntityCreated(int entityIndex) { }
    ///       public void OnEntityDestroyed(int entityIndex) { }
    ///       public void OnComponentAdded(int entityIndex, int typeIndex) { }
    ///       public void OnComponentRemoved(int entityIndex, int typeIndex) { }
    ///   }
    ///   world.AddEventListener(new MyListener());
    /// </summary>
    public interface IWorldEventListener {
        /// <summary> Called after an entity is created and its initial components are added. </summary>
        void OnEntityCreated(int entityIndex);
        /// <summary> Called when an entity is destroyed: it is already marked dead, but its components are not yet removed. </summary>
        void OnEntityDestroyed(int entityIndex);
        /// <summary> Called after a component is added and filters are updated. <paramref name="typeIndex"/> is the component type index. </summary>
        void OnComponentAdded(int entityIndex, int typeIndex);
        /// <summary>
        /// Called after a component is removed and filters are updated.
        /// If it was the last component of a live entity, the entity is destroyed after the dispatch.
        /// </summary>
        void OnComponentRemoved(int entityIndex, int typeIndex);
    }
}
