#!/usr/bin/env python3
"""KenseiECS test stand, module unity: turns the raw results of one run into report.md, report.json
and one C# repro per finding.

  report.py --raw <dir> --out <dir> --cases <cases.json> --rules <rules.json> --meta <meta.json>
            [--pkg <package dir> --docs <docs dir> --known <known.json>]
            [--baseline-report <report.json of the baseline> --baseline-label <label>]

<raw> holds what Unity wrote (setup.json, groups/*.json, cases/<id>.json|.log, repro-verify/*.json)
and what run.sh wrote (meta: one entry per Unity process). Findings are case violations grouped by
signature (rule + cause, the cause being the mechanism and the place: "blocked-error:CS1029@DevTools/
KenseiDebugReleaseCheck.cs"), so one defect seen in several cases is one finding that lists them
("hits"). Whether a violation is a defect of the tree under test is decided by its version profile and
contract quotes (lib/contract.py): a promise newer than the tree, or not in its documents, makes it a
question. With a baseline report the findings are compared by signature (new, known, grown) and a
case that was fine on the baseline and changed without breaking a contract (a new question, a new
KenseiECS warning, a source its stop message no longer names) is a regression, a status of its own.
The report also writes raw/repro-plan.json: the build repros that StandRunner.VerifyRepros runs; on a
second call (after that process) their results go into the report.
"""
import argparse
import glob
import hashlib
import json
import os
import re
import shutil
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import contract  # noqa: E402  (lib/contract.py, next to this file)

HERE = os.path.dirname(os.path.abspath(__file__))
STAND = os.path.dirname(os.path.dirname(HERE))

# Kinds whose repro the stand runs in Unity and classifies (StandRunner.VerifyRepros).
BUILD_REPROS = ("player", "sbp-compile", "legacy-bundles", "sbp-bundles", "aa-content")
VERIFIABLE = BUILD_REPROS + ("editor-spawn",)
# Repros of these kinds are runtime scripts (their MonoBehaviours must be in a runtime assembly).
RUNTIME_REPROS = ("editor-spawn",)
BUILD_KINDS = BUILD_REPROS + ("player-run",)
VERDICT_TEXT = {
    "ok": "ok",
    "finding": "FINDING",
    "question": "question",
    "regression": "regression",
    "na": "n/a",
    "skipped": "skipped",
    "inconclusive": "inconclusive",
}
SEVERITY_ORDER = {"high": 0, "medium": 1, "low": 2}
# A signature whose hits grew at least this much against the baseline is reported as grown.
GROWTH_FACTOR = 2
GROWTH_MIN = 2


def load_json(path, default=None):
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError, TypeError):
        return default


def slug(text):
    return re.sub(r"[^A-Za-z0-9]+", "_", text).strip("_") or "x"


def shorten(text, n):
    text = text or ""
    return text if len(text) <= n else text[: n - 3].rstrip() + "..."


def short_hash(text):
    return hashlib.sha1(text.encode("utf-8")).hexdigest()[:6]


def cs_string(text):
    return '"' + text.replace("\\", "\\\\").replace('"', '\\"').replace("\n", " ") + '"'


def cs_array(items):
    return "new[] { " + ", ".join(cs_string(x) for x in items) + " }" if items else "new string[0]"


def comment_block(lines):
    out = []
    for line in lines:
        for part in str(line).split("\n"):
            out.append("// " + part if part else "//")
    return "\n".join(out)


class Sanitizer:
    """Absolute paths out of the texts of the report: the package copy becomes <pkg>, the stand Stand/,
    the home folder ~ (machine fields such as "src" keep the absolute path the summary compares)."""

    def __init__(self, meta):
        pairs = []
        for path, name in ((meta.get("pkg"), "<pkg>"), (meta.get("src"), "<src>"), (meta.get("project"), "<project>"),
                           (STAND, "Stand"), (os.path.expanduser("~"), "~")):
            if path and len(path) > 3:
                pairs.append((os.path.abspath(path), name))
                pairs.append((os.path.abspath(path).replace("\\", "/"), name))
        self.pairs = sorted(set(pairs), key=lambda p: -len(p[0]))

    def __call__(self, value):
        if isinstance(value, str):
            for path, name in self.pairs:
                value = value.replace(path, name)
            return value
        if isinstance(value, list):
            return [self(v) for v in value]
        if isinstance(value, dict):
            return {k: self(v) for k, v in value.items()}
        return value


def norm_warning(text):
    """A KenseiECS warning without line numbers and the folder of the package, for comparisons."""
    t = re.sub(r"\(\d+,\d+\)", "", text or "")
    t = t.replace("\\", "/")
    t = re.sub(r"^.*?(/pkg/|Packages/com\.kensei\.ecs/)", "", t)
    return t.strip()


# ------------------------------------------------------------------------------------------- repro


def named_target(target):
    return {"Android": "Android", "iOS": "iOS", "StandaloneOSX": "Standalone", "StandaloneWindows64": "Standalone",
            "StandaloneLinux64": "Standalone"}.get(target, "Standalone")


def target_group(target):
    return {"Android": "Android", "iOS": "iOS"}.get(target, "Standalone")


def repro_class(finding_id):
    return "Repro_" + slug(finding_id)


