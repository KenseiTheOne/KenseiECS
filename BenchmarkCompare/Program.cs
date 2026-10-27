using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

// Runs the benchmarks of Benchmark/ against several versions of KenseiECS and writes one table per
// benchmark class with the mean time (and allocations) of every version side by side.
//
//   dotnet run -c Release --project BenchmarkCompare -- [--versions v2.2.1,v2.2.2,wt] [--filter "*KenseiECS*"]
//                                                       [--job default|short|dry] [--out <dir>] [--keep]
//
// A version is a git ref (a tag such as v2.2.1) or wt, the working tree. Every ref is checked out into
// a temporary git worktree and gets today's Benchmark/ sources, so all versions run the same benchmark
// code; only the KenseiECS sources differ. Results: <out>/<version>/ (BenchmarkDotNet artifacts) and
// <out>/comparison.md. Run it on an idle machine: the versions run one after another, so a background
// load that changes between them shows up as a difference.
internal static class Program {
    private static int Main(string[] args) {
        // Caught here, so that the finally below removes the worktrees (an unhandled exception ends the
        // process without running it).
        try {
            return Execute(args);
        } catch (Exception e) {
            Console.Error.WriteLine("BenchmarkCompare: " + e.Message);
            return 1;
        }
    }

    private static int Execute(string[] args) {
        var versions = new List<string> { "v2.2.1", "v2.2.2", "wt" };
        string filter = "*KenseiECS*";
        string job = "default";
        string outDir = null;
        bool keep = false;
        for (int i = 0; i < args.Length; i++) {
            switch (args[i]) {
                case "--versions": versions = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(); break;
                case "--filter": filter = args[++i]; break;
                case "--job": job = args[++i]; break;
                case "--out": outDir = args[++i]; break;
                case "--keep": keep = true; break;
                default:
                    Console.Error.WriteLine("unknown argument " + args[i]);
                    return 2;
            }
        }

        string root = Run("git", "rev-parse --show-toplevel", Environment.CurrentDirectory).Trim();
        outDir = Path.GetFullPath(outDir ?? Path.Combine(root, "BenchmarkDotNet.Artifacts", "compare-" + DateTime.Now.ToString("yyyyMMdd-HHmm")));
        Directory.CreateDirectory(outDir);
        string worktrees = Path.Combine(Path.GetTempPath(), "kensei-bench-" + Guid.NewGuid().ToString("N").Substring(0, 8));

        var labels = new List<string>();
        try {
            foreach (string version in versions) {
                string tree = root;
                string label = version;
                if (version != "wt") {
                    tree = Path.Combine(worktrees, version.Replace('/', '_'));
                    Run("git", $"worktree add --detach \"{tree}\" {version}", root);
                    // Today's benchmark sources against that version's package.
                    CopyBenchmarkSources(Path.Combine(root, "Benchmark"), Path.Combine(tree, "Benchmark"));
                    label = version + " (" + Run("git", "rev-parse --short HEAD", tree).Trim() + ")";
                } else {
                    label = "wt (" + Run("git", "rev-parse --short HEAD", root).Trim() + "+)";
                }
                string artifacts = Path.Combine(outDir, Safe(version));
                Console.WriteLine($"== {label}: benchmarks {filter}, job {job} -> {artifacts}");
                string jobArg = job == "default" ? "" : $" --job {job}";
                RunStreaming("dotnet", $"run -c Release --project \"{Path.Combine(tree, "Benchmark")}\" -- --filter \"{filter}\"{jobArg} --exporters json --artifacts \"{artifacts}\"", tree);
                labels.Add(label);
            }
        } finally {
            if (!keep && Directory.Exists(worktrees)) {
                foreach (string dir in Directory.GetDirectories(worktrees)) {
                    Run("git", $"worktree remove --force \"{dir}\"", root, allowFailure: true);
                }
                Directory.Delete(worktrees, true);
            }
        }

        string md = Compare(outDir, versions, labels, job, filter);
        File.WriteAllText(Path.Combine(outDir, "comparison.md"), md);
        Console.WriteLine(md);
        Console.WriteLine("written " + Path.Combine(outDir, "comparison.md"));
        return 0;
    }

    private static void CopyBenchmarkSources(string from, string to) {
        foreach (string file in Directory.GetFiles(to, "*.cs")) {
            File.Delete(file);
        }
        foreach (string file in Directory.GetFiles(from)) {
            string name = Path.GetFileName(file);
            if (name.EndsWith(".cs") || name.EndsWith(".csproj")) {
                File.Copy(file, Path.Combine(to, name), true);
            }
        }
    }

