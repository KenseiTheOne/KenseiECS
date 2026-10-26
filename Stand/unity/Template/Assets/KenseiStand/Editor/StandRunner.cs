// KenseiECS test stand, module unity: -executeMethod entry points.
//
//   KenseiStand.Editor.StandRunner.Setup        baseline of the scratch project (rel: Debug Mode off,
//                                               dbg: Debug Mode on through the menu), scene, content asset,
//                                               Addressables settings. Run as its own Unity process so the
//                                               next process compiles the editor with the baseline defines.
//   KenseiStand.Editor.StandRunner.RunGroup     the cases of one group (-standGroup), each from the same
//                                               snapshot, results to -standOut/cases/<id>.json and .log.
//   KenseiStand.Editor.StandRunner.VerifyRepros runs the C# repro of each build finding (report.py's plan).
//
// Arguments: -standOut <dir> -standBaseline rel|dbg -standGroup <g> -standTier quick|full
//            -standOnly <id;id> -standCasesFile <cases.json> -standBuilds <dir> -standBackend il2cpp|mono
// Stand/unity/run.sh passes them; it is the supported way to run this.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
#if UNITY_6000_0_OR_NEWER
using UnityEditor.Build.Profile;
#endif
#if STAND_AA
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
#endif

namespace KenseiStand.Editor {
    public static class StandRunner {
        internal static string OutDir => StandArgs.Get("-standOut", Path.GetFullPath("Temp/kensei-stand"));
        internal static string BuildsDir => StandArgs.Get("-standBuilds", Path.GetFullPath("Temp/kensei-stand-builds"));

        // The define sources a stop message can name; the last three are the ones the guard does not read.
        internal static readonly string[] KnownSources = { "Build Profile", "Player Settings", "extraScriptingDefines", "Additional Compiler Arguments", "csc.rsp" };
        private static readonly string[] CompileOnlySources = { "extraScriptingDefines", "Additional Compiler Arguments", "csc.rsp" };

        // ---------------------------------------------------------------- setup

        public static void Setup() {
            var sw = Stopwatch.StartNew();
            var info = new RunInfo { entry = "setup", started = StandText.Now() };
            StandLog.Hook();
            StandLog.Info("setup: begin");
            try {
                Describe(info);
                string baseline = StandArgs.Get("-standBaseline", "rel");
                info.baseline = baseline;

#if UNITY_6000_0_OR_NEWER
                if (BuildProfile.GetActiveBuildProfile() != null) {
                    info.notes.Add("Build Profile '" + BuildProfile.GetActiveBuildProfile().name + "' was active, deactivated");
                    BuildProfile.SetActiveBuildProfile(null);
                }
                if (AssetDatabase.IsValidFolder(StandPaths.Profiles)) {
                    AssetDatabase.DeleteAsset(StandPaths.Profiles);
                }
#endif
                StandRsp.Remove();

                EditorUserBuildSettings.development = false;
                EditorUserBuildSettings.selectedBuildTargetGroup = StandDefines.ActiveGroup;
                string backend = StandArgs.Get("-standBackend", "il2cpp");
                PlayerSettings.SetScriptingBackend(StandDefines.Active,
                    backend == "mono" ? ScriptingImplementation.Mono2x : ScriptingImplementation.IL2CPP);
                if (StandDefines.ActiveTarget == BuildTarget.Android) {
                    EditorUserBuildSettings.exportAsGoogleAndroidProject = true;
                    PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
                    PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "com.kensei.stand");
                }
                info.backend = PlayerSettings.GetScriptingBackend(StandDefines.Active).ToString();

                EnsureDataAsset(info);
                EnsureScene(info);
#if STAND_AA
                EnsureAddressables(info);
#endif
                // Baseline defines: none of the KenseiECS defines anywhere, then Debug Mode through the menu for dbg.
                var kenseiArgs = StandCompilerArgs.Defines(new[] { StandDefines.DebugDefine, StandDefines.AllowDefine });
                foreach (var target in StandDefines.Known) {
                    try {
                        StandDefines.Set(target, StandDefines.Get(target).Where(d => d != StandDefines.DebugDefine && d != StandDefines.AllowDefine));
                        StandCompilerArgs.Set(target, StandCompilerArgs.Get(target).Where(a => !kenseiArgs.Contains(a)));
                    } catch (Exception e) {
                        info.notes.Add("Player Settings " + target.TargetName + ": " + e.Message);
                    }
                }
                if (baseline == "dbg") {
                    try {
                        info.toggleWrote = StandToggle.Ensure(true);
                    } catch (Exception e) {
                        info.ok = false;
                        info.notes.Add("Debug Mode did not turn on through the menu: " + e.Message + "; KENSEI_DEBUG written to the Player Settings of the active target directly");
                        StandDefines.Set(StandDefines.Active, StandDefines.Get(StandDefines.Active).Concat(new[] { StandDefines.DebugDefine }));
                    }
                }
                info.psBaseline = StandSnapshot.Take().Baseline();
                AssetDatabase.SaveAssets();
            } catch (Exception e) {
                info.ok = false;
                info.notes.Add("setup: exception " + e);
            }
            info.durationSec = (float)sw.Elapsed.TotalSeconds;
            StandText.WriteAllText(Path.Combine(OutDir, "setup.json"), JsonUtility.ToJson(info, true));
            StandLog.Info("setup: end ok=" + info.ok);
        }

