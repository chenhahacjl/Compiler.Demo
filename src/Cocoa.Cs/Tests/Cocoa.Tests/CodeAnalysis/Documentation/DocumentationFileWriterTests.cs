using System.IO;
using System.Linq;
using System.Xml.Linq;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Documentation;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Documentation
{
    public class DocumentationFileWriterTests
    {
        private const string Source = @"namespace MyLib
{
    public class Calc
    {
        public function Add(a: i32, b: i32): i32 { return a + b }
    }
}";

        private static (NamedTypeSymbol Calc, FunctionSymbol Add) Bind()
        {
            var compilation = Compilation.Create(SyntaxTree.Parse(Source));
            var calc = compilation.GlobalScope.Classes.Single(c => c.Name == "Calc");
            return (calc, calc.GetDeclaredMethod("Add")!);
        }

        private static XDocument WriteAndLoad(params Symbol[] symbols)
        {
            var path = Path.Combine(Path.GetTempPath(), "cocoa-docwriter-" + System.Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                DocumentationFileWriter.Write(path, "MyLib", symbols);
                return XDocument.Load(path);
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        [Fact]
        public void Write_PreservesParamNameAttribute()
        {
            var (calc, add) = Bind();
            add.DocumentationText = "<summary>Adds.</summary>\n<param name=\"a\">first</param>\n<param name=\"b\">second</param>\n<returns>sum</returns>";

            var doc = WriteAndLoad(calc, add);

            var method = doc.Descendants("member")
                .Single(m => (string?)m.Attribute("name") == "M:MyLib.Calc.Add(System.Int32,System.Int32)");
            Assert.Equal("Adds.", (string?)method.Element("summary"));

            var pars = method.Elements("param").ToList();
            Assert.Equal(2, pars.Count);
            Assert.Equal("a", (string?)pars[0].Attribute("name"));
            Assert.Equal("first", pars[0].Value);
            Assert.Equal("b", (string?)pars[1].Attribute("name"));
            Assert.Equal("sum", (string?)method.Element("returns"));
        }

        [Fact]
        public void Write_EscapesXmlSpecialCharacters()
        {
            var (calc, _) = Bind();
            calc.DocumentationText = "<summary>a < b & c</summary>";

            var doc = WriteAndLoad(calc);

            var type = doc.Descendants("member")
                .Single(m => (string?)m.Attribute("name") == "T:MyLib.Calc");
            Assert.Equal("a < b & c", (string?)type.Element("summary"));
        }

        [Fact]
        public void Write_NoDocumentedSymbols_SkipsFile()
        {
            var (calc, add) = Bind();
            var path = Path.Combine(Path.GetTempPath(), "cocoa-docwriter-empty-" + System.Guid.NewGuid().ToString("N") + ".xml");
            var written = DocumentationFileWriter.Write(path, "MyLib", new Symbol[] { calc, add });

            Assert.Equal(0, written);
            Assert.False(File.Exists(path));
        }
    }
}
