using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// <c>typeof(T)</c> / <c>sizeof(T)</c> 的绑定结果。
    ///
    /// 二者语义不同但都需要「类型实参 + 发射期按类型元数据求值」，故合为一个节点：
    /// <list type="bullet">
    /// <item><c>typeof(T)</c>：类型为 <see cref="NamedTypeSymbol.SystemType"/>；IL 发
    /// <c>ldtoken T; call System.Type::GetTypeFromHandle</c>。</item>
    /// <item><c>sizeof(T)</c>：基元类型在绑定期折叠为常量（C# 同，sizeof(int) 是编译期常量）；
    /// 其余类型类型为 <see cref="TypeSymbol.Int32"/>，IL 发 <c>sizeof T</c>。</item>
    /// </list>
    /// </summary>
    public sealed class BoundTypeOperatorExpression : BoundExpression
    {
        public BoundTypeOperatorExpression(SyntaxNode syntax, bool isTypeOf, TypeSymbol typeArgument, TypeSymbol resultType)
            : base(syntax)
        {
            IsTypeOf = isTypeOf;
            TypeArgument = typeArgument;
            ResultType = resultType;
        }

        public override BoundNodeKind Kind => BoundNodeKind.TypeOperatorExpression;

        public override TypeSymbol Type => ResultType;

        /// <summary>true = <c>typeof</c>；false = <c>sizeof</c>。</summary>
        public bool IsTypeOf { get; }

        /// <summary>类型实参 <c>T</c>。</summary>
        public TypeSymbol TypeArgument { get; }

        /// <summary>表达式类型（typeof → System.Type；sizeof → i32）。</summary>
        public TypeSymbol ResultType { get; }
    }
}
