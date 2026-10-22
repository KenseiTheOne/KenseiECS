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
            "KECS006", "Inheritance of systems with injection is not supported",
            "Class '{0}' has injected fields and derives from '{1}', which has injected fields or implements IInitSystem, so one of the two Init methods would never run; keep the injected fields and Init in one class, or list IInitSystem among this class's bases and call base.Init from OnInit",
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

        private static readonly SymbolDisplayFormat Qualified = SymbolDisplayFormat.FullyQualifiedFormat;

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

            var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var classDecl in receiver.Candidates) {
                var model = context.Compilation.GetSemanticModel(classDecl.SyntaxTree);
                if (!(model.GetDeclaredSymbol(classDecl) is INamedTypeSymbol symbol) || !seen.Add(symbol)) {
                    continue;
                }
                Generate(context, symbol, classDecl);
            }
        }

        private static void Generate(GeneratorExecutionContext context, INamedTypeSymbol symbol, TypeDeclarationSyntax classDecl) {
            if (!HasInjectedFields(symbol)) {
                return;
            }

            if (!(classDecl is ClassDeclarationSyntax)) {
                string kind = classDecl is RecordDeclarationSyntax ? "record" : "struct";
                context.ReportDiagnostic(Diagnostic.Create(NotAClass, classDecl.Identifier.GetLocation(), symbol.Name, kind));
                return;
            }

            var fields = CollectFields(context, symbol);
            if (fields.Count == 0) {
                return;
            }

            if (!classDecl.Modifiers.Any(SyntaxKind.PartialKeyword)) {
                context.ReportDiagnostic(Diagnostic.Create(NotPartial, classDecl.Identifier.GetLocation(), symbol.Name));
                return;
            }

            // The generated Init neither calls a base Init nor overrides it. Report only the
            // setups where some fields are sure to stay null: a base IInitSystem the class
            // does not redeclare keeps the interface (the generated Init never runs), and a
            // base with injected fields needs base.Init called from OnInit.
            var baseType = symbol.BaseType;
            if (baseType != null) {
                bool interfaceStaysWithBase = IsInitSystem(baseType) && !symbol.Interfaces.Any(IsInitSystemInterface);
                bool baseFieldsSkipped = InheritsInjectedFields(baseType) && !CallsBaseInit(symbol);
                if (interfaceStaysWithBase || baseFieldsSkipped) {
                    context.ReportDiagnostic(Diagnostic.Create(InheritedInjection, classDecl.Identifier.GetLocation(), symbol.Name, baseType.Name));
                    return;
                }
            }

            foreach (var member in symbol.GetMembers().OfType<IMethodSymbol>()) {
                bool namedInit = member.Name == "Init" && member.Parameters.Length == 2;
                bool explicitInit = member.ExplicitInterfaceImplementations.Any(m => m.ContainingType.ToDisplayString() == "KenseiECS.IInitSystem");
                if ((namedInit || explicitInit) && !member.IsImplicitlyDeclared && member.Locations.Any(l => l.IsInSource)) {
                    context.ReportDiagnostic(Diagnostic.Create(InitAlreadyDeclared, member.Locations[0], symbol.Name));
                    return;
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated by KenseiECS.Generators />");
            sb.AppendLine("#nullable disable");

            string ns = symbol.ContainingNamespace.IsGlobalNamespace ? null : symbol.ContainingNamespace.ToDisplayString();
            int indent = 0;
            if (ns != null) {
                sb.AppendLine($"namespace {ns} {{");
                indent++;
            }

            var containers = new List<INamedTypeSymbol>();
            for (var outer = symbol.ContainingType; outer != null; outer = outer.ContainingType) {
                string unsupported = UnsupportedContainerKind(outer);
                if (unsupported != null) {
                    context.ReportDiagnostic(Diagnostic.Create(UnsupportedContainer, classDecl.Identifier.GetLocation(), symbol.Name, outer.Name, unsupported));
                    return;
                }
                if (!IsDeclaredPartial(outer)) {
                    context.ReportDiagnostic(Diagnostic.Create(ContainerNotPartial, classDecl.Identifier.GetLocation(), symbol.Name, outer.Name));
                    return;
                }
                containers.Insert(0, outer);
            }
            foreach (var container in containers) {
                string modifiers = container.IsStatic ? "static partial" : "partial";
                sb.AppendLine($"{Pad(indent)}{modifiers} {Keyword(container)} {container.Name}{TypeParameters(container)} {{");
                indent++;
            }

            bool implementsInit = IsInitSystem(symbol);
            string baseList = implementsInit ? "" : " : global::KenseiECS.IInitSystem";
            sb.AppendLine($"{Pad(indent)}partial class {symbol.Name}{TypeParameters(symbol)}{baseList} {{");
            indent++;

            sb.AppendLine($"{Pad(indent)}partial void OnInit(global::KenseiECS.World world, global::KenseiECS.SharedData shared);");
            sb.AppendLine();
            sb.AppendLine($"{Pad(indent)}public void Init(global::KenseiECS.World world, global::KenseiECS.SharedData shared) {{");
            indent++;
            foreach (var line in fields) {
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

        // Syntactic: any `base.Init(...)` call in the class's own declarations.
        private static bool CallsBaseInit(INamedTypeSymbol type) {
            foreach (var reference in type.DeclaringSyntaxReferences) {
                foreach (var invocation in reference.GetSyntax().DescendantNodes().OfType<InvocationExpressionSyntax>()) {
                    if (invocation.Expression is MemberAccessExpressionSyntax access
                        && access.Expression is BaseExpressionSyntax
                        && access.Name.Identifier.Text == "Init") {
                        return true;
                    }
                }
            }
            return false;
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

            public void OnVisitSyntaxNode(SyntaxNode node) {
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
