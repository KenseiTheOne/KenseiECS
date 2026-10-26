// KenseiECS test stand, module unity: data written to and read from JSON (JsonUtility).
// Case definitions come from Stand/unity/cases.json; results go to <report>/raw/cases/<id>.json and are
// turned into report.md / report.json by Stand/unity/lib/report.py.
using System;
using System.Collections.Generic;

namespace KenseiStand.Editor {
    [Serializable]
    public sealed class CaseFile {
        public CaseDef[] cases = new CaseDef[0];
    }

    /// <summary>One case of cases.json. Missing fields keep these defaults.</summary>
    [Serializable]
    public sealed class CaseDef {
        public string id = "";
        public string tier = "quick";          // quick | full
        public string group = "";              // editor | player | sbp | aa | run: one Unity process per group
        public string kind = "";               // see StandCases.Run
        public string title = "";
        public string rule = "";               // rules.json key
        public string needs = "";              // capabilities, comma separated (guard, releaseCheck, toggle22, ...)
        public string expect = "";             // pass | compiled | blocked (guard, check or #error) | blocked-guard | blocked-error | ran
        public string expectMessage = "";      // '|' separated parts the stop message must name ({target}, {profileName}, {profilePath});
                                               // missing from the guard: a finding of guard-names-source; from the check after the
                                               // compile or the #error: a violation of check-names-source (2.2.1 promise)
        public string askMessage = "";         // same, but a missing part is only a question to the contract
        public string expectMarkers = "";      // defines the compile must have seen (non-vacuity of the case)
        public string expectNoMarkers = "";    // defines the compile must not have seen
        public string psDefines = "*";         // '*' keeps the baseline; otherwise the exact defines of the active target
        public string otherPsDefines = "";     // "iOS=A;B,Standalone=C"
        public string profile = "";            // "" | plain | dev: a Build Profile of the active target, made active
        public bool profileInactive;           // create the profile but leave no profile active (BuildPlayerWithProfileOptions activates it)
        public string profileDefines = "";
        public string extraDefines = "";       // BuildPlayerOptions / ScriptCompilationSettings.extraScriptingDefines
        public string rspDefines = "";         // written to Assets/csc.rsp as -define:
        public string argsDefines = "";        // Player Settings -> Additional Compiler Arguments of the active target, one -define: each
        public string rawArgs = "";            // Additional Compiler Arguments as given, '|' separated (e.g. "-define:A;KENSEI_DEBUG")
        public string debugMode = "";          // "" | on | off: through the KenseiECS -> Debug Mode menu item
        public bool development;               // BuildOptions.Development / ScriptCompilationOptions.DevelopmentBuild
        public bool devTicked;                 // EditorUserBuildSettings.development (Build Settings checkbox)
        public string aaWithPlayer = "";       // "" keeps baseline (do not build) | yes | no | default (PreferencesValue, as new settings have)
        public bool checkWarnings;             // a KenseiECS warning in the build is judged (UAC0009: finding; other: question);
                                               // the case adds a define of its own, so its compiles miss the build cache
        public string buildVia = "options";    // options: BuildPlayerOptions | profile: BuildPlayerWithProfileOptions
        public string backend = "";            // player-run: mono | il2cpp
        public string note = "";
    }

    [Serializable]
    public sealed class Violation {
        public string rule = "";
        public string signature = "";          // cause (mechanism and place), used to group cases into one finding
        public string detail = "";
    }

    /// <summary>The state a case really ran with (after the Debug Mode toggle and defaults).</summary>
    [Serializable]
    public sealed class AppliedState {
        public string target = "";
        public List<string> psActive = new List<string>();
        public List<string> psWithDefine = new List<string>();
        public string profileName = "";
        public string profilePath = "";
        public List<string> profileDefines = new List<string>();
        public bool profileDevelopment;
        public bool profileOwnPlayerSettings;
        public List<string> extraDefines = new List<string>();
        public List<string> rspDefines = new List<string>();
        public List<string> compilerArgs = new List<string>();
        public bool profileActive;
        public bool development;
        public bool devTicked;
        public string aaWithPlayer = "";
        public string debugMode = "";
        public string buildVia = "";
        public string options = "";
    }

