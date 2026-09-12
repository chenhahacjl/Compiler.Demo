using System.Linq;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Documentation;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Documentation
{
    public class DocIdBuilderTests
    {
        private const string Source = @"namespace MyLib
{
    public class Calc
    {
        public function Add(a: i32, b: i32): i32 { return a + b }
        public function Name(): string { return """" }
        public function Sum(xs: i32[]): i32 { return 0 }
        public field Count: i32
        public property Value: i32 { get; set; }
        public constructor() { }
    }
}";

        private static NamedTypeSymbol GetCalc()
        {
            var compilation = Compilation.Create(SyntaxTree.Parse(Source));
            return compilation.GlobalScope.Classes.Single(c => c.Name == "Calc");
        }

        [Fact]
        public void Type_HasTPrefix()
        {
            Assert.Equal("T:MyLib.Calc", DocIdBuilder.GetDocId(GetCalc()));
        }

        [Fact]
        public void Method_MapsKeywordTypesToBclFullNames()
        {
            var method = GetCalc().GetDeclaredMethod("Add");
            Assert.Equal("M:MyLib.Calc.Add(System.Int32,System.Int32)", DocIdBuilder.GetDocId(method!));
        }

        [Fact]
        public void Method_NoParams_OmitsParentheses()
        {
            var method = GetCalc().GetDeclaredMethod("Name");
            Assert.Equal("M:MyLib.Calc.Name", DocIdBuilder.GetDocId(method!));
        }

        [Fact]
        public void Method_ArrayParam_UsesElementFullName()
        {
            var method = GetCalc().GetDeclaredMethod("Sum");
            Assert.Equal("M:MyLib.Calc.Sum(System.Int32[])", DocIdBuilder.GetDocId(method!));
        }

        [Fact]
        public void Constructor_UsesHashCtor()
        {
            var ctor = GetCalc().Methods.Single(m => m.IsConstructor && m.Parameters.Length == 0);
            Assert.Equal("M:MyLib.Calc.#ctor", DocIdBuilder.GetDocId(ctor));
        }

        [Fact]
        public void Field_HasFPrefix()
        {
            var field = GetCalc().GetDeclaredField("Count");
            Assert.Equal("F:MyLib.Calc.Count", DocIdBuilder.GetDocId(field!));
        }

        [Fact]
        public void Property_HasPPrefix()
        {
            var property = GetCalc().GetDeclaredProperty("Value");
            Assert.Equal("P:MyLib.Calc.Value", DocIdBuilder.GetDocId(property!));
        }
    }
}
