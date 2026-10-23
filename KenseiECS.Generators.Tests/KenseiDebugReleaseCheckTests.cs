using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using KenseiECS.Editor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;

namespace KenseiECS.Generators.Tests {
    // The release marker (KenseiECS/DevTools/KenseiDebugReleaseCheck.cs) is compiled with the define
    // sets Unity compiles with, and the emitted DLL is searched the way the editor check
    // (KenseiDebugPlayerScriptsCheck) does. It lives here because this project already references
    // Roslyn; the runtime test project compiles the file only with its own fixed defines.
    // Development builds carry the marker too: the editor check skips them by the build options.
    [TestFixture]
    public class KenseiDebugReleaseCheckTests {
        private static string PackageDir([CallerFilePath] string here = "") =>
            Path.Combine(Path.GetDirectoryName(here), "..", "KenseiECS");

        private static string SourcePath() => Path.Combine(PackageDir(), "DevTools", "KenseiDebugReleaseCheck.cs");

        private static byte[] Compile(params string[] defines) {
            var options = CSharpParseOptions.Default.WithPreprocessorSymbols(defines);
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(SourcePath()), options);
            var compilation = CSharpCompilation.Create("KenseiECS", new[] { tree },
                new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var stream = new MemoryStream();
            var result = compilation.Emit(stream);
            Assert.That(result.Success, Is.True, string.Join("\n", result.Diagnostics));
            return stream.ToArray();
        }

        private static bool DefinesMarkerType(byte[] dll) {
            using var pe = new PEReader(new MemoryStream(dll));
            var reader = pe.GetMetadataReader();
            return reader.TypeDefinitions.Any(h => reader.GetString(reader.GetTypeDefinition(h).Name) == KenseiDebugDefines.RELEASE_MARKER);
        }

        // KENSEI_DEBUG from any source (e.g. BuildPlayerOptions.extraScriptingDefines) reaches the player compile.
        [TestCase("UNITY_5_3_OR_NEWER", "KENSEI_DEBUG", null, TestName = "ReleasePlayer")]
        [TestCase("UNITY_5_3_OR_NEWER", "KENSEI_DEBUG", "DEVELOPMENT_BUILD", TestName = "DevelopmentPlayer")]
        public void PlayerWithKenseiDebug_CompilesTheMarker(string a, string b, string c) {
            var dll = Compile(new[] { a, b, c }.Where(d => d != null).ToArray());
            Assert.That(DefinesMarkerType(dll), Is.True);
            Assert.That(KenseiDebugDefines.ContainsReleaseMarker(dll), Is.True, "the editor check finds it in the DLL bytes");
        }

        [TestCase("UNITY_5_3_OR_NEWER", "KENSEI_DEBUG", "UNITY_EDITOR", TestName = "Editor")]
        [TestCase("UNITY_5_3_OR_NEWER", "KENSEI_DEBUG", "KENSEI_ALLOW_DEBUG_RELEASE", TestName = "ReleaseAllowed")]
        [TestCase("UNITY_5_3_OR_NEWER", null, null, TestName = "ReleaseWithoutDebug")]
        [TestCase("KENSEI_DEBUG", null, null, TestName = "DotNetDebugBuild")]
        public void OtherBuilds_HaveNoMarker(string a, string b, string c) {
            var dll = Compile(new[] { a, b, c }.Where(d => d != null).ToArray());
            Assert.That(DefinesMarkerType(dll), Is.False);
            Assert.That(KenseiDebugDefines.ContainsReleaseMarker(dll), Is.False);
        }

        [Test]
        public void ReleaseMarkerSearch_MatchesWholeNamesOnly() {
            byte[] Heap(string text) => System.Text.Encoding.UTF8.GetBytes("\0" + text + "\0");

            Assert.That(KenseiDebugDefines.ContainsReleaseMarker(Heap(KenseiDebugDefines.RELEASE_MARKER)), Is.True);
            Assert.That(KenseiDebugDefines.ContainsReleaseMarker(Heap(KenseiDebugDefines.RELEASE_MARKER + "Old")), Is.False);
            Assert.That(KenseiDebugDefines.ContainsReleaseMarker(Heap("My" + KenseiDebugDefines.RELEASE_MARKER)), Is.False);
            Assert.That(KenseiDebugDefines.ContainsReleaseMarker(null), Is.False);
            Assert.That(KenseiDebugDefines.ContainsReleaseMarker(new byte[3]), Is.False);
        }

        [Test]
        public void ReleaseMarkerName_AppearsInRuntimeSourcesOnlyInItsDeclaration() {
            // The byte search would miss the marker if another name in KenseiECS.dll ended with it (the
            // string heap shares suffixes), and would match a parameter or member of the same name. Editor
            // code is a separate assembly; comments do not reach the DLL.
            var runtimeFiles = Directory.EnumerateFiles(PackageDir(), "*.cs", SearchOption.AllDirectories)
                .Where(p => !p.Contains(Path.DirectorySeparatorChar + "Editor" + Path.DirectorySeparatorChar) &&
                            !p.Contains("Samples~"));
            var uses = runtimeFiles
                .SelectMany(p => File.ReadAllLines(p).Select(line => (p, line: line.Trim())))
                .Where(x => !x.line.StartsWith("//") && x.line.Contains(KenseiDebugDefines.RELEASE_MARKER))
                .ToList();
            Assert.That(uses.Count, Is.EqualTo(1), string.Join("\n", uses.Select(u => u.p + ": " + u.line)));
            Assert.That(uses[0].line, Does.Contain("class " + KenseiDebugDefines.RELEASE_MARKER + " "));
        }

        [TestCase("-define:KENSEI_DEBUG", ExpectedResult = true)]
        [TestCase("/d:FOO;KENSEI_DEBUG", ExpectedResult = true)]
        [TestCase("-d:FOO,KENSEI_DEBUG -nowarn:0169", ExpectedResult = true)]
        [TestCase("-define:\"FOO;KENSEI_DEBUG\"", ExpectedResult = true)]
        [TestCase("-define:KENSEI_DEBUGGER", ExpectedResult = false)]
        [TestCase("-nowarn:KENSEI_DEBUG", ExpectedResult = false)]
        [TestCase("KENSEI_DEBUG", ExpectedResult = false)]
        public bool ArgumentsDefine_ReadsDefineArguments(string line) {
            return KenseiDebugDefines.ArgumentsDefine(new[] { line });
        }

        [Test]
        public void CompiledReleaseError_NamesTheSourceOrEverySourceTheGuardDoesNotRead() {
            var named = KenseiDebugDefines.CompiledReleaseError(new[] { "BuildPlayerOptions.extraScriptingDefines" });
            Assert.That(named, Does.Contain("KENSEI_DEBUG").And.Contain("it comes from BuildPlayerOptions.extraScriptingDefines.")
                .And.Contain("Development Build").And.Contain("KENSEI_ALLOW_DEBUG_RELEASE"));
            Assert.That(named, Does.Not.Contain("csc.rsp"));

            var unknown = KenseiDebugDefines.CompiledReleaseError(new string[0]);
            Assert.That(unknown, Does.Contain("extraScriptingDefines").And.Contain("Additional Compiler Arguments").And.Contain("csc.rsp"));
        }
    }
}
