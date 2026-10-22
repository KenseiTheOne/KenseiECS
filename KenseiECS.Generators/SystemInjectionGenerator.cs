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
    /// Inheritance: when a system with injected fields derives from another one, the base's
    /// generated Init becomes virtual and the derived one overrides it and always calls base.Init
    /// first, so the base fields and base OnInit come before the derived ones. A hand-written
    /// base Init is chained the same way when it is public virtual (or abstract, then without
    /// the base call). A base.Init written in such a class is reported (KECS012): it would run
    /// the base twice. Classes without overriding subclasses keep a plain non-virtual Init. A
    /// hand-written subclass that overrides Init (directly or through hand-written overriding
    /// intermediates) also makes the generated base Init virtual. A base Init that cannot be
    /// overridden, or a hand-written Init overload on a class in between that would take the
    /// generated base.Init(world, shared) call, is reported (KECS006); over a base Init that
    /// cannot be overridden, a class that lists IInitSystem itself keeps the 2.1 layout (its own
    /// Init re-implements the interface and OnInit calls base.Init).
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
            "Class '{0}' calls base.Init, but its generated Init already calls base.Init first; remove the call from OnInit",
            "KenseiECS", DiagnosticSeverity.Error, true);

        private static readonly SymbolDisplayFormat Qualified =SymbolDisplayFormat.FullyQualifiedFormat;

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

            // A hand-written class that overrides Init (directly or through hand-written
            // intermediates) of a generated base needs that base Init to be virtual even when no
            // generated class derives from it.
            foreach (var classDecl in receiver.InitOverrides) {
                var model = context.Compilation.GetSemanticModel(classDecl.SyntaxTree);
                if (model.GetDeclaredSymbol(classDecl) is INamedTypeSymbol symbol && symbol.BaseType != null) {
                    run.MarkGeneratedBaseOverridden(symbol.BaseType);
                }
            }

            // Emitted only after every class is resolved: a base learns that its Init must be
            // virtual from its derived classes.
            foreach (var plan in run.Plans) {
                if (plan.Ok) {
                    Emit(context, plan);
                }
            }
        }

        /// <summary>
        /// One generator pass. Plans are created on demand, base classes first, so a derived
        /// system always knows whether its base in this compilation gets a generated Init.
        /// </summary>
        private sealed class Run {
            private readonly GeneratorExecutionContext _context;
            private readonly Dictionary<INamedTypeSymbol, Plan> _plans = new(SymbolEqualityComparer.Default);

            public readonly List<Plan> Plans = new();

            public Run(GeneratorExecutionContext context) {
                _context = context;
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
                    plan.HidesBase = HasInheritedInit(baseType);
                    return;
                }

                // Otherwise the generated Init overrides the base Init and calls it first, so the
                // base fields and the base OnInit are done before the derived ones.
                for (var current = baseType; current != null && current.SpecialType != SpecialType.System_Object; current = current.BaseType) {
                    if (IsInSource(current) && HasInjectedFields(current)) {
                        var basePlan = GetPlan(current);
                        if (basePlan == null || !basePlan.Ok) {
                            // The base reported its own error; a second one here adds nothing.
                            plan.Ok = false;
                            return;
                        }
                        basePlan.Overridden = true;
                        Chain(plan, current, callsBase: true);
                        return;
                    }

                    var init = DeclaredInit(current);
                    if (init == null) {
                        continue;
                    }
                    string problem = OverrideProblem(current, init);
                    if (problem != null) {
                        // The 2.1 layout, the only one possible over an Init that cannot be
                        // overridden: a class that lists IInitSystem itself re-implements the
                        // interface, and its OnInit is responsible for calling base.Init.
                        if (symbol.Interfaces.Any(IsInitSystemInterface)) {
                            plan.HidesBase = HasInheritedInit(baseType);
                        } else {
                            Fail(plan, current, problem);
                        }
                        return;
                    }
                    if (!Chain(plan, current, callsBase: !init.IsAbstract)) {
                        return;
                    }
                    // A hand-written intermediate override may sit on a generated Init further up;
                    // that one must then be virtual too.
                    if (init.IsOverride && IsInSource(current) && current.BaseType != null) {
                        MarkGeneratedBaseOverridden(current.BaseType);
                    }
                    return;
                }
                Fail(plan, baseType, "no base class declares the Init(World, SharedData) that implements IInitSystem");
            }

            // Makes the generated Init an override of the Init on <target>. Returns false when the
            // generated base call cannot reach it (reported).
            private bool Chain(Plan plan, INamedTypeSymbol target, bool callsBase) {
                plan.Overrides = true;
                plan.CallsBase = callsBase;
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
            /// The generated Init already calls base.Init first, so a <c>base.Init</c> written in
            /// the class would run the base Init (fields and OnInit) a second time. Purely
            /// syntactic: any <c>base.Init</c> member access in the class's own declarations
            /// counts, whatever overload it would bind to. Nested types are skipped, their
            /// <c>base</c> is another class. The class is still emitted, so no follow-up errors
            /// about a missing Init appear next to this one.
            /// </summary>
            private void ReportBaseInitCalls(Plan plan) {
                foreach (var reference in plan.Symbol.DeclaringSyntaxReferences) {
                    var root = reference.GetSyntax();
                    var nodes = root.DescendantNodes(n => n == root || !(n is BaseTypeDeclarationSyntax));
                    foreach (var access in nodes.OfType<MemberAccessExpressionSyntax>()) {
                        if (access.Expression is BaseExpressionSyntax && access.Name.Identifier.ValueText == "Init") {
                            _context.ReportDiagnostic(Diagnostic.Create(BaseInitCalled, access.GetLocation(), plan.Symbol.Name));
                        }
                    }
                }
            }

            /// <summary>
            /// The emitted <c>base.Init(world, shared)</c> is bound by C# overload resolution, which
            /// takes the applicable Init declared in the most derived base. A hand-written Init
            /// overload on a class between <paramref name="plan"/> and <paramref name="target"/>
            /// (the base whose Init must be chained) would silently take the call and the chained
            /// Init would never run, so that is reported instead. Returns false when reported.
            /// </summary>
            private bool FailIfBaseCallCaptured(Plan plan, INamedTypeSymbol target) {
                var compilation = _context.Compilation;
                var world = compilation.GetTypeByMetadataName("KenseiECS.World");
                var shared = compilation.GetTypeByMetadataName("KenseiECS.SharedData");
                if (world == null || shared == null) {
                    return true;
                }
                for (var current = plan.Symbol.BaseType; current != null && !SymbolEqualityComparer.Default.Equals(current, target); current = current.BaseType) {
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

            /// <summary>
            /// Walks up from <paramref name="start"/> past hand-written overriding Inits and marks
            /// the first in-source base with a generated Init as overridden (virtual). Stops at any
            /// other declared Init: the chain is then not rooted in generated code.
            /// </summary>
            public void MarkGeneratedBaseOverridden(INamedTypeSymbol start) {
                for (var current = start; current != null && current.SpecialType != SpecialType.System_Object; current = current.BaseType) {
                    if (IsInSource(current) && HasInjectedFields(current)) {
                        var basePlan = GetPlan(current);
                        if (basePlan != null && basePlan.Ok) {
                            basePlan.Overridden = true;
                        }
                        return;
                    }
                    var init = DeclaredInit(current);
                    if (init == null) {
                        continue;
                    }
                    if (!init.IsOverride || !IsInSource(current)) {
                        return;
                    }
                }
            }

            private void Fail(Plan plan, INamedTypeSymbol baseType, string problem) {
                _context.ReportDiagnostic(Diagnostic.Create(InheritedInjection, plan.Declaration.Identifier.GetLocation(), plan.Symbol.Name, baseType.Name, problem));
                plan.Ok = false;
            }

            // Whether a 2.1-style Init (no override) hides an inherited one and needs `new`.
            private bool HasInheritedInit(INamedTypeSymbol baseType) {
                for (var current = baseType; current != null && current.SpecialType != SpecialType.System_Object; current = current.BaseType) {
                    if (IsInSource(current) && HasInjectedFields(current) && GetPlan(current)?.Ok == true) {
                        return true;
                    }
                    var init = DeclaredInit(current);
                    if (init != null && init.ExplicitInterfaceImplementations.Length == 0 && init.DeclaredAccessibility != Accessibility.Private) {
                        return true;
                    }
                }
                return false;
            }
        }

        private sealed class Plan {
            public INamedTypeSymbol Symbol;
            public TypeDeclarationSyntax Declaration;
            public List<string> Fields;
            public List<INamedTypeSymbol> Containers;
            public bool Ok;
            // Init overrides an inherited virtual Init.
            public bool Overrides;
            // The generated Init calls base.Init before its own fields.
            public bool CallsBase;
            // A 2.1-style Init that hides an inherited one.
            public bool HidesBase;
            // A derived class in this compilation overrides this Init, so it must be virtual.
            public bool Overridden;
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

        private static void Emit(GeneratorExecutionContext context, Plan plan) {
            var symbol = plan.Symbol;
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated by KenseiECS.Generators />");
            sb.AppendLine("#nullable disable");

            string ns = symbol.ContainingNamespace.IsGlobalNamespace ? null : symbol.ContainingNamespace.ToDisplayString();
            int indent = 0;
            if (ns != null) {
                sb.AppendLine($"namespace {ns} {{");
                indent++;
            }

            foreach (var container in plan.Containers) {
                string modifiers = container.IsStatic ? "static partial" : "partial";
                sb.AppendLine($"{Pad(indent)}{modifiers} {Keyword(container)} {container.Name}{TypeParameters(container)} {{");
                indent++;
            }

            // An overriding Init already gets IInitSystem through the base class.
            string baseList = plan.Overrides || IsInitSystem(symbol) ? "" : " : global::KenseiECS.IInitSystem";
            sb.AppendLine($"{Pad(indent)}partial class {symbol.Name}{TypeParameters(symbol)}{baseList} {{");
            indent++;

            // A system without generated subclasses keeps the plain `public void Init` of 2.1.
            string signature = plan.Overrides ? "public override void"
                : plan.HidesBase ? (plan.Overridden ? "public new virtual void" : "public new void")
                : plan.Overridden ? "public virtual void"
                : "public void";
            sb.AppendLine($"{Pad(indent)}partial void OnInit(global::KenseiECS.World world, global::KenseiECS.SharedData shared);");
            sb.AppendLine();
            sb.AppendLine($"{Pad(indent)}{signature} Init(global::KenseiECS.World world, global::KenseiECS.SharedData shared) {{");
            indent++;
            if (plan.CallsBase) {
                sb.AppendLine($"{Pad(indent)}base.Init(world, shared);");
            }
            foreach (var line in plan.Fields) {
                sb.AppendLine($"{Pad(indent)}{line}");
            }
            sb.AppendLine($"{Pad(indent)}OnInit(world, shared);");
            indent--;
            sb.AppendLine($"{Pad(indent)}}}");

            while (indent > 0) {
                indent--;
                sb.AppendLine($"{Pad(indent)}}}");
            }

            string hint = symbol.ToDisplayString().Replace('<', '_').Replace('>', '_').Replace(',', '_').Replace(' ', '_');
            context.AddSource($"{hint}.Injection.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
        }

        // The Init(World, SharedData) a type declares itself: an ordinary one or an explicit IInitSystem.Init.
        private static IMethodSymbol DeclaredInit(INamedTypeSymbol type) {
            foreach (var method in type.GetMembers().OfType<IMethodSymbol>()) {
                if (method.IsStatic || method.MethodKind == MethodKind.Constructor) {
                    continue;
                }
                if (method.ExplicitInterfaceImplementations.Any(m => IsInitSystemInterface(m.ContainingType))) {
                    return method;
                }
                if (method.Name == "Init" && method.Parameters.Length == 2
                    && method.Parameters[0].Type.ToDisplayString(Unannotated) == "KenseiECS.World"
                    && method.Parameters[1].Type.ToDisplayString(Unannotated) == "KenseiECS.SharedData") {
                    return method;
                }
            }
            return null;
        }

        // Why a derived generated Init cannot override this base Init; null when it can.
        private static string OverrideProblem(INamedTypeSymbol type, IMethodSymbol init) {
            const string redeclare = "or list IInitSystem among this class's bases and call base.Init(world, shared) from OnInit";
            if (init.ExplicitInterfaceImplementations.Length > 0) {
                return $"'{type.Name}' implements IInitSystem.Init explicitly; declare it as 'public virtual void Init(World world, SharedData shared)'";
            }
            if (!init.IsVirtual && !init.IsOverride && !init.IsAbstract) {
                // Private injected fields of a referenced assembly are not visible here, so a
                // generated base Init from there cannot be told apart from a hand-written one.
                return IsInSource(type)
                    ? $"'{type.Name}.Init' is not virtual; declare it 'public virtual', {redeclare}"
                    : $"'{type.Name}.Init' is not virtual; if it is generated in another assembly, keep systems that inherit injected fields in one assembly (a generated Init is virtual only when that assembly has generated subclasses of it), {redeclare}";
            }
            if (init.IsSealed) {
                return $"'{type.Name}.Init' is sealed";
            }
            if (init.DeclaredAccessibility != Accessibility.Public) {
                return $"'{type.Name}.Init' is not public";
            }
            // Only checked in source: a referenced generated base may get IInitSystem from its own base.
            if (IsInSource(type) && !init.IsOverride && !type.Interfaces.Any(IsInitSystemInterface)) {
                return $"'{type.Name}.Init' does not implement IInitSystem.Init (it hides the Init of its own base); make it override that Init or remove it";
            }
            return null;
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
                    lines.Add($"this.{field.Name} = {call};");
                } else if (pool != null) {
                    if (!(field.Type is INamedTypeSymbol named) || named.ConstructedFrom.ToDisplayString() != "KenseiECS.ComponentPool<T>") {
                        Report(context, field, WrongFieldType, "Pool", "KenseiECS.ComponentPool<T>");
                        continue;
                    }
                    lines.Add($"this.{field.Name} = world.Pool<{named.TypeArguments[0].ToDisplayString(Qualified)}>();");
                } else if (group != null) {
                    if (!(field.Type is INamedTypeSymbol named) || !named.ConstructedFrom.ToDisplayString().StartsWith("KenseiECS.Group<")) {
                        Report(context, field, WrongFieldType, "Group", "KenseiECS.Group<...>");
                        continue;
                    }
                    string args = string.Join(", ", named.TypeArguments.Select(t => t.ToDisplayString(Qualified)));
                    lines.Add($"this.{field.Name} = world.Group<{args}>();");
                } else if (shared != null) {
                    if (!field.Type.IsReferenceType) {
                        Report(context, field, WrongFieldType, "Shared", "a reference type");
                        continue;
                    }
                    string key = shared.ConstructorArguments.Length > 0 && shared.ConstructorArguments[0].Value is string s
                        ? $"\"{s.Replace("\\", "\\\\").Replace("\"", "\\\"")}\""
                        : null;
                    string typeName = field.Type.ToDisplayString(Qualified);
                    lines.Add(key == null
                        ? $"this.{field.Name} = shared.Get<{typeName}>();"
                        : $"this.{field.Name} = shared.Get<{typeName}>({key});");
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

        private static bool IsInitSystemInterface(INamedTypeSymbol type) {
            return type.ToDisplayString() == "KenseiECS.IInitSystem";
        }

        // Whether a call with a World and a SharedData argument, in that order, could bind to
        // <method>. Method type parameters are assumed to infer, so a doubtful overload counts.
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
                    }
                }
                return true;
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

        private static string TypeParameters(INamedTypeSymbol type) {
            return type.TypeParameters.Length == 0
                ? ""
                : "<" + string.Join(", ", type.TypeParameters.Select(p => p.Name)) + ">";
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

            // Classes with a hand-written `override ... Init(...)`: their generated base Init must be virtual.
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
