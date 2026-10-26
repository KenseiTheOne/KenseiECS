// KenseiECS test stand, module unity: the project state a case runs with, and putting it back.
// Sources of KENSEI_DEBUG that a case can set: Player Settings of a target, a Build Profile (Unity 6),
// BuildPlayerOptions/ScriptCompilationSettings.extraScriptingDefines, Assets/csc.rsp, Player Settings ->
// Additional Compiler Arguments, and the KenseiECS -> Debug Mode menu item, which writes wherever the
// package under test writes.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
#if UNITY_6000_0_OR_NEWER
using UnityEditor.Build.Profile;
#endif
#if STAND_AA
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
#endif

namespace KenseiStand.Editor {
    internal static class StandPaths {
        public const string Root = "Assets/KenseiStand";
        public const string Scene = Root + "/StandScene.unity";
        public const string ControlScene = Root + "/StandControl.unity";
        public const string Data = Root + "/StandData.asset";
        public const string Profiles = Root + "/Profiles";
        public const string Rsp = "Assets/csc.rsp";
    }

    internal static class StandRsp {
        public static void Write(string[] defines) {
            File.WriteAllText(StandPaths.Rsp, string.Join("\n", defines.Select(d => "-define:" + d)) + "\n");
            AssetDatabase.ImportAsset(StandPaths.Rsp, ImportAssetOptions.ForceSynchronousImport);
        }

        public static void Remove() {
            if (!File.Exists(StandPaths.Rsp)) {
                return;
            }
            AssetDatabase.DeleteAsset(StandPaths.Rsp);
            if (File.Exists(StandPaths.Rsp)) {
                File.Delete(StandPaths.Rsp);
            }
            if (File.Exists(StandPaths.Rsp + ".meta")) {
                File.Delete(StandPaths.Rsp + ".meta");
            }
        }
    }

    /// <summary>Player Settings -> Additional Compiler Arguments (Unity 2021.2+): a define source the preprocess guard does not read.</summary>
    internal static class StandCompilerArgs {
        public static string[] Get(NamedBuildTarget target) => PlayerSettings.GetAdditionalCompilerArguments(target) ?? new string[0];

        public static void Set(NamedBuildTarget target, IEnumerable<string> args) {
            var list = args.ToArray();
            if (Get(target).SequenceEqual(list)) {
                return;
            }
            PlayerSettings.SetAdditionalCompilerArguments(target, list);
        }

        public static string[] Defines(string[] defines) => defines.Select(d => "-define:" + d).ToArray();

        /// <summary>TargetName -> arguments of every known target.</summary>
        public static Dictionary<string, string[]> All() {
            var all = new Dictionary<string, string[]>();
            foreach (var t in StandDefines.Known) {
                try {
                    all[t.TargetName] = Get(t);
                } catch (Exception) {
                    // A target this Unity version does not support.
                }
            }
            return all;
        }
    }

    /// <summary>KenseiECS -> Debug Mode, driven through the menu like a user does.</summary>
    internal static class StandToggle {
        public const string MenuPath = "KenseiECS/Debug Mode";
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        public static Type ToggleType => StandTypes.Find("KenseiECS.Editor.KenseiDebugToggle");

        public static bool Execute() => EditorApplication.ExecuteMenuItem(MenuPath);

        /// <summary>The package's own answer (2.2: IsEnabledForActiveTarget, 2.1: IsEnabledForCurrentTarget), null if absent.</summary>
        public static bool? PackageSaysEnabled() {
            var t = ToggleType;
            if (t == null) {
                return null;
            }
            var m = t.GetMethod("IsEnabledForActiveTarget", All, null, Type.EmptyTypes, null) ??
                t.GetMethod("IsEnabledForCurrentTarget", All, null, Type.EmptyTypes, null);
            if (m == null || m.ReturnType != typeof(bool)) {
                return null;
            }
            try {
                return (bool)m.Invoke(null, null);
            } catch (Exception) {
                return null;
            }
        }