        private static void EnsureDataAsset(RunInfo info) {
            if (AssetDatabase.LoadAssetAtPath<ScriptableObject>(StandPaths.Data) != null) {
                return;
            }
            var type = StandTypes.Find("KenseiStand.StandData");
            if (type == null) {
                info.ok = false;
                info.notes.Add("no type KenseiStand.StandData (assembly KenseiStand.Defines not compiled?)");
                return;
            }
            AssetDatabase.CreateAsset(ScriptableObject.CreateInstance(type), StandPaths.Data);
        }

        private static void EnsureScene(RunInfo info) {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var go = new GameObject("StandGame");
            // EcsBootstrap subclass first: its Awake creates the world that StandGame drives in the player.
            foreach (var name in new[] { "KenseiStand.StandBootstrap", "KenseiStand.StandGame" }) {
                var type = StandTypes.Find(name);
                if (type != null) {
                    go.AddComponent(type);
                } else {
                    info.ok = false;
                    info.notes.Add("no type " + name + " (Assembly-CSharp not compiled?): the scene goes without it");
                }
            }
            EditorSceneManager.SaveScene(scene, StandPaths.Scene);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(StandPaths.Scene, true) };

            // The control scene of the player runs: no KenseiECS component (StandCases.PlayerRun).
            var control = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var cgo = new GameObject("StandControl");
            var controlType = StandTypes.Find("KenseiStand.StandControl");
            if (controlType != null) {
                cgo.AddComponent(controlType);
            } else {
                info.notes.Add("no type KenseiStand.StandControl: no control scene for the player runs");
            }
            EditorSceneManager.SaveScene(control, StandPaths.ControlScene);
        }

#if STAND_AA
        private static void EnsureAddressables(RunInfo info) {
            var settings = AddressableAssetSettingsDefaultObject.GetSettings(true);
            if (settings == null) {
                info.ok = false;
                info.notes.Add("AddressableAssetSettings were not created");
                return;
            }
            string guid = AssetDatabase.AssetPathToGUID(StandPaths.Data);
            if (!string.IsNullOrEmpty(guid) && settings.FindAssetEntry(guid) == null) {
                settings.CreateOrMoveEntry(guid, settings.DefaultGroup);
            }
            // Baseline: player builds do not build Addressables; the aa cases switch it on.
            settings.BuildAddressablesWithPlayerBuild = AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer;
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
        }
