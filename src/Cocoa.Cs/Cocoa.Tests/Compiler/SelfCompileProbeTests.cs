using Cocoa.CodeAnalysis;
using System;
using System.IO;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Cocoa.Tests.Compiler
{
    public class SelfCompileProbeTests
    {
        private readonly ITestOutputHelper _out;
        public SelfCompileProbeTests(ITestOutputHelper output) { _out = output; }

        [Fact(Skip = "阶段8 B1→B2：B1 全量语料自举已产出可加载 DLL（~232K 字节）；B1-run 被系统性元数据 #US/字符串 token 发射问题阻断（PrepareMethod 全方法验证无效，根因待查）。猎错基建：HuntInvalid_FromSavedB1 + DumpMainIL_FromSavedB1（读 %TEMP%\\cocoa-b1-probe\\B1.dll）")]
        public void CompileFullSelfCompilerSource()
        {
            var root = AppContext.BaseDirectory;
            while (root != null && !File.Exists(Path.Combine(root, "src", "Cocoa.SDK", "System.Core", "String.co")))
            {
                root = Path.GetDirectoryName(root);
            }

            var compilerDir = Path.Combine(root!, "src", "Cocoa.Co", "Cocoa.Compiler");
            var allFiles = Directory.GetFiles(compilerDir, "*.co", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.Ordinal).ToArray();
            var allSrc = string.Join(Environment.NewLine, allFiles.Select(f => File.ReadAllText(f)));
            _out.WriteLine("full corpus chars: " + allSrc.Length);

            // B1 = 全量编译器自举产物：语料 + Main(args){ BuildDllHex(args[0]) }（自包含编译器 DLL）
            var master = allSrc + Environment.NewLine +
                "function Main(args: string[]): i32 {" + Environment.NewLine +
                "    let h = Cocoa.CodeGen.IlDriver.BuildDllHex(args[0])" + Environment.NewLine +
                "    System.Console.WriteLine(\"B2:\" + h)" + Environment.NewLine +
                "    return 0" + Environment.NewLine +
                "}" + Environment.NewLine;

            var trees = System.Collections.Immutable.ImmutableArray.CreateBuilder<Cocoa.CodeAnalysis.Syntax.SyntaxTree>();
            foreach (var f in allFiles)
            {
                trees.Add(Cocoa.CodeAnalysis.Syntax.SyntaxTree.Parse(File.ReadAllText(f)));
            }

            var mainTree = Cocoa.CodeAnalysis.Syntax.SyntaxTree.Parse(
                "using System\n" +
                "function Main(args: string[]): i32\n{\n" +
                "    let h = Cocoa.CodeGen.IlDriver.BuildDllHex(args[0])\n" +
                "    System.Console.WriteLine(\"R:\" + h)\n" +
                "    return 0\n}\n");
            trees.Add(mainTree);

            // 阶段 8 B1：参考端驱动自编全量语料 → B1 十六进制
            var original = Console.Out;
            string b1Hex;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);
                var compilation = Cocoa.CodeAnalysis.Compilation.Create("Main",
                    new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location },
                    trees.ToArray());
                var result = compilation.Evaluate(new[] { master }, new System.Collections.Generic.Dictionary<Cocoa.CodeAnalysis.Symbols.VariableSymbol, object>());
                var output = writer.ToString().Replace("\r\n", "\n");
                var diag = result.Diagnostics.HasErrors() ? string.Join(" | ", result.Diagnostics.Select(d => d.Message)) : "";
                Console.SetOut(original);
                Assert.True(result.Diagnostics.HasErrors() == false, "COCOMPILE-ERROR: " + diag);
                Assert.NotNull(result.Value);
                Assert.Equal(0, (int)result.Value!);
                var rLine = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("R:", StringComparison.Ordinal));
                Assert.NotNull(rLine);
                b1Hex = rLine![2..].Trim();
            }
            finally
            {
                Console.SetOut(original);
            }

            Assert.False(b1Hex.StartsWith("ERR:", StringComparison.Ordinal), "B1 自举阻塞: " + b1Hex);
            Assert.True(b1Hex.Length > 200000, "B1 hex 过短 (" + b1Hex.Length + ")");
            _out.WriteLine("B1 hex length: " + (b1Hex.Length / 2));

            // B2：运行 B1 可执行 DLL（自包含编译器）→ 以 args=[allSrc] 再编全量语料 → B2 == B1（确定性）
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-b1", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var b1Path = Path.Combine(dir, "B1.dll");
            File.WriteAllBytes(b1Path, SelfHostedEndToEndTests.HexToBytes(b1Hex));
            var probeDir = Path.Combine(Path.GetTempPath(), "cocoa-b1-probe");
            Directory.CreateDirectory(probeDir);
            File.WriteAllBytes(Path.Combine(probeDir, "B1.dll"), SelfHostedEndToEndTests.HexToBytes(b1Hex));
            _out.WriteLine("B1 saved: " + Path.Combine(probeDir, "B1.dll"));
            var b1Asm = System.Reflection.Assembly.LoadFile(b1Path);
            object? b1Exit;
            string? b2Line;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);
                b1Exit = b1Asm.EntryPoint!.Invoke(null, new object[] { new[] { allSrc } });
                var b1out = writer.ToString().Replace("\r\n", "\n");
                Console.SetOut(original);
                _out.WriteLine("B1 stdout head: " + (b1out.Length <= 300 ? b1out.Replace("\n", "\\n") : b1out[..300].Replace("\n", "\\n")));
                b2Line = b1out.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("B2:", StringComparison.Ordinal));
            }
            finally
            {
                Console.SetOut(original);
            }

            Assert.Equal(0, (int)b1Exit!);
            Assert.True(b2Line != null && b2Line.Length > 4, "B1 未产出 B2 行");
            var b2Hex = b2Line![3..].Trim();
            _out.WriteLine("B2 hex length: " + (b2Hex.Length / 2));

            Assert.Equal(b1Hex, b2Hex);

            // B2 可加载可运行（Main(args) → 0）
            var b2Path = Path.Combine(dir, "B2.dll");
            File.WriteAllBytes(b2Path, SelfHostedEndToEndTests.HexToBytes(b2Hex));
            var b2Asm = System.Reflection.Assembly.LoadFile(b2Path);
            Assert.Equal(0, (int)b2Asm.EntryPoint!.Invoke(null, new object[] { new[] { "function Main(args: string[]): i32 { return 0 }\n" } })!);
            _out.WriteLine("B2 types: " + b2Asm.GetTypes().Length);
        }
    }
}