using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Cod;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System.IO;
using System.Linq;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Cod
{
    /// <summary>
    /// cod 库相关测试串行集合：成员测试会改写进程级 COCOA_STDLIB 并 Reset stdlib，
    /// 与其他消费 stdlib 的测试并行时产生注入窗口竞争（G7 期间实证），故整体禁并行。
    /// </summary>
    [CollectionDefinition("CodStdlibSequence", DisableParallelization = true)]
    public class CodStdlibSequenceCollection
    {
    }

    [Collection("CodStdlibSequence")]
    public class CodSerializerTests
    {
        private static readonly string TestRoot = Path.Combine(Path.GetTempPath(), "cocoa-cod-tests");

        private const string LibrarySource = @"
namespace MyLib
{
    public enum Color { Red, Green, Blue }

    function Add(a: i32, b: i32): i32
    {
        return a + b
    }

    function Factorial(n: i32): i32
    {
        var result = 1
        for var i = 1 to n
        {
            result = result * i
        }
        return result
    }

    function Sum(items: i32[]): i32
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

    function Countdown(n: i32): i32
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
        public void G7_GenericDefinition_RoundTrips_TypeFlow()
        {
            var dir = NewDir();
            var source = @"
namespace MyLib
{
    public class Box<T>
    {
        private _value: T

        public constructor(v: T)
        {
            _value = v
        }

        public function Get(): T
        {
            return _value
        }

        public static function Echo(input: Box<T>): Box<T>
        {
            return input
        }
    }
}
";
            var output = EmitLibrary(dir, source);
            var text = File.ReadAllText(output);
            Assert.True(text.Contains("(gcls"), "cod 缺少 gcls 节点，实际前 600 字符：\n" + text.Substring(0, Math.Min(600, text.Length)));
            Assert.Contains("(tpar", text);
            Assert.Contains("!MyLib.Box.T", text);
            Assert.Contains("MyLib.Box`1#!MyLib.Box.T", text);

            var loaded = CodSerializer.Load(output);
            Assert.True(loaded.GenericDefinitions.Length == 1,
                "gdefs=" + loaded.GenericDefinitions.Length +
                " classes=[" + string.Join(",", loaded.Classes.Select(c => c.FullName + (c.IsGenericDefinition ? "<GEN>" : ""))) + "]");

            var box = loaded.GenericDefinitions[0];
            Assert.Equal("MyLib.Box", box.FullName);
            Assert.True(box.IsGenericDefinition);

            var tParameter = Assert.Single(box.TypeParameters);
            Assert.Equal("T", tParameter.Name);
            Assert.True(box.Methods.Length == 3, "methods=[" + string.Join(",", box.Methods.Select(m => m.Name + (m.IsStatic ? "(static)" : ""))) + "]");
            Assert.Equal(0, tParameter.Ordinal);
            Assert.Same(box, tParameter.OwningClass);

            var valueField = box.Fields.Single(f => f.Name == "_value");
            var openReference = Assert.IsType<TypeParameterSymbol>(valueField.Type);
            Assert.Equal("T", openReference.Name);
            Assert.Same(box, openReference.OwningClass);

            var echo = box.Methods.Single(m => m.Name == "Echo");
            Assert.True(echo.IsStatic);
            var parameterType = Assert.IsType<InstantiatedTypeSymbol>(echo.Parameters[0].Type);
            Assert.Same(box, parameterType.GenericDefinition);
            Assert.Equal("T", Assert.IsType<TypeParameterSymbol>(parameterType.TypeArguments[0]).Name);
            var returnType = Assert.IsType<InstantiatedTypeSymbol>(echo.ReturnType);
            Assert.Same(parameterType, returnType);
        }

        [Fact]
        public void G7_InterfaceDeclaration_RoundTrips_ImplementsList()
        {
            var dir = NewDir();
            var source = @"
namespace MyLib
{
    public interface IMarker
    {
    }

    public class Box extends IMarker
    {
        public static function Describe(): string
        {
            return ""box""
        }
    }
}
";
            var output = EmitLibrary(dir, source);
            var text = File.ReadAllText(output);
            Assert.True(text.Contains("iface:true"), "cod 缺少接口位，实际：\n" + text.Substring(0, Math.Min(600, text.Length)));
            Assert.Contains("iface:false", text);
            Assert.Contains("ifaces:1", text);

            var loaded = CodSerializer.Load(output);
            var shape = loaded.Classes.Single(c => c.FullName == "MyLib.IMarker");
            Assert.True(shape.IsInterface, "IMarker 应反序列化为接口");
            var box = loaded.Classes.Single(c => c.FullName == "MyLib.Box");
            Assert.False(box.IsInterface);
            var iface = Assert.Single(box.Interfaces);
            Assert.Equal("MyLib.IMarker", iface.FullName);
        }

        [Fact]
        public void G7_FunctionType_RoundTrips_FntyRef()
        {
            var dir = NewDir();
            var source = @"
namespace MyLib
{
    function Wrap(f: (i32) -> i32, n: i32): i32
    {
        return n
    }

    function HighOrder(g: ((i32) -> i32) -> i32): i32
    {
        return 0
    }
}
";
            var output = EmitLibrary(dir, source);
            var text = File.ReadAllText(output);
            Assert.True(text.Contains("fnty{"), "cod 缺少 fnty 节点，实际：\n" + text.Substring(0, Math.Min(600, text.Length)));

            var loaded = CodSerializer.Load(output);
            var wrap = loaded.Functions.Single(f => f.Name == "Wrap");
            var fType = Assert.IsType<FunctionTypeSymbol>(wrap.Parameters[0].Type);
            Assert.Single(fType.ParameterTypes);
            Assert.Same(TypeSymbol.Int32, fType.ParameterTypes[0]);
            Assert.Same(TypeSymbol.Int32, fType.ReturnType);

            var high = loaded.Functions.Single(f => f.Name == "HighOrder");
            var hType = Assert.IsType<FunctionTypeSymbol>(high.Parameters[0].Type);
            Assert.Same(TypeSymbol.Int32, hType.ReturnType);
            var inner = Assert.IsType<FunctionTypeSymbol>(Assert.Single(hType.ParameterTypes));
            Assert.Same(TypeSymbol.Int32, inner.ParameterTypes[0]);
            Assert.Same(TypeSymbol.Int32, inner.ReturnType);
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
        public void Cod_Read_Rejects_UnknownVersion()
        {
            var source = "(cod COCOD 99)\n";
            var exception = Assert.Throws<InvalidDataException>(() => CodSerializer.Read(source + ChecksumLine(source)));
            Assert.Contains("version 99", exception.Message);
            Assert.Contains("rebuild", exception.Message);
        }

        private static string ChecksumLine(string payload)
        {
            var hex = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
            return "(checksum sha256:" + hex + ")";
        }

        [Fact]
        public void Cod_Checksum_Emitted_And_Accepted()
        {
            var output = EmitLibrary(NewDir(), LibrarySource);
            var text = File.ReadAllText(output);

            Assert.Contains("(checksum sha256:", text);
            CodSerializer.Read(text);
        }

        [Fact]
        public void Cod_Checksum_Tamper_Rejected()
        {
            var output = EmitLibrary(NewDir(), LibrarySource);
            var text = File.ReadAllText(output);

            // 改动正文任意字节（此处改函数名一个字符）→ 校验和不再匹配
            var tampered = text.Replace("name:Add", "name:Adx");
            Assert.NotEqual(text, tampered);

            var exception = Assert.Throws<InvalidDataException>(() => CodSerializer.Read(tampered));
            Assert.Contains("checksum mismatch", exception.Message);
        }

        [Fact]
        public void Cod_Checksum_Missing_Rejected()
        {
            var output = EmitLibrary(NewDir(), LibrarySource);
            var text = File.ReadAllText(output);

            var markerIndex = text.LastIndexOf("(checksum ", StringComparison.Ordinal);
            Assert.True(markerIndex >= 0);
            var withoutChecksum = text.Substring(0, markerIndex);

            var exception = Assert.Throws<InvalidDataException>(() => CodSerializer.Read(withoutChecksum));
            Assert.Contains("checksum missing", exception.Message);
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
        syscall function WriteLine(text: string): void
    }

    function SayHi(): void
    {
        Native.WriteLine(""hi"")
    }
}
";
            var output = EmitLibrary(dir, source);
            var cod = CodSerializer.Read(File.ReadAllText(output));

            var body = cod.Bodies[Assert.Single(cod.Functions, f => f.Name == "SayHi")];
            var call = FindCallToPrint(body);
            Assert.NotNull(call);
            Assert.Equal(Cocoa.CodeAnalysis.Symbols.BuiltinKind.WriteLine, call.Method!.BuiltinKind);
        }

        private static Cocoa.CodeAnalysis.Binding.BoundMemberCallExpression? FindCallToPrint(Cocoa.CodeAnalysis.Binding.BoundNode node)
        {
            if (node is Cocoa.CodeAnalysis.Binding.BoundMemberCallExpression call &&
                call.Method?.BuiltinKind == Cocoa.CodeAnalysis.Symbols.BuiltinKind.WriteLine)
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
function Add(a: i32, b: i32): i32
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
        private _x: i32
        public function X(): i32 { return _x }
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
        syscall function WriteLine(text: string): void
        syscall function Random(max: i32): i32
    }

    class Utils
    {
        static function Triple(x: i32): i32
        {
            return x * 3
        }

        static function Max(a: i32, b: i32): i32
        {
            if (a > b) return a
            return b
        }
    }

    namespace Math
    {
        function Max(a: i32, b: i32): i32
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

            var print = Assert.Single(runtime.Methods, m => m.Name == "WriteLine");
            Assert.True(print.IsStatic);
            Assert.Equal(BuiltinKind.WriteLine, print.BuiltinKind);
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
            static stdcall function GetTickCountAlias(): i32
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
            static stdcall function GetTickCount(): i32
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
        static function Triple(x: i32): i32
        {
            return x * 3
        }

        static function Max(a: i32, b: i32): i32
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

function Main(): i32
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
        public void SystemLibrary_Probe_FindsLibsUpTree()
        {
            var root = Path.Combine(Path.GetTempPath(), "cocoa-libs-probe", Guid.NewGuid().ToString("N"));
            var deep = Path.Combine(root, "x", "y", "bin");
            Directory.CreateDirectory(deep);
            Directory.CreateDirectory(Path.Combine(root, "libs"));
            File.WriteAllText(Path.Combine(root, "libs", "System.Core.cod"), "(cod COCOD 1)");

            var isolatedRoot = Path.Combine(Path.GetTempPath(), "cocoa-libs-probe", Guid.NewGuid().ToString("N"));
            var isolated = Path.Combine(isolatedRoot, "plain", "deeper");
            Directory.CreateDirectory(isolated);

            try
            {
                Assert.Equal(Path.GetFullPath(Path.Combine(root, "libs")), SystemLibrary.FindLibsStore(deep));
                Assert.Null(SystemLibrary.FindLibsStore(isolated));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
                Directory.Delete(isolatedRoot, recursive: true);
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
            Assert.Contains(runtime.Methods, m => m.Name == "WriteLine" && m.BuiltinKind == BuiltinKind.WriteLine);

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
    function Ping(): i32
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

function Main(): i32
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

        [Fact]
        public void FacadeInstanceClass_RoundTrips_MemberFlags()
        {
            // 6b：facade 实例类（映射 BCL、体内不经 cod 执行）按符号序列化——属性访问器（acc 位）往返一致；
            // facade 构造器不经 cod（绑定期经 facade→BCL 合成），IsFacadeClass 由注入侧按 FacadeTargets 补齐。
            // 换门即 System.Core.cod（含 Exception.co）重建成功、消费方 `new Exception(...)` 可绑定。
            var dir = NewDir();
            var source = @"
namespace System
{
    public facade class Exception
    {
        public constructor(message: string)
        {
        }

        public property Message: string
        {
            get
            {
                return """"
            }
        }
    }
}
";
            var output = EmitLibrary(dir, source);

            var loaded = CodSerializer.Load(output);
            var facadeClass = loaded.Classes.Single(c => c.FullName == "System.Exception");
            Assert.False(facadeClass.IsInterface);

            var accessor = loaded.Functions.SingleOrDefault(f => f.ContainingClass == facadeClass && f.IsPropertyAccessor);
            Assert.NotNull(accessor);
            Assert.True(accessor.IsPropertyAccessor);
            Assert.Equal("get_Message", accessor.Name);

            var message = facadeClass.GetProperty("Message");
            Assert.NotNull(message);
            Assert.Same(Cocoa.CodeAnalysis.Symbols.TypeSymbol.String, message.Type);
            Assert.Same(accessor, message.Getter);
        }

        [Fact]
        public void FacadeException_NewBinds_AgainstRebuiltCoreCod()
        {
            // 6b E2E：消费方程序以 `using System` 绑定 `new Exception(...)` + catch + `e.Message`——依赖重建后的
            // System.Core.cod 提供 Exception facade 类壳（构造器经 FacadeTargets 合成、Message 属性经 props 序列化挂接 get_Message）。
            // 注：Evaluator 不支持 try/catch（TryStatement 未实现），此处仅验证绑定无错误。
            var compilation = Compilation.Create(SyntaxTree.Parse(@"using System

function Main(): i32
{
    var thrown = false
    try
    {
        throw new Exception(""boom"")
        thrown = true
    }
    catch (e: Exception)
    {
        System.Console.WriteLine(e.Message)
    }
    if thrown != false return 1
    return 0
}"));
            var errors = compilation.GlobalScope.Diagnostics.Where(d => d.IsError).ToArray();
            Assert.True(errors.Length == 0, string.Join("\n", errors.Select(d => d.Message)));
        }

        [Fact]
        public void GenericOpenBody_ObjectCreation_RoundTrips()
        {
            // M0-1c：开放泛型体内的对象创建 `new Foo<T>(...)`（BoundObjectCreationExpression）随库携带——
            // 序列化为 objnew 节点（类类型 + 实参），读侧按类型+元数重解析构造器。
            var dir = NewDir();
            var source = @"
namespace MyLib
{
    public class Enumerator<T>
    {
        public function Enumerator(v: T)
        {
        }
    }

    public class Wrapper<T>
    {
        public function Make(v: T): Enumerator<T>
        {
            return new Enumerator<T>(v)
        }
    }
}
";
            var output = EmitLibrary(dir, source);
            var text = File.ReadAllText(output);
            Assert.Contains("(objnew", text);

            var loaded = CodSerializer.Load(output);
            Assert.Contains(loaded.GenericDefinitions, g => g.FullName == "MyLib.Wrapper");
            Assert.Contains(loaded.GenericDefinitions, g => g.FullName == "MyLib.Enumerator");
        }

        [Fact]
        public void GenericIface_TypeParam_NotConflated()
        {
            // 泛型基接口实例化不得串味到其他类同类参数名：HashSet<T> extends ICollection<T>
            // 的 iface 实参必须限定为 !Test.HashSet.T（IList<T> 共存时 IList.T 不得被复用）。
            var dir = NewDir();
            var source = @"
namespace Test
{
    public interface ICollection<T>
    {
        function Add(item: T): bool
    }

    public interface IList<T> extends ICollection<T>
    {
        function Get(i: i32): T
    }

    public class HashSet<T> extends ICollection<T>
    {
        public function Add(item: T): bool
        {
            return true
        }
    }
}
";
            var libPath = Path.Combine(dir, "Lib.co");
            File.WriteAllText(libPath, source);
            var compilation = Compilation.Create(SyntaxTree.Load(libPath));
            var output = Path.Combine(dir, "Lib.cod");
            var diagnostics = compilation.EmitCocoa("Lib", output);
            Assert.True(diagnostics.Length == 0, string.Join("; ", diagnostics.Select(d => d.Message)));

            var text = File.ReadAllText(output);
            var hashIdx = text.IndexOf("(gcls Test.HashSet");
            var tparIdx = text.IndexOf("tparams:", hashIdx);
            var header = text.Substring(hashIdx, tparIdx - hashIdx);
            Assert.Contains("Test.ICollection`1#!Test.HashSet.T", header);
            Assert.DoesNotContain("!Test.IList.T", header);
        }
    }
}