def spawn_repro_source(finding, case, setup, meta, rule):
    """A self-contained runtime script: the providers are MonoBehaviours, which Unity adds only from a runtime
    assembly, so this repro goes outside an Editor folder (the stand puts it into Assets/KenseiStandRepro/Runtime/)."""
    fid = finding["id"]
    cls = repro_class(fid)
    header = [
        "Repro %s: %s" % (fid, rule.get("title", finding["rule"])),
        "KenseiECS test stand, module unity, case %s: %s" % (case["id"], case.get("title", "")),
        "Unity %s, com.kensei.ecs %s (%s). No seed: the scenario is deterministic." % (
            setup.get("unityVersion", "?"), setup.get("packageVersion", "?"), meta.get("flavor", "?")),
        "",
        "Expected (contract): " + rule.get("contract", ""),
        "Observed: " + finding["detail"],
        "",
        "Place: runtime. Put this file into Assets/ of a project with com.kensei.ecs, outside any Editor folder",
        "(its MonoBehaviours must be in a runtime assembly), then call KenseiRepro.%s.Run() from anywhere," % cls,
        "or menu Repro/%s in the editor, or play mode. Result: the [REPRO] lines of the log;" % fid,
        "\"defect=True\" - the outer entity lost provider components: the defect is reproduced.",
    ]
    names = ["ReproSpawnA", "ReproSpawnB", "ReproSpawnC", "ReproSpawnInner"]
    src = [comment_block(header), "", "using System;", "using System.Collections.Generic;", "using KenseiECS;", "using UnityEngine;", "",
           "namespace KenseiRepro {"]
    for n in names:
        src += ["    [Serializable] public struct %s : IComponent { public int Value; }" % n]
    src += [""]
    for n in names:
        src += ["    public sealed class %sProvider : EcsComponentProvider<%s> { }" % (n, n)]
    src += [
        "",
        "    // Spawns the inner view from the first OnEntityCreated: the creation of the outer entity, which the first",
        "    // provider makes before the other providers add their components.",
        "    sealed class %sListener : IWorldEventListener {" % cls,
        "        public World World; public EcsEntityView Inner; public bool Fired; public Entity InnerEntity;",
        "        public void OnEntityCreated(int entityIndex) { if (Fired) return; Fired = true; InnerEntity = Inner.Spawn(World); }",
        "        public void OnEntityDestroyed(int entityIndex) { }",
        "        public void OnComponentAdded(int entityIndex, int typeIndex) { }",
        "        public void OnComponentRemoved(int entityIndex, int typeIndex) { }",
        "    }",
        "",
        "    public static class %s {" % cls,
        "#if UNITY_EDITOR",
        '        [UnityEditor.MenuItem("Repro/%s")]' % fid,
        "#endif",
        "        public static void Run() {",
        "            var world = new World();",
        '            var outerObject = new GameObject("ReproOuter") { hideFlags = HideFlags.HideAndDontSave };',
        '            var innerObject = new GameObject("ReproInner") { hideFlags = HideFlags.HideAndDontSave };',
        "            try {",
        "                var outer = outerObject.AddComponent<EcsEntityView>();",
        "                outerObject.AddComponent<ReproSpawnAProvider>();",
        "                outerObject.AddComponent<ReproSpawnBProvider>();",
        "                outerObject.AddComponent<ReproSpawnCProvider>();",
        "                var inner = innerObject.AddComponent<EcsEntityView>();",
        "                innerObject.AddComponent<ReproSpawnInnerProvider>();",
        "                var listener = new %sListener { World = world, Inner = inner };" % cls,
        "                world.AddEventListener(listener);",
        "                Entity entity = outer.Spawn(world);",
        "                world.RemoveEventListener(listener);",
        "                var has = new List<string>();",
        '                if (world.Has<ReproSpawnA>(entity)) has.Add("A");',
        '                if (world.Has<ReproSpawnB>(entity)) has.Add("B");',
        '                if (world.Has<ReproSpawnC>(entity)) has.Add("C");',
        '                bool defect = has.Count != 3;',
        '                Debug.Log("[REPRO] %s outer=[" + string.Join(",", has) + "] (contract: [A,B,C]), inner spawned=" + listener.Fired + " defect=" + defect);' % fid,
        "            } finally {",
        "                UnityEngine.Object.DestroyImmediate(outerObject);",
        "                UnityEngine.Object.DestroyImmediate(innerObject);",
        "                world.Destroy();",
        "            }",
        "        }",
        "    }",
        "}",
        "",
    ]
    return "\n".join(src)


