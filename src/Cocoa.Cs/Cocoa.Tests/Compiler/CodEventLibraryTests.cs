using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace Cocoa.Tests.Compiler
{
    /// <summary>
    /// 阶段 6 Step D-b：事件（evt）跨库 round-trip——库内普通实例类声明事件
    /// （cls 携带 evt 符号 + fld 后备字段，类方法体含触发脱糖），消费方实例化、+=
    /// 订阅并调用库方法触发。
    /// </summary>
    public class CodEventLibraryTests
    {
        [Fact]
        public void Library_With_EventClass_RoundTrips_Symbols_And_AppBuilds()
        {
            var root = CliTestRunner.NewTempDir("cod-evt");
            var libDir = Path.Combine(root, "GreeterLib");
            var appDir = Path.Combine(root, "App");
            Directory.CreateDirectory(libDir);
            Directory.CreateDirectory(appDir);

            File.WriteAllText(Path.Combine(libDir, "Greeter.co"), @"namespace GreeterLib
{
    public class Greeter
    {
        public event onGreet: (string) -> void

        public function Fire(msg: string): void
        {
            System.Console.WriteLine(""firing"")
            onGreet(msg)
        }
    }
}
");
            File.WriteAllText(Path.Combine(libDir, "GreeterLib.cocproj"), @"name = GreeterLib
output = cocoa

[sources]
*.co
");

            File.WriteAllText(Path.Combine(appDir, "app.co"), @"using GreeterLib
using System

function Main(): i32
{
    var g = new Greeter()
    g.onGreet += (m: string) =>
    {
        System.Console.WriteLine(m)
    }
    System.Console.WriteLine(""subscribed"")
    g.Fire(""hello"")
    // 再次触发不重订阅
    g.Fire(""world"")
    return 0
}
");
            File.WriteAllText(Path.Combine(appDir, "App.cocproj"), @"name = App
output = executable
entry = Main

[sources]
*.co

[references]
../GreeterLib/GreeterLib.coa
");

            var libProject = Path.Combine(libDir, "GreeterLib.cocproj");
            var libBuild = CliTestRunner.Run($"build \"{libProject}\"", root);
            Assert.True(libBuild.ExitCode == 0, $"lib build failed: {libBuild.Stdout}{libBuild.Stderr}");
            Assert.True(File.Exists(Path.Combine(libDir, "GreeterLib.coa")));

            var appProject = Path.Combine(appDir, "App.cocproj");
            var appBuild = CliTestRunner.Run($"build \"{appProject}\" -b dotnet --dotnet-runtime net9.0", root);
            Assert.True(appBuild.ExitCode == 0, $"app build failed: {appBuild.Stdout}{appBuild.Stderr}");
            Assert.True(File.Exists(Path.Combine(appDir, "App.exe")));
            Assert.True(File.Exists(Path.Combine(appDir, "GreeterLib.Managed.dll")), "库托管 dll 应按需生成（事件类实例化）");

            // 6e-Step D-b 边界：.coa 携带 evt 符号 + fld 后备字段 + 实例方法/ctor，消费方编译与托管 dll 物化通过；
            // 运行期事件往返（newobj MemberRef 挂接库内实例方法的符号同一性）待 Step F 完善后在此追加 run 断言。
            // 6f-2 检查记录：库 dll 缺实例方法体 → 运行期 MissingMethod(Greeter..ctor)。根因：写侧不序列化实例
            // 方法体，A1 库编译仅得到空壳；放开后读侧要求 System.Core!System.Console.WriteLine[@string] 在消费
            // 方注册表中可解析（当前 candidates=0/owner.Methods=0）→ 未闭环，暂回退写侧门。
            _ = AppDomain.CurrentDomain.BaseDirectory;
            Assert.True(File.Exists(Path.Combine(appDir, "App.dll")), "托管主程序集 App.dll 应在构建期产出");

            // Step F 6f-2 闭环：运行期事件往返（newobj MemberRef 挂接库内实例方法 + System 动态链接）
            var appDll = Path.Combine(appDir, "App.dll");
            var runPsi = new System.Diagnostics.ProcessStartInfo("dotnet", $"\"{appDll}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = appDir,
            };
            using (var runProcess = System.Diagnostics.Process.Start(runPsi)!)
            {
                var stdout = runProcess.StandardOutput.ReadToEnd();
                var stderr = runProcess.StandardError.ReadToEnd();
                runProcess.WaitForExit(30000);
                Assert.True(runProcess.ExitCode == 0, $"app run failed exit={runProcess.ExitCode}: {stdout}{stderr}");
                Assert.Contains("subscribed", stdout);
                Assert.Contains("hello", stdout);
                Assert.Contains("world", stdout);
            }
        }

        // （运行期断言延后至 Step F；见测试注释）
    }
}