namespace KenseiECS {
    /// <summary>
    /// Non-generic component pool interface.
    /// Allows World to store pools of different types in a single array
    /// and perform common operations (Has, Remove, debug) without knowing T.
    /// </summary>
    public interface IComponentPool {
        /// <summary> Unique type index of the component stored in this pool. </summary>
        int TypeIndex { get; }

        /// <summary> Number of components in the pool. </summary>
        int Count { get; }

        /// <summary> Whether the entity at given index has this component. </summary>
        bool Has(int entityIndex);

        /// <summary> Remove component from entity (O(1) swap-remove). </summary>
        void Remove(int entityIndex);

        /// <summary> Remove all components. Used by World.Clear(). </summary>
        void Clear();

        /// <summary> Copy component from one entity to another. Used by World.CopyEntity(). </summary>
        void CopyTo(int srcEntityIndex, int dstEntityIndex);

        /// <summary>
        /// Get component as object (boxing).
        /// Used ONLY for debug and inspector, not at runtime.
        /// </summary>
        object GetRaw(int entityIndex);

#if KENSEI_DEBUG
        /// <summary>
        /// Set component from object (unboxing).
        /// Used ONLY for inspector editing, not at runtime.
        /// </summary>
        void SetRaw(int entityIndex, object value);
#endif
    }
}
