using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 模式语法基类（is 表达式 / switch 表达式臂中的模式）
    /// </summary>
    public abstract class PatternSyntax : SyntaxNode
    {
        private protected PatternSyntax(SyntaxTree syntaxTree)
            : base(syntaxTree)
        {
        }

        public override int RawKind => (int)Kind;
    }
}