        /// <summary>
        /// The menu checkmark as the package sets it: runs the [MenuItem(path, true)] validate method,
        /// then reads Menu.GetChecked. Null when the menu cannot be read in this editor (batchmode).
        /// </summary>
        public static bool? Checkmark() {
            if (!MenuWorks()) {
                return null;
            }
            var t = ToggleType;
            if (t == null) {
                return null;
            }
            foreach (var m in t.GetMethods(All)) {
                foreach (var attr in m.GetCustomAttributes(typeof(MenuItem), false).Cast<MenuItem>()) {
                    if (attr.validate && attr.menuItem == MenuPath) {
                        try {
                            m.Invoke(null, null);
                            return Menu.GetChecked(MenuPath);
                        } catch (Exception) {
                            return null;
                        }
                    }
                }
            }
            return null;
        }

        private static bool? s_MenuWorks;

        // Menu.SetChecked/GetChecked need a menu; check once that they round-trip before trusting them.
        private static bool MenuWorks() {
            if (s_MenuWorks.HasValue) {
                return s_MenuWorks.Value;
            }
            try {
                bool was = Menu.GetChecked(MenuPath);
                Menu.SetChecked(MenuPath, !was);
                bool flipped = Menu.GetChecked(MenuPath) == !was;
                Menu.SetChecked(MenuPath, was);
                s_MenuWorks = flipped && Menu.GetChecked(MenuPath) == was;
            } catch (Exception) {
                s_MenuWorks = false;
            }
            return s_MenuWorks.Value;
        }

        /// <summary>Whether KENSEI_DEBUG is in effect for the active target, read from the defines themselves.</summary>
        public static bool DefinesSayEnabled() {
            var defines = new List<string>(StandDefines.Get(StandDefines.Active));
#if UNITY_6000_0_OR_NEWER
            var profile = BuildProfile.GetActiveBuildProfile();
            if (profile != null && profile.scriptingDefines != null) {
                defines.AddRange(profile.scriptingDefines);
            }
#endif
            return defines.Contains(StandDefines.DebugDefine);
        }

        /// <summary>
        /// Brings Debug Mode to <paramref name="on"/> through the menu item. Returns what changed
        /// (where the package wrote the define). Throws StandInconclusive if the menu does not do it.
        /// </summary>
        public static string Ensure(bool on) {
            bool now = PackageSaysEnabled() ?? DefinesSayEnabled();
            if (now == on) {
                return "Debug Mode already " + (on ? "on" : "off");
            }
            var before = StandDefines.All();
            var profileBefore = ProfileDefines();
            if (!Execute()) {
                throw new StandInconclusive("menu item " + MenuPath + " not found (KenseiECS.Editor not loaded?)");
            }
            bool after = PackageSaysEnabled() ?? DefinesSayEnabled();
            string wrote = Diff(before, StandDefines.All(), profileBefore, ProfileDefines());
            if (after != on) {
                throw new StandInconclusive("Debug Mode did not turn " + (on ? "on" : "off") + " through the menu; changes: " + wrote);
            }
            return (on ? "on: " : "off: ") + wrote;
        }

        public static string[] ProfileDefines() {
#if UNITY_6000_0_OR_NEWER
            var profile = BuildProfile.GetActiveBuildProfile();
            if (profile != null) {
                return profile.scriptingDefines ?? new string[0];
            }
#endif
            return null;
        }

