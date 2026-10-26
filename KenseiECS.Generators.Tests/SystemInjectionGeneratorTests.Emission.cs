using System;
using System.IO;
using System.Linq;
using KenseiECS.Generators;
using Microsoft.CodeAnalysis;
using NUnit.Framework;

namespace KenseiECS.Generators.Tests {
    // The generated file itself: OnInit next to hooks of the same name, hint names, identifiers.
    public partial class SystemInjectionGeneratorTests {
        // ---- OnInit next to an OnInit(World, SharedData) the class already has (GN4)

        private const string BaseWithVirtualHook = @"
using KenseiECS;
using System.Collections.Generic;
public struct A : IComponent { }
public abstract class SystemBase : IInitSystem, IRunSystem {
    public List<string> Log = new List<string>();
    public virtual void Init(World world, SharedData shared) { Log.Add(""base.Init""); OnInit(world, shared); }
    protected virtual void OnInit(World world, SharedData shared) { Log.Add(""base hook""); }
    public void Run(World world) { }
}";

        // 2.2.1 declared its partial OnInit over the base hook: CS0114 in the generated file.
        [Test]
        public void BaseOnInitHook_NotOverridden_IsLeftToTheBase() {
            const string source = BaseWithVirtualHook + @"
public partial class Derived : SystemBase {
    [Pool] private ComponentPool<A> _a;
}";
            var (compilation, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.Errors, Is.Empty, generated);
            AssertGeneratedCompilesCleanly(compilation, generated);
            Assert.That(generated, Does.Not.Contain("OnInit"), "no partial OnInit over the base hook, and no second call of it");
            var (type, system) = InitSystem(Emit(compilation), "Derived");
            Assert.That(Log(type, system), Is.EqualTo(new[] { "base.Init", "base hook" }));
        }

        // 2.2.1 failed with CS0111: the override and the generated partial were two OnInit members.
        [TestCase("protected virtual void OnInit(World world, SharedData shared) { Log.Add(\"base hook\"); }")]
        [TestCase("protected abstract void OnInit(World world, SharedData shared);")]
        public void BaseOnInitHook_Overridden_RunsOnceWithTheDerivedFields(string hook) {
            string source = @"
using KenseiECS;
using System.Collections.Generic;
public struct A : IComponent { }
public abstract class SystemBase : IInitSystem, IRunSystem {
    public List<string> Log = new List<string>();
    public virtual void Init(World world, SharedData shared) { Log.Add(""base.Init""); OnInit(world, shared); }
    " + hook + @"
    public void Run(World world) { }
}
public partial class Derived : SystemBase {
    [Pool] private ComponentPool<A> _a;
    protected override void OnInit(World world, SharedData shared) { Log.Add(""override:"" + (_a != null)); }
}";
            var (compilation, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.Errors, Is.Empty, generated);
            AssertGeneratedCompilesCleanly(compilation, generated);
            var (type, system) = InitSystem(Emit(compilation), "Derived");
            Assert.That(Log(type, system), Is.EqualTo(new[] { "base.Init", "override:True" }), "the base Init calls the override once, after the derived fields are filled");
        }

        // Nothing calls the hook but the generated Init: a base that is not a system, an abstract base
        // Init, an abstract hook. 2.2.1 failed with CS0111; leaving the hook to the base compiled and never called the
        // override (it left the hook to a base Init that does not call it).
        [TestCase("public abstract class SystemBase : IRunSystem {", "protected virtual void OnInit(World world, SharedData shared) { }")]
        [TestCase("public abstract class SystemBase : IRunSystem {", "protected abstract void OnInit(World world, SharedData shared);")]
        [TestCase("public abstract class SystemBase : IInitSystem, IRunSystem { public abstract void Init(World world, SharedData shared);", "protected virtual void OnInit(World world, SharedData shared) { }")]
        public void BaseOnInitHook_Overridden_WithoutChainedBaseCall_IsCalledOnce(string head, string hook) {
            string source = @"
using KenseiECS;
using System.Collections.Generic;
public struct A : IComponent { }
" + head + @"
    public List<string> Log = new List<string>();
    " + hook + @"
    public void Run(World world) { }
}
public partial class Derived : SystemBase {
    [Pool] private ComponentPool<A> _a;
    protected override void OnInit(World world, SharedData shared) { Log.Add(""override:"" + (_a != null)); }
}";
            var (compilation, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.Errors, Is.Empty, generated);
            AssertGeneratedCompilesCleanly(compilation, generated);
            var (type, system) = InitSystem(Emit(compilation), "Derived");
            Assert.That(Log(type, system), Is.EqualTo(new[] { "override:True" }), "the generated Init calls the override, after the fields");
        }

