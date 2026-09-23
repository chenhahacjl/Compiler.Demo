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

        [Fact(Skip = "C-4 自举闭环：已修 usTexts/bcl 定长32溢出(→2048)、bool/char/类数组元素、静态调用返回类型推断；下一阻塞=同类实例方法裸调用（ReportError 无 this.）需 emitter 当前类解析")]
        public void CompileFullSelfCompilerSource()
        {
            var root = AppContext.BaseDirectory;
            while (root != null && !File.Exists(Path.Combine(root, "src", "Cocoa.SDK", "System.Core", "String.co")))
            {
                root = Path.GetDirectoryName(root);
            }

            var compilerDir = Path.Combine(root!, "src", "Cocoa.Co", "Cocoa.Compiler");
            var files = Directory.GetFiles(compilerDir, "*.co", SearchOption.AllDirectories)
                .Where(f => f.Replace('\\', '/').Contains("/Binding/") ||
                            f.Replace('\\', '/').Contains("/Syntax/") ||
                            f.Replace('\\', '/').Contains("/Symbols/"))
                .OrderBy(f => f, StringComparer.Ordinal).ToArray();
            var sb = new System.Text.StringBuilder();
            foreach (var f in files)
            {
                sb.AppendLine(File.ReadAllText(f));
            }

            sb.AppendLine("function Main(): i32 { return 0 }");
            var full = sb.ToString();
            _out.WriteLine("total source chars: " + full.Length);

            var hex = SelfHostedEndToEndTests.RunSelfDriver(full);
            Assert.False(hex.StartsWith("ERR:", StringComparison.Ordinal), "self-compile blocked: " + hex);

            var dir = Path.Combine(Path.GetTempPath(), "cocoa-b1", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var dllPath = Path.Combine(dir, "B1.dll");
            File.WriteAllBytes(dllPath, SelfHostedEndToEndTests.HexToBytes(hex));

            var asm = System.Reflection.Assembly.LoadFile(dllPath);
            var entry = asm.EntryPoint!.Invoke(null, null);
            _out.WriteLine("B1 entry exit: " + entry);
            Assert.Equal(0, (int)entry!);
            _out.WriteLine("emitted types: " + asm.GetTypes().Length);
        }
    }
}











