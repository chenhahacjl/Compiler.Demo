using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Syntax;
using Xunit;

namespace Cocoa.Tests.Compiler
{
    /// <summary>
    /// 阶段 6 Step E/6f-3：跨库同名类型歧义消解——装载不再抛错，改为登记歧义全名集；
    /// 非限定使用 → 绑定期诊断；`using X = 库名.全名` 库限定别名唯一化（绑定侧 + 读侧前缀消歧）。
    /// </summary>
    public class CodAmbiguousNameTests
    {
        private static string WriteLib(string dir, string libName, string value)
        {
            var source = $@"
namespace Shared
{{
    public class Conflict
    {{
        private _v: i32

        public function Value(): i32
        {{
            return {value}
        }}
    }}
}}
";
            var path = Path.Combine(dir, libName + ".coa");
            var compilation = Compilation.Create(SyntaxTree.Parse(source));
            var diagnostics = compilation.EmitCocoa(libName, path);
            Assert.False(diagnostics.HasErrors());
            return path;
        }

        private static string NewDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "coa-amb-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void Same_Type_Across_Two_Libraries_Records_Ambiguity_And_Unqualified_Use_Diagnoses()
        {
            var dir = NewDir();
            var lib1 = WriteLib(dir, "LibOne", "1");
            var lib2 = WriteLib(dir, "LibTwo", "2");

            // 6f-3：装载不再抛错，歧义全名登记到编译
            var compilation = Compilation.Create(new[] { lib1, lib2 },
                SyntaxTree.Parse("function Main(): i32 { return 0 }"));
            Assert.Contains("Shared.Conflict", compilation.AmbiguousCodTypeNames);

            // 非限定使用（`using Shared` + `Conflict`）→ 绑定期诊断，不再静默 first-wins
            var usingCompilation = Compilation.Create(new[] { lib1, lib2 }, SyntaxTree.Parse(@"
using Shared
function Main(): i32
{
    let c = new Conflict()
    return 0
}
"));
            var diagnostics = usingCompilation.GetDiagnostics();
            Assert.True(diagnostics.Any(d => d.IsError),
                "歧义类型非限定使用应报诊断：" + string.Join(" | ", diagnostics.Select(d => d.Message)));
            Assert.Contains(diagnostics, d => d.IsError && d.Message.Contains("Conflict", StringComparison.Ordinal));
        }

        [Fact]
        public void Alias_Disambiguates_Library_Scoped_Type_Binds()
        {
            var dir = NewDir();
            var lib1 = WriteLib(dir, "LibOne", "1");
            var lib2 = WriteLib(dir, "LibTwo", "2");

            var compilation = Compilation.Create(new[] { lib1, lib2 }, linkCodDynamically: true,
                SyntaxTree.Parse(@"
using C = LibOne.Shared.Conflict
function Main(): i32
{
    let c = new C()
    return c.Value()
}
"));
            var exePath = Path.Combine(dir, "app.exe");
            var diagnostics = compilation.Emit("app-exe",
                new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location, lib1, lib2 },
                exePath, Cocoa.Targeting.IlTarget.Parse("net9.0"));
            Assert.False(diagnostics.HasErrors(),
                "库限定别名应唯一化解析：" + string.Join(" | ", diagnostics.Select(d => d.Message)));
            Assert.True(File.Exists(exePath));
        }

        [Fact]
        public void Single_Library_Reference_Binds_Normally()
        {
            var dir = NewDir();
            var lib1 = WriteLib(dir, "LibOne", "1");

            // 真用途：动态链接消费（linkCodDynamically）——实例化库类、调用实例方法并产出可执行 dll 引用
            var compilation = Compilation.Create(new[] { lib1 }, linkCodDynamically: true,
                SyntaxTree.Parse(@"
using Shared
function Main(): i32
{
    let c = new Conflict()
    return c.Value()
}
"));
            var exePath = Path.Combine(dir, "app.exe");
            var diagnostics = compilation.Emit("app-exe",
                new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location, lib1 },
                exePath, Cocoa.Targeting.IlTarget.Parse("net9.0"));
            Assert.False(diagnostics.HasErrors(),
                "单库实例类消费应无诊断：" + string.Join(" | ", diagnostics.Select(d => d.Message)));
            Assert.True(File.Exists(exePath));
        }

        [Fact]
        public void Alias_Chain_App_Consumes_Middle_Library_Runs()
        {
            // App → LibMid → LibOne：LibMid 引用 LibOne+LibTwo（同全名 Shared.Conflict），
            // 以 `using C = LibOne.Shared.Conflict` 唯一化；其 .coa 体携带 LibOne 前缀键，
            // 消费方读侧经库前缀消歧恢复符号 → 运行期贯通（输出 1）
            var root = CliTestRunner.NewTempDir("cod-amb-chain");
            var libOne = Path.Combine(root, "LibOne");
            var libTwo = Path.Combine(root, "LibTwo");
            var mid = Path.Combine(root, "LibMid");
            var appDir = Path.Combine(root, "App");
            Directory.CreateDirectory(libOne);
            Directory.CreateDirectory(libTwo);
            Directory.CreateDirectory(mid);
            Directory.CreateDirectory(appDir);

            File.WriteAllText(Path.Combine(libOne, "LibOne.co"), "namespace Shared\n{\n    public class Conflict\n    {\n        public function Value(): i32\n        {\n            return 1\n        }\n    }\n}\n");
            File.WriteAllText(Path.Combine(libOne, "LibOne.cocproj"), "name = LibOne\noutput = cocoa\n\n[sources]\n*.co\n");
            File.WriteAllText(Path.Combine(libTwo, "LibTwo.co"), "namespace Shared\n{\n    public class Conflict\n    {\n        public function Value(): i32\n        {\n            return 2\n        }\n    }\n}\n");
            File.WriteAllText(Path.Combine(libTwo, "LibTwo.cocproj"), "name = LibTwo\noutput = cocoa\n\n[sources]\n*.co\n");

            File.WriteAllText(Path.Combine(mid, "LibMid.co"), @"namespace Mid
{
    using C = LibOne.Shared.Conflict

    function Get(): i32
    {
        let c = new C()
        return c.Value()
    }
}
");
            File.WriteAllText(Path.Combine(mid, "LibMid.cocproj"), @"name = LibMid
output = cocoa

[sources]
*.co

[references]
../LibOne/LibOne.coa
../LibTwo/LibTwo.coa
");

            File.WriteAllText(Path.Combine(appDir, "main.co"), "using Mid\nfunction Main(): void\n{\n    Console.WriteLine(Get())\n}\n");
            File.WriteAllText(Path.Combine(appDir, "App.cocproj"), @"name = App
output = executable
entry = Main

[sources]
*.co

[references]
../LibOne/LibOne.coa
../LibTwo/LibTwo.coa
../LibMid/LibMid.coa
");

            Assert.True(CliTestRunner.Run($"build \"{Path.Combine(libOne, "LibOne.cocproj")}\"", root).ExitCode == 0, "LibOne build failed");
            Assert.True(CliTestRunner.Run($"build \"{Path.Combine(libTwo, "LibTwo.cocproj")}\"", root).ExitCode == 0, "LibTwo build failed");
            Assert.True(CliTestRunner.Run($"build \"{Path.Combine(mid, "LibMid.cocproj")}\"", root).ExitCode == 0, "LibMid build failed");

            var appProject = Path.Combine(appDir, "App.cocproj");
            var appBuild = CliTestRunner.Run($"build \"{appProject}\" -b dotnet --dotnet-runtime net9.0", root);
            Assert.True(appBuild.ExitCode == 0, $"app build failed: {appBuild.Stdout}{appBuild.Stderr}");
            Assert.True(File.Exists(Path.Combine(appDir, "App.exe")));

            var exePath = Path.Combine(appDir, "App.exe");
            var psi = new ProcessStartInfo(exePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(30000);
            Assert.True(process.ExitCode == 0, $"app run failed exit={process.ExitCode}: {stdout}{stderr}");
            Assert.Contains("1", stdout);
        }
    }
}