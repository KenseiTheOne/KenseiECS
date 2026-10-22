using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using KenseiECS.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;

namespace KenseiECS.Generators.Tests {
    [TestFixture]
    public class SystemInjectionGeneratorTests {
        private const string Snippet = @"
using KenseiECS;

namespace Demo {
    public struct Position : IComponent { public float X; }
    public struct Velocity : IComponent { public float X; }
    public struct Frozen : IComponent { }
    public struct Shield : IComponent { }
    public class Config { public float Speed = 2; }
    public class Names { public string Value; }

    public partial class MoveSystem : IRunSystem {
        [Inc(typeof(Position), typeof(Velocity))] [Exc(typeof(Frozen))]
        private Filter _moving;
        [Inc(typeof(Position))] [Any(typeof(Velocity), typeof(Shield))]
        private Filter _anyFilter;
        [Pool] private ComponentPool<Position> _positions;
        [Pool] private ComponentPool<Velocity> _velocities;
        [Group] private Group<Position, Velocity> _group;
        [Shared] private Config _config;
        [Shared(""names"")] private Names _names;

        public bool OnInitCalled;
        public int GroupCountAtInit = -1;

        partial void OnInit(World world, SharedData shared) {
            OnInitCalled = true;
            GroupCountAtInit = _group.Count;
        }

        public Filter Moving => _moving;
        public Filter AnyFilter => _anyFilter;
        public string NamesValue => _names.Value;

        public void Run(World world) {
            foreach (int e in _moving) {
                _positions.Get(e).X += _velocities.Get(e).X * _config.Speed;
            }
        }
    }

    public static partial class Outer {
        public partial class Nested : IRunSystem {
            [Pool] private ComponentPool<Position> _positions;
            public bool HasPool => _positions != null;
            public void Run(World world) { }
        }
    }
}";

