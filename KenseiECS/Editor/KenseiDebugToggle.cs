#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
#if UNITY_6000_0_OR_NEWER
using UnityEditor.Build.Profile;
#endif

namespace KenseiECS.Editor {
    /// <summary>
    /// Menu toggle for enabling/disabling the KENSEI_DEBUG scripting define.
    /// When enabled, profiler hooks, inspector editing, and profiler window are active.
    /// When disabled, all debug overhead is stripped from compilation.
    ///
    /// Where the define is written:
    /// <list type="bullet">
    /// <item>Unity 6+ with an active Build Profile: the profile's Scripting Defines, so the mode
    /// travels with the profile and other profiles stay untouched.</item>
    /// <item>No active profile (or Unity before 6): the Player Settings defines of the active
    /// build target only. Other targets are left alone, so a release target cannot pick up the
    /// define by accident.</item>
    /// </list>
    /// The checkmark shows whether the define is in effect for the active profile/target, from
    /// either source. Turning it off removes it from both the active profile and the active
    /// target's Player Settings, so a define left in Player Settings by older versions (which
    /// wrote it to every target) does not keep the mode on silently. Other targets are not
    /// touched; if their Player Settings still have the define, a warning lists them.
    ///
    /// A Build Profile is an asset, usually under version control and shared by the team, so
    /// every write to it is logged with the asset's name and path. That includes the Player
    /// Settings write when turning the mode off: if the active profile has its own Player Settings
    /// ("Customize player settings"), Unity redirects PlayerSettings reads and writes to the copy
    /// stored in the profile asset, so "Player Settings" in this class and in the build guard
    /// means that copy, not Project Settings.
    ///
    /// Menu: KenseiECS → Debug Mode
    /// </summary>
    public static class KenseiDebugToggle {
        /// <summary>The scripting define that compiles in KenseiECS validation and debug tooling.</summary>
        internal const string DEFINE = KenseiDebugDefines.DEFINE;
        private const string MENU_PATH = "KenseiECS/Debug Mode";

        [MenuItem(MENU_PATH, false, 1000)]
        private static void Toggle() {
            bool enable = !IsEnabledForActiveTarget();
#if UNITY_6000_0_OR_NEWER
            var profile = BuildProfile.GetActiveBuildProfile();
            if (profile != null) {
                SetProfileDefine(profile, enable);
                if (!enable) {
                    SetPlayerSettingsDefine(enable: false);
                    WarnAboutOtherTargets();
                }
                return;
            }
#endif
            SetPlayerSettingsDefine(enable);
            if (!enable) {
                WarnAboutOtherTargets();
            }
        }

        [MenuItem(MENU_PATH, true)]
        private static bool ToggleValidate() {
            Menu.SetChecked(MENU_PATH, IsEnabledForActiveTarget());
            return true;
        }

        /// <summary>
        /// True when <see cref="DEFINE"/> is set for the active build target: in the active
        /// Build Profile's defines (Unity 6+) or in the target's Player Settings defines.
        /// </summary>
        internal static bool IsEnabledForActiveTarget() {
            var defines = new List<string>();
            CollectActiveDefines(defines);
            return defines.Contains(DEFINE);
        }

        /// <summary>
        /// Appends the scripting defines a player build of the active target gets from the
        /// active Build Profile (Unity 6+) and the target's Player Settings.
        /// </summary>
        internal static void CollectActiveDefines(List<string> defines) {
#if UNITY_6000_0_OR_NEWER
            var profile = BuildProfile.GetActiveBuildProfile();
            if (profile != null && profile.scriptingDefines != null) {
                defines.AddRange(profile.scriptingDefines);
            }
#endif
            CollectPlayerSettingsDefines(ActiveTarget, defines);
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

#if UNITY_6000_0_OR_NEWER
        private static void SetProfileDefine(BuildProfile profile, bool enable) {
            var defines = new List<string>(profile.scriptingDefines ?? Array.Empty<string>());
            if (!SetDefine(defines, enable)) {
                return;
            }

            profile.scriptingDefines = defines.ToArray();
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssetIfDirty(profile);
            Debug.Log(KenseiDebugDefines.ProfileWritten(profile.name, AssetDatabase.GetAssetPath(profile), enable), profile);
            // Editing the defines from code does not always schedule a recompile the way the
            // profile inspector does; request one so the menu takes effect immediately.
            UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();
        }
#endif

        // NamedBuildTarget (Unity 2021.2+) rather than BuildTargetGroup: a Dedicated Server build
        // compiles with its own define slot, which the group API cannot address.
        private static NamedBuildTarget ActiveTarget =>
            ToNamedTarget(BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget),
                EditorUserBuildSettings.standaloneBuildSubtarget == StandaloneBuildSubtarget.Server);

        /// <summary>Maps a build target group to its define slot; a dedicated server has its own.</summary>
        internal static NamedBuildTarget ToNamedTarget(BuildTargetGroup group, bool server) {
            if (server && group == BuildTargetGroup.Standalone) {
                return NamedBuildTarget.Server;
            }
            return NamedBuildTarget.FromBuildTargetGroup(group);
        }

