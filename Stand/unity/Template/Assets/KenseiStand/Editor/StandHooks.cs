// KenseiECS test stand, module unity: build callbacks. A player build of a case stops right after the
// player scripts compiled (IPostBuildPlayerScriptDLLs throws a marker exception): everything KenseiECS
// can break happens up to there, and the rest of a build (packing, IL2CPP, Gradle) costs minutes.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace KenseiStand.Editor {
    internal static class StandHooks {
        public const string AbortMarker = "KENSEI-STAND-ABORT: player scripts compiled, the stand stops the build here on purpose";

        public static bool Armed;
        public static bool AbortAfterScripts = true;
        public static bool ScriptsCompiled;
        public static bool PreprocessCalled;
        // Set by the first IPostBuildPlayerScriptDLLs callback (before any of KenseiECS): a KenseiECS stop
        // after this point comes from a check after the script compile, not from the preprocess guard.
        public static bool PostCompileReached;
        public static string PreprocessOptions = "";
        public static List<string> Markers = new List<string>();
        public static string KenseiDebugCode = "n/a";
        public static string KenseiDll = "";

        public static void Arm() {
            Armed = true;
            ScriptsCompiled = false;
            PreprocessCalled = false;
            PostCompileReached = false;
            PreprocessOptions = "";
            Markers = new List<string>();
            KenseiDebugCode = "n/a";
            KenseiDll = "";
        }

        public static void Disarm() {
            Armed = false;
        }
    }

    // Before KenseiDebugBuildGuard (-1000): records that the preprocess step ran and with which options.
    internal sealed class StandPreprocessProbe : IPreprocessBuildWithReport {
        public int callbackOrder => -2000;

        public void OnPreprocessBuild(BuildReport report) {
            if (!StandHooks.Armed) {
                return;
            }
            StandHooks.PreprocessCalled = true;
            StandHooks.PreprocessOptions = StandText.Flags(report.summary.options);
            StandLog.Info("preprocess: platform=" + report.summary.platform + " options=" + StandHooks.PreprocessOptions);
        }
    }

    // First of all IPostBuildPlayerScriptDLLs callbacks: marks that the player scripts compiled, before a
    // check of KenseiECS (2.2.1: KenseiDebugPlayerScriptsCheck, order -1000) can throw. Unity may not call
    // the later callbacks once one throws, so the abort hook below cannot be relied on for that.
    internal sealed class StandPostCompileProbe : IPostBuildPlayerScriptDLLs {
        public int callbackOrder => int.MinValue + 1;

        public void OnPostBuildPlayerScriptDLLs(BuildReport report) {
            if (!StandHooks.Armed) {
                return;
            }
            StandHooks.PostCompileReached = true;
            StandLog.Info("player scripts compiled (first post-compile callback)");
        }
    }

    internal sealed class StandAbortAfterScripts : IPostBuildPlayerScriptDLLs {
        public int callbackOrder => int.MaxValue - 1;

        public void OnPostBuildPlayerScriptDLLs(BuildReport report) {
            if (!StandHooks.Armed) {
                return;
            }
            StandHooks.ScriptsCompiled = true;
            try {
                var files = report.GetFiles().Select(f => f.path).ToArray();
                string markers = files.FirstOrDefault(p => Path.GetFileName(p) == StandDll.MarkerAssembly);
                string kensei = files.FirstOrDefault(p => Path.GetFileName(p) == StandDll.KenseiAssembly);
                StandHooks.Markers = StandDll.Markers(markers);
                StandHooks.KenseiDll = kensei ?? "";
                StandHooks.KenseiDebugCode = StandDll.KenseiDebugCode(kensei);
                StandLog.Info("player scripts compiled: markers=" + StandText.Join(StandHooks.Markers) +
                    " KenseiECS.dll debug code=" + StandHooks.KenseiDebugCode);
            } catch (Exception e) {
                StandLog.Info("could not read the compiled DLLs: " + e.Message);
            }
            if (StandHooks.AbortAfterScripts) {
                throw new BuildFailedException(StandHooks.AbortMarker);
            }
        }
    }
}
