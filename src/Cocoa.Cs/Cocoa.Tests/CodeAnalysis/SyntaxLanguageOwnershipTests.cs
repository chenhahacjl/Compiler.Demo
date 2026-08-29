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
    /// 互斥节点 kind 对仅 <c>ForStatement</c>（CO 次数循环）/ <c>CSStyleForStatement</c>（C# <c>for(;;)</c>），
    /// 其余方言差异在词法 / 解析标志层（CocoaParser 关闭 C# 拼写、C# 拒绝 CO 关键字）。
    /// 新增 CO 专属特性（A4）时先登记归属表，再由本类断言与各方言解析器行为一致。
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
        public void OwnershipTable_ForStatement_IsCocoaOnly()
        {
            Assert.Equal(SyntaxLanguageOwnership.CocoaOnly, SyntaxKindLanguageOwnership.Ownership(SyntaxKind.ForStatement));
        }

        [Fact]
        public void OwnershipTable_CSStyleForStatement_IsCSharpOnly()
        {
            Assert.Equal(SyntaxLanguageOwnership.CSharpOnly, SyntaxKindLanguageOwnership.Ownership(SyntaxKind.CSStyleForStatement));
        }

        [Fact]
        public void OwnershipTable_ExclusiveForKinds_AreDisjoint()
        {
            // 互斥对：同一 `for` 关键字在 CO/C# 各产生唯一的节点 kind，绝不同时共享
            Assert.NotEqual(
                SyntaxKindLanguageOwnership.Ownership(SyntaxKind.ForStatement),
                SyntaxKindLanguageOwnership.Ownership(SyntaxKind.CSStyleForStatement));
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
        public void CocoaOnlyKeywords_RejectedInCs()
        {
            // 归属表与 C# 方言行为一致性：每个 CocoaOnly 关键字在 C# 惯用位置必产生至少一条错误
            foreach (var (keyword, snippet) in CocoaOnlyKeywordSnippets)
            {
                var tree = SyntaxTree.ParseCs(snippet);
                Assert.True(
                    tree.Diagnostics.Any(d => d.IsError),
                    $"C# 方言应拒绝 CO 专属关键字 {keyword}：\n{snippet}");
            }
        }

        [Fact]
        public void CoRangeFor_NotCSharpStyle()
        {
            var kinds = KindsOf("function Main(): i32 { for i = 0 to 3 { } return 0 }", cs: false);
            Assert.Contains(SyntaxKind.ForStatement, kinds);
            Assert.DoesNotContain(SyntaxKind.CSStyleForStatement, kinds);
        }

        [Fact]
        public void CsStyleFor_NotRange()
        {
            var kinds = KindsOf("class P { static void Main() { for (int i = 0; i < 3; i++) { } } }", cs: true);
            Assert.Contains(SyntaxKind.CSStyleForStatement, kinds);
            Assert.DoesNotContain(SyntaxKind.ForStatement, kinds);
        }

        [Fact]
        public void CoOwnedKeywords_RejectedInCs()
        {
            // CO 专属关键字：function / let / property / constructor / extends / facade / syscall / import / to / step
            var cs = SyntaxTree.ParseCs("function F() { let x = 1 }");
            Assert.True(cs.Diagnostics.Any(d => d.IsError), "C# 方言应拒绝 CO 专属关键字（function/let）。");
        }

        [Fact]
        public void CoParses_OwnedForms()
        {
            var tree = SyntaxTree.Parse("class Foo extends Object { public property X: i32 { get { return 0 } } public constructor() { } }");
            Assert.True(!tree.Diagnostics.Any(d => d.IsError), string.Join("; ", tree.Diagnostics.Select(d => d.Message)));
        }
    }
}
