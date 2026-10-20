using Cocoa.CodeAnalysis;
using Cocoa.Engine;

namespace Cocoa.Host;

/// <summary>
/// CocoaEngine 嵌入式脚本引擎宿主示例（docs/嵌入式引擎API.md §11）。
/// 演示：DoString 执行 / Call 调用顶层函数 / RegisterCallback 注册 Co→C# 回调 /
/// GetGlobal·SetGlobal 读写全局变量 / Output 事件捕获脚本输出。
/// </summary>
internal static class Program
{
    private static void Main()
    {
        Console.WriteLine("== CocoaEngine 嵌入式引擎示例 ==");
        Console.WriteLine();

        using var engine = new CocoaEngine();

        // Output 事件：捕获脚本 WriteLine（仅 Interpreter 后端）
        engine.Output += (_, text) => Console.Write($"[co] {text}");

        // Co → C# 回调必须先于触发绑定的提交注册（API §7.3），脚本以 syscall function 调用
        engine.RegisterCallback("Mul", (Func<int, int, int>)((a, b) => a * b));

        // 执行一段 Co 脚本：声明顶层函数、全局变量，并调用注册的回调
        var result = engine.DoString("""
            using System

            class Host
            {
                syscall function Mul(a: i32, b: i32): i32
            }

            var counter = 0
            function Bump(steps: i32): i32
            {
                counter = counter + steps
                return counter
            }

            Console.WriteLine($"当前计数 = {Host.Mul(6, 7)}")
            """);

        if (result.Diagnostics.HasErrors())
        {
            foreach (var d in result.Diagnostics)
            {
                Console.Error.WriteLine($"  编译诊断: {d}");
            }

            return;
        }

        Console.WriteLine($"DoString 完成，最后值 = {Show(result.Value)}");

        // Co → C#：调用顶层函数（跨 submission 链共享）
        Console.WriteLine($"Call(\"Bump\", 5) = {engine.Call("Bump", 5)}");
        Console.WriteLine($"Call(\"Bump\", 2) = {engine.Call("Bump", 2)}");

        // GetGlobal / SetGlobal：读写脚本全局变量
        Console.WriteLine($"GetGlobal(\"counter\") = {engine.GetGlobal("counter")}");
        engine.SetGlobal("counter", 100);
        Console.WriteLine($"SetGlobal(\"counter\", 100) 后 = {engine.GetGlobal("counter")}");

        // 值映射：raw 值也返回（脚本顶层最后表达式）
        var value = engine.DoString("41 + 1");
        Console.WriteLine($"DoString(\"41 + 1\").Value = {value.Value}");

        // Call 类静态方法（点号限定名）+ 重载消解（按实参元数/精确类型）
        engine.DoString("""
            class Calc
            {
                public static function Describe(x: i32): string { return $"int:{x}" }
                public static function Describe(s: string): string { return $"string:{s}" }
            }
            """);
        Console.WriteLine($"Call(\"Calc.Describe\", 42) = {engine.Call("Calc.Describe", 42)}");
        Console.WriteLine($"Call(\"Calc.Describe\", \"hi\") = {engine.Call("Calc.Describe", "hi")}");

        // DoFile：脚本文件执行（Lua luaL_dofile 等价），文件声明的命名空间类静态方法可经 Call 调用
        var fileResult = engine.DoFile(Path.Combine(AppContext.BaseDirectory, "scripts", "demo.co"));
        if (fileResult.Diagnostics.HasErrors())
        {
            foreach (var d in fileResult.Diagnostics)
            {
                Console.Error.WriteLine($"  编译诊断: {d}");
            }

            return;
        }

        Console.WriteLine($"DoFile 完成（最后值 = {Show(fileResult.Value)}）");
        Console.WriteLine($"Call(\"Demo.Greeter.Greet\", \"Host\") = {engine.Call("Demo.Greeter.Greet", "Host")}");
        Console.WriteLine($"Call(\"Demo.Greeter.Greet\") = {engine.Call("Demo.Greeter.Greet")}");

        engine.Reset();
        Console.WriteLine();
        Console.WriteLine("Reset 完成。");
    }

    private static string Show(object? value) => value == null ? "<null>" : $"{value} ({value.GetType().Name})";
}