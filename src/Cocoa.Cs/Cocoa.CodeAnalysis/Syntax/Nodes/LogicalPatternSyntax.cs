using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 逻辑模式组合：expr is > 0 and < 10 / expr is null or 0 / expr is not null
    /// and: 两个模式都匹配 / or: 至少一个匹配 / not: 取反
    /// </summary>
    public sealed class LogicalPatternSyntax : PatternSyntax
    {
        public LogicalPatternSyntax(SyntaxTree syntaxTree, PatternSyntax left, SyntaxToken operatorToken, PatternSyntax right)
            : base(syntaxTree)
        {
            Left = left;
            OperatorToken = operatorToken;
            Right = right;
        }

        /// <summary>
        /// 一元 not 模式：expr is not null
        /// </summary>
        public LogicalPatternSyntax(SyntaxTree syntaxTree, SyntaxToken notKeyword, PatternSyntax pattern)
            : base(syntaxTree)
        {
            NotKeyword = notKeyword;
            Pattern = pattern;
        }

        public override SyntaxKind Kind => SyntaxKind.LogicalPattern;

        public PatternSyntax? Left { get; }
        public SyntaxToken? OperatorToken { get; }
        public PatternSyntax? Right { get; }

        /// <summary>not 关键字（一元 not 模式时非空）</summary>
        public SyntaxToken? NotKeyword { get; }

        /// <summary>被取反的模式（一元 not 模式时非空）</summary>
        public PatternSyntax? Pattern { get; }

        public bool IsUnary => NotKeyword != null;

        public override System.Collections.Generic.IEnumerable<SyntaxNode> GetChildren()
        {
            if (IsUnary)
            {
                yield return NotKeyword!;
                yield return Pattern!;
            }
            else
            {
                yield return Left!;
                yield return OperatorToken!;
                yield return Right!;
            }
        }
    }
}
