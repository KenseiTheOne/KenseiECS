#if UNITY_EDITOR
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
            var target = EditorUserBuildSettings.selectedBuildTargetGroup;
            var defines = PlayerSettings.GetScriptingDefineSymbolsForGroup(target);
            var list = new System.Collections.Generic.List<string>(defines.Split(';'));

            if (list.Contains(DEFINE)) {
                list.Remove(DEFINE);
            } else {
                list.Add(DEFINE);
            }

            PlayerSettings.SetScriptingDefineSymbolsForGroup(
                target, string.Join(";", list));
        }

        [MenuItem(MENU_PATH, true)]
        private static bool ToggleValidate() {
            var target = EditorUserBuildSettings.selectedBuildTargetGroup;
            var defines = PlayerSettings.GetScriptingDefineSymbolsForGroup(target);
            Menu.SetChecked(MENU_PATH, defines.Contains(DEFINE));
            return true;
        }
    }
}
#endif
