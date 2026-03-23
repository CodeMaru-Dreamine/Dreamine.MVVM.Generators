using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Dreamine.MVVM.Generators
{
    [Generator]
    public sealed class DreamineAutoWiringGenerator : IIncrementalGenerator
    {
        private const string ModelKind = "model";
        private const string EventKind = "event";
        private const string PropertyKind = "property";

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            context.RegisterPostInitializationOutput(ctx =>
            {
                ctx.AddSource("DebugLog.g.cs", SourceText.From("// ✅ Generator Executed", Encoding.UTF8));
            });

            var modelAttrSymbol = context.CompilationProvider
                .Select<Compilation, INamedTypeSymbol?>(static (c, _) =>
                    c.GetTypeByMetadataName("Dreamine.MVVM.Attributes.DreamineModelAttribute"));

            var eventAttrSymbol = context.CompilationProvider
                .Select<Compilation, INamedTypeSymbol?>(static (c, _) =>
                    c.GetTypeByMetadataName("Dreamine.MVVM.Attributes.DreamineEventAttribute"));

            var propAttrSymbol = context.CompilationProvider
                .Select<Compilation, INamedTypeSymbol?>(static (c, _) =>
                    c.GetTypeByMetadataName("Dreamine.MVVM.Attributes.DreaminePropertyAttribute"));

            var combinedSymbols = modelAttrSymbol
                .Combine(eventAttrSymbol)
                .Combine(propAttrSymbol)
                .Select<
                    ((INamedTypeSymbol?, INamedTypeSymbol?), INamedTypeSymbol?),
                    (INamedTypeSymbol? Model, INamedTypeSymbol? Event, INamedTypeSymbol? Property)>(
                    static (tuple, _) => (
                        Model: tuple.Item1.Item1,
                        Event: tuple.Item1.Item2,
                        Property: tuple.Item2
                    ));

            var candidates = context.SyntaxProvider
                .CreateSyntaxProvider(
                    static (s, _) => s is PropertyDeclarationSyntax { AttributeLists.Count: > 0 }
                                  || s is FieldDeclarationSyntax { AttributeLists.Count: > 0 },
                    static (ctx, _) => ctx)
                .Combine(combinedSymbols)
                .Select(static (pair, _) =>
                {
                    var ctx = pair.Left;
                    var (model, ev, prop) = pair.Right;
                    return GetUnifiedCandidateSymbolBased(ctx, model, ev, prop);
                })
                .Where(static x => x is not null)
                .Select(static (x, _) => x!.Value)
                .Collect();

            var grouped = candidates.Select(static (list, _) =>
                list.GroupBy(item => item.Symbol.ContainingType, SymbolEqualityComparer.Default));

            context.RegisterSourceOutput(grouped, (spc, groups) =>
            {
                foreach (var classGroup in groups)
                {
                    var classSymbol = (INamedTypeSymbol)classGroup.Key!;

                    foreach (var item in classGroup)
                    {
                        var fieldName = item.Symbol.Name;
                        var propName = item.IsField
                            ? char.ToUpper(fieldName.TrimStart('_')[0]) + fieldName.TrimStart('_').Substring(1)
                            : fieldName;

                        var exists = classSymbol.GetMembers()
                            .OfType<IPropertySymbol>()
                            .Any(p => p.Name == propName);

                        if (exists)
                        {
                            continue;
                        }

                        var source = GenerateCodeForSingleProperty(
                            classSymbol,                         
                            item.Symbol,
                            item.Kind,
                            item.IsField);

                        var ns = Sanitize(classSymbol.ContainingNamespace.ToDisplayString());
                        var fileName = $"{ns}_{classSymbol.Name}_{propName}_AutoProp.g.cs";
                        spc.AddSource(fileName, SourceText.From(source, Encoding.UTF8));
                    }
                }
            });
        }

        private static (SyntaxNode Syntax, ISymbol Symbol, string Kind, bool IsField)? GetUnifiedCandidateSymbolBased(
            GeneratorSyntaxContext context,
            INamedTypeSymbol? modelAttr,
            INamedTypeSymbol? eventAttr,
            INamedTypeSymbol? propertyAttr)
        {
            if (context.Node is PropertyDeclarationSyntax propertyDeclaration)
            {
                return TryGetPropertyCandidate(
                    context,
                    propertyDeclaration,
                    modelAttr,
                    eventAttr,
                    propertyAttr);
            }

            if (context.Node is FieldDeclarationSyntax fieldDeclaration)
            {
                return TryGetFieldCandidate(
                    context,
                    fieldDeclaration,
                    modelAttr,
                    eventAttr,
                    propertyAttr);
            }

            return null;
        }

        private static (SyntaxNode Syntax, ISymbol Symbol, string Kind, bool IsField)? TryGetPropertyCandidate(
            GeneratorSyntaxContext context,
            PropertyDeclarationSyntax propertyDeclaration,
            INamedTypeSymbol? modelAttr,
            INamedTypeSymbol? eventAttr,
            INamedTypeSymbol? propertyAttr)
        {
            if (propertyDeclaration.Initializer != null)
            {
                return null;
            }

            if (context.SemanticModel.GetDeclaredSymbol(propertyDeclaration) is not IPropertySymbol propertySymbol)
            {
                return null;
            }

            string? kind = GetCandidateKind(
                propertySymbol.GetAttributes(),
                modelAttr,
                eventAttr,
                propertyAttr);

            if (kind is null)
            {
                return null;
            }

            return (propertyDeclaration, propertySymbol, kind, false);
        }

        private static (SyntaxNode Syntax, ISymbol Symbol, string Kind, bool IsField)? TryGetFieldCandidate(
            GeneratorSyntaxContext context,
            FieldDeclarationSyntax fieldDeclaration,
            INamedTypeSymbol? modelAttr,
            INamedTypeSymbol? eventAttr,
            INamedTypeSymbol? propertyAttr)
        {
            foreach (VariableDeclaratorSyntax variable in fieldDeclaration.Declaration.Variables)
            {
                if (context.SemanticModel.GetDeclaredSymbol(variable) is not IFieldSymbol fieldSymbol)
                {
                    continue;
                }

                string? kind = GetCandidateKind(
                    fieldSymbol.GetAttributes(),
                    modelAttr,
                    eventAttr,
                    propertyAttr);

                if (kind is null)
                {
                    continue;
                }

                return (fieldDeclaration, fieldSymbol, kind, true);
            }

            return null;
        }

        private static string? GetCandidateKind(
    IEnumerable<AttributeData> attributes,
    INamedTypeSymbol? modelAttr,
    INamedTypeSymbol? eventAttr,
    INamedTypeSymbol? propertyAttr)
        {
            foreach (INamedTypeSymbol? attributeClass in attributes.Select(attribute => attribute.AttributeClass))
            {
                if (attributeClass is null)
                {
                    continue;
                }

                if (SymbolEqualityComparer.Default.Equals(attributeClass, modelAttr))
                {
                    return ModelKind;
                }

                if (SymbolEqualityComparer.Default.Equals(attributeClass, eventAttr))
                {
                    return EventKind;
                }

                if (SymbolEqualityComparer.Default.Equals(attributeClass, propertyAttr))
                {
                    return PropertyKind;
                }
            }

            return null;
        }

        private static string GenerateCodeForSingleProperty(
                INamedTypeSymbol classSymbol,
                ISymbol symbol,
                string kind,
                bool isField)
        {
            var ns = classSymbol.ContainingNamespace.ToDisplayString();
            var className = classSymbol.Name;

            var type = symbol switch
            {
                IFieldSymbol f => f.Type.ToDisplayString(),
                IPropertySymbol p => p.Type.ToDisplayString(),
                _ => "object"
            };

            var fieldName = symbol.Name;
            var propName = isField
                ? char.ToUpper(fieldName.TrimStart('_')[0]) + fieldName.TrimStart('_').Substring(1)
                : fieldName;

            var initExpr = kind switch
            {
                ModelKind => $"new {type}()",
                EventKind => $"DMContainer.Resolve<{type}>()",
                _ => "null!"
            };

            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated />");
            sb.AppendLine("using Dreamine.MVVM.Core;");
            sb.AppendLine("using Dreamine.MVVM.ViewModels;");
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
            sb.AppendLine($"    public partial class {className} : ViewModelBase");
            sb.AppendLine("    {");

            if (isField)
            {
                if (kind is not PropertyKind)
                {
                    sb.AppendLine($"        private {type} {fieldName}_ = {initExpr};");
                }

                if (kind == PropertyKind)
                {
                    sb.AppendLine($"        public {type} {propName} {{ get => {fieldName}; set => SetProperty(ref {fieldName}, value); }}");
                }
                else
                {
                    sb.AppendLine($"        public {type} {propName} => {fieldName}_;");
                }
            }
            else
            {
                sb.AppendLine($"        public {type} {propName} {{ get; }} = {initExpr};");
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string Sanitize(string name)
        {
            return name.Replace('.', '_').Replace('+', '_');
        }
    }
}