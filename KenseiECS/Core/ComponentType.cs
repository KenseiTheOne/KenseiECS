using System;

namespace KenseiECS {
    /// <summary>
    /// Assigns a unique integer index to each component type and keeps the
    /// reverse map from index to Type.
    /// Indices are process-wide and stable for the lifetime of the app.
    /// </summary>
    public static class ComponentType {
        private static readonly object _lock = new();
        private static Type[] _types = new Type[64];
        private static int _count;

        /// <summary> Total number of registered component types. </summary>
        public static int Count {
            get {
                lock (_lock) {
                    return _count;
                }
            }
        }

        /// <summary> Resolve a type index back to its component Type. </summary>
        public static Type TypeOf(int typeIndex) {
            lock (_lock) {
                if ((uint)typeIndex >= (uint)_count) {
                    throw new ArgumentOutOfRangeException(nameof(typeIndex), typeIndex,
                        $"No component type is registered under index {typeIndex} ({_count} registered)");
                }
                return _types[typeIndex];
            }
        }

        /// <summary> Short name of the component type registered under the given index. </summary>
        public static string NameOf(int typeIndex) =>
            TypeOf(typeIndex).Name;

        internal static int Register(Type type) {
            lock (_lock) {
                int index = _count++;
                if (index == _types.Length) {
                    Array.Resize(ref _types, _types.Length * 2);
                }
                _types[index] = type;
                return index;
            }
        }
    }

    /// <summary>
    /// Provides the unique type index for component type T.
    /// First access registers the type and assigns an index.
    /// </summary>
    public static class ComponentType<T> where T : struct, IComponent {
        public static readonly int Index = ComponentType.Register(typeof(T));
    }
}
