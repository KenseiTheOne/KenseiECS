#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;

namespace KenseiECS.Editor {
    /// <summary>
    /// Type names for the editor windows, with generic arguments spelled out: Type.Name gives
    /// "EventBuffer`1" for every EventBuffer&lt;T&gt;, this gives "EventBuffer&lt;Hit&gt;". Cached per type.
    /// </summary>
    internal static class EditorTypeNames {
        private static readonly Dictionary<Type, string> _names = new();

        internal static string Of(Type type) {
            if (_names.TryGetValue(type, out string cached)) {
                return cached;
            }

            string built = Build(type);
            _names[type] = built;
            return built;
        }

        /// <summary> Name of a registered component type, by its ComponentType index. </summary>
        internal static string OfComponent(int typeIndex) {
            return Of(ComponentType.TypeOf(typeIndex));
        }

        /// <summary> Filter.ToString with these names: <c>Filter Inc&lt;Health, Listeners&lt;IHealthView&gt;&gt;</c>. </summary>
        internal static string OfFilter(Filter filter) {
            var sb = new StringBuilder("Filter");
            AppendComponents(sb, " Inc<", filter.IncludedTypes);
            AppendComponents(sb, " Exc<", filter.ExcludedTypes);
            AppendComponents(sb, " Any<", filter.AnyTypes);
            return sb.ToString();
        }

        private static void AppendComponents(StringBuilder sb, string prefix, ReadOnlySpan<int> typeIndices) {
            if (typeIndices.Length == 0) {
                return;
            }
            sb.Append(prefix);
            for (int i = 0; i < typeIndices.Length; i++) {
                if (i > 0) {
                    sb.Append(", ");
                }
                sb.Append(OfComponent(typeIndices[i]));
            }
            sb.Append('>');
        }

        private static string Build(Type type) {
            if (!type.IsGenericType) {
                return type.Name;
            }

            var sb = new StringBuilder();
            string raw = type.Name;
            int tick = raw.IndexOf('`');
            sb.Append(tick < 0 ? raw : raw.Substring(0, tick));
            sb.Append('<');

            var args = type.GetGenericArguments();
            for (int i = 0; i < args.Length; i++) {
                if (i > 0) {
                    sb.Append(", ");
                }
                sb.Append(Build(args[i]));
            }

            sb.Append('>');
            return sb.ToString();
        }
    }
}
#endif
