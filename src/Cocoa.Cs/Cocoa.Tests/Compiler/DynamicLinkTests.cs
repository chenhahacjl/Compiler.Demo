using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Xunit;

namespace Cocoa.Tests.Compiler
{
    /// <summary>
    /// 动态链接（阶段 A）端到端：库构建只产 cod；消费方按需生成 X.Managed.dll 并部署；
    /// 部署 dll 被误删后增量重建自动自愈（lazy 模型核心承诺）。
    /// </summary>
    public class DynamicLinkTests
    {
        [Fact]
        public void Cod_Consumer_Generates_And_SelfHeals_ManagedDlls()
        {
            var root = CliTestRunner.NewTempDir("dynlink");
            var libDir = Path.Combine(root, "Lib");
            var appDir = Path.Combine(root, "App");
            Directory.CreateDirectory(libDir);
            Directory.CreateDirectory(appDir);

            File.WriteAllText(Path.Combine(libDir, "Lib.co"), @"
namespace MyLib
{
    function Add(a: i32, b: i32): i32
    {
        return a + b
    }

    function Triple(x: i32): i32
    {
        return Add(x, Add(x, x))
    }
}
");
            File.WriteAllText(Path.Combine(libDir, "Lib.cocproj"), "name = Lib\noutput = cocoa\n\n[sources]\n*.co\n");

            File.WriteAllText(Path.Combine(appDir, "main.co"), "using MyLib\nfunction Main(): void\n{\n    Console.WriteLine(Triple(3))\n}\n");
            File.WriteAllText(Path.Combine(appDir, "App.cocproj"), "name = App\noutput = executable\nentry = Main\n\n[sources]\n*.co\n\n[references]\n../Lib/Lib.coa\n");

            // 1. 库构建：只产出 cod，不预生成任何 dll（lazy）
            var libBuild = CliTestRunner.Run($"build \"{Path.Combine(libDir, "Lib.cocproj")}\"", root);
            Assert.Equal(0, libBuild.ExitCode);
            Assert.True(File.Exists(Path.Combine(libDir, "Lib.coa")));
            Assert.Empty(Directory.EnumerateFiles(libDir, "*.dll"));

            // 2. 消费方构建：按需生成并部署 Lib.Managed.dll + System.Core.Managed.dll
            var appProject = Path.Combine(appDir, "App.cocproj");
            var appBuild = CliTestRunner.Run($"build \"{appProject}\" -b dotnet --dotnet-runtime net9.0", root);
            Assert.True(appBuild.ExitCode == 0, $"app build failed: {appBuild.Stdout}{appBuild.Stderr}");
            Assert.True(File.Exists(Path.Combine(appDir, "App.exe")));
            Assert.True(File.Exists(Path.Combine(appDir, "Lib.Managed.dll")), "用户库托管 dll 应按需生成");
            Assert.True(File.Exists(Path.Combine(appDir, "System.Core.Managed.dll")), "系统库托管 dll 应按需生成");

            var exePath = Path.Combine(appDir, "App.exe");
            var run1 = RunExe(exePath);
            Assert.Equal(0, run1.exitCode);
            Assert.Contains("9", run1.output);

            // 3. 自愈：部署的库 dll + stamp 全删 → 增量重建（up-to-date 分支）现场再生 → 运行恢复
            foreach (var dll in Directory.EnumerateFiles(appDir, "*.Managed.dll"))
            {
                File.Delete(dll);
                File.Delete(dll + ".stamp");
            }

            var rebuild = CliTestRunner.Run($"build \"{appProject}\" -b dotnet --dotnet-runtime net9.0", root);
            Assert.Equal(0, rebuild.ExitCode);
            Assert.True(File.Exists(Path.Combine(appDir, "Lib.Managed.dll")), "误删后应自动再生");
            Assert.True(File.Exists(Path.Combine(appDir, "System.Core.Managed.dll")), "系统库 dll 误删后应自动再生");

            var run2 = RunExe(exePath);
            Assert.Equal(0, run2.exitCode);
            Assert.Contains("9", run2.output);
        }

        private static (int exitCode, string output) RunExe(string exePath)
        {
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
            return (process.ExitCode, stdout + stderr);
        }

        [Fact]
        public void Closure_BareCli_UnifiedDynamicLink_DeploysManagedDlls()
        {
            // 阶段 6 并轨 Step C：裸 CLI（cocoa file.co -b dotnet）与 cocoa build 统一动态链接——
            // 产物依赖系统托管 dll（不内联库体），运行期布局 = apphost + app.dll + X.Managed.dll。
            // 闭包经统一链路求值，回归"裸 CLI 只测内联单 dll"的盲区。
            var dir = CliTestRunner.NewTempDir("closure-cli");
            File.WriteAllText(Path.Combine(dir, "Main.co"), @"using System
function Main(): i32
{
    var x = 21
    var f: (i32) -> i32 = (v: i32) => x + v
    Console.WriteLine(f(21))
    return 0
}
");

            var build = CliTestRunner.Run("\"Main.co\" -b dotnet -o app.exe", dir);
            Assert.True(build.ExitCode == 0, $"bare cli build failed: {build.Stdout}{build.Stderr}");

            Assert.True(File.Exists(Path.Combine(dir, "System.Core.Managed.dll")), "系统库托管 dll 应按需生成（统一动态链接）");
            Assert.True(File.Exists(Path.Combine(dir, "System.Core.Managed.dll.stamp")), "stamp 应随托管 dll 生成");
            Assert.True(File.Exists(Path.Combine(dir, "app.exe")), "apphost 应存在");

            var run = RunExe(Path.Combine(dir, "app.exe"));
            Assert.Equal(0, run.exitCode);
            Assert.Contains("42", run.output);
        }
    }
}
