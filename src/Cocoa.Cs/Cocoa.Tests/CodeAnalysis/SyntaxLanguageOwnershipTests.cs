using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Syntax;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// Y A3-0/A3-1：节点层语言归属契约。A3-1 引入 <see cref="SyntaxKindLanguageOwnership"/> 归属表
    /// 作为单一真相源（Shared / CocoaOnly / CSharpOnly），本类据此锁定现状：
    /// <c>ForStatement</c>（C 风格 for(;;)，两语言共用 = Shared）；<c>ForRangeStatement</c>
    /// （CO 次数循环 for N to M = CocoaOnly）。其余方言差异在词法 / 解析标志层。
    /// 详见 蓝图 §6.7.10。
    /// </summary>
    public class SyntaxLanguageOwnershipTests
    {
        private static ImmutableList<SyntaxKind> KindsOf(string source, bool cs)
        {
            var tree = cs ? SyntaxTree.ParseCs(source) : SyntaxTree.Parse(source);
            Assert.True(!tree.Diagnostics.Any(d => d.IsError), string.Join("; ", tree.Diagnostics.Select(d => d.Message)));
            var root = tree.Root;
            return root.DescendantNodesAndSelf().Select(n => n.Kind).ToImmutableList();
        }

        /// <summary>P1-A：C# 语言关键字表对指定 kind 的分类（应为回落/共享）；经 <see cref="Language.GetOrThrow"/> 取已注册 C# 语言实例。</summary>
        private static SyntaxKind CSharpLanguageKeywordKind(SyntaxKind keywordKind)
        {
            var language = Language.GetOrThrow("csharp");
            return language.GetKeywordKind(keywordKind.ToString());
        }

        /// <summary>每个 CO 专属关键字在 C# 方言中的触发性拒绝片段（上下文 = 该关键字惯用位置）。</summary>
        private static readonly (SyntaxKind Keyword, string CsSnippet)[] CocoaOnlyKeywordSnippets =
        {
            (SyntaxKind.FunctionKeyword,    "function F(): i32 { }"),
            (SyntaxKind.LetKeyword,         "class P { static void M() { let x = 1; } }"),
            (SyntaxKind.PropertyKeyword,    "class P { property X: i32 { } }"),
            (SyntaxKind.ConstructorKeyword, "class P { constructor() { } }"),
            (SyntaxKind.ExtendsKeyword,     "class P extends Q { }"),
            (SyntaxKind.FacadeKeyword,      "facade F { }"),
            (SyntaxKind.SyscallKeyword,     "syscall F(): i32 { }"),
            (SyntaxKind.CdeclKeyword,       "cdecl F(): i32 { }"),
            (SyntaxKind.StdcallKeyword,     "stdcall F(): i32 { }"),
            (SyntaxKind.ImportKeyword,      "class K { import kernel32.dll { } }"),
            (SyntaxKind.ToKeyword,          "class P { static void M() { if (a to 3) { } } }"),
            (SyntaxKind.StepKeyword,        "class P { static void M() { if (a step 3) { } } }"),
        };

        [Fact]
        public void OwnershipTable_ForStatement_IsShared()
        {
            // C 风格 for(;;) 两语言共用
            Assert.Equal(SyntaxLanguageOwnership.Shared, SyntaxKindLanguageOwnership.Ownership(SyntaxKind.ForStatement));
        }

        [Fact]
        public void OwnershipTable_ForRangeStatement_IsCocoaOnly()
        {
            Assert.Equal(SyntaxLanguageOwnership.CocoaOnly, SyntaxKindLanguageOwnership.Ownership(SyntaxKind.ForRangeStatement));
        }

        [Fact]
        public void OwnershipTable_ExclusiveForKinds_AreDisjoint()
        {
            // 互斥：CO 次数循环（ForRangeStatement）为 CocoaOnly，与共享的 C 风格 for 不同属
            Assert.NotEqual(
                SyntaxKindLanguageOwnership.Ownership(SyntaxKind.ForStatement),
                SyntaxKindLanguageOwnership.Ownership(SyntaxKind.ForRangeStatement));
        }

        [Fact]
        public void OwnershipTable_CocoaOnlyKeywordKinds_AreAllCocoaOnly()
        {
            // 锁定归属表为单一真相源：表中登记的 CO 专属关键字全为 CocoaOnly
            Assert.Equal(12, CocoaOnlyKeywordSnippets.Length);
            foreach (var (keyword, _) in CocoaOnlyKeywordSnippets)
            {
                Assert.Equal(SyntaxLanguageOwnership.CocoaOnly, SyntaxKindLanguageOwnership.Ownership(keyword));
            }
        }

        [Fact]
        public void OwnershipTable_SharedKinds_RemainShared()
        {
            foreach (var kind in new[]
            {
                SyntaxKind.ClassKeyword,
                SyntaxKind.ReturnKeyword,
                SyntaxKind.IfStatement,
                SyntaxKind.NumberToken,
                SyntaxKind.IdentifierToken,
                SyntaxKind.EqualsToken,
                SyntaxKind.VarKeyword,
                SyntaxKind.NamespaceKeyword,
            })
            {
                Assert.Equal(SyntaxLanguageOwnership.Shared, SyntaxKindLanguageOwnership.Ownership(kind));
            }
        }

        [Fact]
        public void CocoaOnlyKeywords_FallBackToIdentifierInCs()
        {
            // P1-A 词法分家行为反转：CO 专属关键字在 `.cs` 词法表回落为标识符（SyntaxFacts 共享表 → CSharpLanguage 排除）。
            // 每个 CO 独占词在 C# 中可作普通标识符（文档 Phase 3：CO 词在 C# 可作标识符，反之亦然）。
            foreach (var (keyword, snippet) in CocoaOnlyKeywordSnippets)
            {
                Assert.Equal(SyntaxKind.IdentifierToken, CSharpLanguageKeywordKind(keyword));
            }

            // 惯用位置不再产生专属"不支持 CO 关键字"诊断（错与对：回落为标识符后走 C# 语法自然路径）
            var cs = SyntaxTree.ParseCs("class P { static void M() { let x = 1; } }");
            Assert.False(cs.Diagnostics.Any(d => d.IsError), $"C# 中 CO 词回落为标识符后不应报错: {string.Join("; ", cs.Diagnostics.Select(d => d.Message))}");
        }

        [Fact]
        public void CocoaOnlyKeywords_UsableAsCsIdentifiers()
        {
            // 12 个 CO 独占词全部可作 C# 普通标识符（编译 0 错误）
            var cs = SyntaxTree.ParseCs(
                "class P { int function = 1; int let = 2; int property = 3; int constructor = 4; " +
                "int extends = 5; int facade = 6; int syscall = 7; int cdecl = 8; int stdcall = 9; " +
                "int import = 10; int to = 11; int step = 12; }");
            Assert.False(cs.Diagnostics.Any(d => d.IsError), string.Join("; ", cs.Diagnostics.Select(d => d.Message)));
        }

        [Fact]
        public void CoRangeFor_IsForRangeStatement()
        {
            var kinds = KindsOf("function Main(): i32 { for i = 0 to 3 { } return 0 }", cs: false);
            Assert.Contains(SyntaxKind.ForRangeStatement, kinds);
            Assert.DoesNotContain(SyntaxKind.ForStatement, kinds);
        }

        [Fact]
        public void CsStyleFor_IsForStatement()
        {
            var kinds = KindsOf("class P { static void Main() { for (int i = 0; i < 3; i++) { } } }", cs: true);
            Assert.Contains(SyntaxKind.ForStatement, kinds);
            Assert.DoesNotContain(SyntaxKind.ForRangeStatement, kinds);
        }

        [Fact]
        public void CoOwnedKeywords_FallBackToIdentifierInCs()
        {
            // P1-A 行为反转：CO 专属关键字 function/let 在 `.cs` 词法表回落为标识符，不再被专属拒绝。
            var cs = SyntaxTree.ParseCs("class P { static void M() { int let = 1; int function = 2; print(let + function); } }");
            Assert.False(cs.Diagnostics.Any(d => d.IsError), string.Join("; ", cs.Diagnostics.Select(d => d.Message)));
        }

        [Fact]
        public void CoParses_OwnedForms()
        {
            var tree = SyntaxTree.Parse("class Foo extends Object { public property X: i32 { get { return 0 } } public constructor() { } }");
            Assert.True(!tree.Diagnostics.Any(d => d.IsError), string.Join("; ", tree.Diagnostics.Select(d => d.Message)));
        }
    }
}
