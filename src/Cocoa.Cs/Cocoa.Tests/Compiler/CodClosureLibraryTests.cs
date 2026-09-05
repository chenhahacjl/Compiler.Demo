using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace Cocoa.Tests.Compiler
{
    /// <summary>
    /// 阶段 6 Step F 6f-4：捕获闭包经库（__Env_* 环境类 .coa 重建 + A1 库发射）——
    /// 库返回捕获闭包（按值捕捉）与有状态闭包（可变局部捕捉），消费方运行期调用验证：
    /// 环境元数据（IsLambda/IsLambdaWithEnvironment/EnvironmentClass/CapturedVariables）跨库往返完整。
    /// </summary>
    public class CodClosureLibraryTests
    {
        [Fact]
        public void Library_With_Capturing_And_Stateful_Closures_Runs_EndToEnd()
        {
            var root = CliTestRunner.NewTempDir("cod-closure");
            var libDir = Path.Combine(root, "ClosureLib");
            var appDir = Path.Combine(root, "App");
            Directory.CreateDirectory(libDir);
            Directory.CreateDirectory(appDir);

            File.WriteAllText(Path.Combine(libDir, "ClosureLib.co"), @"namespace ClosureLib
{
    public function MakeAdder(n: i32): (i32) -> i32
    {
        return (x: i32) => x + n
    }

    public function Counter(): (i32) -> i32
    {
        var c = 0
        return (x: i32) =>
        {
            c = c + x
            return c
        }
    }
}
");
            File.WriteAllText(Path.Combine(libDir, "ClosureLib.cocproj"), @"name = ClosureLib
output = cocoa

[sources]
*.co
");

            File.WriteAllText(Path.Combine(appDir, "main.co"), @"using ClosureLib
using System

function Main(): void
{
    var add5 = ClosureLib.MakeAdder(5)
    System.Console.WriteLine(add5(37))

    var cnt = ClosureLib.Counter()
    System.Console.WriteLine(cnt(10))
    System.Console.WriteLine(cnt(32))
}
");
            File.WriteAllText(Path.Combine(appDir, "App.cocproj"), @"name = App
output = executable
entry = Main

[sources]
*.co

[references]
../ClosureLib/ClosureLib.coa
");

            var libProject = Path.Combine(libDir, "ClosureLib.cocproj");
            var libBuild = CliTestRunner.Run($"build \"{libProject}\"", root);
            Assert.True(libBuild.ExitCode == 0, $"lib build failed: {libBuild.Stdout}{libBuild.Stderr}");
            Assert.True(File.Exists(Path.Combine(libDir, "ClosureLib.coa")));

            // 记录闭包元数据确已随库携带（IsLambda/EnvClass/捕获清单）
            var libText = File.ReadAllText(Path.Combine(libDir, "ClosureLib.coa"));
            Assert.Contains("envl:true", libText);
            Assert.Contains("envc:__Env_MakeAdder", libText);
            Assert.Contains("envcap:1", libText);

            var appProject = Path.Combine(appDir, "App.cocproj");
            var appBuild = CliTestRunner.Run($"build \"{appProject}\" -b dotnet --dotnet-runtime net9.0", root);
            Assert.True(appBuild.ExitCode == 0, $"app build failed: {appBuild.Stdout}{appBuild.Stderr}");
            Assert.True(File.Exists(Path.Combine(appDir, "App.exe")));
            Assert.True(File.Exists(Path.Combine(appDir, "ClosureLib.Managed.dll")), "库托管 dll 应按需生成（捕获闭包宿主）");

            var run = RunExe(Path.Combine(appDir, "App.exe"));
            Assert.True(run.exitCode == 0, $"app run failed: {run.output}");
            Assert.Contains("42", run.output);
            Assert.Contains("10", run.output);
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
    }
}