using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Cocoa.Syntax;
using CSyntax = global::Cocoa.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
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
        public void GetTypeByMetadataName_ResolvesGenericDefinitionAndInstantiation()
        {
            var code = @"using System

public class Box<T>
{
    private _value: T

    public constructor(value: T)
    {
        _value = value
    }
}

function Main()
{
}";
            var compilation = Compilation.Create(SyntaxTree.Parse(code));

            var boxDef = compilation.GetTypeByMetadataName("Box`1");
            Assert.NotNull(boxDef);
            Assert.IsType<NamedTypeSymbol>(boxDef);
            var boxDefNamed = (NamedTypeSymbol)boxDef!;
            Assert.True(boxDefNamed.IsGenericDefinition);
            Assert.Equal(1, boxDefNamed.TypeParameters.Length);

            var boxInt = compilation.GetTypeByMetadataName("Box`1#System.Int32");
            Assert.NotNull(boxInt);
            Assert.IsType<InstantiatedTypeSymbol>(boxInt);
            Assert.Same(boxDefNamed, ((InstantiatedTypeSymbol)boxInt!).GenericDefinition);
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
        public void Compilation_References_ExposeMetadataReferences()
        {
            var code = @"function Main() {}";
            var compilation = Compilation.Create(new[] { "System.Core.coa", "C:\\libs\\Foo.dll" }, SyntaxTree.Parse(code));

            var references = compilation.References;
            Assert.Equal(2, references.Length);
            Assert.Equal("System.Core.coa", references[0].Display);
            Assert.Equal("C:\\libs\\Foo.dll", references[1].Display);
        }

        [Fact]
        public void Emit_MetadataReferenceOverloads_Resolve()
        {
            var compilation = Compilation.Create(new[] { "System.Core.coa", "C:\\libs\\Foo.dll" }, SyntaxTree.Parse("function Main() {"));

            var viaCompilationRefs = compilation.Emit("test", "C:\\Temp\\cocoa-test-out.exe");
            Assert.Contains(viaCompilationRefs, d => d.IsError);

            var viaMetadataRefs = compilation.Emit("test", compilation.References, "C:\\Temp\\cocoa-test-out.exe");
            Assert.Contains(viaMetadataRefs, d => d.IsError);
        }

        [Fact]
        public void AssemblySymbols_CarryDisplay_And_EmitConsumes()
        {
            var compilation = Compilation.Create(new[] { "System.Core.coa", "C:\\libs\\Foo.dll" }, SyntaxTree.Parse("function Main() {"));

            var foo = compilation.ReferencedAssemblies.FirstOrDefault(a => a.Name == "Foo");
            Assert.True(foo != null, $"refs={compilation.References.Length} asm={compilation.ReferencedAssemblies.Length} foo={foo?.Display ?? "null"}");
            Assert.Equal("C:\\libs\\Foo.dll", foo!.Display);

            var viaAssemblies = compilation.Emit("test", compilation.ReferencedAssemblies, "C:\\Temp\\cocoa-asm-out.exe");
            Assert.Contains(viaAssemblies, d => d.IsError);
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

        [Fact]
        public void SemanticModel_GetSymbolInfo_ResolvesNames()
        {
            var code = @"using System

public class Point
{
    public function Get(): i32 { return 0 }
}

var G: i32 = 5

function Compute(): i32 { return 1 }

function Main()
{
    Console.WriteLine(G)
    Console.WriteLine(Compute())
}";
            var tree = SyntaxTree.Parse(code);
            var compilation = Compilation.Create(tree);
            var model = compilation.GetSemanticModel(tree);

            var nameExpressions = Descendants(tree.Root).OfType<NameExpressionSyntax>().ToList();
            var gExpression = nameExpressions.First(n => n.IdentifierToken.Text == "G");
            var computeCall = Descendants(tree.Root).OfType<CallExpressionSyntax>().First(c => c.Identifier.Text == "Compute");

            var gSymbol = model.GetSymbolInfo(gExpression);
            Assert.NotNull(gSymbol);
            Assert.Equal(SymbolKind.GlobalVariable, gSymbol!.Kind);
            Assert.Equal("G", gSymbol.Name);

            var computeSymbol = model.GetSymbolInfo(computeCall);
            Assert.NotNull(computeSymbol);
            Assert.Equal(SymbolKind.Function, computeSymbol!.Kind);
            Assert.Equal("Compute", computeSymbol.Name);

            Assert.Null(model.GetSymbolInfo(tree.Root));
        }

        [Fact]
        public void SemanticModel_BoundTree_ResolvesLocalsParamsAndInstanceMembers()
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

function UsePoint(p: Point): i32
{
    var local: i32 = p.Get()
    var sum: i32 = local + p.Get()
    return sum
}

function Main()
{
    var pt = new Point(3)
    UsePoint(pt)
}";
            var tree = SyntaxTree.Parse(code);
            var compilation = Compilation.Create(tree);
            var model = compilation.GetSemanticModel(tree);

            var localUse = Descendants(tree.Root).OfType<NameExpressionSyntax>().First(n => n.IdentifierToken.Text == "local");
            var localSymbol = model.GetSymbolInfo(localUse);
            Assert.NotNull(localSymbol);
            Assert.Equal(SymbolKind.LocalVariable, localSymbol!.Kind);
            Assert.Equal("local", localSymbol.Name);
            Assert.Equal(TypeSymbol.Int32, model.GetTypeInfo(localUse));

            var pUse = Descendants(tree.Root).OfType<NameExpressionSyntax>().First(n => n.IdentifierToken.Text == "p");
            var pSymbol = model.GetSymbolInfo(pUse);
            Assert.NotNull(pSymbol);
            Assert.Equal(SymbolKind.Parameter, pSymbol!.Kind);
            Assert.Equal("Point", model.GetTypeInfo(pUse)!.Name);

            var getCall = Descendants(tree.Root).OfType<MemberCallExpressionSyntax>().First(m => m.IdentifierToken.Text == "Get");
            var getSymbol = model.GetSymbolInfo(getCall);
            Assert.NotNull(getSymbol);
            Assert.Equal(SymbolKind.Function, getSymbol!.Kind);
            Assert.Equal("Get", getSymbol.Name);
            Assert.Equal(TypeSymbol.Int32, model.GetTypeInfo(getCall));
        }

        [Fact]
        public void SemanticModel_GetOperation_ReturnsBoundNode()
        {
            var code = @"using System

function Add(a: i32, b: i32): i32
{
    return a + b
}

function Main()
{
    var x = Add(1, 2)
}";
            var tree = SyntaxTree.Parse(code);
            var compilation = Compilation.Create(tree);
            var model = compilation.GetSemanticModel(tree);

            var addCall = Descendants(tree.Root).OfType<CallExpressionSyntax>().First(c => c.Identifier.Text == "Add");
            var operation = model.GetOperation(addCall);
            Assert.NotNull(operation);
            Assert.IsType<BoundCallExpression>(operation);

            var aUse = Descendants(tree.Root).OfType<NameExpressionSyntax>().First(n => n.IdentifierToken.Text == "a");
            var aOperation = model.GetOperation(aUse);
            Assert.NotNull(aOperation);
            Assert.IsType<BoundVariableExpression>(aOperation);
        }

        [Fact]
        public void SemanticModel_PropertyThisBaseAndDiagnostics()
        {
            var code = @"using System

public class Counter
{
    private _count: i32

    public property Count: i32 { get set }

    public function Increment(): i32
    {
        _count = _count + 1
        return this.Count
    }
}

function Main()
{
    var c = new Counter()
    var n = c.Count
    var a = new i32[] { 1, 2, 3 }
    var len = a.Length
    c.Count = n + len
}";
            var tree = SyntaxTree.Parse(code);
            var compilation = Compilation.Create(tree);
            var model = compilation.GetSemanticModel(tree);

            var countAccess = Descendants(tree.Root).OfType<MemberAccessExpressionSyntax>().First(m => m.IdentifierToken.Text == "Count");
            var countSymbol = model.GetSymbolInfo(countAccess);
            Assert.NotNull(countSymbol);
            Assert.Equal(SymbolKind.Property, countSymbol!.Kind);
            Assert.Equal("Count", countSymbol.Name);

            var thisExpression = Descendants(tree.Root).OfType<ThisExpressionSyntax>().First();
            var thisSymbol = model.GetSymbolInfo(thisExpression);
            Assert.NotNull(thisSymbol);
            Assert.Equal("Counter", thisSymbol!.Name);

            var oneLiteral = Descendants(tree.Root).OfType<LiteralExpressionSyntax>().First(l => (int)l.Value == 1);
            Assert.Equal(TypeSymbol.Int32, model.GetTypeInfo(oneLiteral));

            var lengthAccess = Descendants(tree.Root).OfType<MemberAccessExpressionSyntax>().First(m => m.IdentifierToken.Text == "Length");
            Assert.Equal(TypeSymbol.Int32, model.GetTypeInfo(lengthAccess));

            Assert.DoesNotContain(model.GetDiagnostics(), d => d.IsError);
        }

        [Fact]
        public void SemanticModel_GetSymbolInfo_ResolvesStaticMemberAccess()
        {
            var code = @"using System

public class Utils
{
    public static function Twice(x: i32): i32 { return x * 2 }
}

function Main()
{
    Utils.Twice(2)
}";
            var tree = SyntaxTree.Parse(code);
            var compilation = Compilation.Create(tree);
            var model = compilation.GetSemanticModel(tree);

            var memberCall = Descendants(tree.Root).OfType<MemberCallExpressionSyntax>().First(m => m.IdentifierToken.Text == "Twice");
            var symbol = model.GetSymbolInfo(memberCall);
            Assert.NotNull(symbol);
            Assert.Equal(SymbolKind.Function, symbol!.Kind);
            Assert.Equal("Twice", symbol.Name);
        }

        [Fact]
        public void SyntaxTraversal_DescendantsAndWalker()
        {
            var code = @"using System

public class Point
{
    public function Get(): i32 { return 0 }
}

function Main()
{
    var n: i32 = 1
}";
            var tree = SyntaxTree.Parse(code);

            var typeClauses = tree.Root.DescendantNodes().OfType<TypeClauseSyntax>().ToList();
            Assert.Equal(2, typeClauses.Count);

            Assert.Contains(tree.Root, tree.Root.DescendantNodesAndSelf());
            Assert.Contains(tree.Root.DescendantTokens(), t => t.Text == "var");

            var collector = new CollectingWalker();
            collector.Visit(tree.Root);
            Assert.True(collector.Count > 0);
            Assert.Contains(collector.Nodes.OfType<ClassDeclarationSyntax>(), c => c.Identifier.Text == "Point");
        }

        [Fact]
        public void SyntaxFactory_GreenTree_RoundTrips()
        {
            var left = SyntaxFactory.Identifier("a");
            var plus = SyntaxFactory.Token(SyntaxKind.PlusToken);
            var right = SyntaxFactory.Identifier("b");
            var binary = SyntaxFactory.Node(SyntaxKind.BinaryExpression, left, plus, right);

            Assert.Equal(SyntaxKind.BinaryExpression, binary.Kind);
            Assert.Equal(3, binary.SlotCount);
            Assert.Equal(3, binary.Width);
            Assert.Equal("a+b", binary.ToString());
            Assert.Same(left, binary.GetSlot(0));
            Assert.Same(plus, binary.GetSlot(1));

            var spaced = new GreenToken(SyntaxKind.PlusToken, "+",
                leadingTrivia: ImmutableArray.Create(new GreenTrivia(SyntaxKind.WhitespaceTrivia, " ")),
                trailingTrivia: ImmutableArray.Create(new GreenTrivia(SyntaxKind.WhitespaceTrivia, " ")));
            Assert.Equal(" + ", spaced.ToString());
            Assert.Equal(3, spaced.Width);
        }

        [Fact]
        public void GreenRoot_RoundTripsToSource()
        {
            var code = @"using System

function Main()
{
    Console.WriteLine(""hello"")
}";
            var tree = SyntaxTree.Parse(code);

            var green = tree.GreenRoot;
            Assert.NotNull(green);
            Assert.Equal(SyntaxKind.CompilationUnit, green.Kind);

            Assert.Equal(code, green.ToString());
            Assert.Same(green, tree.GreenRoot);
        }

        [Fact]
        public void GreenNode_RawKind_MatchesKindStorage()
        {
            // P1-E-1：绿树存储层 RawKind:int 化——存储与共享枚举解耦（过渡态便捷视图 Kind == (SyntaxKind)RawKind）
            var code = @"function Main()
{
    var x = 1 + 2
}";
            var tree = SyntaxTree.Parse(code);
            var green = tree.GreenRoot;
            Assert.Equal((int)SyntaxKind.CompilationUnit, green.RawKind);
            Assert.Equal(green.Kind, (SyntaxKind)green.RawKind);

            var descendants = tree.Root.DescendantNodesAndSelf();
            foreach (var node in descendants)
            {
                var g = node.ToGreen();
                Assert.Equal((int)g.Kind, g.RawKind);
                Assert.Equal(g.Kind, (SyntaxKind)g.RawKind);
            }
        }

        [Fact]
        public void GreenRoot_RoundTrips_UsingAlias()
        {
            var code = @"using Alias = System.Collections.List

function Main()
{
    var list: Alias = null
}";
            var tree = SyntaxTree.Parse(code);

            Assert.Equal(code, tree.GreenRoot.ToString());

            var directive = ((CompilationUnitSyntax)tree.Root).Members.OfType<UsingDirectiveSyntax>().First();
            Assert.Equal("System.Collections.List", directive.Name);
            Assert.Equal("Alias", directive.Alias);
            Assert.NotNull(directive.EqualsToken);
            Assert.Equal(SyntaxKind.EqualsToken, directive.EqualsToken!.Kind);

            var typed = Assert.IsType<UsingDirectiveSyntax>(tree.GreenRoot.CreateTypedRed(tree).DescendantNodes().OfType<UsingDirectiveSyntax>().First());
            Assert.NotNull(typed.EqualsToken);
            Assert.Equal("=", typed.EqualsToken!.Text);
        }

        [Fact]
        public void GreenRoot_RoundTrips_DelegateCoForm()
        {
            var code = @"delegate IntTransform(x: i32): i32

delegate Handler(a: i32, b: String)

function Main()
{
    var f: IntTransform = null
}";
            var tree = SyntaxTree.Parse(code);

            Assert.Equal(code, tree.GreenRoot.ToString());

            var explicitDelegate = ((CompilationUnitSyntax)tree.Root).Members.OfType<DelegateDeclarationSyntax>().First();
            Assert.Equal("IntTransform", explicitDelegate.Identifier.Text);
            Assert.NotNull(explicitDelegate.ReturnType);
            Assert.NotNull(explicitDelegate.ReturnType!.ColonToken);
            Assert.NotNull(explicitDelegate.OpenParenToken);

            var implicitDelegate = ((CompilationUnitSyntax)tree.Root).Members.OfType<DelegateDeclarationSyntax>().ElementAt(1);
            Assert.Equal("Handler", implicitDelegate.Identifier.Text);
            Assert.Null(implicitDelegate.ReturnType);

            var typed = Assert.IsType<DelegateDeclarationSyntax>(tree.GreenRoot.CreateTypedRed(tree).DescendantNodes().OfType<DelegateDeclarationSyntax>().First());
            Assert.Equal("IntTransform", typed.Identifier.Text);
            Assert.NotNull(typed.ReturnType);
            Assert.Equal("i32", typed.ReturnType!.Identifier.Text);
            Assert.NotNull(typed.OpenParenToken);
            Assert.NotNull(typed.CloseParenToken);
        }

        [Fact]
        public void GreenRoot_RoundTrips_DelegateCsForm()
        {
            var code = @"public delegate int Transformer(int x);";
            var tree = SyntaxTree.ParseCs(code);

            Assert.Equal(code, tree.GreenRoot.ToString());

            var delegateDecl = ((CSyntax.CompilationUnitSyntax)tree.Root).Members.OfType<CSyntax.DelegateDeclarationSyntax>().First();
            Assert.Equal("Transformer", delegateDecl.Identifier.Text);
            Assert.NotNull(delegateDecl.ReturnType);
            Assert.Null(delegateDecl.ReturnType!.ColonToken);
            Assert.NotNull(delegateDecl.SemicolonToken);

            var typed = Assert.IsType<CSyntax.DelegateDeclarationSyntax>(tree.GreenRoot.CreateTypedRed(tree).DescendantNodes().OfType<CSyntax.DelegateDeclarationSyntax>().First());
            Assert.Equal("Transformer", typed.Identifier.Text);
            Assert.Equal("int", typed.ReturnType!.Identifier.Text);
            Assert.NotNull(typed.SemicolonToken);
        }

        [Fact]
        public void FromGreen_ReproducesRedTree()
        {
            var code = @"using System

function Main()
{
    Console.WriteLine(1 + 2)
}";
            var original = SyntaxTree.Parse(code);
            var rebuilt = SyntaxTree.FromGreen(original.GreenRoot);

            Assert.Equal(original.Text.ToString(), rebuilt.Text.ToString());
            Assert.Equal(original.Root.ToString(), rebuilt.Root.ToString());
            Assert.Equal(original.GreenRoot.ToString(), rebuilt.GreenRoot.ToString());
        }

        [Fact]
        public void RedNode_LazyView_FromGreen()
        {
            var tree = SyntaxTree.Parse("function Main() { }");

            var left = SyntaxFactory.Identifier("a");
            var plus = SyntaxFactory.Token(SyntaxKind.PlusToken);
            var right = SyntaxFactory.Identifier("b");
            var green = SyntaxFactory.Node(SyntaxKind.BinaryExpression, left, plus, right);

            var red = green.CreateRed(tree);

            Assert.Equal(SyntaxKind.BinaryExpression, red.Kind);
            Assert.Same(green, red.Green);
            Assert.Equal("a+b", red.ToString());

            var children = red.GetChildren().ToList();
            Assert.Equal(3, children.Count);
            Assert.Equal(SyntaxKind.IdentifierToken, children[0].Kind);
            Assert.Equal(SyntaxKind.PlusToken, children[1].Kind);
            Assert.Equal(SyntaxKind.IdentifierToken, children[2].Kind);
            Assert.Equal("a", children[0].ToString());
            Assert.Equal("+", children[1].ToString());

            var leftRed = Assert.IsType<RedNode>(children[0]);
            Assert.Same(left, leftRed.Green);
            Assert.Same(red, leftRed.Parent);
        }

        [Fact]
        public void TypedRed_FromGreen_BinaryExpression()
        {
            var tree = SyntaxTree.Parse(@"function Main()
{
    var x = a + b
}");
            var binaryRed = tree.Root.DescendantNodes().OfType<BinaryExpressionSyntax>().First();
            var green = binaryRed.ToGreen();
            var typed = green.CreateTypedRed(tree);

            Assert.IsType<BinaryExpressionSyntax>(typed);
            var binary = (BinaryExpressionSyntax)typed;
            Assert.Equal(CocoaSyntaxKind.BinaryExpression, binary.Kind);
            Assert.IsType<NameExpressionSyntax>(binary.Left);
            Assert.Equal(SyntaxKind.PlusToken, binary.OperatorToken.Kind);
            Assert.Equal("+", binary.OperatorToken.Text);
            Assert.IsType<NameExpressionSyntax>(binary.Right);
            Assert.Equal("a", ((NameExpressionSyntax)binary.Left).IdentifierToken.Text);
            Assert.Equal("b", ((NameExpressionSyntax)binary.Right).IdentifierToken.Text);
        }

        [Fact]
        public void TypedRed_FromGreen_CoversMoreKinds()
        {
            var tree = SyntaxTree.Parse(@"function Main()
{
    var x = -a
    x = (a + b)
    var s = a.b
}");
            var unary = tree.Root.DescendantNodes().OfType<UnaryExpressionSyntax>().First();
            var paren = tree.Root.DescendantNodes().OfType<ParenthesizedExpressionSyntax>().First();
            var assign = tree.Root.DescendantNodes().OfType<AssignmentExpressionSyntax>().First();
            var member = tree.Root.DescendantNodes().OfType<MemberAccessExpressionSyntax>().First();

            Assert.IsType<UnaryExpressionSyntax>(unary.ToGreen().CreateTypedRed(tree));
            Assert.IsType<ParenthesizedExpressionSyntax>(paren.ToGreen().CreateTypedRed(tree));
            Assert.IsType<AssignmentExpressionSyntax>(assign.ToGreen().CreateTypedRed(tree));
            Assert.IsType<MemberAccessExpressionSyntax>(member.ToGreen().CreateTypedRed(tree));
        }

        [Fact]
        public void TypedRed_FromGreen_Statements()
        {
            var tree = SyntaxTree.Parse(@"function Main(): i32
{
    if a == 1
    {
        return 1
    }
    else
    {
        return 2
    }
    while a > 0
    {
        a = a - 1
    }
    return 0
}");
            var ifRed = tree.Root.DescendantNodes().OfType<IfStatementSyntax>().First();
            var whileRed = tree.Root.DescendantNodes().OfType<WhileStatementSyntax>().First();
            var returnRed = tree.Root.DescendantNodes().OfType<ReturnStatementSyntax>().First();
            var blockRed = tree.Root.DescendantNodes().OfType<BlockStatementSyntax>().First();

            var typedIf = Assert.IsType<IfStatementSyntax>(ifRed.ToGreen().CreateTypedRed(tree));
            Assert.NotNull(typedIf.ElseClause);
            Assert.Equal(SyntaxKind.EqualsEqualsToken, Assert.IsType<BinaryExpressionSyntax>(typedIf.Condition).OperatorToken.Kind);

            var typedWhile = Assert.IsType<WhileStatementSyntax>(whileRed.ToGreen().CreateTypedRed(tree));
            Assert.Equal(SyntaxKind.GreaterToken, Assert.IsType<BinaryExpressionSyntax>(typedWhile.Condition).OperatorToken.Kind);
            Assert.IsType<BlockStatementSyntax>(typedWhile.Body);

            Assert.IsType<ReturnStatementSyntax>(returnRed.ToGreen().CreateTypedRed(tree));
            Assert.IsType<BlockStatementSyntax>(blockRed.ToGreen().CreateTypedRed(tree));
        }

        [Fact]
        public void TypedRed_FromGreen_VariableDeclaration()
        {
            var tree = SyntaxTree.Parse(@"function Main()
{
    var x: i32 = 1
    let y = 2
}");
            var declared = tree.Root.DescendantNodes().OfType<VariableDeclarationSyntax>().ToList();
            Assert.Equal(2, declared.Count);

            var typedWithType = Assert.IsType<VariableDeclarationSyntax>(declared[0].ToGreen().CreateTypedRed(tree));
            Assert.Equal(SyntaxKind.VarKeyword, typedWithType.Keyword!.Kind);
            Assert.Equal("x", typedWithType.Identifier.Text);
            Assert.NotNull(typedWithType.TypeClause);
            Assert.Equal("i32", typedWithType.TypeClause!.Identifier.Text);
            Assert.NotNull(typedWithType.Initializer);

            var typedLet = Assert.IsType<VariableDeclarationSyntax>(declared[1].ToGreen().CreateTypedRed(tree));
            Assert.Equal(SyntaxKind.LetKeyword, typedLet.Keyword!.Kind);
            Assert.Equal("y", typedLet.Identifier.Text);
            Assert.Null(typedLet.TypeClause);
            Assert.NotNull(typedLet.Initializer);
        }

        [Fact]
        public void TypedRed_FromGreen_Call()
        {
            var tree = SyntaxTree.Parse(@"function Main()
{
    Foo(1, 2)
    Bar<i32>(1)
}");
            var call = tree.Root.DescendantNodes().OfType<CallExpressionSyntax>().First();
            var typedCall = Assert.IsType<CallExpressionSyntax>(call.ToGreen().CreateTypedRed(tree));
            Assert.Equal("Foo", typedCall.Identifier.Text);
            Assert.Equal(2, typedCall.Arguments.Count);
            Assert.Null(typedCall.TypeArguments);

            var genericCall = tree.Root.DescendantNodes().OfType<CallExpressionSyntax>().First(c => c.Identifier.Text == "Bar");
            var typedGeneric = Assert.IsType<CallExpressionSyntax>(genericCall.ToGreen().CreateTypedRed(tree));
            Assert.NotNull(typedGeneric.TypeArguments);
            Assert.Equal("i32", typedGeneric.TypeArguments!.Arguments[0].Identifier.Text);
        }

        [Fact]
        public void TypedRed_FromGreen_MemberCall_ObjectCreation_ElementAccess()
        {
            var tree = SyntaxTree.Parse(@"function Main()
{
    Console.WriteLine(1 + 2)
    var x = new Foo(1)
    var y = a[0]
}");
            var memberCall = tree.Root.DescendantNodes().OfType<MemberCallExpressionSyntax>().First();
            var typedMemberCall = Assert.IsType<MemberCallExpressionSyntax>(memberCall.ToGreen().CreateTypedRed(tree));
            Assert.Equal("WriteLine", typedMemberCall.IdentifierToken.Text);
            Assert.Equal(1, typedMemberCall.Arguments.Count);
            Assert.Equal(SyntaxKind.OpenParenthesisToken, typedMemberCall.OpenParenthesisToken.Kind);

            var objectCreation = tree.Root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().First();
            var typedCreation = Assert.IsType<ObjectCreationExpressionSyntax>(objectCreation.ToGreen().CreateTypedRed(tree));
            Assert.Equal("Foo", typedCreation.Identifier.Text);
            Assert.Equal(SyntaxKind.NewKeyword, typedCreation.NewKeyword.Kind);
            Assert.Equal(1, typedCreation.Arguments.Count);

            var elementAccess = tree.Root.DescendantNodes().OfType<ElementAccessExpressionSyntax>().First();
            var typedAccess = Assert.IsType<ElementAccessExpressionSyntax>(elementAccess.ToGreen().CreateTypedRed(tree));
            Assert.Equal(SyntaxKind.OpenBracketToken, typedAccess.OpenBracketToken.Kind);
            Assert.Equal(SyntaxKind.CloseBracketToken, typedAccess.CloseBracketToken.Kind);
        }

        [Fact]
        public void TypedRed_WholeFile_RoundTrips()
        {
            var code = @"function Main(): i32
{
    var x = 1
    if x == 1
    {
        return 1
    }
    return 0
}";
            var tree = SyntaxTree.Parse(code);
            var typed = tree.GreenRoot.CreateTypedRed(tree);

            var unit = Assert.IsType<CompilationUnitSyntax>(typed);
            Assert.Equal(1, unit.Members.Length);
            var fn = Assert.IsType<FunctionDeclarationSyntax>(unit.Members[0]);
            Assert.Equal("Main", fn.Identifier.Text);
            Assert.NotNull(fn.Type);
            Assert.Equal("i32", fn.Type!.Identifier.Text);
            Assert.NotNull(fn.Body);
            Assert.Equal(3, fn.Body!.Statements.Length);
            Assert.IsType<VariableDeclarationSyntax>(fn.Body.Statements[0]);
            Assert.IsType<IfStatementSyntax>(fn.Body.Statements[1]);
            Assert.IsType<ReturnStatementSyntax>(fn.Body.Statements[2]);
        }

        [Fact]
        public void TypedRed_FromGreen_ControlFlowStatements()
        {
            var tree = SyntaxTree.Parse(@"function Main(): i32
{
    var i = 0
    do
    {
        i = i + 1
        if i > 5
        {
            break
        }
        continue
    }
    while i < 10
    return i
}");
            var doWhile = tree.Root.DescendantNodes().OfType<DoWhileStatementSyntax>().First();
            var typedDoWhile = Assert.IsType<DoWhileStatementSyntax>(doWhile.ToGreen().CreateTypedRed(tree));
            Assert.Equal(SyntaxKind.DoKeyword, typedDoWhile.DoKeyword.Kind);
            Assert.Equal(SyntaxKind.LessToken, Assert.IsType<BinaryExpressionSyntax>(typedDoWhile.Condition).OperatorToken.Kind);

            var breakStmt = tree.Root.DescendantNodes().OfType<BreakStatementSyntax>().First();
            Assert.IsType<BreakStatementSyntax>(breakStmt.ToGreen().CreateTypedRed(tree));

            var continueStmt = tree.Root.DescendantNodes().OfType<ContinueStatementSyntax>().First();
            Assert.IsType<ContinueStatementSyntax>(continueStmt.ToGreen().CreateTypedRed(tree));
        }

        [Fact]
        public void TypedRed_FromGreen_CastIsAsPostfix()
        {
            var tree = SyntaxTree.Parse(@"function Main()
{
    var x = (i32)1
    var b = x is i32
    var c = x as i32
    x++
}");
            var cast = tree.Root.DescendantNodes().OfType<CastExpressionSyntax>().First();
            Assert.Equal("i32", Assert.IsType<CastExpressionSyntax>(cast.ToGreen().CreateTypedRed(tree)).TypeName.Text);

            var isExpression = tree.Root.DescendantNodes().OfType<IsExpressionSyntax>().First();
            Assert.IsType<IsExpressionSyntax>(isExpression.ToGreen().CreateTypedRed(tree));

            var asExpression = tree.Root.DescendantNodes().OfType<AsExpressionSyntax>().First();
            Assert.IsType<AsExpressionSyntax>(asExpression.ToGreen().CreateTypedRed(tree));

            var postfix = tree.Root.DescendantNodes().OfType<PostfixIncrementExpressionSyntax>().First();
            Assert.IsType<PostfixIncrementExpressionSyntax>(postfix.ToGreen().CreateTypedRed(tree));
        }

        [Fact]
        public void TypedRed_FromGreen_Enum()
        {
            var tree = SyntaxTree.Parse("public enum Color { Red, Green = 2 }");
            var enumRed = tree.Root.DescendantNodes().OfType<EnumDeclarationSyntax>().First();
            var typedEnum = Assert.IsType<EnumDeclarationSyntax>(enumRed.ToGreen().CreateTypedRed(tree));
            Assert.Equal("Color", typedEnum.Identifier.Text);
            Assert.Equal(2, typedEnum.Members.Count);
            Assert.Equal("Red", typedEnum.Members[0].Identifier.Text);
            Assert.Equal("Green", typedEnum.Members[1].Identifier.Text);
            Assert.NotNull(typedEnum.Members[1].EqualsToken);
        }

        [Fact]
        public void TypedRed_FromGreen_Conditional_TypeParams_Field()
        {
            var tree = SyntaxTree.Parse(@"function F<T>(x: T): T
{
    return x
}

public class Box
{
    private _x: i32
    public constructor(x: i32)
    {
        _x = x
    }
}

function Main()
{
    var y = a > 0 ? 1 : 2
}");
            var conditional = tree.Root.DescendantNodes().OfType<ConditionalExpressionSyntax>().First();
            var typedConditional = Assert.IsType<ConditionalExpressionSyntax>(conditional.ToGreen().CreateTypedRed(tree));
            Assert.Equal(SyntaxKind.QuestionToken, typedConditional.QuestionToken.Kind);
            Assert.Equal(SyntaxKind.ColonToken, typedConditional.ColonToken.Kind);

            var typeParameters = tree.Root.DescendantNodes().OfType<TypeParameterListSyntax>().First();
            var typedTypeParameters = Assert.IsType<TypeParameterListSyntax>(typeParameters.ToGreen().CreateTypedRed(tree));
            Assert.Equal(1, typedTypeParameters.Parameters.Length);
            Assert.Equal("T", typedTypeParameters.Parameters[0].Text);

            var field = tree.Root.DescendantNodes().OfType<ClassFieldDeclarationSyntax>().First();
            var typedField = Assert.IsType<ClassFieldDeclarationSyntax>(field.ToGreen().CreateTypedRed(tree));
            Assert.Equal("_x", typedField.Identifier.Text);
            Assert.Equal("i32", typedField.Type.Identifier.Text);
        }

        [Fact]
        public void TypedRed_FromGreen_TypeAndDelegateBatch()
        {
            var tree = SyntaxTree.Parse(@"delegate IntTransform(x: i32): i32

function F<T>(x: T): T where T: class
{
    return x
}

function Main()
{
    var a: i32[] = null
    var f: (i32) -> i32 = null
    var l: List<i32> = null
}");
            var arrayType = tree.Root.DescendantNodes().OfType<ArrayTypeClauseSyntax>().First();
            var typedArray = Assert.IsType<ArrayTypeClauseSyntax>(arrayType.ToGreen().CreateTypedRed(tree));
            Assert.Equal("i32", typedArray.ElementType.Identifier.Text);
            Assert.Equal("i32", typedArray.ElementType.Identifier.Text);

            var functionType = tree.Root.DescendantNodes().OfType<FunctionTypeSyntax>().First();
            var typedFunctionType = Assert.IsType<FunctionTypeSyntax>(functionType.ToGreen().CreateTypedRed(tree));
            Assert.Equal(1, typedFunctionType.ParameterTypes.Count);
            Assert.Equal(SyntaxKind.ArrowToken, typedFunctionType.ArrowToken.Kind);

            var genericType = tree.Root.DescendantNodes().OfType<GenericTypeClauseSyntax>().First();
            var typedGenericType = Assert.IsType<GenericTypeClauseSyntax>(genericType.ToGreen().CreateTypedRed(tree));
            Assert.Equal("List", typedGenericType.Identifier.Text);
            Assert.Equal(1, typedGenericType.TypeArguments.Length);

            var whereClause = tree.Root.DescendantNodes().OfType<WhereClauseSyntax>().First();
            Assert.IsType<WhereClauseSyntax>(whereClause.ToGreen().CreateTypedRed(tree));

            var delegateDecl = tree.Root.DescendantNodes().OfType<DelegateDeclarationSyntax>().First();
            var typedDelegate = Assert.IsType<DelegateDeclarationSyntax>(delegateDecl.ToGreen().CreateTypedRed(tree));
            Assert.Equal("IntTransform", typedDelegate.Identifier.Text);
            Assert.Equal(1, typedDelegate.Parameters.Count);
        }

        [Fact]
        public void TypedRed_FromGreen_TryCatchForeach()
        {
            var tree = SyntaxTree.Parse(@"function Main()
{
    try
    {
        foreach (var x in arr)
        {
            arr[0] = x
        }
    }
    catch (e: Exception)
    {
    }
    finally
    {
    }
}");
            var tryStatement = tree.Root.DescendantNodes().OfType<TryStatementSyntax>().First();
            var typedTry = Assert.IsType<TryStatementSyntax>(tryStatement.ToGreen().CreateTypedRed(tree));
            Assert.Equal(1, typedTry.Catches.Length);
            Assert.NotNull(typedTry.Finally);

            var catchClause = tree.Root.DescendantNodes().OfType<CatchClauseSyntax>().First();
            var typedCatch = Assert.IsType<CatchClauseSyntax>(catchClause.ToGreen().CreateTypedRed(tree));
            Assert.Equal("Exception", typedCatch.Type.Identifier.Text);

            var foreachStatement = tree.Root.DescendantNodes().OfType<ForeachStatementSyntax>().First();
            var typedForeach = Assert.IsType<ForeachStatementSyntax>(foreachStatement.ToGreen().CreateTypedRed(tree));
            Assert.Equal("x", typedForeach.Identifier.Text);
            Assert.Equal(SyntaxKind.InKeyword, typedForeach.InKeyword.Kind);
            Assert.Equal(SyntaxKind.OpenParenthesisToken, typedForeach.OpenParenToken!.Kind);
        }

        [Fact]
        public void TypedRed_FromGreen_ForAndArrayCreation()
        {
            var tree = SyntaxTree.Parse(@"function Main()
{
    var sum = 0
    for var i = 0 to 10 step 2
    {
        sum = sum + i
    }
    var a = new i32[3]
    var b = new i32[] { 1, 2, 3 }
    sum = b[0]
}");
            var forStatement = tree.Root.DescendantNodes().OfType<ForStatementSyntax>().First();
            var typedFor = Assert.IsType<ForStatementSyntax>(forStatement.ToGreen().CreateTypedRed(tree));
            Assert.Equal(SyntaxKind.ForKeyword, typedFor.Keyword.Kind);
            Assert.Equal("i", typedFor.Identifier!.Text);
            Assert.Equal(SyntaxKind.ToKeyword, typedFor.ToKeyword.Kind);
            Assert.Equal(SyntaxKind.StepKeyword, typedFor.StepKeyword!.Kind);
            Assert.NotNull(typedFor.Step);

            var sized = tree.Root.DescendantNodes().OfType<ArrayCreationExpressionSyntax>().First(a => a.Size != null);
            var typedSized = Assert.IsType<ArrayCreationExpressionSyntax>(sized.ToGreen().CreateTypedRed(tree));
            Assert.NotNull(typedSized.Size);

            var withElements = tree.Root.DescendantNodes().OfType<ArrayCreationExpressionSyntax>().First(a => a.Size == null);
            var typedElements = Assert.IsType<ArrayCreationExpressionSyntax>(withElements.ToGreen().CreateTypedRed(tree));
            Assert.Equal(3, typedElements.Elements.Count);
            Assert.NotNull(typedElements.OpenBraceToken);
            Assert.NotNull(typedElements.CloseBraceToken);
        }

        [Fact]
        public void TypedRed_FromGreen_NamespaceAndUsing()
        {
            var tree = SyntaxTree.Parse(@"using System

namespace Foo.Bar
{
    function Helper(): i32
    {
        return 1
    }
}");
            var usingDirective = tree.Root.DescendantNodes().OfType<UsingDirectiveSyntax>().First();
            var typedUsing = Assert.IsType<UsingDirectiveSyntax>(usingDirective.ToGreen().CreateTypedRed(tree));
            Assert.Equal("System", typedUsing.NameTokens[0].Text);
            Assert.Null(typedUsing.AliasToken);

            var namespaceDeclaration = tree.Root.DescendantNodes().OfType<NamespaceDeclarationSyntax>().First();
            var typedNamespace = Assert.IsType<NamespaceDeclarationSyntax>(namespaceDeclaration.ToGreen().CreateTypedRed(tree));
            Assert.Equal(3, typedNamespace.NameTokens.Length);
            Assert.Equal(1, typedNamespace.Members.Length);
            Assert.IsType<FunctionDeclarationSyntax>(typedNamespace.Members[0]);
        }

        [Fact]
        public void TypedRed_FromGreen_ClassInterfaceCStyleFor()
        {
            var tree = SyntaxTree.Parse(@"public interface IShape
{
    public function Area(): i32
}

public class Box : IShape
{
    private _x: i32

    public function Get(): i32
    {
        return _x
    }
}

function Main()
{
    for (var i = 0; i < 10; i++)
    {
        var y = i
    }
}");
            var interfaceDeclaration = tree.Root.DescendantNodes().OfType<InterfaceDeclarationSyntax>().First();
            var typedInterface = Assert.IsType<InterfaceDeclarationSyntax>(interfaceDeclaration.ToGreen().CreateTypedRed(tree));
            Assert.Equal("IShape", typedInterface.Identifier.Text);
            Assert.Equal(1, typedInterface.Members.Length);

            var classDeclaration = tree.Root.DescendantNodes().OfType<ClassDeclarationSyntax>().First();
            var typedClass = Assert.IsType<ClassDeclarationSyntax>(classDeclaration.ToGreen().CreateTypedRed(tree));
            Assert.Equal("Box", typedClass.Identifier.Text);
            Assert.Equal(1, typedClass.BaseTypes.Length);
            Assert.Equal(2, typedClass.Members.Length);

            var cstyleFor = tree.Root.DescendantNodes().OfType<CSStyleForStatementSyntax>().First();
            var typedFor = Assert.IsType<CSStyleForStatementSyntax>(cstyleFor.ToGreen().CreateTypedRed(tree));
            Assert.NotNull(typedFor.Init);
            Assert.NotNull(typedFor.Condition);
            Assert.NotNull(typedFor.Update);
        }

        [Fact]
        public void TypedRed_FromGreen_ConstructorAndProperty()
        {
            var tree = SyntaxTree.Parse(@"public class Box
{
    private _x: i32

    public property Count: i32 { get set }

    public constructor(x: i32)
    {
        _x = x
    }
}");
            var ctor = tree.Root.DescendantNodes().OfType<ConstructorDeclarationSyntax>().First();
            var typedCtor = Assert.IsType<ConstructorDeclarationSyntax>(ctor.ToGreen().CreateTypedRed(tree));
            Assert.Equal(SyntaxKind.ConstructorKeyword, typedCtor.ConstructorKeyword!.Kind);
            Assert.Equal(1, typedCtor.Parameters.Count);

            var property = tree.Root.DescendantNodes().OfType<PropertyDeclarationSyntax>().First();
            var typedProperty = Assert.IsType<PropertyDeclarationSyntax>(property.ToGreen().CreateTypedRed(tree));
            Assert.Equal("Count", typedProperty.Identifier.Text);
            Assert.Equal("i32", typedProperty.Type.Identifier.Text);
            Assert.NotNull(typedProperty.Getter);
            Assert.NotNull(typedProperty.Setter);
        }

        [Fact]
        public void TypedRed_FromGreen_SwitchLambda()
        {
            var tree = SyntaxTree.Parse(@"function Main()
{
    switch x
    {
        case 1:
            return 1
        case 2:
            return 2
        default:
            return 0
    }
    var f = (a) => a + 1
}");
            var switchStatement = tree.Root.DescendantNodes().OfType<SwitchStatementSyntax>().First();
            var typedSwitch = Assert.IsType<SwitchStatementSyntax>(switchStatement.ToGreen().CreateTypedRed(tree));
            Assert.Equal(3, typedSwitch.Sections.Length);

            var caseClause = tree.Root.DescendantNodes().OfType<CaseClauseSyntax>().First();
            var typedCase = Assert.IsType<CaseClauseSyntax>(caseClause.ToGreen().CreateTypedRed(tree));
            Assert.Equal(1, typedCase.Values.Count);
            Assert.Equal(SyntaxKind.CaseKeyword, typedCase.CaseKeyword.Kind);

            var lambda = tree.Root.DescendantNodes().OfType<LambdaExpressionSyntax>().First();
            var typedLambda = Assert.IsType<LambdaExpressionSyntax>(lambda.ToGreen().CreateTypedRed(tree));
            Assert.Equal(1, typedLambda.Parameters.Count);
            Assert.Equal(SyntaxKind.FatArrowToken, typedLambda.ArrowToken.Kind);
        }

        [Fact]
        public void TypedRed_FromGreen_InterpolatedAndImport()
        {
            var tree = SyntaxTree.Parse(@"import System.Runtime

function Main()
{
    var name = ""world""
    var msg = $""hello {name}!""
}");
            var import = tree.Root.DescendantNodes().OfType<ImportClauseSyntax>().First();
            var typedImport = Assert.IsType<ImportClauseSyntax>(import.ToGreen().CreateTypedRed(tree));
            Assert.Equal("System", typedImport.NameTokens[0].Text);

            var interpolated = tree.Root.DescendantNodes().OfType<InterpolatedStringExpressionSyntax>().First();
            var typedInterpolated = Assert.IsType<InterpolatedStringExpressionSyntax>(interpolated.ToGreen().CreateTypedRed(tree));
            Assert.True(typedInterpolated.Contents.Length > 0);
            Assert.Contains(typedInterpolated.Contents, c => c is InterpolationSyntax);
        }

        [Fact]
        public void TypedRed_WholeFile_Comprehensive()
        {
            var code = @"using System

public enum Color { Red, Green = 2 }

public interface IShape
{
    public function Area(): i32
}

public class Box : IShape
{
    private _x: i32

    public property Count: i32 { get set }

    public constructor(x: i32)
    {
        _x = x
    }

    public function Area(): i32
    {
        return _x
    }
}

delegate IntTransform(x: i32): i32

function Main()
{
    var sum = 0
    for var i = 0 to 10 step 2
    {
        sum = sum + i
    }
    foreach (var x in arr)
    {
        arr[0] = x
    }
    switch sum
    {
        case 0:
            return 0
        default:
            break
    }
    var s = $""value: {sum}""
    var f = (a) => a + 1
    try
    {
        throw new Exception()
    }
    finally
    {
    }
    return sum
}";
            var tree = SyntaxTree.Parse(code);
            var typed = tree.GreenRoot.CreateTypedRed(tree);

            var unit = Assert.IsType<CompilationUnitSyntax>(typed);
            Assert.True(unit.Members.Length >= 5);
            Assert.Contains(unit.Members, m => m is DelegateDeclarationSyntax);
        }

        private sealed class CollectingWalker : SyntaxWalker
        {
            public List<SyntaxNode> Nodes { get; } = new List<SyntaxNode>();

            public int Count => Nodes.Count;

            protected override void VisitCore(SyntaxNode node)
            {
                Nodes.Add(node);
                base.VisitCore(node);
            }
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
            var member = Assert.Single(((CompilationUnitSyntax)root).Members);
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