    // One row per benchmark case (class, method, parameters): the mean of every version, the change of
    // each version against the first, and the allocations when any version allocates.
    private static string Compare(string outDir, List<string> versions, List<string> labels, string job, string filter) {
        var data = new List<Dictionary<string, (double meanNs, double? bytes)>>();
        var order = new List<(string type, string key)>();
        foreach (string version in versions) {
            var cases = new Dictionary<string, (double, double?)>();
            string results = Path.Combine(outDir, Safe(version), "results");
            if (Directory.Exists(results)) {
                foreach (string file in Directory.GetFiles(results, "*-report*.json").OrderBy(x => x)) {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    foreach (var b in doc.RootElement.GetProperty("Benchmarks").EnumerateArray()) {
                        string type = b.GetProperty("Type").GetString();
                        string method = b.GetProperty("Method").GetString();
                        string parameters = b.TryGetProperty("Parameters", out var p) ? p.GetString() : "";
                        string key = type + "|" + method + (string.IsNullOrEmpty(parameters) ? "" : " (" + parameters.Replace("&", ", ") + ")");
                        if (!b.TryGetProperty("Statistics", out var stats) || stats.ValueKind != JsonValueKind.Object) {
                            continue;
                        }
                        double mean = stats.GetProperty("Mean").GetDouble();
                        double? bytes = null;
                        if (b.TryGetProperty("Memory", out var mem) && mem.ValueKind == JsonValueKind.Object &&
                            mem.TryGetProperty("BytesAllocatedPerOperation", out var alloc) && alloc.ValueKind == JsonValueKind.Number) {
                            bytes = alloc.GetDouble();
                        }
                        cases[key] = (mean, bytes);
                        if (!order.Contains((type, key))) {
                            order.Add((type, key));
                        }
                    }
                }
            }
            data.Add(cases);
        }

        var sb = new StringBuilder();
        sb.AppendLine("# KenseiECS benchmarks, version against version");
        sb.AppendLine();
        sb.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm}, {Environment.OSVersion}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}, " +
                      $"{Environment.ProcessorCount} logical CPUs, filter `{filter}`, job {job}. Same benchmark sources for every version.");
        sb.AppendLine();
        sb.AppendLine("For each case the fastest version is the base: its mean time per operation is in the Fastest column,");
        sb.AppendLine("every other version shows how much slower it is (+12.3% = 12.3% more time than the fastest).");
        foreach (var group in order.GroupBy(x => x.type)) {
            sb.AppendLine();
            sb.AppendLine("## " + group.Key);
            sb.AppendLine();
            sb.Append("| Case | Fastest |");
            foreach (string version in versions) {
                sb.Append(' ').Append(version).Append(" |");
            }
            sb.AppendLine(" Allocated |");
            sb.Append("|---|---|");
            for (int i = 0; i < versions.Count; i++) {
                sb.Append("---:|");
            }
            sb.AppendLine("---|");
            foreach (var (_, key) in group) {
                int fastest = -1;
                for (int v = 0; v < versions.Count; v++) {
                    if (data[v].TryGetValue(key, out var c) && (fastest < 0 || c.meanNs < data[fastest][key].meanNs)) {
                        fastest = v;
                    }
                }
                double baseNs = data[fastest][key].meanNs;
                sb.Append("| ").Append(key.Substring(key.IndexOf('|') + 1)).Append(" | ")
                    .Append(versions[fastest]).Append(", ").Append(Time(baseNs)).Append(" |");
                for (int v = 0; v < versions.Count; v++) {
                    string cell = !data[v].TryGetValue(key, out var c) ? "—"
                        : v == fastest ? "base"
                        : ((c.meanNs / baseNs - 1) * 100).ToString("+0.0", CultureInfo.InvariantCulture) + "%";
                    sb.Append(' ').Append(cell).Append(" |");
                }
                var allocs = new List<double>();
                for (int v = 0; v < versions.Count; v++) {
                    if (data[v].TryGetValue(key, out var c) && c.bytes.HasValue) {
                        allocs.Add(c.bytes.Value);
                    }
                }
                string alloc = allocs.Count == 0 ? "—"
                    : allocs.Min() == allocs.Max() ? Bytes(allocs[0])
                    : Bytes(allocs.Min()) + " – " + Bytes(allocs.Max());
                sb.Append(' ').Append(alloc).AppendLine(" |");
            }
        }
        return sb.ToString();
    }

    private static string Time(double ns) {
        var c = CultureInfo.InvariantCulture;
        if (ns >= 1e6) return (ns / 1e6).ToString("0.00", c) + " ms";
        if (ns >= 1e3) return (ns / 1e3).ToString("0.00", c) + " µs";
        return ns.ToString("0.0", c) + " ns";
    }

    private static string Bytes(double b) => b == 0 ? "0 B" : b >= 1024 ? (b / 1024).ToString("0.#", CultureInfo.InvariantCulture) + " KB" : b.ToString("0", CultureInfo.InvariantCulture) + " B";

    private static string Safe(string version) => version.Replace('/', '_');

    private static string Run(string file, string arguments, string dir, bool allowFailure = false) {
        var psi = new ProcessStartInfo(file, arguments) {
            WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        using var p = Process.Start(psi);
        string output = p.StandardOutput.ReadToEnd();
        string error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0 && !allowFailure) {
            throw new InvalidOperationException($"{file} {arguments} failed ({p.ExitCode}): {error}");
        }
        return output;
    }

    private static void RunStreaming(string file, string arguments, string dir) {
        var psi = new ProcessStartInfo(file, arguments) { WorkingDirectory = dir, UseShellExecute = false };
        using var p = Process.Start(psi);
        p.WaitForExit();
        if (p.ExitCode != 0) {
            throw new InvalidOperationException($"{file} {arguments} failed ({p.ExitCode})");
        }
    }
}
