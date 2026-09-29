using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// when 子句模式：<c>expr is &lt;pattern&gt; when &lt;cond&gt;</c>。
    /// 包装内层模式 + 附加条件，语义为「内层模式成立 且 条件成立」（C# §9.5.1）。
    /// 之所以做成独立模式节点而非 <c>IsExpressionSyntax</c> 的字段：
    /// 后者会牵动红/绿节点 schema 与 CoaFormat 序列化，而模式只走红节点路径，绿工厂本就不重建模式。
    /// </summary>
    public sealed class WhenPatternSyntax : PatternSyntax
    {
        public WhenPatternSyntax(SyntaxTree syntaxTree, PatternSyntax pattern, SyntaxToken whenKeyword, ExpressionSyntax condition)
            : base(syntaxTree)
        {
            Pattern = pattern;
            WhenKeyword = whenKeyword;
            Condition = condition;
        }

        public override SyntaxKind Kind => SyntaxKind.WhenPattern;

        /// <summary>内层模式（常量/声明/关系/逻辑/属性）。</summary>
        public PatternSyntax Pattern { get; }

        public SyntaxToken WhenKeyword { get; }

        /// <summary>附加条件表达式（对已通过类型测试的匹配值求值）。</summary>
        public ExpressionSyntax Condition { get; }

        public override System.Collections.Generic.IEnumerable<SyntaxNode> GetChildren()
        {
            yield return Pattern;
            yield return WhenKeyword;
            yield return Condition;
        }
    }
}
