using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    public class ClassBindingTests
    {
        [Fact]
        public void Class_NewObject_Binds()
        {
            var code = @"using System

public class Point
{
    private _x: i32

    public constructor(x: i32)
    {
        _x = x
    }

    public function Get(): i32
    {
        return _x
    }
}

function Main()
{
    var p = new Point(3)
    Console.WriteLine(p.Get())
}";
            var syntaxTree = SyntaxTree.Parse(code);
            var compilation = Compilation.Create(syntaxTree);
            Assert.Single(compilation.GlobalScope.Classes);
            Assert.Equal("Point", compilation.GlobalScope.Classes[0].Name);
            var diagnostics = GetDiagnostics(code);
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void GetTypeByMetadataName_ResolvesBuiltinSourceAndArray()
        {
            var code = @"using System

public class Point
{
    public function Get(): i32 { return 0 }
}

public enum Color { Red, Green }

function Main()
{
}";
            var compilation = Compilation.Create(SyntaxTree.Parse(code));

            Assert.Equal(TypeSymbol.Int32, compilation.GetTypeByMetadataName("System.Int32"));
            Assert.Equal(TypeSymbol.String, compilation.GetTypeByMetadataName("System.String"));
            Assert.Equal(NamedTypeSymbol.SystemObject, compilation.GetTypeByMetadataName("System.Object"));
            Assert.Equal(TypeSymbol.Void, compilation.GetTypeByMetadataName("System.Void"));

            var point = compilation.GetTypeByMetadataName("Point");
            Assert.NotNull(point);
            Assert.Equal("Point", point!.Name);

            var color = compilation.GetTypeByMetadataName("Color");
            Assert.NotNull(color);
            Assert.Equal(SymbolKind.NamedType, color!.Kind);

            var intArray = compilation.GetTypeByMetadataName("System.Int32[]");
            Assert.NotNull(intArray);
            Assert.True(intArray!.ElementType == TypeSymbol.Int32);

            Assert.Null(compilation.GetTypeByMetadataName("System.NoSuchType"));
        }

        [Fact]
        public void GlobalNamespace_GroupsTypesByDeclaredNamespace()
        {
            var code = @"using System

namespace Foo.Bar
{
    public class Point
    {
        public function Get(): i32 { return 0 }
    }
}

public enum Color { Red, Green }

function Main()
{
}";
            var compilation = Compilation.Create(SyntaxTree.Parse(code));

            Assert.True(compilation.GlobalNamespace.IsGlobal);
            Assert.Equal("", compilation.GlobalNamespace.FullName);
            Assert.Equal(compilation.GlobalNamespace, compilation.GetNamespace(""));

            var fooBar = compilation.GetNamespace("Foo.Bar");
            Assert.NotNull(fooBar);
            Assert.Equal("Bar", fooBar!.Name);
            Assert.Equal("Foo.Bar", fooBar.FullName);
            Assert.Contains(fooBar.GetTypeMembers(), t => t.Name == "Point");

            Assert.Contains(compilation.GlobalNamespace.GetTypeMembers(), t => t.Name == "Color");

            Assert.Null(compilation.GetNamespace("Foo.Baz"));
            Assert.Null(compilation.GetNamespace("Definitely.Not.Real"));
        }

        [Fact]
        public void AssemblySymbols_SourceAndReferenced()
        {
            var code = @"function Main() {}";
            var compilation = Compilation.Create(SyntaxTree.Parse(code));

            Assert.True(compilation.SourceAssembly.IsSourceAssembly);
            Assert.Equal("Cocoa", compilation.SourceAssembly.Name);
            Assert.Equal(SymbolKind.Assembly, compilation.SourceAssembly.Kind);

            var referenced = compilation.ReferencedAssemblies;
            Assert.All(referenced, a =>
            {
                Assert.False(a.IsSourceAssembly);
                Assert.False(string.IsNullOrEmpty(a.Name));
                Assert.Equal(SymbolKind.Assembly, a.Kind);
            });
        }

        [Fact]
        public void GetDiagnostics_AggregatesParseAndBinding()
        {
            // 语法错误
            var parse = Compilation.Create(SyntaxTree.Parse("function Main() { var x =  "));
            Assert.Contains(parse.GetDiagnostics(), d => d.IsError);

            // 绑定错误：未定义类型
            var binding = Compilation.Create(SyntaxTree.Parse("function Main() { var x: NoSuchType = 1 }"));
            Assert.Contains(binding.GetDiagnostics(), d => d.IsError);

            // 干净程序
            var clean = Compilation.Create(SyntaxTree.Parse("function Main() {}"));
            Assert.DoesNotContain(clean.GetDiagnostics(), d => d.IsError);
        }

        [Fact]
        public void GetSymbolsWithName_MatchesAcrossTypesAndFunctions()
        {
            var code = @"using System

public class Point
{
    public function Get(): i32 { return 0 }
}

public enum Color { Red, Green }

function Compute(): i32 { return 1 }

function Main()
{
}";
            var compilation = Compilation.Create(SyntaxTree.Parse(code));

            Assert.Contains(compilation.GetSymbolsWithName("Point"), s => s is NamedTypeSymbol && s.Name == "Point");
            Assert.Contains(compilation.GetSymbolsWithName("Color"), s => s is NamedTypeSymbol && s.Name == "Color");
            Assert.Contains(compilation.GetSymbolsWithName("Compute"), s => s is FunctionSymbol && s.Name == "Compute");
            Assert.Empty(compilation.GetSymbolsWithName("DefinitelyMissing"));
        }

        [Fact]
        public void SemanticModel_GetTypeInfo_ResolvesBuiltinAndDeclaredTypes()
        {
            var code = @"using System

public class Point
{
    public function Get(): i32 { return 0 }
}

function Main()
{
    var n: i32 = 1
    var s: string = ""a""
    var p: Point = null
}";
            var tree = SyntaxTree.Parse(code);
            var compilation = Compilation.Create(tree);
            var model = compilation.GetSemanticModel(tree);

            var clauses = Descendants(tree.Root).OfType<TypeClauseSyntax>().ToList();
            var i32 = clauses.First(c => c.Identifier.Text == "i32");
            var str = clauses.First(c => c.Identifier.Text == "string");
            var point = clauses.First(c => c.Identifier.Text == "Point");

            Assert.Equal(TypeSymbol.Int32, model.GetTypeInfo(i32));
            Assert.Equal(TypeSymbol.String, model.GetTypeInfo(str));
            Assert.Equal("Point", model.GetTypeInfo(point)!.Name);

            Assert.Null(model.GetTypeInfo(tree.Root));
            Assert.Null(model.GetTypeInfo(null!));
        }

        [Fact]
        public void SemanticModel_GetDeclaredSymbol_ResolvesTypesAndFunctions()
        {
            var code = @"using System

public class Point
{
    public function Get(): i32 { return 0 }
}

public enum Color { Red, Green }

function Compute(): i32 { return 1 }

function Main()
{
}";
            var tree = SyntaxTree.Parse(code);
            var compilation = Compilation.Create(tree);
            var model = compilation.GetSemanticModel(tree);

            var classNode = Descendants(tree.Root).OfType<ClassDeclarationSyntax>().Single(c => c.Identifier.Text == "Point");
            var enumNode = Descendants(tree.Root).OfType<EnumDeclarationSyntax>().Single();
            var computeNode = Descendants(tree.Root).OfType<FunctionDeclarationSyntax>().Single(f => f.Identifier.Text == "Compute");

            var pointClass = model.GetDeclaredSymbol(classNode);
            Assert.NotNull(pointClass);
            Assert.Equal("Point", pointClass!.Name);
            Assert.Equal(SymbolKind.NamedType, pointClass.Kind);

            var colorEnum = model.GetDeclaredSymbol(enumNode);
            Assert.NotNull(colorEnum);
            Assert.Equal("Color", colorEnum!.Name);
            Assert.Equal(SymbolKind.NamedType, colorEnum.Kind);

            var computeFn = model.GetDeclaredSymbol(computeNode);
            Assert.NotNull(computeFn);
            Assert.Equal("Compute", computeFn!.Name);
            Assert.Equal(SymbolKind.Function, computeFn.Kind);

            Assert.Null(model.GetDeclaredSymbol(tree.Root));
        }

        private static IEnumerable<SyntaxNode> Descendants(SyntaxNode root)
        {
            foreach (var child in root.GetChildren())
            {
                yield return child;
                foreach (var nested in Descendants(child))
                {
                    yield return nested;
                }
            }
        }

        [Fact]
        public void GlobalNamespace_GroupsNamespacedFunctions()
        {
            var code = @"using System

namespace Utils
{
    public function Helper(): i32 { return 1 }
}

function Main()
{
}";
            var compilation = Compilation.Create(SyntaxTree.Parse(code));

            var utils = compilation.GetNamespace("Utils");
            Assert.NotNull(utils);
            Assert.Contains(utils!.GetFunctionMembers(), f => f.Name == "Helper");

            // namespaced functions live under their namespace, not the global root
            Assert.DoesNotContain(compilation.GlobalNamespace.GetFunctionMembers(), f => f.Name == "Helper");
        }

        [Fact]
        public void Class_MethodCall_Binds()
        {
            var code = @"using System

public class Point
{
    private _x: i32

    public constructor(x: i32)
    {
        _x = x
    }

    public function Double(): i32
    {
        return _x * 2
    }
}

function Main()
{
    var p = new Point(3)
    Console.WriteLine(p.Double())
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void Class_IsDeclared_InGlobalScope()
        {
            var code = @"using System

public class Point
{
    private _x: i32
}

function Main()
{
    Console.WriteLine(1)
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
            var code = @"using System

public class Point
{
    private _x: i32

    public constructor(x: i32)
    {
        _x = x
    }
}

function Main()
{
    var p = new Point(3)
    Runtime.WriteLine(p._x)
}";
            var diagnostics = GetDiagnostics(code);
            var error = Assert.Single(diagnostics);
            Assert.Contains("private", error.Message);
        }

        [Fact]
        public void Class_PrivateMethod_AccessOutside_ReportsError()
        {
            var code = @"using System

public class Point
{
    private _x: i32

    private function Secret(): i32
    {
        return 42
    }
}

function Main()
{
    var p = new Point()
    Runtime.WriteLine(p.Secret())
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
            var code = @"using System

public class A extends B { }
public class B extends A { }

function Main()
{
    Console.WriteLine(1)
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Contains(diagnostics, d => d.Message.Contains("循环继承"));
        }

        [Fact]
        public void Oop_StaticContext_This_ReportsError()
        {
            var code = @"using System

public class Foo
{
    public static function Bar(): i32
    {
        return this._x
    }

    private _x: i32
}

function Main()
{
    Console.WriteLine(Foo.Bar())
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Contains(diagnostics, d => d.Message.Contains("this"));
        }

        [Fact]
        public void Oop_ReadonlyField_OutsideConstructor_ReportsError()
        {
            var code = @"using System

public class Foo
{
    private readonly _x: i32

    public constructor(x: i32)
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
    Console.WriteLine(1)
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
    private _x: i32

    public constructor(x: i32)
    {
        _x = x
    }

    public property X: i32
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

        [Fact]
        public void Class_InternalMember_AccessibleInSameCompilation()
        {
            var code = @"using System

public class Foo
{
    internal _x: i32

    internal function Bar(): i32
    {
        return 42
    }
}

function Main()
{
    var f = new Foo()
    f._x = 1
    Console.WriteLine(f.Bar() + f._x)
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void Class_ProtectedMember_DerivedClass_Accessible()
        {
            var code = @"using System

public class Animal
{
    protected _age: i32

    protected function Age(): i32
    {
        return _age
    }

    protected property AgeProp: i32
    {
        get { return _age }
    }
}

public class Dog extends Animal
{
    public constructor(age: i32)
    {
        _age = age
    }

    public function GetAge(): i32
    {
        return _age + Age() + AgeProp
    }
}

function Main()
{
    var d = new Dog(3)
    Console.WriteLine(d.GetAge())
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void Class_ProtectedMember_GrandChild_Accessible()
        {
            var code = @"using System

public class Animal
{
    protected _age: i32
}

public class Dog extends Animal { }

public class Puppy extends Dog
{
    public constructor(age: i32)
    {
        _age = age
    }

    public function GetAge(): i32
    {
        return _age
    }
}

function Main()
{
    var p = new Puppy(5)
    Console.WriteLine(p.GetAge())
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void Class_ProtectedField_UnrelatedClass_ReportsError()
        {
            var code = @"using System

public class Animal
{
    protected _age: i32
}

public class Keeper
{
    public function ReadAge(a: Animal): i32
    {
        return a._age
    }
}

function Main()
{
    Console.WriteLine(1)
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Contains(diagnostics, d => d.Message.Contains("protected"));
        }

        [Fact]
        public void Class_ProtectedMethod_OutsideClassHierarchy_ReportsError()
        {
            var code = @"using System

public class Animal
{
    protected function Eat(): i32
    {
        return 1
    }
}

function Main()
{
    var a = new Animal()
    Console.WriteLine(a.Eat())
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Contains(diagnostics, d => d.Message.Contains("protected"));
        }

        [Fact]
        public void Class_PrivateCtor_OutsideClass_ReportsError()
        {
            var code = @"
public class Foo
{
    private constructor() { }
}

function Main()
{
    var f = new Foo()
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Contains(diagnostics, d => d.Message.Contains("private"));
        }

        [Fact]
        public void Class_PrivateOnClass_ReportsError()
        {
            var code = @"
private class Foo
{
}

function Main()
{
    var f = new Foo()
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Contains(diagnostics, d => d.Message.Contains("可见性"));
        }

        [Fact]
        public void Class_ProtectedOnClass_ReportsError()
        {
            var code = @"using System

protected class Foo
{
}

function Main()
{
    Console.WriteLine(1)
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Contains(diagnostics, d => d.Message.Contains("可见性"));
        }

        [Fact]
        public void Class_InternalClass_DeclaresOk()
        {
            var code = @"using System

internal class Foo
{
}

function Main()
{
    var f = new Foo()
    Console.WriteLine(1)
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void Class_Partial_TwoParts_MergeIntoOneSymbol()
        {
            var code = @"using System

public partial class Point
{
    private _x: i32
}

public partial class Point
{
    public constructor(x: i32)
    {
        _x = x
    }

    public function Get(): i32
    {
        return _x
    }
}

function Main()
{
    var p = new Point(3)
    Console.WriteLine(p.Get())
}";
            var syntaxTree = SyntaxTree.Parse(code);
            var compilation = Compilation.Create(syntaxTree);
            var classType = Assert.Single(compilation.GlobalScope.Classes);
            Assert.Equal("Point", classType.Name);
            Assert.Contains(classType.Fields, f => f.Name == "_x");
            Assert.Contains(classType.Methods, m => m.Name == "Get");
            Assert.Empty(GetDiagnostics(code));
        }

        [Fact]
        public void Class_Partial_AcrossMultipleTrees_AllMembersBound()
        {
            var tree1 = SyntaxTree.Parse(@"using System

public partial class Point
{
    private _x: i32
}

function Main()
{
    var p = new Point(3)
    Console.WriteLine(p.Get())
}");
            var tree2 = SyntaxTree.Parse(@"
public partial class Point
{
    public constructor(x: i32)
    {
        _x = x
    }

    public function Get(): i32
    {
        return _x
    }
}");

            var compilation = Compilation.Create(tree1, tree2);

            var classType = Assert.Single(compilation.GlobalScope.Classes);
            Assert.Contains(classType.Fields, f => f.Name == "_x");
            Assert.Contains(classType.Methods, m => m.Name == "Get");

            var path = Path.Combine(Path.GetTempPath(), "class_partial_test.exe");
            var diagnostics = compilation.Emit("test", References, path);
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void Class_Partial_ImplicitConstructor_GeneratedOnce()
        {
            var code = @"using System

public partial class A
{
    private _x: i32
}

public partial class A
{
    public function Get(): i32
    {
        return 42
    }
}

function Main()
{
    var a = new A()
    Console.WriteLine(a.Get())
}";
            var syntaxTree = SyntaxTree.Parse(code);
            var compilation = Compilation.Create(syntaxTree);
            var classType = Assert.Single(compilation.GlobalScope.Classes);
            Assert.Single(classType.Methods.Where(m => m.IsConstructor));
            Assert.Empty(GetDiagnostics(code));
        }

        [Fact]
        public void Class_Partial_DuplicateWithoutPartial_ReportsError()
        {
            var code = @"using System

public class Foo
{
}

public class Foo
{
}

function Main()
{
    Console.WriteLine(1)
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Contains(diagnostics, d => d.Message.Contains("'Foo' is already declared."));
        }

        [Fact]
        public void Class_Partial_MissingPartialOnSecondPart_ReportsError()
        {
            var code = @"using System

public partial class Foo
{
}

public class Foo
{
}

function Main()
{
    Console.WriteLine(1)
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Contains(diagnostics, d => d.Message.Contains("'Foo' is already declared."));
        }

        [Fact]
        public void Class_Partial_VisibilityConflict_ReportsError()
        {
            var code = @"using System

public partial class Foo
{
}

internal partial class Foo
{
}

function Main()
{
    Console.WriteLine(1)
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Contains(diagnostics, d => d.Message.Contains("可见性不一致"));
        }

        [Fact]
        public void Class_Partial_BaseClassMismatch_ReportsError()
        {
            var code = @"using System

public class A { }
public class B { }

public partial class Foo extends A
{
}

public partial class Foo extends B
{
}

function Main()
{
    Console.WriteLine(1)
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Contains(diagnostics, d => d.Message.Contains("基类不一致"));
        }

        [Fact]
        public void Class_Partial_OnMethod_ReportsError()
        {
            var code = @"using System

public class Foo
{
    public partial function Bar(): i32
    {
        return 1
    }
}

function Main()
{
    Console.WriteLine(1)
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Contains(diagnostics, d => d.Message.Contains("partial 只能用于类声明"));
        }

        [Fact]
        public void Class_CocoaMembers_BindWithoutErrors()
        {
            var code = @"using System

public class Person
{
    private _name: string
    private _age: i32

    public constructor(name: string, age: i32)
    {
        _name = name
        _age = age
    }

    public property Name: string { get set }

    public function GetAge(): i32
    {
        return _age
    }
}

function Main()
{
    var p = new Person(""A"", 1)
    Console.WriteLine(p.Name)
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void Class_InstanceFieldInitializer_BindsWithoutErrors()
        {
            var code = @"using System

public class Counter
{
    private _count: i32 = 5

    public function Get(): i32
    {
        return _count
    }
}

function Main()
{
    var c = new Counter()
    Console.WriteLine(c.Get())
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void Class_AutoPropertyInitializer_BindsWithoutErrors()
        {
            var code = @"using System

public class Point
{
    public property X: i32 { get set } = 10

    public function GetX(): i32
    {
        return X
    }
}

function Main()
{
    var p = new Point()
    Console.WriteLine(p.GetX())
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void Class_StaticFieldInitializer_CreatesCctorSymbol()
        {
            var code = @"using System

public class Config
{
    public static Max: i32 = 100
}

function Main()
{
    Console.WriteLine(Config.Max)
}";
            var syntaxTree = SyntaxTree.Parse(code);
            var compilation = Compilation.Create(syntaxTree);
            var classType = Assert.Single(compilation.GlobalScope.Classes);
            Assert.True(classType.Methods.Any(m => m.Name == ".cctor" && m.IsStatic && m.IsConstructor));
            var diagnostics = GetDiagnostics(code);
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void Class_NoStaticInitializer_NoCctor()
        {
            var code = @"using System

public class Foo
{
    public static Max: i32
    private _x: i32
}

function Main()
{
    var f = new Foo()
    Console.WriteLine(1)
}";
            var syntaxTree = SyntaxTree.Parse(code);
            var compilation = Compilation.Create(syntaxTree);
            var classType = Assert.Single(compilation.GlobalScope.Classes);
            Assert.DoesNotContain(classType.Methods, m => m.Name == ".cctor");
            var diagnostics = GetDiagnostics(code);
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void Class_UserStaticConstructor_CreatesCctorSymbol()
        {
            var code = @"using System

public class Config
{
    public static Max: i32

    static constructor()
    {
        Max = 42
    }
}

function Main()
{
    Console.WriteLine(Config.Max)
}";
            var syntaxTree = SyntaxTree.Parse(code);
            var compilation = Compilation.Create(syntaxTree);
            var classType = Assert.Single(compilation.GlobalScope.Classes);
            var cctor = Assert.Single(classType.Methods.Where(m => m.Name == ".cctor" && m.IsStatic && m.IsConstructor));
            Assert.Empty(cctor.Parameters);
            var diagnostics = GetDiagnostics(code);
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void Class_UserStaticConstructor_CocoaStyle_CreatesCctorSymbol()
        {
            var code = @"using System

public class Config
{
    public static Max: i32

    static constructor()
    {
        Max = 7
    }
}

function Main()
{
    Console.WriteLine(Config.Max)
}";
            var syntaxTree = SyntaxTree.Parse(code);
            var compilation = Compilation.Create(syntaxTree);
            var classType = Assert.Single(compilation.GlobalScope.Classes);
            var cctor = Assert.Single(classType.Methods.Where(m => m.Name == ".cctor" && m.IsStatic && m.IsConstructor));
            Assert.Empty(cctor.Parameters);
            var diagnostics = GetDiagnostics(code);
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void Class_UserStaticConstructor_NoImplicitCctorDuplicate()
        {
            var code = @"using System

public class Config
{
    public static Max: i32

    static constructor()
    {
        Max = 42
    }
}

function Main()
{
    Console.WriteLine(Config.Max)
}";
            var syntaxTree = SyntaxTree.Parse(code);
            var compilation = Compilation.Create(syntaxTree);
            var classType = Assert.Single(compilation.GlobalScope.Classes);
            Assert.Single(classType.Methods.Where(m => m.Name == ".cctor"));
            Assert.Equal(2, classType.Methods.Count(m => m.IsConstructor)); // 用户 .cctor + 隐式实例构造
            var diagnostics = GetDiagnostics(code);
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void Class_StaticConstructor_WithParameters_ReportsError()
        {
            var code = @"using System

public class Foo
{
    static Foo(i32 x)
    {
    }
}

function Main()
{
    Console.WriteLine(1)
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Contains(diagnostics, d => d.Message.Contains("参数"));
        }

        [Fact]
        public void Class_StaticConstructor_WithChain_ReportsError()
        {
            var code = @"using System

public class Base
{
}

public class Foo extends Base
{
    static constructor() extends base()
    {
    }
}

function Main()
{
    Console.WriteLine(1)
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Contains(diagnostics, d => d.Message.Contains("构造链"));
        }

        [Fact]
        public void Class_StaticConstructor_WithVisibilityModifier_ReportsError()
        {
            var code = @"using System

public class Foo
{
    public static constructor()
    {
    }
}

function Main()
{
    Console.WriteLine(1)
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Contains(diagnostics, d => d.Message.Contains("可见性修饰符"));
        }

        [Fact]
        public void Class_StaticConstructor_ThisAccess_ReportsError()
        {
            var code = @"using System

public class Foo
{
    private _x: i32

    static constructor()
    {
        this._x = 1
    }
}

function Main()
{
    Console.WriteLine(1)
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Contains(diagnostics, d => d.Message.Contains("this"));
        }

        [Fact]
        public void Class_StaticConstructor_InstanceFieldAccess_ReportsError()
        {
            var code = @"using System

public class Foo
{
    private _x: i32

    static constructor()
    {
        _x = 1
    }
}

function Main()
{
    Console.WriteLine(1)
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Contains(diagnostics, d => d.Message.Contains("实例字段"));
        }

        [Fact]
        public void Class_StaticConstructor_Duplicate_ReportsError()
        {
            var code = @"using System

public class Foo
{
    static constructor()
    {
    }

    static constructor()
    {
    }
}

function Main()
{
    Console.WriteLine(1)
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Contains(diagnostics, d => d.Message.Contains("already declared") || d.Message.Contains("已声明"));
        }

        [Fact]
        public void Class_ReadonlyFieldWithInitializer_BindsWithoutErrors()
        {
            var code = @"using System

public class Immutable
{
    public readonly Id: i32 = 42

    public function Get(): i32
    {
        return Id
    }
}

function Main()
{
    var i = new Immutable()
    Console.WriteLine(i.Get())
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void Class_CocoaLocalVariables_BindWithoutErrors()
        {
            var code = @"using System

public class Calculator
{
    public function Add(a: i32, b: i32): i32
    {
        var sum = a + b
        var product = a * b
        return sum + product
    }
}

function Main()
{
    var c = new Calculator()
    Console.WriteLine(c.Add(1, 2))
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void Class_AccessorModifierMoreRestrictiveThanProperty_BindsWithoutErrors()
        {
            var code = @"using System

public class Account
{
    public property Balance: i32 { get private set }

    public function Deposit(amount: i32)
    {
        Balance = Balance + amount
    }

    public function Get(): i32
    {
        return Balance
    }
}

function Main()
{
    var a = new Account()
    a.Deposit(100)
    Console.WriteLine(a.Get())
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void Class_AccessorVisibility_EqualToProperty_ReportsError()
        {
            // 严格对齐 C#（CS0273）：访问器可见性相等也报错
            var code = @"
public class Foo
{
    private property X: i32 { get private set }
}

function Main()
{
}";
            var diagnostics = GetDiagnostics(code);
            var error = Assert.Single(diagnostics.Where(d => d.IsError));
            Assert.Contains("必须比属性更受限", error.Message);
        }

        [Fact]
        public void Class_AccessorVisibility_MorePermissiveThanProperty_ReportsError()
        {
            var code = @"
public class Foo
{
    private property X: i32 { get public set }
}

function Main()
{
}";
            var diagnostics = GetDiagnostics(code);
            var error = Assert.Single(diagnostics.Where(d => d.IsError));
            Assert.Contains("必须比属性更受限", error.Message);
        }

        [Fact]
        public void Class_ProtectedProperty_InternalAccessor_ReportsError()
        {
            // internal 不严格比 protected 更受限（C# 亦报 CS0273）
            var code = @"
public class Foo
{
    protected property X: i32 { internal get }
}

function Main()
{
}";
            var diagnostics = GetDiagnostics(code);
            var error = Assert.Single(diagnostics.Where(d => d.IsError));
            Assert.Contains("必须比属性更受限", error.Message);
        }

        [Fact]
        public void Class_AccessorModifierOnBothAccessors_ReportsError()
        {
            var code = @"
public class Foo
{
    public property X: i32 { private get private set }
}

function Main()
{
}";
            var diagnostics = GetDiagnostics(code);
            var error = Assert.Single(diagnostics.Where(d => d.IsError));
            Assert.Contains("不能同时带可见性修饰符", error.Message);
        }

        private static readonly string[] References = new[]
        {
            typeof(object).Assembly.Location,
            typeof(System.Console).Assembly.Location,
            typeof(System.Random).Assembly.Location,
        };

        [Fact]
        public void ObjectModel_NoExplicitBase_DefaultsToSystemObject()
        {
            var syntaxTree = SyntaxTree.Parse(@"
public class Point
{
    public function Get(): i32
    {
        return 1
    }
}");
            var compilation = Compilation.Create(syntaxTree);
            var point = compilation.GlobalScope.Classes.Single(c => c.Name == "Point");
            Assert.True(point.BaseType == NamedTypeSymbol.SystemObject);
        }

        [Fact]
        public void ObjectModel_ExplicitObjectSpellings_BindToSingleton()
        {
            // 注：基类子句解析仅接受裸标识符（`System.Object` 点号拼写在 Parser 层尚不支持）；
            // CO 方言继承用 extends 关键字（冒号为 .cs 方言拼写）
            foreach (var baseName in new[] { "object", "Object" })
            {
                var code = $@"
public class Point extends {baseName}
{{
    public function Get(): i32
    {{
        return 1
    }}
}}";
                var syntaxTree = SyntaxTree.Parse(code);
                var compilation = Compilation.Create(syntaxTree);
                Assert.True(compilation.GlobalScope.Classes.Single(c => c.Name == "Point").BaseType == NamedTypeSymbol.SystemObject);
                Assert.Empty(GetDiagnostics(code));
            }
        }

        [Fact]
        public void ObjectModel_Interface_BaseTypeNotDefaulted()
        {
            var syntaxTree = SyntaxTree.Parse(@"
interface IShape
{
    function Area(): i32
}");
            var compilation = Compilation.Create(syntaxTree);
            var iface = compilation.GlobalScope.Classes.Single(c => c.IsInterface);
            Assert.Null(iface.BaseType);
        }

        [Fact]
        public void ObjectModel_UserClassObject_ShadowsBuiltInSingleton()
        {
            var syntaxTree = SyntaxTree.Parse(@"
public class Object
{
    public function Ping(): i32
    {
        return 7
    }
}

public class Point extends Object
{
    public function Get(): i32
    {
        return 1
    }
}");
            var compilation = Compilation.Create(syntaxTree);
            var point = compilation.GlobalScope.Classes.Single(c => c.Name == "Point");
            Assert.NotNull(point.BaseType);
            Assert.False(point.BaseType!.IsSystemObjectRoot);
            Assert.NotSame(NamedTypeSymbol.SystemObject, point.BaseType);
        }

        [Fact]
        public void ObjectModel_NoExplicitBase_OverrideObjectMethods_Binds()
        {
            // 6e-M19 M2-c 反转：System.Object 携带真实成员面后，无显式基类类可直接 override 其虚方法
            var code = @"
public class Point
{
    private _x: i32

    public constructor(x: i32)
    {
        _x = x
    }

    public override function ToString(): string
    {
        return ""Point("" + (string)_x + "")""
    }

    public override function GetHashCode(): i32
    {
        return _x * 31
    }

    public override function Equals(other: any): bool
    {
        // v1 以引用同一性演示 override 生效（null/as 随后续里程碑引入）
        return System.Object.ReferenceEquals(other, this)
    }
}

function Main(): i32
{
    var p = new Point(7)
    if p.ToString() != ""Point(7)"" return 1
    if p.GetHashCode() != 217 return 2
    if !p.Equals(p) return 3
    return 0
}";
            Assert.Empty(GetDiagnostics(code));
        }

        [Fact]
        public void ObjectModel_Override_SignatureMismatch_Reports()
        {
            // CS0115/CS1715 对齐：基类有同名虚方法但签名不匹配（GetHashCode 返回类型错）
            var code = @"
public class Point
{
    public override function GetHashCode(): string
    {
        return ""x""
    }
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Contains(diagnostics, d => d.Message.Contains("签名不匹配"));
        }

        [Fact]
        public void ObjectModel_Override_WrongParameterCount_Reports()
        {
            // Equals(other: any) 参数个数不符 → 签名不匹配诊断
            var code = @"
public class Point
{
    public override function Equals(a: any, b: any): bool
    {
        return false
    }
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Contains(diagnostics, d => d.Message.Contains("签名不匹配"));
        }

        [Fact]
        public void ObjectModel_Override_NonVirtualGetType_Reports()
        {
            // GetType 非虚（C# 同构）：override 被拒绝
            var code = @"
public class Point
{
    public override function GetType(): object
    {
        return this
    }
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Contains(diagnostics, d => d.Message.Contains("找不到可重写"));
        }

        [Fact]
        public void ObjectModel_ExplicitObjectBase_Chain_And_BaseCall_Bind()
        {
            // 显式 extends Object + base(...) 零参链 + base.ToString() 直调默认实现
            var code = @"using System

public class Point extends Object
{
    public constructor() extends base()
    {
    }

    public function Describe(): string
    {
        return base.ToString()
    }
}

function Main(): i32
{
    var p = new Point()
    Console.WriteLine(p.Describe())
    return 0
}";
            Assert.Empty(GetDiagnostics(code));
        }

        [Fact]
        public void ObjectModel_FieldObjectTypeClause_Binds()
        {
            var code = @"
public class Holder
{
    private _o: object
}";
            Assert.Empty(GetDiagnostics(code));
        }

        [Fact]
        public void ObjectModel_ObjectMemberCalls_ResolveOnClassReceivers()
        {
            // 6e-M19 M2-c：用户类实例沿继承链解析 Object 内建成员（ToString/GetHashCode/Equals/GetType）
            var code = @"using System

public class Point
{
    private _x: i32

    public constructor(x: i32)
    {
        _x = x
    }
}

function Main(): i32
{
    var p = new Point(1)
    var s = p.ToString()
    var h = p.GetHashCode()
    var e = p.Equals(p)
    var t = p.GetType()
    return 0
}";
            Assert.Empty(GetDiagnostics(code));
        }

        [Fact]
        public void ObjectModel_ObjectStaticMethods_BindDotted()
        {
            // 6e-M19 M2-c：System.Object 静态 Equals/ReferenceEquals 点号调用
            var code = @"using System

public class Point
{
}

function Main(): i32
{
    var a = new Point()
    if !Object.Equals(a, a) return 1
    if !System.Object.ReferenceEquals(a, a) return 2
    return 0
}";
            Assert.Empty(GetDiagnostics(code));
        }

        [Fact]
        public void ObjectModel_ClassEquality_BindsReferenceKind()
        {
            // 6e-M19 M2-c：类类型 == / != 绑定为引用相等 kind（含基类/派生类混合比较）
            var code = @"using System

public class Point
{
}

public class Point3D extends Point
{
}

function Main(): i32
{
    var p = new Point()
    var q = p
    if !(p == q) return 1
    if p != q return 2
    var d = new Point3D()
    var o: object = d
    if !(o == d) return 3
    if o != p return 4
    return 0
}";
            Assert.Empty(GetDiagnostics(code));
        }

        [Fact]
        public void ObjectModel_UnrelatedClassEquality_Reports()
        {
            // 无继承关系的两类比较：既无转换也无引用可比性 → 报错（对齐 C# CS0019）
            var code = @"
public class A
{
}

public class B
{
}

function Main(): i32
{
    var a = new A()
    var b = new B()
    if a == b return 1
    return 0
}";
            var diagnostics = GetDiagnostics(code);
            Assert.NotEmpty(diagnostics);
        }

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