        public static string Diff(Dictionary<string, string[]> before, Dictionary<string, string[]> after, string[] profileBefore, string[] profileAfter) {
            var parts = new List<string>();
            foreach (var kv in after) {
                before.TryGetValue(kv.Key, out var old);
                old = old ?? new string[0];
                var added = kv.Value.Except(old).ToArray();
                var removed = old.Except(kv.Value).ToArray();
                if (added.Length > 0) {
                    parts.Add("+" + string.Join(";", added) + " in Player Settings " + kv.Key);
                }
                if (removed.Length > 0) {
                    parts.Add("-" + string.Join(";", removed) + " from Player Settings " + kv.Key);
                }
            }
            if (profileBefore != null || profileAfter != null) {
                var pb = profileBefore ?? new string[0];
                var pa = profileAfter ?? new string[0];
                var added = pa.Except(pb).ToArray();
                var removed = pb.Except(pa).ToArray();
#if UNITY_6000_0_OR_NEWER
                var profile = BuildProfile.GetActiveBuildProfile();
                string name = profile != null ? "'" + profile.name + "' (" + AssetDatabase.GetAssetPath(profile) + ")" : "?";
#else
                string name = "?";
#endif
                if (added.Length > 0) {
                    parts.Add("+" + string.Join(";", added) + " in Build Profile " + name);
                }
                if (removed.Length > 0) {
                    parts.Add("-" + string.Join(";", removed) + " from Build Profile " + name);
                }
            }
            return parts.Count == 0 ? "nothing changed" : string.Join(", ", parts);
        }
    }

#if UNITY_6000_0_OR_NEWER
    /// <summary>Build Profiles made by the stand (internal Unity API through reflection).</summary>
    internal static class StandProfiles {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        public static bool Available =>
            typeof(BuildProfile).GetMethod("CreateInstance", BindingFlags.NonPublic | BindingFlags.Static, null,
                new[] { typeof(BuildTarget), typeof(StandaloneBuildSubtarget) }, null) != null;

        public static BuildProfile Create(string name, bool development, string[] defines, bool ownPlayerSettings) {
            var create = typeof(BuildProfile).GetMethod("CreateInstance", BindingFlags.NonPublic | BindingFlags.Static, null,
                new[] { typeof(BuildTarget), typeof(StandaloneBuildSubtarget) }, null);
            if (create == null) {
                throw new StandSkipped("BuildProfile.CreateInstance(BuildTarget, StandaloneBuildSubtarget) not found in this Unity version");
            }
            var profile = (BuildProfile)create.Invoke(null, new object[] { StandDefines.ActiveTarget, StandaloneBuildSubtarget.Player });
            if (!AssetDatabase.IsValidFolder(StandPaths.Profiles)) {
                AssetDatabase.CreateFolder(StandPaths.Root, "Profiles");
            }
            string path = StandPaths.Profiles + "/" + name + ".asset";
            AssetDatabase.CreateAsset(profile, path);
            profile.scriptingDefines = defines ?? new string[0];
            SetPlatformSetting(profile, "development", development);
            // Export a Gradle project, like the global setting of the stand: no Gradle run if a build gets that far.
            SetPlatformSetting(profile, "exportAsGoogleAndroidProject", true);
            if (ownPlayerSettings) {
                CreateOwnPlayerSettings(profile);
            }
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssetIfDirty(profile);
            return profile;
        }

        public static bool GetDevelopment(BuildProfile profile) {
            var pbp = typeof(BuildProfile).GetProperty("platformBuildProfile", All)?.GetValue(profile);
            var prop = pbp?.GetType().GetProperty("development", All);
            return prop != null && (bool)prop.GetValue(pbp);
        }

        private static void SetPlatformSetting(BuildProfile profile, string property, bool value) {
            var pbp = typeof(BuildProfile).GetProperty("platformBuildProfile", All)?.GetValue(profile);
            var prop = pbp?.GetType().GetProperty(property, All);
            if (prop == null || !prop.CanWrite) {
                if (property == "development") {
                    throw new StandSkipped("no BuildProfile.platformBuildProfile.development in this Unity version");
                }
                return;
            }
            prop.SetValue(pbp, value);
        }

        // "Customize player settings" of the profile: Unity's BuildProfileModuleUtil.CreatePlayerSettingsFromGlobal.
        private static void CreateOwnPlayerSettings(BuildProfile profile) {
            var util = StandTypes.Find("UnityEditor.Build.Profile.BuildProfileModuleUtil");
            var method = util?.GetMethod("CreatePlayerSettingsFromGlobal", All, null, new[] { typeof(BuildProfile) }, null);
            if (method == null) {
                throw new StandSkipped("no BuildProfileModuleUtil.CreatePlayerSettingsFromGlobal(BuildProfile) in this Unity version");
            }
            method.Invoke(null, new object[] { profile });
            if (!HasOwnPlayerSettings(profile)) {
                throw new StandInconclusive("CreatePlayerSettingsFromGlobal did not give the profile its own Player Settings");
            }
        }