def repro_source(finding, case, cdef, setup, meta, rule, clean):
    if case.get("kind") == "editor-spawn":
        return spawn_repro_source(finding, case, setup, meta, rule)
    applied = case.get("applied", {})
    ev = case.get("evidence", {})
    target = setup.get("target", "Android")
    nt = named_target(target)
    grp = target_group(target)
    cls = repro_class(finding["id"])
    kind = case.get("kind", "")
    uses_aa = kind == "aa-content" or bool(cdef.get("aaWithPlayer"))
    uses_sbp = kind == "sbp-bundles"
    fid = finding["id"]
    params = {
        "psDefines(" + nt + ")": cdef.get("psDefines", "*"),
        "debugMode": cdef.get("debugMode", ""),
        "profile": cdef.get("profile", ""),
        "profileInactive": cdef.get("profileInactive", False),
        "profileDefines": cdef.get("profileDefines", ""),
        "extraScriptingDefines": cdef.get("extraDefines", ""),
        "csc.rsp": cdef.get("rspDefines", ""),
        "additionalCompilerArguments": cdef.get("argsDefines", "") or cdef.get("rawArgs", ""),
        "development": cdef.get("development", False),
        "devTicked": cdef.get("devTicked", False),
        "aaWithPlayer": cdef.get("aaWithPlayer", ""),
        "buildVia": cdef.get("buildVia", "options"),
    }
    params = {k: v for k, v in params.items() if v not in ("", False, "*", "options")}
    header = [
        "Repro %s: %s" % (finding["id"], rule.get("title", finding["rule"])),
        "KenseiECS test stand, module unity, case %s: %s" % (case["id"], case.get("title", "")),
        "Unity %s, com.kensei.ecs %s (%s), target %s, scripting backend %s"
        % (setup.get("unityVersion", "?"), setup.get("packageVersion", "?"), meta.get("flavor", "?"), target, setup.get("backend", "?"))
        + (", Addressables %s, SBP %s" % (setup.get("aaVersion", "?"), setup.get("sbpVersion", "?")) if uses_aa or uses_sbp or kind == "sbp-compile" else ""),
        "Editor: %s." % ("Debug Mode on (baseline dbg)" if meta.get("cfg") == "dbg" else "Debug Mode off (baseline rel)"),
        "No seed: the scenario is deterministic. Case parameters: %s" % json.dumps(params, ensure_ascii=False),
        "State the case ran with: Player Settings %s = %s; profile %s; toggle: %s"
        % (applied.get("target", nt), ";".join(applied.get("psActive", [])) or "(empty)",
           (applied.get("profileName") + " " + str(applied.get("profileDefines"))) if applied.get("profileName") else "none",
           applied.get("debugMode") or "not used"),
        "",
        "Expected (contract): " + rule.get("contract", ""),
        "Observed: " + finding["detail"],
    ]
    if ev.get("cs1029Line"):
        header.append("Error: " + clean(ev["cs1029Line"]))
    if ev.get("guardMessage"):
        header.append("Guard error: " + clean(ev["guardMessage"][:400]))
    if ev.get("checkMessage"):
        header.append("Check error: " + clean(ev["checkMessage"][:400]))
    if ev.get("aaError"):
        header.append("Addressables: " + clean(ev["aaError"]))
    blocked = case.get("actual", "").startswith("blocked")
    header += [
        "",
        "Run: put this file into Assets/Editor/ of a project with com.kensei.ecs%s and one scene in Build Settings"
        % (" and com.unity.addressables %s (default group with any asset)" % setup.get("aaVersion", "") if uses_aa else ""),
        "(a scratch project: the repro changes Player Settings and build settings), then",
        "  Unity -batchmode -nographics -quit -projectPath <project> -buildTarget %s -executeMethod KenseiRepro.%s.Run" % (target, cls),
        ("or menu Repro/%s. Result: the [REPRO] lines of the log; \"success=False\" and a KenseiECS error in the log - the defect is reproduced" % fid)
        if blocked else
        ("or menu Repro/%s. Result: the [REPRO] lines of the log; \"success=True\" - the defect is reproduced (the build was not stopped)" % fid),
    ]
    if cdef.get("profile"):
        header += [
            "The Build Profile is made in code (internal API of Unity 6, as File > Build Profiles > Add Build Profile): %s, %sScripting Defines = %s%s."
            % (target, "Development Build on, " if cdef.get("profile") == "dev" else "", cdef.get("profileDefines") or "(empty)",
               ", NOT active" if cdef.get("profileInactive") else ", Make Active"),
        ]

    usings = ["System", "UnityEditor", "UnityEditor.Build", "UnityEditor.Build.Reporting", "UnityEditor.Build.Player"]
    if cdef.get("profile") or kind.startswith("toggle-profile") or kind == "toggle-ownps":
        usings.append("UnityEditor.Build.Profile")
    if uses_aa:
        usings += ["UnityEditor.AddressableAssets", "UnityEditor.AddressableAssets.Build", "UnityEditor.AddressableAssets.Settings"]
    if uses_sbp:
        usings += ["UnityEditor.Build.Pipeline", "UnityEditor.Build.Pipeline.Interfaces"]

    body = []
    exp = case.get("expected", "")
    if target == "Android" and kind in VERIFIABLE:
        body.append("EditorUserBuildSettings.exportAsGoogleAndroidProject = true; // like the stand: export a Gradle project, no Gradle run")
    # define sources, in the order the stand applies them
    if cdef.get("psDefines", "*") != "*":
        defs = [d for d in cdef["psDefines"].split(";") if d]
        body.append("PlayerSettings.SetScriptingDefineSymbols(NamedBuildTarget.%s, %s);" % (nt, cs_array(defs)))
    if cdef.get("profile"):
        pdefs = [d for d in cdef.get("profileDefines", "").split(";") if d]
        body.append("var profile = MakeProfile(%s, %s, %s);" % (cs_string("Repro_" + case["id"].replace("-", "_")),
                                                              "true" if cdef.get("profile") == "dev" else "false", cs_array(pdefs)))
        if cdef.get("profileInactive"):
            body.append("BuildProfile.SetActiveBuildProfile(null); // the profile is NOT active; BuildPlayerWithProfileOptions must activate it")
        else:
            body.append("BuildProfile.SetActiveBuildProfile(profile);")
    if cdef.get("rspDefines"):
        defs = [d for d in cdef["rspDefines"].split(";") if d]
        body.append('System.IO.File.WriteAllText("Assets/csc.rsp", %s);' % cs_string("\\n".join("-define:" + d for d in defs) + "\\n"))
        body.append('AssetDatabase.ImportAsset("Assets/csc.rsp");')
    args = ["-define:" + d for d in cdef.get("argsDefines", "").split(";") if d] + [a for a in cdef.get("rawArgs", "").split("|") if a]
    if args:
        body.append("PlayerSettings.SetAdditionalCompilerArguments(NamedBuildTarget.%s, %s); // Player Settings -> Additional Compiler Arguments"
                    % (nt, cs_array(args)))
    if kind in ("legacy-bundles", "sbp-bundles"):
        body.append('System.IO.File.WriteAllText("Assets/ReproData.txt", "repro"); // any asset for the bundle')
        body.append('AssetDatabase.ImportAsset("Assets/ReproData.txt");')
    if cdef.get("devTicked"):
        body.append("EditorUserBuildSettings.development = true; // the Development Build checkbox of Build Settings")
    if uses_aa:
        body.append("var aa = AddressableAssetSettingsDefaultObject.GetSettings(true);")
        option = {"yes": "BuildWithPlayer", "no": "DoNotBuildWithPlayer", "default": "PreferencesValue"}.get(cdef.get("aaWithPlayer", ""))
        if option:
            body.append("aa.BuildAddressablesWithPlayerBuild = AddressableAssetSettings.PlayerBuildOption.%s;%s"
                        % (option, " // the default of new settings: the Preferences decide (on unless turned off)" if option == "PreferencesValue" else ""))
    if cdef.get("debugMode") in ("on", "off"):
        body.append("// Debug Mode %s through the menu of the package; the stand's toggle wrote: %s" % (cdef["debugMode"], applied.get("debugMode") or "?"))
        body.append('if (KenseiDebugIsOn() != %s) EditorApplication.ExecuteMenuItem("KenseiECS/Debug Mode");' % ("true" if cdef["debugMode"] == "on" else "false"))

    opt_player = "BuildOptions.Development" if cdef.get("development") else "BuildOptions.None"
    extra = [d for d in cdef.get("extraDefines", "").split(";") if d]
    if kind == "player":
        if cdef.get("buildVia") == "profile":
            body += [
                "var report = BuildPipeline.BuildPlayer(new BuildPlayerWithProfileOptions {",
                '    buildProfile = profile, locationPathName = "Builds/Repro", options = %s' % opt_player,
                "});",
            ]
        else:
            body += [
                "var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {",
                "    scenes = new[] { EditorBuildSettings.scenes[0].path },",
                '    locationPathName = "Builds/Repro",',
                "    target = BuildTarget.%s, targetGroup = BuildTargetGroup.%s," % (target, grp),
                "    options = %s," % opt_player,
                "    extraScriptingDefines = %s" % cs_array(extra),
                "});",
            ]
        want = "Succeeded" if exp == "compiled" else "Failed (guard, check after the compile or #error of KenseiECS)"
        body.append('UnityEngine.Debug.Log("[REPRO] %s: " + report.summary.result + ", errors " + report.summary.totalErrors + " (contract: %s)");' % (fid, want))
        body.append('UnityEngine.Debug.Log("[REPRO] %s success=" + (report.summary.result == BuildResult.Succeeded));' % fid)
    elif kind == "sbp-compile":
        opt = "ScriptCompilationOptions.DevelopmentBuild" if cdef.get("development") else "ScriptCompilationOptions.None"
        body += [
            "// This is how a content build compiles the scripts: SBP (BuildPlayerScripts) and Addressables pass ScriptOptions=None.",
            "var result = PlayerBuildInterface.CompilePlayerScripts(new ScriptCompilationSettings {",
            "    target = BuildTarget.%s, group = BuildTargetGroup.%s, options = %s, extraScriptingDefines = %s" % (target, grp, opt, cs_array(extra)),
            '}, "Temp/ReproScripts");',
            'int n = result.assemblies == null ? 0 : result.assemblies.Count;',
            'UnityEngine.Debug.Log("[REPRO] %s: assemblies=" + n + " (contract: > 0; with the defect the log has a KenseiECS error)");' % fid,
            'UnityEngine.Debug.Log("[REPRO] %s success=" + (n > 0 && result.typeDB != null));' % fid,
        ]
    elif kind == "aa-content":
        body += [
            "AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult result);",
            'UnityEngine.Debug.Log("[REPRO] %s: Error=\\"" + result.Error + "\\" (contract: empty)");' % fid,
            'UnityEngine.Debug.Log("[REPRO] %s success=" + string.IsNullOrEmpty(result.Error));' % fid,
        ]
    elif kind == "legacy-bundles":
        body += [
            "var manifest = BuildPipeline.BuildAssetBundles(\"Temp/ReproBundles\",",
            '    new[] { new AssetBundleBuild { assetBundleName = "repro", assetNames = new[] { "Assets/ReproData.txt" } } },',
            "    BuildAssetBundleOptions.None, BuildTarget.%s);" % target,
            'UnityEngine.Debug.Log("[REPRO] %s: manifest=" + (manifest != null) + " (contract: True)");' % fid,
            'UnityEngine.Debug.Log("[REPRO] %s success=" + (manifest != null));' % fid,
        ]
    elif kind == "sbp-bundles":
        body += [
            'var parameters = new BundleBuildParameters(BuildTarget.%s, BuildTargetGroup.%s, "Temp/ReproSbp") { UseCache = false };' % (target, grp),
            "var content = new BundleBuildContent(new[] {",
            '    new AssetBundleBuild { assetBundleName = "repro", assetNames = new[] { "Assets/ReproData.txt" } }',
            "});",
            "var code = ContentPipeline.BuildAssetBundles(parameters, content, out IBundleBuildResults _);",
            'UnityEngine.Debug.Log("[REPRO] %s: ReturnCode=" + code + " (contract: Success)");' % fid,
            'UnityEngine.Debug.Log("[REPRO] %s success=" + (code == ReturnCode.Success));' % fid,
        ]
    elif kind == "player-run":
        backend = "IL2CPP" if cdef.get("backend") == "il2cpp" else "Mono2x"
        body += [
            "// A whole macOS player (no stop after the scripts); a scene with a component that calls the same code",
            "// as StandGame.Smoke() of the stand (World, SystemsRunner, a system with a generated Init) and quits with 0/3.",
            "PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.%s);" % backend,
            "var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {",
            "    scenes = new[] { EditorBuildSettings.scenes[0].path },",
            '    locationPathName = "Builds/Repro/Stand.app",',
            "    target = BuildTarget.StandaloneOSX, targetGroup = BuildTargetGroup.Standalone,",
            "    options = %s," % opt_player,
            "    extraScriptingDefines = %s" % cs_array(extra),
            "});",
            'UnityEngine.Debug.Log("[REPRO] %s: build " + report.summary.result + "; then: Builds/Repro/Stand.app/Contents/MacOS/<player> -batchmode -nographics -logFile player.log, expected an OK line and exit code 0");' % finding["id"],
            "// What the stand saw: exit code %s, %s" % (ev.get("playerExit"), clean((ev.get("result") or "")[:200])),
        ]
    elif kind.startswith("static"):
        body += [
            "// A package from a git URL is immutable: Unity ignores an asset without a .meta. Check of the package tree:",
            'var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/com.kensei.ecs");',
            "foreach (var path in System.IO.Directory.GetFileSystemEntries(info.resolvedPath, \"*\", System.IO.SearchOption.AllDirectories)) {",
            "    var name = System.IO.Path.GetFileName(path);",
            '    if (name.StartsWith(".") || name.EndsWith("~") || name.EndsWith(".meta") || path.Contains("~/") || path.Contains("/.")) continue;',
            '    if (!System.IO.File.Exists(path + ".meta")) UnityEngine.Debug.LogError("[REPRO] %s: no .meta: " + path);' % finding["id"],
            "}",
        ]
        for line in case.get("checks", [])[:12]:
            body.append("// " + clean(line))
    elif kind.startswith("toggle"):
        body += [
            "// Steps of case %s (baseline: KENSEI_DEBUG removed from the Player Settings of every target):" % case["id"],
        ]
        for line in case.get("evidence", {}).get("logLines", [])[:12]:
            body.append("//   " + clean(line))
        body += [
            'EditorApplication.ExecuteMenuItem("KenseiECS/Debug Mode"); // on',
            'PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.%s, out string[] afterOn);' % nt,
            'UnityEngine.Debug.Log("[REPRO] %s: after on, Player Settings %s = " + string.Join(";", afterOn));' % (finding["id"], nt),
            'EditorApplication.ExecuteMenuItem("KenseiECS/Debug Mode"); // off',
            'PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.%s, out string[] afterOff);' % nt,
            'UnityEngine.Debug.Log("[REPRO] %s: after off, Player Settings %s = " + string.Join(";", afterOff));' % (finding["id"], nt),
        ]
    else:
        body += [
            "// An editor case: open a project with the package (%s) and check:" % ("Debug Mode on" if meta.get("cfg") == "dbg" else "Debug Mode off"),
        ]
        for line in case.get("checks", [])[:12]:
            body.append("//   " + clean(line))
        for line in case.get("evidence", {}).get("kenseiCompileErrors", [])[:6]:
            body.append("//   " + clean(line))
        body.append('UnityEngine.Debug.Log("[REPRO] %s: see the checks above");' % finding["id"])

    helper = []
    if cdef.get("profile"):
        helper += [
            "",
            "        // A Build Profile (Unity 6) in code: internal BuildProfile.CreateInstance(BuildTarget, StandaloneBuildSubtarget),",
            "        // as File > Build Profiles > Add Build Profile. Asset Assets/<name>.asset; delete it after the repro.",
            "        private static BuildProfile MakeProfile(string name, bool development, string[] defines) {",
            "            const System.Reflection.BindingFlags All = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |",
            "                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance;",
            "            var create = typeof(BuildProfile).GetMethod(\"CreateInstance\", All, null, new[] { typeof(BuildTarget), typeof(StandaloneBuildSubtarget) }, null);",
            "            var profile = (BuildProfile)create.Invoke(null, new object[] { BuildTarget.%s, StandaloneBuildSubtarget.Player });" % target,
            '            AssetDatabase.CreateAsset(profile, "Assets/" + name + ".asset");',
            "            profile.scriptingDefines = defines;",
            '            var platform = typeof(BuildProfile).GetProperty("platformBuildProfile", All)?.GetValue(profile);',
            '            platform?.GetType().GetProperty("development", All)?.SetValue(platform, development);',
            '            platform?.GetType().GetProperty("exportAsGoogleAndroidProject", All)?.SetValue(platform, true);',
            "            EditorUtility.SetDirty(profile);",
            "            AssetDatabase.SaveAssetIfDirty(profile);",
            "            return profile;",
            "        }",
        ]
    if cdef.get("debugMode") in ("on", "off"):
        helper += [
            "",
            "        // Debug Mode as the package sees it (internal API; otherwise from the defines of the active target).",
            "        private static bool KenseiDebugIsOn() {",
            '            var t = Type.GetType("KenseiECS.Editor.KenseiDebugToggle, KenseiECS.Editor");',
            '            var m = t?.GetMethod("IsEnabledForActiveTarget", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)',
            '                ?? t?.GetMethod("IsEnabledForCurrentTarget", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);',
            "            if (m != null) return (bool)m.Invoke(null, null);",
            "            PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.%s, out string[] d);" % nt,
            '            return Array.IndexOf(d, "KENSEI_DEBUG") >= 0;',
            "        }",
        ]

    src = [comment_block(header), ""]
    src += ["using %s;" % u for u in usings]
    src += [
        "",
        "namespace KenseiRepro {",
        "    public static class %s {" % cls,
        '        [MenuItem("Repro/%s")]' % finding["id"],
        "        public static void Run() {",
    ]
    src += ["            " + line for line in body]
    src += ["        }"] + helper + ["    }", "}", ""]
    return "\n".join(src)


