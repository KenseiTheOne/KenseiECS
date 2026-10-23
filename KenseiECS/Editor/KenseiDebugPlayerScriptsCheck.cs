#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace KenseiECS.Editor {
    /// <summary>
    /// Fails a non-development player build whose compiled KenseiECS.dll has KENSEI_DEBUG, wherever the
    /// define came from. BuildPlayerOptions.extraScriptingDefines, Player Settings -> Additional Compiler
    /// Arguments and an Assets/csc.rsp reach the compile but not the preprocess guard
    /// (<see cref="KenseiDebugBuildGuard"/>), which reads the Scripting Define Symbols of the Build Profile
    /// and Player Settings only. Such a compile contains a marker type (DevTools/KenseiDebugReleaseCheck.cs);
    /// this check looks for it in the DLL. Development builds are recognised by the build options, as in
    /// the guard.
    ///
    /// Unity calls IPostBuildPlayerScriptDLLs for player builds only. Content builds (Addressables,
    /// the Scriptable Build Pipeline, AssetBundles) compile the same player scripts but are not checked,
    /// so building content with Debug Mode on works. A define from the Scripting Define Symbols is
    /// reported earlier, by the guard; this check comes after the script compile, before the player is
    /// packed and before IL2CPP.
    /// </summary>
    internal sealed class KenseiDebugPlayerScriptsCheck : IPostBuildPlayerScriptDLLs {
        private const string DllName = "KenseiECS.dll";

        public int callbackOrder => -1000;

        public void OnPostBuildPlayerScriptDLLs(BuildReport report) {
            // The defines this build was given; read once, so a later build cannot see them.
            var extraDefines = KenseiDebugBuildOptionsCapture.Take();
            if ((report.summary.options & BuildOptions.Development) != 0) {
                return;
            }
            string dll = FindKenseiDll(report);
            if (dll == null) {
                Debug.LogWarning("KenseiECS: could not find the compiled " + DllName + " of this release player build, " +
                    "so it was not checked for " + KenseiDebugDefines.DEFINE + " from BuildPlayerOptions.extraScriptingDefines, " +
                    "Additional Compiler Arguments or csc.rsp (the Scripting Define Symbols were checked before the build).");
                return;
            }
            byte[] bytes;
            try {
                bytes = File.ReadAllBytes(dll);
            } catch (IOException e) {
                Debug.LogWarning("KenseiECS: could not read " + dll + " to check it for " + KenseiDebugDefines.DEFINE + ": " + e.Message);
                return;
            }
            if (KenseiDebugDefines.ContainsReleaseMarker(bytes)) {
                throw new BuildFailedException(KenseiDebugDefines.CompiledReleaseError(DefineSources(report, extraDefines)));
            }
        }

        // The sources the preprocess guard does not read, where this build has the define. Only the
        // wording of the error depends on them: the marker in the DLL already decided.
        private static List<string> DefineSources(BuildReport report, string[] extraDefines) {
            var sources = new List<string>();
            if (extraDefines != null && Array.IndexOf(extraDefines, KenseiDebugDefines.DEFINE) >= 0) {
                sources.Add("BuildPlayerOptions.extraScriptingDefines");
            }
            var target = KenseiDebugBuildGuard.BuiltTarget(report.summary);
            if (KenseiDebugDefines.ArgumentsDefine(PlayerSettings.GetAdditionalCompilerArguments(target))) {
                sources.Add("Player Settings -> Additional Compiler Arguments of " + target.TargetName);
            }
            const string rsp = "Assets/csc.rsp";
            try {
                if (File.Exists(rsp) && KenseiDebugDefines.ArgumentsDefine(File.ReadAllLines(rsp))) {
                    sources.Add(rsp);
                }
            } catch (IOException) {
                // Unreadable: the error then lists every source the guard does not read.
            }
            return sources;
        }

        // The build report lists the compiled script assemblies at this point. The staging folder is
        // the fallback (Unity 2021.3 reads the assemblies from there): it is rebuilt for every player
        // build, so it cannot hold the DLL of an earlier build or of a content build
        // (Library/PlayerScriptAssemblies could).
        private static string FindKenseiDll(BuildReport report) {
#if UNITY_2022_1_OR_NEWER
            var files = report.GetFiles();
#else
            var files = report.files;
#endif
            foreach (var file in files) {
                if (string.Equals(Path.GetFileName(file.path), DllName, StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(file.path)) {
                    return file.path;
                }
            }
            const string staging = "Temp/StagingArea";
            if (Directory.Exists(staging)) {
                foreach (var path in Directory.EnumerateFiles(staging, DllName, SearchOption.AllDirectories)) {
                    return path;
                }
            }
            return null;
        }
    }

    /// <summary>
    /// Keeps BuildPlayerOptions.extraScriptingDefines of the player build being made for the error of
    /// <see cref="KenseiDebugPlayerScriptsCheck"/>: a BuildPlayerProcessor is the only callback that sees
    /// them. Runs before Addressables' processor (order 1), which builds content in the same step.
    /// </summary>
    internal sealed class KenseiDebugBuildOptionsCapture : BuildPlayerProcessor {
        private static string[] _extraScriptingDefines;

        public override int callbackOrder => -1000;

        public override void PrepareForBuild(BuildPlayerContext buildPlayerContext) {
            _extraScriptingDefines = buildPlayerContext.BuildPlayerOptions.extraScriptingDefines;
        }

        // The captured defines, cleared so a build that skips PrepareForBuild cannot report stale ones.
        internal static string[] Take() {
            var defines = _extraScriptingDefines;
            _extraScriptingDefines = null;
            return defines;
        }
    }
}
#endif
