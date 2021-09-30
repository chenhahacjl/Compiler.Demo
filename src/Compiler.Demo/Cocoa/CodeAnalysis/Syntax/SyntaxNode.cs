using System.Collections.Generic;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 语法节点
    /// </summary>
    public abstract class SyntaxNode
    {
        public abstract SyntaxKind Kind { get; }

        public abstract IEnumerable<SyntaxNode> GetChildren();
    }
}