# ------------------------------------------------------------------------------------------- baseline


def observed(case_result):
    """What a case showed beyond its verdict, for the comparison with a baseline."""
    ev = case_result.get("evidence") or {}
    return {
        "actual": case_result.get("actual", ""),
        "namedSources": sorted(ev.get("namedSources") or []),
        "kenseiWarnings": sorted({norm_warning(w) for w in ev.get("kenseiWarnings") or []}),
    }


def own_sources(definition):
    """The define sources a case's stop message is expected to name (its "expectMessage", "a|b"), without
    placeholders such as {target}."""
    return [p for p in str((definition or {}).get("expectMessage") or "").split("|") if p and "{" not in p]


def compare_baseline(base, findings, questions, ordered, label, path, defs=None):
    """Findings by signature against the baseline, and the regressions of cases that were fine there."""
    out = {"label": label, "report": path, "newSignatures": [], "knownSignatures": [], "grown": [], "goneSignatures": [],
           "regressions": [], "changes": [], "comparableCases": 0}
    base_cases = {c["id"]: c for c in base.get("cases") or []}
    ran = lambda c: c and c.get("verdict") not in (None, "na", "skipped", "missing")
    now_ids = {r["id"] for r in ordered if ran(r)}
    both = {cid for cid in now_ids if ran(base_cases.get(cid))}
    out["comparableCases"] = len(both)
    base_sigs = {}
    for f in base.get("findings") or []:
        sig = f.get("signature") or "%s|%s" % (f.get("rule"), f.get("cause"))
        base_sigs[sig] = f
    for f in findings:
        b = base_sigs.get(f["signature"])
        if b is None:
            f["baseline"] = {"status": "new"}
            out["newSignatures"].append(f["signature"])
            continue
        bh = b.get("hits") or len(b.get("cases") or [])
        status = "known"
        if f["hits"] >= GROWTH_FACTOR * max(bh, 1) and f["hits"] - bh >= GROWTH_MIN:
            status = "grown"
            out["grown"].append({"signature": f["signature"], "hits": f["hits"], "baselineHits": bh})
        else:
            out["knownSignatures"].append(f["signature"])
        f["baseline"] = {"status": status, "hits": bh, "id": b.get("id")}
    now_sigs = {f["signature"] for f in findings}
    for sig, b in base_sigs.items():
        if sig in now_sigs:
            continue
        cases = [c for c in b.get("cases") or [] if c in both]
        if cases:
            out["goneSignatures"].append({"signature": sig, "id": b.get("id"), "cases": cases})

    regressions = []
    for r in ordered:
        cid = r["id"]
        if cid not in both or r.get("verdict") in ("finding",):
            continue
        b = base_cases[cid]
        if b.get("verdict") != "ok":
            continue
        now_obs = observed(r)
        base_obs = b.get("observed")
        if r.get("verdict") == "question":
            texts = [q["text"] for q in questions if cid in q.get("cases", [])]
            regressions.append({"case": cid, "kind": "new-question",
                                "title": "%s: fine on %s, a question to the contract now" % (cid, label),
                                "detail": "; ".join(texts)[:600] or "the case became a question"})
        if base_obs:
            new_w = [w for w in now_obs["kenseiWarnings"] if w not in base_obs.get("kenseiWarnings", [])]
            if new_w:
                regressions.append({"case": cid, "kind": "new-kensei-warning",
                                    "title": "%s: KenseiECS logs a warning it did not log on %s" % (cid, label),
                                    "detail": " | ".join(new_w)[:600]})
            lost = [s for s in base_obs.get("namedSources", []) if s not in now_obs["namedSources"]]
            # Only the case's own source counts: a message that stopped listing every possible source and names
            # the one the case used (the 2.2.1 check after the script compile) lost nothing.
            own = own_sources((defs or {}).get(cid))
            if own:
                lost = [s for s in lost if s in own]
            if lost and r.get("verdict") in ("ok", "question") and now_obs["actual"].startswith("blocked"):
                regressions.append({"case": cid, "kind": "lost-source-name",
                                    "title": "%s: the stop message no longer names %s (it did on %s)" % (cid, ", ".join(lost), label),
                                    "detail": "named now: %s; on %s: %s" % (", ".join(now_obs["namedSources"]) or "none", label,
                                                                           ", ".join(base_obs.get("namedSources", [])))})
            if base_obs.get("actual") and now_obs["actual"] and base_obs["actual"] != now_obs["actual"] and r.get("verdict") == "ok":
                out["changes"].append({"case": cid, "change": "%s -> %s" % (base_obs["actual"], now_obs["actual"])})
        if r.get("verdict") == "inconclusive":
            out["changes"].append({"case": cid, "change": "ok on %s, inconclusive now: %s" % (label, (r.get("reason") or "")[:200])})
    for g in regressions:
        g["id"] = "unity.regression.%s.%s" % (g["case"], g["kind"])
        g["signature"] = "regression|%s|%s" % (g["case"], g["kind"])
        g["status"] = "regression"
    out["regressions"] = regressions
    return out


