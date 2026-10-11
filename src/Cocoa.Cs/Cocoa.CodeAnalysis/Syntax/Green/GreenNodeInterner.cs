using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 绿节点 DAG 规范化（2.1）：进程内无状态、会话级（每 SyntaxTree 一棵）intern 池——
    /// 高频子树（空参数列表、空块、单标识符、重复运算符 token 等）全局唯一化，绿层由树变 DAG。
    /// 红树主 API 不变；位置只在红层物化时现算，共享绿实例多次物化互不干扰。
    /// 自底向上使用：先 intern 子节点，再 intern 父节点——相等性靠子槽引用==（子已规范）+ Kind + 文本/值。
    /// 无 static 可变状态（池实例随 <see cref="SyntaxTree"/> 生命周期走，REPL 连续编辑仍逐树受益）。
    /// </summary>
    internal sealed class GreenNodeInterner
    {
        private readonly Dictionary<TriviaKey, GreenTrivia> _trivias = new();
        private readonly Dictionary<TokenKey, GreenToken> _tokens = new();
        private readonly Dictionary<NodeKey, GreenNodeWithChildren> _nodes = new(NodeKeyComparer.Instance);

        public GreenTrivia InternTrivia(SyntaxKind kind, string text)
        {
            var key = new TriviaKey(kind, text);
            if (_trivias.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var trivia = new GreenTrivia(kind, text);
            _trivias.Add(key, trivia);
            return trivia;
        }

        public GreenToken InternToken(SyntaxKind kind, string text, object? value,
            ImmutableArray<GreenTrivia> leadingTrivia, ImmutableArray<GreenTrivia> trailingTrivia)
        {
            var key = new TokenKey(kind, text, value, leadingTrivia, trailingTrivia);
            if (_tokens.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var token = new GreenToken(kind, text, value, leadingTrivia, trailingTrivia);
            _tokens.Add(key, token);
            return token;
        }

        public GreenNodeWithChildren InternNode(SyntaxKind kind, ImmutableArray<GreenNode?> slots)
        {
            var key = new NodeKey(kind, slots.IsDefault ? ImmutableArray<GreenNode?>.Empty : slots);
            if (_nodes.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var node = new GreenNodeWithChildren(kind, key.Slots);
            _nodes.Add(key, node);
            return node;
        }

        /// <summary>intern 一组 trivia（测试/工厂用，把已构造 trivia 转录进池）。</summary>
        public ImmutableArray<GreenTrivia> InternTriviaRange(IEnumerable<GreenTrivia> trivia)
        {
            var builder = ImmutableArray.CreateBuilder<GreenTrivia>();
            foreach (var t in trivia)
            {
                builder.Add(InternTrivia(t.Kind, t.Text));
            }

            return builder.ToImmutable();
        }

        private readonly record struct TriviaKey(SyntaxKind Kind, string Text);

        private readonly record struct TokenKey(SyntaxKind Kind, string Text, object? Value,
            ImmutableArray<GreenTrivia> LeadingTrivia, ImmutableArray<GreenTrivia> TrailingTrivia) : IEquatable<TokenKey>
        {
            public bool Equals(TokenKey other)
            {
                return Kind == other.Kind &&
                       string.Equals(Text, other.Text, StringComparison.Ordinal) &&
                       ValueEquals(Value, other.Value) &&
                       TriviaSequenceEquals(LeadingTrivia, other.LeadingTrivia) &&
                       TriviaSequenceEquals(TrailingTrivia, other.TrailingTrivia);
            }

            public override int GetHashCode()
            {
                var hash = 17;
                hash = hash * 31 + (int)Kind;
                hash = hash * 31 + (Text?.GetHashCode() ?? 0);
                hash = hash * 31 + (Value?.GetHashCode() ?? 0);
                hash = hash * 31 + TriviaSequenceHash(LeadingTrivia);
                hash = hash * 31 + TriviaSequenceHash(TrailingTrivia);
                return hash;
            }

            private static bool ValueEquals(object? a, object? b)
            {
                if (ReferenceEquals(a, b))
                {
                    return true;
                }

                if (a == null || b == null)
                {
                    return false;
                }

                // boxed 原始值（数字/布尔/char）引用不同但值相等 → 值比较
                return a.Equals(b);
            }

            private static bool TriviaSequenceEquals(ImmutableArray<GreenTrivia> a, ImmutableArray<GreenTrivia> b)
            {
                if (a.Length != b.Length)
                {
                    return false;
                }

                for (var i = 0; i < a.Length; i++)
                {
                    if (!ReferenceEquals(a[i], b[i]))
                    {
                        return false;
                    }
                }

                return true;
            }

            private static int TriviaSequenceHash(ImmutableArray<GreenTrivia> trivia)
            {
                var hash = 17;
                foreach (var t in trivia)
                {
                    hash = hash * 31 + (t?.GetHashCode() ?? 0);
                }

                return hash;
            }
        }

        private readonly struct NodeKey
        {
            public NodeKey(SyntaxKind kind, ImmutableArray<GreenNode?> slots)
            {
                Kind = kind;
                Slots = slots;
            }

            public SyntaxKind Kind { get; }

            public ImmutableArray<GreenNode?> Slots { get; }
        }

        private sealed class NodeKeyComparer : IEqualityComparer<NodeKey>
        {
            public static readonly NodeKeyComparer Instance = new();

            public bool Equals(NodeKey x, NodeKey y)
            {
                if (x.Kind != y.Kind || x.Slots.Length != y.Slots.Length)
                {
                    return false;
                }

                for (var i = 0; i < x.Slots.Length; i++)
                {
                    if (!ReferenceEquals(x.Slots[i], y.Slots[i]))
                    {
                        return false;
                    }
                }

                return true;
            }

            public int GetHashCode(NodeKey obj)
            {
                var hash = 17;
                hash = hash * 31 + (int)obj.Kind;
                foreach (var slot in obj.Slots)
                {
                    hash = hash * 31 + (slot?.GetHashCode() ?? 0);
                }

                return hash;
            }
        }
    }
}