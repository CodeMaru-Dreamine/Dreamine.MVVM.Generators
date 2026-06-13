using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
    /// <c>DreamineCommandAttribute</c>가 적용된 메서드를 기반으로
    /// 커맨드 프로퍼티와 forwarding 메서드 구현을 생성하는 증분 생성기입니다.
    /// </summary>
    [Generator]
    public sealed class DreamineCommandSourceGenerator : IIncrementalGenerator
    {
        private const string DreamineCommandAttributeMetadataName = "Dreamine.MVVM.Attributes.DreamineCommandAttribute";

        private static readonly DiagnosticDescriptor MethodMustBePartialDescriptor = new(
            id: "DMCMD001",
            title: "DreamineCommand method must be partial",
            messageFormat: "Method '{0}' marked with forwarding [DreamineCommand] must be declared partial",
            category: "Dreamine.MVVM.Generators",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor ContainingTypeMustBePartialDescriptor = new(
            id: "DMCMD002",
            title: "Containing type must be partial",
            messageFormat: "Containing type '{0}' for [DreamineCommand] method must be declared partial",
            category: "Dreamine.MVVM.Generators",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor MethodMustBeParameterlessVoidDescriptor = new(
            id: "DMCMD003",
            title: "DreamineCommand method must be parameterless void",
            messageFormat: "Method '{0}' marked with [DreamineCommand] must be a parameterless void method",
            category: "Dreamine.MVVM.Generators",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor CommandNameConflictDescriptor = new(
            id: "DMCMD004",
            title: "Generated command property name conflicts with an existing member",
            messageFormat: "Generated command property '{0}' conflicts with an existing member in type '{1}'",
            category: "Dreamine.MVVM.Generators",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor CanExecuteMethodNotFoundDescriptor = new(
            id: "DMCMD005",
            title: "CanExecute method not found",
            messageFormat: "CanExecute method '{0}' specified on [DreamineCommand] was not found in type '{1}'. The method must be a parameterless bool method.",
            category: "Dreamine.MVVM.Generators",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        /// <summary>
        /// 증분 생성기 파이프라인을 초기화합니다.
        /// </summary>
        /// <param name="context">생성기 초기화 컨텍스트입니다.</param>
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            IncrementalValueProvider<INamedTypeSymbol?> attributeSymbolProvider =
                context.CompilationProvider.Select(
                    static (compilation, _) => compilation.GetTypeByMetadataName(DreamineCommandAttributeMetadataName));

            IncrementalValueProvider<ImmutableArray<CommandCandidateModel>> candidateProvider =
                context.SyntaxProvider
                    .CreateSyntaxProvider(
                        predicate: static (node, _) => node is MethodDeclarationSyntax { AttributeLists.Count: > 0 },
                        transform: static (syntaxContext, _) => syntaxContext)
                    .Combine(attributeSymbolProvider)
                    .Select(static (pair, _) => TryCreateCandidate(pair.Left, pair.Right))
                    .Where(static candidate => candidate is not null)
                    .Select(static (candidate, _) => candidate!)
                    .Collect();

            context.RegisterSourceOutput(candidateProvider, static (sourceProductionContext, candidates) =>
            {
                Emit(sourceProductionContext, candidates);
            });
        }

        /// <summary>
        /// 구문/시맨틱 정보를 기반으로 생성 후보를 구성합니다.
        /// </summary>
        /// <param name="context">구문 분석 컨텍스트입니다.</param>
        /// <param name="attributeSymbol"><c>DreamineCommandAttribute</c> 심볼입니다.</param>
        /// <returns>유효한 후보이면 모델을 반환하고, 아니면 <see langword="null"/>을 반환합니다.</returns>
        private static CommandCandidateModel? TryCreateCandidate(
            GeneratorSyntaxContext context,
            INamedTypeSymbol? attributeSymbol)
        {
            if (attributeSymbol is null)
            {
                return null;
            }

            if (context.Node is not MethodDeclarationSyntax methodSyntax)
            {
                return null;
            }

            if (context.SemanticModel.GetDeclaredSymbol(methodSyntax) is not IMethodSymbol methodSymbol)
            {
                return null;
            }

            AttributeData? attributeData = methodSymbol
                .GetAttributes()
                .FirstOrDefault(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributeSymbol));

            if (attributeData is null)
            {
                return null;
            }

            string? targetMethod = GetConstructorStringArgument(attributeData, 0);
            string? bindTo = GetNamedStringArgument(attributeData, "BindTo");
            string? commandNameOverride = GetNamedStringArgument(attributeData, "CommandName");
            string? canExecuteMethod = GetNamedStringArgument(attributeData, "CanExecute");

            string commandPropertyName = BuildCommandPropertyName(methodSymbol.Name, commandNameOverride);

            return new CommandCandidateModel(
                methodSyntax,
                methodSymbol,
                targetMethod,
                bindTo,
                commandPropertyName,
                canExecuteMethod);
        }

        /// <summary>
        /// 수집된 후보를 진단하고 소스를 생성합니다.
        /// </summary>
        /// <param name="context">소스 출력 컨텍스트입니다.</param>
        /// <param name="candidates">수집된 후보 목록입니다.</param>
        private static void Emit(
            SourceProductionContext context,
            ImmutableArray<CommandCandidateModel> candidates)
        {
            if (candidates.IsDefaultOrEmpty)
            {
                return;
            }

            foreach (CommandCandidateModel candidate in candidates)
            {
                List<Diagnostic> diagnostics = ValidateCandidate(candidate);
                foreach (Diagnostic diagnostic in diagnostics)
                {
                    context.ReportDiagnostic(diagnostic);
                }

                if (diagnostics.Count > 0)
                {
                    continue;
                }

                string source = BuildSource(candidate);
                string fileName = BuildFileName(candidate);

                context.AddSource(fileName, SourceText.From(source, Encoding.UTF8));
            }
        }

        /// <summary>
        /// 후보의 유효성을 검증합니다.
        /// </summary>
        /// <param name="candidate">검사할 후보입니다.</param>
        /// <returns>발견된 진단 목록입니다.</returns>
        private static List<Diagnostic> ValidateCandidate(CommandCandidateModel candidate)
        {
            List<Diagnostic> diagnostics = new List<Diagnostic>();

            bool hasTargetMethod = !string.IsNullOrWhiteSpace(candidate.TargetMethod);

            if (hasTargetMethod && !IsPartialMethod(candidate.MethodSyntax))
            {
                diagnostics.Add(Diagnostic.Create(
                    MethodMustBePartialDescriptor,
                    candidate.MethodSyntax.Identifier.GetLocation(),
                    candidate.MethodSymbol.ToDisplayString()));
            }

            if (!IsContainingTypePartial(candidate.MethodSymbol.ContainingType))
            {
                diagnostics.Add(Diagnostic.Create(
                    ContainingTypeMustBePartialDescriptor,
                    candidate.MethodSyntax.Identifier.GetLocation(),
                    candidate.MethodSymbol.ContainingType.ToDisplayString()));
            }

            if (!IsParameterlessVoidMethod(candidate.MethodSymbol))
            {
                diagnostics.Add(Diagnostic.Create(
                    MethodMustBeParameterlessVoidDescriptor,
                    candidate.MethodSyntax.Identifier.GetLocation(),
                    candidate.MethodSymbol.ToDisplayString()));
            }

            if (HasConflictingMember(candidate.MethodSymbol.ContainingType, candidate.CommandPropertyName))
            {
                diagnostics.Add(Diagnostic.Create(
                    CommandNameConflictDescriptor,
                    candidate.MethodSyntax.Identifier.GetLocation(),
                    candidate.CommandPropertyName,
                    candidate.MethodSymbol.ContainingType.ToDisplayString()));
            }

            if (!string.IsNullOrWhiteSpace(candidate.CanExecuteMethod) &&
                !HasValidCanExecuteMethod(candidate.MethodSymbol.ContainingType, candidate.CanExecuteMethod!))
            {
                diagnostics.Add(Diagnostic.Create(
                    CanExecuteMethodNotFoundDescriptor,
                    candidate.MethodSyntax.Identifier.GetLocation(),
                    candidate.CanExecuteMethod,
                    candidate.MethodSymbol.ContainingType.ToDisplayString()));
            }

            return diagnostics;
        }

        /// <summary>
        /// partial 메서드 여부를 확인합니다.
        /// </summary>
        /// <param name="methodSyntax">검사할 메서드 구문입니다.</param>
        /// <returns>partial 메서드이면 <see langword="true"/>이고, 아니면 <see langword="false"/>입니다.</returns>
        private static bool IsPartialMethod(MethodDeclarationSyntax methodSyntax)
        {
            return methodSyntax.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PartialKeyword));
        }

        /// <summary>
        /// containing type이 partial인지 확인합니다.
        /// </summary>
        /// <param name="typeSymbol">검사할 타입 심볼입니다.</param>
        /// <returns>partial 타입이면 <see langword="true"/>이고, 아니면 <see langword="false"/>입니다.</returns>
        private static bool IsContainingTypePartial(INamedTypeSymbol typeSymbol)
        {
            foreach (SyntaxReference syntaxReference in typeSymbol.DeclaringSyntaxReferences)
            {
                if (syntaxReference.GetSyntax() is TypeDeclarationSyntax typeDeclaration &&
                    typeDeclaration.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PartialKeyword)))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 메서드가 parameterless void 형식인지 확인합니다.
        /// </summary>
        /// <param name="methodSymbol">검사할 메서드 심볼입니다.</param>
        /// <returns>조건을 만족하면 <see langword="true"/>이고, 아니면 <see langword="false"/>입니다.</returns>
        private static bool IsParameterlessVoidMethod(IMethodSymbol methodSymbol)
        {
            return methodSymbol.Parameters.Length == 0 &&
                   methodSymbol.ReturnsVoid &&
                   !methodSymbol.IsGenericMethod;
        }

        /// <summary>
        /// 같은 이름의 멤버가 이미 존재하는지 확인합니다.
        /// </summary>
        /// <param name="typeSymbol">검사할 타입입니다.</param>
        /// <param name="memberName">검사할 멤버 이름입니다.</param>
        /// <returns>같은 이름의 멤버가 있으면 <see langword="true"/>이고, 아니면 <see langword="false"/>입니다.</returns>
        private static bool HasConflictingMember(INamedTypeSymbol typeSymbol, string memberName)
        {
            return typeSymbol.GetMembers(memberName).Any();
        }

        private static bool HasValidCanExecuteMethod(INamedTypeSymbol typeSymbol, string methodName)
        {
            return typeSymbol.GetMembers(methodName)
                .OfType<IMethodSymbol>()
                .Any(m => m.Parameters.Length == 0 &&
                          !m.ReturnsVoid &&
                          m.ReturnType.SpecialType == SpecialType.System_Boolean);
        }

        /// <summary>
        /// 생성할 커맨드 프로퍼티 이름을 결정합니다.
        /// </summary>
        /// <param name="methodName">원본 메서드 이름입니다.</param>
        /// <param name="commandNameOverride">명시적으로 지정한 커맨드 이름입니다.</param>
        /// <returns>생성할 커맨드 프로퍼티 이름입니다.</returns>
        private static string BuildCommandPropertyName(string methodName, string? commandNameOverride)
        {
            if (!string.IsNullOrWhiteSpace(commandNameOverride))
            {
                return commandNameOverride!;
            }

            return methodName + "Command";
        }

        /// <summary>
        /// 생성 파일 이름을 만듭니다.
        /// </summary>
        /// <param name="candidate">대상 후보입니다.</param>
        /// <returns>생성 파일 이름입니다.</returns>
        private static string BuildFileName(CommandCandidateModel candidate)
        {
            string namespaceName = candidate.MethodSymbol.ContainingNamespace.IsGlobalNamespace
                ? "Global"
                : Sanitize(candidate.MethodSymbol.ContainingNamespace.ToDisplayString());

            string typeName = string.Join("_", GetContainingTypeChain(candidate.MethodSymbol.ContainingType).Select(type => type.Name));
            string methodName = candidate.MethodSymbol.Name;

            return namespaceName + "_" + typeName + "_" + methodName + "_DreamineCommand.g.cs";
        }

        /// <summary>
        /// 생성 코드를 만듭니다.
        /// </summary>
        /// <param name="candidate">생성 대상 후보입니다.</param>
        /// <returns>생성된 C# 소스 문자열입니다.</returns>
        private static string BuildSource(CommandCandidateModel candidate)
        {
            string namespaceName = candidate.MethodSymbol.ContainingNamespace.IsGlobalNamespace
                ? string.Empty
                : candidate.MethodSymbol.ContainingNamespace.ToDisplayString();

            List<INamedTypeSymbol> typeChain = GetContainingTypeChain(candidate.MethodSymbol.ContainingType);
            string commandFieldName = "_" + ToCamel(candidate.CommandPropertyName);
            string helperTypeName = "__DreamineGeneratedCommand_" + candidate.MethodSymbol.Name;
            bool hasTargetMethod = !string.IsNullOrWhiteSpace(candidate.TargetMethod);
            string? normalizedInvocation = hasTargetMethod
                ? NormalizeInvocation(candidate.TargetMethod!)
                : null;

            StringBuilder builder = new StringBuilder();
            builder.AppendLine("// <auto-generated />");
            builder.AppendLine("#nullable enable");
            builder.AppendLine("using System;");
            builder.AppendLine("using System.Windows.Input;");
            builder.AppendLine();

            if (!string.IsNullOrWhiteSpace(namespaceName))
            {
                builder.AppendLine("namespace " + namespaceName);
                builder.AppendLine("{");
            }

            for (int i = 0; i < typeChain.Count; i++)
            {
                string indent = new string(' ', 4 * (i + 1));
                builder.AppendLine(indent + GetTypeDeclarationHeader(typeChain[i]));
                builder.AppendLine(indent + "{");
            }

            string memberIndent = new string(' ', 4 * (typeChain.Count + 1));

            builder.AppendLine(memberIndent + "/// <summary>");
            builder.AppendLine(memberIndent + "/// 생성된 ICommand 프로퍼티입니다.");
            builder.AppendLine(memberIndent + "/// </summary>");
            builder.AppendLine(memberIndent + "private ICommand? " + commandFieldName + ";");
            string ctorArgs = !string.IsNullOrWhiteSpace(candidate.CanExecuteMethod)
                ? candidate.MethodSymbol.Name + ", " + candidate.CanExecuteMethod
                : candidate.MethodSymbol.Name;
            builder.AppendLine(memberIndent + "public ICommand " + candidate.CommandPropertyName + " => " + commandFieldName + " ??= new " + helperTypeName + "(" + ctorArgs + ");");
            builder.AppendLine();

            bool hasBody = candidate.MethodSyntax.Body is not null || candidate.MethodSyntax.ExpressionBody is not null;
            if (hasTargetMethod && !hasBody)
            {
                builder.AppendLine(memberIndent + GetMethodSignatureWithoutAttributes(candidate.MethodSyntax));
                builder.AppendLine(memberIndent + "{");

                if (!string.IsNullOrWhiteSpace(candidate.BindTo))
                {
                    builder.AppendLine(memberIndent + "    var __result = " + normalizedInvocation + ";");
                    builder.AppendLine(memberIndent + "    " + candidate.BindTo + " = __result;");
                }
                else
                {
                    builder.AppendLine(memberIndent + "    " + normalizedInvocation + ";");
                }

                builder.AppendLine(memberIndent + "}");
                builder.AppendLine();
            }

            bool hasCanExecute = !string.IsNullOrWhiteSpace(candidate.CanExecuteMethod);

            builder.AppendLine(memberIndent + "private sealed class " + helperTypeName + " : ICommand");
            builder.AppendLine(memberIndent + "{");
            builder.AppendLine(memberIndent + "    private readonly Action _execute;");
            if (hasCanExecute)
            {
                builder.AppendLine(memberIndent + "    private readonly Func<bool> _canExecute;");
                builder.AppendLine(memberIndent + "    public " + helperTypeName + "(Action execute, Func<bool> canExecute)");
                builder.AppendLine(memberIndent + "    {");
                builder.AppendLine(memberIndent + "        _execute = execute ?? throw new ArgumentNullException(nameof(execute));");
                builder.AppendLine(memberIndent + "        _canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));");
                builder.AppendLine(memberIndent + "    }");
            }
            else
            {
                builder.AppendLine(memberIndent + "    public " + helperTypeName + "(Action execute)");
                builder.AppendLine(memberIndent + "    {");
                builder.AppendLine(memberIndent + "        _execute = execute ?? throw new ArgumentNullException(nameof(execute));");
                builder.AppendLine(memberIndent + "    }");
            }

            builder.AppendLine(memberIndent + "    public event EventHandler? CanExecuteChanged;");
            builder.AppendLine(memberIndent + "    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);");
            if (hasCanExecute)
            {
                builder.AppendLine(memberIndent + "    public bool CanExecute(object? parameter) => _canExecute();");
            }
            else
            {
                builder.AppendLine(memberIndent + "    public bool CanExecute(object? parameter) => true;");
            }

            builder.AppendLine(memberIndent + "    public void Execute(object? parameter) => _execute();");
            builder.AppendLine(memberIndent + "}");
            builder.AppendLine();

            for (int i = typeChain.Count - 1; i >= 0; i--)
            {
                string indent = new string(' ', 4 * (i + 1));
                builder.AppendLine(indent + "}");
            }

            if (!string.IsNullOrWhiteSpace(namespaceName))
            {
                builder.AppendLine("}");
            }

            return builder.ToString();
        }

        /// <summary>
        /// 원본 메서드에서 Attribute와 본문을 제거한 시그니처를 만듭니다.
        /// </summary>
        /// <param name="methodSyntax">원본 메서드 구문입니다.</param>
        /// <returns>생성용 메서드 시그니처 문자열입니다.</returns>
        private static string GetMethodSignatureWithoutAttributes(MethodDeclarationSyntax methodSyntax)
        {
            MethodDeclarationSyntax withoutBody = methodSyntax
                .WithAttributeLists(default)
                .WithBody(null)
                .WithExpressionBody(null)
                .WithSemicolonToken(default);

            return withoutBody.NormalizeWhitespace().ToFullString();
        }

        /// <summary>
        /// 생성자 문자열 인자를 가져옵니다.
        /// </summary>
        /// <param name="attribute">검사할 Attribute 데이터입니다.</param>
        /// <param name="index">가져올 생성자 인덱스입니다.</param>
        /// <returns>값이 있으면 문자열을 반환하고, 아니면 <see langword="null"/>을 반환합니다.</returns>
        private static string? GetConstructorStringArgument(AttributeData attribute, int index)
        {
            if (attribute.ConstructorArguments.Length <= index)
            {
                return null;
            }

            return attribute.ConstructorArguments[index].Value?.ToString();
        }

        /// <summary>
        /// named argument 문자열 값을 가져옵니다.
        /// </summary>
        /// <param name="attribute">검사할 Attribute 데이터입니다.</param>
        /// <param name="name">찾을 인자 이름입니다.</param>
        /// <returns>값이 있으면 문자열을 반환하고, 아니면 <see langword="null"/>을 반환합니다.</returns>
        private static string? GetNamedStringArgument(AttributeData attribute, string name)
        {
            return attribute.NamedArguments
                .Where(argument => string.Equals(argument.Key, name, StringComparison.Ordinal))
                .Select(argument => argument.Value.Value?.ToString())
                .FirstOrDefault();
        }

        /// <summary>
        /// TargetMethod 문자열을 호출 형태로 정규화합니다.
        /// </summary>
        /// <param name="targetMethod">원본 대상 메서드 문자열입니다.</param>
        /// <returns>호출 형태로 정규화된 문자열입니다.</returns>
        private static string NormalizeInvocation(string targetMethod)
        {
            string trimmed = targetMethod.Trim();

            if (trimmed.EndsWith(")", StringComparison.Ordinal))
            {
                return trimmed;
            }

            return trimmed + "()";
        }

        /// <summary>
        /// 바깥 타입부터 안쪽 타입까지의 체인을 반환합니다.
        /// </summary>
        /// <param name="innerMostType">가장 안쪽 타입입니다.</param>
        /// <returns>바깥쪽부터 정렬된 타입 체인입니다.</returns>
        private static List<INamedTypeSymbol> GetContainingTypeChain(INamedTypeSymbol innerMostType)
        {
            Stack<INamedTypeSymbol> stack = new Stack<INamedTypeSymbol>();
            INamedTypeSymbol? current = innerMostType;

            while (current is not null)
            {
                stack.Push(current);
                current = current.ContainingType;
            }

            return stack.ToList();
        }

        /// <summary>
        /// partial 타입 선언 헤더를 생성합니다.
        /// </summary>
        /// <param name="typeSymbol">대상 타입 심볼입니다.</param>
        /// <returns>생성용 타입 선언 헤더입니다.</returns>
        private static string GetTypeDeclarationHeader(INamedTypeSymbol typeSymbol)
        {
            string kind = typeSymbol.TypeKind switch
            {
                TypeKind.Class => "partial class",
                TypeKind.Struct => "partial struct",
                _ => "partial class"
            };

            string accessibility = typeSymbol.DeclaredAccessibility switch
            {
                Accessibility.Public => "public",
                Accessibility.Internal => "internal",
                Accessibility.Private => "private",
                Accessibility.Protected => "protected",
                Accessibility.ProtectedAndInternal => "protected internal",
                Accessibility.ProtectedOrInternal => "protected internal",
                _ => "internal"
            };

            string staticKeyword = typeSymbol.IsStatic ? " static" : string.Empty;

            string typeParameters = typeSymbol.TypeParameters.Length > 0
                ? "<" + string.Join(", ", typeSymbol.TypeParameters.Select(parameter => parameter.Name)) + ">"
                : string.Empty;

            return accessibility + staticKeyword + " " + kind + " " + typeSymbol.Name + typeParameters;
        }

        /// <summary>
        /// 파일명에 안전한 형태로 문자열을 정리합니다.
        /// </summary>
        /// <param name="name">원본 문자열입니다.</param>
        /// <returns>정리된 문자열입니다.</returns>
        private static string Sanitize(string name)
        {
            return name.Replace('.', '_').Replace('+', '_');
        }

        /// <summary>
        /// PascalCase 문자열을 camelCase로 변환합니다.
        /// </summary>
        /// <param name="name">변환할 문자열입니다.</param>
        /// <returns>camelCase 문자열입니다.</returns>
        private static string ToCamel(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return name;
            }

            if (name.Length == 1)
            {
                return name.ToLowerInvariant();
            }

            return char.ToLowerInvariant(name[0]) + name.Substring(1);
        }

        /// <summary>
        /// 생성 대상 메서드 메타데이터를 나타냅니다.
        /// </summary>
        private sealed class CommandCandidateModel
        {
            /// <summary>
            /// <see cref="CommandCandidateModel"/> 클래스의 새 인스턴스를 초기화합니다.
            /// </summary>
            /// <param name="methodSyntax">원본 메서드 구문입니다.</param>
            /// <param name="methodSymbol">원본 메서드 심볼입니다.</param>
            /// <param name="targetMethod">대상 메서드 경로입니다.</param>
            /// <param name="bindTo">반환값을 대입할 프로퍼티 이름입니다.</param>
            /// <param name="commandPropertyName">생성할 커맨드 프로퍼티 이름입니다.</param>
            public CommandCandidateModel(
                MethodDeclarationSyntax methodSyntax,
                IMethodSymbol methodSymbol,
                string? targetMethod,
                string? bindTo,
                string commandPropertyName,
                string? canExecuteMethod = null)
            {
                MethodSyntax = methodSyntax;
                MethodSymbol = methodSymbol;
                TargetMethod = targetMethod;
                BindTo = bindTo;
                CommandPropertyName = commandPropertyName;
                CanExecuteMethod = canExecuteMethod;
            }

            /// <summary>
            /// 원본 메서드 구문을 가져옵니다.
            /// </summary>
            public MethodDeclarationSyntax MethodSyntax { get; }

            /// <summary>
            /// 원본 메서드 심볼을 가져옵니다.
            /// </summary>
            public IMethodSymbol MethodSymbol { get; }

            /// <summary>
            /// 대상 메서드 경로를 가져옵니다.
            /// </summary>
            public string? TargetMethod { get; }

            /// <summary>
            /// 반환값을 대입할 프로퍼티 이름을 가져옵니다.
            /// </summary>
            public string? BindTo { get; }

            /// <summary>
            /// 생성할 커맨드 프로퍼티 이름을 가져옵니다.
            /// </summary>
            public string CommandPropertyName { get; }

            /// <summary>
            /// CanExecute 판단 메서드 이름을 가져옵니다.
            /// </summary>
            public string? CanExecuteMethod { get; }
        }
    }
}
