// KenseiECS test stand, module unity: what each case kind does. A kind fills CaseResult.actual and
// evidence (build kinds, judged by StandRunner.Judge) or adds violations and sets the verdict itself
// (editor and toggle kinds, expect "pass").
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Player;
using UnityEditor.Build.Reporting;
using UnityEngine;
#if UNITY_6000_0_OR_NEWER
using UnityEditor.Build.Profile;
#endif
#if STAND_AA
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
#endif
#if STAND_SBP
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Interfaces;
#endif

namespace KenseiStand.Editor {
    internal static class StandCases {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        public static void Run(CaseDef c, CaseResult r, RunInfo info) {
            switch (c.kind) {
                case "editor-compile": EditorCompile(r, info); break;
                case "editor-generator": EditorGenerator(r, info); break;
                case "editor-smoke": EditorSmoke(r, info); break;
                case "editor-menus": EditorMenus(r, info); break;
                case "editor-spawn": EditorSpawn(r); break;
                case "toggle-noprofile": ToggleNoProfile(r); break;
                case "toggle-profile": ToggleProfile(r); break;
                case "toggle-leftovers": ToggleLeftovers(r); break;
                case "toggle-ownps": ToggleOwnPlayerSettings(r); break;
                case "toggle-21": Toggle21(r); break;
                case "player": Player(c, r, info); break;
                case "sbp-compile": SbpCompile(c, r, info); break;
                case "legacy-bundles": LegacyBundles(c, r, info); break;
                case "sbp-bundles": SbpBundles(c, r, info); break;
                case "aa-content": AaContent(c, r, info); break;
                case "player-run": PlayerRun(c, r, info); break;
                default: throw new StandInconclusive("unknown kind '" + c.kind + "'");
            }
        }

        private static void Check(CaseResult r, bool ok, string rule, string signature, string what) {
            if (ok) {
                r.checks.Add("ok: " + what);
            } else {
                StandRunner.AddViolation(r, rule, signature, what);
            }
        }

        // ---------------------------------------------------------------- editor

        private static void EditorCompile(CaseResult r, RunInfo info) {
            var pkgErrors = info.editorCompileErrors.Where(e => IsPackagePath(e, info)).ToList();
            var standErrors = info.editorCompileErrors.Where(e => e.Contains("Assets/KenseiStand")).ToList();
            r.evidence.kenseiCompileErrors = pkgErrors;
            r.evidence.standCompileErrors = standErrors;
            if (pkgErrors.Count > 0) {
                StandRunner.AddViolation(r, "editor-compiles", "editor-compile:" + StandRunner.CompileSignature(pkgErrors[0]),
                    "the editor (" + (info.baseline == "dbg" ? "Debug Mode on" : "Debug Mode off") + ") does not compile the package: " + string.Join(" | ", pkgErrors.Take(3)));
            } else {
                r.checks.Add("ok: no compile errors in the package in the startup log");
            }
            if (standErrors.Count > 0) {
                throw new StandInconclusive("the code of the stand does not compile: " + string.Join(" | ", standErrors.Take(3)));
            }
            if (pkgErrors.Count == 0 && info.editorCompileFailed) {
                throw new StandInconclusive("EditorUtility.scriptCompilationFailed=true, but no package errors in the log: " +
                    string.Join(" | ", info.editorCompileErrors.Take(3)));
            }
            foreach (var asm in new[] { "KenseiECS", "KenseiECS.Editor", "Assembly-CSharp" }) {
                Check(r, StandTypes.AssemblyLoaded(asm), "editor-compiles", "editor-assembly-missing:" + asm, "assembly " + asm + " is loaded in the editor");
            }
            bool wantDebug = info.baseline == "dbg";
            if (pkgErrors.Count == 0 && info.editorDebug != wantDebug) {
                throw new StandInconclusive("the editor is compiled " + (info.editorDebug ? "with" : "without") + " KENSEI_DEBUG, the baseline is " + info.baseline +
                    ": the setup did not apply (see setup.json)");
            }
            r.checks.Add("ok: KenseiECS in the editor " + (info.editorDebug ? "with" : "without") + " KENSEI_DEBUG, as baseline " + info.baseline + " requires");
            r.actual = r.violations.Count > 0 ? "fail" : "pass";
        }

        private static bool IsPackagePath(string line, RunInfo info) =>
            line.Contains("Packages/com.kensei.ecs/") || (info.packagePath.Length > 0 && line.Replace('\\', '/').Contains(info.packagePath.Replace('\\', '/')));

        private static void EditorGenerator(CaseResult r, RunInfo info) {
            var system = StandTypes.Find("KenseiStand.StandMoveSystem");
            var initInterface = StandTypes.Find("KenseiECS.IInitSystem");
            var world = StandTypes.Find("KenseiECS.World");
            var shared = StandTypes.Find("KenseiECS.SharedData");
            if (system == null || initInterface == null || world == null || shared == null) {
                throw new StandInconclusive("no types StandMoveSystem/IInitSystem/World/SharedData (Assembly-CSharp or KenseiECS not compiled, see E1)");
            }
            string analyzer = Path.Combine(info.packagePath, "Plugins", "KenseiECS.Generators.dll");
            r.checks.Add((File.Exists(analyzer) ? "ok" : "note") + ": Plugins/KenseiECS.Generators.dll " + (File.Exists(analyzer) ? "is there" : "is missing"));
            string meta = analyzer + ".meta";
            if (File.Exists(meta)) {
                bool label = File.ReadAllText(meta).Contains("RoslynAnalyzer");
                r.checks.Add((label ? "ok" : "note") + ": the RoslynAnalyzer label in the .meta is " + (label ? "there" : "missing"));
            }
            bool implements = initInterface.IsAssignableFrom(system);
            var init = system.GetMethod("Init", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly, null, new[] { world, shared }, null);
            Check(r, implements, "generator-in-unity", "generator:no-IInitSystem", "the generated partial added IInitSystem to StandMoveSystem");
            Check(r, init != null, "generator-in-unity", "generator:no-Init", "StandMoveSystem has a generated Init(World, SharedData)");
            r.actual = r.violations.Count > 0 ? "fail" : "pass";
        }

