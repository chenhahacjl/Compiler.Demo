using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Cod;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System.IO;
using System.Linq;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Cod
{
    public class CodSerializerTests
    {
        private static readonly string TestRoot = Path.Combine(Path.GetTempPath(), "cocoa-cod-tests");

        private const string LibrarySource = @"
namespace MyLib
{
    public enum Color { Red, Green, Blue }

    function Add(a: int, b: int): int
    {
        return a + b
    }

    function Factorial(n: int): int
    {
        var result = 1
        for var i = 1 to n
        {
            result = result * i
        }
        return result
    }

    function Sum(items: int[]): int
    {
        var total = 0
        for var i = 0 to items.Length - 1
        {
            total = total + items[i]
        }
        return total
    }

    function IsGreen(c: Color): bool
    {
        return c == Color.Green
    }

    function Countdown(n: int): int
    {
        var steps = 0
        var current = n
        while current > 0
        {
            steps = steps + 1
            current = current - 1
        }
        return steps
    }

    function Greet(name: string): string
    {
        return ""Hello "" + name
    }
}
";

        private static string NewDir()
        {
            var dir = Path.Combine(TestRoot, System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string EmitLibrary(string dir, string source)
        {
            var libPath = Path.Combine(dir, "Lib.co");
            File.WriteAllText(libPath, source);
            var compilation = Compilation.Create(SyntaxTree.Load(libPath));

            var output = Path.Combine(dir, "Lib.cod");
            var diagnostics = compilation.EmitCocoa("Lib", output);
            Assert.True(diagnostics.Length == 0, string.Join("; ", diagnostics));

            return output;
        }

        [Fact]
        public void Cod_Emit_WritesFile()
        {
            var output = EmitLibrary(NewDir(), LibrarySource);
            Assert.True(File.Exists(output));
            var text = File.ReadAllText(output);
            Assert.Contains("COCOD", text);
        }

        [Fact]
        public void Cod_Serialize_RoundTrip_Stable()
        {
            var output = EmitLibrary(NewDir(), LibrarySource);
            var text = File.ReadAllText(output);

            var cod = CodSerializer.Read(text);

            using var writer = new StringWriter();
            CodSerializer.Write(writer, cod);

            Assert.Equal(text, writer.ToString());
        }

        [Fact]
        public void Cod_Deserialize_Symbols()
        {
            var output = EmitLibrary(NewDir(), LibrarySource);
            var cod = CodSerializer.Read(File.ReadAllText(output));

            Assert.Contains(cod.Functions, f => f.Name == "Add");
            Assert.Contains(cod.Functions, f => f.Name == "Factorial");
            Assert.Contains(cod.Functions, f => f.Name == "Sum");
            Assert.Contains(cod.Functions, f => f.Name == "IsGreen");
            Assert.Contains(cod.Functions, f => f.Name == "Greet");
            Assert.Contains(cod.Enums, e => e.Name == "Color");
            Assert.Contains(cod.Bodies.Keys, f => f.Name == "Factorial");
            Assert.Equal(CodRequirement.Any, cod.Requires);
            Assert.Contains(cod.Namespaces, ns => ns == "MyLib");

            var add = Assert.Single(cod.Functions, f => f.Name == "Add");
            Assert.Equal(2, add.Parameters.Length);
            Assert.Same(TypeSymbol.Int32, add.ReturnType);
            Assert.Same(TypeSymbol.Int32, add.Parameters[0].Type);
        }

        [Fact]
        public void Cod_Builtin_RoundTrips_ToSingleton()
        {
            var dir = NewDir();
            var source = @"
namespace MyLib
{
    class Native
    {
        syscall function Print(text: string): void
    }

    function SayHi(): void
    {
        Native.Print(""hi"")
    }
}
";
            var output = EmitLibrary(dir, source);
            var cod = CodSerializer.Read(File.ReadAllText(output));

            var body = cod.Bodies[Assert.Single(cod.Functions, f => f.Name == "SayHi")];
            var call = FindCallToPrint(body);
            Assert.NotNull(call);
            Assert.Equal(Cocoa.CodeAnalysis.Symbols.BuiltinKind.Print, call.Method!.BuiltinKind);
        }

        private static Cocoa.CodeAnalysis.Binding.BoundMemberCallExpression? FindCallToPrint(Cocoa.CodeAnalysis.Binding.BoundNode node)
        {
            if (node is Cocoa.CodeAnalysis.Binding.BoundMemberCallExpression call &&
                call.Method?.BuiltinKind == Cocoa.CodeAnalysis.Symbols.BuiltinKind.Print)
            {
                return call;
            }

            foreach (var child in EnumerateChildren(node))
            {
                var found = FindCallToPrint(child);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static System.Collections.Generic.IEnumerable<Cocoa.CodeAnalysis.Binding.BoundNode> EnumerateChildren(Cocoa.CodeAnalysis.Binding.BoundNode node)
        {
            switch (node.Kind)
            {
                case Cocoa.CodeAnalysis.Binding.BoundNodeKind.BlockStatement:
                    foreach (var s in ((Cocoa.CodeAnalysis.Binding.BoundBlockStatement)node).Statements)
                    {
                        yield return s;
                    }
                    break;
                case Cocoa.CodeAnalysis.Binding.BoundNodeKind.ExpressionStatement:
                    yield return ((Cocoa.CodeAnalysis.Binding.BoundExpressionStatement)node).Expression;
                    break;
                case Cocoa.CodeAnalysis.Binding.BoundNodeKind.MemberCallExpression:
                    yield return ((Cocoa.CodeAnalysis.Binding.BoundMemberCallExpression)node).Expression;
                    foreach (var a in ((Cocoa.CodeAnalysis.Binding.BoundMemberCallExpression)node).Arguments)
                    {
                        yield return a;
                    }
                    break;
            }
        }

        [Fact]
        public void Cod_Reject_Entry()
        {
            var dir = NewDir();
            var source = @"using System

namespace MyLib
{
    function Main(): void
    {
        Console.WriteLine(""hi"")
    }
}
";
            var libPath = Path.Combine(dir, "Lib.co");
            File.WriteAllText(libPath, source);
            var compilation = Compilation.Create(SyntaxTree.Load(libPath));

            var diagnostics = compilation.EmitCocoa("Lib", Path.Combine(dir, "Lib.cod"));
            Assert.True(diagnostics.HasErrors());
            Assert.Contains("入口", diagnostics[0].Message);
        }

        [Fact]
        public void Cod_Reject_NoNamespace()
        {
            var dir = NewDir();
            var source = @"
function Add(a: int, b: int): int
{
    return a + b
}
";
            var libPath = Path.Combine(dir, "Lib.co");
            File.WriteAllText(libPath, source);
            var compilation = Compilation.Create(SyntaxTree.Load(libPath));

            var diagnostics = compilation.EmitCocoa("Lib", Path.Combine(dir, "Lib.cod"));
            Assert.True(diagnostics.HasErrors());
            Assert.Contains("namespace", diagnostics[0].Message);
        }

        [Fact]
        public void Cod_Reject_Oop()
        {
            var dir = NewDir();
            var source = @"
namespace MyLib
{
    public class Point
    {
        private _x: int
        public function X(): int { return _x }
    }
}
";
            var libPath = Path.Combine(dir, "Lib.co");
            File.WriteAllText(libPath, source);
            var compilation = Compilation.Create(SyntaxTree.Load(libPath));

            var diagnostics = compilation.EmitCocoa("Lib", Path.Combine(dir, "Lib.cod"));
            Assert.True(diagnostics.HasErrors());
            Assert.Contains("实例类", diagnostics[0].Message);
        }

        [Fact]
        public void Cod_ContainerClass_RoundTrips()
        {
            var dir = NewDir();
            var source = @"
namespace System
{
    class Runtime
    {
        syscall function Print(text: string): void
        syscall function Random(max: int): int
    }

    class Utils
    {
        static function Triple(x: int): int
        {
            return x * 3
        }

        static function Max(a: int, b: int): int
        {
            if (a > b) return a
            return b
        }
    }

    namespace Math
    {
        function Max(a: int, b: int): int
        {
            if (a > b) return a
            return b
        }
    }
}
";
            var output = EmitLibrary(dir, source);
            var cod = CodSerializer.Read(File.ReadAllText(output));

            var runtime = Assert.Single(cod.Classes, c => c.Name == "Runtime");
            Assert.Equal("System", runtime.Namespace);
            Assert.Equal("System.Runtime", runtime.FullName);

            var print = Assert.Single(runtime.Methods, m => m.Name == "Print");
            Assert.True(print.IsStatic);
            Assert.Equal(BuiltinKind.Print, print.BuiltinKind);
            Assert.Same(runtime, print.ContainingClass);

            // 6e-M18：静态方法容器类（方法带函数体）符号往返
            var utils = Assert.Single(cod.Classes, c => c.Name == "Utils");
            Assert.Equal("System.Utils", utils.FullName);
            Assert.Equal(2, utils.Methods.Length);

            var triple = Assert.Single(utils.Methods, m => m.Name == "Triple");
            Assert.True(triple.IsStatic);
            Assert.Same(utils, triple.ContainingClass);
            Assert.Contains(cod.Functions, f => f.Name == "Triple" && f.ContainingClass == utils);

            var utilsMax = Assert.Single(utils.Methods, m => m.Name == "Max");
            Assert.Same(utils, utilsMax.ContainingClass);
            Assert.Contains(cod.Functions, f => f.Name == "Max" && f.ContainingClass == utils);

            var math = Assert.Single(cod.Functions, f => f.Name == "Max" && f.ContainingClass == null);
            Assert.Equal("System.Math", math.Namespace);
        }

        [Fact]
        public void Cod_ExternEntryPointCharSet_RoundTrips()
        {
            var dir = NewDir();
            var source = @"
namespace System
{
    class Kernel32
    {
        import kernel32.dll
        {
            static stdcall function GetTickCountAlias(): int
                extern(entry = GetTickCount, charset = ansi)
        }
    }
}
";
            var output = EmitLibrary(dir, source);
            var cod = CodSerializer.Read(File.ReadAllText(output));

            var kernel32 = Assert.Single(cod.Classes, c => c.Name == "Kernel32");
            var method = Assert.Single(kernel32.Methods, m => m.Name == "GetTickCountAlias");

            Assert.True(method.IsExtern);
            Assert.Equal("kernel32.dll", method.DllName);
            Assert.Equal("GetTickCount", method.EntryPoint);
            Assert.Equal(CharSet.Ansi, method.CharSet);
        }

        [Fact]
        public void Cod_ExternDefaultCharsetUnicode_WhenNoMetadata()
        {
            var dir = NewDir();
            var source = @"
namespace System
{
    class Kernel32
    {
        import kernel32.dll
        {
            static stdcall function GetTickCount(): int
        }
    }
}
";
            var output = EmitLibrary(dir, source);
            var cod = CodSerializer.Read(File.ReadAllText(output));

            var kernel32 = Assert.Single(cod.Classes, c => c.Name == "Kernel32");
            var method = Assert.Single(kernel32.Methods, m => m.Name == "GetTickCount");

            Assert.Null(method.EntryPoint);
            Assert.Equal(CharSet.Unicode, method.CharSet);
        }

        [Fact]
        public void Cod_ContainerClass_StaticMethod_EndToEnd()
        {
            var dir = NewDir();
            var source = @"
namespace System
{
    class Utils
    {
        static function Triple(x: int): int
        {
            return x * 3
        }

        static function Max(a: int, b: int): int
        {
            if (a > b) return a
            return b
        }
    }
}
";
            var diagnostics = Compilation.Create(SyntaxTree.Parse(source)).EmitCocoa("System.Core", Path.Combine(dir, "System.Core.cod"));
            Assert.Empty(diagnostics);
            Assert.True(File.Exists(Path.Combine(dir, "System.Core.cod")));

            var previous = Environment.GetEnvironmentVariable("COCOA_STDLIB");
            try
            {
                Environment.SetEnvironmentVariable("COCOA_STDLIB", dir);
                SystemLibrary.Reset();

                var compilation = Compilation.Create(SyntaxTree.Parse(@"using System

function Main(): int
{
    return Utils.Triple(3) + Utils.Max(2, 7)
}"));
                var result = compilation.Evaluate(new System.Collections.Generic.Dictionary<Cocoa.CodeAnalysis.Symbols.VariableSymbol, object>());
                Assert.Empty(result.Diagnostics);
                Assert.Equal(9 + 7, result.Value);
            }
            finally
            {
                Environment.SetEnvironmentVariable("COCOA_STDLIB", previous);
                SystemLibrary.Reset();
            }
        }

        [Fact]
        public void Cod_SystemLibrary_Loads_WhenPresent()
        {
            var baseDirectory = Path.GetDirectoryName(typeof(CodSerializerTests).Assembly.Location)!;
            var systemCore = Path.Combine(baseDirectory, "System.Core.cod");

            if (!File.Exists(systemCore))
            {
                return; // 系统库未部署时跳过（降级语义）
            }

            SystemLibrary.Reset();
            var libraries = SystemLibrary.Load();
            Assert.NotEmpty(libraries);

            var core = Assert.Single(libraries, lib => lib.Classes.Any(c => c.Name == "Runtime"));
            var runtime = Assert.Single(core.Classes, c => c.Name == "Runtime");
            Assert.Equal("System.Runtime", runtime.FullName);
            Assert.Contains(runtime.Methods, m => m.Name == "Print" && m.BuiltinKind == BuiltinKind.Print);

            // 6e-M18：Math 为静态容器类（方法含类归属）
            var math = Assert.Single(core.Classes, c => c.Name == "Math");
            Assert.Equal("System.Math", math.FullName);
            Assert.Contains(math.Methods, m => m.Name == "Max" && m.IsStatic);
            Assert.Contains(core.Functions, f => f.Name == "Max" && f.ContainingClass == math);
        }

        [Fact]
        public void Cod_SystemLibrary_Discovers_Additional_Modules()
        {
            // 多程序集发现（6e-M17）：目录内 System*.cod 自动加载，核心 System.Core.cod 强制首位；
            // 未来大功能模块（System.Net.cod 等）放入目录即生效。本测试用临时 System.Demo.cod 模拟。
            var baseDirectory = Path.GetDirectoryName(typeof(CodSerializerTests).Assembly.Location)!;
            if (!File.Exists(Path.Combine(baseDirectory, "System.Core.cod")))
            {
                return; // 核心库未部署时跳过（降级语义）
            }

            var dir = Path.Combine(Path.GetTempPath(), "cocoa-cod-discovery", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                // 部署场景：把核心库也放入该目录（模拟编译器部署目录含 System.Core.cod + 未来模块）
                File.Copy(Path.Combine(baseDirectory, "System.Core.cod"), Path.Combine(dir, "System.Core.cod"));

                var demoTree = SyntaxTree.Parse(@"
namespace System.Demo
{
    function Ping(): int
    {
        return 42
    }
}");
                var diagnostics = Compilation.Create(demoTree).EmitCocoa("System.Demo", Path.Combine(dir, "System.Demo.cod"));
                Assert.Empty(diagnostics);
                Assert.True(File.Exists(Path.Combine(dir, "System.Demo.cod")));

                var previous = Environment.GetEnvironmentVariable("COCOA_STDLIB");
                try
                {
                    Environment.SetEnvironmentVariable("COCOA_STDLIB", dir);
                    SystemLibrary.Reset();
                    var libraries = SystemLibrary.Load();

                    Assert.Equal(2, libraries.Length);
                    Assert.Contains(libraries, lib => lib.Namespaces.Contains("System.Demo"));
                    Assert.Contains(libraries, lib => lib.Classes.Any(c => c.Name == "Runtime"));
                    Assert.Equal("System.Runtime", libraries[0].Classes.First(c => c.Name == "Runtime").FullName);

                    // 端到端：用户程序同时命中核心库与额外模块（同 scope 注入）
                    var compilation = Compilation.Create(SyntaxTree.Parse(@"using System

function Main(): int
{
    Console.WriteLine(System.Demo.Ping())
    return System.Demo.Ping()
}"));
                    var result = compilation.Evaluate(new System.Collections.Generic.Dictionary<Cocoa.CodeAnalysis.Symbols.VariableSymbol, object>());
                    Assert.Empty(result.Diagnostics);
                    Assert.Equal(42, result.Value);
                }
                finally
                {
                    Environment.SetEnvironmentVariable("COCOA_STDLIB", previous);
                    SystemLibrary.Reset();
                }
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