#endif

        // ---------------------------------------------------------------- groups

        public static void RunGroup() {
            var sw = Stopwatch.StartNew();
            string group = StandArgs.Get("-standGroup");
            var info = new RunInfo { entry = "group", group = group, started = StandText.Now() };
            StandLog.Hook();
            StandLog.Info("group " + group + ": begin");
            try {
                Describe(info);
                info.baseline = StandArgs.Get("-standBaseline", "rel");
                var file = JsonUtility.FromJson<CaseFile>(File.ReadAllText(StandArgs.Get("-standCasesFile")));
                var only = new HashSet<string>(StandText.Split(StandArgs.Get("-standOnly")));
                string tier = StandArgs.Get("-standTier", "quick");
                var cases = file.cases.Where(c => c.group == group &&
                    (only.Count > 0 ? only.Contains(c.id) : tier == "full" || c.tier == "quick")).ToList();
                var snapshot = StandSnapshot.Take();
                info.psBaseline = snapshot.Baseline();
                StandLog.Info("group " + group + ": " + cases.Count + " case(s), baseline " + snapshot.Describe());
                foreach (var c in cases) {
                    var result = RunCase(c, info, snapshot);
                    info.casesRun.Add(c.id);
                    StandText.WriteAllText(Path.Combine(OutDir, "cases", c.id + ".json"), JsonUtility.ToJson(result, true));
                }
            } catch (Exception e) {
                info.ok = false;
                info.notes.Add("group: exception " + e);
            }
            info.durationSec = (float)sw.Elapsed.TotalSeconds;
            StandText.WriteAllText(Path.Combine(OutDir, "groups", group + ".json"), JsonUtility.ToJson(info, true));
            StandLog.Info("group " + group + ": end");
        }

        // Capabilities that come from the contract of the package version (not applicable: "na"); every other
        // missing capability is the environment (a module, package or Unity API that is not there: "skipped").
        private static readonly HashSet<string> VersionCaps = new HashSet<string> { "guard", "releaseCheck", "toggle22", "toggle21" };

        private static CaseResult RunCase(CaseDef c, RunInfo info, StandSnapshot snapshot) {
            var r = new CaseResult {
                id = c.id, group = c.group, kind = c.kind, title = c.title, rule = c.rule, tier = c.tier,
                expected = c.expect, started = StandText.Now(), log = "cases/" + c.id + ".log"
            };
            var sw = Stopwatch.StartNew();
            StandLog.Info("BEGIN case " + c.id + " (" + c.kind + "): " + c.title);
            StandLog.Begin();
            var missing = StandText.Split(c.needs.Replace(',', ';')).Where(n => !info.capabilities.Contains(n)).ToList();
            if (missing.Count > 0) {
                r.verdict = missing.Any(VersionCaps.Contains) ? "na" : "skipped";
                r.actual = "n/a";
                r.naReason = NaReason(missing, info);
            } else {
                try {
                    StandApply.Apply(c, r);
                    StandCases.Run(c, r, info);
                    if (string.IsNullOrEmpty(r.verdict)) {
                        Judge(c, r, info);
                    }
                } catch (StandSkipped e) {
                    r.verdict = "skipped";
                    r.actual = "n/a";
                    r.naReason = e.Message;
                } catch (StandInconclusive e) {
                    r.verdict = "inconclusive";
                    r.reason = e.Message;
                } catch (Exception e) {
                    r.verdict = "inconclusive";
                    r.reason = "exception of the stand: " + StandText.Trim(e.ToString(), 1500);
                } finally {
                    StandHooks.Disarm();
                    try {
                        snapshot.Restore();
                    } catch (Exception e) {
                        r.questions.Add("stand: could not put the project state back: " + e.Message);
                    }
                }
            }
            var entries = StandLog.End();
            r.durationSec = (float)sw.Elapsed.TotalSeconds;
            StandText.WriteAllText(Path.Combine(OutDir, "cases", c.id + ".log"),
                "case " + c.id + " (" + c.kind + "): " + c.title + "\nverdict: " + r.verdict + " expected=" + r.expected + " actual=" + r.actual +
                (r.reason.Length > 0 ? "\nreason: " + r.reason : "") + (r.naReason.Length > 0 ? "\nnot run: " + r.naReason : "") + "\n\n" + StandLog.Format(entries));
            StandLog.Info("END case " + c.id + ": " + r.verdict + " (expected " + r.expected + ", actual " + r.actual + ") " +
                r.durationSec.ToString("F1") + "s");
            return r;
        }

        // ---------------------------------------------------------------- repro verification

        /// <summary>
        /// Runs the C# repro of each finding (raw/repro-plan.json, the files copied by run.sh to
        /// Assets/KenseiStandRepro/Editor/ before this process started) the way its header says, from the
        /// baseline of the project, and classifies what it did like a case. A repro that does not
        /// reproduce the case is reported, so a finding never ships with a repro that does not show it.
        /// Player builds stop after the script compile here too (the stand's hook is armed).
        /// </summary>
        public static void VerifyRepros() {
            StandLog.Hook();
            StandLog.Info("verify: begin");
            var info = new RunInfo { entry = "verify", started = StandText.Now() };
            Describe(info);
            ReproPlan plan;
            try {
                plan = JsonUtility.FromJson<ReproPlan>(File.ReadAllText(StandArgs.Get("-standReproPlan")));
            } catch (Exception e) {
                StandLog.Info("verify: no plan: " + e.Message);
                return;
            }
            var snapshot = StandSnapshot.Take();
            foreach (var item in plan.items) {
                var v = new ReproVerdict { finding = item.finding, type = item.type, caseId = item.caseId, expectActual = item.expectActual };
                var sw = Stopwatch.StartNew();
                StandLog.Info("BEGIN repro " + item.finding + " (" + item.type + ")");
                StandLog.Begin();
                var r = new CaseResult { id = item.caseId, kind = item.kind };
                try {
                    var run = StandTypes.Find(item.type)?.GetMethod("Run", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (run == null) {
                        v.actual = "no-repro";
                        v.reason = "type " + item.type + " not found: the repro file did not compile (errors in the log of the verify process)";
                    } else {
                        StandHooks.Arm();
                        try {
                            run.Invoke(null, null);
                        } catch (System.Reflection.TargetInvocationException e) {
                            r.evidence.exception = StandText.Trim((e.InnerException ?? e).ToString(), 1500);
                        } finally {
                            StandHooks.Disarm();
                        }
                        var entries = StandLog.Since(0);
                        v.reproLines = entries.Where(e => e.text.Contains("[REPRO]")).Select(e => StandText.Trim(e.text, 600)).ToList();
                        r.evidence.scriptsCompiled = StandHooks.ScriptsCompiled;
                        r.evidence.postCompileReached = StandHooks.PostCompileReached;
                        r.evidence.markers = StandHooks.Markers;
                        r.evidence.kenseiDebugCode = StandHooks.KenseiDebugCode;
                        StandCases.ScanErrors(entries, r, info);
                        if (item.kind == "editor-spawn") {
                            // A runtime repro: "[REPRO] <id> ... defect=True|False"; the case of a finding failed.
                            bool defect = v.reproLines.Any(l => l.Contains(" defect=True"));
                            bool clean = v.reproLines.Any(l => l.Contains(" defect=False"));
                            v.actual = defect ? "fail" : clean ? "pass" : r.evidence.exception.Length > 0 ? "repro-exception" : "no-result";
                        } else {
                            // The repro's own verdict: "[REPRO] <id> success=True|False" (a player build stopped by the
                            // stand after the compile counts as compiled).
                            bool success = v.reproLines.Any(l => l.Contains(" success=True")) || (item.kind == "player" && StandHooks.ScriptsCompiled);
                            v.actual = v.reproLines.Count == 0 && r.evidence.exception.Length > 0 && !success
                                ? "repro-exception"
                                : StandCases.Classify(success, r);
                        }
                        v.reproduced = v.actual == item.expectActual;
                        if (!v.reproduced) {
                            v.reason = "the repro gave " + v.actual + ", case " + item.caseId + " gave " + item.expectActual +
                                (r.evidence.exception.Length > 0 ? "; exception: " + StandText.Trim(r.evidence.exception, 400) : "");
                        }
                    }
                } catch (Exception e) {
                    v.actual = "stand-error";
                    v.reason = "exception of the stand: " + StandText.Trim(e.ToString(), 800);
                } finally {
                    try {
                        snapshot.Restore();
                        foreach (var dir in new[] { "Builds/Repro", "Temp/ReproScripts", "Temp/ReproBundles", "Temp/ReproSbp" }) {
                            if (Directory.Exists(dir)) {
                                Directory.Delete(dir, true);
                            }
                        }
                        if (File.Exists("Assets/ReproData.txt")) {
                            AssetDatabase.DeleteAsset("Assets/ReproData.txt");
                        }
                        // Build Profiles a repro made (MakeProfile: Assets/Repro_<case>.asset).
                        foreach (var asset in Directory.GetFiles("Assets", "Repro_*.asset")) {
                            AssetDatabase.DeleteAsset(asset.Replace('\\', '/'));
                        }
                        if (Directory.Exists("Builds") && !Directory.EnumerateFileSystemEntries("Builds").Any()) {
                            Directory.Delete("Builds");
                        }
                    } catch (Exception e) {
                        v.reason += (v.reason.Length > 0 ? "; " : "") + "stand: could not put the project state back: " + e.Message;
                    }
                }
                v.evidence = r.evidence;
                var log = StandLog.End();
                v.durationSec = (float)sw.Elapsed.TotalSeconds;
                string safe = Regex.Replace(item.finding, @"[^A-Za-z0-9._-]+", "_");
                StandText.WriteAllText(Path.Combine(OutDir, "repro-verify", safe + ".json"), JsonUtility.ToJson(v, true));
                StandText.WriteAllText(Path.Combine(OutDir, "repro-verify", safe + ".log"),
                    "repro " + item.finding + " (" + item.type + ", case " + item.caseId + ")\nactual: " + v.actual + " expected: " + item.expectActual +
                    " reproduced: " + v.reproduced + (v.reason.Length > 0 ? "\nreason: " + v.reason : "") + "\n\n" + StandLog.Format(log));
                StandLog.Info("END repro " + item.finding + ": " + v.actual + " (case " + item.expectActual + ") reproduced=" + v.reproduced);
            }
            StandLog.Info("verify: end");
        }

        private static string NaReason(List<string> missing, RunInfo info) {
            var parts = new List<string>();
            foreach (var m in missing) {
                switch (m) {
                    case "guard":
                    case "releaseCheck":
                    case "toggle22":
                        parts.Add("not in this version: the " + m + " contract exists since 2.2.0, the package is " + info.packageVersion);
                        break;
                    case "toggle21":
                        parts.Add("the Debug Mode contract of 2.1.x (every target) does not apply to " + info.packageVersion);
                        break;
                    case "aa":
                        parts.Add("Addressables are not installed (--addressables none)");
                        break;
                    case "sbp":
                        parts.Add("no com.unity.scriptablebuildpipeline");
                        break;
                    case "profiles":
                        parts.Add("no Build Profiles (Unity before 6, or the internal API is missing)");
                        break;
                    case "osxPlayer":
                        parts.Add("no macOS build module, or the editor does not run on macOS (the stand starts the player)");
                        break;
                    case "il2cppMac":
                        parts.Add("no IL2CPP for macOS (module Mac Build Support (IL2CPP))");
                        break;
                    default:
                        parts.Add("capability " + m + " is missing");
                        break;
                }
            }
            return string.Join("; ", parts);
        }

        // ---------------------------------------------------------------- versions and capabilities

        private static void Describe(RunInfo info) {
            info.unityVersion = Application.unityVersion;
            info.target = StandDefines.ActiveTarget.ToString();
            info.backend = PlayerSettings.GetScriptingBackend(StandDefines.Active).ToString();
            var pkg = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/com.kensei.ecs");
            if (pkg == null && StandTypes.Assembly("KenseiECS") != null) {
                pkg = UnityEditor.PackageManager.PackageInfo.FindForAssembly(StandTypes.Assembly("KenseiECS"));
            }
            if (pkg != null) {
                info.packageName = pkg.name;
                info.packageVersion = pkg.version;
                info.packagePath = pkg.resolvedPath;
            } else {
                info.ok = false;
                info.notes.Add("package com.kensei.ecs not found");
            }
#if STAND_AA
            var aa = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(AddressableAssetSettings).Assembly);
            info.aaVersion = aa != null ? aa.version : "?";
#endif
#if STAND_SBP
            var sbp = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(UnityEditor.Build.Pipeline.ContentPipeline).Assembly);
            info.sbpVersion = sbp != null ? sbp.version : "?";
