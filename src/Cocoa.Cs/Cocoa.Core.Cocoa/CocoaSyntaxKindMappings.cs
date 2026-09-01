using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis
{
    /// <summary>
    /// Cocoa 侧 RawKind → <see cref="CocoaSyntaxKind"/> 显式映射（P1-E-2b 单一真相点）。
    /// 当前值域与共享 <see cref="SyntaxKind"/> 完全对齐（= 绿树 <see cref="GreenNode.RawKind"/>），
    /// 故映射即强转；未来值域分叉（CO 新增专属 kind）时仅需修改本处，调用方零改动。
    /// </summary>
    public static class CocoaSyntaxKindMappings
    {
        /// <summary>RawKind(int) → Cocoa 语法类型（未知值返回 BadToken 哨兵）。</summary>
        public static CocoaSyntaxKind ToCocoaSyntaxKind(int rawKind)
        {
            return rawKind >= 0 && rawKind <= (int)CocoaSyntaxKind.AsExpression
                ? (CocoaSyntaxKind)rawKind
                : CocoaSyntaxKind.BadToken;
        }

        /// <summary>共享联合枚举（过渡态）→ Cocoa 语法类型。</summary>
        public static CocoaSyntaxKind ToCocoaSyntaxKind(SyntaxKind kind) => ToCocoaSyntaxKind((int)kind);

        /// <summary>Cocoa 语法类型 → RawKind(int)（= 绿树存储值）。</summary>
        public static int ToRawKind(CocoaSyntaxKind kind) => (int)kind;
    }
}