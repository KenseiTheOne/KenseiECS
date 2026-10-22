using System.Collections.Generic;
using System.Text;

namespace KenseiECS.Editor {
    /// <summary>
    /// Define names and the messages of the Debug Mode toggle and the release build guard.
    /// Kept free of UnityEditor types so the wording and the target bookkeeping are covered
    /// by the .NET test project, which cannot load the editor assemblies.
    /// </summary>
    internal static class KenseiDebugDefines {
        /// <summary>The scripting define that compiles in KenseiECS validation and debug tooling.</summary>
        internal const string DEFINE = "KENSEI_DEBUG";

        /// <summary>Lets a non-development build with <see cref="DEFINE"/> through the build guard.</summary>
        internal const string ALLOW_DEFINE = "KENSEI_ALLOW_DEBUG_RELEASE";

        /// <summary>
        /// Error of the release build guard. Names every source the define comes from, so the
        /// reader knows which asset to edit: the Build Profile (by name and asset path) and/or the
        /// Player Settings of the built target.
        /// </summary>
        /// <param name="profileOverridesPlayerSettings">
        /// Whether the active profile has its own Player Settings ("Customize player settings"): then the
        /// Player Settings defines the build reads are the copy stored in the profile asset, not Project
        /// Settings. Null when it could not be determined; ignored without a profile.
        /// </param>
        internal static string ReleaseBuildError(string profileName, string profilePath, bool inProfile,
            string targetName, bool inPlayerSettings, bool? profileOverridesPlayerSettings = false) {
            var text = new StringBuilder(512);
            text.Append("KenseiECS: this release (non-development) build has the ").Append(DEFINE)
                .Append(" scripting define, which compiles validation and debug tooling into the player. It comes from ");
            if (inProfile) {
                text.Append("the Scripting Defines of Build Profile ").Append(DescribeProfile(profileName, profilePath));
                if (inPlayerSettings) {
                    text.Append(" and from ");
                }
            }
            if (inPlayerSettings || !inProfile) {
                AppendPlayerSettings(text, profileName, profilePath, profileOverridesPlayerSettings);
                text.Append(' ').Append(targetName);
            }
            text.Append(". Remove it there (KenseiECS -> Debug Mode turns it off for the active profile and target) or tick Development Build. ")
                .Append("To ship it on purpose and turn this check off, add ").Append(ALLOW_DEFINE).Append(" to the same defines.");
            return text.ToString();
        }

        /// <summary>
        /// One-line log after the toggle edits a Build Profile asset: the profile is usually shared
        /// through version control, so the change should not go unnoticed in the next commit.
        /// </summary>
        internal static string ProfileWritten(string profileName, string profilePath, bool enabled) =>
            "KenseiECS: Debug Mode " + (enabled ? "on: added " : "off: removed ") + DEFINE +
            (enabled ? " to" : " from") + " the Scripting Defines of Build Profile " +
            DescribeProfile(profileName, profilePath) + "; the asset is modified (commit or revert it).";

        /// <summary>
        /// One-line log after the toggle edited Player Settings defines that live in the active Build
        /// Profile asset (the profile overrides the project's Player Settings, so Unity redirects
        /// PlayerSettings.SetScriptingDefineSymbols to the copy in the profile and saves the asset).
        /// </summary>
        internal static string ProfilePlayerSettingsWritten(string profileName, string profilePath, string targetName,
            bool enabled) =>
            "KenseiECS: Debug Mode " + (enabled ? "on: added " : "off: removed ") + DEFINE +
            (enabled ? " to" : " from") + " the Player Settings (Scripting Define Symbols of " + targetName +
            ") stored in Build Profile " + DescribeProfile(profileName, profilePath) +
            ", which overrides the project's Player Settings; the asset is modified (commit or revert it).";

        /// <summary>
        /// Adds <paramref name="targetName"/> to <paramref name="targets"/> when its defines still
        /// contain <see cref="DEFINE"/>; a target already listed is not added twice.
        /// </summary>
        internal static void AddIfDefined(List<string> targets, string targetName, string[] defines) {
            if (defines == null || targets.Contains(targetName)) {
                return;
            }
            for (int i = 0; i < defines.Length; i++) {
                if (defines[i] == DEFINE) {
                    targets.Add(targetName);
                    return;
                }
            }
        }

        /// <summary>
        /// Warning after Debug Mode is turned off while other build targets still carry the define
        /// in their Player Settings (KenseiECS before 2.2 wrote it to every target). The toggle does
        /// not remove it there: those targets may have it on purpose.
        /// </summary>
        /// <param name="targets"> Names of the targets that still have the define. </param>
        /// <param name="profileName"> Active Build Profile, or null without one. </param>
        /// <param name="profilePath"> Asset path of the active profile. </param>
        /// <param name="profileOverridesPlayerSettings">
        /// Whether the active profile has its own Player Settings (null: unknown). While it does, Unity
        /// reads the profile's copy, so the targets were read from it and leftovers in the project's
        /// Player Settings could not be checked; the message says so.
        /// </param>
        internal static string OtherTargetsStillDefined(IReadOnlyList<string> targets, string profileName = null,
            string profilePath = null, bool? profileOverridesPlayerSettings = false) {
            var text = new StringBuilder(256);
            text.Append("KenseiECS: Debug Mode is off for the active target, but ").Append(DEFINE)
                .Append(" is still in ");
            AppendPlayerSettings(text, profileName, profilePath, profileOverridesPlayerSettings);
            text.Append(": ");
            for (int i = 0; i < targets.Count; i++) {
                if (i > 0) {
                    text.Append(", ");
                }
                text.Append(targets[i]);
            }
            text.Append(". Debug Mode changes only the active profile/target; remove it there by hand if those builds should not have it ")
                .Append("(versions before 2.2 wrote it to every target).");
            if (profileName != null && profileOverridesPlayerSettings != false) {
                text.Append(profileOverridesPlayerSettings == true
                    ? " While this profile is active Unity reads its own copy of Player Settings, so the project's Player Settings (ProjectSettings.asset) were not checked."
                    : " If this profile overrides Player Settings, the list comes from its copy and the project's Player Settings (ProjectSettings.asset) were not checked.");
            }
            return text.ToString();
        }

        // "Player Settings -> Scripting Define Symbols of" naming where those Player Settings live:
        // with a profile that overrides them, a PlayerSettings call reads and writes the copy inside
        // the profile asset, and the reader would not find the define in Project Settings.
        private static void AppendPlayerSettings(StringBuilder text, string profileName, string profilePath,
            bool? profileOverrides) {
            if (profileName == null || profileOverrides == false) {
                text.Append("Player Settings");
            } else if (profileOverrides == true) {
                text.Append("the Player Settings stored in Build Profile ").Append(DescribeProfile(profileName, profilePath))
                    .Append(" (it overrides Project Settings)");
            } else {
                text.Append("Player Settings (stored in Build Profile ").Append(DescribeProfile(profileName, profilePath))
                    .Append(" if it overrides them, otherwise in Project Settings)");
            }
            text.Append(" -> Scripting Define Symbols of");
        }

        private static string DescribeProfile(string name, string path) =>
            string.IsNullOrEmpty(path) ? "'" + name + "'" : "'" + name + "' (" + path + ")";
    }
}
