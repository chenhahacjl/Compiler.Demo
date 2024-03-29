﻿using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    public abstract class SeparatedSyntaxList
    {
        public abstract ImmutableArray<SyntaxNode> GetWhiteSeparators();
    }

    public sealed class SeparatedSyntaxList<T> : SeparatedSyntaxList, IEnumerable<T>
        where T : SyntaxNode
    {
        private readonly ImmutableArray<SyntaxNode> m_nodesAndSeparators;

        public SeparatedSyntaxList(ImmutableArray<SyntaxNode> nodesAndSeparators)
        {
            m_nodesAndSeparators = nodesAndSeparators;
        }

        public int Count => (m_nodesAndSeparators.Length + 1) / 2;

        public T this[int index] => (T)m_nodesAndSeparators[index * 2];

        public SyntaxToken GetSeparator(int index)
        {
            if (index == Count - 1)
            {
                return null;
            }

            return (SyntaxToken)m_nodesAndSeparators[index * 2 + 1];
        }

        public override ImmutableArray<SyntaxNode> GetWhiteSeparators() => m_nodesAndSeparators;

        public IEnumerator<T> GetEnumerator()
        {
            for (var i = 0; i < Count; i++)
            {
                yield return this[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