# ------------------------------------------------------------------------------------------- report


# Rules about what a stop message names: the source of the define is part of their place, so a message that
# fails for csc.rsp and one that fails for a -define: list in Additional Compiler Arguments are two signatures.
SOURCE_RULES = ("check-names-source", "guard-names-source")


def define_source(cdef):
    """Where a case puts KENSEI_DEBUG: debug-mode, player-settings, build-profile, extra-defines, csc-rsp,
    compiler-args (one symbol per -define:) or compiler-args-list (a -define: of several symbols), joined by '+'."""
    def has(value):
        return "KENSEI_DEBUG" in re.split(r"[;,|\s]+", value or "")
    parts = []
    if (cdef.get("debugMode") or "") == "on":
        parts.append("debug-mode")
    if (cdef.get("psDefines") or "*") != "*" and has(cdef.get("psDefines")):
        parts.append("player-settings")
    if has(cdef.get("profileDefines")):
        parts.append("build-profile")
    if has(cdef.get("extraDefines")):
        parts.append("extra-defines")
    if has(cdef.get("rspDefines")):
        parts.append("csc-rsp")
    if has(cdef.get("argsDefines")):
        parts.append("compiler-args")
    for raw in (cdef.get("rawArgs") or "").split("|"):
        m = re.search(r"-define:([^\s]*KENSEI_DEBUG[^\s]*)", raw)
        if m:
            parts.append("compiler-args-list" if re.search(r"[;,]", m.group(1)) else "compiler-args")
    return "+".join(dict.fromkeys(parts)) or "baseline"


