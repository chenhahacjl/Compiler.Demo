using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Emit.Native;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.Tests.CodeAnalysis.Emit.IL;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// 索引器 `this[i]` + `List&lt;T&gt;.Count` 属性消费端锁定（自举缺口 §4.4，M0-3）：
    /// 集合源码方式集成（List&lt;T&gt; 待 G7 .cod 化），本测试锁定 `list[i]` 读 / `list[i] = x` 写 /
    /// `list.Count` 属性 / `RemoveAt` 在 Evaluator / IL / native x64 三后端的语义一致。
    /// </summary>
    public class IndexerSemanticsTests
    {
        private const string Source = @"namespace System.Collections.Generic
{
    public class List<T>
    {
        private _items: T[]
        private _count: i32

        public constructor()
        {
            _items = new T[4]
            _count = 0
        }

        public property Count: i32
        {
            get
            {
                return _count
            }
        }

        public property this[index: i32]: T
        {
            get
            {
                return _items[index]
            }
            set
            {
                _items[index] = value
            }
        }

        public function Add(item: T): void
        {
            if _count >= _items.Length
            {
                var grown = new T[_items.Length * 2]
                var i = 0
                while i < _count
                {
                    grown[i] = _items[i]
                    i = i + 1
                }
                _items = grown
            }
            _items[_count] = item
            _count = _count + 1
        }

        public function RemoveAt(index: i32): void
        {
            var i = index
            while i < _count - 1
            {
                _items[i] = _items[i + 1]
                i = i + 1
            }
            _count = _count - 1
        }
    }
}

using System

function Main(): i32
{
    var list = new List<i32>()
    list.Add(10)
    list.Add(20)
    list.Add(30)
    Console.WriteLine(list.Count)
    Console.WriteLine(list[0])
    Console.WriteLine(list[1])
    list[1] = 99
    Console.WriteLine(list[1])
    list.RemoveAt(0)
    Console.WriteLine(list[0])
    return 0
}";

        private const string ExpectedOutput = "3\n10\n20\n99\n99\n";

        [Fact]
        public void Evaluator_Indexer_CountProperty()
        {
            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);

                var tree = SyntaxTree.Parse(Source);
                var compilation = Compilation.Create(tree);
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());

                Assert.True(!result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                Assert.Equal(ExpectedOutput, writer.ToString().Replace("\r\n", "\n"));
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        [Fact]
        public void IlE2e_Indexer_CountProperty()
        {
            var (exitCode, stdout) = IlE2eTests.EmitAndRun(Source, "idx-il");
            Assert.Equal(0, exitCode);
            Assert.Equal(ExpectedOutput, stdout.Replace("\r\n", "\n"));
        }

        [Fact]
        public void NativeX64_Indexer_CountProperty()
        {
            var (exitCode, stdout) = EmitNativeAndRun(Source, "idx-native");
            Assert.Equal(0, exitCode);
            Assert.Equal(ExpectedOutput, stdout);
        }

        private static (int ExitCode, string Stdout) EmitNativeAndRun(string source, string name)
        {
            var directory = Path.Combine(Path.GetTempPath(), "cocoa-idx-smoke");
            Directory.CreateDirectory(directory);
            var exePath = Path.Combine(directory, name + ".exe");

            var syntaxTree = SyntaxTree.Parse(source);
            var compilation = Compilation.Create(syntaxTree);
            var diagnostics = compilation.EmitNative(name, exePath, new TargetPlatform(TargetOS.Windows, Architecture.X64));

            Assert.True(diagnostics.IsEmpty, string.Join("\n", diagnostics.Select(d => d.Message)));
            Assert.True(File.Exists(exePath));

            var psi = new ProcessStartInfo(exePath)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi)!;
            using var output = new MemoryStream();
            var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output);

            if (!process.WaitForExit(15000))
            {
                process.Kill();
                throw new TimeoutException("Native exe did not exit in time.");
            }

            outputTask.Wait();
            var stdout = Encoding.Unicode.GetString(output.ToArray()).Replace("\r\n", "\n").Replace("\r", "\n");
            return (process.ExitCode, stdout);
        }
    }
}