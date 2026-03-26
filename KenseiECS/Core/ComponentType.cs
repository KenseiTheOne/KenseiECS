using System.Threading;

namespace KenseiECS {
    /// <summary>
    /// Assigns a unique integer index to each component type.
    /// Used by filters and World to map types without Dictionary<Type> lookups.
    /// Thread-safe counter — indices are stable for the lifetime of the app.
    /// </summary>
    public static class ComponentType {
        private static int _nextIndex;

        internal static int NextIndex() => Interlocked.Increment(ref _nextIndex) - 1;

        /// <summary> Total number of registered component types. </summary>
        public static int Count => _nextIndex;
    }

    /// <summary>
    /// Provides the unique type index for component type T.
    /// First access registers the type and assigns an index.
    /// </summary>
    public static class ComponentType<T> where T : struct, IComponent {
        public static readonly int Index = ComponentType.NextIndex();
    }
}
