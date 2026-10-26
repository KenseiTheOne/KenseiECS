using System;
using System.Collections.Generic;
using System.Reflection;

namespace StandShared {
    /// <summary> Model id of a class in a stand program; the oracle maps runtime types back to the model with it. </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
    public sealed class ClsIdAttribute : Attribute {
        public string Id { get; }

        public ClsIdAttribute(string id) {
            Id = id;
        }
    }

    /// <summary> One call recorded by <see cref="Probe.Hit"/>: which method ran and which injected fields were still null then. </summary>
    public sealed class ProbeEvent {
        public string Tag;
        public string[] NullFields;

        public override string ToString() {
            return NullFields.Length == 0 ? Tag : Tag + " (null: " + string.Join(", ", NullFields) + ")";
        }
    }

    /// <summary>
    /// Instrumentation of the stand programs. Every hand-written Init, every OnInit and every hook
    /// calls <see cref="Hit"/>; the event keeps the order of the calls and a snapshot of the
    /// injected fields (fields with a KenseiECS injection attribute) of the whole object at that
    /// moment. The log is per thread, so programs can run in parallel.
    /// </summary>
    public static class Probe {
        [ThreadStatic] private static List<ProbeEvent> _events;

        public static List<ProbeEvent> Events => _events ??= new List<ProbeEvent>();

        public static void Reset() {
            Events.Clear();
        }

        public static void Hit(object self, string tag) {
            Events.Add(new ProbeEvent { Tag = tag, NullFields = NullInjectedFields(self) });
        }

        /// <summary> "ClsId.field" of every injected instance field of <paramref name="self"/> that is null. </summary>
        public static string[] NullInjectedFields(object self) {
            var result = new List<string>();
            for (var type = self.GetType(); type != null && type != typeof(object); type = type.BaseType) {
                string id = type.GetCustomAttribute<ClsIdAttribute>(false)?.Id ?? type.Name;
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) {
                    if (IsInjected(field) && field.GetValue(self) == null) {
                        result.Add(id + "." + field.Name);
                    }
                }
            }
            return result.ToArray();
        }

        public static bool IsInjected(FieldInfo field) {
            foreach (var attribute in field.CustomAttributes) {
                var type = attribute.AttributeType;
                if (type.Namespace != "KenseiECS") {
                    continue;
                }
                switch (type.Name) {
                    case "IncAttribute":
                    case "ExcAttribute":
                    case "AnyAttribute":
                    case "PoolAttribute":
                    case "GroupAttribute":
                    case "SharedAttribute":
                        return true;
                }
            }
            return false;
        }
    }
}
