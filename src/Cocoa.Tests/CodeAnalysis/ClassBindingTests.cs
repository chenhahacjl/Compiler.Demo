using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System.Collections.Immutable;
using System.IO;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    public class ClassBindingTests
    {
        [Fact]
        public void Class_NewObject_Binds()
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
}

function Main()
{
    var p = new Point(3)
    print(p.Get())
}";
            var syntaxTree = SyntaxTree.Parse(code);
            var compilation = Compilation.Create(syntaxTree);
            Assert.Single(compilation.GlobalScope.Classes);
            Assert.Equal("Point", compilation.GlobalScope.Classes[0].Name);
            var diagnostics = GetDiagnostics(code);
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void Class_MethodCall_Binds()
        {
            var code = @"
public class Point
{
    private _x: int

    public constructor(x: int)
    {
        _x = x
    }

    public function Double(): int
    {
        return _x * 2
    }
}

function Main()
{
    var p = new Point(3)
    print(p.Double())
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void Class_IsDeclared_InGlobalScope()
        {
            var code = @"
public class Point
{
    private _x: int
}

function Main()
{
    print(1)
}";
            var syntaxTree = SyntaxTree.Parse(code);
            var compilation = Compilation.Create(syntaxTree);
            Assert.NotEmpty(compilation.GlobalScope.Classes);
        }

        [Fact]
        public void Class_NewObject_ParsesAsObjectCreation()
        {
            var syntaxTree = SyntaxTree.Parse("function Main() { var p = new Point(3) }");
            var root = syntaxTree.Root;
            var member = Assert.Single(root.Members);
            var func = Assert.IsType<FunctionDeclarationSyntax>(member);
            Assert.True(ContainsNode(func, SyntaxKind.ObjectCreationExpression));
        }

        private static bool ContainsNode(SyntaxNode node, SyntaxKind kind)
        {
            if (node.Kind == kind)
            {
                return true;
            }

            foreach (var child in node.GetChildren())
            {
                if (ContainsNode(child, kind))
                {
                    return true;
                }
            }

            return false;
        }

        [Fact]
        public void Class_PrivateField_AccessOutside_ReportsError()
        {
            var code = @"
public class Point
{
    private _x: int

    public constructor(x: int)
    {
        _x = x
    }
}

function Main()
{
    var p = new Point(3)
    print(p._x)
}";
            var diagnostics = GetDiagnostics(code);
            var error = Assert.Single(diagnostics);
            Assert.Contains("private", error.Message);
        }

        [Fact]
        public void Class_PrivateMethod_AccessOutside_ReportsError()
        {
            var code = @"
public class Point
{
    private _x: int

    private function Secret(): int
    {
        return 42
    }
}

function Main()
{
    var p = new Point()
    print(p.Secret())
}";
            var diagnostics = GetDiagnostics(code);
            var error = Assert.Single(diagnostics);
            Assert.Contains("private", error.Message);
        }

        [Fact]
        public void Oop_AbstractClass_Instantiation_ReportsError()
        {
            var code = @"
public abstract class Animal
{
    public abstract function Sound(): string
}

function Main()
{
    var a = new Animal()
}";
            var diagnostics = GetDiagnostics(code);
            var error = Assert.Single(diagnostics);
            Assert.Contains("抽象类", error.Message);
        }

        [Fact]
        public void Oop_CircularInheritance_ReportsError()
        {
            var code = @"
public class A: B { }
public class B: A { }

function Main()
{
    print(1)
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Contains(diagnostics, d => d.Message.Contains("循环继承"));
        }

        [Fact]
        public void Oop_StaticContext_This_ReportsError()
        {
            var code = @"
public class Foo
{
    public static function Bar(): int
    {
        return this._x
    }

    private _x: int
}

function Main()
{
    print(Foo.Bar())
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Contains(diagnostics, d => d.Message.Contains("this"));
        }

        [Fact]
        public void Oop_ReadonlyField_OutsideConstructor_ReportsError()
        {
            var code = @"
public class Foo
{
    private readonly _x: int

    public constructor(x: int)
    {
        _x = x
    }

    public function Reset()
    {
        _x = 0
    }
}

function Main()
{
    print(1)
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Contains(diagnostics, d => d.Message.Contains("only variable") || d.Message.Contains("_x"));
        }

        [Fact]
        public void Oop_Property_WithoutSetter_Assignment_ReportsError()
        {
            var code = @"
public class Foo
{
    private _x: int

    public constructor(x: int)
    {
        _x = x
    }

    public property X: int
    {
        get { return _x }
    }
}

function Main()
{
    var f = new Foo(1)
    f.X = 2
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Contains(diagnostics, d => d.Message.Contains("Cannot assign") || d.Message.Contains("X"));
        }

        private static readonly string[] References = new[]
        {
            typeof(object).Assembly.Location,
            typeof(System.Console).Assembly.Location,
            typeof(System.Random).Assembly.Location,
        };

        private static ImmutableArray<Diagnostic> GetDiagnostics(string code)
        {
            var syntaxTree = SyntaxTree.Parse(code);
            var compilation = Compilation.Create(syntaxTree);
            var path = Path.Combine(Path.GetTempPath(), "class_binding_test.exe");
            var diagnostics = compilation.Emit("test", References, path);
            return diagnostics;
        }
    }
}