        public static bool HasOwnPlayerSettings(BuildProfile profile) {
            var prop = typeof(BuildProfile).GetProperty("playerSettings", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            try {
                return prop?.GetValue(profile) is UnityEngine.Object o && o != null;
            } catch (Exception) {
                return false;
            }
        }
    }
#endif

    /// <summary>Everything a case may change, taken before the group and put back after every case.</summary>
    internal sealed class StandSnapshot {
        private Dictionary<string, string[]> _ps;
        private Dictionary<string, string[]> _args;
        private bool _development;
        private bool _devTickedKnown;
#if STAND_AA
        private AddressableAssetSettings.PlayerBuildOption _aa;
#endif

        public static StandSnapshot Take() {
            var s = new StandSnapshot {
                _ps = StandDefines.All(), _args = StandCompilerArgs.All(), _development = EditorUserBuildSettings.development, _devTickedKnown = true
            };
#if STAND_AA
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            s._aa = settings != null ? settings.BuildAddressablesWithPlayerBuild : AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer;
#endif
            return s;
        }

        public string Describe() => StandDefines.Describe(_ps);

        public List<string> Baseline() => _ps.Select(kv => kv.Key + "=" + string.Join(";", kv.Value)).ToList();

        public void Restore() {
#if UNITY_6000_0_OR_NEWER
            if (BuildProfile.GetActiveBuildProfile() != null) {
                BuildProfile.SetActiveBuildProfile(null);
            }
            if (AssetDatabase.IsValidFolder(StandPaths.Profiles)) {
                AssetDatabase.DeleteAsset(StandPaths.Profiles);
            }
#endif
            StandRsp.Remove();
            foreach (var kv in _ps) {
                StandDefines.Set(StandDefines.ByName(kv.Key), kv.Value);
            }
            foreach (var kv in _args) {
                StandCompilerArgs.Set(StandDefines.ByName(kv.Key), kv.Value);
            }
            if (_devTickedKnown) {
                EditorUserBuildSettings.development = _development;
            }
#if STAND_AA
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings != null && settings.BuildAddressablesWithPlayerBuild != _aa) {
                settings.BuildAddressablesWithPlayerBuild = _aa;
                EditorUtility.SetDirty(settings);
            }
#endif
            AssetDatabase.SaveAssets();
        }
    }

    /// <summary>Applies the define sources of a case. Order: Player Settings, profile, csc.rsp, compiler arguments, Build Settings, Addressables, Debug Mode.</summary>
    internal static class StandApply {
#if UNITY_6000_0_OR_NEWER
        public static BuildProfile Profile;
#endif
        // Addressables' own preference key (AddressablesPreferences.kBuildAddressablesWithPlayerBuildKey, default true).
        // The stand reads it and never writes it: EditorPrefs belong to the user of this machine, not to the project.
        private const string AaPreferenceKey = "Addressables.BuildAddressablesWithPlayerBuild";

