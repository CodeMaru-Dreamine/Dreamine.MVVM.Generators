using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace Dreamine.MVVM.Generators
{
    /// <summary>
    /// <c>DreamineModelAttribute</c>, <c>DreamineEventAttribute</c>,
    /// <c>DreaminePropertyAttribute</c>가 적용된 필드를 기반으로
    /// 보조 프로퍼티 코드를 생성하는 증분 생성기입니다.
    /// </summary>
    [Generator]
    public sealed class DreamineAutoWiringGenerator : IIncrementalGenerator
    {
        private const string ModelAttributeMetadataName = "Dreamine.MVVM.Attributes.DreamineModelAttribute";
        private const string EventAttributeMetadataName = "Dreamine.MVVM.Attributes.DreamineEventAttribute";
        private const string PropertyAttributeMetadataName = "Dreamine.MVVM.Attributes.DreaminePropertyAttribute";

        /// <summary>
        /// 증분 생성기 파이프라인을 초기화합니다.
        /// </summary>
        /// <param name="context">생성기 초기화 컨텍스트입니다.</param>
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            IncrementalValueProvider<AttributeSymbolSet> attributeSymbolsProvider =
                context.CompilationProvider.Select(
                    static (compilation, _) => new AttributeSymbolSet(
                        compilation.GetTypeByMetadataName(ModelAttributeMetadataName),
                        compilation.GetTypeByMetadataName(EventAttributeMetadataName),
                        compilation.GetTypeByMetadataName(PropertyAttributeMetadataName)));

            IncrementalValueProvider<ImmutableArray<AutoWiringCandidate>> candidatesProvider =
                context.SyntaxProvider
                    .CreateSyntaxProvider(
                        predicate: static (node, _) => IsCandidateSyntax(node),
                        transform: static (syntaxContext, _) => syntaxContext)
                    .Combine(attributeSymbolsProvider)
                    .Select(static (pair, _) => TryCreateCandidate(pair.Left, pair.Right))
                    .Where(static candidate => candidate is not null)
                    .Select(static (candidate, _) => candidate!)
                    .Collect();

            context.RegisterSourceOutput(candidatesProvider, static (sourceProductionContext, candidates) =>
            {
                Emit(sourceProductionContext, candidates);
            });
        }

        /// <summary>
        /// 후보가 될 수 있는 구문인지 확인합니다.
        /// </summary>
        /// <param name="node">검사할 구문 노드입니다.</param>
        /// <returns>후보 구문이면 <see langword="true"/>이고, 아니면 <see langword="false"/>입니다.</returns>
        private static bool IsCandidateSyntax(SyntaxNode node)
        {
            if (node is not VariableDeclaratorSyntax variable)
            {
                return false;
            }

            if (variable.Parent is not VariableDeclarationSyntax variableDeclaration)
            {
                return false;
            }

            if (variableDeclaration.Parent is not FieldDeclarationSyntax fieldDeclaration)
            {
                return false;
            }

            return fieldDeclaration.AttributeLists.Count > 0;
        }

        /// <summary>
        /// 구문/시맨틱 정보를 바탕으로 생성 대상 후보를 만듭니다.
        /// </summary>
        /// <param name="context">구문 분석 컨텍스트입니다.</param>
        /// <param name="attributeSymbols">사용할 Attribute 심볼 집합입니다.</param>
        /// <returns>유효한 후보이면 해당 모델을 반환하고, 아니면 <see langword="null"/>을 반환합니다.</returns>
        private static AutoWiringCandidate? TryCreateCandidate(
            GeneratorSyntaxContext context,
            AttributeSymbolSet attributeSymbols)
        {
            if (attributeSymbols.IsIncomplete)
            {
                return null;
            }

            if (context.Node is not VariableDeclaratorSyntax variable)
            {
                return null;
            }

            if (context.SemanticModel.GetDeclaredSymbol(variable) is not IFieldSymbol fieldSymbol)
            {
                return null;
            }

            if (fieldSymbol.ContainingType is null)
            {
                return null;
            }

            // 1차 버전에서는 중첩 타입은 제외
            if (fieldSymbol.ContainingType.ContainingType is not null)
            {
                return null;
            }

            AttributeData? matchedAttribute;
            CandidateKind? kind = GetCandidateKind(fieldSymbol, attributeSymbols, out matchedAttribute);
            if (kind is null || matchedAttribute is null)
            {
                return null;
            }

            string? configuredPropertyName = GetConfiguredPropertyName(matchedAttribute);
            string? generatedPropertyName = ResolveGeneratedPropertyName(fieldSymbol.Name, configuredPropertyName);

            if (string.IsNullOrWhiteSpace(generatedPropertyName))
            {
                return null;
            }

            return new AutoWiringCandidate(
                fieldSymbol,
                fieldSymbol.ContainingType,
                kind.Value,
                generatedPropertyName!);
        }

        /// <summary>
        /// 수집된 후보를 기반으로 소스를 생성합니다.
        /// </summary>
        /// <param name="context">소스 출력 컨텍스트입니다.</param>
        /// <param name="candidates">수집된 후보 목록입니다.</param>
        private static void Emit(
            SourceProductionContext context,
            ImmutableArray<AutoWiringCandidate> candidates)
        {
            if (candidates.IsDefaultOrEmpty)
            {
                return;
            }

            foreach (AutoWiringCandidate candidate in candidates)
            {
                if (HasConflictingMember(candidate.ContainingType, candidate.GeneratedPropertyName))
                {
                    continue;
                }

                string source = BuildSource(candidate);
                string fileName = BuildFileName(candidate);

                context.AddSource(fileName, SourceText.From(source, Encoding.UTF8));
            }
        }

        /// <summary>
        /// 필드에 적용된 Attribute 종류를 판별합니다.
        /// </summary>
        /// <param name="fieldSymbol">검사할 필드 심볼입니다.</param>
        /// <param name="attributeSymbols">비교할 Attribute 심볼 집합입니다.</param>
        /// <param name="matchedAttribute">일치한 Attribute 데이터입니다.</param>
        /// <returns>일치한 종류가 있으면 해당 <see cref="CandidateKind"/>를 반환하고, 아니면 <see langword="null"/>을 반환합니다.</returns>
        private static CandidateKind? GetCandidateKind(
            IFieldSymbol fieldSymbol,
            AttributeSymbolSet attributeSymbols,
            out AttributeData? matchedAttribute)
        {
            foreach (AttributeData attribute in fieldSymbol.GetAttributes())
            {
                INamedTypeSymbol? attributeClass = attribute.AttributeClass;
                if (attributeClass is null)
                {
                    continue;
                }

                if (SymbolEqualityComparer.Default.Equals(attributeClass, attributeSymbols.ModelAttribute))
                {
                    matchedAttribute = attribute;
                    return CandidateKind.Model;
                }

                if (SymbolEqualityComparer.Default.Equals(attributeClass, attributeSymbols.EventAttribute))
                {
                    matchedAttribute = attribute;
                    return CandidateKind.Event;
                }

                if (SymbolEqualityComparer.Default.Equals(attributeClass, attributeSymbols.PropertyAttribute))
                {
                    matchedAttribute = attribute;
                    return CandidateKind.Property;
                }
            }

            matchedAttribute = null;
            return null;
        }

        /// <summary>
        /// Attribute에 지정된 명시적 프로퍼티 이름을 가져옵니다.
        /// </summary>
        /// <param name="attribute">검사할 Attribute 데이터입니다.</param>
        /// <returns>설정된 이름이 있으면 반환하고, 없으면 <see langword="null"/>을 반환합니다.</returns>
        private static string? GetConfiguredPropertyName(AttributeData attribute)
        {
            foreach (KeyValuePair<string, TypedConstant> namedArgument in attribute.NamedArguments)
            {
                if (namedArgument.Key == "PropertyName" &&
                    namedArgument.Value.Value is string namedValue &&
                    !string.IsNullOrWhiteSpace(namedValue))
                {
                    return namedValue;
                }
            }

            if (attribute.ConstructorArguments.Length > 0 &&
                attribute.ConstructorArguments[0].Value is string ctorValue &&
                !string.IsNullOrWhiteSpace(ctorValue))
            {
                return ctorValue;
            }

            return null;
        }

        /// <summary>
        /// 생성할 프로퍼티 이름을 결정합니다.
        /// </summary>
        /// <param name="fieldName">원본 필드 이름입니다.</param>
        /// <param name="configuredPropertyName">명시적으로 지정된 프로퍼티 이름입니다.</param>
        /// <returns>유효한 프로퍼티 이름이면 반환하고, 아니면 <see langword="null"/>을 반환합니다.</returns>
        private static string? ResolveGeneratedPropertyName(string fieldName, string? configuredPropertyName)
        {
            if (!string.IsNullOrWhiteSpace(configuredPropertyName))
            {
                return configuredPropertyName;
            }

            string normalized = fieldName.TrimStart('_');
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            if (normalized.Length == 1)
            {
                return normalized.ToUpperInvariant();
            }

            return char.ToUpperInvariant(normalized[0]) + normalized.Substring(1);
        }

        /// <summary>
        /// 이미 같은 이름의 멤버가 존재하는지 확인합니다.
        /// </summary>
        /// <param name="typeSymbol">검사 대상 타입입니다.</param>
        /// <param name="memberName">확인할 멤버 이름입니다.</param>
        /// <returns>같은 이름의 멤버가 있으면 <see langword="true"/>이고, 아니면 <see langword="false"/>입니다.</returns>
        private static bool HasConflictingMember(INamedTypeSymbol typeSymbol, string memberName)
        {
            return typeSymbol.GetMembers(memberName).Length > 0;
        }

        /// <summary>
        /// 생성 파일 이름을 만듭니다.
        /// </summary>
        /// <param name="candidate">대상 후보입니다.</param>
        /// <returns>생성 파일 이름입니다.</returns>
        private static string BuildFileName(AutoWiringCandidate candidate)
        {
            string namespaceName = candidate.ContainingType.ContainingNamespace.IsGlobalNamespace
                ? "Global"
                : Sanitize(candidate.ContainingType.ContainingNamespace.ToDisplayString());

            return namespaceName + "_" +
                   candidate.ContainingType.Name + "_" +
                   candidate.GeneratedPropertyName + "_AutoWiring.g.cs";
        }

        /// <summary>
        /// 생성 코드를 만듭니다.
        /// </summary>
        /// <param name="candidate">생성 대상 후보입니다.</param>
        /// <returns>생성된 C# 소스 문자열입니다.</returns>
        private static string BuildSource(AutoWiringCandidate candidate)
        {
            string namespaceName = candidate.ContainingType.ContainingNamespace.IsGlobalNamespace
                ? string.Empty
                : candidate.ContainingType.ContainingNamespace.ToDisplayString();

            string className = candidate.ContainingType.Name;
            string typeName = candidate.FieldSymbol.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            string fieldName = candidate.FieldSymbol.Name;
            string propertyName = candidate.GeneratedPropertyName;

            StringBuilder builder = new StringBuilder();
            builder.AppendLine("// <auto-generated />");
            builder.AppendLine("#nullable enable");
            builder.AppendLine("using Dreamine.MVVM.Core;");

            if (!string.IsNullOrWhiteSpace(namespaceName))
            {
                builder.AppendLine("namespace " + namespaceName);
                builder.AppendLine("{");
            }

            builder.AppendLine("    public partial class " + className);
            builder.AppendLine("    {");

            AppendPropertyCode(builder, candidate.Kind, candidate.FieldSymbol, typeName, fieldName, propertyName);

            builder.AppendLine("    }");

            if (!string.IsNullOrWhiteSpace(namespaceName))
            {
                builder.AppendLine("}");
            }

            return builder.ToString();
        }

        /// <summary>
        /// 종류에 맞는 프로퍼티 코드를 생성합니다.
        /// </summary>
        /// <param name="builder">대상 문자열 빌더입니다.</param>
        /// <param name="kind">생성 대상 종류입니다.</param>
        /// <param name="fieldSymbol">원본 필드 심볼입니다.</param>
        /// <param name="typeName">필드 타입 이름입니다.</param>
        /// <param name="fieldName">필드 이름입니다.</param>
        /// <param name="propertyName">생성할 프로퍼티 이름입니다.</param>
        private static void AppendPropertyCode(
            StringBuilder builder,
            CandidateKind kind,
            IFieldSymbol fieldSymbol,
            string typeName,
            string fieldName,
            string propertyName)
        {
            switch (kind)
            {
                case CandidateKind.Property:
                    builder.AppendLine("        /// <summary>");
                    builder.AppendLine("        /// 생성된 프로퍼티입니다.");
                    builder.AppendLine("        /// </summary>");
                    builder.AppendLine("        public " + typeName + " " + propertyName);
                    builder.AppendLine("        {");
                    builder.AppendLine("            get => " + fieldName + ";");
                    builder.AppendLine("            set => SetProperty(ref " + fieldName + ", value);");
                    builder.AppendLine("        }");
                    break;

                case CandidateKind.Model:
                    AppendLazyAccessPropertyCode(
                        builder,
                        fieldSymbol,
                        typeName,
                        fieldName,
                        propertyName,
                        "new " + typeName + "()");
                    break;

                case CandidateKind.Event:
                    AppendLazyAccessPropertyCode(
                        builder,
                        fieldSymbol,
                        typeName,
                        fieldName,
                        propertyName,
                        "DMContainer.Resolve<" + typeName + ">()");
                    break;
            }
        }

        /// <summary>
        /// 지연 초기화 기반 읽기 전용 프로퍼티 코드를 생성합니다.
        /// </summary>
        /// <param name="builder">대상 문자열 빌더입니다.</param>
        /// <param name="fieldSymbol">원본 필드 심볼입니다.</param>
        /// <param name="typeName">필드 타입 이름입니다.</param>
        /// <param name="fieldName">필드 이름입니다.</param>
        /// <param name="propertyName">프로퍼티 이름입니다.</param>
        /// <param name="initializerExpression">초기화 식입니다.</param>
        private static void AppendLazyAccessPropertyCode(
            StringBuilder builder,
            IFieldSymbol fieldSymbol,
            string typeName,
            string fieldName,
            string propertyName,
            string initializerExpression)
        {
            builder.AppendLine("        /// <summary>");
            builder.AppendLine("        /// 생성된 참조 프로퍼티입니다.");
            builder.AppendLine("        /// </summary>");

            if (fieldSymbol.IsReadOnly || fieldSymbol.Type.IsValueType)
            {
                builder.AppendLine("        public " + typeName + " " + propertyName + " => " + fieldName + ";");
                return;
            }

            builder.AppendLine("        public " + typeName + " " + propertyName);
            builder.AppendLine("        {");
            builder.AppendLine("            get");
            builder.AppendLine("            {");
            builder.AppendLine("                if (" + fieldName + " is null)");
            builder.AppendLine("                {");
            builder.AppendLine("                    " + fieldName + " = " + initializerExpression + ";");
            builder.AppendLine("                }");
            builder.AppendLine();
            builder.AppendLine("                return " + fieldName + ";");
            builder.AppendLine("            }");
            builder.AppendLine("        }");
        }

        /// <summary>
        /// 파일 이름에 안전한 문자열로 변환합니다.
        /// </summary>
        /// <param name="name">원본 문자열입니다.</param>
        /// <returns>정리된 문자열입니다.</returns>
        private static string Sanitize(string name)
        {
            return name.Replace('.', '_').Replace('+', '_');
        }

        /// <summary>
        /// 생성 대상 종류를 나타냅니다.
        /// </summary>
        private enum CandidateKind
        {
            Model,
            Event,
            Property
        }

        /// <summary>
        /// Attribute 심볼 집합을 나타냅니다.
        /// </summary>
        private sealed class AttributeSymbolSet
        {
            /// <summary>
            /// <see cref="AttributeSymbolSet"/> 클래스의 새 인스턴스를 초기화합니다.
            /// </summary>
            /// <param name="modelAttribute">Model Attribute 심볼입니다.</param>
            /// <param name="eventAttribute">Event Attribute 심볼입니다.</param>
            /// <param name="propertyAttribute">Property Attribute 심볼입니다.</param>
            public AttributeSymbolSet(
                INamedTypeSymbol? modelAttribute,
                INamedTypeSymbol? eventAttribute,
                INamedTypeSymbol? propertyAttribute)
            {
                ModelAttribute = modelAttribute;
                EventAttribute = eventAttribute;
                PropertyAttribute = propertyAttribute;
            }

            /// <summary>
            /// Model Attribute 심볼을 가져옵니다.
            /// </summary>
            public INamedTypeSymbol? ModelAttribute { get; }

            /// <summary>
            /// Event Attribute 심볼을 가져옵니다.
            /// </summary>
            public INamedTypeSymbol? EventAttribute { get; }

            /// <summary>
            /// Property Attribute 심볼을 가져옵니다.
            /// </summary>
            public INamedTypeSymbol? PropertyAttribute { get; }

            /// <summary>
            /// 필수 심볼이 모두 준비되었는지 여부를 가져옵니다.
            /// </summary>
            public bool IsIncomplete
            {
                get
                {
                    return ModelAttribute is null ||
                           EventAttribute is null ||
                           PropertyAttribute is null;
                }
            }
        }

        /// <summary>
        /// 자동 생성 대상 필드 메타데이터를 나타냅니다.
        /// </summary>
        private sealed class AutoWiringCandidate
        {
            /// <summary>
            /// <see cref="AutoWiringCandidate"/> 클래스의 새 인스턴스를 초기화합니다.
            /// </summary>
            /// <param name="fieldSymbol">대상 필드 심볼입니다.</param>
            /// <param name="containingType">필드를 포함하는 타입입니다.</param>
            /// <param name="kind">후보 종류입니다.</param>
            /// <param name="generatedPropertyName">생성할 프로퍼티 이름입니다.</param>
            public AutoWiringCandidate(
                IFieldSymbol fieldSymbol,
                INamedTypeSymbol containingType,
                CandidateKind kind,
                string generatedPropertyName)
            {
                FieldSymbol = fieldSymbol;
                ContainingType = containingType;
                Kind = kind;
                GeneratedPropertyName = generatedPropertyName;
            }

            /// <summary>
            /// 대상 필드 심볼을 가져옵니다.
            /// </summary>
            public IFieldSymbol FieldSymbol { get; }

            /// <summary>
            /// 필드를 포함하는 타입을 가져옵니다.
            /// </summary>
            public INamedTypeSymbol ContainingType { get; }

            /// <summary>
            /// 후보 종류를 가져옵니다.
            /// </summary>
            public CandidateKind Kind { get; }

            /// <summary>
            /// 생성할 프로퍼티 이름을 가져옵니다.
            /// </summary>
            public string GeneratedPropertyName { get; }
        }
    }
}