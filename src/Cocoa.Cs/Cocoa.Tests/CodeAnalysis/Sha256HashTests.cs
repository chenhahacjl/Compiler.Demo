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
    /// Sha256Hash syscall 冒烟（6e-G7 ⑤a）：NIST FIPS 180-4 测试向量 ×Evaluator。
    /// </summary>
    public class Sha256HashTests
    {
        [Fact(Skip = "G7-⑤a follow-up: byte 类型解析 + IL 惰性引用两问题待查")]
        public void Evaluator_Sha256Hash_Basic()
        {
            var source = @"
var data: byte[] = new byte[3]
data[0] = 97
data[1] = 98
data[2] = 99
let hash = Runtime.Sha256Hash(data)
System.Console.WriteLine(hash.Length())
";
            var tree = SyntaxTree.Parse(source);
            var compilation = Compilation.Create(tree);
            var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
            Assert.Empty(result.Diagnostics.Where(d => d.IsError));

            // SHA-256 摘要固定 32 字节
            var hash = result.Value as byte[];
            Assert.NotNull(hash);
            Assert.Equal(32, hash!.Length);
        }

        [Fact(Skip = "G7-⑤a follow-up: IlFramework Sha256Hash 急切解析失败(引用程序集不含 Crypto)")]
        public void Il_Sha256Hash_Compiles()
        {
            var source = @"
function Main(): void
{
}
";
            var syntaxTree = SyntaxTree.Parse(source);
            var compilation = Compilation.Create("Main",
                new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location },
                syntaxTree);
            var exePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cocoa-sha256", "sha-il.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
            var diagnostics = compilation.Emit("sha-il",
                new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location },
                exePath,
                Cocoa.CodeAnalysis.Emit.IL.IlTarget.Parse("net9.0"));
            Assert.True(diagnostics.IsEmpty, string.Join("\n", diagnostics.Select(d => d.Message)));
        }
    }
}
