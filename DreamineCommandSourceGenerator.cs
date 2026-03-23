using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace Dreamine.MVVM.Generators
{
    /// <summary>
    /// \brief [DreamineCommand] 기반 커맨드/메서드 구현 소스 제너레이터.
    /// \details
    /// - 대상 메서드에 [DreamineCommand("Event.ReadmeCleck", BindTo="Readme")]를 붙이면,
    ///   Generator가 다음을 자동 생성합니다.
    ///   1) ICommand {MethodName}Command 프로퍼티
    ///   2) partial 메서드 구현(메서드 바디가 비어있을 때)
    ///   3) TargetMethod() 호출 + BindTo 프로퍼티 대입(옵션)
    /// \note
    /// - Attribute 타입(DreamineCommandAttribute)은 소비 프로젝트에서 참조 가능해야 합니다.
    /// </summary>
    [Generator]
    public sealed class DreamineCommandSourceGenerator : IIncrementalGenerator
    {
        /// <summary>
        /// \brief [DreamineCommand] 메서드는 partial 이어야 한다.
        /// </summary>
        private static readonly DiagnosticDescriptor NotPartialMethodRule =
            new DiagnosticDescriptor(
                id: "DMCMD001",
                title: "DreamineCommand requires partial method",
                messageFormat: "[DreamineCommand] 메서드는 partial 이어야 합니다: '{0}'",
                category: "Dreamine.MVVM.Generators",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true);

        /// <summary>
        /// \brief Initialize - 증분 소스 제너레이터 구성.
        /// </summary>
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // ------------------------------------------------------------------
            // \brief DreamineCommandAttribute Symbol 확보
            // ------------------------------------------------------------------
            var dreamineCommandAttrSymbol = context.CompilationProvider
                .Select(static (Compilation c, CancellationToken _) =>
                    c.GetTypeByMetadataName("Dreamine.MVVM.Attributes.DreamineCommandAttribute"));

            // ------------------------------------------------------------------
            // \brief 후보 메서드 수집 (MethodDeclarationSyntax + AttributeData)
            // \details
            // - transform 단계에서 semantic symbol을 뽑아 Candidate로 바로 축소
            // ------------------------------------------------------------------
            var candidates = context.SyntaxProvider.CreateSyntaxProvider(
                    predicate: static (SyntaxNode node, CancellationToken _) =>
                        node is MethodDeclarationSyntax { AttributeLists.Count: > 0 },
                    transform: static (GeneratorSyntaxContext gctx, CancellationToken _) =>
                    {
                        if (gctx.Node is not MethodDeclarationSyntax mds)
                            return default(Candidate?);

                        if (gctx.SemanticModel.GetDeclaredSymbol(mds) is not IMethodSymbol ms)
                            return default(Candidate);

                        return new Candidate(mds, ms);
                    })
                .Where(static c => c.HasValue)
                .Select(static (c, _) => c!.Value)
                .Combine(dreamineCommandAttrSymbol)
                .Select(static (pair, _) => FilterByAttribute(pair.Left, pair.Right))
                .Where(static c => c.HasValue)
                .Select(static (c, _) => c!.Value);

            // ------------------------------------------------------------------
            // \brief 소스 생성
            // ------------------------------------------------------------------
            context.RegisterSourceOutput(candidates, static (spc, candidate) =>
            {
                var (source, diags) = Generate(candidate);

                foreach (var d in diags)
                    spc.ReportDiagnostic(d);

                if (string.IsNullOrWhiteSpace(source))
                    return;

                var ns = Sanitize(candidate.MethodSymbol.ContainingNamespace.ToDisplayString());
                var className = candidate.MethodSymbol.ContainingType.Name;
                var methodName = candidate.MethodSymbol.Name;

                var fileName = $"{ns}_{className}_{methodName}_DreamineCommand.g.cs";
                spc.AddSource(fileName, SourceText.From(source, Encoding.UTF8));
            });
        }

        /// <summary>
        /// \brief Candidate가 DreamineCommandAttribute를 갖는지 확인하고 AttributeData를 붙여 반환합니다.
        /// </summary>
        private static CandidateWithAttribute? FilterByAttribute(Candidate c, INamedTypeSymbol? attrSymbol)
        {
            if (attrSymbol is null)
                return null;

            var attr = c.MethodSymbol
                .GetAttributes()
                .FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attrSymbol));

            if (attr is null)
                return null;

            return new CandidateWithAttribute(c.MethodSyntax, c.MethodSymbol, attr);
        }

        /// <summary>
        /// \brief CandidateWithAttribute 기반으로 최종 생성 코드를 만듭니다.
        /// </summary>
        private static (string Source, List<Diagnostic> Diagnostics) Generate(CandidateWithAttribute candidate)
        {
            var diags = new List<Diagnostic>();

            var methodSyntax = candidate.MethodSyntax;
            var methodSymbol = candidate.MethodSymbol;
            var attrData = candidate.Attribute;

            // ------------------------------------------------------------------
            // \brief partial 메서드 요구
            // ------------------------------------------------------------------
            if (!methodSyntax.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
            {
                diags.Add(Diagnostic.Create(
                    NotPartialMethodRule,
                    methodSyntax.Identifier.GetLocation(),
                    methodSymbol.ToDisplayString()));
                return (string.Empty, diags);
            }

            // ------------------------------------------------------------------
            // \brief Attribute 파라미터 파싱
            // ------------------------------------------------------------------
            var targetMethod = GetCtorStringArgument(attrData, index: 0) ?? string.Empty;
            var bindTo = GetNamedStringArgument(attrData, "BindTo");
            var commandNameOverride = GetNamedStringArgument(attrData, "CommandName");

            if (string.IsNullOrWhiteSpace(targetMethod))
                return (string.Empty, diags);

            var invocation = NormalizeInvocation(targetMethod);

            var methodName = methodSymbol.Name;
            var commandName = !string.IsNullOrWhiteSpace(commandNameOverride)
                ? commandNameOverride!
                : methodName + "Command";

            var ns = methodSymbol.ContainingNamespace.ToDisplayString();
            var typeChain = GetContainingTypeChain(methodSymbol.ContainingType);

            // ------------------------------------------------------------------
            // \brief 소스 생성
            // ------------------------------------------------------------------
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated />");
            sb.AppendLine("#nullable enable");
            sb.AppendLine("using System.Windows.Input;");
            sb.AppendLine("using Dreamine.MVVM.Core;");
            sb.AppendLine("using Dreamine.MVVM.ViewModels;");
            sb.AppendLine();
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");

            for (int i = 0; i < typeChain.Count; i++)
            {
                var t = typeChain[i];
                var indent = new string(' ', 4 * (i + 1));
                sb.AppendLine($"{indent}{GetTypeDeclarationHeader(t)}");
                sb.AppendLine($"{indent}{{");
            }

            var innerIndent = new string(' ', 4 * (typeChain.Count + 1));

            // \brief ICommand 프로퍼티 생성(상속 강제 금지)
            var fieldName = "_" + ToCamel(commandName);
            sb.AppendLine($"{innerIndent}/// <summary>");
            sb.AppendLine($"{innerIndent}/// \\brief {commandName} ICommand 프로퍼티(자동 생성).");
            sb.AppendLine($"{innerIndent}/// </summary>");
            sb.AppendLine($"{innerIndent}private ICommand? {fieldName};");
            sb.AppendLine($"{innerIndent}public ICommand {commandName} => {fieldName} ??= new RelayCommand({methodName});");
            sb.AppendLine();

            // \brief partial 메서드 바디가 비어있을 때만 구현 생성
            var hasBody = methodSyntax.Body is not null || methodSyntax.ExpressionBody is not null;
            if (!hasBody)
            {
                sb.AppendLine($"{innerIndent}{GetMethodSignature(methodSyntax)}");
                sb.AppendLine($"{innerIndent}{{");

                if (!string.IsNullOrWhiteSpace(bindTo))
                {
                    sb.AppendLine($"{innerIndent}    var __result = {invocation};");
                    sb.AppendLine($"{innerIndent}    {bindTo} = __result;");
                }
                else
                {
                    sb.AppendLine($"{innerIndent}    {invocation};");
                }

                sb.AppendLine($"{innerIndent}}}");
                sb.AppendLine();
            }

            for (int i = typeChain.Count - 1; i >= 0; i--)
            {
                var indent = new string(' ', 4 * (i + 1));
                sb.AppendLine($"{indent}}}");
            }

            sb.AppendLine("}");
            return (sb.ToString(), diags);
        }

        /// <summary>
        /// \brief 생성 대상 메서드 시그니처를 원본 문법에서 추출해 재구성합니다.
        /// \details
        /// - AttributeLists는 제거합니다(생성 파일에서 using/타입해석 문제 방지)
        /// - 접근자/정적/partial 유지
        /// - 본문은 Generator에서 생성
        /// </summary>
        private static string GetMethodSignature(MethodDeclarationSyntax methodSyntax)
        {
            /// \brief Attribute 제거 + 본문 제거 후 시그니처만 구성
            var withoutBody = methodSyntax
                .WithAttributeLists(default)         /// \brief Attribute 제거(핵심)
                .WithBody(null)
                .WithExpressionBody(null)
                .WithSemicolonToken(default);

            return withoutBody.ToFullString().Trim();
        }

        /// <summary>
        /// \brief DreamineCommandAttribute 생성자 문자열 인자를 가져옵니다.
        /// </summary>
        private static string? GetCtorStringArgument(AttributeData attr, int index)
        {
            if (attr.ConstructorArguments.Length <= index)
                return null;

            var arg = attr.ConstructorArguments[index];
            return arg.Value?.ToString();
        }

        /// <summary>
        /// \brief DreamineCommandAttribute의 NamedArgument 문자열 값을 가져옵니다.
        /// </summary>
        private static string? GetNamedStringArgument(AttributeData attr, string name)
        {
            return attr.NamedArguments
                .Where(kv => string.Equals(kv.Key, name, StringComparison.Ordinal))
                .Select(kv => kv.Value.Value?.ToString())
                .FirstOrDefault();
        }

        /// <summary>
        /// \brief TargetMethod 문자열을 invocation 형태로 정규화합니다.
        /// </summary>
        private static string NormalizeInvocation(string targetMethod)
        {
            var t = targetMethod.Trim();
            if (t.EndsWith(")", StringComparison.Ordinal))
                return t;
            return t + "()";
        }

        /// <summary>
        /// \brief 중첩 타입 체인을 바깥 -> 안쪽 순서로 반환합니다.
        /// </summary>
        private static List<INamedTypeSymbol> GetContainingTypeChain(INamedTypeSymbol innerMost)
        {
            var stack = new Stack<INamedTypeSymbol>();
            INamedTypeSymbol? cur = innerMost;

            while (cur is not null)
            {
                stack.Push(cur);
                cur = cur.ContainingType;
            }

            return stack.ToList();
        }

        /// <summary>
        /// \brief 타입 선언 헤더를 생성합니다(반드시 partial).
        /// \details 상속(: ViewModelBase) 같은 것은 절대 강제하지 않습니다.
        /// </summary>
        private static string GetTypeDeclarationHeader(INamedTypeSymbol t)
        {
            var kind = t.TypeKind switch
            {
                TypeKind.Class => "partial class",
                TypeKind.Struct => "partial struct",
                TypeKind.Interface => "partial interface",
                _ => "partial class"
            };

            var accessibility = t.DeclaredAccessibility switch
            {
                Accessibility.Public => "public",
                Accessibility.Internal => "internal",
                Accessibility.Private => "private",
                Accessibility.Protected => "protected",
                Accessibility.ProtectedAndInternal => "protected internal",
                Accessibility.ProtectedOrInternal => "protected internal",
                _ => "internal"
            };

            var staticKeyword = t.IsStatic ? " static" : string.Empty;

            var typeParams = t.TypeParameters.Length > 0
                ? "<" + string.Join(", ", t.TypeParameters.Select(p => p.Name)) + ">"
                : string.Empty;

            return $"{accessibility}{staticKeyword} {kind} {t.Name}{typeParams}";
        }

        /// <summary>
        /// \brief 이름을 파일명에 안전한 형태로 바꿉니다.
        /// </summary>
        private static string Sanitize(string name)
        {
            return name.Replace('.', '_').Replace('+', '_');
        }

        /// <summary>
        /// \brief PascalCase 문자열을 camelCase로 변환합니다.
        /// </summary>
        private static string ToCamel(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;

            if (name.Length == 1)
                return name.ToLowerInvariant();

            return char.ToLowerInvariant(name[0]) + name.Substring(1);
        }

        /// <summary>
        /// \brief 후보 메서드(문법 + 심볼) 정보.
        /// </summary>
        private readonly struct Candidate
        {
            /// <summary>\brief MethodDeclarationSyntax</summary>
            public MethodDeclarationSyntax MethodSyntax { get; }

            /// <summary>\brief IMethodSymbol</summary>
            public IMethodSymbol MethodSymbol { get; }

            /// <summary>\brief 생성자</summary>
            public Candidate(MethodDeclarationSyntax methodSyntax, IMethodSymbol methodSymbol)
            {
                MethodSyntax = methodSyntax;
                MethodSymbol = methodSymbol;
            }
        }

        /// <summary>
        /// \brief AttributeData까지 포함된 최종 후보 정보.
        /// </summary>
        private readonly struct CandidateWithAttribute
        {
            /// <summary>\brief MethodDeclarationSyntax</summary>
            public MethodDeclarationSyntax MethodSyntax { get; }

            /// <summary>\brief IMethodSymbol</summary>
            public IMethodSymbol MethodSymbol { get; }

            /// <summary>\brief AttributeData</summary>
            public AttributeData Attribute { get; }

            /// <summary>\brief 생성자</summary>
            public CandidateWithAttribute(MethodDeclarationSyntax methodSyntax, IMethodSymbol methodSymbol, AttributeData attribute)
            {
                MethodSyntax = methodSyntax;
                MethodSymbol = methodSymbol;
                Attribute = attribute;
            }
        }
    }
}