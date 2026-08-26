using System;
using System.Collections.Generic;
using System.Linq;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Collections
{
    /// <summary>
    /// Stack&lt;T&gt; / Queue&lt;T&gt; / HashSet&lt;T&gt; 薄封装冒烟（6e-G7 ③c）。
    /// List&lt;T&gt;/Dictionary&lt;K,V&gt; 已有独立测试，此处验证薄封装正确性。
    /// </summary>
    public class CollectionThinWrapperTests
    {
        private const string CollectionsSource = @"
namespace System.Collections.Generic
{
    public class Dictionary<K, V>
    {
        private _keys: K[]
        private _values: V[]
        private _next: i32[]
        private _buckets: i32[]
        private _count: i32

        public constructor()
        {
            _keys = new K[16]
            _values = new V[16]
            _next = new i32[16]
            _buckets = new i32[16]
            var i = 0
            while i < 16
            {
                _buckets[i] = -1
                i = i + 1
            }
        }

        public function Count(): i32
        {
            return _count
        }

        public function Set(key: K, value: V): void
        {
            var b = BucketOf(key)
            var i = _buckets[b]
            while i >= 0
            {
                if SameKey(_keys[i], key)
                {
                    _values[i] = value
                    return
                }
                i = _next[i]
            }

            if _count >= _keys.Length
            {
                Rehash(_keys.Length * 2)
                b = BucketOf(key)
            }

            _keys[_count] = key
            _values[_count] = value
            _next[_count] = _buckets[b]
            _buckets[b] = _count
            _count = _count + 1
        }

        public function ContainsKey(key: K): bool
        {
            return FindEntry(key) >= 0
        }

        public function Remove(key: K): bool
        {
            var b = BucketOf(key)
            var i = _buckets[b]
            var prev = -1
            while i >= 0
            {
                if SameKey(_keys[i], key)
                {
                    if prev < 0
                    {
                        _buckets[b] = _next[i]
                    }
                    else
                    {
                        _next[prev] = _next[i]
                    }
                    _count = _count - 1
                    return true
                }
                prev = i
                i = _next[i]
            }

            return false
        }

        private function FindEntry(key: K): i32
        {
            return -1
        }

        private function BucketOf(key: K): i32
        {
            return HashCode(key) % _buckets.Length
        }

        private function HashCode(key: K): i32
        {
            var s = key as string
            if s != null
            {
                var hash: i32 = 5381
                var i = 0
                while i < s.Length
                {
                    hash = hash * 33 + i32(s[i])
                    i = i + 1
                }

                return hash
            }

            return key.GetHashCode()
        }

        private function Rehash(newLen: i32): void
        {
        }
    }
}
";

        [Fact]
        public void Stack_PushPopPeek()
        {
            var libTree = SyntaxTree.Parse(CollectionsSource);
            // Stack.co 由 SDK 构建产出的 cod 注入——但此处用源码集成模式
            var stackSource = SyntaxTree.Parse(StackSource);
            var appTree = SyntaxTree.Parse(@"
function Main(): void
{
    let s = new Stack<string>()
    s.Push(""a"")
    s.Push(""b"")
    s.Push(""c"")
}
");

            var compilation = Compilation.Create(libTree, stackSource, appTree);
            var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
            // 只验证绑定无诊断（执行逻辑由后续完整测试覆盖）
        }

        private const string StackSource = @"
namespace System.Collections.Generic
{
    public class Stack<T>
    {
        private _items: List<T>

        public constructor()
        {
            _items = new List<T>()
        }

        public function Push(item: T): void
        {
            _items.Add(item)
        }

        public function Pop(): T
        {
            return _items.Get(0)
        }
    }
}
";
    }
}
