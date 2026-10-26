// KenseiECS test stand, module unity: helpers (command line, log capture, defines, DLL inspection).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace KenseiStand.Editor {
    /// <summary>The case could not be judged: the stand or the environment failed, not KenseiECS.</summary>
    internal sealed class StandInconclusive : Exception {
        public StandInconclusive(string message) : base(message) { }
    }

    /// <summary>The case cannot run in this environment (a module, package or setting it needs is not there).</summary>
    internal sealed class StandSkipped : Exception {
        public StandSkipped(string message) : base(message) { }
    }

    internal static class StandArgs {
        public static string Get(string name, string fallback = "") {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++) {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) {
                    return args[i + 1];
                }
            }
            return fallback;
        }
    }

    internal struct LogEntry {
        public LogType type;
        public string text;
        public string stack;
    }

    /// <summary>Collects every log message of a case (all threads), so the case log and the error scan see the same lines.</summary>
    internal static class StandLog {
        public const string Prefix = "[KENSEI-STAND] ";
        private static readonly object Gate = new object();
        private static List<LogEntry> s_Entries;
        private static bool s_Hooked;

        public static void Hook() {
            if (s_Hooked) {
                return;
            }
            Application.logMessageReceivedThreaded += OnLog;
            s_Hooked = true;
        }

        public static void Begin() {
            lock (Gate) {
                s_Entries = new List<LogEntry>();
            }
        }

        public static List<LogEntry> End() {
            lock (Gate) {
                var entries = s_Entries ?? new List<LogEntry>();
                s_Entries = null;
                return entries;
            }
        }

        /// <summary>Entries logged since <paramref name="from"/> in the current capture.</summary>
        public static List<LogEntry> Since(int from) {
            lock (Gate) {
                if (s_Entries == null || from >= s_Entries.Count) {
                    return new List<LogEntry>();
                }
                return s_Entries.GetRange(from, s_Entries.Count - from);
            }
        }

        public static int Count {
            get {
                lock (Gate) {
                    return s_Entries == null ? 0 : s_Entries.Count;
                }
            }
        }

        private static void OnLog(string text, string stack, LogType type) {
            lock (Gate) {
                s_Entries?.Add(new LogEntry { type = type, text = text ?? "", stack = stack ?? "" });
            }
        }

        public static void Info(string text) {
            Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null, "{0}{1}", Prefix, text);
        }

        public static bool IsError(LogEntry e) =>
            e.type == LogType.Error || e.type == LogType.Exception || e.type == LogType.Assert ||
            e.text.Contains("error CS");

        public static string Format(List<LogEntry> entries) {
            var text = new StringBuilder(entries.Count * 80);
            foreach (var e in entries) {
                text.Append('[').Append(e.type).Append("] ").Append(e.text.Replace("\r", "")).Append('\n');
                if ((e.type == LogType.Exception || e.type == LogType.Error) && e.stack.Length > 0 && !e.text.Contains("error CS")) {
                    foreach (var line in e.stack.Replace("\r", "").Split('\n').Take(12)) {
                        if (line.Length > 0) {
                            text.Append("    ").Append(line).Append('\n');
                        }
                    }
                }
            }
            return text.ToString();
        }
    }

    internal static class StandText {
        public static string Trim(string s, int max = 600) {
            if (s == null) {
                return "";
            }
            s = s.Replace("\r", "").Replace("\n", " | ");
            return s.Length > max ? s.Substring(0, max) + "..." : s;
        }

        public static string[] Split(string s) {
            if (string.IsNullOrEmpty(s)) {
                return new string[0];
            }
            return s.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
        }

        public static string Join(IEnumerable<string> items) => "[" + string.Join(";", items) + "]";

        public static string Now() => DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");

        /// <summary>Set bits of a flags enum by name. Mono's Enum.ToString also lists every zero-valued (obsolete) member.</summary>
        public static string Flags<T>(T value) where T : Enum {
            ulong bits = Convert.ToUInt64(value);
            if (bits == 0) {
                return "None";
            }
            var names = new List<string>();
            var seen = new HashSet<ulong>();
            foreach (T member in Enum.GetValues(typeof(T))) {
                ulong v = Convert.ToUInt64(member);
                if (v != 0 && (bits & v) == v && seen.Add(v) && (v & (v - 1)) == 0) {
                    // Enum.GetName, not ToString: Mono formats even a single flag with the zero-valued names.
                    names.Add(Enum.GetName(typeof(T), member));
                }
            }
            return string.Join(", ", names);
        }

        public static void WriteAllText(string path, string text) {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }
    }

    /// <summary>Player Settings scripting defines per target (NamedBuildTarget, Unity 2021.2+).</summary>
    internal static class StandDefines {
        public const string DebugDefine = "KENSEI_DEBUG";
        public const string AllowDefine = "KENSEI_ALLOW_DEBUG_RELEASE";

        public static readonly NamedBuildTarget[] Known = {
            NamedBuildTarget.Standalone, NamedBuildTarget.Server, NamedBuildTarget.Android, NamedBuildTarget.iOS, NamedBuildTarget.WebGL
        };

        public static BuildTarget ActiveTarget => EditorUserBuildSettings.activeBuildTarget;

        public static BuildTargetGroup ActiveGroup => BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);

        public static NamedBuildTarget Active => NamedBuildTarget.FromBuildTargetGroup(ActiveGroup);

        public static string[] Get(NamedBuildTarget target) {
            PlayerSettings.GetScriptingDefineSymbols(target, out string[] defines);
            return defines ?? new string[0];
        }

        public static void Set(NamedBuildTarget target, IEnumerable<string> defines) {
            var list = defines.ToArray();
            if (Get(target).SequenceEqual(list)) {
                return;
            }
            PlayerSettings.SetScriptingDefineSymbols(target, list);
        }

        public static NamedBuildTarget ByName(string name) {
            foreach (var t in Known) {
                if (string.Equals(t.TargetName, name, StringComparison.OrdinalIgnoreCase)) {
                    return t;
                }
            }
            throw new StandInconclusive("unknown NamedBuildTarget '" + name + "'");
        }

        /// <summary>TargetName -> defines of every known target.</summary>
        public static Dictionary<string, string[]> All() {
            var all = new Dictionary<string, string[]>();
            foreach (var t in Known) {
                try {
                    all[t.TargetName] = Get(t);
                } catch (Exception) {
                    // A target this Unity version does not support: nothing to compare.
                }
            }
            return all;
        }

        public static List<string> WithDefine(Dictionary<string, string[]> all) =>
            all.Where(kv => kv.Value.Contains(DebugDefine)).Select(kv => kv.Key + "=" + string.Join(";", kv.Value)).ToList();

        public static string Describe(Dictionary<string, string[]> all) =>
            string.Join(", ", all.Select(kv => kv.Key + StandText.Join(kv.Value)));
    }

    internal static class StandTypes {
        public static Type Find(string fullName) {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) {
                Type t;
                try {
                    t = asm.GetType(fullName, false);
                } catch (Exception) {
                    continue;
                }
                if (t != null) {
                    return t;
                }
            }
            return null;
        }

        public static bool AssemblyLoaded(string name) =>
            AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == name);

        public static System.Reflection.Assembly Assembly(string name) =>
            AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name);
    }

    /// <summary>Reads a compiled DLL: which define markers it has and whether KenseiECS has its debug-only code.</summary>
    internal static class StandDll {
        public const string MarkerAssembly = "KenseiStand.Defines.dll";
        public const string KenseiAssembly = "KenseiECS.dll";

        private static readonly string[] MarkerDefines = {
            "DEVELOPMENT_BUILD", "KENSEI_DEBUG", "KENSEI_ALLOW_DEBUG_RELEASE", "UNITY_EDITOR", "ENABLE_IL2CPP", "ADDRESSABLES_ADD_DEFINES", "Always"
        };

        public static List<string> Markers(string dll) {
            var found = new List<string>();
            if (string.IsNullOrEmpty(dll) || !File.Exists(dll)) {
                return found;
            }
            var bytes = File.ReadAllBytes(dll);
            foreach (var define in MarkerDefines) {
                if (Contains(bytes, "StandMark_" + define + "\0")) {
                    found.Add(define);
                }
            }
            return found;
        }

        /// <summary>"yes" when the KenseiECS.dll has the EcsProfiler type, which exists only under KENSEI_DEBUG.</summary>
        public static string KenseiDebugCode(string dll) {
            if (string.IsNullOrEmpty(dll) || !File.Exists(dll)) {
                return "n/a";
            }
            return Contains(File.ReadAllBytes(dll), "\0EcsProfiler\0") ? "yes" : "no";
        }

        public static string FindIn(string dir, string fileName) {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) {
                return "";
            }
            return Directory.GetFiles(dir, fileName, SearchOption.AllDirectories).FirstOrDefault() ?? "";
        }

        private static bool Contains(byte[] hay, string needle) {
            var n = Encoding.ASCII.GetBytes(needle);
            int last = hay.Length - n.Length;
            for (int i = 0; i <= last; i++) {
                if (hay[i] != n[0]) {
                    continue;
                }
                int j = 1;
                while (j < n.Length && hay[i + j] == n[j]) {
                    j++;
                }
                if (j == n.Length) {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>Compiler errors written to the Unity log before the stand started (the editor compile at startup).</summary>
    internal static class StandEditorLog {
        private static readonly Regex CompileError = new Regex(@"(?<file>[^\s:()]+\.cs)\((?<line>\d+),(?<col>\d+)\): error (?<code>CS\d+): (?<msg>.*)$");

        public static List<string> StartupCompileErrors() {
            var errors = new List<string>();
            string log = StandArgs.Get("-logFile");
            if (string.IsNullOrEmpty(log) || !File.Exists(log)) {
                return errors;
            }
            string text;
            using (var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream)) {
                text = reader.ReadToEnd();
            }
            int stop = text.IndexOf(StandLog.Prefix, StringComparison.Ordinal);
            if (stop >= 0) {
                text = text.Substring(0, stop);
            }
            foreach (var raw in text.Split('\n')) {
                var m = CompileError.Match(raw.TrimEnd('\r'));
                if (m.Success) {
                    string line = m.Groups["file"].Value + "(" + m.Groups["line"].Value + "," + m.Groups["col"].Value + "): " +
                        m.Groups["code"].Value + ": " + StandText.Trim(m.Groups["msg"].Value, 300);
                    if (!errors.Contains(line)) {
                        errors.Add(line);
                    }
                }
            }
            return errors;
        }
    }
}
