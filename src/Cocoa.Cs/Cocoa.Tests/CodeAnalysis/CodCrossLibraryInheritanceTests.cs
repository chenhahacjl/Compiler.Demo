using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.Targeting;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// 6e-M25 前置（System.UI/Handle 硬需求）：跨库基类继承——
    /// 基类在另一个 `.coa`（含 BCL 接口如 IDisposable），派生库经 `extends base(...)` 构造链。
    /// 覆盖三处修复：① `.coa` 门禁放行 cod 来源基类；② IL 构造链跨库解析；③ 派生类不重列基类已实现接口。
    /// </summary>
    public class CodCrossLibraryInheritanceTests
    {
        private const string BaseLibSource = @"using System

namespace A
{
    public class Base extends IDisposable
    {
        public field Raw: nint

        public constructor(value: nint)
        {
            Raw = value
        }

        public function GetRaw(): nint
        {
            return Raw
        }

        public virtual function Dispose(): void
        {
        }
    }
}";

        private const string DerivedLibSource = @"using System
using A

namespace B
{
    public class Derived extends Base
    {
        public field Extra: i32

        public constructor(value: nint, extra: i32) extends base(value)
        {
            Extra = extra
        }

        public function Sum(): i64
        {
            return i64(Raw) + i64(Extra)
        }
    }
}";

        private const string Consumer = @"using System
using B

function Main(): i32
{
    let d = new Derived(40, 2)
    Console.WriteLine(d.Sum())
    Console.WriteLine(d.GetRaw() == 40)
    d.Dispose()
    return 0
}";

        private const string Expected = "42\nTrue\n";

        private static string[] Bcl() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private static string NewDir() => Path.Combine(Path.GetTempPath(), "cocoa-xlib-" + Guid.NewGuid().ToString("N"));

        private static string CompileBaseLib()
        {
            var dir = NewDir();
            Directory.CreateDirectory(dir);
            var output = Path.Combine(dir, "BaseLib.coa");

            var compilation = Compilation.Create(SyntaxTree.Parse(BaseLibSource));
            var diagnostics = compilation.EmitCocoa("BaseLib", output);
            Assert.True(diagnostics.IsEmpty, string.Join("\n", diagnostics));
            return output;
        }

        private static string CompileDerivedLib(string baseLibPath)
        {
            var dir = NewDir();
            Directory.CreateDirectory(dir);
            var output = Path.Combine(dir, "DerivedLib.coa");

            var compilation = Compilation.Create(new[] { baseLibPath }, SyntaxTree.Parse(DerivedLibSource));
            var diagnostics = compilation.EmitCocoa("DerivedLib", output);
            Assert.True(diagnostics.IsEmpty, string.Join("\n", diagnostics));
            return output;
        }

        [Fact]
        public void EmitCocoa_CrossLibraryBase_Serializes()
        {
            var baseLib = CompileBaseLib();
            var derivedLib = CompileDerivedLib(baseLib);
            Assert.True(File.Exists(derivedLib));
        }

        [Fact]
        public void Il_E2e_CrossLibraryInheritance()
        {
            var baseLib = CompileBaseLib();
            var derivedLib = CompileDerivedLib(baseLib);

            var references = new[] { baseLib, derivedLib }
                .Concat(Bcl())
                .ToArray();

            var compilation = Compilation.Create("Main", references, SyntaxTree.Parse(Consumer));

            var directory = Path.Combine(Path.GetTempPath(), "cocoa-xlib-il");
            Directory.CreateDirectory(directory);
            var exePath = Path.Combine(directory, Path.GetRandomFileName() + ".exe");

            var diagnostics = compilation.Emit("Main", references, exePath, IlTarget.Parse("net9.0"));
            Assert.Empty(diagnostics);
            Assert.True(File.Exists(exePath));

            var psi = new ProcessStartInfo("dotnet", "\"" + exePath + "\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(15000);

            Assert.True(process.ExitCode == 0, $"exit={process.ExitCode}\n{stdout}\n{stderr}");
            Assert.Equal(Expected, stdout.Replace("\r\n", "\n"));
        }
    }
}
