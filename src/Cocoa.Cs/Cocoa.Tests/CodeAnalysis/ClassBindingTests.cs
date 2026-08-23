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
            Assert.True(point.BaseType == ClassTypeSymbol.SystemObject);
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
                Assert.True(compilation.GlobalScope.Classes.Single(c => c.Name == "Point").BaseType == ClassTypeSymbol.SystemObject);
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
            Assert.NotSame(ClassTypeSymbol.SystemObject, point.BaseType);
        }

        [Fact]
        public void ObjectModel_ObjectOnlyBase_Override_ReportsNoBase()
        {
            var code = @"
public class Point
{
    public override function ToString(): string
    {
        return ""Point""
    }
}";
            var diagnostics = GetDiagnostics(code);
            Assert.Contains(diagnostics, d => d.Message.Contains("没有基类"));
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
