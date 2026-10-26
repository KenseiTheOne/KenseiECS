// Runs every [Test] method of every [TestFixture] class compiled into this assembly (the generated
// repros) and writes one line per test to <repro dir>/verify.txt:
//   REPRODUCED <test>: <assertion message>   the repro fails on this tree, as it should while the defect is there
//   NOT REPRODUCED <test>                    the repro passes: the defect is gone (or the repro is wrong)
//   IGNORED <test>: <reason>                 built in the other configuration (release vs KENSEI_DEBUG)
//   ERROR <test>: <exception>                the repro itself broke
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

public static class ReproMain {
    public static int Main(string[] args) {
        if (args.Length < 1) {
            Console.Error.WriteLine("usage: FuzzCore.ReproCheck <repro dir>");
            return 64;
        }
        var lines = new List<string>();
        var fixtures = typeof(ReproMain).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<TestFixtureAttribute>() != null)
            .OrderBy(t => t.FullName, StringComparer.Ordinal);
        foreach (var type in fixtures) {
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(x => x.GetCustomAttribute<TestAttribute>() != null)) {
                string name = type.Name + "." + m.Name;
                string line;
                // Each test in its own NUnit context: outside a test runner, failures would otherwise accumulate.
                using var isolated = new NUnit.Framework.Internal.TestExecutionContext.IsolatedContext();
                try {
                    var obj = Activator.CreateInstance(type);
                    m.Invoke(obj, null);
                    line = "NOT REPRODUCED " + name;
                } catch (TargetInvocationException e) when (e.InnerException is IgnoreException ig) {
                    line = "IGNORED " + name + ": " + ig.Message.Replace('\n', ' ');
                } catch (TargetInvocationException e) when (e.InnerException is AssertionException ae) {
                    line = "REPRODUCED " + name + ": " + First(ae.Message);
                } catch (TargetInvocationException e) {
                    line = "ERROR " + name + ": " + e.InnerException.GetType().Name + ": " + First(e.InnerException.Message);
                }
                lines.Add(line);
                Console.WriteLine(line);
            }
        }
        File.WriteAllLines(Path.Combine(args[0], "verify.txt"), lines);
        return 0;
    }

    private static string First(string s) {
        var parts = s.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).Take(3);
        return string.Join(" | ", parts);
    }
}