        private static void EditorSmoke(CaseResult r, RunInfo info) {
            var game = StandTypes.Find("KenseiStand.StandGame");
            var smoke = game?.GetMethod("Smoke", BindingFlags.Public | BindingFlags.Static);
            if (smoke == null) {
                throw new StandInconclusive("no KenseiStand.StandGame.Smoke (Assembly-CSharp not compiled, see E1)");
            }
            string result;
            try {
                result = (string)smoke.Invoke(null, null);
            } catch (TargetInvocationException e) {
                var inner = e.InnerException ?? e;
                result = "FAIL exception " + inner.GetType().Name + ": " + inner.Message;
                r.evidence.exception = StandText.Trim(inner.ToString(), 1500);
            }
            r.evidence.result = result;
            Check(r, result.StartsWith("OK"), "runtime-smoke", "smoke:" + Regex.Replace(result, @"[^A-Za-z]+", "-").Trim('-'),
                "StandGame.Smoke() in the editor: " + result);
            if (result.StartsWith("OK")) {
                bool debug = result.StartsWith("OK debug");
                if (debug != info.editorDebug) {
                    r.questions.Add("Smoke reports mode " + (debug ? "debug" : "release") + ", while KenseiECS in the editor is " +
                        (info.editorDebug ? "with" : "without") + " KENSEI_DEBUG: the defines of Assembly-CSharp and the package differ");
                }
            }
            r.actual = r.violations.Count > 0 ? "fail" : "pass";
        }

        private static void EditorMenus(CaseResult r, RunInfo info) {
            var asm = StandTypes.Assembly("KenseiECS.Editor");
            if (asm == null) {
                throw new StandInconclusive("assembly KenseiECS.Editor is not loaded (see E1)");
            }
            var menus = new HashSet<string>();
            Type[] types;
            try {
                types = asm.GetTypes();
            } catch (ReflectionTypeLoadException e) {
                types = e.Types.Where(t => t != null).ToArray();
            }
            foreach (var t in types) {
                foreach (var m in t.GetMethods(All)) {
                    foreach (var attr in m.GetCustomAttributes(typeof(MenuItem), false).Cast<MenuItem>()) {
                        if (!attr.validate) {
                            menus.Add(attr.menuItem);
                        }
                    }
                }
            }
            r.evidence.logLines = menus.OrderBy(x => x).ToList();
            foreach (var always in new[] { "KenseiECS/Debug Mode", "KenseiECS/Systems" }) {
                Check(r, menus.Contains(always), "debug-menu-items", "menu-missing:" + always, "menu item " + always + " is always there");
            }
            foreach (var debugOnly in new[] { "KenseiECS/World Inspector", "KenseiECS/Profiler" }) {
                if (info.editorDebug) {
                    Check(r, menus.Contains(debugOnly), "debug-menu-items", "menu-missing-debug:" + debugOnly, "menu item " + debugOnly + " is there with KENSEI_DEBUG");
                } else {
                    Check(r, !menus.Contains(debugOnly), "debug-menu-items", "menu-present-release:" + debugOnly, "no menu item " + debugOnly + " without KENSEI_DEBUG");
                }
            }
            r.actual = r.violations.Count > 0 ? "fail" : "pass";
        }

        // EcsEntityView.Spawn of a view with three providers (A, B, C) while a world listener spawns another
        // view from OnEntityCreated of the entity being spawned (KenseiStand.StandSpawnCheck in
        // Assembly-CSharp). The control is the same Spawn without the listener: it must give A, B, C, or the
        // stand itself is wrong.
        private static void EditorSpawn(CaseResult r) {
            var check = StandTypes.Find("KenseiStand.StandSpawnCheck");
            var run = check?.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
            if (run == null) {
                throw new StandInconclusive("no KenseiStand.StandSpawnCheck.Run (Assembly-CSharp not compiled, see E1)");
            }
            string json;
            try {
                json = (string)run.Invoke(null, null);
            } catch (TargetInvocationException e) {
                var inner = e.InnerException ?? e;
                r.evidence.exception = StandText.Trim(inner.ToString(), 1500);
                throw new StandInconclusive("StandSpawnCheck.Run threw: " + inner.GetType().Name + ": " + inner.Message);
            }
            var rep = JsonUtility.FromJson<SpawnReport>(json);
            r.evidence.result = json;
            string want = StandText.Join(rep.expected);
            r.evidence.logLines.Add("plain Spawn: " + StandText.Join(rep.plain) + "; nested: outer " + StandText.Join(rep.outer) +
                ", inner " + StandText.Join(rep.inner) + ", listener fired " + rep.listenerFired + (rep.error.Length > 0 ? ", error " + rep.error : ""));
            if (rep.error.Length > 0 && !rep.listenerFired) {
                throw new StandInconclusive("the Spawn check did not get to the nested Spawn: " + rep.error);
            }
            Check(r, rep.plain.Count == rep.expected.Count && rep.expected.All(rep.plain.Contains), "entity-view-spawn", "spawn-plain:lost-components",
                "Spawn without a listener gives the entity " + want + " (got " + StandText.Join(rep.plain) + ")");
            if (!rep.listenerFired) {
                throw new StandInconclusive("the world listener of the nested Spawn did not fire (OnEntityCreated)");
            }
            if (rep.error.Length > 0) {
                StandRunner.AddViolation(r, "entity-view-spawn", "spawn-nested:exception",
                    "Spawn from the listener during the outer Spawn threw: " + rep.error);
            }
            var lost = rep.expected.Where(x => !rep.outer.Contains(x)).ToList();
            var foreign = rep.outer.Where(x => !rep.expected.Contains(x)).ToList();
            Check(r, lost.Count == 0, "entity-view-spawn", "spawn-nested:outer-lost-components",
                "the outer entity keeps " + want + " when a listener spawns another view during its Spawn (got " + StandText.Join(rep.outer) +
                (lost.Count > 0 ? ", lost " + StandText.Join(lost) : "") + ")");
            Check(r, foreign.Count == 0, "entity-view-spawn", "spawn-nested:outer-foreign-components",
                "the outer entity gets no component of the other view (got " + StandText.Join(rep.outer) + ")");
            Check(r, rep.innerAlive && rep.inner.Count == 1 && rep.inner[0] == "StandSpawnInner", "entity-view-spawn", "spawn-nested:inner-wrong",
                "the view spawned by the listener has exactly its own component [StandSpawnInner] (got " + StandText.Join(rep.inner) +
                (rep.innerAlive ? "" : ", entity not alive") + ")");
            r.actual = r.violations.Count > 0 ? "fail" : "pass";
        }

