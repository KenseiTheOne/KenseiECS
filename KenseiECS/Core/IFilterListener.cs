namespace KenseiECS {
    /// <summary>
    /// Observes entities entering and leaving a filter. Register via filter.AddListener().
    /// Callbacks run synchronously inside the structural change that caused them,
    /// so the entity is alive on enter and may already be dying on leave. The one exception
    /// on enter: a component that a listener adds to an entity whose DestroyEntity is running
    /// lets the dying entity (IsAlive false) enter the filters it matches, together with the
    /// components the destroy has not removed yet, until the destroy removes that component too.
    /// A listener that re-adds components to dying entities should check IsAlive in OnEntityAdded.
    /// </summary>
    public interface IFilterListener {
        /// <summary> Called when an entity starts matching <paramref name="filter"/>. </summary>
        void OnEntityAdded(Filter filter, int entityIndex);
        /// <summary> Called when an entity stops matching <paramref name="filter"/>. </summary>
        void OnEntityRemoved(Filter filter, int entityIndex);
    }
}