        internal static void CollectPlayerSettingsDefines(NamedBuildTarget target, List<string> defines) {
            PlayerSettings.GetScriptingDefineSymbols(target, out string[] symbols);
            defines.AddRange(symbols);
        }

        private static void SetPlayerSettingsDefine(bool enable) {
            var target = ActiveTarget;
            var defines = new List<string>();
            CollectPlayerSettingsDefines(target, defines);
            if (!SetDefine(defines, enable)) {
                return;
            }
#if UNITY_6000_0_OR_NEWER
            // With a profile that overrides Player Settings, Unity writes this into the profile
            // asset and saves it. Compare the file as well as asking the internal flag, so the
            // write is logged even if the flag cannot be read on this Unity version.
            var profile = BuildProfile.GetActiveBuildProfile();
            string profilePath = profile != null ? AssetDatabase.GetAssetPath(profile) : null;
            byte[] before = ReadAssetFile(profilePath);
#endif
            PlayerSettings.SetScriptingDefineSymbols(target, defines.ToArray());
#if UNITY_6000_0_OR_NEWER
            if (profile != null &&
                (ProfileOverridesPlayerSettings(profile) == true || !SameBytes(before, ReadAssetFile(profilePath)))) {
                Debug.Log(KenseiDebugDefines.ProfilePlayerSettingsWritten(profile.name, profilePath, target.TargetName, enable), profile);
            }
#endif
        }

#if UNITY_6000_0_OR_NEWER
        // BuildProfile.playerSettings is internal (6000.0-6000.6): the profile's own Player Settings,
        // null when it uses Project Settings. There is no public way to ask.
        private static readonly PropertyInfo ProfilePlayerSettingsProperty =
            typeof(BuildProfile).GetProperty("playerSettings", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>
        /// Whether <paramref name="profile"/> has its own Player Settings ("Customize player settings").
        /// While such a profile is active, PlayerSettings defines are read from and written to the copy
        /// in the profile asset. Null when this Unity version does not expose it.
        /// </summary>
        internal static bool? ProfileOverridesPlayerSettings(BuildProfile profile) {
            if (profile == null) {
                return false;
            }
            if (ProfilePlayerSettingsProperty == null) {
                return null;
            }
            try {
                return ProfilePlayerSettingsProperty.GetValue(profile) is UnityEngine.Object settings && settings != null;
            } catch (Exception) {
                return null;
            }
        }

        private static byte[] ReadAssetFile(string path) {
            try {
                return string.IsNullOrEmpty(path) || !File.Exists(path) ? null : File.ReadAllBytes(path);
            } catch (IOException) {
                return null;
            }
        }

        private static bool SameBytes(byte[] a, byte[] b) {
            if (a == null || b == null) {
                return a == b;
            }
            if (a.Length != b.Length) {
                return false;
            }
            for (int i = 0; i < a.Length; i++) {
                if (a[i] != b[i]) {
                    return false;
                }
            }
            return true;
        }
#endif

        /// <summary>
        /// Logs a warning when build targets other than the active one still have the define in
        /// their Player Settings. Nothing is removed: a team may keep it on another target on purpose.
        /// </summary>
        private static void WarnAboutOtherTargets() {
            var active = ActiveTarget;
            var leftovers = new List<string>();
            var defines = new List<string>();
            foreach (var target in KnownTargets()) {
                if (target.Equals(active)) {
                    continue;
                }
                defines.Clear();
                try {
                    CollectPlayerSettingsDefines(target, defines);
                } catch (Exception) {
                    // A target this Unity version no longer supports may throw; it builds nothing.
                    continue;
                }
                KenseiDebugDefines.AddIfDefined(leftovers, target.TargetName, defines.ToArray());
            }
            if (leftovers.Count > 0) {
                string profileName = null;
                string profilePath = null;
                bool? overrides = false;
#if UNITY_6000_0_OR_NEWER
                var profile = BuildProfile.GetActiveBuildProfile();
                if (profile != null) {
                    profileName = profile.name;
                    profilePath = AssetDatabase.GetAssetPath(profile);
                    overrides = ProfileOverridesPlayerSettings(profile);
                }
#endif
                Debug.LogWarning(KenseiDebugDefines.OtherTargetsStillDefined(leftovers, profileName, profilePath, overrides));
            }
        }

        // Every define slot Player Settings can hold: the non-obsolete build target groups (the set
        // 2.1.x wrote to before 2023.1) plus the Dedicated Server slot, which has no group of its own.
        private static List<NamedBuildTarget> KnownTargets() {
            var targets = new List<NamedBuildTarget> { NamedBuildTarget.Server };
            var fields = typeof(BuildTargetGroup).GetFields(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < fields.Length; i++) {
                if (fields[i].IsDefined(typeof(ObsoleteAttribute), false)) {
                    continue;
                }
                var group = (BuildTargetGroup)fields[i].GetValue(null);
                if (group == BuildTargetGroup.Unknown) {
                    continue;
                }
                NamedBuildTarget target;
                try {
                    target = NamedBuildTarget.FromBuildTargetGroup(group);
                } catch (Exception) {
                    // Groups without a define slot of their own are rejected; nothing to check.
                    continue;
                }
                if (!targets.Contains(target)) {
                    targets.Add(target);
                }
            }
            return targets;
        }
    }
}
#endif