        // ---------------------------------------------------------------- Debug Mode toggle

        private static void ClearDefineEverywhere() {
            foreach (var t in StandDefines.Known) {
                try {
                    StandDefines.Set(t, StandDefines.Get(t).Where(d => d != StandDefines.DebugDefine));
                } catch (Exception) {
                    // A target this editor cannot address.
                }
            }
        }

        private static void Toggle(CaseResult r, string step) {
            if (!StandToggle.Execute()) {
                throw new StandInconclusive("menu item " + StandToggle.MenuPath + " not found (KenseiECS.Editor not loaded?)");
            }
            r.evidence.logLines.Add("menu " + StandToggle.MenuPath + " (" + step + ")");
        }

        private static bool Has(string[] defines) => defines != null && defines.Contains(StandDefines.DebugDefine);

        private static void CheckOthersUnchanged(CaseResult r, Dictionary<string, string[]> before, Dictionary<string, string[]> after, string step, string rule) {
            string active = StandDefines.Active.TargetName;
            foreach (var kv in before) {
                if (kv.Key == active) {
                    continue;
                }
                after.TryGetValue(kv.Key, out var now);
                Check(r, now != null && now.SequenceEqual(kv.Value), rule, step + ":other-target-changed:" + kv.Key,
                    step + ": Player Settings " + kv.Key + " unchanged (" + StandText.Join(kv.Value) + " -> " + StandText.Join(now ?? new string[0]) + ")");
            }
        }

        private static void CheckCheckmark(CaseResult r, bool expected, string step) {
            bool? mark = StandToggle.Checkmark();
            if (mark == null) {
                r.checks.Add("note: " + step + ": the menu checkmark cannot be read (no menu in batchmode), not checked");
                return;
            }
            Check(r, mark.Value == expected, "toggle-checkmark", step + ":checkmark", step + ": Debug Mode checkmark = " + mark.Value + " (expected " + expected + ")");
        }

        private static void ToggleNoProfile(CaseResult r) {
            ClearDefineEverywhere();
            var active = StandDefines.Active.TargetName;
            const string rule = "toggle-writes-documented-place";
            var before = StandDefines.All();
            Toggle(r, "on");
            var on = StandDefines.All();
            r.evidence.logLines.Add("after on: " + StandDefines.Describe(on));
            Check(r, Has(on[active]), rule, "on:active-target-missing", "on: KENSEI_DEBUG in the Player Settings of the active target " + active);
            CheckOthersUnchanged(r, before, on, "on", rule);
            CheckCheckmark(r, true, "on");
            Toggle(r, "off");
            var off = StandDefines.All();
            r.evidence.logLines.Add("after off: " + StandDefines.Describe(off));
            Check(r, !Has(off[active]), rule, "off:active-target-left", "off: KENSEI_DEBUG removed from Player Settings " + active);
            CheckOthersUnchanged(r, before, off, "off", rule);
            CheckCheckmark(r, false, "off");
            r.actual = r.violations.Count > 0 ? "fail" : "pass";
        }

