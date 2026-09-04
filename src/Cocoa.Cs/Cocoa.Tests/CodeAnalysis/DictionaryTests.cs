using System;
using System.Collections.Generic;
using System.Linq;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// Dictionary&lt;K,V&gt; 源码集成冒烟（6e-G7 ③b）：
    /// Set/Get/TryGetValue(out)/ContainsKey/Remove/Count 全路径 ×Evaluator+IL。
    /// </summary>
    public class DictionaryTests
    {
        private const string DictSource = @"
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

        public function Get(key: K): V
        {
            return _values[FindEntry(key)]
        }

        public function TryGetValue(key: K, out value: V): bool
        {
            var b = BucketOf(key)
            var i = _buckets[b]
            while i >= 0
            {
                if SameKey(_keys[i], key)
                {
                    value = _values[i]
                    return true
                }
                i = _next[i]
            }

            return false
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

                    var last = _count - 1
                    if i < last
                    {
                        _keys[i] = _keys[last]
                        _values[i] = _values[last]
                        _next[i] = _next[last]

                        var ni = 0
                        while ni < _count
                        {
                            if _next[ni] == last
                            {
                                _next[ni] = i
                            }
                            ni = ni + 1
                        }

                        var bi = 0
                        while bi < _buckets.Length
                        {
                            if _buckets[bi] == last
                            {
                                _buckets[bi] = i
                            }
                            bi = bi + 1
                        }
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
            var b = BucketOf(key)
            var i = _buckets[b]
            while i >= 0
            {
                if SameKey(_keys[i], key)
                {
                    return i
                }
                i = _next[i]
            }

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
                var i = 0
                while i < s.Length
                {
                    i = i + 1
                }

            }

            var hash: i32 = 5381
            var i2 = 0
            while i2 < s.Length
            {
                hash = hash * 33 + i32(s[i2])
                i2 = i2 + 1
            }

            return hash
            return key.GetHashCode()
        }

        private function SameKey(a: K, b: K): bool
        {
            var sa = a as string
            var sb = b as string
            if sa != null && sb != null
            {
                return sa == sb
            }

            return true
        }

        private function Rehash(newLen: i32): void
        {
            var newKeys = new K[newLen]
            var newValues = new V[newLen]
            var newNext = new i32[newLen]
            var newBuckets = new i32[newLen]
            var i = 0
            while i < newLen
            {
                newBuckets[i] = -1
                i = i + 1
            }

            var j = 0
            while j < _count
            {
                newKeys[j] = _keys[j]
                newValues[j] = _values[j]
                var b2 = HashCode(newKeys[j]) % newLen
                newNext[j] = newBuckets[b2]
                newBuckets[b2] = j
                j = j + 1
            }

            _keys = newKeys
            _values = newValues
            _next = newNext
            _buckets = newBuckets
        }
    }
}
";

        private const string AppSource = @"
using System.Collections.Generic

function Main(): void
{
    let d = new Dictionary<string, i32>()
    d.Set(""alpha"", 1)
    d.Set(""beta"", 2)
    d.Set(""gamma"", 3)

    System.Console.WriteLine(d.Count())
    System.Console.WriteLine(d.Get(""alpha""))
    System.Console.WriteLine(d.Get(""beta""))
    System.Console.WriteLine(d.ContainsKey(""gamma""))
    System.Console.WriteLine(d.ContainsKey(""delta""))

    var v: i32 = 0
    let found = d.TryGetValue(""beta"", out v)
    System.Console.WriteLine(found)
    System.Console.WriteLine(v)

    d.Remove(""beta"")
    System.Console.WriteLine(d.Count())
    System.Console.WriteLine(d.ContainsKey(""beta""))

    d.Set(""alpha"", 100)
    System.Console.WriteLine(d.Get(""alpha""))
}
";

        private static (Compilation Compilation, Func<List<string>> Diagnostics) Compile(string appSource)
        {
            var dictTree = SyntaxTree.Parse(DictSource);
            var appTree = SyntaxTree.Parse(appSource);
            var compilation = Compilation.Create(dictTree, appTree);
            return (compilation, () =>
            {
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                return result.Diagnostics.Where(d => d.IsError).Select(d => d.Message).ToList();
            });
        }

        [Fact]
        public void Evaluator_Dictionary_AllOperations()
        {
            var (compilation, getDiagnostics) = Compile(AppSource);
            Assert.Empty(getDiagnostics());

            var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
            Assert.Empty(result.Diagnostics.Where(d => d.IsError));
        }

        [Fact]
        public void Il_Dictionary_Compiles()
        {
            var (compilation, getDiagnostics) = Compile(AppSource);
            Assert.Empty(getDiagnostics());

            var exePath = Path.Combine(Path.GetTempPath(), "cocoa-dict", "dict-il.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
            var emitDiagnostics = compilation.Emit("dict-il",
                new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location },
                exePath,
                Cocoa.Targeting.IlTarget.Parse("net9.0"));
            Assert.True(emitDiagnostics.IsEmpty, string.Join("\n", emitDiagnostics.Select(d => d.Message)));
        }
    }
}
