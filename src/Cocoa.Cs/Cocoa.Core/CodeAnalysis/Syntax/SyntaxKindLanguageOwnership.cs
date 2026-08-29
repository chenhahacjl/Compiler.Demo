using System.Collections.Generic;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>SyntaxKind 的语言归属（Y A3-1，A3 拆分基线的单一真相源）：Shared / CocoaOnly / CSharpOnly。</summary>
    public enum SyntaxLanguageOwnership
    {
        Shared,
        CocoaOnly,
        CSharpOnly,
    }

    /// <summary>
    /// 节点 / token 的语言归属表。互斥 kind 对目前仅 <c>ForStatement</c>（CO 次数循环
    /// <c>for i = 0 to n</c>）/ <c>CSStyleForStatement</c>（C# <c>for(;;)</c>）；
    /// 其余方言差异在词法 / 解析标志层（CocoaParser 关闭 C# 拼写、C# 拒绝 CO 关键字），节点层基本共享。
    /// 新增 CO 专属特性（A4）时在此登记，C# 侧勿使用。
    /// </summary>
    public static class SyntaxKindLanguageOwnership
    {
        private static readonly HashSet<SyntaxKind> CocoaOnlyKinds = new HashSet<SyntaxKind>
        {
            // 节点：CO 次数循环（C# 侧为 CSStyleForStatement）
            SyntaxKind.ForStatement,

            // 词法：CO 专属关键字（C# 侧无对应 token）
            SyntaxKind.FunctionKeyword,
            SyntaxKind.LetKeyword,
            SyntaxKind.PropertyKeyword,
            SyntaxKind.ConstructorKeyword,
            SyntaxKind.ExtendsKeyword,
            SyntaxKind.FacadeKeyword,
            SyntaxKind.SyscallKeyword,
            SyntaxKind.ImportKeyword,
            SyntaxKind.ToKeyword,
            SyntaxKind.StepKeyword,
            SyntaxKind.CdeclKeyword,
            SyntaxKind.StdcallKeyword,
        };

        private static readonly HashSet<SyntaxKind> CSharpOnlyKinds = new HashSet<SyntaxKind>
        {
            // C# `for(;;)`；CO 侧仅在错误恢复时产生（含诊断）
            SyntaxKind.CSStyleForStatement,
        };

        public static SyntaxLanguageOwnership Ownership(SyntaxKind kind)
        {
            if (CocoaOnlyKinds.Contains(kind))
            {
                return SyntaxLanguageOwnership.CocoaOnly;
            }

            if (CSharpOnlyKinds.Contains(kind))
            {
                return SyntaxLanguageOwnership.CSharpOnly;
            }

            return SyntaxLanguageOwnership.Shared;
        }
    }
}