        private static void ToggleProfile(CaseResult r) {
#if UNITY_6000_0_OR_NEWER
            ClearDefineEverywhere();
            var active = StandDefines.Active.TargetName;
            const string rule = "toggle-writes-documented-place";
            var profile = StandProfiles.Create("Stand_T2", false, new string[0], false);
            BuildProfile.SetActiveBuildProfile(profile);
            if (BuildProfile.GetActiveBuildProfile() != profile) {
                throw new StandInconclusive("the Build Profile did not become active");
            }
            string path = AssetDatabase.GetAssetPath(profile);
            r.applied.profileName = profile.name;
            r.applied.profilePath = path;
            var before = StandDefines.All();

            int mark = StandLog.Count;
            Toggle(r, "on");
            var on = StandDefines.All();
            r.evidence.logLines.Add("after on: profile " + StandText.Join(profile.scriptingDefines ?? new string[0]) + ", " + StandDefines.Describe(on));
            Check(r, Has(profile.scriptingDefines), rule, "profile-on:profile-missing", "on: KENSEI_DEBUG in the Scripting Defines of profile " + profile.name);
            Check(r, !Has(on[active]), rule, "profile-on:player-settings-written", "on: Player Settings " + active + " untouched (the define only in the profile)");
            CheckOthersUnchanged(r, before, on, "on", rule);
            CheckProfileLog(r, StandLog.Since(mark), profile.name, path, "on");
            CheckCheckmark(r, true, "on");

            mark = StandLog.Count;
            Toggle(r, "off");
            var off = StandDefines.All();
            r.evidence.logLines.Add("after off: profile " + StandText.Join(profile.scriptingDefines ?? new string[0]) + ", " + StandDefines.Describe(off));
            Check(r, !Has(profile.scriptingDefines), rule, "profile-off:profile-left", "off: KENSEI_DEBUG removed from the Scripting Defines of the profile");
            Check(r, !Has(off[active]), rule, "profile-off:player-settings-left", "off: no define in Player Settings " + active);
            CheckOthersUnchanged(r, before, off, "off", rule);
            CheckProfileLog(r, StandLog.Since(mark), profile.name, path, "off");
            CheckCheckmark(r, false, "off");
            r.actual = r.violations.Count > 0 ? "fail" : "pass";
#else
            throw new StandSkipped("Build Profiles exist only in Unity 6");
#endif
        }

        private static void CheckProfileLog(CaseResult r, List<LogEntry> entries, string name, string path, string step) {
            var line = entries.FirstOrDefault(e => !e.text.StartsWith(StandLog.Prefix) && e.text.Contains(name) && e.text.Contains(path));
            r.evidence.logLines.AddRange(entries.Where(e => !e.text.StartsWith(StandLog.Prefix)).Select(e => step + " log: " + StandText.Trim(e.text, 300)));
            Check(r, line.text != null, "toggle-logs-profile-write", step + ":no-profile-log",
                step + ": a log line with the name and path of the profile (" + name + ", " + path + ")");
        }

        private static void ToggleLeftovers(CaseResult r) {
            ClearDefineEverywhere();
            const string rule = "toggle-leftovers-warning";
            var other = StandDefines.Active.TargetName == NamedBuildTarget.iOS.TargetName ? NamedBuildTarget.Android : NamedBuildTarget.iOS;
            StandDefines.Set(other, new[] { StandDefines.DebugDefine });
            Toggle(r, "on");
            int mark = StandLog.Count;
            Toggle(r, "off");
            var warnings = StandLog.Since(mark).Where(e => e.type == LogType.Warning && !e.text.StartsWith(StandLog.Prefix)).ToList();
            r.evidence.logLines.AddRange(warnings.Select(w => "warning: " + StandText.Trim(w.text, 400)));
            Check(r, warnings.Any(w => w.text.Contains(other.TargetName) && w.text.Contains(StandDefines.DebugDefine)), rule, "leftovers:no-warning",
                "off: a warning lists target " + other.TargetName + " with KENSEI_DEBUG left");
            Check(r, Has(StandDefines.Get(other)), rule, "leftovers:other-target-cleared", "off: Player Settings " + other.TargetName + " untouched (the define stays)");
            r.actual = r.violations.Count > 0 ? "fail" : "pass";
        }

        private static void ToggleOwnPlayerSettings(CaseResult r) {
#if UNITY_6000_0_OR_NEWER
            ClearDefineEverywhere();
            const string rule = "toggle-ownps";
            var active = StandDefines.Active;
            var profile = StandProfiles.Create("Stand_T4", false, new string[0], true);
            string path = AssetDatabase.GetAssetPath(profile);
            BuildProfile.SetActiveBuildProfile(profile);
            if (BuildProfile.GetActiveBuildProfile() != profile) {
                throw new StandInconclusive("the Build Profile did not become active");
            }
            // With the profile active, Player Settings calls go to the copy in the profile.
            StandDefines.Set(active, new[] { StandDefines.DebugDefine });
            if (!Has(StandDefines.Get(active))) {
                throw new StandInconclusive("could not write KENSEI_DEBUG to the Player Settings of the profile");
            }
            int mark = StandLog.Count;
            Toggle(r, "off");
            var entries = StandLog.Since(mark);
            r.evidence.logLines.AddRange(entries.Where(e => !e.text.StartsWith(StandLog.Prefix)).Select(e => "log: " + StandText.Trim(e.text, 400)));
            Check(r, !Has(StandDefines.Get(active)), rule, "ownps:copy-left", "off: KENSEI_DEBUG removed from the Player Settings stored in the profile");
            Check(r, entries.Any(e => e.text.Contains(profile.name) && e.text.Contains(path)), rule, "ownps:no-log",
                "off: the write to the profile is logged with its name and path (" + profile.name + ", " + path + ")");
            BuildProfile.SetActiveBuildProfile(null);
            Check(r, !Has(StandDefines.Get(active)), rule, "ownps:project-settings-written", "the Player Settings of the project (no profile) have no KENSEI_DEBUG");
            r.actual = r.violations.Count > 0 ? "fail" : "pass";
#else
            throw new StandSkipped("Build Profiles exist only in Unity 6");
#endif
        }