        // The hook is declared in a hand-written class between the derived class and its generated
        // base: the generated base Init cannot know it.
        [Test]
        public void HookDeclaredBetweenGeneratedBaseAndDerived_Overridden_IsCalledOnce() {
            const string source = @"
using KenseiECS;
using System.Collections.Generic;
public struct A : IComponent { }
public partial class G : IRunSystem {
    [Pool] protected ComponentPool<A> BasePool;
    public List<string> Log = new List<string>();
    public void Run(World world) { }
}
public abstract class Mid : G {
    protected virtual void OnInit(World world, SharedData shared) { Log.Add(""mid hook""); }
}
public partial class Leaf : Mid {
    [Pool] private ComponentPool<A> _own;
    protected override void OnInit(World world, SharedData shared) { Log.Add(""leaf:"" + (_own != null) + "","" + (BasePool != null)); }
}";
            var (compilation, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.Errors, Is.Empty, generated);
            var (type, system) = InitSystem(Emit(compilation), "Leaf");
            Assert.That(Log(type, system), Is.EqualTo(new[] { "leaf:True,True" }));
        }

        // The 2.1 layout over a non-virtual base Init that calls the hook: the generated Init calls
        // OnInit, OnInit must call base.Init, and the base Init calls the hook again (without a guard,
        // without end). Leaving the override uncalled ran nothing at all.
        [Test]
        public void LegacyLayout_HookOverride_ReportsKECS006() {
            const string source = @"
using KenseiECS;
using System.Collections.Generic;
public struct A : IComponent { }
public class SystemBase : IInitSystem, IRunSystem {
    public List<string> Log = new List<string>();
    public void Init(World world, SharedData shared) { Log.Add(""base Init""); OnInit(world, shared); }
    protected virtual void OnInit(World world, SharedData shared) { }
    public void Run(World world) { }
}
public partial class Derived : SystemBase, IInitSystem {
    [Pool] private ComponentPool<A> _a;
    protected override void OnInit(World world, SharedData shared) { if (Log.Count == 0) base.Init(world, shared); Log.Add(""override""); }
}";
            var diagnostic = AssertReportedOnUserCode(source, "KECS006", "Derived");
            Assert.That(diagnostic.GetMessage(), Does.Contain("overrides the hook OnInit").And.Contain("partial void OnInit"));
        }

        [Test]
        public void BaseOnInitHook_WithPartialOnInit_BothRun() {
            const string source = BaseWithVirtualHook + @"
public partial class Derived : SystemBase {
    [Pool] private ComponentPool<A> _a;
    partial void OnInit(World world, SharedData shared) { Log.Add(""partial:"" + (_a != null)); }
}";
            var (compilation, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.Errors, Is.Empty, generated);
            AssertGeneratedCompilesCleanly(compilation, generated);
            var (type, system) = InitSystem(Emit(compilation), "Derived");
            Assert.That(Log(type, system), Is.EqualTo(new[] { "base.Init", "base hook", "partial:True" }), "the partial OnInit runs after the base Init, which calls its own hook");
        }

        // 2.2.1: CS0108 in the generated file.
        [Test]
        public void NonVirtualBaseOnInit_IsNotHidden() {
            const string source = @"
using KenseiECS;
using System.Collections.Generic;
public struct A : IComponent { }
public abstract class SystemBase : IInitSystem, IRunSystem {
    public List<string> Log = new List<string>();
    public virtual void Init(World world, SharedData shared) { OnInit(world, shared); }
    protected void OnInit(World world, SharedData shared) { Log.Add(""base helper""); }
    public void Run(World world) { }
}
public partial class Derived : SystemBase {
    [Pool] private ComponentPool<A> _a;
}";
            var (compilation, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.Errors, Is.Empty, generated);
            AssertGeneratedCompilesCleanly(compilation, generated);
            var (type, system) = InitSystem(Emit(compilation), "Derived");
            Assert.That(Log(type, system), Is.EqualTo(new[] { "base helper" }));
        }