    [Serializable]
    public sealed class Evidence {
        public bool success;
        public bool scriptsCompiled;           // player build reached IPostBuildPlayerScriptDLLs
        public bool postCompileReached;        // the first IPostBuildPlayerScriptDLLs callback ran (before KenseiECS checks)
        public bool preprocessCalled;
        public string buildResult = "";
        public int totalErrors = -1;
        public int assemblies = -1;
        public List<string> markers = new List<string>();
        public string kenseiDebugCode = "n/a"; // yes | no | n/a: KenseiECS.dll has the KENSEI_DEBUG-only EcsProfiler type
        public string kenseiDll = "";
        public bool cs1029;
        public string cs1029Line = "";
        public string guardMessage = "";       // BuildFailedException of the preprocess guard (KenseiDebugBuildGuard)
        public string checkMessage = "";       // BuildFailedException of a check after the script compile (2.2.1+)
        public List<string> namedSources = new List<string>(); // define sources the stop message names
        public List<string> kenseiWarnings = new List<string>(); // warnings of KenseiECS (its log lines, compiler warnings in its files)
        public string aaError = "";
        public string sbpReturnCode = "";
        public List<string> kenseiCompileErrors = new List<string>();
        public List<string> standCompileErrors = new List<string>();
        public List<string> errors = new List<string>();
        public List<string> logLines = new List<string>();
        public string exception = "";
        public string result = "";
        public int playerExit = -1;
        public string playerLog = "";
    }

    [Serializable]
    public sealed class CaseResult {
        public string id = "";
        public string group = "";
        public string kind = "";
        public string title = "";
        public string rule = "";
        public string tier = "";
        public string verdict = "";            // ok | finding | question | na (not in this version) | skipped (environment) | inconclusive
        public string expected = "";
        public string actual = "";
        public string reason = "";
        public string naReason = "";
        public float durationSec;
        public string started = "";
        public string log = "";
        public AppliedState applied = new AppliedState();
        public Evidence evidence = new Evidence();
        public List<string> checks = new List<string>();
        public List<Violation> violations = new List<Violation>();
        public List<string> questions = new List<string>();
    }

    /// <summary>What KenseiStand.StandSpawnCheck (Assembly-CSharp) reports, as JSON.</summary>
    [Serializable]
    public sealed class SpawnReport {
        public List<string> plain = new List<string>();      // components of a Spawn without the listener
        public List<string> outer = new List<string>();      // components of the outer entity after the nested Spawn
        public List<string> inner = new List<string>();      // components of the entity the listener spawned
        public List<string> expected = new List<string>();   // the providers of the outer view
        public bool listenerFired;
        public bool innerAlive;
        public string error = "";
    }

    /// <summary>Repros to run (written by report.py to raw/repro-plan.json), one per finding with a build repro.</summary>
    [Serializable]
    public sealed class ReproPlan {
        public ReproItem[] items = new ReproItem[0];
    }

    [Serializable]
    public sealed class ReproItem {
        public string finding = "";            // finding id
        public string type = "";               // full type name, KenseiRepro.Repro_...
        public string caseId = "";             // the case the repro was written from
        public string kind = "";               // its kind (player, sbp-compile, editor-spawn, ...)
        public string expectActual = "";       // what the case observed (blocked-error, compiled, fail, ...)
        public bool runtime;                   // a runtime script (Assets/KenseiStandRepro/Runtime/), not an editor one
    }

    /// <summary>What a repro did when the stand ran it: classified like a case, compared with the case.</summary>
    [Serializable]
    public sealed class ReproVerdict {
        public string finding = "";
        public string type = "";
        public string caseId = "";
        public string expectActual = "";
        public string actual = "";
        public bool reproduced;
        public string reason = "";
        public List<string> reproLines = new List<string>();
        public Evidence evidence = new Evidence();
        public float durationSec;
    }

    [Serializable]
    public sealed class RunInfo {
        public string entry = "";
        public string group = "";
        public string started = "";
        public float durationSec;
        public string unityVersion = "";
        public string packageName = "";
        public string packageVersion = "";
        public string packagePath = "";
        public string aaVersion = "";
        public string sbpVersion = "";
        public string target = "";
        public string backend = "";
        public string baseline = "";
        public bool editorDebug;
        public bool editorCompileFailed;
        public List<string> editorCompileErrors = new List<string>();
        public List<string> capabilities = new List<string>();
        public List<string> casesRun = new List<string>();
        public List<string> notes = new List<string>();
        public List<string> psBaseline = new List<string>();
        public string toggleWrote = "";
        public bool ok = true;
    }
}
