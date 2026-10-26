using System;
using KenseiECS;

namespace Stand.Contracts {
    /// <summary>
    /// Short printable values for call results and probes. Shared with the repro programs, so a repro
    /// prints exactly what the stand recorded and the repro check can compare the two strings.
    /// </summary>
    public static class Fmt {
        public static string Value(object v) {
            if (v == null) {
                return "null";
            }
            if (v is string s) {
                return s.Length > 120 ? s.Substring(0, 117) + "..." : s;
            }
            if (v is bool b) {
                return b ? "True" : "False";
            }
            if (v is int || v is long || v is double || v is Entity) {
                return v.ToString();
            }
            var t = v.GetType();
            if (t.IsArray) {
                return Name(t.GetElementType()) + "[" + ((Array)v).Length + "]";
            }
            if (v is Filter) {
                return "Filter";
            }
            if (t.Namespace != null && (t.Namespace.StartsWith("KenseiECS", StringComparison.Ordinal) || t.Namespace.StartsWith("Stand.", StringComparison.Ordinal))) {
                return Name(t);
            }
            string text = v.ToString();
            return text.Length > 120 ? text.Substring(0, 117) + "..." : text;
        }

        public static string Name(Type t) {
            string name = t.Name;
            int tick = name.IndexOf('`');
            if (tick >= 0) {
                name = name.Substring(0, tick);
            }
            if (t.IsGenericType) {
                var args = t.GetGenericArguments();
                var parts = new string[args.Length];
                for (int i = 0; i < args.Length; i++) {
                    parts[i] = Name(args[i]);
                }
                name += "<" + string.Join(", ", parts) + ">";
            }
            return name;
        }

        /// <summary> "ok:&lt;value&gt;" or "ex:&lt;exception type&gt;": the signature both the stand and a repro print. </summary>
        public static string Try(Func<object> f) {
            try {
                return "ok:" + Value(f());
            } catch (Exception e) {
                return "ex:" + e.GetType().Name;
            }
        }
    }
}
