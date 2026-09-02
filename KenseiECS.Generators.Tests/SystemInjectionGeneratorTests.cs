using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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

        private static (Compilation compilation, ImmutableArrayWrapper diagnostics, string generated) RunGenerator(string source) {
            var references = new List<MetadataReference> {
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
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var driver = CSharpGeneratorDriver.Create(new ISourceGenerator[] { new SystemInjectionGenerator() }, parseOptions: parseOptions);
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
