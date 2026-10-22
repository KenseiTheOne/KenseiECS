using System.Collections.Generic;
using KenseiECS.Editor;
using NUnit.Framework;

namespace KenseiECS.Tests {
    // The Debug Mode toggle and the release build guard live in the editor assembly, which the
    // .NET tests cannot load; their messages and the leftover-target bookkeeping are in the
    // Unity-free KenseiDebugDefines and are checked here.
    [TestFixture]
    public class V220EditorMessagesTests {
        private const string ProfileName = "Android Release";
        private const string ProfilePath = "Assets/Settings/Build Profiles/Android Release.asset";

        [Test]
        public void GuardError_DefineInProfile_NamesProfileAndAsset() {
            string text = KenseiDebugDefines.ReleaseBuildError(ProfileName, ProfilePath, true, "Android", false);

            StringAssert.Contains("Build Profile 'Android Release' (" + ProfilePath + ")", text);
            StringAssert.DoesNotContain("Player Settings -> Scripting Define Symbols of", text);
            StringAssert.Contains(KenseiDebugDefines.DEFINE, text);
        }

        [Test]
        public void GuardError_DefineInPlayerSettings_NamesTarget() {
            string text = KenseiDebugDefines.ReleaseBuildError(ProfileName, ProfilePath, false, "Android", true);

            StringAssert.Contains("Player Settings -> Scripting Define Symbols of Android", text);
            StringAssert.DoesNotContain("Build Profile '", text);
        }

        [Test]
        public void GuardError_DefineInBoth_NamesBothSources() {
            string text = KenseiDebugDefines.ReleaseBuildError(ProfileName, ProfilePath, true, "Server", true);

            StringAssert.Contains("Build Profile 'Android Release'", text);
            StringAssert.Contains(" and from Player Settings -> Scripting Define Symbols of Server", text);
        }

        [Test]
        public void GuardError_NoProfile_NamesPlayerSettings() {
            string text = KenseiDebugDefines.ReleaseBuildError(null, null, false, "Standalone", true);

            StringAssert.Contains("of Standalone", text);
            StringAssert.DoesNotContain("Build Profile '", text);
        }

        [Test]
        public void GuardError_TellsHowToAllowAndHowToFix() {
            string text = KenseiDebugDefines.ReleaseBuildError(ProfileName, ProfilePath, true, "Android", false);

            StringAssert.Contains("add " + KenseiDebugDefines.ALLOW_DEFINE, text);
            StringAssert.Contains("Development Build", text);
            StringAssert.Contains("KenseiECS -> Debug Mode", text);
        }

        [Test]
        public void ProfileWritten_IsOneLineWithNameAndPath() {
            string on = KenseiDebugDefines.ProfileWritten("Android Dev", "Assets/Android Dev.asset", true);
            string off = KenseiDebugDefines.ProfileWritten("Android Dev", "Assets/Android Dev.asset", false);

            StringAssert.Contains("added KENSEI_DEBUG to the Scripting Defines of Build Profile 'Android Dev' (Assets/Android Dev.asset)", on);
            StringAssert.Contains("removed KENSEI_DEBUG from the Scripting Defines", off);
            StringAssert.DoesNotContain("\n", on);
            StringAssert.DoesNotContain("\n", off);
        }

        [Test]
        public void ProfileWritten_WithoutPath_ShowsName() {
            string text = KenseiDebugDefines.ProfileWritten("Dev", "", true);

            StringAssert.Contains("Build Profile 'Dev';", text);
        }

        [Test]
        public void AddIfDefined_AddsOnlyTargetsWithExactDefineOnce() {
            var targets = new List<string>();

            KenseiDebugDefines.AddIfDefined(targets, "Standalone", new[] { "FOO", "KENSEI_DEBUG" });
            KenseiDebugDefines.AddIfDefined(targets, "Standalone", new[] { "KENSEI_DEBUG" });
            KenseiDebugDefines.AddIfDefined(targets, "Android", new[] { "KENSEI_DEBUG_EXTRA", "KENSEI_ALLOW_DEBUG_RELEASE" });
            KenseiDebugDefines.AddIfDefined(targets, "iOS", new string[0]);
            KenseiDebugDefines.AddIfDefined(targets, "WebGL", null);
            KenseiDebugDefines.AddIfDefined(targets, "Server", new[] { "KENSEI_DEBUG" });

            CollectionAssert.AreEqual(new[] { "Standalone", "Server" }, targets);
        }

