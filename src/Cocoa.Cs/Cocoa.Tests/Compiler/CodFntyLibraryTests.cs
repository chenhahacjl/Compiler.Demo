using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace Cocoa.Tests.Compiler
{
    /// <summary>
    /// 阶段 6 Step D-a：lambda/函数值库体（fnval/invoc 节点）.coa 序列化 round-trip——
    /// 库内高阶函数消费 fnty 参数并间接调用；消费方把自身 lambda 传入库函数。
    /// 捕获闭包（__Env_* 宿主装饰经 .coa 重建）另记边界（Step F 追）。
    /// </summary>
    public class CodFntyLibraryTests
    {
        [Fact]
        public void Library_With_HigherOrder_Functions_RoundTrips_And_Runs()
        {
            var root = CliTestRunner.NewTempDir("cod-fnty");
            var libDir = Path.Combine(root, "MathLib");
            var appDir = Path.Combine(root, "App");
            Directory.CreateDirectory(libDir);
            Directory.CreateDirectory(appDir);

            File.WriteAllText(Path.Combine(libDir, "Math.co"), @"namespace MathLib
{
    public function ApplyThe(f: (i32) -> i32, v: i32): i32
    {
        return f(v)
    }

    public function Identity(): (i32) -> i32
    {
        return (x: i32) => x
    }
}
");
File.WriteAllText(Path.Combine(libDir, "MathLib.cocproj"), @"name = MathLib
output = cocoa

[sources]
*.co
");

            File.WriteAllText(Path.Combine(appDir, "app.co"), @"using MathLib
using System

function Main(): i32
{
    var double2 = (x: i32) => x * 2
    var r = MathLib.ApplyThe(double2, 21)
    var id = MathLib.Identity()
    var m = id(5)
    System.Console.WriteLine(r)
    System.Console.WriteLine(m)
    return 0
}
");
            File.WriteAllText(Path.Combine(appDir, "App.cocproj"), @"name = App
output = executable
entry = Main

[sources]
*.co

[references]
../MathLib/MathLib.coa
");

            var libProject = Path.Combine(libDir, "MathLib.cocproj");
            var libBuild = CliTestRunner.Run($"build \"{libProject}\"", root);
            Assert.True(libBuild.ExitCode == 0, $"lib build failed: {libBuild.Stdout}{libBuild.Stderr}");
            Assert.True(File.Exists(Path.Combine(libDir, "MathLib.coa")));

            var appProject = Path.Combine(appDir, "App.cocproj");
            var appBuild = CliTestRunner.Run($"build \"{appProject}\" -b dotnet --dotnet-runtime net9.0", root);
            Assert.True(appBuild.ExitCode == 0, $"app build failed: {appBuild.Stdout}{appBuild.Stderr}");
            Assert.True(File.Exists(Path.Combine(appDir, "App.exe")));
            Assert.True(File.Exists(Path.Combine(appDir, "MathLib.Managed.dll")), "库托管 dll 应按需生成（fnval 消费）");

            var run = RunExe(Path.Combine(appDir, "App.exe"));
            Assert.True(run.exitCode == 0, $"app run failed: {run.output}");
            Assert.Contains("42", run.output);
            Assert.Contains("5", run.output);
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