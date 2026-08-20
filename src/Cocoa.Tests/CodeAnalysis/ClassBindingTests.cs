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

        [Fact]
        public void Class_InternalMember_AccessibleInSameCompilation()
        {
            var code = @"
public class Foo
{
    internal _x: int

    internal function Bar(): int
    {
        return 42
    }
}

function Main()
{
    var f = new Foo()
    f._x = 1
    print(f.Bar() + f._x)
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void Class_ProtectedMember_DerivedClass_Accessible()
        {
            var code = @"
public class Animal
{
    protected _age: int

    protected function Age(): int
    {
        return _age
    }

    protected property AgeProp: int
    {
        get { return _age }
    }
}

public class Dog: Animal
{
    public constructor(age: int)
    {
        _age = age
    }

    public function GetAge(): int
    {
        return _age + Age() + AgeProp
    }
}

function Main()
{
    var d = new Dog(3)
    print(d.GetAge())
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void Class_ProtectedMember_GrandChild_Accessible()
        {
            var code = @"
public class Animal
{
    protected _age: int
}

public class Dog: Animal { }

public class Puppy: Dog
{
    public constructor(age: int)
    {
        _age = age
    }

    public function GetAge(): int
    {
        return _age
    }
}

function Main()
{
    var p = new Puppy(5)
    print(p.GetAge())
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void Class_ProtectedField_UnrelatedClass_ReportsError()
        {
            var code = @"
public class Animal
{
    protected _age: int
}

public class Keeper
{
    public function ReadAge(a: Animal): int
    {
        return a._age
    }
}

function Main()
{
    print(1)
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Contains(diagnostics, d => d.Message.Contains("protected"));
        }

        [Fact]
        public void Class_ProtectedMethod_OutsideClassHierarchy_ReportsError()
        {
            var code = @"
public class Animal
{
    protected function Eat(): int
    {
        return 1
    }
}

function Main()
{
    var a = new Animal()
    print(a.Eat())
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
            var code = @"
protected class Foo
{
}

function Main()
{
    print(1)
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Contains(diagnostics, d => d.Message.Contains("可见性"));
        }

        [Fact]
        public void Class_InternalClass_DeclaresOk()
        {
            var code = @"
internal class Foo
{
}

function Main()
{
    var f = new Foo()
    print(1)
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void Class_Partial_TwoParts_MergeIntoOneSymbol()
        {
            var code = @"
public partial class Point
{
    private _x: int
}

public partial class Point
{
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
            var classType = Assert.Single(compilation.GlobalScope.Classes);
            Assert.Equal("Point", classType.Name);
            Assert.Contains(classType.Fields, f => f.Name == "_x");
            Assert.Contains(classType.Methods, m => m.Name == "Get");
            Assert.Empty(GetDiagnostics(code));
        }

        [Fact]
        public void Class_Partial_AcrossMultipleTrees_AllMembersBound()
        {
            var tree1 = SyntaxTree.Parse(@"
public partial class Point
{
    private _x: int
}

function Main()
{
    var p = new Point(3)
    print(p.Get())
}");
            var tree2 = SyntaxTree.Parse(@"
public partial class Point
{
    public constructor(x: int)
    {
        _x = x
    }

    public function Get(): int
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
            var code = @"
public partial class A
{
    private _x: int
}

public partial class A
{
    public function Get(): int
    {
        return 42
    }
}

function Main()
{
    var a = new A()
    print(a.Get())
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
            var code = @"
public class Foo
{
}

public class Foo
{
}

function Main()
{
    print(1)
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Contains(diagnostics, d => d.Message.Contains("'Foo' is already declared."));
        }

        [Fact]
        public void Class_Partial_MissingPartialOnSecondPart_ReportsError()
        {
            var code = @"
public partial class Foo
{
}

public class Foo
{
}

function Main()
{
    print(1)
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Contains(diagnostics, d => d.Message.Contains("'Foo' is already declared."));
        }

        [Fact]
        public void Class_Partial_VisibilityConflict_ReportsError()
        {
            var code = @"
public partial class Foo
{
}

internal partial class Foo
{
}

function Main()
{
    print(1)
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Contains(diagnostics, d => d.Message.Contains("可见性不一致"));
        }

        [Fact]
        public void Class_Partial_BaseClassMismatch_ReportsError()
        {
            var code = @"
public class A { }
public class B { }

public partial class Foo: A
{
}

public partial class Foo: B
{
}

function Main()
{
    print(1)
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Contains(diagnostics, d => d.Message.Contains("基类不一致"));
        }

        [Fact]
        public void Class_Partial_OnMethod_ReportsError()
        {
            var code = @"
public class Foo
{
    public partial function Bar(): int
    {
        return 1
    }
}

function Main()
{
    print(1)
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Contains(diagnostics, d => d.Message.Contains("partial 只能用于类声明"));
        }

        [Fact]
        public void Class_CSharpStyleMembers_BindWithoutErrors()
        {
            var code = @"
public class Person
{
    private string _name;
    private _age: int

    public Person(string name, age: int)
    {
        _name = name;
        _age = age;
    }

    public string Name { get; set; }

    public int GetAge()
    {
        return _age;
    }
}

function Main()
{
    var p = new Person(""A"", 1)
    print(p.Name)
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void Class_InstanceFieldInitializer_BindsWithoutErrors()
        {
            var code = @"
public class Counter
{
    private int _count = 5;

    public function Get(): int
    {
        return _count;
    }
}

function Main()
{
    var c = new Counter()
    print(c.Get())
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void Class_AutoPropertyInitializer_BindsWithoutErrors()
        {
            var code = @"
public class Point
{
    public int X { get; set; } = 10;

    public function GetX(): int
    {
        return X;
    }
}

function Main()
{
    var p = new Point()
    print(p.GetX())
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void Class_StaticFieldInitializer_CreatesCctorSymbol()
        {
            var code = @"
public class Config
{
    public static int Max = 100;
}

function Main()
{
    print(Config.Max)
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
            var code = @"
public class Foo
{
    public static int Max;
    private _x: int
}

function Main()
{
    var f = new Foo()
    print(1)
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
            var code = @"
public class Config
{
    public static int Max;

    static Config()
    {
        Max = 42;
    }
}

function Main()
{
    print(Config.Max)
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
            var code = @"
public class Config
{
    public static int Max;

    static constructor()
    {
        Max = 7;
    }
}

function Main()
{
    print(Config.Max)
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
            var code = @"
public class Config
{
    public static int Max;

    static Config()
    {
        Max = 42;
    }
}

function Main()
{
    print(Config.Max)
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
            var code = @"
public class Foo
{
    static Foo(int x)
    {
    }
}

function Main()
{
    print(1)
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Contains(diagnostics, d => d.Message.Contains("参数"));
        }

        [Fact]
        public void Class_StaticConstructor_WithChain_ReportsError()
        {
            var code = @"
public class Base
{
}

public class Foo: Base
{
    static Foo(): base()
    {
    }
}

function Main()
{
    print(1)
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Contains(diagnostics, d => d.Message.Contains("构造链"));
        }

        [Fact]
        public void Class_StaticConstructor_WithVisibilityModifier_ReportsError()
        {
            var code = @"
public class Foo
{
    public static Foo()
    {
    }
}

function Main()
{
    print(1)
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Contains(diagnostics, d => d.Message.Contains("可见性修饰符"));
        }

        [Fact]
        public void Class_StaticConstructor_ThisAccess_ReportsError()
        {
            var code = @"
public class Foo
{
    private _x: int

    static Foo()
    {
        this._x = 1;
    }
}

function Main()
{
    print(1)
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Contains(diagnostics, d => d.Message.Contains("this"));
        }

        [Fact]
        public void Class_StaticConstructor_InstanceFieldAccess_ReportsError()
        {
            var code = @"
public class Foo
{
    private _x: int

    static Foo()
    {
        _x = 1;
    }
}

function Main()
{
    print(1)
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Contains(diagnostics, d => d.Message.Contains("实例字段"));
        }

        [Fact]
        public void Class_StaticConstructor_Duplicate_ReportsError()
        {
            var code = @"
public class Foo
{
    static Foo()
    {
    }

    static constructor()
    {
    }
}

function Main()
{
    print(1)
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Contains(diagnostics, d => d.Message.Contains("already declared") || d.Message.Contains("已声明"));
        }

        [Fact]
        public void Class_ReadonlyFieldWithInitializer_BindsWithoutErrors()
        {
            var code = @"
public class Immutable
{
    public readonly int Id = 42;

    public function Get(): int
    {
        return Id;
    }
}

function Main()
{
    var i = new Immutable()
    print(i.Get())
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void Class_CSharpStyleLocalVariables_BindWithoutErrors()
        {
            var code = @"
public class Calculator
{
    public int Add(int a, int b)
    {
        int sum = a + b;
        var product = a * b;
        return sum + product;
    }
}

function Main()
{
    var c = new Calculator()
    print(c.Add(1, 2))
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void Class_AccessorModifierMoreRestrictiveThanProperty_BindsWithoutErrors()
        {
            var code = @"
public class Account
{
    public int Balance { get; private set; }

    public function Deposit(amount: int)
    {
        Balance = Balance + amount
    }

    public function Get(): int
    {
        return Balance
    }
}

function Main()
{
    var a = new Account()
    a.Deposit(100)
    print(a.Get())
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
    private int X { get; private set; }
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
    private int X { get; public set; }
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
    protected int X { internal get; }
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
    public int X { private get; private set; }
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
