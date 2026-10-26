using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Stand.Contracts.Repros {
    /// <summary>
    /// Runs every repro class (namespace Stand.Contracts.Repros, const Id, const Expected, static Run())
    /// and writes the comparison to the path given as the first argument.
    /// </summary>
    public static class ReproMain {
        public sealed class Result {
            public string Id { get; set; }
            public string Expected { get; set; }
            public string Observed { get; set; }
            public string Status { get; set; }
            public string Error { get; set; }
        }

        public static int Main(string[] args) {
            if (args.Length < 1) {
                Console.Error.WriteLine("usage: Stand.Contracts.Repro <repro-check.json>");
                return 2;
            }
            var results = new List<Result>();
            var types = typeof(ReproMain).Assembly.GetTypes()
                .Where(t => t.Namespace == "Stand.Contracts.Repros" && t.GetField("Id") != null && t.GetMethod("Run", Type.EmptyTypes) != null)
                .OrderBy(t => t.Name, StringComparer.Ordinal);
            foreach (var t in types) {
                var r = new Result {
                    Id = (string)t.GetField("Id").GetValue(null),
                    Expected = (string)t.GetField("Expected")?.GetValue(null)
                };
                try {
                    r.Observed = (string)t.GetMethod("Run", Type.EmptyTypes).Invoke(null, null);
                    r.Status = r.Observed == r.Expected ? "verified" : "mismatch";
                } catch (TargetInvocationException e) {
                    r.Status = "error";
                    r.Error = e.InnerException?.GetType().Name + ": " + e.InnerException?.Message;
                }
                results.Add(r);
            }
            var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            File.WriteAllText(args[0], JsonSerializer.Serialize(results, options));
            Console.WriteLine("repro check: " + results.Count + " repros, verified " + results.Count(r => r.Status == "verified")
                + ", mismatch " + results.Count(r => r.Status == "mismatch") + ", error " + results.Count(r => r.Status == "error"));
            foreach (var r in results.Where(r => r.Status != "verified")) {
                Console.WriteLine("  " + r.Status + " " + r.Id + ": expected [" + r.Expected + "], got [" + (r.Observed ?? r.Error) + "]");
            }
            return 0;
        }
    }
}