def effective_signature(defs, case_result, violation):
    """The cause of a violation as the report groups it: for SOURCE_RULES with the source of the define of its case."""
    sig = violation["signature"]
    if violation.get("rule") in SOURCE_RULES:
        sig = "%s@%s" % (sig, define_source(defs.get(case_result.get("id"), {})))
    return sig


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--raw", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--cases", required=True)
    ap.add_argument("--rules", required=True)
    ap.add_argument("--meta", required=True)
    ap.add_argument("--pkg", default="")
    ap.add_argument("--docs", default="")
    ap.add_argument("--docs-note", default="")
    ap.add_argument("--known", default="")
    ap.add_argument("--baseline-report", default="")
    ap.add_argument("--baseline-label", default="")
    args = ap.parse_args()

    meta = load_json(args.meta, {}) or {}
    clean = Sanitizer(meta)
    rules = {k: v for k, v in (load_json(args.rules, {}) or {}).items() if not k.startswith("_")}
    known = {k: v for k, v in (load_json(args.known, {}) or {}).items() if not k.startswith("_")} if args.known else {}
    pkg_dir = args.pkg or meta.get("src") or ""
    docs_dir = args.docs or None
    prof = contract.profile(pkg_dir)
    defs = {c["id"]: c for c in (load_json(args.cases, {"cases": []}) or {"cases": []})["cases"]}
    order = list(defs.keys())
    setup = load_json(os.path.join(args.raw, "setup.json"), {}) or {}
    groups = {}
    for p in sorted(glob.glob(os.path.join(args.raw, "groups", "*.json"))):
        g = load_json(p)
        if g:
            groups[g.get("group", os.path.basename(p))] = g
    results = {}
    for p in glob.glob(os.path.join(args.raw, "cases", "*.json")):
        r = load_json(p)
        if r and r.get("id"):
            results[r["id"]] = r
    runs = meta.get("runs", [])

    # Cases that run.sh selected but Unity never reported (crash, timeout).
    for cid in meta.get("selected", []):
        if cid not in results:
            results[cid] = {
                "id": cid, "group": defs.get(cid, {}).get("group", "?"), "kind": defs.get(cid, {}).get("kind", "?"),
                "title": defs.get(cid, {}).get("title", ""), "rule": defs.get(cid, {}).get("rule", ""),
                "tier": defs.get(cid, {}).get("tier", ""), "verdict": "inconclusive", "expected": defs.get(cid, {}).get("expect", ""),
                "actual": "no-result", "reason": meta.get("missingReason", {}).get(cid, "Unity wrote no result (the process crashed or timed out)"),
                "violations": [], "checks": [], "questions": [], "evidence": {}, "applied": {}, "log": "",
            }
    ordered = sorted(results.values(), key=lambda r: order.index(r["id"]) if r["id"] in order else 999)

    os.makedirs(args.out, exist_ok=True)
    cases_dir = os.path.join(args.out, "cases")
    if os.path.isdir(os.path.join(args.raw, "cases")) and os.path.abspath(os.path.join(args.raw, "cases")) != os.path.abspath(cases_dir):
        os.makedirs(cases_dir, exist_ok=True)
        for p in glob.glob(os.path.join(args.raw, "cases", "*")):
            shutil.copy2(p, cases_dir)
    repro_dir = os.path.join(args.out, "repro")
    if os.path.isdir(repro_dir):
        shutil.rmtree(repro_dir)

    # ---- violations grouped by signature (rule + cause)
    grouped = {}
    for r in ordered:
        if r.get("verdict") != "finding":
            continue
        for v in r.get("violations", []):
            key = (v["rule"], effective_signature(defs, r, v))
            grouped.setdefault(key, []).append((r, v))
    by_rule = {}
    for (rule_id, sig) in grouped:
        by_rule.setdefault(rule_id, []).append(sig)
    findings = []
    questions = []
    downgraded = set()
    verify_dir = os.path.join(args.raw, "repro-verify")
    plan = []
    for (rule_id, sig), items in grouped.items():
        rule = rules.get(rule_id, {})
        signature = "%s|%s" % (rule_id, sig)
        fid = "unity." + rule_id + ("" if len(by_rule[rule_id]) == 1 else "." + short_hash(sig))
        # The most minimal case for the repro: a quick one if there is one, then one without a Build Profile.
        ranked = sorted(items, key=lambda it: (defs.get(it[0]["id"], {}).get("tier") != "quick",
                                               bool(defs.get(it[0]["id"], {}).get("profile")),
                                               order.index(it[0]["id"]) if it[0]["id"] in order else 999))
        repro_case, repro_v = ranked[0]
        cases = [it[0]["id"] for it in items]
        state, found, note = contract.judge(rule, prof, pkg_dir, docs_dir)
        if state != "applies":
            # Not a promise of this tree: ask, do not claim a defect.
            downgraded.add((rule_id, sig))
            questions.append({
                "id": "unity.q." + rule_id + "." + short_hash(sig), "signature": signature, "rule": rule_id, "kind": state,
                "case": ", ".join(cases), "cases": cases,
                "title": "%s (%s): %s" % (rule.get("title", rule_id), sig, note),
                "text": "violation of rule %s (%s) in %s: %s. Not a finding: %s." % (rule_id, sig, ", ".join(cases), shorten(clean(repro_v["detail"]), 300).rstrip("."), note),
            })
            continue
        tags = []
        for kid, k in known.items():
            for m in k.get("match", []):
                if m.get("rule") != rule_id:
                    continue
                if m.get("casePrefix") and not any(c.startswith(m["casePrefix"]) for c in cases):
                    continue
                if m.get("causeContains") and m["causeContains"] not in sig:
                    continue
                if kid not in tags:
                    tags.append(kid)
        finding = {
            "id": fid,
            "signature": signature,
            "family": tags[0] if tags else None,
            "class": rule.get("class", "unknown"),
            "rule": rule_id,
            "ruleTitle": rule.get("title", rule_id),
            "title": rule.get("title", rule_id),
            "contract": rule.get("contract", ""),
            "contractSince": rule.get("since"),
            "contractQuotes": found,
            "contractNote": note,
            "severity": rule.get("severity", "medium"),
            "cause": sig,
            "known": tags,
            "cases": cases,
            "hits": len(cases),
            "detail": clean(repro_v["detail"]),
            "seed": None,
            "params": {
                "case": repro_case["id"],
                "kind": repro_case.get("kind"),
                "applied": clean(repro_case.get("applied", {})),
                "definition": defs.get(repro_case["id"], {}),
            },
            "repro": "repro/" + fid + ".cs",
            "reproVerified": None,
            "log": repro_case.get("log", ""),
            "evidence": {
                k: clean(repro_case.get("evidence", {}).get(k))
                for k in ("cs1029Line", "guardMessage", "checkMessage", "aaError", "buildResult", "markers", "kenseiDebugCode",
                          "kenseiCompileErrors", "kenseiWarnings", "namedSources", "result")
                if repro_case.get("evidence", {}).get(k)
            },
        }
        if repro_case.get("kind") in VERIFIABLE:
            plan.append({"finding": fid, "type": "KenseiRepro." + repro_class(fid), "caseId": repro_case["id"],
                         "kind": repro_case.get("kind"), "expectActual": repro_case.get("actual", ""),
                         "runtime": repro_case.get("kind") in RUNTIME_REPROS})
            v = load_json(os.path.join(verify_dir, re.sub(r"[^A-Za-z0-9._-]+", "_", fid) + ".json"))
            if v:
                finding["reproVerified"] = {
                    "reproduced": bool(v.get("reproduced")), "actual": v.get("actual"), "expected": v.get("expectActual"),
                    "reason": clean(v.get("reason", "")), "log": "raw/repro-verify/" + re.sub(r"[^A-Za-z0-9._-]+", "_", fid) + ".log",
                    "lines": clean(v.get("reproLines", [])[:6]),
                }
        findings.append(finding)
        os.makedirs(repro_dir, exist_ok=True)
        with open(os.path.join(args.out, finding["repro"]), "w", encoding="utf-8") as f:
            f.write(repro_source(finding, repro_case, defs.get(repro_case["id"], {}), setup, meta, rule, clean))
    findings.sort(key=lambda f: (SEVERITY_ORDER.get(f["severity"], 9), f["id"]))
    with open(os.path.join(args.raw, "repro-plan.json"), "w", encoding="utf-8") as f:
        json.dump({"items": plan}, f, ensure_ascii=False, indent=2)
    # A case whose every violation became a question is a question in the table too.
    for r in ordered:
        if r.get("verdict") == "finding" and r.get("violations") and \
                all((v["rule"], effective_signature(defs, r, v)) in downgraded for v in r["violations"]):
            r["verdict"] = "question"

    for r in ordered:
        for i, q in enumerate(r.get("questions", [])):
            questions.append({"id": "unity.q.%s.%d" % (r["id"], i + 1), "signature": "question|%s|%s" % (r["id"], short_hash(q)),
                              "rule": r.get("rule"), "kind": "case", "case": r["id"], "cases": [r["id"]],
                              "title": "%s: %s" % (r["id"], clean(q)[:200]), "text": clean(q)})

    # ---- baseline
    baseline = None
    regressions = []
    base = load_json(args.baseline_report) if args.baseline_report else None
    if args.baseline_report and base is None:
        baseline = {"label": args.baseline_label, "report": args.baseline_report, "error": "no baseline report (run the module on the baseline first)"}
    elif base is not None:
        baseline = compare_baseline(base, findings, questions, ordered, args.baseline_label or base.get("flavor") or "baseline",
                                    args.baseline_report, defs)
        regressions = baseline.pop("regressions")
        regressed = {g["case"] for g in regressions}
        for r in ordered:
            if r["id"] in regressed and r.get("verdict") in ("ok", "question"):
                r["verdict"] = "regression"
        # A question that came with a regression is reported as the regression only.
        moved = {g["case"] for g in regressions if g["kind"] == "new-question"}
        questions = [q for q in questions if not (q.get("kind") == "case" and q.get("case") in moved)]
        for q in questions:
            if q.get("kind") != "case" and set(q.get("cases", [])) <= moved and q.get("cases"):
                q["regression"] = True

    counts = {}
    for r in ordered:
        counts[r.get("verdict", "?")] = counts.get(r.get("verdict", "?"), 0) + 1

    report = {
        "module": "unity",
        "flavor": meta.get("flavor"),
        "config": meta.get("cfg"),
        "mode": meta.get("mode"),
        "src": meta.get("src"),
        "unity": setup.get("unityVersion") or meta.get("unityVersion"),
        "unityPath": meta.get("unity"),
        "unitySource": meta.get("unitySource"),
        "unityModules": (meta.get("modules") or "").split(),
        "package": {"name": setup.get("packageName"), "version": setup.get("packageVersion") or prof.get("version"), "copy": meta.get("pkg"),
                    "srcDigest": meta.get("srcDigest")},
        "contractProfile": prof,
        "project": meta.get("project"),
        "addressables": setup.get("aaVersion") or None,
        "sbp": setup.get("sbpVersion") or None,
        "target": setup.get("target") or meta.get("target"),
        "backend": setup.get("backend") or meta.get("backend"),
        "baseline": {"editorDebugMode": meta.get("cfg") == "dbg", "toggleWrote": setup.get("toggleWrote", ""), "playerSettings": setup.get("psBaseline", [])},
        "capabilities": setup.get("capabilities", []),
        "contractDocs": {"docs": docs_dir, "note": args.docs_note, "package": pkg_dir,
                         "missingFiles": contract.missing_files(rules, pkg_dir, docs_dir)},
        "known": {k: {"title": v.get("title"), "since": v.get("since"), "fixedIn": v.get("fixedIn"),
                      "found": [f["id"] for f in findings if k in f.get("known", [])]} for k, v in known.items()},
        "reproVerification": {"planned": len(plan),
                              "verified": sum(1 for f in findings if f.get("reproVerified")),
                              "reproduced": sum(1 for f in findings if (f.get("reproVerified") or {}).get("reproduced"))},
        "started": meta.get("started"),
        "finished": meta.get("finished"),
        "durationSec": meta.get("durationSec"),
        "runs": runs,
        "summary": counts,
        "signatures": [{"signature": f["signature"], "id": f["id"], "family": f["family"], "hits": f["hits"], "cases": f["cases"]} for f in findings],
        "findings": findings,
        "contractQuestions": questions,
        "regressions": regressions,
        "baselineComparison": baseline,
        "skipped": [{"case": r["id"], "reason": clean(r.get("naReason") or r.get("reason") or "")} for r in ordered if r.get("verdict") == "skipped"],
        "cases": [
            {
                "id": r["id"], "group": r.get("group"), "kind": r.get("kind"), "tier": r.get("tier"), "title": r.get("title"),
                "rule": r.get("rule"), "expected": r.get("expected"), "actual": r.get("actual"), "verdict": r.get("verdict"),
                "reason": clean(r.get("reason") or r.get("naReason") or ""), "durationSec": round(r.get("durationSec", 0) or 0, 1),
                "log": r.get("log") or "", "violations": clean(r.get("violations", [])), "checks": clean(r.get("checks", [])),
                "observed": observed(r) if r.get("kind") in BUILD_KINDS else None,
            }
            for r in ordered
        ],
        "notes": clean(setup.get("notes", []) + [n for g in groups.values() for n in g.get("notes", [])] + meta.get("notes", [])),
    }
    with open(os.path.join(args.out, "report.json"), "w", encoding="utf-8") as f:
        json.dump(report, f, ensure_ascii=False, indent=2)

    write_markdown(args, report, meta, setup, prof, ordered, findings, questions, regressions, baseline, known, docs_dir, pkg_dir, clean)

    print("report: %d case(s): %s; findings %d, questions %d%s" % (
        len(ordered), ", ".join("%s %d" % (k, v) for k, v in sorted(counts.items())), len(findings), len(questions),
        ", regressions %d" % len(regressions) if baseline else ""))
    for f in findings:
        print("  FINDING %s [%s/%s] %s: %s%s" % (f["id"], f["class"], f["severity"], ",".join(f["cases"]), f["cause"],
                                               " (%s)" % f["baseline"]["status"] if f.get("baseline") else ""))
    for g in regressions:
        print("  REGRESSION %s: %s" % (g["id"], g["title"]))
    return 1 if findings else 0


