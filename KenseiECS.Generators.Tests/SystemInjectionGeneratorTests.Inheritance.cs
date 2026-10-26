using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using KenseiECS.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;

namespace KenseiECS.Generators.Tests {
    // Inheritance: which Init the generated one chains to, in what order, and what is reported.
    public partial class SystemInjectionGeneratorTests {
        // Compiles a referenced assembly "Lib", through the generator unless told otherwise.
        private static byte[] CompileLibrary(string source, bool generate = true, params string[] defines) {
            Compilation compilation;
            if (generate) {
                var (output, diagnostics, generated) = RunGenerator(source, new SystemInjectionGenerator(), defines: defines);
                Assert.That(diagnostics.Errors, Is.Empty, "the library must generate: " + generated);
                compilation = output;
            } else {
                compilation = CreateCompilation(new[] { source }, new CSharpParseOptions(LanguageVersion.CSharp9, preprocessorSymbols: defines));
            }
            using var stream = new MemoryStream();
            var result = compilation.WithAssemblyName("Lib").Emit(stream);
            Assert.That(result.Success, Is.True, "the library must compile: " + string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
            return stream.ToArray();
        }

        private static (Compilation compilation, ImmutableArrayWrapper diagnostics, string generated) RunGeneratorWithLibrary(string source, byte[] library) {
            return RunGenerator(source, new SystemInjectionGenerator(), extraReferences: new[] { MetadataReference.CreateFromImage(library) });
        }

        // Loads the library and the compiled code into one context, so the types resolve.
        private static Assembly EmitWithLibrary(Compilation compilation, byte[] library) {
            var context = new AssemblyLoadContext("WithLibrary" + Guid.NewGuid());
            context.LoadFromStream(new MemoryStream(library));
            using var stream = new MemoryStream();
            var result = compilation.Emit(stream);
            Assert.That(result.Success, Is.True,
                "generated code must compile: " + string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
            stream.Position = 0;
            return context.LoadFromStream(stream);
        }

        private static (Type type, ISystem system) InitSystem(Assembly assembly, string typeName) {
            var type = assembly.GetType(typeName)!;
            var system = (ISystem)Activator.CreateInstance(type)!;
            new SystemsRunner(new World()).Add(system).Init();
            return (type, system);
        }

        private static object Get(Type type, ISystem system, string property) {
            return type.GetProperty(property)!.GetValue(system);
        }

        // ---- Init form does not depend on the subclasses of the compilation (G-1)

        private const string LibWithEditorOnlySubclass = @"
using KenseiECS;
using System.Collections.Generic;
namespace Lib {
    public struct A : IComponent { }
    public partial class BaseSystem : IRunSystem {
        [Pool] private ComponentPool<A> _a;
        public List<string> Log = new List<string>();
        public bool HasPool => _a != null;
        partial void OnInit(World world, SharedData shared) { Log.Add(""base""); }
        public virtual void Run(World world) { }
    }
#if UNITY_EDITOR
    public partial class EditorDebugSystem : BaseSystem {
        [Pool] private ComponentPool<A> _debug;
    }
#endif
}";

        // On 2.2.1 the base Init was virtual only in the compilation with the subclass (the Editor),
        // so in a player the derived class silently kept the 2.1 layout (base fields null) or got
        // KECS006, while the Editor accepted it and reported the base.Init the player needed.
        [TestCase(true, true)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(false, false)]
        public void BaseInOtherAssembly_WithDefineDependentSubclass_ChainsTheSameWay(bool editor, bool listsInitSystem) {
            var library = CompileLibrary(LibWithEditorOnlySubclass, defines: editor ? new[] { "UNITY_EDITOR" } : Array.Empty<string>());
            string source = @"
using KenseiECS;
public struct B : IComponent { }
public partial class Derived : Lib.BaseSystem" + (listsInitSystem ? ", IInitSystem" : "") + @" {
    [Pool] private ComponentPool<B> _b;
    public bool HasOwnPool => _b != null;
    partial void OnInit(World world, SharedData shared) { Log.Add(""derived""); }
}";
            var (compilation, diagnostics, generated) = RunGeneratorWithLibrary(source, library);
            Assert.That(diagnostics.Errors, Is.Empty, generated);
            Assert.That(generated, Does.Contain("public override void Init(").And.Contain("base.Init(world, shared);"), "the derived Init must chain whatever the library's defines");
            AssertGeneratedCompilesCleanly(compilation, generated);
            var (type, system) = InitSystem(EmitWithLibrary(compilation, library), "Derived");
            Assert.That(Get(type, system, "HasPool"), Is.True, "the base fields must be injected");
            Assert.That(Get(type, system, "HasOwnPool"), Is.True);
            Assert.That(Log(type, system), Is.EqualTo(new[] { "base", "derived" }));
        }

        [Test]
        public void DerivedFromGeneratedBaseInOtherAssembly_WithoutSubclassesThere_Chains() {
            const string lib = @"
using KenseiECS;
using System.Collections.Generic;
namespace Lib {
    public struct A : IComponent { }
    public partial class BaseSystem : IRunSystem {
        [Pool] private ComponentPool<A> _a;
        public List<string> Log = new List<string>();
        public bool HasPool => _a != null;
        partial void OnInit(World world, SharedData shared) { Log.Add(""base""); }
        public void Run(World world) { }
    }
}";
            var library = CompileLibrary(lib);
            const string source = @"
using KenseiECS;
public struct B : IComponent { }
public partial class Derived : Lib.BaseSystem {
    [Pool] private ComponentPool<B> _b;
    partial void OnInit(World world, SharedData shared) { Log.Add(""derived:"" + (_b != null)); }
}";
            var (compilation, diagnostics, generated) = RunGeneratorWithLibrary(source, library);
            Assert.That(diagnostics.Errors, Is.Empty, "a generated base from another assembly must be chained: " + generated);
            var (type, system) = InitSystem(EmitWithLibrary(compilation, library), "Derived");
            Assert.That(Get(type, system, "HasPool"), Is.True);
            Assert.That(Log(type, system), Is.EqualTo(new[] { "base", "derived:True" }));
        }

        // ---- Order: derived fields, base Init, derived OnInit (G-2)

        // 2.2.0 and 2.2.1 called base.Init before filling the derived fields, so a virtual hook the
        // base calls from OnInit saw them null; on 2.1 (base.Init called from the derived OnInit)
        // they were already filled. The derived fields come first again.
        [Test]
        public void DerivedFields_AreFilledBeforeTheGeneratedBaseInit() {
            const string source = @"
using KenseiECS;
using System.Collections.Generic;
public struct A : IComponent { }
public struct B : IComponent { }
public partial class BaseSystem : IRunSystem {
    [Pool] protected ComponentPool<A> PoolA;
    public List<string> Log = new List<string>();
    partial void OnInit(World world, SharedData shared) { Log.Add(""base:"" + (PoolA != null)); Configure(); }
    protected virtual void Configure() { }
    public void Run(World world) { }
}
public partial class Derived : BaseSystem {
    [Inc(typeof(B))] private Filter _f;
    protected override void Configure() { Log.Add(""configure:"" + (_f != null)); }
    partial void OnInit(World world, SharedData shared) { Log.Add(""derived:"" + (_f != null) + "","" + (PoolA != null)); }
}";
            var (compilation, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.Errors, Is.Empty, generated);
            AssertGeneratedCompilesCleanly(compilation, generated);
            string derived = compilation.SyntaxTrees.Single(t => t.FilePath.EndsWith("Derived.Injection.g.cs")).ToString();
            int field = derived.IndexOf("this._f =", StringComparison.Ordinal);
            int baseCall = derived.IndexOf("base.Init(world, shared);", StringComparison.Ordinal);
            int onInit = derived.IndexOf("OnInit(world, shared);", baseCall + 1, StringComparison.Ordinal);
            Assert.That(field, Is.GreaterThan(0).And.LessThan(baseCall), "the derived fields come before base.Init: " + derived);
            Assert.That(onInit, Is.GreaterThan(baseCall), "the derived OnInit comes after base.Init: " + derived);

            var (type, system) = InitSystem(Emit(compilation), "Derived");
            Assert.That(Log(type, system), Is.EqualTo(new[] { "base:True", "configure:True", "derived:True,True" }),
                "the base OnInit and the hook it calls see the derived fields; the derived OnInit sees all fields");
        }

        [Test]
        public void DerivedFields_AreFilledBeforeAHandWrittenBaseInit() {
            const string source = @"
using KenseiECS;
using System.Collections.Generic;
public struct B : IComponent { }
public abstract class SystemBase : IInitSystem, IRunSystem {
    public List<string> Log = new List<string>();
    public virtual void Init(World world, SharedData shared) { Log.Add(""base""); Configure(world); }
    protected abstract void Configure(World world);
    public void Run(World world) { }
}
public partial class Derived : SystemBase {
    [Pool] private ComponentPool<B> _b;
    protected override void Configure(World world) { Log.Add(""configure:"" + (_b != null)); }
    partial void OnInit(World world, SharedData shared) { Log.Add(""derived""); }
}";
            var (compilation, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.Errors, Is.Empty, generated);
            var (type, system) = InitSystem(Emit(compilation), "Derived");
            Assert.That(Log(type, system), Is.EqualTo(new[] { "base", "configure:True", "derived" }));
        }

        // ---- The 2.1 layout must call base.Init (F4)

        // 2.1.1 reported a class that keeps the 2.1 layout (IInitSystem listed over a base Init that
        // cannot be overridden) without calling base.Init from OnInit; 2.2.0 dropped the check, and
        // the base Init with its injected fields was silently skipped.
        [Test]
        public void LegacyLayout_OverHidingIntermediate_WithoutBaseInit_ReportsKECS006() {
            const string source = @"
using KenseiECS;
public struct A : IComponent { }
public struct B : IComponent { }
public partial class G1 : IRunSystem {
    [Pool] protected ComponentPool<A> PoolA;
    public void Run(World world) { }
}
public class Mid : G1 {
    public new void Init(World world, SharedData shared) { base.Init(world, shared); }
}
public partial class G2 : Mid, IInitSystem {
    [Pool] private ComponentPool<B> _b;
}";
            var diagnostic = AssertReportedOnUserCode(source, "KECS006", "G2");
            Assert.That(diagnostic.GetMessage(), Does.Contain("'Mid.Init' is not virtual").And.Contain("lists IInitSystem itself").And.Contain("add that call"),
                "the message must say that OnInit has to call base.Init");
        }

        [Test]
        public void LegacyLayout_OverNonVirtualHandWrittenBase_WithoutBaseInit_ReportsKECS006() {
            const string source = @"
using KenseiECS;
public struct A : IComponent { }
public struct B : IComponent { }
public abstract class SystemBase : IInitSystem, IRunSystem {
    protected ComponentPool<A> PoolA;
    public void Init(World world, SharedData shared) { PoolA = world.Pool<A>(); }
    public abstract void Run(World world);
}
public partial class Derived : SystemBase, IInitSystem {
    [Pool] private ComponentPool<B> _b;
    public override void Run(World world) { }
}";
            AssertReportedOnUserCode(source, "KECS006", "Derived");
        }

        private const string LibOlderGeneratedBase = @"
using KenseiECS;
using System.Collections.Generic;
namespace Lib {
    public struct A : IComponent { }
    // As a 2.2.1 generator emitted it without subclasses: non-virtual Init.
    public partial class BaseSystem : IRunSystem, IInitSystem {
        [Pool] protected ComponentPool<A> PoolA;
        public List<string> Log = new List<string>();
        public void Init(World world, SharedData shared) { PoolA = world.Pool<A>(); Log.Add(""base""); }
        public void Run(World world) { }
    }
}";

        [Test]
        public void LegacyLayout_OverOlderGeneratedBaseInOtherAssembly_WithoutBaseInit_ReportsKECS006() {
            var library = CompileLibrary(LibOlderGeneratedBase, generate: false);
            const string source = @"
using KenseiECS;
public struct B : IComponent { }
public partial class Derived : Lib.BaseSystem, IInitSystem {
    [Pool] private ComponentPool<B> _b;
}";
            var (_, diagnostics, generated) = RunGeneratorWithLibrary(source, library);
            var reported = diagnostics.Errors.Where(d => d.Id == "KECS006").ToList();
            Assert.That(reported, Is.Not.Empty, "the skipped base Init must be reported: " + generated);
            Assert.That(reported[0].GetMessage(), Does.Contain("base.Init(world, shared)"));
        }

        [Test]
        public void LegacyLayout_OverOlderGeneratedBaseInOtherAssembly_WithBaseInit_RunsTheBase() {
            var library = CompileLibrary(LibOlderGeneratedBase, generate: false);
            const string source = @"
using KenseiECS;
public struct B : IComponent { }
public partial class Derived : Lib.BaseSystem, IInitSystem {
    [Pool] private ComponentPool<B> _b;
    partial void OnInit(World world, SharedData shared) { base.Init(world, shared); Log.Add(""derived:"" + (_b != null)); }
}";
            var (compilation, diagnostics, generated) = RunGeneratorWithLibrary(source, library);
            Assert.That(diagnostics.Errors, Is.Empty, generated);
            AssertGeneratedCompilesCleanly(compilation, generated);
            var (type, system) = InitSystem(EmitWithLibrary(compilation, library), "Derived");
            Assert.That(Log(type, system), Is.EqualTo(new[] { "base", "derived:True" }));
        }

        // An explicit base IInitSystem.Init cannot be called through base.Init, so the 2.1 layout over
        // it stays accepted without the call (as on 2.1).
        [Test]
        public void LegacyLayout_OverExplicitBaseInit_WithoutBaseInit_IsAccepted() {
            const string source = @"
using KenseiECS;
public struct B : IComponent { }
public abstract class SystemBase : IInitSystem, IRunSystem {
    void IInitSystem.Init(World world, SharedData shared) { }
    public abstract void Run(World world);
}
public partial class Derived : SystemBase, IInitSystem {
    [Pool] private ComponentPool<B> _b;
    public bool HasPool => _b != null;
    public override void Run(World world) { }
}";
            var (compilation, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.Errors, Is.Empty, generated);
            AssertGeneratedCompilesCleanly(compilation, generated);
            var (type, system) = InitSystem(Emit(compilation), "Derived");
            Assert.That(Get(type, system, "HasPool"), Is.True);
        }

        // ---- Hand-written overrides of a generated Init (G-3)

        // 2.1 rejected an override of a generated Init (CS0506, not virtual). Since 2.2 it compiles,
        // and one without base.Init leaves the base fields null: KECS013, a warning.
        [Test]
        public void HandWrittenOverride_WithoutBaseInit_ReportsKECS013() {
            const string source = @"
using KenseiECS;
public struct A : IComponent { }
public partial class Root : IRunSystem {
    [Pool] private ComponentPool<A> _a;
    public void Run(World world) { }
}
public class Leaf : Root {
    public override void Init(World world, SharedData shared) { }
}";
            var (compilation, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.Errors, Is.Empty, generated);
            var reported = diagnostics.All.Where(d => d.Id == "KECS013").ToList();
            Assert.That(reported, Has.Count.EqualTo(1), "the override without base.Init must be reported: " + string.Join("; ", diagnostics.All));
            Assert.That(reported[0].Severity, Is.EqualTo(DiagnosticSeverity.Warning));
            var userTree = compilation.SyntaxTrees.First();
            Assert.That(reported[0].Location.SourceTree, Is.SameAs(userTree));
            Assert.That(userTree.GetText().ToString(reported[0].Location.SourceSpan), Is.EqualTo("Init"), "KECS013 must point at the override");
            Assert.That(reported[0].GetMessage(), Does.Contain("'Leaf'").And.Contain("'Root'").And.Contain("base.Init(world, shared)"));
        }

        [Test]
        public void HandWrittenOverride_ThroughOverridingIntermediate_WithoutBaseInit_ReportsKECS013() {
            const string source = @"
using KenseiECS;
public struct A : IComponent { }
public partial class Root : IRunSystem {
    [Pool] private ComponentPool<A> _a;
    public void Run(World world) { }
}
public class Mid : Root {
    public override void Init(World world, SharedData shared) { base.Init(world, shared); }
}
public class Leaf : Mid {
    public override void Init(World world, SharedData shared) { }
}";
            var (_, diagnostics, _) = RunGenerator(source);
            var reported = diagnostics.All.Where(d => d.Id == "KECS013").ToList();
            Assert.That(reported, Has.Count.EqualTo(1), string.Join("; ", diagnostics.All));
            Assert.That(reported[0].GetMessage(), Does.Contain("'Leaf'").And.Contain("'Root'"));
        }

        // A generated base in another assembly is recognised by injected fields visible from here.
        [Test]
        public void HandWrittenOverride_OfGeneratedBaseInOtherAssembly_WithoutBaseInit_ReportsKECS013() {
            const string lib = @"
using KenseiECS;
namespace Lib {
    public struct A : IComponent { }
    public partial class Root : IRunSystem {
        [Pool] protected ComponentPool<A> PoolA;
        public void Run(World world) { }
    }
}";
            var library = CompileLibrary(lib);
            const string source = @"
using KenseiECS;
public class Leaf : Lib.Root {
    public override void Init(World world, SharedData shared) { }
}";
            var (_, diagnostics, _) = RunGeneratorWithLibrary(source, library);
            Assert.That(diagnostics.All.Select(d => d.Id), Does.Contain("KECS013"), string.Join("; ", diagnostics.All));
        }

        [TestCase("public override void Init(World world, SharedData shared) { base.Init(world, shared); }")]
        [TestCase("public override void Init(World world, SharedData shared) => Setup(world, shared);\n    private void Setup(World world, SharedData shared) { base.Init(world, shared); }")]
        public void HandWrittenOverride_CallingBaseInit_IsNotKECS013(string member) {
            string source = @"
using KenseiECS;
public struct A : IComponent { }
public partial class Root : IRunSystem {
    [Pool] private ComponentPool<A> _a;
    public bool HasPool => _a != null;
    public void Run(World world) { }
}
public class Leaf : Root {
    " + member + @"
}";
            var (compilation, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.All.Select(d => d.Id), Has.None.EqualTo("KECS013"), generated);
            var (type, system) = InitSystem(Emit(compilation), "Leaf");
            Assert.That(Get(type, system, "HasPool"), Is.True);
        }

        // Only a generated Init needs the call: a hand-written virtual Init may be replaced.
        [Test]
        public void HandWrittenOverride_OfHandWrittenInit_IsNotKECS013() {
            const string source = @"
using KenseiECS;
public abstract class Root : IInitSystem, IRunSystem {
    public virtual void Init(World world, SharedData shared) { }
    public void Run(World world) { }
}
public class Leaf : Root {
    public override void Init(World world, SharedData shared) { }
}";
            var (_, diagnostics, _) = RunGenerator(source);
            Assert.That(diagnostics.All, Is.Empty);
        }

        // ---- Which base Init is the system's (G-4)

        // A base that gets IInitSystem through an interface of its own (IGameSystem : IInitSystem) was
        // taken for a class that hides the interface Init: KECS006, or with IInitSystem listed on the
        // derived class the 2.1 layout that skipped the base Init.
        [TestCase(false)]
        [TestCase(true)]
        public void BaseImplementingInitSystemThroughAnotherInterface_IsChained(bool listsInitSystem) {
            string source = @"
using KenseiECS;
using System.Collections.Generic;
public struct A : IComponent { }
public interface IGameSystem : IInitSystem, IRunSystem { }
public abstract class GameSystem : IGameSystem {
    public List<string> Log = new List<string>();
    public virtual void Init(World world, SharedData shared) { Log.Add(""base""); }
    public abstract void Run(World world);
}
public partial class Move : GameSystem" + (listsInitSystem ? ", IInitSystem" : "") + @" {
    [Pool] private ComponentPool<A> _a;
    partial void OnInit(World world, SharedData shared) { Log.Add(""move:"" + (_a != null)); }
    public override void Run(World world) { }
}";
            var (compilation, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.Errors, Is.Empty, generated);
            Assert.That(generated, Does.Contain("public override void Init("));
            AssertGeneratedCompilesCleanly(compilation, generated);
            var (type, system) = InitSystem(Emit(compilation), "Move");
            Assert.That(Log(type, system), Is.EqualTo(new[] { "base", "move:True" }));
        }

        [Test]
        public void LegacyLayout_ListingAnInterfaceThatExtendsInitSystem_IsAccepted() {
            const string source = @"
using KenseiECS;
using System.Collections.Generic;
public struct A : IComponent { }
public interface IGameSystem : IInitSystem, IRunSystem { }
public abstract class SystemBase : IInitSystem, IRunSystem {
    public List<string> Log = new List<string>();
    public void Init(World world, SharedData shared) { Log.Add(""base""); }
    public abstract void Run(World world);
}
public partial class Derived : SystemBase, IGameSystem {
    [Pool] private ComponentPool<A> _a;
    partial void OnInit(World world, SharedData shared) { base.Init(world, shared); Log.Add(""derived:"" + (_a != null)); }
    public override void Run(World world) { }
}";
            var (compilation, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.Errors, Is.Empty, generated);
            AssertGeneratedCompilesCleanly(compilation, generated);
            var (type, system) = InitSystem(Emit(compilation), "Derived");
            Assert.That(Log(type, system), Is.EqualTo(new[] { "base", "derived:True" }));
        }

        // Init<T>(World, SharedData) cannot take base.Init(world, shared): T is not inferable.
        [Test]
        public void GenericInitHelperOnIntermediate_IsNotTheSystemInit() {
            const string source = @"
using KenseiECS;
using System.Collections.Generic;
public struct A : IComponent { }
public struct B : IComponent { }
public partial class G1 : IRunSystem {
    [Pool] private ComponentPool<A> _a;
    public List<string> Log = new List<string>();
    partial void OnInit(World world, SharedData shared) { Log.Add(""g1""); }
    public void Run(World world) { }
}
public abstract class Mid : G1 {
    protected void Init<T>(World world, SharedData shared) where T : struct { Log.Add(""helper""); }
}
public partial class G2 : Mid {
    [Pool] private ComponentPool<B> _b;
    partial void OnInit(World world, SharedData shared) { Log.Add(""g2""); }
}";
            var (compilation, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.Errors, Is.Empty, generated);
            AssertGeneratedCompilesCleanly(compilation, generated);
            var (type, system) = InitSystem(Emit(compilation), "G2");
            Assert.That(Log(type, system), Is.EqualTo(new[] { "g1", "g2" }), "the generated override must chain to G1.Init");
        }

        // A non-void Init(World, SharedData) is not the system Init, but it takes the base call.
        [Test]
        public void NonVoidInitOnIntermediate_IsReportedAsAnOverload() {
            const string source = @"
using KenseiECS;
public struct A : IComponent { }
public struct B : IComponent { }
public partial class G1 : IRunSystem {
    [Pool] private ComponentPool<A> _a;
    public void Run(World world) { }
}
public abstract class Mid : G1 {
    protected new int Init(World world, SharedData shared) => 0;
}
public partial class G2 : Mid {
    [Pool] private ComponentPool<B> _b;
}";
            var diagnostic = AssertReportedOnUserCode(source, "KECS006", "G2");
            Assert.That(diagnostic.GetMessage(), Does.Contain("'Mid' declares another Init overload").And.Contain("rename it"));
        }

        // The runner calls the explicit IInitSystem.Init, whatever the member order; an override of
        // the public virtual Init next to it would never run, since the explicit one does not call it.
        [TestCase(true)]
        [TestCase(false)]
        public void BaseWithExplicitAndPublicInit_ReportsKECS006(bool publicFirst) {
            const string publicInit = "public virtual void Init(World world, SharedData shared) { }";
            const string explicitInit = "void IInitSystem.Init(World world, SharedData shared) { }";
            string source = @"
using KenseiECS;
public struct A : IComponent { }
public abstract class SystemBase : IInitSystem, IRunSystem {
    " + (publicFirst ? publicInit : explicitInit) + @"
    " + (publicFirst ? explicitInit : publicInit) + @"
    public void Run(World world) { }
}
public partial class Derived : SystemBase {
    [Pool] private ComponentPool<A> _a;
}";
            var diagnostic = AssertReportedOnUserCode(source, "KECS006", "Derived");
            Assert.That(diagnostic.GetMessage(), Does.Contain("explicitly").And.Contain("does not call 'SystemBase.Init'")
                .And.Contain("call Init(world, shared) from the explicit implementation, or remove it"));
        }

        // The template "an explicit IInitSystem.Init does the mandatory part and calls the public
        // virtual Init": 2.2.0 and 2.2.1 chained it only when the public Init came first, 2.2.2
        // reported KECS006 ("would never run") in both orders.
        [TestCase(true, "Init(world, shared);")]
        [TestCase(false, "Init(world, shared);")]
        [TestCase(true, "this.Init(world, shared);")]
        public void BaseExplicitInitCallingPublicVirtualInit_IsChained(bool publicFirst, string call) {
            const string publicInit = "public virtual void Init(World world, SharedData shared) { Log.Add(\"SystemBase.Init\"); }";
            string explicitInit = "void IInitSystem.Init(World world, SharedData shared) { Log.Add(\"mandatory\"); " + call + " }";
            string source = @"
using KenseiECS;
using System.Collections.Generic;
public struct A : IComponent { }
public abstract class SystemBase : IInitSystem, IRunSystem {
    public List<string> Log = new List<string>();
    " + (publicFirst ? publicInit : explicitInit) + @"
    " + (publicFirst ? explicitInit : publicInit) + @"
    public void Run(World world) { }
}
public partial class MoveSystem : SystemBase {
    [Inc(typeof(A))] private Filter _f;
    partial void OnInit(World world, SharedData shared) { Log.Add(""MoveSystem.OnInit filter="" + (_f != null)); }
}";
            var (compilation, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.Errors, Is.Empty, generated);
            Assert.That(generated, Does.Contain("public override void Init("));
            AssertGeneratedCompilesCleanly(compilation, generated);
            var (type, system) = InitSystem(Emit(compilation), "MoveSystem");
            Assert.That(Log(type, system), Is.EqualTo(new[] { "mandatory", "SystemBase.Init", "MoveSystem.OnInit filter=True" }),
                "the runner calls the explicit Init, which reaches the generated override");
        }

        // Calling another method (an overload, or the interface again) is not forwarding.
        [Test]
        public void BaseExplicitInitCallingAnOverload_ReportsKECS006() {
            const string source = @"
using KenseiECS;
public struct A : IComponent { }
public abstract class SystemBase : IInitSystem, IRunSystem {
    public virtual void Init(World world, SharedData shared) { }
    protected void Init(World world, object context) { }
    void IInitSystem.Init(World world, SharedData shared) { Init(world, (object)shared); }
    public void Run(World world) { }
}
public partial class Derived : SystemBase {
    [Pool] private ComponentPool<A> _a;
}";
            var diagnostic = AssertReportedOnUserCode(source, "KECS006", "Derived");
            Assert.That(diagnostic.GetMessage(), Does.Contain("does not call 'SystemBase.Init'"));
        }

        // ---- The 2.1 layout over an Init that only an override in between makes non-overridable

        // The documented rule (KECS006 without base.Init) did not apply: the interface maps to
        // Root.Init, the slot's first declaration, not to the override in Mid, and that was taken
        // for "the Init of another base". Mid's and Root's Init were silently skipped.
        [TestCase("public sealed override void Init(World world, SharedData shared) { Log.Add(\"mid\"); }", "public virtual void Init(World world, SharedData shared) { Log.Add(\"root\"); }")]
        [TestCase("protected override void Init(World world, SharedData shared) { Log.Add(\"mid\"); }",
            "void IInitSystem.Init(World world, SharedData shared) { Init(world, shared); } protected virtual void Init(World world, SharedData shared) { Log.Add(\"root\"); }")]
        public void LegacyLayout_OverOverrideThatCannotBeOverridden_WithoutBaseInit_ReportsKECS006(string midInit, string rootInit) {
            string source = @"
using KenseiECS;
using System.Collections.Generic;
public struct A : IComponent { }
public class Root : IInitSystem, IRunSystem {
    public List<string> Log = new List<string>();
    " + rootInit + @"
    public void Run(World world) { }
}
public class Mid : Root {
    " + midInit + @"
}
public partial class Derived : Mid, IInitSystem {
    [Pool] private ComponentPool<A> _a;
}";
            var diagnostic = AssertReportedOnUserCode(source, "KECS006", "Derived");
            Assert.That(diagnostic.GetMessage(), Does.Contain("lists IInitSystem itself").And.Contain("add that call"));
        }

        [TestCase("public sealed override void Init(World world, SharedData shared) { Log.Add(\"mid\"); }", "public virtual void Init(World world, SharedData shared) { Log.Add(\"root\"); }")]
        [TestCase("protected override void Init(World world, SharedData shared) { Log.Add(\"mid\"); }",
            "void IInitSystem.Init(World world, SharedData shared) { Init(world, shared); } protected virtual void Init(World world, SharedData shared) { Log.Add(\"root\"); }")]
        public void LegacyLayout_OverOverrideThatCannotBeOverridden_WithBaseInit_RunsTheOverride(string midInit, string rootInit) {
            string source = @"
using KenseiECS;
using System.Collections.Generic;
public struct A : IComponent { }
public class Root : IInitSystem, IRunSystem {
    public List<string> Log = new List<string>();
    " + rootInit + @"
    public void Run(World world) { }
}
public class Mid : Root {
    " + midInit + @"
}
public partial class Derived : Mid, IInitSystem {
    [Pool] private ComponentPool<A> _a;
    partial void OnInit(World world, SharedData shared) { base.Init(world, shared); Log.Add(""derived:"" + (_a != null)); }
}";
            var (compilation, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.Errors, Is.Empty, generated);
            var (type, system) = InitSystem(Emit(compilation), "Derived");
            Assert.That(Log(type, system), Is.EqualTo(new[] { "mid", "derived:True" }));
        }

        // A non-method member named Init between the classes hides the base Init: the generated
        // override failed with CS0505 in the generated file.
        [TestCase("public new int Init;")]
        [TestCase("public new int Init => 0;")]
        [TestCase("public new event System.Action Init;")]
        [TestCase("public new class Init { }")]
        public void NonMethodInitOnIntermediate_ReportsKECS006(string member) {
            string source = @"
using KenseiECS;
public struct A : IComponent { }
public struct B : IComponent { }
public partial class G1 : IRunSystem {
    [Pool] private ComponentPool<A> _a;
    public void Run(World world) { }
}
public abstract class Mid : G1 {
    " + member + @"
}
public partial class G2 : Mid {
    [Pool] private ComponentPool<B> _b;
}";
            var diagnostic = AssertReportedOnUserCode(source, "KECS006", "G2");
            Assert.That(diagnostic.GetMessage(), Does.Contain("'Mid' declares a member named Init that is not a method"));
        }

        // ---- An override in between does not make an unreached Init reachable (GN2)

        private const string ExplicitBaseNotCallingPublic = @"
public class Base1 : IInitSystem {
    public List<string> Log = new List<string>();
    public virtual void Init(World world, SharedData shared) { Log.Add(""Base1.Init""); }
    void IInitSystem.Init(World world, SharedData shared) { Log.Add(""Base1.explicit""); }
}";

        // The runner calls Base1's explicit Init, which does not call the public one, so neither the
        // hand-written override nor the generated one runs. Up to 2.2.2 an override was trusted: no
        // diagnostic, and Sys's fields stayed null (as without the override, which was KECS006).
        [TestCase(1)]
        [TestCase(2)]
        public void BaseWithExplicitAndPublicInit_OverrideInBetween_ReportsKECS006(int overrides) {
            string source = @"
using System.Collections.Generic;
using KenseiECS;
public struct A : IComponent { }
" + ExplicitBaseNotCallingPublic + @"
public class Mid : Base1 { public override void Init(World world, SharedData shared) { base.Init(world, shared); } }
public class Last : Mid { " + (overrides == 2 ? "public override void Init(World world, SharedData shared) { base.Init(world, shared); }" : "") + @" }
public partial class Sys : Last {
    [Pool] private ComponentPool<A> _a;
}";
            var diagnostic = AssertReportedOnUserCode(source, "KECS006", "Sys");
            Assert.That(diagnostic.GetMessage(), Does.Contain("'Base1' implements IInitSystem.Init explicitly")
                .And.Contain("does not call 'Base1.Init'")
                .And.Contain("call Init(world, shared) from the explicit implementation, or remove it"));
        }

        // A base from another assembly is not read, so its explicit Init is taken for one that does not
        // call the public Init, as without an override in between (KECS006 since 2.2.2); here the
        // override is hand-written in the deriving assembly.
        [TestCase(false)]
        [TestCase(true)]
        public void BaseInOtherAssemblyWithExplicitAndPublicInit_OverrideInBetween_ReportsKECS006(bool explicitCallsPublic) {
            var library = CompileLibrary(@"
using KenseiECS;
namespace Lib {
    public class Base1 : IInitSystem {
        public virtual void Init(World world, SharedData shared) { }
        void IInitSystem.Init(World world, SharedData shared) { " + (explicitCallsPublic ? "Init(world, shared);" : "") + @" }
    }
}", generate: false);
            const string source = @"
using KenseiECS;
public struct A : IComponent { }
public class Mid : Lib.Base1 { public override void Init(World world, SharedData shared) { base.Init(world, shared); } }
public partial class Sys : Mid {
    [Pool] private ComponentPool<A> _a;
}";
            var (_, diagnostics, generated) = RunGeneratorWithLibrary(source, library);
            var reported = diagnostics.Errors.Where(d => d.Id == "KECS006").ToList();
            Assert.That(reported, Is.Not.Empty, "the override in between must not hide the explicit base Init: " + generated);
            Assert.That(reported[0].GetMessage(), Does.Contain("a base from another assembly is not read").And.Contain("remove the explicit implementation"));
        }

        [Test]
        public void BaseExplicitInitCallingPublicVirtualInit_OverrideInBetween_IsChained() {
            const string source = @"
using System.Collections.Generic;
using KenseiECS;
public struct A : IComponent { }
public class Base1 : IInitSystem {
    public List<string> Log = new List<string>();
    void IInitSystem.Init(World world, SharedData shared) { Log.Add(""Base1.explicit""); Init(world, shared); }
    public virtual void Init(World world, SharedData shared) { Log.Add(""Base1.Init""); }
}
public class Mid : Base1 { public override void Init(World world, SharedData shared) { base.Init(world, shared); Log.Add(""Mid.Init""); } }
public partial class Sys : Mid {
    [Pool] private ComponentPool<A> _a;
    partial void OnInit(World world, SharedData shared) { Log.Add(""Sys.OnInit:"" + (_a != null)); }
}";
            var (compilation, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.Errors, Is.Empty, generated);
            var (type, system) = InitSystem(Emit(compilation), "Sys");
            Assert.That(Log(type, system), Is.EqualTo(new[] { "Base1.explicit", "Base1.Init", "Mid.Init", "Sys.OnInit:True" }));
        }

        // Listing IInitSystem maps the interface to the generated override: the chain runs, the explicit
        // base Init is bypassed (as without the override in between).
        [Test]
        public void BaseWithExplicitAndPublicInit_OverrideInBetween_DerivedListsInitSystem_IsChained() {
            string source = @"
using System.Collections.Generic;
using KenseiECS;
public struct A : IComponent { }
" + ExplicitBaseNotCallingPublic + @"
public class Mid : Base1 { public override void Init(World world, SharedData shared) { base.Init(world, shared); Log.Add(""Mid.Init""); } }
public partial class Sys : Mid, IInitSystem {
    [Pool] private ComponentPool<A> _a;
    partial void OnInit(World world, SharedData shared) { Log.Add(""Sys.OnInit:"" + (_a != null)); }
}";
            var (compilation, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.Errors, Is.Empty, generated);
            var (type, system) = InitSystem(Emit(compilation), "Sys");
            Assert.That(Log(type, system), Is.EqualTo(new[] { "Base1.Init", "Mid.Init", "Sys.OnInit:True" }));
        }

        // ---- Overloads that take the generated base.Init(world, shared) (NF3)

        // C# finds an override at the class that first declares the method, so an applicable overload
        // on the chained base itself, or above it up to that class, takes the call. Up to 2.2.2 only
        // the classes strictly between the system and the chained base were checked: the call silently
        // went to the overload, and Last.Init and Root.Init never ran.
        [TestCase("public class Mid : Root { public void Init(World world, params SharedData[] shared) { } }\npublic class Last : Mid { public override void Init(World world, SharedData shared) { base.Init(world, shared); } }", "Mid")]
        [TestCase("public class Mid : Root { public void Init(World world, object shared) { } }\npublic class Last : Mid { public override void Init(World world, SharedData shared) { base.Init(world, shared); } }", "Mid")]
        [TestCase("public class Mid : Root { public void Init(World world, SharedData shared, int x = 0) { } }\npublic class Last : Mid { public override void Init(World world, SharedData shared) { base.Init(world, shared); } }", "Mid")]
        [TestCase("public class Last : Root { public override void Init(World world, SharedData shared) { base.Init(world, shared); } public void Init(World world, object shared) { } }", "Last")]
        public void OverloadOnOrAboveTheChainedOverride_ReportsKECS006(string classes, string declaring) {
            string source = @"
using KenseiECS;
public struct A : IComponent { }
public class Root : IInitSystem {
    public virtual void Init(World world, SharedData shared) { }
}
" + classes + @"
public partial class Sys : Last {
    [Pool] private ComponentPool<A> _a;
}";
            var diagnostic = AssertReportedOnUserCode(source, "KECS006", "Sys");
            Assert.That(diagnostic.GetMessage(), Does.Contain($"'{declaring}' declares another Init overload").And.Contain("rename it"));
        }

        // The same over a generated base that overrides an abstract Init: G2's base.Init bound to
        // Mid.Init(World, object), so G1's fields stayed null.
        [Test]
        public void OverloadAboveAGeneratedOverride_ReportsKECS006() {
            const string source = @"
using KenseiECS;
public struct A : IComponent { }
public struct B : IComponent { }
public abstract class Root : IInitSystem {
    public abstract void Init(World world, SharedData shared);
}
public abstract class Mid : Root {
    public void Init(World world, object shared) { }
}
public partial class G1 : Mid {
    [Pool] protected ComponentPool<A> _a;
}
public partial class G2 : G1 {
    [Pool] private ComponentPool<B> _b;
}";
            var diagnostic = AssertReportedOnUserCode(source, "KECS006", "G2");
            Assert.That(diagnostic.GetMessage(), Does.Contain("'Mid' declares another Init overload"));
        }

        // An overload next to the first declaration loses to it (an exact match in the same class).
        [Test]
        public void OverloadNextToTheFirstDeclaration_IsAccepted() {
            const string source = @"
using System.Collections.Generic;
using KenseiECS;
public struct A : IComponent { }
public class Root : IInitSystem {
    public List<string> Log = new List<string>();
    public virtual void Init(World world, SharedData shared) { Log.Add(""Root.Init""); }
    public void Init(World world, object context) { Log.Add(""Root.overload""); }
}
public class Last : Root { public override void Init(World world, SharedData shared) { base.Init(world, shared); Log.Add(""Last.Init""); } }
public partial class Sys : Last {
    [Pool] private ComponentPool<A> _a;
    partial void OnInit(World world, SharedData shared) { Log.Add(""Sys.OnInit:"" + (_a != null)); }
}";
            var (compilation, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.Errors, Is.Empty, generated);
            var (type, system) = InitSystem(Emit(compilation), "Sys");
            Assert.That(Log(type, system), Is.EqualTo(new[] { "Root.Init", "Last.Init", "Sys.OnInit:True" }));
        }

        // Documented limitation of the 2.1 layout: a `new Init` in between that does not implement
        // IInitSystem.Init itself, with no injected fields above it, is taken for an unrelated helper.
        // The generated Init replaces the interface's Init without a diagnostic, and the hidden base
        // Init does not run; base.Init in OnInit would reach the helper, not the hidden Init.
        [TestCase("public new void Init(World world, SharedData shared) { Log.Add(\"BaseM.Init\"); }")]
        [TestCase("public new virtual void Init(World world, SharedData shared) { Log.Add(\"BaseM.Init\"); }")]
        public void LegacyLayout_OverNewInitThatDoesNotImplementInitSystem_IsNotReported(string hiding) {
            string source = @"
using System.Collections.Generic;
using KenseiECS;
public struct A : IComponent { }
public class BaseR : IInitSystem {
    public List<string> Log = new List<string>();
    public virtual void Init(World world, SharedData shared) { Log.Add(""BaseR.Init""); }
}
public class BaseM : BaseR {
    " + hiding + @"
}
public partial class SysD : BaseM, IInitSystem {
    [Pool] private ComponentPool<A> _a;
    partial void OnInit(World world, SharedData shared) { Log.Add(""SysD.OnInit:"" + (_a != null)); }
}";
            var (compilation, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.Errors, Is.Empty, generated);
            var (type, system) = InitSystem(Emit(compilation), "SysD");
            Assert.That(Log(type, system), Is.EqualTo(new[] { "SysD.OnInit:True" }));
        }
    }
}
