using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Cod;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System.IO;
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
    function SayHi(): void
    {
        print(""hi"")
    }
}
";
            var output = EmitLibrary(dir, source);
            var cod = CodSerializer.Read(File.ReadAllText(output));

            var body = cod.Bodies[Assert.Single(cod.Functions, f => f.Name == "SayHi")];
            var call = FindCallToPrint(body);
            Assert.NotNull(call);
            Assert.Equal(Cocoa.CodeAnalysis.Symbols.BuiltinKind.Print, call.Function.BuiltinKind);
        }

        private static Cocoa.CodeAnalysis.Binding.BoundCallExpression? FindCallToPrint(Cocoa.CodeAnalysis.Binding.BoundNode node)
        {
            if (node is Cocoa.CodeAnalysis.Binding.BoundCallExpression call && call.Function.Name == "print")
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
            }
        }

        [Fact]
        public void Cod_Reject_Entry()
        {
            var dir = NewDir();
            var source = @"
namespace MyLib
{
    function Main(): void
    {
        print(""hi"")
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
            Assert.Contains("class", diagnostics[0].Message);
        }
    }
}
