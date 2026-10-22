#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
#if UNITY_6000_0_OR_NEWER
using UnityEditor.Build.Profile;
#endif

namespace KenseiECS.Editor {
    /// <summary>
    /// Fails a non-development player build that would ship with KENSEI_DEBUG.
    /// The define compiles in validation, debug names and per-system timings, which cost
    /// frame time and memory in a release player; it usually gets there because Debug Mode
    /// was left on. Development builds are not checked.
    ///
    /// The defines checked are the ones the build reads from assets: the active Build Profile's
    /// Scripting Defines (Unity 6+) plus the Player Settings defines of the built target. The
    /// error names the source: the profile (name and asset path) and/or the Player Settings
    /// target. If the profile has its own Player Settings, Unity reads the copy stored in the
    /// profile asset while it is active, and the error says that the Player Settings are there.
    /// To ship a release build with validation on deliberately, add KENSEI_ALLOW_DEBUG_RELEASE to
    /// the same defines.
    ///
    /// Defines passed only to the build (BuildPlayerOptions.extraScriptingDefines, csc.rsp) are not
    /// visible here. A KENSEI_DEBUG from those is caught when the player scripts compile, by the
    /// #error in DevTools/KenseiDebugReleaseCheck.cs; an allow define from those satisfies that
    /// #error but not this guard.
    ///
    /// This is a last line of defence, not a pre-flight check: Unity has already run the
    /// preprocessors ordered before it and still calls the ones after it once it throws, so asset
    /// changes those make can stay behind after the failed build. A build script (CI) should check
    /// the defines itself before calling BuildPlayer.
    /// </summary>
    internal sealed class KenseiDebugBuildGuard : IPreprocessBuildWithReport {
        // Early, so the error is raised before preprocessors with the default order 0 and is easy
        // to find in the log. It does not stop the later ones: Unity still calls them after the
        // exception (see the class remarks).
        public int callbackOrder => -1000;

        public void OnPreprocessBuild(BuildReport report) {
            var summary = report.summary;
            if ((summary.options & BuildOptions.Development) != 0) {
                return;
            }

            string profileName = null;
            string profilePath = null;
            string[] profileDefines = null;
            bool? overridesPlayerSettings = false;
#if UNITY_6000_0_OR_NEWER
            var profile = BuildProfile.GetActiveBuildProfile();
            if (profile != null) {
                profileName = profile.name;
                profilePath = AssetDatabase.GetAssetPath(profile);
                profileDefines = profile.scriptingDefines;
                overridesPlayerSettings = KenseiDebugToggle.ProfileOverridesPlayerSettings(profile);
            }
#endif
            var target = BuiltTarget(summary);
            var playerDefines = new List<string>();
            KenseiDebugToggle.CollectPlayerSettingsDefines(target, playerDefines);

            bool inProfile = profileDefines != null && Array.IndexOf(profileDefines, KenseiDebugDefines.DEFINE) >= 0;
            bool inPlayerSettings = playerDefines.Contains(KenseiDebugDefines.DEFINE);
            if (!inProfile && !inPlayerSettings) {
                return;
            }
            bool allowed = (profileDefines != null && Array.IndexOf(profileDefines, KenseiDebugDefines.ALLOW_DEFINE) >= 0) ||
                playerDefines.Contains(KenseiDebugDefines.ALLOW_DEFINE);
            if (allowed) {
                return;
            }

            throw new BuildFailedException(KenseiDebugDefines.ReleaseBuildError(
                profileName, profilePath, inProfile, target.TargetName, inPlayerSettings, overridesPlayerSettings));
        }

        private static NamedBuildTarget BuiltTarget(BuildSummary summary) {
#if UNITY_2023_1_OR_NEWER
            bool server = summary.platformGroup == BuildTargetGroup.Standalone &&
                summary.GetSubtarget<StandaloneBuildSubtarget>() == StandaloneBuildSubtarget.Server;
#else
            // Before 2023.1 the subtarget of the build is read from the build settings, which a
            // build of the active target uses.
            bool server = summary.platformGroup == BuildTargetGroup.Standalone &&
                EditorUserBuildSettings.standaloneBuildSubtarget == StandaloneBuildSubtarget.Server;
#endif
            return KenseiDebugToggle.ToNamedTarget(summary.platformGroup, server);
        }
    }
}
#endif