def write_markdown(args, report, meta, setup, prof, ordered, findings, questions, regressions, baseline, known, docs_dir, pkg_dir, clean):
    counts = report["summary"]
    runs = report["runs"]
    md = []
    md.append("# Stand module unity: %s-%s" % (meta.get("flavor"), meta.get("cfg")))
    md.append("")
    md.append("- Package: com.kensei.ecs %s, sources `%s` (digest %s); version profile: %s" % (
        setup.get("packageVersion") or prof.get("version") or "?", clean(meta.get("src")), meta.get("srcDigest", "?"), prof.get("label")))
    md.append("- Scratch project: `%s`" % clean(meta.get("project", "?")))
    md.append("- Unity %s (%s), target %s, scripting backend %s" % (
        setup.get("unityVersion") or meta.get("unityVersion", "?"), meta.get("unitySource") or "?", setup.get("target") or meta.get("target", "?"),
        setup.get("backend") or meta.get("backend", "?")))
    md.append("- Addressables: %s, SBP: %s; build modules of the editor: %s" % (setup.get("aaVersion") or "not installed", setup.get("sbpVersion") or "none",
                                                                            meta.get("modules") or "?"))
    md.append("- Editor baseline: %s" % ("dbg - Debug Mode on through the menu of the package (%s)" % setup.get("toggleWrote", "?") if meta.get("cfg") == "dbg"
                                         else "rel - Debug Mode off, KENSEI_DEBUG in no target"))
    caps = [c for c in setup.get("capabilities", []) if not c.startswith("impl:")]
    impl = [c[5:] for c in setup.get("capabilities", []) if c.startswith("impl:")]
    md.append("- Capabilities: %s; in the code of the package: %s" % (", ".join(caps) or "?", ", ".join(impl) or "no guard and no release check"))
    md.append("- Contract documents: docs `%s`%s; package `%s`%s" % (
        clean(docs_dir) or "none", " (%s)" % args.docs_note if args.docs_note else "", clean(pkg_dir),
        "; missing files: " + ", ".join(report["contractDocs"]["missingFiles"]) if report["contractDocs"]["missingFiles"] else ""))
    rv = report["reproVerification"]
    if rv["planned"]:
        md.append("- Repros: %d of %d build repros run by the stand, %d reproduce their case" % (rv["verified"], rv["planned"], rv["reproduced"]))
    md.append("- Mode: %s, %s; started %s, %s s" % (meta.get("mode"), "selected cases " + meta.get("only") if meta.get("only") else "every case of the mode",
                                                    meta.get("started"), meta.get("durationSec")))
    if runs:
        md.append("- Unity processes: " + ", ".join("%s %s s%s" % (x["name"], x["seconds"], " (TIMEOUT)" if x.get("timedOut") else (" (exit %s)" % x["exit"] if x.get("exit") not in (0, None) else ""))
                                                   for x in runs))
    md.append("- Result: %s" % ", ".join("%s %d" % (VERDICT_TEXT.get(k, k), v) for k, v in sorted(counts.items())))
    if baseline:
        if baseline.get("error"):
            md.append("- Baseline %s: %s" % (baseline.get("label"), baseline["error"]))
        else:
            md.append("- Baseline %s: %d comparable cases; signatures new %d, grown %d, gone %d; regressions %d" % (
                baseline["label"], baseline["comparableCases"], len(baseline["newSignatures"]), len(baseline["grown"]),
                len(baseline["goneSignatures"]), len(regressions)))
    md.append("")

    md.append("## Findings")
    md.append("")
    if not findings:
        md.append("None.")
        md.append("")
    for f in findings:
        md.append("### %s - %s" % (f["id"], f["ruleTitle"]))
        md.append("")
        if f.get("known"):
            md.append("- Known: %s - %s" % (", ".join(f["known"]), "; ".join(known[k].get("title", "") for k in f["known"])))
        else:
            md.append("- Known: no (no known defect has this signature)")
        md.append("- Signature: `%s`, %d case(s): %s%s" % (f["signature"], f["hits"], ", ".join(f["cases"]),
                                                         "; against the baseline: %s" % f["baseline"]["status"] if f.get("baseline") else ""))
        md.append("- Class: `%s`, severity: **%s** (estimate)" % (f["class"], f["severity"]))
        md.append("- Observed: %s" % f["detail"])
        for k in ("cs1029Line", "guardMessage", "checkMessage", "aaError"):
            if f["evidence"].get(k):
                md.append("- %s: `%s`" % (k, str(f["evidence"][k])[:400]))
        md.append("- Contract (promise since %s): %s" % (f.get("contractSince") or "?", f["contract"]))
        if f.get("contractQuotes"):
            md.append("- Contract quotes in this version: %s" % "; ".join("%s: \"%s\"" % (a["file"], a["quote"]) for a in f["contractQuotes"]))
        if f.get("contractNote"):
            md.append("- Version profile: %s" % f["contractNote"])
        md.append("- Repro: `%s` (case %s); case log: `%s`" % (f["repro"], f["params"]["case"], f["log"]))
        v = f.get("reproVerified")
        if v:
            md.append("- Repro run by the stand: %s (repro: %s, case: %s)%s; log `%s`" % (
                "reproduces" if v["reproduced"] else "DOES NOT reproduce", v["actual"], v["expected"],
                "; " + v["reason"] if v.get("reason") else "", v["log"]))
        elif f["params"].get("kind") in VERIFIABLE:
            md.append("- Repro not run by the stand (--no-verify-repro, or the verify process did not get to it)")
        else:
            md.append("- The repro is not a build (%s): the stand does not run it" % f["params"].get("kind"))
        md.append("")

    if regressions or (baseline and not baseline.get("error")):
        md.append("## Against the baseline")
        md.append("")
        if baseline.get("error"):
            md.append(baseline["error"])
        else:
            md.append("- New signatures: %s" % (", ".join("`%s`" % s for s in baseline["newSignatures"]) or "none"))
            md.append("- Grown signatures: %s" % (", ".join("`%s` %d -> %d" % (g["signature"], g["baselineHits"], g["hits"]) for g in baseline["grown"]) or "none"))
            md.append("- Gone signatures (their cases ran in both): %s" % (", ".join("`%s` (%s)" % (g["signature"], ", ".join(g["cases"])) for g in baseline["goneSignatures"]) or "none"))
            if baseline["changes"]:
                md.append("- Other changes (no contract involved): %s" % "; ".join("%s %s" % (c["case"], c["change"]) for c in baseline["changes"]))
            md.append("")
            md.append("Regressions (status regression: changed against the baseline without breaking a contract):")
            md.append("")
            if not regressions:
                md.append("None.")
            for g in regressions:
                md.append("- %s: %s. %s" % (g["id"], g["title"], g["detail"]))
        md.append("")

    if known:
        md.append("## Known defects")
        md.append("")
        for kid, k in known.items():
            hit = report["known"][kid]["found"]
            ran = [r for r in ordered if any(m.get("rule") == r.get("rule") and r["id"].startswith(m.get("casePrefix", ""))
                                             for m in k.get("match", [])) and r.get("verdict") not in ("na", "skipped")]
            if hit:
                state = "PRESENT: " + ", ".join(hit)
            elif not ran:
                state = "not checked in this run (no case of its rules ran)"
            else:
                state = "not reproduced: " + ", ".join("%s %s" % (r["id"], VERDICT_TEXT.get(r.get("verdict"), r.get("verdict"))) for r in ran)
            md.append("- %s (since %s, fixed in %s): %s - %s" % (kid, k.get("since", "?"), k.get("fixedIn", "-"), state, k.get("title", "")))
        md.append("")

    md.append("## Cases")
    md.append("")
    md.append("| id | what it checks | expected | actual | verdict | s |")
    md.append("|---|---|---|---|---|---|")
    for r in ordered:
        md.append("| %s | %s | %s | %s | %s | %s |" % (
            r["id"], r.get("title", "").replace("|", "/"), r.get("expected", ""), r.get("actual", ""),
            VERDICT_TEXT.get(r.get("verdict"), r.get("verdict")), round(r.get("durationSec", 0) or 0)))
    md.append("")

    md.append("## Questions to the contract")
    md.append("")
    if not questions:
        md.append("None.")
    for q in questions:
        md.append("- %s: %s" % (q["case"], q["text"]))
    md.append("")

    other = [r for r in ordered if r.get("verdict") in ("na", "skipped", "inconclusive")]
    md.append("## Not checked")
    md.append("")
    if not other:
        md.append("None.")
    for r in other:
        md.append("- %s (%s): %s" % (r["id"], VERDICT_TEXT.get(r["verdict"]), clean(r.get("reason") or r.get("naReason") or "")))
    md.append("")

    md.append("## Checks per case")
    md.append("")
    for r in ordered:
        if not r.get("checks"):
            continue
        md.append("**%s** (`%s`)" % (r["id"], r.get("log", "")))
        md.append("")
        for c in r["checks"]:
            md.append("- " + clean(c))
        md.append("")
    notes = report["notes"]
    if notes:
        md.append("## Notes of the stand")
        md.append("")
        for n in notes:
            md.append("- " + str(n)[:600])
        md.append("")

    with open(os.path.join(args.out, "report.md"), "w", encoding="utf-8") as f:
        f.write("\n".join(md))


if __name__ == "__main__":
    sys.exit(main())
