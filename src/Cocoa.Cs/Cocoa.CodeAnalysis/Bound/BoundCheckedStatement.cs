using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Bound
{
    /// <summary>
    /// <c>checked { … }</c> / <c>unchecked { … }</c> 块。
    ///
    /// 算术溢出语义是**发射期**决定（IL 选 add.ovf/sub.ovf/mul.ovf，unchecked 选无检查变体），
    /// 绑定期无法就地改写表达式树（同一表达式在不同上下文中可处于不同溢出模式），
    /// 故保留为独立语句节点携带上下文标记，供发射层在遍历时切换模式。
    /// </summary>
    public sealed class BoundCheckedStatement : BoundStatement
    {
        public BoundCheckedStatement(SyntaxNode syntax, BoundStatement body, bool isChecked)
            : base(syntax)
        {
            Body = body;
            IsChecked = isChecked;
        }

        public override BoundNodeKind Kind => BoundNodeKind.CheckedStatement;

        /// <summary>块体。</summary>
        public BoundStatement Body { get; }

        /// <summary>true = checked 上下文（溢出抛 OverflowException）；false = unchecked（回绕）。</summary>
        public bool IsChecked { get; }
    }
}
