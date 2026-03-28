#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace KenseiECS.Editor {
    /// <summary>
    /// Menu toggle for enabling/disabling KENSEI_DEBUG scripting define.
    /// When enabled, profiler hooks, inspector editing, and profiler window are active.
    /// When disabled, all debug overhead is stripped from compilation.
    ///
    /// Menu: KenseiECS → Debug Mode
    /// </summary>
    public static class KenseiDebugToggle {
        private const string DEFINE = "KENSEI_DEBUG";
        private const string MENU_PATH = "KenseiECS/Debug Mode";

        [MenuItem(MENU_PATH, false, 1000)]
        private static void Toggle() {
            var defines = GetDefines();
            var list = new List<string>(defines);

            if (list.Contains(DEFINE)) {
                list.Remove(DEFINE);
            } else {
                list.Add(DEFINE);
            }

            SetDefines(list);
        }

        [MenuItem(MENU_PATH, true)]
        private static bool ToggleValidate() {
            var defines = GetDefines();
            Menu.SetChecked(MENU_PATH, defines.Contains(DEFINE));
            return true;
        }

        private static string[] GetDefines() {
#if UNITY_2023_1_OR_NEWER
            var target = UnityEditor.Build.NamedBuildTarget.FromBuildTargetGroup(
                EditorUserBuildSettings.selectedBuildTargetGroup);
            PlayerSettings.GetScriptingDefineSymbols(target, out string[] defines);
            return defines;
#else
            var target = EditorUserBuildSettings.selectedBuildTargetGroup;
            return PlayerSettings.GetScriptingDefineSymbolsForGroup(target)
                .Split(';')
                .Where(s => !string.IsNullOrEmpty(s))
                .ToArray();
#endif
        }

        private static void SetDefines(List<string> defines) {
#if UNITY_2023_1_OR_NEWER
            var target = UnityEditor.Build.NamedBuildTarget.FromBuildTargetGroup(
                EditorUserBuildSettings.selectedBuildTargetGroup);
            PlayerSettings.SetScriptingDefineSymbols(target, defines.ToArray());
#else
            var target = EditorUserBuildSettings.selectedBuildTargetGroup;
            PlayerSettings.SetScriptingDefineSymbolsForGroup(
                target, string.Join(";", defines));
#endif
        }
    }
}
#endif