#endif
            info.editorDebug = StandTypes.Find("KenseiECS.EcsProfiler") != null;
            info.editorCompileFailed = EditorUtility.scriptCompilationFailed;
            info.editorCompileErrors = StandEditorLog.StartupCompileErrors();
            info.capabilities = Capabilities(info);
        }

        // The contract follows the package version (docs of that version), not what the code has:
        // a 2.2+ package without the guard is a finding, not a skipped case.
        private static List<string> Capabilities(RunInfo info) {
            var caps = new List<string>();
            string contract = StandArgs.Get("-standContract", "");
            if (contract.Length == 0) {
                contract = AtLeast(info.packageVersion, 2, 2) ? "2.2" : "2.1";
            }
            caps.Add("contract" + contract);
            if (contract != "2.1") {
                caps.Add("guard");
                caps.Add("releaseCheck");
                caps.Add("toggle22");
            } else {
                caps.Add("toggle21");
            }
#if UNITY_6000_0_OR_NEWER
            if (StandProfiles.Available) {
                caps.Add("profiles");
            }
#endif
#if STAND_AA
            caps.Add("aa");
#endif
#if STAND_SBP
            caps.Add("sbp");
#endif
            // The run group starts the built player, which needs a macOS host and the macOS build module.
            if (Application.platform == RuntimePlatform.OSXEditor &&
                BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneOSX)) {
                caps.Add("osxPlayer");
                string engine = BuildPipeline.GetPlaybackEngineDirectory(BuildTarget.StandaloneOSX, BuildOptions.None);
                string arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
                if (!string.IsNullOrEmpty(engine) && Directory.Exists(Path.Combine(engine, "Variations", "macos_" + arch + "_player_nondevelopment_il2cpp"))) {
                    caps.Add("il2cppMac");
                }
            }
            // What the code has, for the report only.
            if (StandTypes.Find("KenseiECS.Editor.KenseiDebugBuildGuard") != null) {
                caps.Add("impl:guard");
            }
            if (!string.IsNullOrEmpty(info.packagePath) && File.Exists(Path.Combine(info.packagePath, "DevTools", "KenseiDebugReleaseCheck.cs"))) {
                caps.Add("impl:releaseCheck");
            }
            if (StandTypes.Find("KenseiECS.Editor.KenseiDebugPlayerScriptsCheck") != null) {
                caps.Add("impl:postCompileCheck");
            }
            return caps;
        }

        private static bool AtLeast(string version, int major, int minor) {
            var m = Regex.Match(version ?? "", @"^(\d+)\.(\d+)");
            if (!m.Success) {
                return true;
            }
            int a = int.Parse(m.Groups[1].Value), b = int.Parse(m.Groups[2].Value);
            return a > major || (a == major && b >= minor);
        }

        // ---------------------------------------------------------------- judging build cases

        private static bool Blocked(string actual) =>
            actual == "blocked-guard" || actual == "blocked-check" || actual == "blocked-error";

        private static string BlockMessage(CaseResult r) {
            switch (r.actual) {
                case "blocked-guard": return r.evidence.guardMessage;
                case "blocked-check": return r.evidence.checkMessage;
                case "blocked-error": return r.evidence.cs1029Line;
                default: return "";
            }
        }

        internal static void Judge(CaseDef c, CaseResult r, RunInfo info) {
            string exp = c.expect;
            if (exp == "pass") {
                r.verdict = r.violations.Count > 0 ? "finding" : (r.questions.Count > 0 ? "question" : "ok");
                return;
            }
            if (exp == "ran") {
                JudgeRun(c, r);
                return;
            }
            // "blocked": any KenseiECS stop (guard, check after the compile, #error); the documented
            // mechanism changed in 2.2.1, the contract (a release player with the define does not build) did not.
            if (exp == "blocked" && Blocked(r.actual)) {
                r.checks.Add("ok: KenseiECS stopped the build (" + r.actual + "): " + StandText.Trim(BlockMessage(r), 300));
                CheckStopMessage(c, r);
                r.verdict = r.violations.Count > 0 ? "finding" : "ok";
                return;
            }
            if (r.actual == "failed-other" || r.actual == "stand-compile-error") {
                r.verdict = "inconclusive";
                r.reason = "the build failed, not because of KenseiECS: " + FirstError(r);
                return;
            }
            if (r.actual == exp) {
                if (exp == "compiled") {
                    foreach (var m in StandText.Split(c.expectMarkers)) {
                        if (!r.evidence.markers.Contains(m)) {
                            r.verdict = "inconclusive";
                            r.reason = "define " + m + " did not reach the compile (markers " + StandText.Join(r.evidence.markers) + "): the stand did not set its source";
                            return;
                        }
                    }
                    foreach (var m in StandText.Split(c.expectNoMarkers)) {
                        if (r.evidence.markers.Contains(m)) {
                            r.verdict = "inconclusive";
                            r.reason = "define " + m + " reached the compile (markers " + StandText.Join(r.evidence.markers) + "), and it should not have";
                            return;
                        }
                    }
                    if (r.evidence.kenseiDebugCode != "n/a") {
                        bool debug = r.evidence.markers.Contains("KENSEI_DEBUG");
                        bool code = r.evidence.kenseiDebugCode == "yes";
                        r.checks.Add((debug == code ? "ok" : "note") + ": KENSEI_DEBUG in the compile=" + debug + ", debug code in KenseiECS.dll=" + code);
                        if (debug != code) {
                            r.questions.Add("KENSEI_DEBUG in the compile=" + debug + ", but the debug code (EcsProfiler) in KenseiECS.dll=" + code +
                                ": the define reached the project assemblies but not the package (or the other way round)");
                        }
                    }
                    if (c.checkWarnings) {
                        JudgeWarnings(r);
                    }
                }
                if (exp == "blocked-guard") {
                    CheckGuardMessage(c, r);
                }
                r.verdict = r.violations.Count > 0 ? "finding" : (r.questions.Count > 0 && c.checkWarnings ? "question" : "ok");
                return;
            }

            string source = Source(c);
            if (exp == "blocked-guard" && r.actual == "blocked-check") {
                AddViolation(r, "guard-sees-source", "guard-missed:" + source,
                    "only the check after the script compile stopped the build; the guard did not see KENSEI_DEBUG from " + source +
                    ": " + StandText.Trim(r.evidence.checkMessage, 300));
                r.verdict = "finding";
                return;
            }
            if (exp.StartsWith("blocked") && r.actual == "compiled") {
                if (!r.evidence.markers.Contains("KENSEI_DEBUG")) {
                    r.verdict = "inconclusive";
                    r.reason = "KENSEI_DEBUG from " + source + " did not reach the player compile (markers " + StandText.Join(r.evidence.markers) +
                        "): the stand could not set this source";
                    return;
                }
                if (r.evidence.kenseiDebugCode == "no") {
                    r.verdict = "question";
                    r.questions.Add("KENSEI_DEBUG from " + source + " reached the project assemblies (marker), but KenseiECS.dll was compiled without the debug code: " +
                        "the release player did not get validation, though the documents promise a stop of the build for this source.");
                    return;
                }
                AddViolation(r, c.rule, "not-blocked:" + source,
                    "the release build with KENSEI_DEBUG from " + source + " compiled its scripts; debug code in KenseiECS.dll: " + r.evidence.kenseiDebugCode);
            } else if (exp == "blocked-guard" && r.actual == "blocked-error") {
                AddViolation(r, "guard-sees-source", "guard-missed:" + source,
                    "only the #error stopped the build (" + r.evidence.cs1029Line + "); the guard did not see KENSEI_DEBUG from " + source);
            } else if (exp == "blocked-error" && r.actual == "blocked-guard") {
                r.checks.Add("ok: the guard stopped the build before the #error (stricter than documented): " + StandText.Trim(r.evidence.guardMessage, 200));
                r.verdict = "ok";
                return;
            } else if (r.actual == "kensei-compile-error") {
                AddViolation(r, c.rule, "kensei-compile-error:" + CompileSignature(r.evidence.kenseiCompileErrors.FirstOrDefault()),
                    "KenseiECS does not compile: " + string.Join(" | ", r.evidence.kenseiCompileErrors.Take(3)));
            } else if (exp == "compiled" && Blocked(r.actual)) {
                string sig = r.actual == "blocked-error" ? "blocked-error:" + CompileSignature(r.evidence.cs1029Line) : r.actual;
                string why = r.actual == "blocked-error" ? r.evidence.cs1029Line : r.actual + ": " + StandText.Trim(BlockMessage(r), 300);
                AddViolation(r, c.rule, sig, "the build failed because of KenseiECS: " + why +
                    (r.evidence.aaError.Length > 0 ? "; Addressables: " + r.evidence.aaError : ""));
            } else {
                AddViolation(r, c.rule, "unexpected:" + r.actual, "expected " + exp + ", got " + r.actual + ": " + FirstError(r));
            }
            r.verdict = "finding";
        }

        // A build that should pass cleanly: the UAC0009 warning (DEVELOPMENT_BUILD in KenseiECS code, Unity 6.6)
        // is promised gone since 2.2.1; any other KenseiECS warning is a question (no document promises none).
        private static void JudgeWarnings(CaseResult r) {
            var warnings = r.evidence.kenseiWarnings;
            if (warnings.Count == 0) {
                r.checks.Add("ok: KenseiECS logged no warning in this build");
                return;
            }
            var other = new List<string>();
            foreach (var w in warnings) {
                var uac = Regex.Match(w, @"(?<file>[^\s:()]+\.cs)\(\d+,\d+\): warning (?<code>UAC0009)");
                if (uac.Success) {
                    AddViolation(r, "kensei-build-warning", "warning:UAC0009@" + PackageRelative(uac.Groups["file"].Value),
                        "KenseiECS code still raises UAC0009 (DEVELOPMENT_BUILD is deprecated): " + StandText.Trim(w, 300));
                } else {
                    other.Add(StandText.Trim(w, 240));
                }
            }
            if (other.Count > 0) {
                r.questions.Add("KenseiECS logged " + other.Count + " warning(s) in a build that should pass cleanly (no document promises none): " +
                    string.Join(" | ", other));
            }
        }

        private static void JudgeRun(CaseDef c, CaseResult r) {
            if (r.actual == "ran") {
                bool debug = r.evidence.result.StartsWith("OK debug");
                bool wantDebug = c.extraDefines.Contains(StandDefines.DebugDefine) || r.applied.psActive.Contains(StandDefines.DebugDefine);
                r.checks.Add("ok: the player ran: " + r.evidence.result + " (exit code " + r.evidence.playerExit + ")");
                if (debug != wantDebug) {
                    r.verdict = "inconclusive";
                    r.reason = "the player was built " + (debug ? "with" : "without") + " KENSEI_DEBUG, the case asks for " + (wantDebug ? "with" : "without");
                    return;
                }
                r.verdict = "ok";
                return;
            }
            if (r.actual == "run-failed") {
                string what = r.evidence.result.Length > 0 ? r.evidence.result : "no [KENSEI-STAND-PLAYER] line in the player log";
                AddViolation(r, c.rule, "player-run:" + Regex.Replace(what.Split(' ').Skip(1).FirstOrDefault() ?? "no-result", @"[^A-Za-z]+", "-"),
                    "the player (" + r.applied.options + ") exited with code " + r.evidence.playerExit + ": " + what);
                r.verdict = "finding";
                return;
            }
            if (Blocked(r.actual) || r.actual == "kensei-compile-error") {
                AddViolation(r, c.rule, r.actual == "blocked-error" ? "blocked-error:" + CompileSignature(r.evidence.cs1029Line) : r.actual,
                    "the player build for the run failed because of KenseiECS: " + StandText.Trim(BlockMessage(r) + string.Join(" | ", r.evidence.kenseiCompileErrors.Take(3)), 600));
                r.verdict = "finding";
                return;
            }
            r.verdict = "inconclusive";
            r.reason = "the player build for the run failed, not because of KenseiECS: " + FirstError(r);
        }

        private static IEnumerable<KeyValuePair<string, string>> MessageParts(string spec, CaseResult r) {
            foreach (var raw in (spec ?? "").Split('|')) {
                string part = raw.Replace("{target}", r.applied.target)
                    .Replace("{profileName}", r.applied.profileName)
                    .Replace("{profilePath}", r.applied.profilePath);
                if (part.Length > 0) {
                    yield return new KeyValuePair<string, string>(raw, part);
                }
            }
        }

        private static void CheckGuardMessage(CaseDef c, CaseResult r) {
            string msg = r.evidence.guardMessage;
            r.evidence.namedSources = KnownSources.Where(s => msg.Contains(s)).ToList();
            foreach (var kv in MessageParts(c.expectMessage, r)) {
                if (msg.Contains(kv.Value)) {
                    r.checks.Add("ok: the guard error names \"" + kv.Value + "\"");
                } else {
                    AddViolation(r, "guard-names-source", "guard-message-missing:" + kv.Key,
                        "the guard error does not name \"" + kv.Value + "\": " + StandText.Trim(msg, 400));
                }
            }
            AskMessage(c, r, msg);
        }

        // The stop of a "blocked" case. What each mechanism promises about its message
        // (docs/unity/debug-tools.md): the check after the script compile (2.2.1+) stops "with an error that
        // names the source"; the #error of 2.2.0 only "says so" (that the define is set). Both are judged
        // against the naming promise of rule check-names-source, whose "since" (2.2.1) makes a miss in 2.2.0
        // a question (report.py). An error that lists every source the guard does not read, also the ones
        // this build does not use, does not name the source. A guard stop here means the define was also in
        // the Scripting Define Symbols, which the case does not set: a question.
        private static void CheckStopMessage(CaseDef c, CaseResult r) {
            string msg = BlockMessage(r);
            r.evidence.namedSources = KnownSources.Where(s => msg.Contains(s)).ToList();
            bool compileStop = r.actual == "blocked-check" || r.actual == "blocked-error";
            string mechanism = r.actual == "blocked-check" ? "check" : r.actual == "blocked-error" ? "error" : "guard";
            bool allNamed = true;
            foreach (var kv in MessageParts(c.expectMessage, r)) {
                if (msg.Contains(kv.Value)) {
                    r.checks.Add("ok: the stop message names \"" + kv.Value + "\"");
                } else if (compileStop) {
                    allNamed = false;
                    AddViolation(r, "check-names-source", mechanism + "-message-missing:" + kv.Key,
                        (mechanism == "check" ? "the error of the check after the script compile" : "the #error") + " does not name the source \"" + kv.Value + "\": " +
                        StandText.Trim(msg, 400));
                } else {
                    r.questions.Add("the guard stopped the build (" + StandText.Trim(msg, 200) + "), though the define comes from a source the guard does not read (" +
                        Source(c) + "); the message does not name \"" + kv.Value + "\"");
                }
            }
            if (compileStop && allNamed && c.expectMessage.Length > 0) {
                // The list of every source (the fallback of 2.2.1, the wording of 2.2.0): all the compile-only sources this
                // build does not use are named too. One of them named in passing (a hint) is not taken for that list.
                var used = CompileOnlySourcesOf(c);
                var unused = CompileOnlySources.Where(s => !used.Contains(s)).ToList();
                var others = unused.Where(s => msg.Contains(s)).ToList();
                if (unused.Count > 0 && others.Count == unused.Count) {
                    AddViolation(r, "check-names-source", mechanism + "-message-generic",
                        (mechanism == "check" ? "the error of the check after the script compile" : "the #error") + " also names " + string.Join(", ", others) +
                        ", which this build does not use: a list of every possible source, not the source of this build: " + StandText.Trim(msg, 400));
                } else {
                    r.checks.Add("ok: the stop message is not a list of every source" + (others.Count > 0 ? " (it also mentions " + string.Join(", ", others) + ")" : ""));
                }
            }
            AskMessage(c, r, msg);
        }

        private static void AskMessage(CaseDef c, CaseResult r, string msg) {
            foreach (var kv in MessageParts(c.askMessage, r)) {
                if (msg.Contains(kv.Value)) {
                    r.checks.Add("ok: the message names \"" + kv.Value + "\"");
                } else {
                    r.questions.Add("the stop message (" + r.actual + ") does not name \"" + kv.Value + "\": " + StandText.Trim(msg, 300));
                }
            }
        }

        internal static void AddViolation(CaseResult r, string rule, string signature, string detail) {
            r.violations.Add(new Violation { rule = rule, signature = signature, detail = detail });
            r.checks.Add("FAIL [" + rule + "] " + detail);
        }

        private static bool HasDebug(string defines) => StandText.Split(defines).Contains(StandDefines.DebugDefine);

        private static bool RawArgsHaveDebug(string rawArgs) =>
            !string.IsNullOrEmpty(rawArgs) && Regex.IsMatch(rawArgs, @"(^|[:;,""\s])" + StandDefines.DebugDefine + @"($|[;,""\s|])");

        private static List<string> CompileOnlySourcesOf(CaseDef c) {
            var used = new List<string>();
            if (HasDebug(c.extraDefines)) {
                used.Add("extraScriptingDefines");
            }
            if (HasDebug(c.argsDefines) || RawArgsHaveDebug(c.rawArgs)) {
                used.Add("Additional Compiler Arguments");
            }
            if (HasDebug(c.rspDefines)) {
                used.Add("csc.rsp");
            }
            return used;
        }

        private static string Source(CaseDef c) {
            var parts = new List<string>();
            if (c.debugMode == "on") {
                parts.Add("Debug Mode");
            }
            if (c.psDefines != "*" && HasDebug(c.psDefines)) {
                parts.Add("Player Settings");
            }
            if (HasDebug(c.profileDefines)) {
                parts.Add("Build Profile");
            }
            parts.AddRange(CompileOnlySourcesOf(c));
            return parts.Count == 0 ? "base" : string.Join("+", parts);
        }

        private static readonly Regex CsError = new Regex(@"(?<file>[^\s:()]+\.cs)\(\d+,\d+\): error (?<code>CS\d+)");

        /// <summary>"CS1029@DevTools/KenseiDebugReleaseCheck.cs": code and file relative to the package, without line numbers.</summary>
        internal static string CompileSignature(string line) {
            if (string.IsNullOrEmpty(line)) {
                return "?";
            }
            var m = CsError.Match(line);
            if (!m.Success) {
                return StandText.Trim(line, 80);
            }
            return m.Groups["code"].Value + "@" + PackageRelative(m.Groups["file"].Value);
        }

        internal static string PackageRelative(string file) {
            file = file.Replace('\\', '/');
            foreach (var marker in new[] { "/pkg/", "Packages/com.kensei.ecs/" }) {
                int i = file.IndexOf(marker, StringComparison.Ordinal);
                if (i >= 0) {
                    return file.Substring(i + marker.Length);
                }
            }
            return file;
        }

        private static string FirstError(CaseResult r) {
            if (r.evidence.exception.Length > 0) {
                return r.evidence.exception;
            }
            return r.evidence.errors.FirstOrDefault() ?? "(no errors in the log; result " + r.evidence.buildResult + ")";
        }
    }
}