        [Test]
        public void OtherTargetsWarning_ListsTargetsAndDoesNotPromiseRemoval() {
            string text = KenseiDebugDefines.OtherTargetsStillDefined(new[] { "Standalone", "Server" });

            StringAssert.Contains("Scripting Define Symbols of: Standalone, Server.", text);
            StringAssert.Contains("remove it there by hand", text);
        }

        [Test]
        public void OtherTargetsWarning_ProjectSettings_SaysNothingAboutProfiles() {
            string text = KenseiDebugDefines.OtherTargetsStillDefined(new[] { "Standalone" }, ProfileName, ProfilePath, false);

            StringAssert.Contains("is still in Player Settings -> Scripting Define Symbols of: Standalone.", text);
            StringAssert.DoesNotContain("ProjectSettings.asset", text);
        }

        [Test]
        public void OtherTargetsWarning_ProfileOverridesPlayerSettings_NamesCopyAndUncheckedProject() {
            string text = KenseiDebugDefines.OtherTargetsStillDefined(new[] { "Standalone" }, ProfileName, ProfilePath, true);

            StringAssert.Contains("the Player Settings stored in Build Profile 'Android Release' (" + ProfilePath + ")", text);
            StringAssert.Contains("Scripting Define Symbols of: Standalone.", text);
            StringAssert.Contains("the project's Player Settings (ProjectSettings.asset) were not checked", text);
        }

        [Test]
        public void OtherTargetsWarning_OverrideUnknown_Hedges() {
            string text = KenseiDebugDefines.OtherTargetsStillDefined(new[] { "Standalone" }, ProfileName, ProfilePath, null);

            StringAssert.Contains("if it overrides them, otherwise in Project Settings", text);
            StringAssert.Contains("If this profile overrides Player Settings", text);
        }

        [Test]
        public void GuardError_ProfileOverridesPlayerSettings_PointsAtProfileCopy() {
            string text = KenseiDebugDefines.ReleaseBuildError(ProfileName, ProfilePath, false, "Android", true, true);

            StringAssert.Contains("the Player Settings stored in Build Profile 'Android Release' (" + ProfilePath + ") (it overrides Project Settings) -> Scripting Define Symbols of Android", text);
        }

        [Test]
        public void GuardError_OverrideUnknown_NamesBothPlaces() {
            string text = KenseiDebugDefines.ReleaseBuildError(ProfileName, ProfilePath, false, "Android", true, null);

            StringAssert.Contains("Player Settings (stored in Build Profile 'Android Release' (" + ProfilePath + ") if it overrides them, otherwise in Project Settings) -> Scripting Define Symbols of Android", text);
        }

        [Test]
        public void GuardError_NoProfile_IgnoresOverrideFlag() {
            string text = KenseiDebugDefines.ReleaseBuildError(null, null, false, "Android", true, null);

            StringAssert.Contains("from Player Settings -> Scripting Define Symbols of Android.", text);
        }

        [Test]
        public void ProfilePlayerSettingsWritten_IsOneLineWithTargetNameAndPath() {
            string off = KenseiDebugDefines.ProfilePlayerSettingsWritten("Android Dev", "Assets/Android Dev.asset", "Android", false);

            StringAssert.Contains("removed KENSEI_DEBUG from the Player Settings (Scripting Define Symbols of Android) stored in Build Profile 'Android Dev' (Assets/Android Dev.asset)", off);
            StringAssert.Contains("commit or revert it", off);
            StringAssert.DoesNotContain("\n", off);
        }
    }
}
