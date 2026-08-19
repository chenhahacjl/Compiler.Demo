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
        public void Library_NoMain_DoesNotRequireEntryPoint()
        {
            var code = @"
public class Greeter
{
    public function Hello(): string
    {
        return ""hi""
    }
}";
            var syntaxTree = SyntaxTree.Parse(code);
            var compilation = Compilation.Create(syntaxTree);
            var path = Path.Combine(Path.GetTempPath(), "cocoa-lib-test", "greeter.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var diagnostics = compilation.Emit("greeter", References, path, IlTarget.Parse("net9.0"), emitLibrary: true);
            Assert.Empty(diagnostics);
            Assert.True(File.Exists(path));
        }
    }
}
