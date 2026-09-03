using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 构造函数链调用：`base(...)` / `this(...)`（子类构造先调基类/本类构造）。
    /// </summary>
    public sealed class BoundConstructorChainExpression : BoundExpression
    {
        public BoundConstructorChainExpression(SyntaxNode syntax, ConstructorInitializerKind initializerKind, FunctionSymbol? constructor, ImmutableArray<BoundExpression> arguments)
            : base(syntax)
        {
            InitializerKind = initializerKind;
            Constructor = constructor;
            Arguments = arguments;
        }

        public override BoundNodeKind Kind => BoundNodeKind.ConstructorChainExpression;
        public override TypeSymbol Type => TypeSymbol.Void;

        public ConstructorInitializerKind InitializerKind { get; }

        /// <summary>
        /// 目标构造函数（base = 基类构造；this = 本类其他构造）。
        /// 6e-M19 M2-c：null = 链到内建 System.Object（无 .ctor 符号）的 0 参 no-op，发射器跳过。
        /// </summary>
        public FunctionSymbol? Constructor { get; }

        public ImmutableArray<BoundExpression> Arguments { get; }
    }

    public enum ConstructorInitializerKind
    {
        Base,
        This,
    }
}
