// SystemsRunner API newer than 2.1.1, reached through reflection so that the stand compiles
// against every tree. Each member is checked once; a missing one switches the matching checks off
// ("not in this version").
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KenseiECS;
using Stand.Runner.Kit;

namespace Stand.Runner {
    /// <summary> Which documented contract the checks follow. </summary>
    public enum Contract {
        /// <summary> 2.1.x: no rollback (a failed Init resumes), inline group slot separate from child.IsEnabled. </summary>
        V21,
        /// <summary> 2.2.x: Init rollback, groups/phases, SetActive(bool), IndexOf, cleanWhenDisabled, duplicate names throw under KENSEI_DEBUG. </summary>
        V22
    }

    public static class Api {
        private static readonly Type R = typeof(SystemsRunner);

        public static readonly Func<SystemsRunner, string, SystemsRunner, SystemsRunner> AddGroupFn =
            Make<Func<SystemsRunner, string, SystemsRunner, SystemsRunner>>(R.GetMethod("AddGroup", new[] { typeof(string), R }));

        public static readonly Func<SystemsRunner, string, SystemsRunner, SystemsRunner> AddPhaseFn =
            Make<Func<SystemsRunner, string, SystemsRunner, SystemsRunner>>(R.GetMethod("AddPhase", new[] { typeof(string), R }));

        public static readonly Action<SystemsRunner, bool> SetActiveSelfFn =
            Make<Action<SystemsRunner, bool>>(R.GetMethod("SetActive", new[] { typeof(bool) }));

        public static readonly Func<SystemsRunner, string, int> IndexOfNameFn =
            Make<Func<SystemsRunner, string, int>>(R.GetMethod("IndexOf", new[] { typeof(string) }));

        public static readonly Func<SystemsRunner, ISystem, int> IndexOfSystemFn =
            Make<Func<SystemsRunner, ISystem, int>>(R.GetMethod("IndexOf", new[] { typeof(ISystem) }));

        private static readonly MethodInfo OneFrameCleanDef = R.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "OneFrame" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1 &&
                                 m.GetParameters()[0].ParameterType == typeof(bool));

        private static readonly Func<SystemsRunner, bool, SystemsRunner>[] OneFrameCleanFns = BuildOneFrameClean();

        /// <summary> The obsolete Add(SystemsRunner, string) overload exists (2.2.0+); calls bind to it from code typed SystemsRunner. </summary>
        public static readonly bool HasObsoleteAddRunnerName = R.GetMethod("Add", new[] { R, typeof(string) }) != null;

        public static readonly bool LibraryDebug = R.GetMethod("ResetTimings") != null;

        public static bool HasAddGroup => AddGroupFn != null;
        public static bool HasAddPhase => AddPhaseFn != null;
        public static bool HasSetActiveSelf => SetActiveSelfFn != null;
        public static bool HasIndexOf => IndexOfNameFn != null && IndexOfSystemFn != null;
        public static bool HasCleanWhenDisabled => OneFrameCleanDef != null;

        public static SystemsRunner OneFrame(SystemsRunner runner, int type, bool cleanWhenDisabled) {
            if (!cleanWhenDisabled || !HasCleanWhenDisabled) {
                return Events.OneFrame(runner, type);
            }
            return OneFrameCleanFns[type](runner, true);
        }

        /// <summary> Register a phase: AddPhase where it exists, otherwise the 2.1 Add(runner, name). </summary>
        public static SystemsRunner AddPhase(SystemsRunner parent, string name, SystemsRunner child, int api) {
            if (api == 0 && HasAddPhase) {
                return AddPhaseFn(parent, name, child);
            }
            if (api == 2) {
                // Statically typed SystemsRunner: binds to the obsolete Add(SystemsRunner, string) on 2.2+,
                // to Add(ISystem, string) on 2.1 — a phase either way.
                return parent.Add(child, name);
            }
            return parent.Add((ISystem)child, name);
        }

        /// <summary> Register an inline group: AddGroup for a named one, Add(runner) / Add(runner, null) for an unnamed one. </summary>
        public static SystemsRunner AddGroup(SystemsRunner parent, string name, SystemsRunner child, int api) {
            if (name != null) {
                if (!HasAddGroup) {
                    throw new NotSupportedException("AddGroup is not in this version");
                }
                return AddGroupFn(parent, name, child);
            }
            if (api == 1) {
                return parent.Add(child, (string)null);
            }
            return parent.Add(child);
        }

        public static Dictionary<string, bool> Features() {
            return new Dictionary<string, bool> {
                ["AddGroup(name, runner)"] = HasAddGroup,
                ["AddPhase(name, runner)"] = HasAddPhase,
                ["SetActive(bool)"] = HasSetActiveSelf,
                ["IndexOf(ISystem)/IndexOf(string)"] = HasIndexOf,
                ["OneFrame<T>(cleanWhenDisabled)"] = HasCleanWhenDisabled,
                ["[Obsolete] Add(SystemsRunner, string)"] = HasObsoleteAddRunnerName
            };
        }

        private static TDelegate Make<TDelegate>(MethodInfo method) where TDelegate : Delegate {
            return method == null ? null : (TDelegate)Delegate.CreateDelegate(typeof(TDelegate), method);
        }

        private static Func<SystemsRunner, bool, SystemsRunner>[] BuildOneFrameClean() {
            var fns = new Func<SystemsRunner, bool, SystemsRunner>[Events.TypeCount];
            if (OneFrameCleanDef == null) {
                return fns;
            }
            for (int t = 0; t < fns.Length; t++) {
                fns[t] = Make<Func<SystemsRunner, bool, SystemsRunner>>(OneFrameCleanDef.MakeGenericMethod(Events.TypeOf(t)));
            }
            return fns;
        }
    }
}