        private static void Toggle21(CaseResult r) {
            ClearDefineEverywhere();
            const string rule = "toggle-21-all-targets";
            Toggle(r, "on");
            var on = StandDefines.All();
            r.evidence.logLines.Add("after on: " + StandDefines.Describe(on));
            foreach (var kv in on) {
                Check(r, Has(kv.Value), rule, "21-on:missing:" + kv.Key, "on: KENSEI_DEBUG in Player Settings " + kv.Key);
            }
            Toggle(r, "off");
            var off = StandDefines.All();
            r.evidence.logLines.Add("after off: " + StandDefines.Describe(off));
            foreach (var kv in off) {
                Check(r, !Has(kv.Value), rule, "21-off:left:" + kv.Key, "off: KENSEI_DEBUG removed from Player Settings " + kv.Key);
            }
            r.actual = r.violations.Count > 0 ? "fail" : "pass";
        }

        // ---------------------------------------------------------------- builds

        private static string CaseDir(CaseDef c) {
            string dir = Path.Combine(StandRunner.BuildsDir, c.id);
            if (Directory.Exists(dir)) {
                Directory.Delete(dir, true);
            }
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string Location(string dir) {
            switch (StandDefines.ActiveTarget) {
                case BuildTarget.Android:
                    return EditorUserBuildSettings.exportAsGoogleAndroidProject ? Path.Combine(dir, "gradle") : Path.Combine(dir, "stand.apk");
                case BuildTarget.StandaloneOSX:
                    return Path.Combine(dir, "Stand.app");
                case BuildTarget.StandaloneWindows64:
                    return Path.Combine(dir, "Stand.exe");
                default:
                    return Path.Combine(dir, "player");
            }
        }

        private static void Player(CaseDef c, CaseResult r, RunInfo info) {
            string dir = CaseDir(c);
            var options = c.development ? BuildOptions.Development : BuildOptions.None;
            r.applied.options = StandText.Flags(options);
            BuildReport report = null;
            StandHooks.Arm();
            try {
                if (c.buildVia == "profile") {
#if UNITY_6000_0_OR_NEWER
                    if (StandApply.Profile == null) {
                        throw new StandInconclusive("buildVia=profile without a profile in the case");
                    }
                    report = BuildPipeline.BuildPlayer(new BuildPlayerWithProfileOptions {
                        buildProfile = StandApply.Profile, locationPathName = Location(dir), options = options
                    });
#else
                    throw new StandSkipped("BuildPlayerWithProfileOptions exists only in Unity 6");
#endif
                } else {
                    report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
                        scenes = new[] { StandPaths.Scene },
                        locationPathName = Location(dir),
                        target = StandDefines.ActiveTarget,
                        targetGroup = StandDefines.ActiveGroup,
                        options = options,
                        extraScriptingDefines = StandText.Split(c.extraDefines)
                    });
                }
            } catch (StandInconclusive) {
                throw;
            } catch (StandSkipped) {
                throw;
            } catch (Exception e) {
                r.evidence.exception = StandText.Trim(e.ToString(), 1500);
            } finally {
                StandHooks.Disarm();
            }
            r.evidence.preprocessCalled = StandHooks.PreprocessCalled;
            r.evidence.scriptsCompiled = StandHooks.ScriptsCompiled;
            r.evidence.postCompileReached = StandHooks.PostCompileReached;
            r.evidence.markers = StandHooks.Markers;
            r.evidence.kenseiDebugCode = StandHooks.KenseiDebugCode;
            r.evidence.kenseiDll = StandHooks.KenseiDll;
            if (report != null) {
                r.evidence.buildResult = report.summary.result.ToString();
                r.evidence.totalErrors = report.summary.totalErrors;
            }
            if (StandHooks.PreprocessOptions.Length > 0) {
                r.applied.options = StandHooks.PreprocessOptions;
            }
            ScanErrors(StandLog.Since(0), r, info);
            bool success = StandHooks.ScriptsCompiled || (report != null && report.summary.result == BuildResult.Succeeded);
            r.evidence.success = success;
            r.actual = Classify(success, r);
        }

        private static void SbpCompile(CaseDef c, CaseResult r, RunInfo info) {
            string dir = CaseDir(c);
            var settings = new ScriptCompilationSettings {
                target = StandDefines.ActiveTarget,
                group = StandDefines.ActiveGroup,
                options = c.development ? ScriptCompilationOptions.DevelopmentBuild : ScriptCompilationOptions.None,
                extraScriptingDefines = StandText.Split(c.extraDefines)
            };
            r.applied.options = settings.options.ToString();
            bool typeDb = false;
            try {
                var result = PlayerBuildInterface.CompilePlayerScripts(settings, dir);
                r.evidence.assemblies = result.assemblies == null ? -1 : result.assemblies.Count;
                typeDb = result.typeDB != null;
            } catch (Exception e) {
                r.evidence.exception = StandText.Trim(e.ToString(), 1500);
            }
            r.evidence.markers = StandDll.Markers(StandDll.FindIn(dir, StandDll.MarkerAssembly));
            r.evidence.kenseiDll = StandDll.FindIn(dir, StandDll.KenseiAssembly);
            r.evidence.kenseiDebugCode = StandDll.KenseiDebugCode(r.evidence.kenseiDll);
            ScanErrors(StandLog.Since(0), r, info);
            bool success = r.evidence.assemblies > 0 && typeDb;
            r.evidence.success = success;
            r.evidence.buildResult = "assemblies=" + r.evidence.assemblies + ", typeDB=" + (typeDb ? "set" : "null");
            r.actual = Classify(success, r);
        }

