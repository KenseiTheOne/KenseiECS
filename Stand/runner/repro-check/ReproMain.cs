// Runs every generated Repro_* class and writes one line per repro to <repro dir>/verify.txt:
// "REPRODUCED <class>: <what broke>", "NOT REPRODUCED <class>" or "ERROR <class>: <exception>".
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

public static class ReproMain {
    public static int Main(string[] args) {
        if (args.Length < 1) {
            Console.Error.WriteLine("usage: Stand.Runner.ReproCheck <repro dir>");
            return 64;
        }
        var lines = new List<string>();
        var types = typeof(ReproMain).Assembly.GetTypes()
            .Where(t => t.Name.StartsWith("Repro_", StringComparison.Ordinal) && t.GetMethod("Run", BindingFlags.Public | BindingFlags.Static) != null)
            .OrderBy(t => t.Name, StringComparer.Ordinal);
        foreach (var type in types) {
            string line;
            try {
                var result = (string)type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
                line = result != null ? "REPRODUCED " + type.Name + ": " + result.Replace('\n', ' ') : "NOT REPRODUCED " + type.Name;
            } catch (TargetInvocationException e) {
                line = "ERROR " + type.Name + ": " + e.InnerException.GetType().Name + ": " + e.InnerException.Message.Replace('\n', ' ');
            }
            lines.Add(line);
            Console.WriteLine(line);
        }
        File.WriteAllLines(Path.Combine(args[0], "verify.txt"), lines);
        return 0;
    }
}
