#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
#if UNITY_2023_1_OR_NEWER
using UnityEditor.Build;
#else
using System.Reflection;
#endif

namespace KenseiECS.Editor {
    /// <summary>
    /// Menu toggle for enabling/disabling KENSEI_DEBUG scripting define.
    /// When enabled, profiler hooks, inspector editing, and profiler window are active.
    /// When disabled, all debug overhead is stripped from compilation.
    /// The define is applied to every build target so switching platforms keeps the mode;
    /// the menu checkmark reflects the currently selected target.
    ///
    /// Menu: KenseiECS → Debug Mode
    /// </summary>
    public static class KenseiDebugToggle {
        private const string DEFINE = "KENSEI_DEBUG";
        private const string MENU_PATH = "KenseiECS/Debug Mode";

        [MenuItem(MENU_PATH, false, 1000)]
        private static void Toggle() {
            bool enable = !IsEnabledForCurrentTarget();
#if UNITY_2023_1_OR_NEWER
            foreach (var target in GetAllTargets()) {
                Apply(target, enable);
            }
#else
            foreach (var group in GetAllGroups()) {
                try {
                    Apply(group, enable);
                } catch (Exception) {
                    // Groups whose platform module is not installed, or that this Unity
                    // version no longer supports, throw; there is nothing to set for them.
                }
            }
#endif
        }

        [MenuItem(MENU_PATH, true)]
        private static bool ToggleValidate() {
            Menu.SetChecked(MENU_PATH, IsEnabledForCurrentTarget());
            return true;
        }

        private static bool SetDefine(List<string> defines, bool enable) {
            if (defines.Contains(DEFINE) == enable) {
                return false;
            }

            if (enable) {
                defines.Add(DEFINE);
            } else {
                defines.RemoveAll(define => define == DEFINE);
            }
            return true;
        }

#if UNITY_2023_1_OR_NEWER
        private static NamedBuildTarget CurrentTarget => NamedBuildTarget.FromBuildTargetGroup(EditorUserBuildSettings.selectedBuildTargetGroup);

        private static bool IsEnabledForCurrentTarget() {
            PlayerSettings.GetScriptingDefineSymbols(CurrentTarget, out string[] defines);
            return Array.IndexOf(defines, DEFINE) >= 0;
        }

        private static List<NamedBuildTarget> GetAllTargets() {
            var targets = new List<NamedBuildTarget> {
                NamedBuildTarget.Standalone,
                NamedBuildTarget.Server,
                NamedBuildTarget.Android,
                NamedBuildTarget.iOS,
                NamedBuildTarget.WebGL
            };

            var current = CurrentTarget;
            if (!targets.Contains(current)) {
                targets.Add(current);
            }
            return targets;
        }

        private static void Apply(NamedBuildTarget target, bool enable) {
            PlayerSettings.GetScriptingDefineSymbols(target, out string[] defines);
            var list = new List<string>(defines);
            if (SetDefine(list, enable)) {
                PlayerSettings.SetScriptingDefineSymbols(target, list.ToArray());
            }
        }
#else
        private static BuildTargetGroup CurrentGroup => EditorUserBuildSettings.selectedBuildTargetGroup;

        private static bool IsEnabledForCurrentTarget() =>
            GetDefines(CurrentGroup).Contains(DEFINE);

        private static List<BuildTargetGroup> GetAllGroups() {
            var groups = new List<BuildTargetGroup>();
            var members = typeof(BuildTargetGroup).GetFields(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < members.Length; i++) {
                if (members[i].IsDefined(typeof(ObsoleteAttribute), false)) {
                    continue;
                }

                var group = (BuildTargetGroup)members[i].GetValue(null);
                if (group == BuildTargetGroup.Unknown || groups.Contains(group)) {
                    continue;
                }
                groups.Add(group);
            }
            return groups;
        }

        private static List<string> GetDefines(BuildTargetGroup group) {
            var defines = new List<string>();
            foreach (var define in PlayerSettings.GetScriptingDefineSymbolsForGroup(group).Split(';')) {
                if (define.Length > 0) {
                    defines.Add(define);
                }
            }
            return defines;
        }

        private static void Apply(BuildTargetGroup group, bool enable) {
            var defines = GetDefines(group);
            if (SetDefine(defines, enable)) {
                PlayerSettings.SetScriptingDefineSymbolsForGroup(group, string.Join(";", defines));
            }
        }
#endif
    }
}
#endif
