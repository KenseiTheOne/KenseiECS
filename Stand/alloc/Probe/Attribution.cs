using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using KenseiECS;

namespace KenseiStand.Alloc.Probe {
    /// <summary>
    /// Attribution knobs of the probe (--pretouch-type-names, --run-cctors-of): one kind of one-time work
    /// done before any scenario, outside every window. When a one-time difference disappears in a
    /// fresh process with a knob, the difference was that work; the stand reports it that way instead
    /// of guessing.
    /// </summary>
    public static class Attribution {
        /// <summary>
        /// <c>Type.Name</c> of every component type the probe uses. CoreCLR caches a type's name in its
        /// RuntimeTypeCache: the first call allocates the cache and the string, later calls return the
        /// cached string. A library that builds <c>typeof(T).Name</c> inside a window then allocates
        /// nothing for it. (Mono and IL2CPP build a new string on every call, so there the library's
        /// own cache is what keeps it to one string per pool.)
        /// </summary>
        public static int PretouchTypeNames() {
            int n = 0;
            foreach (var t in ComponentTypes()) {
                Sink.Value = t.Name;
                n++;
            }
            return n;
        }

        /// <summary>
        /// Non-generic KenseiECS types that declare static fields: the candidates whose static constructor
        /// can create state on a first call. One per line for --list-static-types.
        /// </summary>
        public static List<string> StaticTypes() {
            var list = new List<string>();
            foreach (var t in typeof(World).Assembly.GetTypes()) {
                if (t.Namespace == null || !t.Namespace.StartsWith("KenseiECS", StringComparison.Ordinal) || t.ContainsGenericParameters) {
                    continue;
                }
                if (t.GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public
                                | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly).Length > 0) {
                    list.Add(t.FullName);
                }
            }
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        /// <summary>
        /// Runs the static constructor of one KenseiECS type (--run-cctors-of), looked up by full name: unlike an
        /// enumeration of the assembly, it creates no reflection cache for any other type (the first Type.Name of a
        /// component type stays inside the scenario). Static state a first call would create (EcsProfiler's event
        /// buffer under KENSEI_DEBUG, for example) then exists before the window.
        /// </summary>
        public static bool RunClassConstructorOf(string fullName) {
            var t = typeof(World).Assembly.GetType(fullName, false);
            if (t == null) {
                return false;
            }
            RuntimeHelpers.RunClassConstructor(t.TypeHandle);
            return true;
        }

        private static IEnumerable<Type> ComponentTypes() {
            foreach (var t in typeof(Program).Assembly.GetTypes()) {
                if (t.IsValueType && !t.ContainsGenericParameters && typeof(IComponent).IsAssignableFrom(t)) {
                    yield return t;
                }
            }
            foreach (var t in Registry.ClosedGenericComponents) {
                yield return t;
            }
        }
    }
}