        private static void LegacyBundles(CaseDef c, CaseResult r, RunInfo info) {
            string dir = CaseDir(c);
            AssetBundleManifest manifest = null;
            try {
                manifest = BuildPipeline.BuildAssetBundles(dir,
                    new[] { new AssetBundleBuild { assetBundleName = "stand", assetNames = new[] { StandPaths.Data } } },
                    BuildAssetBundleOptions.None, StandDefines.ActiveTarget);
            } catch (Exception e) {
                r.evidence.exception = StandText.Trim(e.ToString(), 1500);
            }
            ScanErrors(StandLog.Since(0), r, info);
            bool success = manifest != null;
            r.evidence.success = success;
            r.evidence.buildResult = success ? "manifest" : "manifest=null";
            r.actual = Classify(success, r);
        }

        private static void SbpBundles(CaseDef c, CaseResult r, RunInfo info) {
#if STAND_SBP
            string dir = CaseDir(c);
            var parameters = new BundleBuildParameters(StandDefines.ActiveTarget, StandDefines.ActiveGroup, dir) { UseCache = false };
            r.applied.options = "ScriptOptions=" + parameters.ScriptOptions;
            var content = new BundleBuildContent(new[] {
                new AssetBundleBuild { assetBundleName = "stand", assetNames = new[] { StandPaths.Data } }
            });
            ReturnCode code = ReturnCode.Exception;
            try {
                code = ContentPipeline.BuildAssetBundles(parameters, content, out IBundleBuildResults _);
            } catch (Exception e) {
                r.evidence.exception = StandText.Trim(e.ToString(), 1500);
            }
            r.evidence.sbpReturnCode = code.ToString();
            ScanErrors(StandLog.Since(0), r, info);
            bool success = code == ReturnCode.Success;
            r.evidence.success = success;
            r.evidence.buildResult = "ReturnCode=" + code;
            r.actual = Classify(success, r);
#else
            throw new StandSkipped("no com.unity.scriptablebuildpipeline");
#endif
        }

