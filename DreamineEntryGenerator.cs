using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace Dreamine.MVVM.Generators
{
    /// <summary>
    /// <c>DreamineEntryAttribute</c>가 적용된 WPF 엔트리 클래스를 분석하고
    /// Dreamine 부트스트랩 코드를 생성하는 증분 생성기입니다.
    /// </summary>
    [Generator]
    public sealed class DreamineEntryGenerator : IIncrementalGenerator
    {
        private const string EntryAttributeMetadataName = "Dreamine.MVVM.Attributes.DreamineEntryAttribute";
        private const string WpfApplicationMetadataName = "System.Windows.Application";

        private static readonly DiagnosticDescriptor EntryMustBePartialDescriptor = new(
            id: "DMG001",
            title: "Entry class must be partial",
            messageFormat: "Class '{0}' marked with [DreamineEntry] must be declared partial",
            category: "Dreamine.MVVM.Generators",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor EntryMustInheritApplicationDescriptor = new(
            id: "DMG002",
            title: "Entry class must inherit Application",
            messageFormat: "Class '{0}' marked with [DreamineEntry] must inherit System.Windows.Application",
            category: "Dreamine.MVVM.Generators",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor DuplicateEntryDescriptor = new(
            id: "DMG003",
            title: "Only one entry class is allowed",
            messageFormat: "Only one valid [DreamineEntry] class is allowed but '{0}' is also marked as an entry",
            category: "Dreamine.MVVM.Generators",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        /// <summary>
        /// 증분 생성기 파이프라인을 초기화합니다.
        /// </summary>
        /// <param name="context">생성기 초기화 컨텍스트입니다.</param>
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            IncrementalValueProvider<INamedTypeSymbol?> entryAttributeSymbolProvider =
                context.CompilationProvider.Select(
                    static (compilation, _) => compilation.GetTypeByMetadataName(EntryAttributeMetadataName));

            IncrementalValueProvider<INamedTypeSymbol?> applicationSymbolProvider =
                context.CompilationProvider.Select(
                    static (compilation, _) => compilation.GetTypeByMetadataName(WpfApplicationMetadataName));

            IncrementalValueProvider<(INamedTypeSymbol? EntryAttributeSymbol, INamedTypeSymbol? ApplicationSymbol)> symbolsProvider =
                entryAttributeSymbolProvider.Combine(applicationSymbolProvider);

            IncrementalValueProvider<ImmutableArray<EntryCandidateModel>> candidateProvider =
                context.SyntaxProvider
                    .CreateSyntaxProvider(
                        predicate: static (node, _) => IsCandidateSyntax(node),
                        transform: static (syntaxContext, _) => syntaxContext)
                    .Combine(symbolsProvider)
                    .Select(static (pair, _) => TryCreateCandidate(pair.Left, pair.Right.EntryAttributeSymbol, pair.Right.ApplicationSymbol))
                    .Where(static candidate => candidate is not null)
                    .Select(static (candidate, _) => candidate!)
                    .Collect();

            context.RegisterSourceOutput(candidateProvider, static (sourceProductionContext, candidates) =>
            {
                Emit(sourceProductionContext, candidates);
            });
        }

        /// <summary>
        /// 엔트리 후보가 될 수 있는 구문인지 확인합니다.
        /// </summary>
        /// <param name="node">검사할 구문 노드입니다.</param>
        /// <returns>후보가 될 수 있으면 <see langword="true"/>이고, 아니면 <see langword="false"/>입니다.</returns>
        private static bool IsCandidateSyntax(SyntaxNode node)
        {
            return node is ClassDeclarationSyntax classDeclaration &&
                   classDeclaration.AttributeLists.Count > 0;
        }

        /// <summary>
        /// 구문/시맨틱 정보를 기반으로 엔트리 후보 모델을 생성합니다.
        /// </summary>
        /// <param name="context">구문 분석 컨텍스트입니다.</param>
        /// <param name="entryAttributeSymbol"><c>DreamineEntryAttribute</c> 심볼입니다.</param>
        /// <param name="applicationSymbol">WPF <c>Application</c> 심볼입니다.</param>
        /// <returns>
        /// 엔트리 후보이면 <see cref="EntryCandidateModel"/>를 반환하고,
        /// 아니면 <see langword="null"/>을 반환합니다.
        /// </returns>
        private static EntryCandidateModel? TryCreateCandidate(
            GeneratorSyntaxContext context,
            INamedTypeSymbol? entryAttributeSymbol,
            INamedTypeSymbol? applicationSymbol)
        {
            if (entryAttributeSymbol is null || applicationSymbol is null)
            {
                return null;
            }

            if (context.Node is not ClassDeclarationSyntax classDeclaration)
            {
                return null;
            }

            if (context.SemanticModel.GetDeclaredSymbol(classDeclaration) is not INamedTypeSymbol classSymbol)
            {
                return null;
            }

            bool hasEntryAttribute = classSymbol
                .GetAttributes()
                .Any(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, entryAttributeSymbol));

            if (!hasEntryAttribute)
            {
                return null;
            }

            string namespaceName = classSymbol.ContainingNamespace is { IsGlobalNamespace: false }
                ? classSymbol.ContainingNamespace.ToDisplayString()
                : string.Empty;

            return new EntryCandidateModel(
                classSymbol,
                classDeclaration,
                namespaceName,
                classSymbol.Name,
                IsPartial(classDeclaration),
                InheritsFrom(classSymbol, applicationSymbol));
        }

        /// <summary>
        /// 수집된 엔트리 후보를 진단하고 소스를 생성합니다.
        /// </summary>
        /// <param name="context">소스 출력 컨텍스트입니다.</param>
        /// <param name="candidates">수집된 엔트리 후보 목록입니다.</param>
        private static void Emit(
            SourceProductionContext context,
            ImmutableArray<EntryCandidateModel> candidates)
        {
            if (candidates.IsDefaultOrEmpty)
            {
                return;
            }

            foreach (EntryCandidateModel candidate in candidates)
            {
                if (!candidate.IsPartial)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        EntryMustBePartialDescriptor,
                        candidate.ClassDeclaration.Identifier.GetLocation(),
                        candidate.ClassName));
                }

                if (!candidate.IsApplicationDerived)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        EntryMustInheritApplicationDescriptor,
                        candidate.ClassDeclaration.Identifier.GetLocation(),
                        candidate.ClassName));
                }
            }

            ImmutableArray<EntryCandidateModel> validCandidates = candidates
                .Where(static candidate => candidate.IsPartial && candidate.IsApplicationDerived)
                .ToImmutableArray();

            if (validCandidates.Length == 0)
            {
                return;
            }

            if (validCandidates.Length > 1)
            {
                foreach (EntryCandidateModel candidate in validCandidates)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        DuplicateEntryDescriptor,
                        candidate.ClassDeclaration.Identifier.GetLocation(),
                        candidate.ClassName));
                }

                return;
            }

            EntryCandidateModel entryCandidate = validCandidates[0];
            string source = BuildEntrySource(entryCandidate.Namespace, entryCandidate.ClassName);

            context.AddSource(
                $"{entryCandidate.ClassName}.DreamineEntry.g.cs",
                SourceText.From(source, Encoding.UTF8));
        }

        /// <summary>
        /// 클래스 선언이 partial인지 확인합니다.
        /// </summary>
        /// <param name="classDeclaration">검사할 클래스 선언입니다.</param>
        /// <returns>partial이면 <see langword="true"/>이고, 아니면 <see langword="false"/>입니다.</returns>
        private static bool IsPartial(ClassDeclarationSyntax classDeclaration)
        {
            return classDeclaration.Modifiers.Any(static modifier => modifier.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword));
        }

        /// <summary>
        /// 대상 클래스가 지정한 기반 타입을 상속하는지 확인합니다.
        /// </summary>
        /// <param name="symbol">검사할 클래스 심볼입니다.</param>
        /// <param name="baseTypeSymbol">기준 기반 타입 심볼입니다.</param>
        /// <returns>상속 관계가 있으면 <see langword="true"/>이고, 아니면 <see langword="false"/>입니다.</returns>
        private static bool InheritsFrom(INamedTypeSymbol symbol, INamedTypeSymbol baseTypeSymbol)
        {
            INamedTypeSymbol? current = symbol;

            while (current is not null)
            {
                if (SymbolEqualityComparer.Default.Equals(current, baseTypeSymbol))
                {
                    return true;
                }

                current = current.BaseType;
            }

            return false;
        }

        /// <summary>
        /// 엔트리 부트스트랩 소스 코드를 생성합니다.
        /// </summary>
        /// <param name="namespaceName">대상 네임스페이스입니다.</param>
        /// <param name="className">대상 클래스 이름입니다.</param>
        /// <returns>생성된 C# 소스 코드 문자열입니다.</returns>
        private static string BuildEntrySource(string namespaceName, string className)
        {
            string namespaceBlockStart = string.IsNullOrWhiteSpace(namespaceName)
                ? string.Empty
                : $"namespace {namespaceName}\r\n{{";

            string namespaceBlockEnd = string.IsNullOrWhiteSpace(namespaceName)
                ? string.Empty
                : "}";

            return $$"""
// <auto-generated />
#nullable enable
using System.Windows;
using Dreamine.MVVM.Wpf;

{{namespaceBlockStart}}
    public partial class {{className}}
    {
        /// <summary>
        /// 애플리케이션 시작 시 Dreamine 부트스트랩을 수행합니다.
        /// </summary>
        /// <param name="e">시작 이벤트 인자입니다.</param>
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var rootAssembly = typeof({{className}}).Assembly;

            RegisterBefore();

            DreamineWpfOptions options = DreamineWpfOptions.CreateDefault();
            ConfigureDreamine(options);

            DreamineAppBuilder.Initialize(rootAssembly, options);

            ShowMainWindow();
            Dispatcher.InvokeAsync(() => RegisterAfter());
        }

        /// <summary>
        /// Dreamine 초기화 전에 사용자 정의 등록을 수행합니다.
        /// </summary>
        static partial void RegisterBefore();

        /// <summary>
        /// Dreamine WPF 옵션을 사용자 정의합니다.
        /// </summary>
        /// <param name="options">Dreamine WPF 옵션입니다.</param>
        static partial void ConfigureDreamine(DreamineWpfOptions options);

        /// <summary>
        /// Dreamine 초기화 후 사용자 정의 후처리를 수행합니다.
        /// </summary>
        static partial void RegisterAfter();

        /// <summary>
        /// 사용자가 직접 MainWindow를 표시해야 할 때 사용합니다.
        /// </summary>
        static partial void ShowMainWindow();
    }
{{namespaceBlockEnd}}
""";
        }
    }
}