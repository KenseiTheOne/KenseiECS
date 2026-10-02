namespace KenseiECS {
    /// <summary>
    /// Observes entities entering and leaving a filter. Register via filter.AddListener().
    /// Callbacks run synchronously inside the structural change that caused them,
    /// so the entity is alive on enter and may already be dying on leave.
    /// </summary>
    public interface IFilterListener {
        /// <summary> Called when an entity starts matching <paramref name="filter"/>. </summary>
        void OnEntityAdded(Filter filter, int entityIndex);
        /// <summary> Called when an entity stops matching <paramref name="filter"/>. </summary>
        void OnEntityRemoved(Filter filter, int entityIndex);
    }
}
