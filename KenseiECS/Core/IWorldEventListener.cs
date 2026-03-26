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
        void OnEntityCreated(int entityIndex);
        void OnEntityDestroyed(int entityIndex);
        void OnComponentAdded(int entityIndex, int typeIndex);
        void OnComponentRemoved(int entityIndex, int typeIndex);
    }
}