        // The DLL shipped inside the Unity package must stay functional, whatever
        // compiler built it: load it by path and run the same end-to-end scenario.
        private static ISourceGenerator LoadPackagedGenerator() {
            string dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "KenseiECS.sln"))) {
                dir = Path.GetDirectoryName(dir);
            }
            Assert.That(dir, Is.Not.Null, "repository root with KenseiECS.sln must be above the test directory");
            string dll = Path.Combine(dir!, "KenseiECS", "Plugins", "KenseiECS.Generators.dll");
            Assert.That(File.Exists(dll), Is.True, "packaged generator DLL must exist at " + dll);
            // A separate load context: Assembly.LoadFrom would hand back the
            // KenseiECS.Generators the test project references, not the packaged file.
            // Roslyn itself still resolves from the default context, so the types match.
            var assembly = new AssemblyLoadContext("PackagedGenerator").LoadFromAssemblyPath(dll);
            var type = assembly.GetType("KenseiECS.Generators.SystemInjectionGenerator");
            Assert.That(type, Is.Not.Null, "packaged DLL must contain the generator type");
            return (ISourceGenerator)Activator.CreateInstance(type!)!;
        }

        [Test]
        public void PackagedDll_GeneratesWorkingInit() {
            var (compilation, diagnostics, generated) = RunGenerator(Snippet, LoadPackagedGenerator());
            Assert.That(diagnostics.Errors, Is.Empty, "packaged generator must not report errors: " + generated);
            var assembly = Emit(compilation);
            var systemType = assembly.GetType("Demo.MoveSystem")!;
            var system = (ISystem)Activator.CreateInstance(systemType)!;
            Assert.That(system, Is.InstanceOf<IInitSystem>(), "packaged generator must add IInitSystem");

            var shared = new SharedData();
            AddShared(shared, assembly.GetType("Demo.Config")!, Activator.CreateInstance(assembly.GetType("Demo.Config")!)!, null);
            AddShared(shared, assembly.GetType("Demo.Names")!, Activator.CreateInstance(assembly.GetType("Demo.Names")!)!, "names");
            new SystemsRunner(new World(), shared).Add(system).Init();
            Assert.That(systemType.GetField("OnInitCalled")!.GetValue(system), Is.True, "packaged generator's Init must run OnInit");
        }

        // Catches a packaged DLL that was not rebuilt after a generator change.
        [Test]
        public void PackagedDll_MatchesSourceDiagnostics() {
            const string source = @"
using KenseiECS;
public struct P : IComponent { }
public partial class Broken : IRunSystem {
    [Pool] private readonly ComponentPool<P> _p;
    public void Run(World world) { }
}";
            var (_, diagnostics, _) = RunGenerator(source, LoadPackagedGenerator());
            Assert.That(diagnostics.Errors.Select(d => d.Id), Does.Contain("KECS009"),
                "packaged DLL must be rebuilt from KenseiECS.Generators (Release) and copied to KenseiECS/Plugins");

            const string inherited = @"
using KenseiECS;
public struct P : IComponent { }
public partial class BaseSystem : IRunSystem {
    [Pool] private ComponentPool<P> _p;
    public bool HasPool => _p != null;
    public void Run(World world) { }
}
public partial class Derived : BaseSystem {
    [Pool] private ComponentPool<P> _q;
    public bool HasOwn => _q != null;
}";
            var (compilation, inheritedDiagnostics, generated) = RunGenerator(inherited, LoadPackagedGenerator());
            Assert.That(inheritedDiagnostics.Errors, Is.Empty, "packaged DLL must support inherited injection (2.2): " + generated);
            var assembly = Emit(compilation);
            var derivedType = assembly.GetType("Derived")!;
            var system = (ISystem)Activator.CreateInstance(derivedType)!;
            new SystemsRunner(new World()).Add(system).Init();
            Assert.That(derivedType.GetProperty("HasPool")!.GetValue(system), Is.True, "packaged DLL must chain the base Init");
            Assert.That(derivedType.GetProperty("HasOwn")!.GetValue(system), Is.True, "packaged DLL must inject the derived fields");

            const string captured = @"
using KenseiECS;
public struct P : IComponent { }
public partial class G1 : IRunSystem {
    [Pool] private ComponentPool<P> _p;
    public void Run(World world) { }
}
public abstract class Mid : G1 {
    protected void Init(World world, object context) { }
}
public partial class G2 : Mid {
    [Pool] private ComponentPool<P> _q;
}";
            var (_, capturedDiagnostics, _) = RunGenerator(captured, LoadPackagedGenerator());
            Assert.That(capturedDiagnostics.Errors.Select(d => d.Id), Does.Contain("KECS006"),
                "packaged DLL must report an intermediate Init overload that takes the base call (2.2 round 3)");

            const string doubleCall = @"
using KenseiECS;
public struct P : IComponent { }
public partial class G1 : IRunSystem {
    [Pool] private ComponentPool<P> _p;
    public void Run(World world) { }
}
public partial class G2 : G1 {
    [Pool] private ComponentPool<P> _q;
    partial void OnInit(World world, SharedData shared) => base.Init(world, shared);
}";
            var (_, doubleCallDiagnostics, _) = RunGenerator(doubleCall, LoadPackagedGenerator());
            Assert.That(doubleCallDiagnostics.Errors.Select(d => d.Id), Does.Contain("KECS012"),
                "packaged DLL must report a base.Init that the generated Init already makes (2.2 KECS012)");
        }

        private static (Compilation compilation, ImmutableArrayWrapper diagnostics, string generated) RunGenerator(string source) {
            return RunGenerator(source, new SystemInjectionGenerator());
        }

        private static (Compilation compilation, ImmutableArrayWrapper diagnostics, string generated) RunGenerator(string source, ISourceGenerator generator, bool nullable = false, IEnumerable<MetadataReference> extraReferences = null) {
            var references = new List<MetadataReference>(extraReferences ?? Array.Empty<MetadataReference>()) {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(World).Assembly.Location),
                MetadataReference.CreateFromFile(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
                MetadataReference.CreateFromFile(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Collections.dll")),
                MetadataReference.CreateFromFile(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "netstandard.dll"))
            };

            var parseOptions = new CSharpParseOptions(LanguageVersion.CSharp9);
            var compilation = CSharpCompilation.Create(
                "GeneratedSystems",
                new[] { CSharpSyntaxTree.ParseText(source, parseOptions) },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                    nullableContextOptions: nullable ? NullableContextOptions.Enable : NullableContextOptions.Disable));

            var driver = CSharpGeneratorDriver.Create(new[] { generator }, parseOptions: parseOptions);
            driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
            string generated = string.Join("\n\n", output.SyntaxTrees.Skip(1).Select(t => t.ToString()));
            return (output, new ImmutableArrayWrapper(diagnostics), generated);
        }

        private static Assembly Emit(Compilation compilation) {
            using var stream = new MemoryStream();
            var result = compilation.Emit(stream);
            Assert.That(result.Success, Is.True,
                "generated code must compile: " + string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
            return Assembly.Load(stream.ToArray());
        }

        private static MetadataReference EmitReference(Compilation compilation, string assemblyName) {
            using var stream = new MemoryStream();
            var result = compilation.WithAssemblyName(assemblyName).Emit(stream);
            Assert.That(result.Success, Is.True, "referenced code must compile");
            return MetadataReference.CreateFromImage(stream.ToArray());
        }

        // No errors or warnings (CS0108/CS0114 hiding, CS0109 needless `new`) in the generated trees.
        private static void AssertGeneratedCompilesCleanly(Compilation compilation, string generated) {
            var userTree = compilation.SyntaxTrees.First();
            var problems = compilation.GetDiagnostics()
                .Where(d => d.Severity >= DiagnosticSeverity.Warning && d.Location.SourceTree != null && d.Location.SourceTree != userTree);
            Assert.That(problems, Is.Empty, "generated code must compile without warnings: " + generated);
        }

        private static string[] Log(Type type, object system) {
            return ((List<string>)type.GetField("Log")!.GetValue(system)!).ToArray();
        }

        [Test]
        public void GeneratedSource_ImplementsIInitSystem() {
            var (_, diagnostics, generated) = RunGenerator(Snippet);
            Assert.That(diagnostics.Errors, Is.Empty, "generator must not report errors: " + generated);
            Assert.That(generated, Does.Contain("partial class MoveSystem : global::KenseiECS.IInitSystem"), "generated partial must add IInitSystem");
            Assert.That(generated, Does.Contain("world.Filter().Inc<global::Demo.Position>().Inc<global::Demo.Velocity>().Exc<global::Demo.Frozen>().End()"), "filter construction must follow the attributes");
            Assert.That(generated, Does.Contain("shared.Get<global::Demo.Names>(\"names\")"), "keyed shared lookup must be emitted");
        }

        [Test]
        public void GeneratedSystem_RunsEndToEnd() {
            var (compilation, diagnostics, generated) = RunGenerator(Snippet);
            Assert.That(diagnostics.Errors, Is.Empty, "generator must not report errors: " + generated);
            var assembly = Emit(compilation);

            var systemType = assembly.GetType("Demo.MoveSystem")!;
            var positionType = assembly.GetType("Demo.Position")!;
            var velocityType = assembly.GetType("Demo.Velocity")!;
            var configType = assembly.GetType("Demo.Config")!;
            var namesType = assembly.GetType("Demo.Names")!;

            var world = new World();
            var shared = new SharedData();
            AddShared(shared, configType, Activator.CreateInstance(configType)!, null);
            var names = Activator.CreateInstance(namesType)!;
            namesType.GetField("Value")!.SetValue(names, "ok");
            AddShared(shared, namesType, names, "names");

            var system = (ISystem)Activator.CreateInstance(systemType)!;
            Assert.That(system, Is.InstanceOf<IInitSystem>(), "generated class must implement IInitSystem");

            var runner = new SystemsRunner(world, shared).Add(system);
            runner.Init();

            Assert.That(systemType.GetField("OnInitCalled")!.GetValue(system), Is.True, "OnInit must be called after injection");
            Assert.That(systemType.GetField("GroupCountAtInit")!.GetValue(system), Is.EqualTo(0), "group must be injected before OnInit");
            Assert.That(systemType.GetProperty("NamesValue")!.GetValue(system), Is.EqualTo("ok"), "keyed shared data must be injected");

            var entity = CreateEntity(world, positionType, 1f);
            AddComponent(world, entity, velocityType, 3f);
            runner.Run();

            var moving = (Filter)systemType.GetProperty("Moving")!.GetValue(system)!;
            var anyFilter = (Filter)systemType.GetProperty("AnyFilter")!.GetValue(system)!;
            Assert.That(moving.Count, Is.EqualTo(1), "Inc/Exc filter must be injected and match");
            Assert.That(anyFilter.Count, Is.EqualTo(1), "Inc/Any filter must be injected and match");
            Assert.That(GetX(world, entity, positionType), Is.EqualTo(7f), "Run must use injected pools and shared config (1 + 3 * 2)");
        }

        [Test]
        public void NestedPartialClass_IsSupported() {
            var (compilation, diagnostics, generated) = RunGenerator(Snippet);
            Assert.That(diagnostics.Errors, Is.Empty, "generator must not report errors: " + generated);
            var assembly = Emit(compilation);
            var nestedType = assembly.GetType("Demo.Outer+Nested")!;
            var system = (ISystem)Activator.CreateInstance(nestedType)!;
            new SystemsRunner(new World()).Add(system).Init();
            Assert.That(nestedType.GetProperty("HasPool")!.GetValue(system), Is.True, "nested partial class must get its pool injected");
        }

        [Test]
        public void NonPartialClass_ReportsError() {
            const string source = @"
using KenseiECS;
public struct P : IComponent { }
public class Broken : IRunSystem {
    [Pool] private ComponentPool<P> _p;
    public void Run(World world) { }
}";
            var (_, diagnostics, _) = RunGenerator(source);
            Assert.That(diagnostics.Errors.Select(d => d.Id), Does.Contain("KECS001"), "non-partial class with injected fields must produce KECS001");
        }

        [Test]
        public void NonPartialContainer_ReportsError() {
            const string source = @"
using KenseiECS;
public struct P : IComponent { }
public static class Outer {
    public partial class Nested : IRunSystem {
        [Pool] private ComponentPool<P> _p;
        public void Run(World world) { }
    }
}";
            var (_, diagnostics, _) = RunGenerator(source);
            Assert.That(diagnostics.Errors.Select(d => d.Id), Does.Contain("KECS005"), "non-partial containing type must produce KECS005");
        }

        [Test]
        public void ExplicitInit_ReportsError() {
            const string source = @"
using KenseiECS;
public struct P : IComponent { }
public partial class Broken : IInitSystem {
    [Pool] private ComponentPool<P> _p;
    public void Init(World world, SharedData shared) { }
}";
            var (_, diagnostics, _) = RunGenerator(source);
            Assert.That(diagnostics.Errors.Select(d => d.Id), Does.Contain("KECS002"), "hand-written Init next to injected fields must produce KECS002");
        }

        [Test]
        public void WrongFieldType_ReportsError() {
            const string source = @"
using KenseiECS;
public struct P : IComponent { }
public partial class Broken : IRunSystem {
    [Pool] private int _p;
    public void Run(World world) { }
}";
            var (_, diagnostics, _) = RunGenerator(source);
            Assert.That(diagnostics.Errors.Select(d => d.Id), Does.Contain("KECS003"), "[Pool] on a non-pool field must produce KECS003");
        }

        [Test]
        public void ExcludeOnlyFilter_ReportsError() {
            const string source = @"
using KenseiECS;
public struct P : IComponent { }
public partial class Broken : IRunSystem {
    [Exc(typeof(P))] private Filter _f;
    public void Run(World world) { }
}";
            var (_, diagnostics, _) = RunGenerator(source);
            Assert.That(diagnostics.Errors.Select(d => d.Id), Does.Contain("KECS004"), "[Exc] without [Inc]/[Any] must produce KECS004");
        }

        // 2.2: the base's generated Init turns virtual, the derived one overrides it and
        // calls base.Init first, so both get their fields and OnInit runs base-first.
        [Test]
        public void DerivedFromGeneratedSystem_InjectsBaseThenDerived() {
            const string source = @"
using KenseiECS;
using System.Collections.Generic;
public struct A : IComponent { }
public struct B : IComponent { }
public partial class BaseSystem : IRunSystem {
    [Pool] private ComponentPool<A> _a;
    public List<string> Log = new List<string>();
    public bool HasPool => _a != null;
    partial void OnInit(World world, SharedData shared) { Log.Add(""base:"" + (_a != null)); }
    public virtual void Run(World world) { }
}
public partial class Derived : BaseSystem {
    [Inc(typeof(B))] private Filter _f;
    public bool HasFilter => _f != null;
    partial void OnInit(World world, SharedData shared) { Log.Add(""derived:"" + (_f != null) + "","" + HasPool); }
}";
            var (compilation, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.Errors, Is.Empty, "a derived generated system must be supported: " + generated);
            AssertGeneratedCompilesCleanly(compilation, generated);
            Assert.That(generated, Does.Contain("public virtual void Init("), "the base Init must become virtual");
            Assert.That(generated, Does.Contain("public override void Init("), "the derived Init must override it");
            Assert.That(generated, Does.Contain("base.Init(world, shared);"), "the derived Init must chain to the base");

            var assembly = Emit(compilation);
            var derivedType = assembly.GetType("Derived")!;
            var system = (ISystem)Activator.CreateInstance(derivedType)!;
            new SystemsRunner(new World()).Add(system).Init();
            Assert.That(derivedType.GetProperty("HasPool")!.GetValue(system), Is.True, "base fields must be injected");
            Assert.That(derivedType.GetProperty("HasFilter")!.GetValue(system), Is.True, "derived fields must be injected");
            Assert.That(Log(derivedType, system), Is.EqualTo(new[] { "base:True", "derived:True,True" }),
                "base Init (fields + OnInit) must finish before the derived fields and OnInit");

            var baseType = assembly.GetType("BaseSystem")!;
            var baseSystem = (ISystem)Activator.CreateInstance(baseType)!;
            new SystemsRunner(new World()).Add(baseSystem).Init();
            Assert.That(Log(baseType, baseSystem), Is.EqualTo(new[] { "base:True" }), "the base on its own still runs its Init once");
        }

        [Test]
        public void ThreeLevelChain_ThroughPlainIntermediate_RunsInOrder() {
            const string source = @"
using KenseiECS;
using System.Collections.Generic;
public struct A : IComponent { }
public struct B : IComponent { }
public struct C : IComponent { }
public abstract partial class Root : IRunSystem {
    [Pool] private ComponentPool<A> _a;
    public List<string> Log = new List<string>();
    partial void OnInit(World world, SharedData shared) { Log.Add(""root:"" + (_a != null)); }
    public abstract void Run(World world);
}
public abstract class Plain : Root { }
public partial class Middle : Plain {
    [Pool] private ComponentPool<B> _b;
    partial void OnInit(World world, SharedData shared) { Log.Add(""middle:"" + (_b != null)); }
    public override void Run(World world) { }
}
public sealed partial class Leaf : Middle {
    [Inc(typeof(C))] private Filter _c;
    partial void OnInit(World world, SharedData shared) { Log.Add(""leaf:"" + (_c != null)); }
}";
            var (compilation, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.Errors, Is.Empty, "a three-level chain must be supported: " + generated);
            AssertGeneratedCompilesCleanly(compilation, generated);
            var assembly = Emit(compilation);

            var leafType = assembly.GetType("Leaf")!;
            var leaf = (ISystem)Activator.CreateInstance(leafType)!;
            new SystemsRunner(new World()).Add(leaf).Init();
            Assert.That(Log(leafType, leaf), Is.EqualTo(new[] { "root:True", "middle:True", "leaf:True" }), "every level must run once, root first");

            var middleType = assembly.GetType("Middle")!;
            var middle = (ISystem)Activator.CreateInstance(middleType)!;
            new SystemsRunner(new World()).Add(middle).Init();
            Assert.That(Log(middleType, middle), Is.EqualTo(new[] { "root:True", "middle:True" }), "a middle class registered on its own runs its own chain");
        }

        [Test]
        public void HandWrittenIntermediateOverride_MakesGeneratedRootVirtual() {
            const string source = @"
using KenseiECS;
using System.Collections.Generic;
public struct A : IComponent { }
public struct B : IComponent { }
public abstract partial class Root : IRunSystem {
    [Pool] private ComponentPool<A> _a;
    public List<string> Log = new List<string>();
    partial void OnInit(World world, SharedData shared) { Log.Add(""root:"" + (_a != null)); }
    public abstract void Run(World world);
}
public abstract class Mid : Root {
    public override void Init(World world, SharedData shared) { base.Init(world, shared); Log.Add(""mid""); }
}
public partial class Leaf : Mid {
    [Pool] private ComponentPool<B> _b;
    partial void OnInit(World world, SharedData shared) { Log.Add(""leaf:"" + (_b != null)); }
    public override void Run(World world) { }
}";
            var (compilation, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.Errors, Is.Empty, "a hand-written intermediate override must be supported: " + generated);
            AssertGeneratedCompilesCleanly(compilation, generated);
            var assembly = Emit(compilation);

            var leafType = assembly.GetType("Leaf")!;
            var leaf = (ISystem)Activator.CreateInstance(leafType)!;
            new SystemsRunner(new World()).Add(leaf).Init();
            Assert.That(Log(leafType, leaf), Is.EqualTo(new[] { "root:True", "mid", "leaf:True" }), "root, the hand-written middle and the leaf must run once each, in order");
        }

        [Test]
        public void HandWrittenOverride_WithoutGeneratedSibling_MakesGeneratedBaseVirtual() {
            const string source = @"
using KenseiECS;
using System.Collections.Generic;
public struct A : IComponent { }
public abstract partial class Root : IRunSystem {
    [Pool] private ComponentPool<A> _a;
    public List<string> Log = new List<string>();
    partial void OnInit(World world, SharedData shared) { Log.Add(""root:"" + (_a != null)); }
    public abstract void Run(World world);
}
public class Leaf : Root {
    public override void Init(World world, SharedData shared) { base.Init(world, shared); Log.Add(""leaf""); }
    public override void Run(World world) { }
}";
            var (compilation, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.Errors, Is.Empty, generated);
            Assert.That(generated, Does.Contain("public virtual void Init("), "a hand-written override alone must make the generated Init virtual");
            AssertGeneratedCompilesCleanly(compilation, generated);
            var assembly = Emit(compilation);

            var leafType = assembly.GetType("Leaf")!;
            var leaf = (ISystem)Activator.CreateInstance(leafType)!;
            new SystemsRunner(new World()).Add(leaf).Init();
            Assert.That(Log(leafType, leaf), Is.EqualTo(new[] { "root:True", "leaf" }));
        }

        [Test]
        public void DerivedFromGenericGeneratedBase_IsSupported() {
            const string source = @"
using KenseiECS;
namespace Demo {
    public struct P : IComponent { }
    public struct Q : IComponent { }
    public abstract partial class PoolSystem<T> : IRunSystem where T : struct, IComponent {
        [Pool] protected ComponentPool<T> Pool;
        public void Run(World world) { }
    }
    public partial class QSystem : PoolSystem<P> {
        [Inc(typeof(Q))] private Filter _q;
        public bool AllInjected => Pool != null && _q != null;
    }
}";
            var (compilation, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.Errors, Is.Empty, "a generic generated base must be supported: " + generated);
            AssertGeneratedCompilesCleanly(compilation, generated);
            var assembly = Emit(compilation);
            var systemType = assembly.GetType("Demo.QSystem")!;
            var system = (ISystem)Activator.CreateInstance(systemType)!;
            new SystemsRunner(new World()).Add(system).Init();
            Assert.That(systemType.GetProperty("AllInjected")!.GetValue(system), Is.True, "the closed generic base and the derived class must both be injected");
        }

        [Test]
        public void DerivedFromVirtualHandWrittenInit_ChainsToIt() {
            const string source = @"
using KenseiECS;
using System.Collections.Generic;
public struct A : IComponent { }
public abstract class SystemBase : IInitSystem, IRunSystem {
    public List<string> Log = new List<string>();
    public virtual void Init(World world, SharedData shared) { Log.Add(""base""); }
    public abstract void Run(World world);
}
public partial class Derived : SystemBase {
    [Pool] private ComponentPool<A> _a;
    partial void OnInit(World world, SharedData shared) { Log.Add(""derived:"" + (_a != null)); }
    public override void Run(World world) { }
}";
            var (compilation, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.Errors, Is.Empty, "a public virtual hand-written base Init must be chained: " + generated);
            AssertGeneratedCompilesCleanly(compilation, generated);
            var assembly = Emit(compilation);
            var derivedType = assembly.GetType("Derived")!;
            var system = (ISystem)Activator.CreateInstance(derivedType)!;
            new SystemsRunner(new World()).Add(system).Init();
            Assert.That(Log(derivedType, system), Is.EqualTo(new[] { "base", "derived:True" }), "the base Init must run first, then the derived fields and OnInit");
        }

        [Test]
        public void DerivedFromAbstractHandWrittenInit_OverridesWithoutBaseCall() {
            const string source = @"
using KenseiECS;
public struct A : IComponent { }
public abstract class SystemBase : IInitSystem, IRunSystem {
    public abstract void Init(World world, SharedData shared);
    public void Run(World world) { }
}
public partial class Derived : SystemBase {
    [Pool] private ComponentPool<A> _a;
    public bool HasPool => _a != null;
}";
            var (compilation, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.Errors, Is.Empty, "an abstract base Init must be implemented by the generated override: " + generated);
            Assert.That(generated, Does.Not.Contain("base.Init"), "an abstract base Init cannot be called");
            AssertGeneratedCompilesCleanly(compilation, generated);
            var assembly = Emit(compilation);
            var derivedType = assembly.GetType("Derived")!;
            var system = (ISystem)Activator.CreateInstance(derivedType)!;
            new SystemsRunner(new World()).Add(system).Init();
            Assert.That(derivedType.GetProperty("HasPool")!.GetValue(system), Is.True, "the override must inject the fields");
        }

        [Test]
        public void DerivedFromNonVirtualHandWrittenInit_ReportsError() {
            const string source = @"
using KenseiECS;
public struct A : IComponent { }
public abstract class SystemBase : IInitSystem, IRunSystem {
    public void Init(World world, SharedData shared) { }
    public abstract void Run(World world);
}
public partial class Derived : SystemBase {
    [Pool] private ComponentPool<A> _a;
    public override void Run(World world) { }
}";
            var diagnostic = AssertReportedOnUserCode(source, "KECS006", "Derived");
            Assert.That(diagnostic.GetMessage(), Does.Contain("is not virtual"), "the message must say what to change");
        }

        [Test]
        public void DerivedFromExplicitHandWrittenInit_ReportsError() {
            const string source = @"
using KenseiECS;
public struct A : IComponent { }
public abstract class SystemBase : IInitSystem, IRunSystem {
    void IInitSystem.Init(World world, SharedData shared) { }
    public abstract void Run(World world);
}
public partial class Derived : SystemBase {
    [Pool] private ComponentPool<A> _a;
    public override void Run(World world) { }
}";
            var diagnostic = AssertReportedOnUserCode(source, "KECS006", "Derived");
            Assert.That(diagnostic.GetMessage(), Does.Contain("explicitly"), "the message must name the explicit implementation");
        }

        [Test]
        public void IntermediateHidingInit_ReportsError() {
            const string source = @"
using KenseiECS;
public struct A : IComponent { }
public struct B : IComponent { }
public abstract class SystemBase : IInitSystem, IRunSystem {
    public virtual void Init(World world, SharedData shared) { }
    public void Run(World world) { }
}
public class Hider : SystemBase {
    public new virtual void Init(World world, SharedData shared) { }
}
public partial class Derived : Hider {
    [Pool] private ComponentPool<A> _a;
}";
            var diagnostic = AssertReportedOnUserCode(source, "KECS006", "Derived");
            Assert.That(diagnostic.GetMessage(), Does.Contain("Hider"), "the message must name the class that hides Init");
        }

        // A base compiled into another assembly by 2.1 or without generated subclasses has a
        // non-virtual generated Init: nothing to override, so the derived class is reported.
        [Test]
        public void DerivedFromGeneratedBaseInOtherAssembly_ReportsError() {
            const string baseSource = @"
using KenseiECS;
namespace Lib {
    public struct A : IComponent { }
    public partial class BaseSystem : IRunSystem {
        [Pool] private ComponentPool<A> _a;
        public void Run(World world) { }
    }
}";
            var (baseCompilation, baseDiagnostics, baseGenerated) = RunGenerator(baseSource);
            Assert.That(baseDiagnostics.Errors, Is.Empty, baseGenerated);
            var baseReference = EmitReference(baseCompilation, "Lib");

            const string source = @"
using KenseiECS;
public struct B : IComponent { }
public partial class Derived : Lib.BaseSystem {
    [Pool] private ComponentPool<B> _b;
}";
            var (compilation, diagnostics, generated) = RunGenerator(source, new SystemInjectionGenerator(), extraReferences: new[] { baseReference });
            var reported = diagnostics.Errors.Where(d => d.Id == "KECS006").ToList();
            Assert.That(reported, Is.Not.Empty, "a non-virtual base Init from another assembly must be reported: " + generated);
            Assert.That(reported[0].GetMessage(), Does.Contain("not virtual").And.Contain("another assembly"), "the message must say why");
        }

        // A base in another assembly whose generated Init is virtual (it had generated
        // subclasses there) can be extended like a hand-written virtual Init.
        [Test]
        public void DerivedFromVirtualGeneratedBaseInOtherAssembly_IsSupported() {
            const string baseSource = @"
using KenseiECS;
namespace Lib {
    public struct A : IComponent { }
    public partial class BaseSystem : IRunSystem {
        [Pool] private ComponentPool<A> _a;
        public bool HasPool => _a != null;
        public void Run(World world) { }
    }
    public partial class LocalDerived : BaseSystem {
        [Pool] private ComponentPool<A> _again;
    }
}";
            var (baseCompilation, baseDiagnostics, baseGenerated) = RunGenerator(baseSource);
            Assert.That(baseDiagnostics.Errors, Is.Empty, baseGenerated);
            using var baseStream = new MemoryStream();
            Assert.That(baseCompilation.WithAssemblyName("Lib").Emit(baseStream).Success, Is.True);
            var baseImage = baseStream.ToArray();

            const string source = @"
using KenseiECS;
public struct B : IComponent { }
public partial class Derived : Lib.BaseSystem {
    [Pool] private ComponentPool<B> _b;
    public bool HasOwnPool => _b != null;
}";
            var (compilation, diagnostics, generated) = RunGenerator(source, new SystemInjectionGenerator(),
                extraReferences: new[] { MetadataReference.CreateFromImage(baseImage) });
            Assert.That(diagnostics.Errors, Is.Empty, "a virtual base Init from another assembly must be chained: " + generated);
            var context = new AssemblyLoadContext("CrossAssembly" + Guid.NewGuid());
            context.LoadFromStream(new MemoryStream(baseImage));
            using var stream = new MemoryStream();
            Assert.That(compilation.Emit(stream).Success, Is.True, "generated code must compile");
            stream.Position = 0;
            var assembly = context.LoadFromStream(stream);
            var derivedType = assembly.GetType("Derived")!;
            var system = (ISystem)Activator.CreateInstance(derivedType)!;
            new SystemsRunner(new World()).Add(system).Init();
            Assert.That(derivedType.GetProperty("HasPool")!.GetValue(system), Is.True, "the base fields must be injected through base.Init");
            Assert.That(derivedType.GetProperty("HasOwnPool")!.GetValue(system), Is.True, "the derived fields must be injected");
        }

        [Test]
        public void BaseWithFailedGeneration_DoesNotCascade() {
            const string source = @"
using KenseiECS;
public struct A : IComponent { }
public class BaseSystem : IRunSystem {
    [Pool] private ComponentPool<A> _a;
    public void Run(World world) { }
}
public partial class Derived : BaseSystem {
    [Pool] private ComponentPool<A> _b;
}";
            var (_, diagnostics, _) = RunGenerator(source);
            var ids = diagnostics.Errors.Select(d => d.Id).ToList();
            Assert.That(ids, Does.Contain("KECS001"), "the non-partial base must be reported");
            Assert.That(ids, Does.Not.Contain("KECS006"), "the derived class must not get a second error for the base's mistake");
        }

        [Test]
        public void StandaloneSystem_KeepsNonVirtualInit() {
            var (_, diagnostics, generated) = RunGenerator(Snippet);
            Assert.That(diagnostics.Errors, Is.Empty, generated);
            Assert.That(generated, Does.Contain("public void Init(global::KenseiECS.World world, global::KenseiECS.SharedData shared)"), "a system without generated subclasses keeps the 2.1 Init");
            Assert.That(generated, Does.Not.Contain("virtual").And.Not.Contain("override").And.Not.Contain("base.Init"), "nothing changes for systems without inheritance");
        }

        // Compiled on 2.1: OnInit chained to the base's generated Init by hand. Since 2.2 the
        // generated override already calls base.Init first, so the hand-written call would run
        // the base Init a second time: KECS012 on the call.
        [Test]
        public void DerivedFromGeneratedSystem_CallingBaseInit_ReportsKECS012() {
            const string source = @"
using KenseiECS;
public struct A : IComponent { }
public struct B : IComponent { }
public partial class BaseSystem : IRunSystem {
    [Pool] private ComponentPool<A> _a;
    public virtual void Run(World world) { }
}
public partial class Derived : BaseSystem {
    [Inc(typeof(B))] private Filter _f;
    partial void OnInit(World world, SharedData shared) => base.Init(world, shared);
}";
            var diagnostic = AssertReportedOnUserCode(source, "KECS012", "base.Init");
            Assert.That(diagnostic.GetMessage(), Does.Contain("Derived").And.Contain("already calls base.Init first").And.Contain("remove the call"),
                "the message must say why and what to do");
        }

        // The 2.1 layout over a hand-written public virtual base (IInitSystem redeclared, base.Init
        // from OnInit) is no longer kept: the generated Init overrides and calls the base itself.
        [Test]
        public void DerivedRedeclaringInitSystem_CallingBaseInit_ReportsKECS012() {
            const string source = @"
using KenseiECS;
public struct A : IComponent { }
public abstract class SystemBase : IInitSystem, IRunSystem {
    public virtual void Init(World world, SharedData shared) { }
    public abstract void Run(World world);
}
public partial class Derived : SystemBase, IInitSystem {
    [Pool] private ComponentPool<A> _a;
    partial void OnInit(World world, SharedData shared) { base.Init(world, shared); }
    public override void Run(World world) { }
}";
            AssertReportedOnUserCode(source, "KECS012", "base.Init");
        }

        // Without the call, a class that redeclares IInitSystem over a virtual base is chained like
        // any other: the base Init runs once, before the derived fields.
        [Test]
        public void DerivedRedeclaringInitSystem_WithoutBaseInitCall_Overrides() {
            const string source = @"
using KenseiECS;
using System.Collections.Generic;
public struct A : IComponent { }
public abstract class SystemBase : IInitSystem, IRunSystem {
    public List<string> Log = new List<string>();
    public virtual void Init(World world, SharedData shared) { Log.Add(""base""); }
    public abstract void Run(World world);
}
public partial class Derived : SystemBase, IInitSystem {
    [Pool] private ComponentPool<A> _a;
    partial void OnInit(World world, SharedData shared) { Log.Add(""derived:"" + (_a != null)); }
    public override void Run(World world) { }
}";
            var (compilation, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.Errors, Is.Empty, generated);
            Assert.That(generated, Does.Contain("public override void Init(").And.Contain("base.Init(world, shared);"), generated);
            AssertGeneratedCompilesCleanly(compilation, generated);
            var assembly = Emit(compilation);
            var derivedType = assembly.GetType("Derived")!;
            var system = (ISystem)Activator.CreateInstance(derivedType)!;
            new SystemsRunner(new World()).Add(system).Init();
            Assert.That(Log(derivedType, system), Is.EqualTo(new[] { "base", "derived:True" }), "the base Init must run once, first");
        }

        // KECS006 on 2.1 (the interface stayed with the base Init); 2.2-rc chained it and skipped the
        // generated call. Now the generated override always calls the base, so the call is KECS012.
        [Test]
        public void DerivedFromInitSystemBase_CallingBaseInitWithoutRedeclaring_ReportsKECS012() {
            const string source = @"
using KenseiECS;
public struct A : IComponent { }
public abstract class SystemBase : IInitSystem, IRunSystem {
    public virtual void Init(World world, SharedData shared) { }
    public abstract void Run(World world);
}
public partial class Derived : SystemBase {
    [Pool] private ComponentPool<A> _a;
    partial void OnInit(World world, SharedData shared) => base.Init(world, shared);
    public override void Run(World world) { }
}";
            AssertReportedOnUserCode(source, "KECS012", "base.Init");
        }

        // Recognition is syntactic: every `base.Init` member access in the class's own parts counts,
        // whatever overload it binds to and however it is written.
        [TestCase("partial void OnInit(World world, SharedData shared) { base.Init(shared: shared, world: world); }")]
        [TestCase("partial void OnInit(World world, SharedData shared) { System.Action<World, SharedData> init = base.Init; init(world, shared); }")]
        [TestCase("partial void OnInit(World world, SharedData shared) { System.Action run = () => base.Init(world, shared); run(); }")]
        [TestCase("public void Restart(World world, SharedData shared) { base.Init(world, shared); }")]
        [TestCase("public void Helper() { base.Init(1, 2); }")]
        public void BaseInitInDerivedGeneratedSystem_AnyForm_ReportsKECS012(string member) {
            string source = @"
using KenseiECS;
public struct A : IComponent { }
public struct B : IComponent { }
public abstract class Root : IRunSystem {
    protected void Init(int a, int b) { }
    public void Run(World world) { }
}
public partial class G1 : Root {
    [Pool] private ComponentPool<A> _a;
}
public partial class G2 : G1 {
    [Pool] private ComponentPool<B> _b;
}
public partial class G2 {
    " + member + @"
}";
            AssertReportedOnUserCode(source, "KECS012", "base.Init");
        }

        // An abstract base Init is not called by the generated override, so KECS012 does not apply
        // there (a base.Init on an abstract method is the compiler's CS0205).
        [Test]
        public void DerivedFromAbstractInit_BaseInitCall_IsNotKECS012() {
            const string source = @"
using KenseiECS;
public struct A : IComponent { }
public abstract class SystemBase : IInitSystem, IRunSystem {
    public abstract void Init(World world, SharedData shared);
    public void Run(World world) { }
}
public partial class Derived : SystemBase {
    [Pool] private ComponentPool<A> _a;
    partial void OnInit(World world, SharedData shared) => base.Init(world, shared);
}";
            var (_, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.All.Select(d => d.Id), Has.None.EqualTo("KECS012"), generated);
        }

        // Over a hand-written base Init that cannot be overridden (not virtual), the 2.1 layout is
        // still the way: the class lists IInitSystem itself and OnInit calls base.Init.
        [Test]
        public void DerivedRedeclaringInitSystem_OverNonVirtualBase_KeepsTheLegacyLayout() {
            const string source = @"
using KenseiECS;
using System.Collections.Generic;
public struct A : IComponent { }
public abstract class SystemBase : IInitSystem, IRunSystem {
    public List<string> Log = new List<string>();
    public void Init(World world, SharedData shared) { Log.Add(""base""); }
    public abstract void Run(World world);
}
public partial class Derived : SystemBase, IInitSystem {
    [Pool] private ComponentPool<A> _a;
    partial void OnInit(World world, SharedData shared) { base.Init(world, shared); Log.Add(""derived:"" + (_a != null)); }
    public override void Run(World world) { }
}";
            var (compilation, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.Errors, Is.Empty, generated);
            Assert.That(generated, Does.Contain("public new void Init(").And.Not.Contain("base.Init"), generated);
            AssertGeneratedCompilesCleanly(compilation, generated);
            var assembly = Emit(compilation);
            var derivedType = assembly.GetType("Derived")!;
            var system = (ISystem)Activator.CreateInstance(derivedType)!;
            new SystemsRunner(new World()).Add(system).Init();
            Assert.That(Log(derivedType, system), Is.EqualTo(new[] { "base", "derived:True" }), "the re-implemented Init runs, OnInit calls the base once");
        }
        // A base.Init inside a nested type belongs to that type: no KECS012 for the outer class, and
        // the generated override still calls the base Init (2.2.0-rc dropped that call).
        [Test]
        public void BaseInitCallInNestedType_DoesNotCountForOuterClass_HandWrittenBase() {
            const string source = @"
using KenseiECS;
using System.Collections.Generic;
public struct A : IComponent { }
public abstract class SystemBase : IInitSystem, IRunSystem {
    public List<string> Log = new List<string>();
    public virtual void Init(World world, SharedData shared) { Log.Add(""base""); }
    public void Run(World world) { }
}
public partial class Derived : SystemBase {
    [Pool] private ComponentPool<A> _a;
    partial void OnInit(World world, SharedData shared) { Log.Add(""derived:"" + (_a != null)); }
    private class Helper : SystemBase {
        public override void Init(World world, SharedData shared) { base.Init(world, shared); }
    }
}";
            var (compilation, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.Errors, Is.Empty, generated);
            Assert.That(generated, Does.Contain("base.Init(world, shared)"), "the outer class does not call base.Init itself, so the generated override must");
            AssertGeneratedCompilesCleanly(compilation, generated);
            var assembly = Emit(compilation);
            var derivedType = assembly.GetType("Derived")!;
            var system = (ISystem)Activator.CreateInstance(derivedType)!;
            new SystemsRunner(new World()).Add(system).Init();
            Assert.That(Log(derivedType, system), Is.EqualTo(new[] { "base", "derived:True" }), "the base Init must run, then the derived OnInit");
        }

        [Test]
        public void BaseInitCallInNestedType_DoesNotCountForOuterClass_GeneratedBase() {
            const string source = @"
using KenseiECS;
using System.Collections.Generic;
public struct A : IComponent { }
public struct B : IComponent { }
public partial class BaseSystem : IRunSystem {
    public List<string> Log = new List<string>();
    [Pool] private ComponentPool<A> _a;
    partial void OnInit(World world, SharedData shared) { Log.Add(""base:"" + (_a != null)); }
    public void Run(World world) { }
}
public partial class Derived : BaseSystem {
    [Pool] private ComponentPool<B> _b;
    partial void OnInit(World world, SharedData shared) { Log.Add(""derived:"" + (_b != null)); }
    private class Helper : BaseSystem {
        public override void Init(World world, SharedData shared) { base.Init(world, shared); }
    }
}";
            var (compilation, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.Errors, Is.Empty, generated);
            AssertGeneratedCompilesCleanly(compilation, generated);
            var assembly = Emit(compilation);
            var derivedType = assembly.GetType("Derived")!;
            var system = (ISystem)Activator.CreateInstance(derivedType)!;
            new SystemsRunner(new World()).Add(system).Init();
            Assert.That(Log(derivedType, system), Is.EqualTo(new[] { "base:True", "derived:True" }), "the generated base Init must be chained, not skipped");
        }

        // Round-3 regression: the generated base.Init(world, shared) is bound by overload resolution,
        // which prefers an applicable Init declared on a more derived hand-written class over the
        // chained one. That would silently skip the base Init, so it is reported (as 2.1 did).
        [TestCase("protected void Init(World world, object context) { }")]
        [TestCase("protected void Init(World world, SharedData shared, int x = 0) { }")]
        [TestCase("protected void Init<T>(World world, T context) { }")]
        [TestCase("protected void Init(params object[] args) { }")]
        [TestCase("public new void Init(World world, object context) { }")]
        [TestCase("protected static void Init(World world, object context) { }")]
        public void IntermediateInitOverload_BetweenGeneratedClasses_ReportsError(string midOverload) {
            string source = @"
using KenseiECS;
public struct A : IComponent { }
public struct B : IComponent { }
public abstract class Root : IRunSystem {
    public void Run(World world) { }
}
public partial class G1 : Root {
    [Pool] private ComponentPool<A> _a;
}
public abstract class Mid : G1 {
    " + midOverload + @"
}
public partial class G2 : Mid {
    [Pool] private ComponentPool<B> _b;
}";
            var diagnostic = AssertReportedOnUserCode(source, "KECS006", "G2");
            Assert.That(diagnostic.GetMessage(), Does.Contain("Mid"), "the message must name the class with the overload");
        }

        [Test]
        public void IntermediateInitOverload_OverHandWrittenVirtualRoot_ReportsError() {
            const string source = @"
using KenseiECS;
public struct B : IComponent { }
public abstract class Root : IInitSystem, IRunSystem {
    public virtual void Init(World world, SharedData shared) { }
    public void Run(World world) { }
}
public abstract class Mid : Root {
    protected void Init(World world, object context) { }
}
public partial class G : Mid {
    [Pool] private ComponentPool<B> _b;
}";
            var diagnostic = AssertReportedOnUserCode(source, "KECS006", "G");
            Assert.That(diagnostic.GetMessage(), Does.Contain("Mid"), "the message must name the class with the overload");
        }

        // Overloads that cannot take (World, SharedData) or are private do not capture the call and
        // stay allowed. (A static one does capture it: base access then fails with CS0176.)
        [TestCase("protected void Init(int a, int b) { Log.Add(\"other\"); }")]
        [TestCase("protected void Init(World world) { Log.Add(\"other\"); }")]
        [TestCase("private void Init(World world, object context) { Log.Add(\"other\"); }")]
        [TestCase("protected void Init(ref World world, object context) { Log.Add(\"other\"); }")]
        public void IntermediateInitOverload_NotApplicable_StillChains(string midOverload) {
            string source = @"
using KenseiECS;
using System.Collections.Generic;
public struct A : IComponent { }
public struct B : IComponent { }
public abstract class Root : IRunSystem {
    public List<string> Log = new List<string>();
    public void Run(World world) { }
}
public partial class G1 : Root {
    [Pool] private ComponentPool<A> _a;
    partial void OnInit(World world, SharedData shared) { Log.Add(""g1""); }
}
public abstract class Mid : G1 {
    " + midOverload + @"
}
public partial class G2 : Mid {
    [Pool] private ComponentPool<B> _b;
    partial void OnInit(World world, SharedData shared) { Log.Add(""g2""); }
}";
            var (compilation, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.Errors, Is.Empty, generated);
            AssertGeneratedCompilesCleanly(compilation, generated);
            var assembly = Emit(compilation);
            var type = assembly.GetType("G2")!;
            var system = (ISystem)Activator.CreateInstance(type)!;
            new SystemsRunner(new World()).Add(system).Init();
            Assert.That(Log(type, system), Is.EqualTo(new[] { "g1", "g2" }), "the generated override must chain to G1.Init");
        }

        // An override on the intermediate is looked up at its virtual declaration on the root, below
        // G1 in the hierarchy, so G1.Init still takes the generated base call.
        [Test]
        public void IntermediateOverrideOfRootOverload_StillChains() {
            const string source = @"
using KenseiECS;
using System.Collections.Generic;
public struct A : IComponent { }
public struct B : IComponent { }
public abstract class Root : IRunSystem {
    public List<string> Log = new List<string>();
    protected virtual void Init(World world, object context) { Log.Add(""root-helper""); }
    public void Run(World world) { }
}
public partial class G1 : Root {
    [Pool] private ComponentPool<A> _a;
    partial void OnInit(World world, SharedData shared) { Log.Add(""g1""); }
}
public abstract class Mid : G1 {
    protected override void Init(World world, object context) { Log.Add(""mid-helper""); }
}
public partial class G2 : Mid {
    [Pool] private ComponentPool<B> _b;
    partial void OnInit(World world, SharedData shared) { Log.Add(""g2""); }
}";
            var (compilation, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.Errors, Is.Empty, generated);
            AssertGeneratedCompilesCleanly(compilation, generated);
            var assembly = Emit(compilation);
            var type = assembly.GetType("G2")!;
            var system = (ISystem)Activator.CreateInstance(type)!;
            new SystemsRunner(new World()).Add(system).Init();
            Assert.That(Log(type, system), Is.EqualTo(new[] { "g1", "g2" }), "the generated override must chain to G1.Init");
        }

        [Test]
        public void DerivedFromInitSystemBase_NonVirtual_CallingBaseInitWithoutRedeclaring_ReportsError() {
            // Without IInitSystem in the derived base list the interface stays mapped to the
            // non-virtual base Init, so the generated Init would never run.
            const string source = @"
using KenseiECS;
public struct A : IComponent { }
public abstract class SystemBase : IInitSystem, IRunSystem {
    public void Init(World world, SharedData shared) { }
    public abstract void Run(World world);
}
public partial class Derived : SystemBase {
    [Pool] private ComponentPool<A> _a;
    partial void OnInit(World world, SharedData shared) => base.Init(world, shared);
    public override void Run(World world) { }
}";
            AssertReportedOnUserCode(source, "KECS006", "Derived");
        }

        // Two subclasses of one generated base: the one that still calls base.Init gets KECS012,
        // the other one is chained without errors.
        [Test]
        public void LegacySibling_ReportsKECS012_ChainedSiblingIsClean() {
            const string source = @"
using KenseiECS;
public struct A : IComponent { }
public struct B : IComponent { }
public partial class BaseSystem : IRunSystem {
    [Pool] private ComponentPool<A> _a;
    public void Run(World world) { }
}
public partial class Legacy : BaseSystem {
    [Pool] private ComponentPool<B> _b;
    partial void OnInit(World world, SharedData shared) => base.Init(world, shared);
}
public partial class Chained : BaseSystem {
    [Pool] private ComponentPool<B> _b;
}";
            var (_, diagnostics, generated) = RunGenerator(source);
            var reported = diagnostics.Errors.ToList();
            Assert.That(reported.Select(d => d.Id), Is.EqualTo(new[] { "KECS012" }), generated);
            Assert.That(reported[0].GetMessage(), Does.Contain("'Legacy'"), "only the class that calls base.Init is reported");
        }

        // A component produced by another source generator is an error type for this
        // one; whether it exists is the compiler's call, as on 2.1.0.
        [Test]
        public void UnresolvedFilterType_IsLeftToTheCompiler() {
            const string source = @"
using KenseiECS;
public partial class FromOtherGenerator : IRunSystem {
    [Inc(new System.Type[] { typeof(GeneratedComponent) })] private Filter _f;
    [Shared] private GeneratedConfig _config;
    public void Run(World world) { }
}";
            var (_, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.All.Select(d => d.Id), Has.None.StartsWith("KECS"), "an unresolved type must not be reported by the generator: " + string.Join("; ", diagnostics.All));
            Assert.That(generated, Does.Contain(".Inc<GeneratedComponent>()"), "the filter must still be generated");
            Assert.That(generated, Does.Contain("shared.Get<GeneratedConfig>()"), "the shared lookup must still be generated");
        }

        [Test]
        public void DerivedWithoutInjectedFields_InheritsGeneratedInit() {
            const string source = @"
using KenseiECS;
public struct A : IComponent { }
public partial class BaseSystem : IRunSystem {
    [Pool] private ComponentPool<A> _a;
    public bool HasPool => _a != null;
    public virtual void Run(World world) { }
}
public class Derived : BaseSystem { }";
            var (compilation, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.Errors, Is.Empty, "a derived class without injected fields must not be reported: " + generated);
            var assembly = Emit(compilation);
            var derivedType = assembly.GetType("Derived")!;
            var system = (ISystem)Activator.CreateInstance(derivedType)!;
            new SystemsRunner(new World()).Add(system).Init();
            Assert.That(derivedType.GetProperty("HasPool")!.GetValue(system), Is.True, "the base's generated Init must inject the base's fields");
        }

        [Test]
        public void ExplicitInterfaceInit_ReportsError() {
            const string source = @"
using KenseiECS;
public struct P : IComponent { }
public partial class Broken : IInitSystem {
    [Pool] private ComponentPool<P> _p;
    void IInitSystem.Init(World world, SharedData shared) { }
}";
            AssertReportedOnUserCode(source, "KECS002", "Init");
        }

        [Test]
        public void NullableAnnotatedFields_AreInjected() {
            const string source = @"
using KenseiECS;
namespace Demo {
    public struct P : IComponent { }
    public class Config { }
    public partial class NullableSystem : IRunSystem {
        [Inc(typeof(P))] private Filter? _f;
        [Pool] private ComponentPool<P>? _p;
        [Shared] private Config? _config;
        public bool AllInjected => _f != null && _p != null && _config != null;
        public void Run(World world) { }
    }
}";
            var (compilation, diagnostics, generated) = RunGenerator(source, new SystemInjectionGenerator(), nullable: true);
            Assert.That(diagnostics.All.Where(d => d.Id.StartsWith("KECS")), Is.Empty, "nullable-annotated fields must not be reported: " + generated);
            var userTree = compilation.SyntaxTrees.First();
            var generatedWarnings = compilation.GetDiagnostics()
                .Where(d => d.Severity >= DiagnosticSeverity.Warning && d.Location.SourceTree != null && d.Location.SourceTree != userTree);
            Assert.That(generatedWarnings, Is.Empty, "generated code must compile cleanly under #nullable enable: " + generated);
            var assembly = Emit(compilation);
            var systemType = assembly.GetType("Demo.NullableSystem")!;
            var system = (ISystem)Activator.CreateInstance(systemType)!;
            var shared = new SharedData();
            AddShared(shared, assembly.GetType("Demo.Config")!, Activator.CreateInstance(assembly.GetType("Demo.Config")!)!, null);
            new SystemsRunner(new World(), shared).Add(system).Init();
            Assert.That(systemType.GetProperty("AllInjected")!.GetValue(system), Is.True, "Filter?, ComponentPool<T>? and shared T? fields must be injected");
        }

        [Test]
        public void GenericSystem_IsGenerated() {
            const string source = @"
using KenseiECS;
namespace Demo {
    public struct P : IComponent { }
    public partial class GenericSystem<T> : IRunSystem where T : struct, IComponent {
        [Inc(typeof(P))] private Filter _f;
        [Pool] private ComponentPool<T> _pool;
        public bool AllInjected => _f != null && _pool != null;
        public void Run(World world) { }
    }
}";
            var (compilation, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.Errors, Is.Empty, "generic system must not be reported: " + generated);
            Assert.That(generated, Does.Contain("partial class GenericSystem<T> : global::KenseiECS.IInitSystem"), "generated partial must repeat the type parameters");
            Assert.That(generated, Does.Contain("world.Pool<T>()"), "a pool of the type parameter must be fetched by the parameter");
            var assembly = Emit(compilation);
            var systemType = assembly.GetType("Demo.GenericSystem`1")!.MakeGenericType(assembly.GetType("Demo.P")!);
            var system = (ISystem)Activator.CreateInstance(systemType)!;
            new SystemsRunner(new World()).Add(system).Init();
            Assert.That(systemType.GetProperty("AllInjected")!.GetValue(system), Is.True, "closed generic system must get its fields injected");
        }

        [Test]
        public void EmptyInc_ReportsError() {
            const string source = @"
using KenseiECS;
public struct P : IComponent { }
public partial class Broken : IRunSystem {
    [Inc] private Filter _f;
    public void Run(World world) { }
}";
            AssertReportedOnUserCode(source, "KECS007", "_f");
        }

        [Test]
        public void EmptyIncWithAny_IsAccepted() {
            const string source = @"
using KenseiECS;
public struct P : IComponent { }
public partial class AnyOnly : IRunSystem {
    [Inc] [Any(typeof(P))] private Filter _f;
    public void Run(World world) { }
}";
            var (compilation, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.Errors, Is.Empty, "[Any] with types makes the filter valid even with an empty [Inc]: " + generated);
            Emit(compilation);
        }

        [Test]
        public void NonComponentFilterType_ReportsError() {
            const string source = @"
using KenseiECS;
public class NotAComponent { }
public partial class Broken : IRunSystem {
    [Inc(typeof(NotAComponent))] private Filter _f;
    public void Run(World world) { }
}";
            AssertReportedOnUserCode(source, "KECS008", "_f");
        }

        [Test]
        public void StructWithoutIComponentInExc_ReportsError() {
            const string source = @"
using KenseiECS;
public struct P : IComponent { }
public struct Plain { }
public partial class Broken : IRunSystem {
    [Inc(typeof(P))] [Exc(typeof(Plain))] private Filter _f;
    public void Run(World world) { }
}";
            AssertReportedOnUserCode(source, "KECS008", "_f");
        }

        [Test]
        public void SharedOnValueType_ReportsError() {
            const string source = @"
using KenseiECS;
public struct Settings { public int Value; }
public partial class Broken : IRunSystem {
    [Shared] private Settings _settings;
    public void Run(World world) { }
}";
            AssertReportedOnUserCode(source, "KECS003", "_settings");
        }

        [Test]
        public void SharedOnInt_ReportsError() {
            const string source = @"
using KenseiECS;
public partial class Broken : IRunSystem {
    [Shared(""count"")] private int _count;
    public void Run(World world) { }
}";
            AssertReportedOnUserCode(source, "KECS003", "_count");
        }

        [Test]
        public void ReadonlyInjectedField_ReportsError() {
            const string source = @"
using KenseiECS;
public struct P : IComponent { }
public partial class Broken : IRunSystem {
    [Pool] private readonly ComponentPool<P> _p;
    public void Run(World world) { }
}";
            AssertReportedOnUserCode(source, "KECS009", "_p");
        }

        [Test]
        public void RecordSystem_ReportsError() {
            const string source = @"
using KenseiECS;
public struct P : IComponent { }
public partial record Broken : IRunSystem {
    [Pool] private ComponentPool<P> _p;
    public void Run(World world) { }
}";
            AssertReportedOnUserCode(source, "KECS010", "Broken");
        }

        [Test]
        public void StructSystem_ReportsError() {
            const string source = @"
using KenseiECS;
public struct P : IComponent { }
public partial struct Broken : IRunSystem {
    [Pool] private ComponentPool<P> _p;
    public void Run(World world) { }
}";
            AssertReportedOnUserCode(source, "KECS010", "Broken");
        }

        [Test]
        public void SystemNestedInRecord_ReportsError() {
            const string source = @"
using KenseiECS;
public struct P : IComponent { }
public partial record Outer {
    public partial class Nested : IRunSystem {
        [Pool] private ComponentPool<P> _p;
        public void Run(World world) { }
    }
}";
            AssertReportedOnUserCode(source, "KECS011", "Nested");
        }

        // The error must point at the user's declaration, and the generator must not
        // emit code that fails to compile on its own.
        private static Diagnostic AssertReportedOnUserCode(string source, string id, string locationText) {
            var (compilation, diagnostics, generated) = RunGenerator(source);
            var userTree = compilation.SyntaxTrees.First();
            var reported = diagnostics.Errors.Where(d => d.Id == id).ToList();
            Assert.That(reported, Is.Not.Empty, $"{id} must be reported; got: " + string.Join("; ", diagnostics.All) + "\n" + generated);
            var location = reported[0].Location;
            Assert.That(location.SourceTree, Is.SameAs(userTree), $"{id} must point at the user's source");
            Assert.That(userTree.GetText().ToString(location.SourceSpan), Is.EqualTo(locationText), $"{id} must point at the offending declaration");
            var generatedErrors = compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error && d.Location.SourceTree != null && d.Location.SourceTree != userTree);
            Assert.That(generatedErrors, Is.Empty, "generated code must not contain compile errors");
            return reported[0];
        }

        private static void AddShared(SharedData shared, Type type, object instance, string key) {
            var add = typeof(SharedData).GetMethod(nameof(SharedData.Add))!.MakeGenericMethod(type);
            add.Invoke(shared, new[] { instance, key });
        }

        private static Entity CreateEntity(World world, Type componentType, float x) {
            var value = Activator.CreateInstance(componentType)!;
            componentType.GetField("X")!.SetValue(value, x);
            var create = typeof(World).GetMethod(nameof(World.CreateEntity))!.MakeGenericMethod(componentType);
            return (Entity)create.Invoke(world, new[] { value })!;
        }

        private static void AddComponent(World world, Entity entity, Type componentType, float x) {
            var value = Activator.CreateInstance(componentType)!;
            componentType.GetField("X")!.SetValue(value, x);
            var add = typeof(World).GetMethod(nameof(World.Add))!.MakeGenericMethod(componentType);
            add.Invoke(world, new[] { entity, value });
        }

        private static float GetX(World world, Entity entity, Type componentType) {
            var get = typeof(World).GetMethod(nameof(World.Get))!.MakeGenericMethod(componentType);
            var value = get.Invoke(world, new object[] { entity })!;
            return (float)componentType.GetField("X")!.GetValue(value)!;
        }

        public readonly struct ImmutableArrayWrapper {
            public readonly IReadOnlyList<Diagnostic> All;

            public ImmutableArrayWrapper(System.Collections.Immutable.ImmutableArray<Diagnostic> diagnostics) {
                All = diagnostics;
            }

            public IEnumerable<Diagnostic> Errors => All.Where(d => d.Severity == DiagnosticSeverity.Error);
        }
    }
}
