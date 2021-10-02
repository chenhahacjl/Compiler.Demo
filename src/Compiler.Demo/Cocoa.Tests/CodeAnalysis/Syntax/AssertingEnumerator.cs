using Cocoa.CodeAnalysis.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Syntax
{
    internal sealed class AssertingEnumerator : IDisposable
    {
        private readonly IEnumerator<SyntaxNode> m_enumerator;
        private bool m_hasErrors;

        public AssertingEnumerator(SyntaxNode node)
        {
            m_enumerator = Flatten(node).GetEnumerator();
        }

        private bool MarkFailed()
        {
            m_hasErrors = true;
            return false;
        }

        public void Dispose()
        {
            if (!m_hasErrors)
            {
                Assert.False(m_enumerator.MoveNext());
            }
            m_enumerator.Dispose();
        }

        private static IEnumerable<SyntaxNode> Flatten(SyntaxNode node)
        {
            var stack = new Stack<SyntaxNode>();
            stack.Push(node);

            while (stack.Count > 0)
            {
                var n = stack.Pop();
                yield return n;

                foreach (var child in n.GetChildren().Reverse())
                {
                    stack.Push(child);
                }
            }
        }

        public void AssertNode(SyntaxKind kind)
        {
            try
            {
                Assert.True(m_enumerator.MoveNext());
                Assert.Equal(kind, m_enumerator.Current.Kind);
                Assert.IsNotType<SyntaxToken>(m_enumerator.Current);
            }
            catch when (MarkFailed())
            {
                throw;
            }
        }

        public void AssertToken(SyntaxKind kind, string text)
        {
            try
            {
                Assert.True(m_enumerator.MoveNext());
                Assert.Equal(kind, m_enumerator.Current.Kind);
                var token = Assert.IsType<SyntaxToken>(m_enumerator.Current);
                Assert.Equal(text, token.Text);
            }
            catch when (MarkFailed())
            {
                throw;
            }
        }
    }
}
