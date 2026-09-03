using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>
    /// C# 语言语法根类（S-1 复制分家）：隐藏共享 <see cref="SyntaxNode.Kind"/>（virtual 哨兵），
    /// 以 <c>new abstract</c> 声明语言枚举 <see cref="CSharpSyntaxKind"/> 的 Kind；具体节点 override 语言枚举。
    /// </summary>
    public abstract class CSharpSyntaxNode : SyntaxNode
    {
        protected CSharpSyntaxNode(SyntaxTree syntaxTree)
            : base(syntaxTree)
        {
        }

        /// <summary>C# 语法类型（语言枚举接管共享联合视图）。</summary>
        public new abstract CSharpSyntaxKind Kind { get; }

        /// <summary>语言无关原始 kind（P2-6：与共享值域对齐，供绿/红桥接）。</summary>
        public override int RawKind => (int)Kind;
    }
}
