using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;

namespace KenseiECS.Generators.Tests {
    // The compile-time backstop of the release build guard (KenseiECS/DevTools/KenseiDebugReleaseCheck.cs)
    // is parsed with the define sets Unity compiles with. It lives here because this project already
    // references Roslyn; the runtime test project compiles it only with its own fixed defines.
    [TestFixture]
    public class KenseiDebugReleaseCheckTests {
        private static string SourcePath([CallerFilePath] string here = "") =>
            Path.Combine(Path.GetDirectoryName(here), "..", "KenseiECS", "DevTools", "KenseiDebugReleaseCheck.cs");

        private static bool HasErrorDirective(params string[] defines) {
            var options = CSharpParseOptions.Default.WithPreprocessorSymbols(defines);
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(SourcePath()), options);
            return tree.GetDiagnostics().Any(d => d.Id == "CS1029" && d.Severity == DiagnosticSeverity.Error);
        }

        [Test]
        public void ReleasePlayerWithKenseiDebug_FailsToCompile() {
            // KENSEI_DEBUG from any source (e.g. BuildPlayerOptions.extraScriptingDefines) reaches the player compile.
            Assert.That(HasErrorDirective("UNITY_5_3_OR_NEWER", "KENSEI_DEBUG"), Is.True);
        }

        [TestCase("UNITY_5_3_OR_NEWER", "KENSEI_DEBUG", "DEVELOPMENT_BUILD", TestName = "DevelopmentPlayer")]
        [TestCase("UNITY_5_3_OR_NEWER", "KENSEI_DEBUG", "UNITY_EDITOR", TestName = "Editor")]
        [TestCase("UNITY_5_3_OR_NEWER", "KENSEI_DEBUG", "KENSEI_ALLOW_DEBUG_RELEASE", TestName = "ReleaseAllowed")]
        [TestCase("UNITY_5_3_OR_NEWER", null, null, TestName = "ReleaseWithoutDebug")]
        [TestCase("KENSEI_DEBUG", null, null, TestName = "DotNetDebugBuild")]
        public void OtherBuilds_Compile(string a, string b, string c) {
            var defines = new[] { a, b, c }.Where(d => d != null).ToArray();
            Assert.That(HasErrorDirective(defines), Is.False);
        }
    }
}