        // A class nested in its generated base sees the base's private OnInit: CS0108 on 2.2.1.
        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void DerivedNestedInGeneratedBase_CompilesCleanly(bool baseOnInit, bool innerOnInit) {
            string source = @"
using KenseiECS;
using System.Collections.Generic;
public struct A : IComponent { }
public struct B : IComponent { }
public partial class BaseSystem : IRunSystem {
    [Pool] protected ComponentPool<A> PoolA;
    public List<string> Log = new List<string>();
    " + (baseOnInit ? "partial void OnInit(World world, SharedData shared) { Log.Add(\"base\"); }" : "") + @"
    public void Run(World world) { }
    public partial class Inner : BaseSystem {
        [Pool] private ComponentPool<B> _b;
        public bool HasPools => PoolA != null && _b != null;
        " + (innerOnInit ? "partial void OnInit(World world, SharedData shared) { Log.Add(\"inner\"); }" : "") + @"
    }
}";
            var (compilation, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.Errors, Is.Empty, generated);
            AssertGeneratedCompilesCleanly(compilation, generated);
            var (type, system) = InitSystem(Emit(compilation), "BaseSystem+Inner");
            Assert.That(Get(type, system, "HasPools"), Is.True);
            var expected = new[] { baseOnInit ? "base" : null, innerOnInit ? "inner" : null }.Where(s => s != null).ToArray();
            Assert.That(Log(type, system), Is.EqualTo(expected), "each OnInit runs once");
        }

        // An OnInit written without `partial` collided with the generated declaration (CS0111);
        // it is called instead.
        [Test]
        public void NonPartialOnInit_IsCalledAfterInjection() {
            const string source = @"
using KenseiECS;
using System.Collections.Generic;
public struct A : IComponent { }
public partial class Plain : IRunSystem {
    [Pool] private ComponentPool<A> _a;
    public List<string> Log = new List<string>();
    void OnInit(World world, SharedData shared) { Log.Add(""own:"" + (_a != null)); }
    public void Run(World world) { }
}";
            var (compilation, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.Errors, Is.Empty, generated);
            AssertGeneratedCompilesCleanly(compilation, generated);
            Assert.That(generated, Does.Not.Contain("partial void OnInit"));
            var (type, system) = InitSystem(Emit(compilation), "Plain");
            Assert.That(Log(type, system), Is.EqualTo(new[] { "own:True" }));
        }

        // A generated base can offer a virtual OnInit hook of its own to generated subclasses.
        [Test]
        public void VirtualOnInitOfGeneratedBase_IsAHookForGeneratedSubclasses() {
            const string source = @"
using KenseiECS;
using System.Collections.Generic;
public struct A : IComponent { }
public struct B : IComponent { }
public partial class G1 : IRunSystem {
    [Pool] protected ComponentPool<A> PoolA;
    public List<string> Log = new List<string>();
    protected virtual void OnInit(World world, SharedData shared) { Log.Add(""g1""); }
    public void Run(World world) { }
}
public partial class G2 : G1 {
    [Pool] private ComponentPool<B> _b;
    protected override void OnInit(World world, SharedData shared) { base.OnInit(world, shared); Log.Add(""g2:"" + (PoolA != null && _b != null)); }
}";
            var (compilation, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.Errors, Is.Empty, generated);
            AssertGeneratedCompilesCleanly(compilation, generated);
            var assembly = Emit(compilation);
            var (g2Type, g2) = InitSystem(assembly, "G2");
            Assert.That(Log(g2Type, g2), Is.EqualTo(new[] { "g1", "g2:True" }), "G1.Init calls the hook once, after both classes' fields");
            var (g1Type, g1) = InitSystem(assembly, "G1");
            Assert.That(Log(g1Type, g1), Is.EqualTo(new[] { "g1" }));
        }

        // ---- Hint names (GN5)

        // Roslyn compares hint names without case: Game.AI.Move and Game.Ai.Move made the generator
        // throw (CS8785, a warning), and then no system of the assembly got an Init.
        [Test]
        public void HintNames_DifferingOnlyInCase_AreMadeUnique() {
            const string source = @"
using KenseiECS;
namespace Game.AI {
    public struct P : IComponent { }
    public partial class Move : IRunSystem { [Pool] private ComponentPool<P> _p; public bool HasPool => _p != null; public void Run(World world) { } }
}
namespace Game.Ai {
    public partial class Move : IRunSystem { [Pool] private ComponentPool<Game.AI.P> _p; public bool HasPool => _p != null; public void Run(World world) { } }
}
namespace Game {
    public partial class Other : IRunSystem { [Pool] private ComponentPool<Game.AI.P> _p; public bool HasPool => _p != null; public void Run(World world) { } }
}";
            var (compilation, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.All, Is.Empty, "the generator must not fail: " + string.Join("; ", diagnostics.All));
            var paths = compilation.SyntaxTrees.Skip(1).Select(t => Path.GetFileName(t.FilePath)).ToList();
            Assert.That(paths, Has.Count.EqualTo(3));
            Assert.That(paths.Distinct(StringComparer.OrdinalIgnoreCase).Count(), Is.EqualTo(3), string.Join(", ", paths));
            Assert.That(paths, Does.Contain("Game.Other.Injection.g.cs"), "a name without a collision stays as before");
            var assembly = Emit(compilation);
            foreach (var name in new[] { "Game.AI.Move", "Game.Ai.Move", "Game.Other" }) {
                var (type, system) = InitSystem(assembly, name);
                Assert.That(Get(type, system, "HasPool"), Is.True, name + " must get its Init");
            }
        }

        [Test]
        public void HintNames_OfGenericAndUnderscoreNames_AreMadeUnique() {
            const string source = @"
using KenseiECS;
public struct P : IComponent { }
public partial class Gen<T> : IRunSystem { [Pool] private ComponentPool<P> _p; public void Run(World world) { } }
public partial class Gen_T_ : IRunSystem { [Pool] private ComponentPool<P> _p; public bool HasPool => _p != null; public void Run(World world) { } }";
            var (compilation, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.All, Is.Empty, string.Join("; ", diagnostics.All));
            Assert.That(compilation.SyntaxTrees.Skip(1).Count(), Is.EqualTo(2));
            var (type, system) = InitSystem(Emit(compilation), "Gen_T_");
            Assert.That(Get(type, system, "HasPool"), Is.True);
        }

        // Which of the colliding names gets the extra segment depends on the names, not on the files.
        [Test]
        public void HintNames_DoNotDependOnFileOrder() {
            const string first = @"
using KenseiECS;
namespace Game.AI { public struct P : IComponent { } public partial class Move : IRunSystem { [Pool] private ComponentPool<P> _p; public void Run(World world) { } } }";
            const string second = @"
using KenseiECS;
namespace Game.Ai { public partial class Move : IRunSystem { [Pool] private ComponentPool<Game.AI.P> _p; public void Run(World world) { } } }";
            string Names(params string[] sources) {
                var (compilation, diagnostics, _) = RunGenerator(sources, new SystemInjectionGenerator());
                Assert.That(diagnostics.All, Is.Empty, string.Join("; ", diagnostics.All));
                return string.Join("|", compilation.SyntaxTrees.Skip(sources.Length)
                    .Select(t => Path.GetFileName(t.FilePath) + "=" + t.ToString().Split('\n').First(l => l.StartsWith("namespace")))
                    .OrderBy(s => s, StringComparer.Ordinal));
            }
            Assert.That(Names(second, first), Is.EqualTo(Names(first, second)));
        }

        // ---- Identifiers and literals in the generated file (GN6)

        // A keyword in a namespace or type name put '@' into the hint name, which Roslyn rejects: the
        // generator failed as a whole (CS8785). Field, type and type parameter names were not escaped.
        [Test]
        public void KeywordIdentifiers_AreEscaped() {
            const string source = @"
using KenseiECS;
namespace Game.@event {
    public struct A : IComponent { }
    public static partial class @static {
        public partial class @class<@in> : IRunSystem {
            [Pool] private ComponentPool<A> @event;
            [Inc(typeof(A))] private Filter @for;
            public bool HasFields => @event != null && @for != null;
            public void Run(World world) { }
        }
    }
}";
            var (compilation, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.All, Is.Empty, "the generator must not fail: " + string.Join("; ", diagnostics.All));
            AssertGeneratedCompilesCleanly(compilation, generated);
            var assembly = Emit(compilation);
            var type = assembly.GetType("Game.event.static+class`1")!.MakeGenericType(typeof(int));
            var system = (ISystem)Activator.CreateInstance(type)!;
            new SystemsRunner(new World()).Add(system).Init();
            Assert.That(type.GetProperty("HasFields")!.GetValue(system), Is.True);
        }

        [Test]
        public void SharedKey_WithControlCharacters_IsEscaped() {
            const string source = @"
using KenseiECS;
namespace Demo {
    public class Config { }
    public partial class KeyedSystem : IRunSystem {
        [Shared(""line\nbreak\t\u0001\\\"""")] private Config _config;
        public bool HasConfig => _config != null;
        public void Run(World world) { }
    }
}";
            var (compilation, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.Errors, Is.Empty, generated);
            AssertGeneratedCompilesCleanly(compilation, generated);
            var assembly = Emit(compilation);
            var configType = assembly.GetType("Demo.Config")!;
            var shared = new SharedData();
            AddShared(shared, configType, Activator.CreateInstance(configType)!, "line\nbreak\t\u0001\\\"");
            var type = assembly.GetType("Demo.KeyedSystem")!;
            var system = (ISystem)Activator.CreateInstance(type)!;
            new SystemsRunner(new World(), shared).Add(system).Init();
            Assert.That(type.GetProperty("HasConfig")!.GetValue(system), Is.True, "the key must reach SharedData unchanged");
        }

        // `new` exactly when the generated Init hides an accessible inherited member: 2.2.1 left out
        // `new` over a static Init or a non-method Init (CS0108) and added it over an Init with other
        // parameter modes (CS0109).
        [TestCase("public static void Init(World world, SharedData shared) { }", true)]
        [TestCase("public int Init => 0;", true)]
        [TestCase("public int Init(World world, SharedData shared) => 0;", true)]
        [TestCase("public void Init(in World world, in SharedData shared) { }", false)]
        [TestCase("public void Init(World world, object shared) { }", false)]
        public void InitOverAnUnrelatedBaseMember_IsNewExactlyWhenItHidesIt(string rootMember, bool hides) {
            string source = @"
using KenseiECS;
public struct A : IComponent { }
public abstract class Root {
    " + rootMember + @"
}
public partial class S : Root, IRunSystem {
    [Pool] private ComponentPool<A> _a;
    public bool HasPool => _a != null;
    public void Run(World world) { }
}";
            var (compilation, diagnostics, generated) = RunGenerator(source);
            Assert.That(diagnostics.Errors, Is.Empty, generated);
            AssertGeneratedCompilesCleanly(compilation, generated);
            Assert.That(generated.Contains("public new virtual void Init("), Is.EqualTo(hides), generated);
            var (type, system) = InitSystem(Emit(compilation), "S");
            Assert.That(Get(type, system, "HasPool"), Is.True);
        }

        // A private protected Init of a base in another assembly is not accessible here: no `new`.
        [Test]
        public void InitOverAnInaccessibleBaseInit_IsNotNew() {
            const string lib = @"
namespace Lib {
    public abstract class Root {
        private protected void Init(KenseiECS.World world, KenseiECS.SharedData shared) { }
    }
}";
            var library = CompileLibrary(lib, generate: false);
            const string source = @"
using KenseiECS;
public struct A : IComponent { }
public partial class S : Lib.Root, IRunSystem {
    [Pool] private ComponentPool<A> _a;
    public void Run(World world) { }
}";
            var (compilation, diagnostics, generated) = RunGeneratorWithLibrary(source, library);
            Assert.That(diagnostics.Errors, Is.Empty, generated);
            AssertGeneratedCompilesCleanly(compilation, generated);
            Assert.That(generated, Does.Contain("public virtual void Init(").And.Not.Contain(" new "));
        }
    }
}
