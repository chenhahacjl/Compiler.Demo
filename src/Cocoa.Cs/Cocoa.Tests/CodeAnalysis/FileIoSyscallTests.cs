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
    /// 文件 IO / 环境 syscall 冒烟（6e-G7 ④）。
    /// 通过 stdlib 注入的 System.Core.coa 使用新增 syscall。
    /// </summary>
    public class FileIoSyscallTests
    {
        [Fact]
        public void Evaluator_FileRoundTrip()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-fio", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var testFile = Path.Combine(dir, "test.txt").Replace("\\", "/");

            var source = $@"
var path = ""{testFile}""
File.WriteAllText(path, ""hello G7"")
let ok = File.Exists(path)
let content = File.ReadAllText(path)
System.Console.WriteLine(ok)
System.Console.WriteLine(content)
";

            var tree = SyntaxTree.Parse(source);
            var compilation = Compilation.Create(tree);
            var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
            Assert.Empty(result.Diagnostics.Where(d => d.IsError));

            Assert.True(File.Exists(testFile));
            Assert.Equal("hello G7", File.ReadAllText(testFile));
        }

        [Fact]
        public void Evaluator_GetEnvironmentVariable()
        {
            Environment.SetEnvironmentVariable("__G7_TEST__", "hello_env");
            try
            {
                var source = "System.Console.WriteLine(Environment.GetEnvironmentVariable(\"__G7_TEST__\"))";
                var tree = SyntaxTree.Parse(source);
                var compilation = Compilation.Create(tree);
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                Assert.Empty(result.Diagnostics.Where(d => d.IsError));
            }
            finally
            {
                Environment.SetEnvironmentVariable("__G7_TEST__", null);
            }
        }

        [Fact]
        public void Evaluator_GetCurrentDirectory()
        {
            var source = "System.Console.WriteLine(Environment.GetCurrentDirectory())";
            var tree = SyntaxTree.Parse(source);
            var compilation = Compilation.Create(tree);
            var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
            Assert.Empty(result.Diagnostics.Where(d => d.IsError));
        }
    }
}
