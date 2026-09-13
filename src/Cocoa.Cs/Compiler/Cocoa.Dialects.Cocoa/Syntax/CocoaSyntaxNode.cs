using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    /// <summary>
    /// Cocoa 语言语法根类（S-1 复制分家；2026-09-13 去 C# 方言后并入共享 <see cref="SyntaxNode.Kind"/>）：
    /// 具体节点直接 override 共享 <see cref="SyntaxNode.Kind"/>（<see cref="SyntaxKind"/>）。
    /// </summary>
    public abstract class CocoaSyntaxNode : SyntaxNode
    {
        protected CocoaSyntaxNode(SyntaxTree syntaxTree)
            : base(syntaxTree)
        {
        }

        /// <summary>语言无关原始 kind（P2-6：与共享值域对齐，供绿/红桥接）。</summary>
        public override int RawKind => (int)Kind;
    }
}