        public static void Apply(CaseDef c, CaseResult r) {
#if UNITY_6000_0_OR_NEWER
            Profile = null;
#endif
            var active = StandDefines.Active;
            if (c.psDefines != "*") {
                StandDefines.Set(active, StandText.Split(c.psDefines));
            }
            if (c.checkWarnings) {
                // Compiler warnings are printed only when the scripts really compile; a define of its own makes every
                // compile of this case miss the build cache (Library/Bee), so its warnings do not depend on the cases before it.
                StandDefines.Set(active, StandDefines.Get(active).Concat(new[] { "STAND_FRESH_" + DateTime.UtcNow.Ticks }));
            }
            if (!string.IsNullOrEmpty(c.otherPsDefines)) {
                foreach (var entry in c.otherPsDefines.Split(',')) {
                    var kv = entry.Split('=');
                    if (kv.Length == 2) {
                        StandDefines.Set(StandDefines.ByName(kv[0].Trim()), StandText.Split(kv[1]));
                    }
                }
            }
            if (!string.IsNullOrEmpty(c.profile)) {
#if UNITY_6000_0_OR_NEWER
                Profile = StandProfiles.Create("Stand_" + c.id.Replace("-", "_"), c.profile == "dev", StandText.Split(c.profileDefines), c.profile == "ownps");
                if (c.profileInactive) {
                    if (BuildProfile.GetActiveBuildProfile() != null) {
                        BuildProfile.SetActiveBuildProfile(null);
                    }
                    if (BuildProfile.GetActiveBuildProfile() != null) {
                        throw new StandInconclusive("could not deactivate the active Build Profile");
                    }
                } else {
                    BuildProfile.SetActiveBuildProfile(Profile);
                    if (BuildProfile.GetActiveBuildProfile() != Profile) {
                        throw new StandInconclusive("the Build Profile did not become active");
                    }
                }
#else
                throw new StandSkipped("Build Profiles exist only in Unity 6");
#endif
            }
            if (!string.IsNullOrEmpty(c.rspDefines)) {
                StandRsp.Write(StandText.Split(c.rspDefines));
            }
            if (!string.IsNullOrEmpty(c.argsDefines)) {
                StandCompilerArgs.Set(active, StandCompilerArgs.Get(active).Concat(StandCompilerArgs.Defines(StandText.Split(c.argsDefines))));
            }
            if (!string.IsNullOrEmpty(c.rawArgs)) {
                var raw = c.rawArgs.Split('|').Select(a => a.Trim()).Where(a => a.Length > 0);
                StandCompilerArgs.Set(active, StandCompilerArgs.Get(active).Concat(raw));
            }
            if (c.devTicked) {
                EditorUserBuildSettings.development = true;
            }
            if (!string.IsNullOrEmpty(c.aaWithPlayer)) {
#if STAND_AA
                var settings = AddressableAssetSettingsDefaultObject.Settings;
                if (settings == null) {
                    throw new StandInconclusive("no AddressableAssetSettings (setup did not create them)");
                }
                if (c.aaWithPlayer == "default") {
                    if (!EditorPrefs.GetBool(AaPreferenceKey, true)) {
                        throw new StandSkipped("the Addressables preference \"Build Addressables on Player Build\" of this machine is off, so the default " +
                            "settings do not build content with the player; the stand does not change editor preferences");
                    }
                    settings.BuildAddressablesWithPlayerBuild = AddressableAssetSettings.PlayerBuildOption.PreferencesValue;
                } else {
                    settings.BuildAddressablesWithPlayerBuild = c.aaWithPlayer == "yes"
                        ? AddressableAssetSettings.PlayerBuildOption.BuildWithPlayer
                        : AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer;
                }
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssetIfDirty(settings);
#else
                throw new StandSkipped("Addressables are not installed");
#endif
            }
            if (c.debugMode == "on" || c.debugMode == "off") {
                r.applied.debugMode = StandToggle.Ensure(c.debugMode == "on");
            }
            Record(c, r);
        }

        private static void Record(CaseDef c, CaseResult r) {
            var a = r.applied;
            a.target = StandDefines.Active.TargetName;
            a.psActive = StandDefines.Get(StandDefines.Active).ToList();
            a.psWithDefine = StandDefines.WithDefine(StandDefines.All());
            a.extraDefines = StandText.Split(c.extraDefines).ToList();
            a.rspDefines = StandText.Split(c.rspDefines).ToList();
            a.compilerArgs = StandCompilerArgs.Get(StandDefines.Active).ToList();
            a.development = c.development;
            a.devTicked = EditorUserBuildSettings.development;
            a.buildVia = c.buildVia;
#if UNITY_6000_0_OR_NEWER
            var active = BuildProfile.GetActiveBuildProfile();
            var profile = active != null ? active : Profile;
            a.profileActive = active != null;
            if (profile != null) {
                a.profileName = profile.name;
                a.profilePath = AssetDatabase.GetAssetPath(profile);
                a.profileDefines = (profile.scriptingDefines ?? new string[0]).ToList();
                a.profileDevelopment = StandProfiles.GetDevelopment(profile);
                a.profileOwnPlayerSettings = StandProfiles.HasOwnPlayerSettings(profile);
            }
#endif
#if STAND_AA
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            a.aaWithPlayer = settings != null ? settings.BuildAddressablesWithPlayerBuild.ToString() : "";
#endif
        }
    }
}
