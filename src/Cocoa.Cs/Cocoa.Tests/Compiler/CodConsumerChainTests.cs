using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace Cocoa.Tests.Compiler
{
    /// <summary>
    /// 阶段 6 Step F 6f-2 链式动态链接：App → LibA → LibB 三级库链运行期贯通——
    /// LibA 的 Managed.dll 跨界调用 LibB.Managed（全目录 provenance：库间调用不落本地方法）。
    /// 透传依赖由引用侧显式提供（App 同时引用 LibB 与 LibA，LibB 在前）。
    /// </summary>
    public class CodConsumerChainTests
    {
        [Fact]
        public void App_Consumes_LibA_Which_Calls_LibB_Runs_EndToEnd()
        {
            var root = CliTestRunner.NewTempDir("cod-chain");
            var libB = Path.Combine(root, "LibB");
            var libA = Path.Combine(root, "LibA");
            var appDir = Path.Combine(root, "App");
            Directory.CreateDirectory(libB);
            Directory.CreateDirectory(libA);
            Directory.CreateDirectory(appDir);

            File.WriteAllText(Path.Combine(libB, "LibB.co"), "namespace B\n{\n    function Make(): i32\n    {\n        return 21\n    }\n}\n");
            File.WriteAllText(Path.Combine(libB, "LibB.cocproj"), "name = LibB\noutput = cocoa\n\n[sources]\n*.co\n");

            File.WriteAllText(Path.Combine(libA, "LibA.co"), "using B\nnamespace A\n{\n    function Double(): i32\n    {\n        return B.Make() * 2\n    }\n}\n");
            File.WriteAllText(Path.Combine(libA, "LibA.cocproj"), @"name = LibA
output = cocoa

[sources]
*.co

[references]
../LibB/LibB.coa
");

            File.WriteAllText(Path.Combine(appDir, "main.co"), "using A\nfunction Main(): void\n{\n    Console.WriteLine(A.Double())\n}\n");
            File.WriteAllText(Path.Combine(appDir, "App.cocproj"), @"name = App
output = executable
entry = Main

[sources]
*.co

[references]
../LibB/LibB.coa
../LibA/LibA.coa
");

            var libBBuild = CliTestRunner.Run($"build \"{Path.Combine(libB, "LibB.cocproj")}\"", root);
            Assert.True(libBBuild.ExitCode == 0, $"LibB build failed: {libBBuild.Stdout}{libBBuild.Stderr}");
            Assert.True(File.Exists(Path.Combine(libB, "LibB.coa")));

            var libABuild = CliTestRunner.Run($"build \"{Path.Combine(libA, "LibA.cocproj")}\"", root);
            Assert.True(libABuild.ExitCode == 0, $"LibA build failed: {libABuild.Stdout}{libABuild.Stderr}");
            Assert.True(File.Exists(Path.Combine(libA, "LibA.coa")));

            var appProject = Path.Combine(appDir, "App.cocproj");
            var appBuild = CliTestRunner.Run($"build \"{appProject}\" -b dotnet --dotnet-runtime net9.0", root);
            Assert.True(appBuild.ExitCode == 0, $"app build failed: {appBuild.Stdout}{appBuild.Stderr}");
            Assert.True(File.Exists(Path.Combine(appDir, "App.exe")));
            Assert.True(File.Exists(Path.Combine(appDir, "LibA.Managed.dll")), "LibA 托管 dll 应按需生成");
            Assert.True(File.Exists(Path.Combine(appDir, "LibB.Managed.dll")), "LibB 托管 dll 应为 LibA 依赖一并部署");

            var exePath = Path.Combine(appDir, "App.exe");
            var run = Start(exePath);
            Assert.True(run.ExitCode == 0, $"app run failed exit={run.ExitCode}: {run.Stdout}{run.Stderr}");
            Assert.Contains("42", run.Stdout);
        }

        private static (int ExitCode, string Stdout, string Stderr) Start(string exePath)
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
            return (process.ExitCode, stdout, stderr);
        }
    }
}