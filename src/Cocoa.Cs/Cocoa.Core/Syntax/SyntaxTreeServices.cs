using System.Collections.Generic;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 语法树共享服务（P1-3 钩子预备）：供语言库 <see cref="Language.GetRootMembers"/> /
    /// <see cref="Language.GetDeclaredNamespaceNames"/> 钩子在 P1 委托（共享节点形态）；
    /// P2-6 消费者适配后，语言库切语言节点实现，本器随共享具体节点类一并收口。
    /// </summary>
    internal static class SyntaxTreeServices
    {
        /// <summary>根直接成员（不含 EOF token）。</summary>
        public static ImmutableArray<SyntaxNode> GetRootMembers(SyntaxTree syntaxTree)
            => syntaxTree.Root.Members.Cast<SyntaxNode>().ToImmutableArray();

        /// <summary>声明/嵌套命名空间名集合（不判重；调用方按需 Distinct/排序）。</summary>
        public static ImmutableArray<string> GetDeclaredNamespaceNames(SyntaxTree syntaxTree)
        {
            var names = new List<string>();
            CollectNamespaceNames(syntaxTree.Root.Members, names);
            return names.ToImmutableArray();
        }

        private static void CollectNamespaceNames(ImmutableArray<MemberSyntax> members, List<string> names)
        {
            foreach (var member in members)
            {
                if (member is NamespaceDeclarationSyntax ns)
                {
                    names.Add(ns.Name);
                    CollectNamespaceNames(ns.Members, names);
                }
            }
        }
    }
}
