using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Dreamine.MVVM.Generators
{
    internal sealed class EntryCandidateModel
    {
        /// <summary>
        /// 대상 클래스 심볼을 가져옵니다.
        /// </summary>
        public INamedTypeSymbol ClassSymbol { get; }

        /// <summary>
        /// 대상 클래스 선언 구문을 가져옵니다.
        /// </summary>
        public ClassDeclarationSyntax ClassDeclaration { get; }

        /// <summary>
        /// 대상 네임스페이스를 가져옵니다.
        /// </summary>
        public string Namespace { get; }

        /// <summary>
        /// 대상 클래스 이름을 가져옵니다.
        /// </summary>
        public string ClassName { get; }

        /// <summary>
        /// partial 선언 여부를 가져옵니다.
        /// </summary>
        public bool IsPartial { get; }

        /// <summary>
        /// Application 상속 여부를 가져옵니다.
        /// </summary>
        public bool IsApplicationDerived { get; }

        /// <summary>
        /// <see cref="EntryCandidateModel"/> 클래스의 새 인스턴스를 초기화합니다.
        /// </summary>
        /// <param name="classSymbol">대상 클래스 심볼입니다.</param>
        /// <param name="classDeclaration">대상 클래스 선언 구문입니다.</param>
        /// <param name="namespace">대상 네임스페이스입니다.</param>
        /// <param name="className">대상 클래스 이름입니다.</param>
        /// <param name="isPartial">partial 선언 여부입니다.</param>
        /// <param name="isApplicationDerived">Application 상속 여부입니다.</param>
        public EntryCandidateModel(
            INamedTypeSymbol classSymbol,
            ClassDeclarationSyntax classDeclaration,
            string @namespace,
            string className,
            bool isPartial,
            bool isApplicationDerived)
        {
            ClassSymbol = classSymbol;
            ClassDeclaration = classDeclaration;
            Namespace = @namespace;
            ClassName = className;
            IsPartial = isPartial;
            IsApplicationDerived = isApplicationDerived;
        }
    }
}