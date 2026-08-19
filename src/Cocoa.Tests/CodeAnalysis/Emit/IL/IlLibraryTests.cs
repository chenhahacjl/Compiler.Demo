using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Emit.IL;
using Cocoa.CodeAnalysis.Syntax;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Emit.IL
{
    public class IlLibraryTests
    {
        private static readonly string[] References = new[]
        {
            typeof(object).Assembly.Location,
            typeof(System.Console).Assembly.Location,
        };

        [Fact]
        public void Library_Emit_ProducesDll_WithPublicClass()
        {
            var code = @"
public class Point
{
    private _x: int

    public constructor(x: int)
    {
        _x = x
    }

    public function Get(): int
    {
        return _x
    }
}";
            var syntaxTree = SyntaxTree.Parse(code);
            var compilation = Compilation.Create(syntaxTree);
            var path = Path.Combine(Path.GetTempPath(), "cocoa-lib-test", "mylib.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var diagnostics = compilation.Emit("mylib", References, path, IlTarget.Parse("net9.0"), emitLibrary: true);
            Assert.Empty(diagnostics);
            Assert.True(File.Exists(path));

            var assembly = Assembly.LoadFile(path);
            var point = assembly.GetTypes().Single(t => t.Name == "Point");
            Assert.True(point.IsPublic);

            var ctor = point.GetConstructor(new[] { typeof(int) });
            Assert.NotNull(ctor);
            Assert.True(ctor.IsPublic);

            var get = point.GetMethod("Get", BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(get);
            Assert.True(get.IsPublic);

            var x = point.GetField("_x", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(x);
            Assert.False(x.IsPublic);
        }

        [Fact]
        public void MetadataReader_FindsTypeInfo_InCocoaLibrary()
        {
            var code = @"
namespace MyLib
{
    public class Point
    {
        private _x: int

        public constructor(x: int)
        {
            _x = x
        }

        public function Get(): int
        {
            return _x
        }
    }
}";
            var syntaxTree = SyntaxTree.Parse(code);
            var compilation = Compilation.Create(syntaxTree);
            var path = Path.Combine(Path.GetTempPath(), "cocoa-lib-test", "mylib_info.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var diagnostics = compilation.Emit("mylib", References, path, IlTarget.Parse("net9.0"), emitLibrary: true);
            Assert.Empty(diagnostics);

            var reader = new MetadataReader(new[] { path });
            var info = reader.FindTypeInfo("MyLib.Point");
            Assert.NotNull(info);
            Assert.Equal("MyLib.Point", info.FullName);
            Assert.Contains(info.Methods, m => m.Name == ".ctor" && m.ParameterTypes.Count == 1 && m.ParameterTypes[0].FullName == "System.Int32");
            Assert.Contains(info.Methods, m => m.Name == "Get");

            var builder = new MetadataBuilder("test", "test");
            var method = reader.FindMethod("MyLib.Point", ".ctor", new[] { "System.Int32" }, builder);
            Assert.NotNull(method);
        }

        [Fact]
        public void Library_Consumed_ByAnotherCompilation_WithUsing()
        {
            var libCode = @"
namespace MyLib
{
    public class Point
    {
        private _x: int

        public constructor(x: int)
        {
            _x = x
        }

        public function Get(): int
        {
            return _x
        }

        public function Add(other: int): int
        {
            return _x + other
        }
    }
}";
            var libTree = SyntaxTree.Parse(libCode);
            var libCompilation = Compilation.Create(libTree);
            var libPath = Path.Combine(Path.GetTempPath(), "cocoa-lib-test", "consume_lib.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(libPath)!);
            var libDiagnostics = libCompilation.Emit("consume_lib", References, libPath, IlTarget.Parse("net9.0"), emitLibrary: true);
            Assert.Empty(libDiagnostics);

            var appCode = @"
using MyLib

function Main()
{
    var p = new Point(5)
    print(p.Get())
    print(p.Add(3))
}";
            var appTree = SyntaxTree.Parse(appCode);
            var appCompilation = Compilation.Create("Main", new[] { libPath }, appTree);
            var appPath = Path.Combine(Path.GetTempPath(), "cocoa-lib-test", "consume_app.exe");
            var emitRefs = References.Concat(new[] { libPath }).ToArray();
            var appDiagnostics = appCompilation.Emit("consume_app", emitRefs, appPath, IlTarget.Parse("net9.0"));
            Assert.Empty(appDiagnostics);

            var psi = new System.Diagnostics.ProcessStartInfo("dotnet", $"\"{appPath}\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var process = System.Diagnostics.Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit(15000);
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("5\r\n8\r\n", stdout);
        }

        [Fact]
        public void Library_Internal_Protected_Members_EmitCorrectFlags()
        {
            var code = @"
namespace MyLib
{
    public class Account
    {
        internal _balance: int

        protected function GetBalance(): int
        {
            return _balance
        }

        internal function Add(balance: int)
        {
            _balance = _balance + balance
        }

        public constructor(balance: int)
        {
            _balance = balance
        }
    }

    internal class Hidden
    {
        public function Noop(): int
        {
            return 0
        }
    }
}";
            var syntaxTree = SyntaxTree.Parse(code);
            var compilation = Compilation.Create(syntaxTree);
            var path = Path.Combine(Path.GetTempPath(), "cocoa-lib-test", "vis_lib.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var diagnostics = compilation.Emit("vis_lib", References, path, IlTarget.Parse("net9.0"), emitLibrary: true);
            Assert.Empty(diagnostics);

            var assembly = Assembly.LoadFile(path);
            var account = assembly.GetType("MyLib.Account");
            Assert.NotNull(account);
            Assert.True(account.IsPublic);

            var balance = account.GetField("_balance", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(balance);
            Assert.True(balance.IsAssembly, "internal 字段应为 Assembly 可见性");

            var getBalance = account.GetMethod("GetBalance", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(getBalance);
            Assert.True(getBalance.IsFamily, "protected 方法应为 Family 可见性");

            var add = account.GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(add);
            Assert.True(add.IsAssembly, "internal 方法应为 Assembly 可见性");

            // internal 类型对外不可见
            Assert.Null(assembly.GetType("MyLib.Hidden"));

            // FindTypeInfo 同样不可见 internal 类型 + 不导出 internal/protected 成员
            var reader = new MetadataReader(new[] { path });
            Assert.Null(reader.FindTypeInfo("MyLib.Hidden"));
            var info = reader.FindTypeInfo("MyLib.Account");
            Assert.NotNull(info);
            Assert.DoesNotContain(info.Methods, m => m.Name == "GetBalance");
            Assert.DoesNotContain(info.Methods, m => m.Name == "Add");
            Assert.DoesNotContain(info.Fields, f => f.Name == "_balance");
        }

        [Fact]
        public void Library_Internal_NotVisible_ToConsumer_Using()
        {
            var libCode = @"
namespace MyLib
{
    internal class Hidden
    {
        public function Noop(): int
        {
            return 0
        }
    }
}";
            var libTree = SyntaxTree.Parse(libCode);
            var libCompilation = Compilation.Create(libTree);
            var libPath = Path.Combine(Path.GetTempPath(), "cocoa-lib-test", "hidden_lib.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(libPath)!);
            var libDiagnostics = libCompilation.Emit("hidden_lib", References, libPath, IlTarget.Parse("net9.0"), emitLibrary: true);
            Assert.Empty(libDiagnostics);

            var appCode = @"
using MyLib

function Main()
{
    var h = new Hidden()
    print(h.Noop())
}";
            var appTree = SyntaxTree.Parse(appCode);
            var appCompilation = Compilation.Create("Main", new[] { libPath }, appTree);
            var appPath = Path.Combine(Path.GetTempPath(), "cocoa-lib-test", "hidden_app.exe");
            var emitRefs = References.Concat(new[] { libPath }).ToArray();
            var appDiagnostics = appCompilation.Emit("hidden_app", emitRefs, appPath, IlTarget.Parse("net9.0"));
            Assert.Contains(appDiagnostics, d => d.Message.Contains(" undefined type") || d.Message.Contains("Hidden"));
        }
    }
}
        {
            var libCode = @"
public class Shape
{
    private _name: string

    public constructor(name: string)
    {
        _name = name
    }

    public virtual function Describe(): string
    {
        return _name
    }

    public property Name: string
    {
        get { return _name }
        set { _name = value }
    }
}

public class Circle: Shape
{
    private _radius: int

    public constructor(name: string, radius: int): base(name)
    {
        _radius = radius
    }

    public override function Describe(): string
    {
        return base.Describe() + (string)_radius
    }
}";
            var libTree = SyntaxTree.Parse(libCode);
            var libCompilation = Compilation.Create(libTree);
            var libPath = Path.Combine(Path.GetTempPath(), "cocoa-lib-test", "oop_lib.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(libPath)!);
            var libDiagnostics = libCompilation.Emit("oop_lib", References, libPath, IlTarget.Parse("net9.0"), emitLibrary: true);
            Assert.Empty(libDiagnostics);

            var assembly = Assembly.LoadFile(libPath);
            var shape = assembly.GetType("Shape");
            var circle = assembly.GetType("Circle");
            Assert.True(shape.BaseType == typeof(object) || shape.BaseType.Name == "Object");
            Assert.True(circle.BaseType == shape, "Circle 应继承 Shape");

            var ctor = circle.GetConstructor(new[] { typeof(string), typeof(int) });
            Assert.NotNull(ctor);
            var obj = ctor.Invoke(new object[] { "big", 4 });

            var describe = circle.GetMethod("Describe");
            Assert.NotNull(describe);
            Assert.True(describe.IsVirtual);
            Assert.Equal("big4", describe.Invoke(obj, null));

            var nameProperty = shape.GetProperty("Name");
            Assert.NotNull(nameProperty);
            Assert.Equal("big", nameProperty.GetValue(obj));
            nameProperty.SetValue(obj, "renamed");
            Assert.Equal("renamed", nameProperty.GetValue(obj));
        }
    }
}