        private static void AaContent(CaseDef c, CaseResult r, RunInfo info) {
#if STAND_AA
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) {
                throw new StandInconclusive("no AddressableAssetSettings (setup did not create them)");
            }
            AddressablesPlayerBuildResult result = null;
            try {
                AddressableAssetSettings.BuildPlayerContent(out result);
            } catch (Exception e) {
                r.evidence.exception = StandText.Trim(e.ToString(), 1500);
            }
            r.evidence.aaError = result == null ? "no result" : result.Error ?? "";
            ScanErrors(StandLog.Since(0), r, info);
            bool success = result != null && string.IsNullOrEmpty(result.Error);
            r.evidence.success = success;
            r.evidence.buildResult = success ? "OK" : "Error=" + r.evidence.aaError;
            r.actual = Classify(success, r);
#else
            throw new StandSkipped("Addressables are not installed");
#endif
        }

        // ---------------------------------------------------------------- a player that runs

        // A full macOS player (no stop after the scripts), started headless: StandGame.Start runs
        // StandGame.Smoke() and quits with 0 (OK) or 3 (FAIL). The group runs with -buildTarget OSXUniversal.
        private static void PlayerRun(CaseDef c, CaseResult r, RunInfo info) {
            if (StandDefines.ActiveTarget != BuildTarget.StandaloneOSX) {
                throw new StandInconclusive("the run group starts with -buildTarget OSXUniversal, the active target is " + StandDefines.ActiveTarget);
            }
            var named = NamedBuildTarget.Standalone;
            var backendBefore = PlayerSettings.GetScriptingBackend(named);
            var backend = c.backend == "il2cpp" ? ScriptingImplementation.IL2CPP : ScriptingImplementation.Mono2x;
            try {
                PlayerSettings.SetScriptingBackend(named, backend);
                bool arm = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64;
                r.checks.Add("note: macOS architecture: " + SetMacArchitecture(arm ? "ARM64" : "x64"));
                string dir = CaseDir(c);
                string app = Path.Combine(dir, "Stand.app");
                var options = c.development ? BuildOptions.Development : BuildOptions.None;
                r.applied.options = StandText.Flags(options) + ", backend " + backend;
                BuildReport report = null;
                StandHooks.Arm();
                StandHooks.AbortAfterScripts = false;
                try {
                    report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
                        scenes = new[] { StandPaths.Scene },
                        locationPathName = app,
                        target = BuildTarget.StandaloneOSX,
                        targetGroup = BuildTargetGroup.Standalone,
                        options = options,
                        extraScriptingDefines = StandText.Split(c.extraDefines)
                    });
                } catch (Exception e) {
                    r.evidence.exception = StandText.Trim(e.ToString(), 1500);
                } finally {
                    StandHooks.Disarm();
                    StandHooks.AbortAfterScripts = true;
                }
                r.evidence.scriptsCompiled = StandHooks.ScriptsCompiled;
                r.evidence.markers = StandHooks.Markers;
                r.evidence.kenseiDebugCode = StandHooks.KenseiDebugCode;
                r.evidence.buildResult = report == null ? "no report" : report.summary.result.ToString();
                ScanErrors(StandLog.Since(0), r, info);
                if (report == null || report.summary.result != BuildResult.Succeeded) {
                    r.actual = Classify(false, r);
                    return;
                }
                string exe = PlayerExe(app);
                var run = RunPlayer(exe, Path.Combine(dir, "player.log"));
                r.evidence.logLines.Add("player: " + exe + ", exit code " + run.exit + ", log " + run.log);
                if (!run.ok && !run.reported) {
                    // No result line: a native crash or an exit before StandGame ran. Run the same player again,
                    // then, if it fails again, a control player without KenseiECS code, before blaming KenseiECS.
                    r.checks.Add("note: run 1: exit code " + run.exit + ", no [KENSEI-STAND-PLAYER] line" + run.crashNote);
                    var again = RunPlayer(exe, Path.Combine(dir, "player-2.log"));
                    r.evidence.logLines.Add("player, run 2: exit code " + again.exit + ", log " + again.log);
                    if (again.ok) {
                        r.checks.Add("note: run 2 passed: the failure of run 1 did not repeat (a flaky player failure, not a result of KenseiECS)");
                    } else if (!again.reported) {
                        r.checks.Add("note: run 2: exit code " + again.exit + ", no [KENSEI-STAND-PLAYER] line" + again.crashNote);
                        string control = ControlRun(dir, options, r);
                        if (control != null) {
                            throw new StandInconclusive("the player fails without a result (exit codes " + run.exit + ", " + again.exit +
                                "), and the control player without KenseiECS code (" + StandPaths.ControlScene + ") did not rule the environment out: " + control +
                                " - the failure cannot be put on KenseiECS (environment or Unity)");
                        }
                        r.checks.Add("note: the control player without KenseiECS code and the same settings ran: the failure belongs to the KenseiECS scene");
                    }
                    run = again;
                }
                r.evidence.playerExit = run.exit;
                r.evidence.result = run.result;
                r.evidence.playerLog = run.excerpt;
                r.actual = run.ok ? "ran" : "run-failed";
            } finally {
                PlayerSettings.SetScriptingBackend(named, backendBefore);
            }
        }

        private static string PlayerExe(string app) {
            string exe = Directory.Exists(Path.Combine(app, "Contents", "MacOS"))
                ? Directory.GetFiles(Path.Combine(app, "Contents", "MacOS")).FirstOrDefault()
                : null;
            if (exe == null) {
                throw new StandInconclusive("no Contents/MacOS/<player> in " + app);
            }
            return exe;
        }

        private sealed class PlayerRunResult {
            public int exit = -1;
            public string log = "";
            public string result = "";      // text after [KENSEI-STAND-PLAYER]
            public string excerpt = "";
            public bool reported;           // the player logged a result line
            public bool ok;                 // exit 0 and "OK ..."
            public string crashNote = "";
        }

        // Starts the player headless and waits up to 180 s for it.
        private static PlayerRunResult RunPlayer(string exe, string log) {
            var res = new PlayerRunResult { log = log };
            if (File.Exists(log)) {
                File.Delete(log);
            }
            var started = DateTime.Now;
            var psi = new System.Diagnostics.ProcessStartInfo(exe, "-batchmode -nographics -logFile \"" + log + "\"") {
                UseShellExecute = false, CreateNoWindow = true
            };
            using (var p = System.Diagnostics.Process.Start(psi)) {
                if (!p.WaitForExit(180000)) {
                    try {
                        p.Kill();
                    } catch (Exception) {
                        // Already gone.
                    }
                    throw new StandInconclusive("the player did not exit within 180 s (" + exe + ")");
                }
                res.exit = p.ExitCode;
            }
            string text = File.Exists(log) ? File.ReadAllText(log) : "";
            const string tag = "[KENSEI-STAND-PLAYER]";
            var line = text.Split('\n').FirstOrDefault(l => l.Contains(tag));
            res.reported = line != null;
            res.result = line == null ? "" : line.Substring(line.IndexOf(tag, StringComparison.Ordinal) + tag.Length).Trim();
            res.excerpt = StandText.Trim(string.Join("\n", text.Split('\n').Where(l =>
                l.Contains(tag) || l.Contains("Exception") || l.Contains("Error")).Take(20)), 3000);
            res.ok = res.exit == 0 && res.result.StartsWith("OK");
            if (!res.reported) {
                res.crashNote = CrashReport(Path.GetFileName(exe), started);
            }
            return res;
        }

        // macOS writes a crash report per crashed process (~/Library/Logs/DiagnosticReports/<name>-<time>.ips).
        private static string CrashReport(string processName, DateTime since) {
            try {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Library", "Logs", "DiagnosticReports");
                if (!Directory.Exists(dir)) {
                    return "";
                }
                var report = new DirectoryInfo(dir).GetFiles(processName + "-*.ips")
                    .Where(f => f.LastWriteTime >= since.AddSeconds(-2)).OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
                return report == null ? "" : "; crash report " + report.Name;
            } catch (Exception) {
                return "";
            }
        }

        // The control: the same build options and backend, the scene with StandControl only. Null when it ran
        // fine; otherwise what went wrong.
        private static string ControlRun(string dir, BuildOptions options, CaseResult r) {
            if (!File.Exists(StandPaths.ControlScene)) {
                return "no control scene (the setup did not create it), nothing to compare with";
            }
            string app = Path.Combine(dir, "control", "Stand.app");
            BuildReport report;
            try {
                report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
                    scenes = new[] { StandPaths.ControlScene },
                    locationPathName = app,
                    target = BuildTarget.StandaloneOSX,
                    targetGroup = BuildTargetGroup.Standalone,
                    options = options
                });
            } catch (Exception e) {
                return "the control did not build: " + StandText.Trim(e.Message, 300);
            }
            if (report.summary.result != BuildResult.Succeeded) {
                return "the control did not build: " + report.summary.result;
            }
            var run = RunPlayer(PlayerExe(app), Path.Combine(dir, "control", "player.log"));
            r.evidence.logLines.Add("control player: exit code " + run.exit + ", " + (run.reported ? run.result : "no result line") + run.crashNote);
            return run.exit == 0 && run.result.StartsWith("CONTROL OK") ? null : "exit code " + run.exit + (run.reported ? ", " + run.result : ", no result line") + run.crashNote;
        }

        // UnityEditor.OSXStandalone.UserBuildSettings.architecture (Unity 2022+): one architecture keeps IL2CPP short.
        private static string SetMacArchitecture(string value) {
            var type = StandTypes.Find("UnityEditor.OSXStandalone.UserBuildSettings");
            var prop = type?.GetProperty("architecture", BindingFlags.Public | BindingFlags.Static);
            if (prop == null || !prop.CanWrite || !prop.PropertyType.IsEnum) {
                return "not set (no UserBuildSettings.architecture)";
            }
            try {
                prop.SetValue(null, Enum.Parse(prop.PropertyType, value));
                return prop.GetValue(null).ToString();
            } catch (Exception e) {
                return "not set: " + e.Message;
            }
        }

        // ---------------------------------------------------------------- error scan

        private static readonly Regex CsError = new Regex(@"[^\s:()]+\.cs\(\d+,\d+\): error CS\d+");
        private static readonly Regex CsWarning = new Regex(@"[^\s:()]+\.cs\(\d+,\d+\): warning [A-Z]+\d+");

        internal static void ScanErrors(List<LogEntry> entries, CaseResult r, RunInfo info) {
            var e = r.evidence;
            foreach (var entry in entries) {
                if (entry.text.StartsWith(StandLog.Prefix)) {
                    continue;
                }
                if (!StandLog.IsError(entry)) {
                    if (entry.type == LogType.Warning) {
                        CollectWarning(entry.text, e, info);
                    }
                    continue;
                }
                string text = entry.text;
                if (text.Contains(StandHooks.AbortMarker)) {
                    continue;
                }
                if (e.errors.Count < 25) {
                    e.errors.Add(StandText.Trim(text, 500));
                }
                foreach (var raw in text.Replace("\r", "").Split('\n')) {
                    var line = raw.Trim();
                    if (!CsError.IsMatch(line)) {
                        if (CsWarning.IsMatch(line)) {
                            CollectWarning(line, e, info);
                        }
                        continue;
                    }
                    bool inPackage = IsPackagePath(line, info);
                    if (line.Contains("error CS1029") && inPackage) {
                        if (!e.cs1029) {
                            e.cs1029 = true;
                            e.cs1029Line = StandText.Trim(line, 500);
                        }
                    } else if (inPackage) {
                        if (!e.kenseiCompileErrors.Contains(line) && e.kenseiCompileErrors.Count < 10) {
                            e.kenseiCompileErrors.Add(StandText.Trim(line, 400));
                        }
                    } else if (line.Contains("Assets/KenseiStand")) {
                        if (!e.standCompileErrors.Contains(line) && e.standCompileErrors.Count < 10) {
                            e.standCompileErrors.Add(StandText.Trim(line, 400));
                        }
                    }
                }
                if (text.Contains("error CS")) {
                    continue;
                }
                // A BuildFailedException of KenseiECS: the preprocess guard, or a check that runs after the
                // player scripts compiled (2.2.1: KenseiDebugPlayerScriptsCheck). Told apart by the stack,
                // else by whether the scripts had compiled when it was thrown.
                bool byGuard = entry.stack.Contains("KenseiDebugBuildGuard") || text.Contains("KenseiDebugBuildGuard");
                bool byCheck = entry.stack.Contains("KenseiDebugPlayerScriptsCheck") || text.Contains("KenseiDebugPlayerScriptsCheck");
                bool kensei = byGuard || byCheck ||
                    (text.Contains("KenseiECS") && text.Contains(StandDefines.DebugDefine) && text.Contains("BuildFailedException"));
                if (!kensei) {
                    continue;
                }
                if (!byGuard && !byCheck) {
                    byCheck = StandHooks.PostCompileReached || StandHooks.ScriptsCompiled;
                    byGuard = !byCheck;
                }
                if (byGuard && e.guardMessage.Length == 0) {
                    e.guardMessage = StandText.Trim(text, 1200);
                } else if (byCheck && e.checkMessage.Length == 0) {
                    e.checkMessage = StandText.Trim(text, 1200);
                }
            }
        }

        // A warning of KenseiECS: a log line of the package ("KenseiECS: ...") or a compiler warning in one of its files.
        private static void CollectWarning(string text, Evidence e, RunInfo info) {
            foreach (var raw in text.Replace("\r", "").Split('\n')) {
                string line = raw.Trim();
                if (line.Length == 0) {
                    continue;
                }
                bool ours = line.StartsWith("KenseiECS:") || (CsWarning.IsMatch(line) && IsPackagePath(line, info));
                if (!ours) {
                    continue;
                }
                string item = StandText.Trim(line, 400);
                if (!e.kenseiWarnings.Contains(item) && e.kenseiWarnings.Count < 10) {
                    e.kenseiWarnings.Add(item);
                }
            }
        }

        internal static string Classify(bool success, CaseResult r) {
            var e = r.evidence;
            if (success && e.kenseiCompileErrors.Count == 0 && !e.cs1029 && e.guardMessage.Length == 0 && e.checkMessage.Length == 0) {
                return "compiled";
            }
            if (e.guardMessage.Length > 0) {
                return "blocked-guard";
            }
            if (e.cs1029) {
                return "blocked-error";
            }
            if (e.checkMessage.Length > 0) {
                return "blocked-check";
            }
            if (e.kenseiCompileErrors.Count > 0) {
                return "kensei-compile-error";
            }
            if (e.standCompileErrors.Count > 0) {
                return "stand-compile-error";
            }
            return success ? "compiled" : "failed-other";
        }
    }
}
