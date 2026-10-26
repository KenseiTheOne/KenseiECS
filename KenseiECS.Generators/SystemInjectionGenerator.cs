using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace KenseiECS.Generators {
    /// <summary>
    /// For every partial class with [Inc]/[Exc]/[Any]/[Pool]/[Group]/[Shared]
    /// fields, emits an IInitSystem.Init that fills those fields from the
    /// World and SharedData and then calls partial void OnInit(world, shared).
    /// </summary>
    /// <remarks>
    /// The generated Init is <c>public virtual</c> in every class that is not sealed, whatever
    /// subclasses the compilation has, so a subclass in another assembly or under another define
    /// sees the same Init. Inheritance: when a system with injected fields derives from another
    /// one, its generated Init overrides the base Init: it fills its own fields, calls
    /// base.Init (the base fields and the base OnInit, which then see the derived fields too) and
    /// calls its own OnInit last. A hand-written base Init is chained the same way when it is
    /// public virtual (or abstract, then without the base call), also next to an explicit
    /// IInitSystem.Init that calls it. A base.Init written in such a
    /// class is reported (KECS012): it would run the base twice. A base Init that cannot be
    /// overridden, or a hand-written member between that would take the generated
    /// base.Init(world, shared) call or hide the base Init, is reported (KECS006); over a base Init
    /// that cannot be overridden, a class that lists IInitSystem itself keeps the 2.1 layout (its
    /// own Init re-implements the interface, and its OnInit must call base.Init, KECS006 otherwise).
    /// A hand-written override of a generated Init that does not call base.Init is a warning
    /// (KECS013). When the class or a base already has an OnInit(World, SharedData) of its own
    /// (for example the virtual hook of a hand-written base), no partial OnInit is declared over it;
    /// an override of such a hook is called by the generated Init unless the chained base Init can
    /// call it.
    /// </remarks>
    [Generator]
    public sealed class SystemInjectionGenerator : ISourceGenerator {
        private const string IncAttribute = "KenseiECS.IncAttribute";
        private const string ExcAttribute = "KenseiECS.ExcAttribute";
        private const string AnyAttribute = "KenseiECS.AnyAttribute";
        private const string PoolAttribute = "KenseiECS.PoolAttribute";
        private const string GroupAttribute = "KenseiECS.GroupAttribute";
        private const string SharedAttribute = "KenseiECS.SharedAttribute";

        private static readonly DiagnosticDescriptor NotPartial = new(
            "KECS001", "System class must be partial",
            "Class '{0}' has injected fields but is not declared partial; add the partial modifier so Init can be generated",
            "KenseiECS", DiagnosticSeverity.Error, true);

        private static readonly DiagnosticDescriptor InitAlreadyDeclared = new(
            "KECS002", "Init is generated",
            "Class '{0}' declares Init(World, SharedData) and also has injected fields; remove Init and put custom setup into 'partial void OnInit(World world, SharedData shared)'",
            "KenseiECS", DiagnosticSeverity.Error, true);

        private static readonly DiagnosticDescriptor WrongFieldType = new(
            "KECS003", "Injected field has an unexpected type",
            "Field '{0}' is marked [{1}] but its type is '{2}'; expected {3}",
            "KenseiECS", DiagnosticSeverity.Error, true);

        private static readonly DiagnosticDescriptor ContainerNotPartial = new(
            "KECS005", "Containing type must be partial",
            "Class '{0}' is nested in '{1}', which is not declared partial; add the partial modifier to every containing type",
            "KenseiECS", DiagnosticSeverity.Error, true);

        private static readonly DiagnosticDescriptor FilterWithoutInc = new(
            "KECS004", "Filter needs Inc or Any",
            "Filter field '{0}' has [Exc] without [Inc] or [Any]; exclude-only filters are not supported",
            "KenseiECS", DiagnosticSeverity.Error, true);

        private static readonly DiagnosticDescriptor InheritedInjection = new(
            "KECS006", "Base Init cannot be chained",
            "Class '{0}' has injected fields and derives from '{1}', but its generated Init cannot override and call the base Init: {2}",
            "KenseiECS", DiagnosticSeverity.Error, true);

        private static readonly DiagnosticDescriptor FilterWithoutTypes = new(
            "KECS007", "Filter needs component types",
            "Filter field '{0}' lists no component types in [Inc] or [Any]; add at least one required or alternative type",
            "KenseiECS", DiagnosticSeverity.Error, true);

        private static readonly DiagnosticDescriptor NotAComponent = new(
            "KECS008", "Filter type must be a component",
            "Filter field '{0}' lists '{1}' in [{2}], but filter types must be structs implementing KenseiECS.IComponent",
            "KenseiECS", DiagnosticSeverity.Error, true);

        private static readonly DiagnosticDescriptor ReadonlyField = new(
            "KECS009", "Injected field must not be readonly",
            "Field '{0}' is injected but declared readonly; the generated Init assigns it, remove the readonly modifier",
            "KenseiECS", DiagnosticSeverity.Error, true);

        private static readonly DiagnosticDescriptor NotAClass = new(
            "KECS010", "System with injected fields must be a class",
            "Type '{0}' has injected fields but is a {1}; injection is generated only for classes",
            "KenseiECS", DiagnosticSeverity.Error, true);

        private static readonly DiagnosticDescriptor UnsupportedContainer = new(
            "KECS011", "Containing type must be a class or struct",
            "Class '{0}' is nested in '{1}', which is a {2}; only classes and structs can contain a system with injected fields",
            "KenseiECS", DiagnosticSeverity.Error, true);

        private static readonly DiagnosticDescriptor BaseInitCalled = new(
            "KECS012", "Generated Init already calls base.Init",
            "Class '{0}' calls base.Init, but its generated Init already calls base.Init before OnInit; remove the call from OnInit",
            "KenseiECS", DiagnosticSeverity.Error, true);

        private static readonly DiagnosticDescriptor OverrideSkipsGeneratedInit = new(
            "KECS013", "Override of a generated Init does not call base.Init",
            "Class '{0}' overrides Init without calling base.Init(world, shared), so the generated Init of '{1}' never runs: its injected fields stay null and its OnInit is skipped; call base.Init(world, shared) from the override",
            "KenseiECS", DiagnosticSeverity.Warning, true);

        private static readonly SymbolDisplayFormat Qualified = SymbolDisplayFormat.FullyQualifiedFormat;

        // A namespace as written in a declaration: no global::, keywords escaped (Game.@event).
        private static readonly SymbolDisplayFormat NamespaceFormat = SymbolDisplayFormat.FullyQualifiedFormat
            .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted);

        // Type name without the '?' of a nullable reference type, so `Filter?` compares equal to `Filter`.
        private static readonly SymbolDisplayFormat Unannotated = SymbolDisplayFormat.CSharpErrorMessageFormat
            .RemoveMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

        public void Initialize(GeneratorInitializationContext context) {
            context.RegisterForSyntaxNotifications(() => new Receiver());
        }

        public void Execute(GeneratorExecutionContext context) {
            if (!(context.SyntaxReceiver is Receiver receiver)) {
                return;
            }

            var run = new Run(context);
            foreach (var classDecl in receiver.Candidates) {
                var model = context.Compilation.GetSemanticModel(classDecl.SyntaxTree);
                if (model.GetDeclaredSymbol(classDecl) is INamedTypeSymbol symbol) {
                    run.GetPlan(symbol, classDecl);
                }
            }

            // A hand-written override of a generated Init must call base.Init, or the generated
            // Init (and with it the injection) never runs.
            var checkedOverrides = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var classDecl in receiver.InitOverrides) {
                var model = context.Compilation.GetSemanticModel(classDecl.SyntaxTree);
                if (model.GetDeclaredSymbol(classDecl) is INamedTypeSymbol symbol && checkedOverrides.Add(symbol)) {
                    run.CheckHandWrittenOverride(symbol);
                }
            }

            var emitted = run.Plans.Where(p => p.Ok).ToList();
            var hints = HintNames(emitted);
            foreach (var plan in emitted) {
                Emit(context, plan, hints[plan]);
            }
        }

        /// <summary>
        /// One generator pass. Plans are created on demand, base classes first, so a derived
        /// system always knows whether its base in this compilation gets a generated Init.
        /// </summary>
        private sealed class Run {
            private readonly GeneratorExecutionContext _context;
            private readonly Dictionary<INamedTypeSymbol, Plan> _plans = new(SymbolEqualityComparer.Default);
            private readonly IMethodSymbol _interfaceInit;

            public readonly List<Plan> Plans = new();

            public Run(GeneratorExecutionContext context) {
                _context = context;
                _interfaceInit = context.Compilation.GetTypeByMetadataName("KenseiECS.IInitSystem")?
                    .GetMembers("Init").OfType<IMethodSymbol>().FirstOrDefault();
            }

            // Null when the class has no injected fields (nothing to generate).
            public Plan GetPlan(INamedTypeSymbol symbol, TypeDeclarationSyntax declaration = null) {
                symbol = symbol.OriginalDefinition;
                if (_plans.TryGetValue(symbol, out var plan)) {
                    return plan;
                }
                declaration ??= FindDeclaration(symbol);
                plan = declaration == null || !HasInjectedFields(symbol) ? null : Analyze(_context, symbol, declaration);
                // Added before Resolve, so a malformed (cyclic) hierarchy cannot recurse forever.
                _plans.Add(symbol, plan);
                if (plan != null) {
                    if (plan.Ok) {
                        Resolve(plan);
                    }
                    if (plan.Ok) {
                        ResolveOnInit(plan);
                    }
                    Plans.Add(plan);
                }
                return plan;
            }

            // A base reached through a derived class may keep its fields in any of its parts.
            private static TypeDeclarationSyntax FindDeclaration(INamedTypeSymbol symbol) {
                TypeDeclarationSyntax first = null;
                foreach (var reference in symbol.DeclaringSyntaxReferences) {
                    if (!(reference.GetSyntax() is TypeDeclarationSyntax declaration)) {
                        continue;
                    }
                    first ??= declaration;
                    if (declaration.Members.OfType<FieldDeclarationSyntax>().Any(f => f.AttributeLists.Count > 0)) {
                        return declaration;
                    }
                }
                return first;
            }

            private void Resolve(Plan plan) {
                var symbol = plan.Symbol;
                var baseType = symbol.BaseType;
                if (baseType == null) {
                    return;
                }

                // A base that is not a system (no IInitSystem, no injected fields) has nothing to
                // chain; an unrelated Init(World, SharedData) on it is only hidden.
                if (!IsInitSystem(baseType) && !InheritsInjectedFields(baseType)) {
                    plan.HidesBase = HidesInheritedInit(plan);
                    return;
                }

                // Otherwise the generated Init overrides the base Init and calls it, so the base
                // fields and the base OnInit are done before the derived OnInit.
                for (var current = baseType; current != null && current.SpecialType != SpecialType.System_Object; current = current.BaseType) {
                    if (IsInSource(current) && HasInjectedFields(current)) {
                        var basePlan = GetPlan(current);
                        if (basePlan == null || !basePlan.Ok) {
                            // The base reported its own error; a second one here adds nothing.
                            plan.Ok = false;
                            return;
                        }
                        Chain(plan, current, callsBase: true);
                        return;
                    }

                    var init = DeclaredInit(current, symbol);
                    if (init == null) {
                        continue;
                    }
                    string problem = OverrideProblem(current, init, ListsInitSystem(symbol));
                    if (problem == null) {
                        Chain(plan, current, callsBase: !init.IsAbstract);
                        return;
                    }
                    if (!ListsInitSystem(symbol)) {
                        Fail(plan, current, problem + Remedy(current, init));
                        return;
                    }
                    // The 2.1 layout, the only one possible over an Init that cannot be
                    // overridden: a class that lists IInitSystem itself re-implements the
                    // interface, and its OnInit is responsible for calling base.Init. Without that
                    // call the base Init (its injection, or the Init the interface ran) is skipped.
                    if (init.ExplicitInterfaceImplementations.Length == 0
                        && (InheritsInjectedFields(baseType) || ImplementsInitSystem(current, init))
                        && !CallsBaseInit(symbol)) {
                        Fail(plan, current, $"{problem}, and '{symbol.Name}' lists IInitSystem itself, so its generated Init replaces the base Init, which then runs only through a base.Init(world, shared) call in OnInit; add that call");
                        return;
                    }
                    plan.HidesBase = HidesInheritedInit(plan);
                    plan.ReplacedInit = current;
                    plan.ReplacedInitProblem = problem;
                    return;
                }
                Fail(plan, baseType, "no base class declares the Init(World, SharedData) that implements IInitSystem");
            }

            // Makes the generated Init an override of the Init on <target>. Returns false when the
            // override or the generated base call cannot reach it (reported).
            private bool Chain(Plan plan, INamedTypeSymbol target, bool callsBase) {
                if (!FailIfInitHidden(plan, target)) {
                    return false;
                }
                plan.Overrides = true;
                plan.CallsBase = callsBase;
                plan.ChainTarget = target;
                if (!callsBase) {
                    return true;
                }
                if (!FailIfBaseCallCaptured(plan, target)) {
                    return false;
                }
                ReportBaseInitCalls(plan);
                return true;
            }

            /// <summary>
            /// The generated Init already calls base.Init, so a <c>base.Init</c> written in the
            /// class would run the base Init (fields and OnInit) a second time. Purely syntactic:
            /// any <c>base.Init</c> member access in the class's own declarations counts, whatever
            /// overload it would bind to. Nested types are skipped, their <c>base</c> is another
            /// class. The class is still emitted, so no follow-up errors about a missing Init
            /// appear next to this one.
            /// </summary>
            private void ReportBaseInitCalls(Plan plan) {
                foreach (var access in BaseInitAccesses(plan.Symbol)) {
                    _context.ReportDiagnostic(Diagnostic.Create(BaseInitCalled, access.GetLocation(), plan.Symbol.Name));
                }
            }

            /// <summary>
            /// A member named Init that is not a method (a field, property, event or nested type)
            /// on a class between <paramref name="plan"/> and <paramref name="target"/> hides the
            /// base Init, so the generated override would fail with CS0505. Reported instead.
            /// </summary>
            private bool FailIfInitHidden(Plan plan, INamedTypeSymbol target) {
                var compilation = _context.Compilation;
                for (var current = plan.Symbol.BaseType; current != null && !SymbolEqualityComparer.Default.Equals(current, target); current = current.BaseType) {
                    foreach (var member in current.GetMembers("Init")) {
                        if (member is IMethodSymbol || member.IsImplicitlyDeclared || !compilation.IsSymbolAccessibleWithin(member, plan.Symbol)) {
                            continue;
                        }
                        Fail(plan, current, $"'{current.Name}' declares a member named Init that is not a method, which hides the Init of '{target.Name}'; rename it");
                        return false;
                    }
                }
                return true;
            }

            /// <summary>
            /// The emitted <c>base.Init(world, shared)</c> is bound by C# overload resolution, which
            /// takes the applicable Init declared in the most derived base, and sees an override at the
            /// class that first declares the method. A hand-written Init overload on a class between
            /// <paramref name="plan"/> and that class (<paramref name="target"/>, the base whose Init
            /// must be chained, included when its Init is an override) would silently take the call and
            /// the chained Init would never run, so that is reported instead. An overload next to the
            /// first declaration loses to it. Returns false when reported.
            /// </summary>
            private bool FailIfBaseCallCaptured(Plan plan, INamedTypeSymbol target) {
                var compilation = _context.Compilation;
                var world = compilation.GetTypeByMetadataName("KenseiECS.World");
                var shared = compilation.GetTypeByMetadataName("KenseiECS.SharedData");
                if (world == null || shared == null) {
                    return true;
                }
                // Compared as definitions: the chain target of a generic generated base names its
                // own base with that base's type parameters.
                var declaring = FirstDeclarationOfChainedInit(target).OriginalDefinition;
                for (var current = plan.Symbol.BaseType; current != null && !SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, declaring); current = current.BaseType) {
                    foreach (var method in current.GetMembers("Init").OfType<IMethodSymbol>()) {
                        // An override is looked up at its original declaration, which sits higher up.
                        // A static overload still takes part and turns the call into CS0176.
                        if (method.IsOverride || method.MethodKind != MethodKind.Ordinary
                            || !compilation.IsSymbolAccessibleWithin(method, plan.Symbol)
                            || !AcceptsInitArguments(compilation, method, world, shared)) {
                            continue;
                        }
                        Fail(plan, current, $"'{current.Name}' declares another Init overload, {method.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)}, that would take the generated base.Init(world, shared) call; rename it");
                        return false;
                    }
                }
                return true;
            }

            // The class with the first declaration (virtual or abstract, not an override) of the
            // Init(World, SharedData) that a generated Init chained to <target> overrides: <target>
            // itself unless its Init is an override. Up through hand-written overrides, the chain
            // targets of generated bases in this compilation, and, for an override of a generated
            // Init that the compilation does not contain yet, the generated base below it.
            private INamedTypeSymbol FirstDeclarationOfChainedInit(INamedTypeSymbol target) {
                var current = target;
                // A malformed (cyclic) hierarchy must not loop forever.
                for (int guard = 0; guard < 256; guard++) {
                    if (IsInSource(current) && HasInjectedFields(current)) {
                        var basePlan = GetPlan(current);
                        if (basePlan == null || !basePlan.Ok || !basePlan.Overrides || basePlan.ChainTarget == null) {
                            return current;
                        }
                        current = basePlan.ChainTarget;
                        continue;
                    }
                    var init = current.GetMembers("Init").OfType<IMethodSymbol>()
                        .FirstOrDefault(m => m.MethodKind == MethodKind.Ordinary && !m.IsStatic && IsInitSignature(m));
                    if (init == null || !init.IsOverride) {
                        return current;
                    }
                    if (init.OverriddenMethod != null) {
                        current = init.OverriddenMethod.ContainingType;
                        continue;
                    }
                    var generated = current.BaseType;
                    while (generated != null && !(IsInSource(generated) && HasInjectedFields(generated))) {
                        generated = generated.BaseType;
                    }
                    if (generated == null) {
                        return current;
                    }
                    current = generated;
                }
                return current;
            }

            /// <summary>
            /// KECS013: a hand-written class without injected fields whose <c>override</c> of
            /// Init(World, SharedData) ends up overriding a generated Init (directly or through
            /// hand-written overriding intermediates) and has no <c>base.Init</c> in its own
            /// declarations. Like KECS012 the check is syntactic. A generated Init from another
            /// assembly is recognised by injected fields visible there (protected or public).
            /// </summary>
            public void CheckHandWrittenOverride(INamedTypeSymbol symbol) {
                if (GetPlan(symbol) != null) {
                    // Injected fields next to a hand-written Init are KECS002.
                    return;
                }
                foreach (var method in symbol.GetMembers("Init").OfType<IMethodSymbol>()) {
                    if (!method.IsOverride || !IsInitSignature(method) || !method.Locations.Any(l => l.IsInSource)) {
                        continue;
                    }
                    var root = GeneratedInitBelow(symbol);
                    if (root != null && !CallsBaseInit(symbol)) {
                        _context.ReportDiagnostic(Diagnostic.Create(OverrideSkipsGeneratedInit, method.Locations[0], symbol.Name, root.Name));
                    }
                    return;
                }
            }

            // The class whose generated Init an Init override in <symbol> ends up overriding, or null.
            private INamedTypeSymbol GeneratedInitBelow(INamedTypeSymbol symbol) {
                for (var current = symbol.BaseType; current != null && current.SpecialType != SpecialType.System_Object; current = current.BaseType) {
                    if (HasInjectedFields(current)) {
                        if (!IsInSource(current)) {
                            return current;
                        }
                        var basePlan = GetPlan(current);
                        return basePlan != null && basePlan.Ok ? current : null;
                    }
                    var init = DeclaredInit(current, symbol);
                    if (init == null) {
                        continue;
                    }
                    if (!init.IsOverride) {
                        return null;
                    }
                }
                return null;
            }

            private void Fail(Plan plan, INamedTypeSymbol baseType, string problem) {
                _context.ReportDiagnostic(Diagnostic.Create(InheritedInjection, plan.Declaration.Identifier.GetLocation(), plan.Symbol.Name, baseType.Name, problem));
                plan.Ok = false;
            }

            /// <summary>
            /// Whether a non-override Init(World, SharedData) in <paramref name="plan"/> hides an
            /// inherited member and needs <c>new</c>: an accessible inherited method with the same
            /// signature (static or not, any return type) or an accessible inherited non-method
            /// member named Init, as C# hiding works. Otherwise <c>new</c> would be CS0109.
            /// </summary>
            private bool HidesInheritedInit(Plan plan) {
                var compilation = _context.Compilation;
                for (var current = plan.Symbol.BaseType; current != null && current.SpecialType != SpecialType.System_Object; current = current.BaseType) {
                    if (IsInSource(current) && HasInjectedFields(current) && GetPlan(current)?.Ok == true) {
                        return true;
                    }
                    foreach (var member in current.GetMembers("Init")) {
                        if (member.IsImplicitlyDeclared || !compilation.IsSymbolAccessibleWithin(member, plan.Symbol)) {
                            continue;
                        }
                        if (!(member is IMethodSymbol method) || (method.MethodKind == MethodKind.Ordinary && IsInitSignature(method))) {
                            return true;
                        }
                    }
                }
                return false;
            }

            /// <summary>
            /// How the generated Init reaches OnInit. Normally it declares <c>partial void OnInit</c>
            /// and calls it. A class that declares an OnInit(World, SharedData) of its own that is
            /// not that partial (an override of a base hook, or a plain method) gets no declaration:
            /// an override of a hook that the chained base Init can call (the hook is first declared
            /// in that base or below it) is left to that Init, anything else is called. In the 2.1
            /// layout an override of a hook the replaced base Init can call is KECS006: OnInit must
            /// call base.Init, which would call the override again.
            /// When a base already has an accessible OnInit(World, SharedData) (the virtual hook of a
            /// hand-written base, or the private OnInit of a generated base the class is nested in),
            /// the partial would hide it: it is declared only when the class implements it, with
            /// the hiding warning silenced, and otherwise left out together with the call.
            /// </summary>
            private void ResolveOnInit(Plan plan) {
                bool implementsPartial = false, ownNonMethod = false;
                IMethodSymbol own = null;
                foreach (var member in plan.Symbol.GetMembers("OnInit")) {
                    if (member is IMethodSymbol method) {
                        if (!IsHookSignature(method)) {
                            continue;
                        }
                        if (IsGeneratedHookImplementation(method)) {
                            implementsPartial = true;
                        } else if (!method.IsImplicitlyDeclared) {
                            own = method;
                        }
                    } else if (!member.IsImplicitlyDeclared) {
                        ownNonMethod = true;
                    }
                }
                bool inherited = InheritsOnInit(plan);
                if (implementsPartial) {
                    plan.OnInit = OnInitMode.Declare;
                    plan.HidesOnInit = inherited;
                } else if (ownNonMethod) {
                    plan.OnInit = OnInitMode.None;
                } else if (own != null) {
                    if (own.IsOverride && plan.CallsBase && HookDeclaredInOrBelow(own, plan.ChainTarget)) {
                        // The chained base Init sees the hook, so it is the one that calls it.
                        plan.OnInit = OnInitMode.None;
                    } else if (own.IsOverride && plan.ReplacedInit != null && HookDeclaredInOrBelow(own, plan.ReplacedInit)) {
                        Fail(plan, plan.ReplacedInit,
                            $"{plan.ReplacedInitProblem}, and '{plan.Symbol.Name}' lists IInitSystem itself and overrides the hook OnInit that '{plan.ReplacedInit.Name}.Init' may call: in this layout the generated Init calls that override, and the base.Init it must call would run the hook again; make '{plan.ReplacedInit.Name}.Init' public virtual, or move the setup into 'partial void OnInit' instead of overriding the hook");
                    } else {
                        // Nothing else calls it: a base that is not a system, an abstract base Init,
                        // or a hook declared above the base whose Init is chained.
                        plan.OnInit = OnInitMode.CallExisting;
                    }
                } else {
                    plan.OnInit = inherited ? OnInitMode.None : OnInitMode.Declare;
                }
            }

            // Whether the first declaration of the hook <own> overrides is in <type> or one of its bases,
            // where the Init of <type> can call it.
            private static bool HookDeclaredInOrBelow(IMethodSymbol own, INamedTypeSymbol type) {
                if (type == null) {
                    return false;
                }
                var root = own;
                while (root.IsOverride && root.OverriddenMethod != null) {
                    root = root.OverriddenMethod;
                }
                var declaring = root.ContainingType.OriginalDefinition;
                for (var current = type; current != null; current = current.BaseType) {
                    if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, declaring)) {
                        return true;
                    }
                }
                return false;
            }

            private bool InheritsOnInit(Plan plan) {
                var compilation = _context.Compilation;
                for (var current = plan.Symbol.BaseType; current != null && current.SpecialType != SpecialType.System_Object; current = current.BaseType) {
                    // The OnInit a generated base gets is private: only a class nested in it sees it.
                    if (IsInSource(current) && HasInjectedFields(current) && IsNestedIn(plan.Symbol, current.OriginalDefinition)) {
                        var basePlan = GetPlan(current);
                        if (basePlan != null && basePlan.Ok && basePlan.OnInit == OnInitMode.Declare) {
                            return true;
                        }
                    }
                    foreach (var member in current.GetMembers("OnInit")) {
                        if (member.IsImplicitlyDeclared || !compilation.IsSymbolAccessibleWithin(member, plan.Symbol)) {
                            continue;
                        }
                        if (!(member is IMethodSymbol method) || IsHookSignature(method)) {
                            return true;
                        }
                    }
                }
                return false;
            }

            // The Init(World, SharedData) a type declares itself that a derived Init would override:
            // an explicit IInitSystem.Init (preferred, the interface maps to it whatever the member
            // order) unless it calls an ordinary Init of the type that can be overridden, which is
            // then taken (the runner reaches it, and an override of it, through the explicit one);
            // otherwise an ordinary instance Init with that signature, void, not generic,
            // visible from <within>. Other Init methods are overloads (see FailIfBaseCallCaptured).
            private IMethodSymbol DeclaredInit(INamedTypeSymbol type, INamedTypeSymbol within) {
                var members = type.GetMembers();
                foreach (var method in members.OfType<IMethodSymbol>()) {
                    if (method.ExplicitInterfaceImplementations.Any(m => IsInitSystemInterface(m.ContainingType))) {
                        var forwarded = ForwardedInit(type, method);
                        return forwarded != null && CanBeOverridden(forwarded)
                            && _context.Compilation.IsSymbolAccessibleWithin(forwarded, within)
                            ? forwarded
                            : method;
                    }
                }
                foreach (var method in members.OfType<IMethodSymbol>()) {
                    if (method.MethodKind == MethodKind.Ordinary && !method.IsStatic && method.ReturnsVoid
                        && IsInitSignature(method)
                        && _context.Compilation.IsSymbolAccessibleWithin(method, within)) {
                        return method;
                    }
                }
                return null;
            }

            // Whether the runner reaches <init> through IInitSystem.Init on <type>: it is what the
            // interface maps to, the Init an explicit implementation calls, or an override of either
            // (the interface maps to the slot's first declaration, not to an override in a class that
            // does not list the interface). A virtual Init that hides the one the interface maps to is not.
            private bool ImplementsInitSystem(INamedTypeSymbol type, IMethodSymbol init) {
                if (_interfaceInit == null) {
                    return true;
                }
                if (!(type.FindImplementationForInterfaceMember(_interfaceInit) is IMethodSymbol implementation)) {
                    return false;
                }
                if (implementation.ExplicitInterfaceImplementations.Length > 0) {
                    implementation = ForwardedInit(implementation.ContainingType, implementation) ?? implementation;
                }
                for (var method = init; method != null; method = method.IsOverride ? method.OverriddenMethod : null) {
                    if (SymbolEqualityComparer.Default.Equals(implementation.OriginalDefinition, method.OriginalDefinition)) {
                        return true;
                    }
                }
                return false;
            }

            // The ordinary Init(World, SharedData) of <type> that its explicit IInitSystem.Init
            // <explicitInit> calls (`Init(...)` or `this.Init(...)`, bound by the compiler), or null.
            // Only a declaration in source can be read: an explicit implementation from another
            // assembly is never taken for forwarding.
            private IMethodSymbol ForwardedInit(INamedTypeSymbol type, IMethodSymbol explicitInit) {
                var ordinary = type.GetMembers("Init").OfType<IMethodSymbol>()
                    .FirstOrDefault(m => m.MethodKind == MethodKind.Ordinary && !m.IsStatic && m.ReturnsVoid && IsInitSignature(m));
                if (ordinary == null) {
                    return null;
                }
                foreach (var reference in explicitInit.OriginalDefinition.DeclaringSyntaxReferences) {
                    var syntax = reference.GetSyntax();
                    SemanticModel model = null;
                    foreach (var invocation in syntax.DescendantNodes().OfType<InvocationExpressionSyntax>()) {
                        bool callsInit = invocation.Expression is IdentifierNameSyntax name && name.Identifier.ValueText == "Init"
                            || invocation.Expression is MemberAccessExpressionSyntax access && access.Expression is ThisExpressionSyntax
                                && access.Name.Identifier.ValueText == "Init";
                        if (!callsInit) {
                            continue;
                        }
                        model ??= _context.Compilation.GetSemanticModel(syntax.SyntaxTree);
                        if (model.GetSymbolInfo(invocation).Symbol is IMethodSymbol called && OverridesOrIs(ordinary, called)) {
                            return ordinary;
                        }
                    }
                }
                return null;
            }

            // <method> is <target> or overrides it (a call inside the class binds to the first
            // declaration of an overridden method).
            private static bool OverridesOrIs(IMethodSymbol method, IMethodSymbol target) {
                for (var current = method; current != null; current = current.IsOverride ? current.OverriddenMethod : null) {
                    if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, target.OriginalDefinition)) {
                        return true;
                    }
                }
                return false;
            }

            private static bool CanBeOverridden(IMethodSymbol init) {
                return (init.IsVirtual || init.IsAbstract || init.IsOverride) && !init.IsSealed
                    && init.DeclaredAccessibility == Accessibility.Public;
            }

            // Why a derived generated Init cannot override this base Init; null when it can.
            // derivedListsInit: the derived class lists IInitSystem itself, so the interface maps to
            // the generated Init, whatever the base's explicit implementation does.
            private string OverrideProblem(INamedTypeSymbol type, IMethodSymbol init, bool derivedListsInit) {
                if (init.ExplicitInterfaceImplementations.Length > 0) {
                    return $"'{type.Name}' implements IInitSystem.Init explicitly";
                }
                if (!init.IsVirtual && !init.IsOverride && !init.IsAbstract) {
                    return $"'{type.Name}.Init' is not virtual";
                }
                if (init.IsSealed) {
                    return $"'{type.Name}.Init' is sealed";
                }
                if (init.DeclaredAccessibility != Accessibility.Public) {
                    return $"'{type.Name}.Init' is not public";
                }
                if (init.IsOverride) {
                    // In source an override may override a generated Init that does not exist yet
                    // (nothing to check); otherwise, like a new virtual Init, it must be on the Init
                    // that IInitSystem.Init reaches.
                    if (derivedListsInit || init.OverriddenMethod == null || ImplementsInitSystem(type, init)) {
                        return null;
                    }
                    return UnreachedOverrideProblem(type, init);
                }
                // A new virtual Init must be the one the interface maps to.
                if (!ImplementsInitSystem(type, init)) {
                    return $"'{type.Name}.Init' does not implement IInitSystem.Init (it hides the Init of its own base)";
                }
                return null;
            }

            // Why the runner never reaches an override of <init>, a hand-written override on <type> of an
            // Init that IInitSystem.Init does not end up in, with the remedy.
            private string UnreachedOverrideProblem(INamedTypeSymbol type, IMethodSymbol init) {
                var first = init;
                while (first.IsOverride && first.OverriddenMethod != null) {
                    first = first.OverriddenMethod;
                }
                string overridden = $"'{first.ContainingType.Name}.Init'";
                var mapped = _interfaceInit == null ? null : type.FindImplementationForInterfaceMember(_interfaceInit) as IMethodSymbol;
                if (mapped != null && mapped.ExplicitInterfaceImplementations.Length > 0) {
                    var owner = mapped.ContainingType;
                    if (!IsInSource(owner)) {
                        return $"'{owner.Name}' implements IInitSystem.Init explicitly, so the runner calls that implementation, and a base from another assembly is not read to see whether it calls {overridden}, which '{type.Name}.Init' overrides; remove the explicit implementation";
                    }
                    bool canCall = owner.GetMembers("Init").OfType<IMethodSymbol>()
                        .Any(m => m.MethodKind == MethodKind.Ordinary && !m.IsStatic && m.ReturnsVoid && IsInitSignature(m));
                    return $"'{owner.Name}' implements IInitSystem.Init explicitly, and that implementation does not call {overridden}, which '{type.Name}.Init' overrides, so the runner never reaches an override of it; "
                        + (canCall ? "call Init(world, shared) from the explicit implementation, or remove it" : "remove the explicit implementation");
                }
                return $"'{type.Name}.Init' overrides {overridden}, which does not implement IInitSystem.Init (it hides the Init of its own base), so the runner never reaches an override of it; make {overridden} override that Init or remove it";
            }

            private static string Remedy(INamedTypeSymbol type, IMethodSymbol init) {
                const string redeclare = "or list IInitSystem among this class's bases and call base.Init(world, shared) from OnInit";
                if (init.ExplicitInterfaceImplementations.Length > 0) {
                    // The interface maps to the explicit one even next to an ordinary Init, which
                    // DeclaredInit takes instead only when the explicit one calls it.
                    if (!type.GetMembers("Init").OfType<IMethodSymbol>().Any(m => m.MethodKind == MethodKind.Ordinary && !m.IsStatic && IsInitSignature(m))) {
                        return "; declare it as 'public virtual void Init(World world, SharedData shared)'";
                    }
                    return IsInSource(type)
                        ? $"; the runner calls it, not '{type.Name}.Init', and it does not call '{type.Name}.Init', so an override of '{type.Name}.Init' would never run; call Init(world, shared) from the explicit implementation, or remove it"
                        : $"; the runner calls it, not '{type.Name}.Init', and a base from another assembly is not read to see whether it calls '{type.Name}.Init'; remove the explicit implementation";
                }
                if (!init.IsVirtual && !init.IsOverride && !init.IsAbstract) {
                    // Private injected fields of a referenced assembly are not visible here, so a
                    // generated base Init from there cannot be told apart from a hand-written one.
                    return IsInSource(type)
                        ? $"; declare it 'public virtual', {redeclare}"
                        : $"; declare it 'public virtual' (if it was generated in another assembly by KenseiECS 2.2.1 or older, which made it virtual only when that assembly had generated subclasses, rebuild that assembly: a generated Init is now virtual unless its class is sealed), {redeclare}";
                }
                // An override that the runner does not reach names its remedy itself (UnreachedOverrideProblem).
                if (init.IsSealed || init.DeclaredAccessibility != Accessibility.Public || init.IsOverride) {
                    return "";
                }
                return "; make it override that Init or remove it";
            }
        }

        // How the generated Init reaches the class's OnInit.
        private enum OnInitMode {
            // `partial void OnInit` is declared and called (the call is dropped when not implemented).
            Declare,
            // The class's own non-partial OnInit(World, SharedData) is called.
            CallExisting,
            // No declaration and no call: the OnInit there belongs to a base.
            None
        }

        private sealed class Plan {
            public INamedTypeSymbol Symbol;
            public TypeDeclarationSyntax Declaration;
            public List<string> Fields;
            public List<INamedTypeSymbol> Containers;
            public bool Ok;
            // Init overrides an inherited virtual Init.
            public bool Overrides;
            // The generated Init calls base.Init between its own fields and OnInit.
            public bool CallsBase;
            // The base whose Init the generated Init overrides (when Overrides).
            public INamedTypeSymbol ChainTarget;
            // The 2.1 layout: the base whose Init cannot be overridden, which the generated Init
            // replaces, and why it cannot be overridden.
            public INamedTypeSymbol ReplacedInit;
            public string ReplacedInitProblem;
            // A 2.1-style Init that hides an inherited one.
            public bool HidesBase;
            public OnInitMode OnInit;
            // The declared partial OnInit hides an inherited one (CS0108/CS0114 silenced).
            public bool HidesOnInit;
        }

        // Per-class checks that do not depend on the base class.
        private static Plan Analyze(GeneratorExecutionContext context, INamedTypeSymbol symbol, TypeDeclarationSyntax classDecl) {
            var plan = new Plan { Symbol = symbol, Declaration = classDecl };

            if (!(classDecl is ClassDeclarationSyntax)) {
                string kind = classDecl is RecordDeclarationSyntax ? "record" : "struct";
                context.ReportDiagnostic(Diagnostic.Create(NotAClass, classDecl.Identifier.GetLocation(), symbol.Name, kind));
                return plan;
            }

            plan.Fields = CollectFields(context, symbol);
            if (plan.Fields.Count == 0) {
                return plan;
            }

            if (!classDecl.Modifiers.Any(SyntaxKind.PartialKeyword)) {
                context.ReportDiagnostic(Diagnostic.Create(NotPartial, classDecl.Identifier.GetLocation(), symbol.Name));
                return plan;
            }

            foreach (var member in symbol.GetMembers().OfType<IMethodSymbol>()) {
                bool namedInit = member.Name == "Init" && member.Parameters.Length == 2;
                bool explicitInit = member.ExplicitInterfaceImplementations.Any(m => m.ContainingType.ToDisplayString() == "KenseiECS.IInitSystem");
                if ((namedInit || explicitInit) && !member.IsImplicitlyDeclared && member.Locations.Any(l => l.IsInSource)) {
                    context.ReportDiagnostic(Diagnostic.Create(InitAlreadyDeclared, member.Locations[0], symbol.Name));
                    return plan;
                }
            }

            plan.Containers = new List<INamedTypeSymbol>();
            for (var outer = symbol.ContainingType; outer != null; outer = outer.ContainingType) {
                string unsupported = UnsupportedContainerKind(outer);
                if (unsupported != null) {
                    context.ReportDiagnostic(Diagnostic.Create(UnsupportedContainer, classDecl.Identifier.GetLocation(), symbol.Name, outer.Name, unsupported));
                    return plan;
                }
                if (!IsDeclaredPartial(outer)) {
                    context.ReportDiagnostic(Diagnostic.Create(ContainerNotPartial, classDecl.Identifier.GetLocation(), symbol.Name, outer.Name));
                    return plan;
                }
                plan.Containers.Insert(0, outer);
            }

            plan.Ok = true;
            return plan;
        }

        private static void Emit(GeneratorExecutionContext context, Plan plan, string hintName) {
            var symbol = plan.Symbol;
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated by KenseiECS.Generators />");
            sb.AppendLine("#nullable disable");

            string ns = symbol.ContainingNamespace.IsGlobalNamespace ? null : symbol.ContainingNamespace.ToDisplayString(NamespaceFormat);
            int indent = 0;
            if (ns != null) {
                sb.AppendLine($"namespace {ns} {{");
                indent++;
            }

            foreach (var container in plan.Containers) {
                string modifiers = container.IsStatic ? "static partial" : "partial";
                sb.AppendLine($"{Pad(indent)}{modifiers} {Keyword(container)} {Identifier(container.Name)}{TypeParameters(container)} {{");
                indent++;
            }

            // An overriding Init already gets IInitSystem through the base class.
            string baseList = plan.Overrides || IsInitSystem(symbol) ? "" : " : global::KenseiECS.IInitSystem";
            sb.AppendLine($"{Pad(indent)}partial class {Identifier(symbol.Name)}{TypeParameters(symbol)}{baseList} {{");
            indent++;

            // Virtual in every class that can have subclasses, so the form of Init does not depend
            // on which subclasses this compilation happens to contain.
            bool open = !symbol.IsSealed;
            string signature = plan.Overrides ? "public override void"
                : plan.HidesBase ? (open ? "public new virtual void" : "public new void")
                : open ? "public virtual void"
                : "public void";
            if (plan.OnInit == OnInitMode.Declare) {
                if (plan.HidesOnInit) {
                    // A partial method without an access modifier cannot be declared `new`.
                    sb.AppendLine("#pragma warning disable CS0108, CS0114");
                }
                sb.AppendLine($"{Pad(indent)}partial void OnInit(global::KenseiECS.World world, global::KenseiECS.SharedData shared);");
                if (plan.HidesOnInit) {
                    sb.AppendLine("#pragma warning restore CS0108, CS0114");
                }
                sb.AppendLine();
            }
            sb.AppendLine($"{Pad(indent)}{signature} Init(global::KenseiECS.World world, global::KenseiECS.SharedData shared) {{");
            indent++;
            // Own fields first: they need only world and shared, and the base Init (its OnInit,
            // virtual hooks it calls) then sees them, as with the 2.1 base.Init call in OnInit.
            foreach (var line in plan.Fields) {
                sb.AppendLine($"{Pad(indent)}{line}");
            }
            if (plan.CallsBase) {
                sb.AppendLine($"{Pad(indent)}base.Init(world, shared);");
            }
            if (plan.OnInit != OnInitMode.None) {
                sb.AppendLine($"{Pad(indent)}OnInit(world, shared);");
            }
            indent--;
            sb.AppendLine($"{Pad(indent)}}}");

            while (indent > 0) {
                indent--;
                sb.AppendLine($"{Pad(indent)}}}");
            }

            context.AddSource(hintName, SourceText.From(sb.ToString(), Encoding.UTF8));
        }

        /// <summary>
        /// Hint names are compared without case (Roslyn), so Game.AI.Move and Game.Ai.Move would
        /// collide and the generator would fail as a whole (CS8785), leaving every system of the
        /// assembly without Init. Names equal without case get a numeric segment in ordinal order
        /// of their full names, so the result does not depend on the order of the files. A type or
        /// namespace name cannot start with a digit, so the segment cannot meet another name.
        /// </summary>
        private static Dictionary<Plan, string> HintNames(List<Plan> plans) {
            var names = new Dictionary<Plan, string>();
            foreach (var group in plans.GroupBy(p => HintBase(p.Symbol), StringComparer.OrdinalIgnoreCase)) {
                var ordered = group
                    .OrderBy(p => HintBase(p.Symbol), StringComparer.Ordinal)
                    .ThenBy(p => p.Symbol.ToDisplayString(Qualified), StringComparer.Ordinal)
                    .ToList();
                for (int i = 0; i < ordered.Count; i++) {
                    string name = HintBase(ordered[i].Symbol);
                    names[ordered[i]] = (i == 0 ? name : name + "." + (i + 1)) + ".Injection.g.cs";
                }
            }
            return names;
        }

        // Namespace.Outer.Type_T_ without '@' (not allowed in a hint name) and without '<', '>'.
        private static string HintBase(INamedTypeSymbol symbol) {
            var parts = new List<string>();
            for (var type = symbol; type != null; type = type.ContainingType) {
                string typeParameters = type.TypeParameters.Length == 0 ? "" : "_" + string.Join("__", type.TypeParameters.Select(p => p.Name)) + "_";
                parts.Insert(0, type.Name + typeParameters);
            }
            for (var ns = symbol.ContainingNamespace; ns != null && !ns.IsGlobalNamespace; ns = ns.ContainingNamespace) {
                parts.Insert(0, ns.Name);
            }
            return string.Join(".", parts);
        }

        private static bool IsInSource(INamedTypeSymbol type) {
            return type.DeclaringSyntaxReferences.Length > 0;
        }

        private static List<string> CollectFields(GeneratorExecutionContext context, INamedTypeSymbol symbol) {
            var lines = new List<string>();
            foreach (var field in symbol.GetMembers().OfType<IFieldSymbol>()) {
                if (field.IsStatic) {
                    continue;
                }

                AttributeData inc = null, exc = null, any = null, pool = null, group = null, shared = null;
                foreach (var attribute in field.GetAttributes()) {
                    string name = attribute.AttributeClass?.ToDisplayString();
                    switch (name) {
                        case IncAttribute: inc = attribute; break;
                        case ExcAttribute: exc = attribute; break;
                        case AnyAttribute: any = attribute; break;
                        case PoolAttribute: pool = attribute; break;
                        case GroupAttribute: group = attribute; break;
                        case SharedAttribute: shared = attribute; break;
                    }
                }

                if (inc == null && exc == null && any == null && pool == null && group == null && shared == null) {
                    continue;
                }
                if (field.IsReadOnly) {
                    context.ReportDiagnostic(Diagnostic.Create(ReadonlyField, field.Locations[0], field.Name));
                    continue;
                }

                string target = "this." + Identifier(field.Name);
                if (inc != null || exc != null || any != null) {
                    if (field.Type.ToDisplayString(Unannotated) != "KenseiECS.Filter") {
                        Report(context, field, WrongFieldType, "Inc/Exc/Any", "KenseiECS.Filter");
                        continue;
                    }
                    if (inc == null && any == null) {
                        context.ReportDiagnostic(Diagnostic.Create(FilterWithoutInc, field.Locations[0], field.Name));
                        continue;
                    }
                    if (TypeCount(inc) + TypeCount(any) == 0) {
                        context.ReportDiagnostic(Diagnostic.Create(FilterWithoutTypes, field.Locations[0], field.Name));
                        continue;
                    }
                    if (!CheckComponentTypes(context, field, "Inc", inc)
                        || !CheckComponentTypes(context, field, "Exc", exc)
                        || !CheckComponentTypes(context, field, "Any", any)) {
                        continue;
                    }
                    var call = new StringBuilder("world.Filter()");
                    AppendTypeCalls(call, "Inc", inc);
                    AppendTypeCalls(call, "Exc", exc);
                    AppendTypeCalls(call, "Any", any);
                    call.Append(".End()");
                    lines.Add($"{target} = {call};");
                } else if (pool != null) {
                    if (!(field.Type is INamedTypeSymbol named) || named.ConstructedFrom.ToDisplayString() != "KenseiECS.ComponentPool<T>") {
                        Report(context, field, WrongFieldType, "Pool", "KenseiECS.ComponentPool<T>");
                        continue;
                    }
                    lines.Add($"{target} = world.Pool<{named.TypeArguments[0].ToDisplayString(Qualified)}>();");
                } else if (group != null) {
                    if (!(field.Type is INamedTypeSymbol named) || !named.ConstructedFrom.ToDisplayString().StartsWith("KenseiECS.Group<")) {
                        Report(context, field, WrongFieldType, "Group", "KenseiECS.Group<...>");
                        continue;
                    }
                    string args = string.Join(", ", named.TypeArguments.Select(t => t.ToDisplayString(Qualified)));
                    lines.Add($"{target} = world.Group<{args}>();");
                } else if (shared != null) {
                    if (!field.Type.IsReferenceType) {
                        Report(context, field, WrongFieldType, "Shared", "a reference type");
                        continue;
                    }
                    string key = shared.ConstructorArguments.Length > 0 && shared.ConstructorArguments[0].Value is string s
                        ? SymbolDisplay.FormatLiteral(s, quote: true)
                        : null;
                    string typeName = field.Type.ToDisplayString(Qualified);
                    lines.Add(key == null
                        ? $"{target} = shared.Get<{typeName}>();"
                        : $"{target} = shared.Get<{typeName}>({key});");
                }
            }
            return lines;
        }

        private static void AppendTypeCalls(StringBuilder call, string method, AttributeData attribute) {
            if (attribute == null || attribute.ConstructorArguments.Length == 0 || attribute.ConstructorArguments[0].IsNull) {
                return;
            }
            foreach (var value in attribute.ConstructorArguments[0].Values) {
                if (value.Value is ITypeSymbol type) {
                    call.Append('.').Append(method).Append('<').Append(type.ToDisplayString(Qualified)).Append(">()");
                }
            }
        }

        private static int TypeCount(AttributeData attribute) {
            return attribute == null || attribute.ConstructorArguments.Length == 0 || attribute.ConstructorArguments[0].IsNull
                ? 0
                : attribute.ConstructorArguments[0].Values.Count(v => v.Value is ITypeSymbol);
        }

        // Inc/Exc/Any<T> require `struct, IComponent`; report here instead of a constraint error in the .g.cs.
        // An error type may come from another generator, so leave it to the compiler.
        private static bool CheckComponentTypes(GeneratorExecutionContext context, IFieldSymbol field, string method, AttributeData attribute) {
            if (attribute == null || attribute.ConstructorArguments.Length == 0 || attribute.ConstructorArguments[0].IsNull) {
                return true;
            }
            foreach (var value in attribute.ConstructorArguments[0].Values) {
                if (value.Value is ITypeSymbol type && type.TypeKind != TypeKind.Error && !(type.IsValueType && type.AllInterfaces.Any(i => i.ToDisplayString() == "KenseiECS.IComponent"))) {
                    context.ReportDiagnostic(Diagnostic.Create(NotAComponent, field.Locations[0], field.Name, type.ToDisplayString(), method));
                    return false;
                }
            }
            return true;
        }

        private static bool IsInitSystem(INamedTypeSymbol type) {
            return type.AllInterfaces.Any(IsInitSystemInterface);
        }

        // The class (re)implements IInitSystem itself: it lists it, or an interface that extends it.
        private static bool ListsInitSystem(INamedTypeSymbol type) {
            return type.Interfaces.Any(i => IsInitSystemInterface(i) || i.AllInterfaces.Any(IsInitSystemInterface));
        }

        private static bool IsInitSystemInterface(INamedTypeSymbol type) {
            return type.ToDisplayString() == "KenseiECS.IInitSystem";
        }

        // Same signature as IInitSystem.Init: Init(World, SharedData), by value, not generic.
        private static bool IsInitSignature(IMethodSymbol method) {
            return method.Name == "Init" && HasWorldAndShared(method);
        }

        // Same signature as the generated hook: OnInit(World, SharedData), by value, not generic.
        private static bool IsHookSignature(IMethodSymbol method) {
            return method.Name == "OnInit" && method.MethodKind == MethodKind.Ordinary && HasWorldAndShared(method);
        }

        private static bool HasWorldAndShared(IMethodSymbol method) {
            return method.TypeParameters.Length == 0 && method.Parameters.Length == 2
                && method.Parameters[0].RefKind == RefKind.None && method.Parameters[1].RefKind == RefKind.None
                && method.Parameters[0].Type.ToDisplayString(Unannotated) == "KenseiECS.World"
                && method.Parameters[1].Type.ToDisplayString(Unannotated) == "KenseiECS.SharedData";
        }

        // `partial void OnInit(...) { ... }` written by the user: the implementation of the
        // generated declaration (a partial method with an access modifier is not that one).
        private static bool IsGeneratedHookImplementation(IMethodSymbol method) {
            foreach (var reference in method.DeclaringSyntaxReferences) {
                if (reference.GetSyntax() is MethodDeclarationSyntax declaration
                    && declaration.Modifiers.Any(SyntaxKind.PartialKeyword)
                    && !declaration.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword) || m.IsKind(SyntaxKind.PrivateKeyword)
                        || m.IsKind(SyntaxKind.ProtectedKeyword) || m.IsKind(SyntaxKind.InternalKeyword))) {
                    return true;
                }
            }
            return false;
        }

        private static bool IsNestedIn(INamedTypeSymbol type, INamedTypeSymbol container) {
            for (var outer = type.ContainingType; outer != null; outer = outer.ContainingType) {
                if (SymbolEqualityComparer.Default.Equals(outer.OriginalDefinition, container)) {
                    return true;
                }
            }
            return false;
        }

        // Every `base.Init` member access in the class's own declarations; nested types have their own base.
        private static IEnumerable<MemberAccessExpressionSyntax> BaseInitAccesses(INamedTypeSymbol type) {
            foreach (var reference in type.DeclaringSyntaxReferences) {
                var root = reference.GetSyntax();
                var nodes = root.DescendantNodes(n => n == root || !(n is BaseTypeDeclarationSyntax));
                foreach (var access in nodes.OfType<MemberAccessExpressionSyntax>()) {
                    if (access.Expression is BaseExpressionSyntax && access.Name.Identifier.ValueText == "Init") {
                        yield return access;
                    }
                }
            }
        }

        private static bool CallsBaseInit(INamedTypeSymbol type) {
            return BaseInitAccesses(type).Any();
        }

        // Whether a call with a World and a SharedData argument, in that order, could bind to
        // <method>. Constraints on method type parameters are not checked, so a doubtful overload
        // counts; a type parameter that no argument can infer makes the method inapplicable.
        private static bool AcceptsInitArguments(Compilation compilation, IMethodSymbol method, ITypeSymbol world, ITypeSymbol shared) {
            var parameters = method.Parameters;
            var argumentTypes = new[] { world, shared };
            if (Applicable(expanded: false)) {
                return true;
            }
            return parameters.Length > 0 && parameters[parameters.Length - 1].IsParams && Applicable(expanded: true);

            bool Applicable(bool expanded) {
                int fixedCount = expanded ? parameters.Length - 1 : parameters.Length;
                if (expanded ? fixedCount > 2 : fixedCount < 2) {
                    return false;
                }
                var receiving = new List<ITypeSymbol>();
                for (int i = 0; i < fixedCount; i++) {
                    var parameter = parameters[i];
                    if (i >= 2) {
                        // Normal form: the parameters after the two arguments must be optional.
                        if (!parameter.IsOptional && !parameter.IsParams) {
                            return false;
                        }
                        continue;
                    }
                    if (!Accepts(parameter.RefKind, parameter.Type, argumentTypes[i])) {
                        return false;
                    }
                    receiving.Add(parameter.Type);
                }
                if (expanded) {
                    var last = parameters[parameters.Length - 1];
                    if (!(last.Type is IArrayTypeSymbol array)) {
                        return false;
                    }
                    for (int i = fixedCount; i < 2; i++) {
                        if (!Accepts(RefKind.None, array.ElementType, argumentTypes[i])) {
                            return false;
                        }
                        receiving.Add(array.ElementType);
                    }
                }
                // Type inference has only the two arguments to go by.
                return method.TypeParameters.All(p => receiving.Any(t => Mentions(t, p)));
            }

            bool Accepts(RefKind refKind, ITypeSymbol parameterType, ITypeSymbol argumentType) {
                if (refKind != RefKind.None && refKind != RefKind.In) {
                    return false;
                }
                if (ContainsTypeParameter(parameterType)) {
                    return true;
                }
                var conversion = compilation.ClassifyCommonConversion(argumentType, parameterType);
                return conversion.Exists && conversion.IsImplicit;
            }
        }

        private static bool ContainsTypeParameter(ITypeSymbol type) {
            switch (type) {
                case ITypeParameterSymbol parameter:
                    return parameter.TypeParameterKind == TypeParameterKind.Method;
                case IArrayTypeSymbol array:
                    return ContainsTypeParameter(array.ElementType);
                case INamedTypeSymbol named:
                    return named.TypeArguments.Any(ContainsTypeParameter);
                default:
                    return false;
            }
        }

        private static bool Mentions(ITypeSymbol type, ITypeParameterSymbol parameter) {
            switch (type) {
                case ITypeParameterSymbol other:
                    return SymbolEqualityComparer.Default.Equals(other, parameter);
                case IArrayTypeSymbol array:
                    return Mentions(array.ElementType, parameter);
                case INamedTypeSymbol named:
                    return named.TypeArguments.Any(t => Mentions(t, parameter));
                default:
                    return false;
            }
        }

        private static bool IsInjectionAttribute(AttributeData attribute) {
            switch (attribute.AttributeClass?.ToDisplayString()) {
                case IncAttribute:
                case ExcAttribute:
                case AnyAttribute:
                case PoolAttribute:
                case GroupAttribute:
                case SharedAttribute:
                    return true;
                default:
                    return false;
            }
        }

        private static bool HasInjectedFields(INamedTypeSymbol type) {
            foreach (var field in type.GetMembers().OfType<IFieldSymbol>()) {
                if (!field.IsStatic && field.GetAttributes().Any(IsInjectionAttribute)) {
                    return true;
                }
            }
            return false;
        }

        // A base in this compilation gets IInitSystem only from this generator run, which the
        // compilation does not see yet, so look at the injected fields themselves.
        private static bool InheritsInjectedFields(INamedTypeSymbol type) {
            for (var current = type; current != null && current.SpecialType != SpecialType.System_Object; current = current.BaseType) {
                if (HasInjectedFields(current)) {
                    return true;
                }
            }
            return false;
        }

        private static string UnsupportedContainerKind(INamedTypeSymbol type) {
            if (type.TypeKind == TypeKind.Interface) {
                return "interface";
            }
            foreach (var reference in type.DeclaringSyntaxReferences) {
                if (reference.GetSyntax() is RecordDeclarationSyntax) {
                    return "record";
                }
            }
            return null;
        }

        private static void Report(GeneratorExecutionContext context, IFieldSymbol field, DiagnosticDescriptor descriptor, string attribute, string expected) {
            context.ReportDiagnostic(Diagnostic.Create(descriptor, field.Locations[0], field.Name, attribute, field.Type.ToDisplayString(), expected));
        }

        private static bool IsDeclaredPartial(INamedTypeSymbol type) {
            foreach (var reference in type.DeclaringSyntaxReferences) {
                if (reference.GetSyntax() is TypeDeclarationSyntax declaration && declaration.Modifiers.Any(SyntaxKind.PartialKeyword)) {
                    return true;
                }
            }
            return false;
        }

        private static string Keyword(INamedTypeSymbol type) {
            return type.TypeKind == TypeKind.Struct ? "struct" : "class";
        }

        // A name as written in code: a reserved keyword gets '@' (@event, @class).
        private static string Identifier(string name) {
            return SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;
        }

        private static string TypeParameters(INamedTypeSymbol type) {
            return type.TypeParameters.Length == 0
                ? ""
                : "<" + string.Join(", ", type.TypeParameters.Select(p => Identifier(p.Name))) + ">";
        }

        private static string Pad(int indent) {
            return new string(' ', indent * 4);
        }

        private sealed class Receiver : ISyntaxReceiver {
            private static readonly HashSet<string> Names = new() {
                "Inc", "Exc", "Any", "Pool", "Group", "Shared",
                "IncAttribute", "ExcAttribute", "AnyAttribute", "PoolAttribute", "GroupAttribute", "SharedAttribute"
            };

            public List<TypeDeclarationSyntax> Candidates { get; } = new();

            // Classes with a hand-written `override ... Init(...)`: checked for KECS013.
            public List<ClassDeclarationSyntax> InitOverrides { get; } = new();

            public void OnVisitSyntaxNode(SyntaxNode node) {
                if (node is MethodDeclarationSyntax method) {
                    if (method.Identifier.Text == "Init" && method.ParameterList.Parameters.Count == 2
                        && method.Modifiers.Any(SyntaxKind.OverrideKeyword) && method.Parent is ClassDeclarationSyntax owner) {
                        InitOverrides.Add(owner);
                    }
                    return;
                }
                if (!(node is FieldDeclarationSyntax field) || field.AttributeLists.Count == 0) {
                    return;
                }
                foreach (var list in field.AttributeLists) {
                    foreach (var attribute in list.Attributes) {
                        string name = attribute.Name is QualifiedNameSyntax q ? q.Right.Identifier.Text : attribute.Name.ToString();
                        if (Names.Contains(name) && field.Parent is TypeDeclarationSyntax owner) {
                            Candidates.Add(owner);
                            return;
                        }
                    }
                }
            }
        }
    }
}
