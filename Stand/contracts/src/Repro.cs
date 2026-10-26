using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Stand.Contracts {
    /// <summary>
    /// Writes a finding as a standalone C# class: the scenario setup it needs, the call exactly as the
    /// stand made it, and the probes that showed the problem, returning one signature line. The repro
    /// check (Stand/contracts/repro) compiles every such file against the same source tree and define
    /// and compares the line with the one the stand recorded.
    /// </summary>
    public static class ReproWriter {
        public static string ClassName(string id) {
            var s = Regex.Replace(id, "[^A-Za-z0-9]+", "_").Trim('_');
            if (s.Length > 90) {
                s = s.Substring(0, 80) + "_" + ((uint)id.GetHashCode()).ToString("x8");
            }
            return "R_" + s;
        }

        public static string FileName(string id) {
            var s = Regex.Replace(id, "[^A-Za-z0-9.@-]+", "-").Trim('-');
            if (s.Length > 100) {
                s = s.Substring(0, 90) + "-" + ((uint)id.GetHashCode()).ToString("x8");
            }
            return s + ".cs";
        }

        public static string Write(Finding f, ReproMaterial m, string flavor, string cfg, string mode, int seed) {
            var sb = new StringBuilder();
            sb.AppendLine("// " + f.Id);
            sb.AppendLine("// " + f.Title);
            sb.AppendLine("// Stand/contracts, " + flavor + "-" + cfg + ", mode " + mode + ", seed " + seed);
            sb.AppendLine("// Case: " + f.CaseKeys.FirstOrDefault());
            sb.AppendLine("// Class: " + f.Class + ", rule " + f.Rule + ", severity " + f.Severity);
            sb.AppendLine("// The stand saw:      " + m.Expected);
            sb.AppendLine("// On a fresh state:   " + m.RefExpected);
            if (f.DocRef != null) {
                sb.AppendLine("// Contract: " + f.DocRef.Replace("\n", " "));
            }
            sb.AppendLine("#pragma warning disable CS0618, CS0168, CS0219, CS8321");
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using System.IO;");
            sb.AppendLine("using KenseiECS;");
            sb.AppendLine("using Stand.Contracts.Probe;");
            sb.AppendLine();
            sb.AppendLine("namespace Stand.Contracts.Repros {");
            sb.AppendLine("    public static class " + ClassName(f.Id) + " {");
            sb.AppendLine("        public const string Id = " + Lit(f.Id) + ";");
            sb.AppendLine("        public const string Expected = " + Lit(m.Expected) + ";");
            sb.AppendLine();
            sb.AppendLine("        public static string Run() {");
            foreach (var line in m.Setup) {
                sb.AppendLine("            " + line);
            }
            foreach (var line in m.Preludes) {
                sb.AppendLine("            " + line);
            }
            sb.AppendLine("            var log = new List<string>();");
            sb.AppendLine("            log.Add(\"call=\" + Fmt.Try(() => { " + m.CallBody + " }));");
            for (int i = 0; i < m.ProbeNames.Count; i++) {
                sb.AppendLine("            log.Add(" + Lit(m.ProbeNames[i] + "=") + " + Fmt.Try(" + ProbeDef.Lambda(m.ProbeCodes[i]) + "));");
            }
            sb.AppendLine("            return string.Join(\" | \", log);");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        public static string Lit(string s) {
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "") + "\"";
        }
    }
}
