using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis
{
    /// <summary>
    /// C# 侧 RawKind → <see cref="CSharpSyntaxKind"/> 显式映射（P1-E-2b 单一真相点）。
    /// 当前值域与共享 <see cref="SyntaxKind"/> 完全对齐（= 绿树 <see cref="GreenNode.RawKind"/>），
    /// 故映射即强转；未来值域分叉（C# 新增专属 kind）时仅需修改本处，调用方零改动。
    /// </summary>
    public static class CSharpSyntaxKindMappings
    {
        /// <summary>RawKind(int) → C# 语法类型（未知值返回 BadToken 哨兵）。</summary>
        public static CSharpSyntaxKind ToCSharpSyntaxKind(int rawKind)
        {
            return rawKind >= 0 && rawKind <= (int)CSharpSyntaxKind.AsExpression
                ? (CSharpSyntaxKind)rawKind
                : CSharpSyntaxKind.BadToken;
        }

        /// <summary>共享联合枚举（过渡态）→ C# 语法类型。</summary>
        public static CSharpSyntaxKind ToCSharpSyntaxKind(SyntaxKind kind) => ToCSharpSyntaxKind((int)kind);

        /// <summary>C# 语法类型 → RawKind(int)（= 绿树存储值）。</summary>
        public static int ToRawKind(CSharpSyntaxKind kind) => (int)kind;
    }
}