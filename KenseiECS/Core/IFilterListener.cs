namespace KenseiECS {
    /// <summary>
    /// Observes entities entering and leaving a filter. Register via filter.AddListener().
    /// Callbacks run synchronously inside the structural change that caused them,
    /// so the entity is alive on enter and may already be dying on leave.
    /// </summary>
    public interface IFilterListener {
        void OnEntityAdded(Filter filter, int entityIndex);
        void OnEntityRemoved(Filter filter, int entityIndex);
    }
}
