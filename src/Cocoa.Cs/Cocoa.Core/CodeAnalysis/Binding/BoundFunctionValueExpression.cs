using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 函数值表达式（6e-M22 C4）：lambda 字面量或方法组 → 一等函数对象。
    /// <list type="bullet">
    /// <item>lambda：<see cref="Body"/> 携带已绑定体（BindProgram 后处理入 Functions 清单），Receiver 为 null</item>
    /// <item>方法组：<see cref="Receiver"/> 为接收者（实例方法作环境/this；静态为 null），Body 为 null</item>
    /// </list>
    /// 类型恒为 <see cref="FunctionTypeSymbol"/>（签名形状，不含接收者）。
    /// </summary>
    internal sealed class BoundFunctionValueExpression : BoundExpression
    {
        public BoundFunctionValueExpression(SyntaxNode syntax, FunctionSymbol function, BoundExpression? receiver, BoundBlockStatement? body, FunctionTypeSymbol type)
            : base(syntax)
        {
            Function = function;
            Receiver = receiver;
            Body = body;
            Type = type;
        }

        public override BoundNodeKind Kind => BoundNodeKind.FunctionValueExpression;

        public override TypeSymbol Type { get; }

        public FunctionSymbol Function { get; }

        /// <summary>方法组接收者（实例方法的环境槽）；null = 静态/lambda。</summary>
        public BoundExpression? Receiver { get; }

        /// <summary>lambda 已绑定体；方法组为 null。</summary>
        public BoundBlockStatement? Body { get; }
    }
}
