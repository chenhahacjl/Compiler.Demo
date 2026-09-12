using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Serialization;
using Cocoa.CodeAnalysis.Syntax;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Documentation
{
    public class EmitCocoaDocumentationTests
    {
        private const string Source = @"namespace MyLib
{
    /// <summary>Simple calculator.</summary>
    public class Calc
    {
        /// <summary>Adds two numbers.</summary>
        /// <param name=""a"">First.</param>
        /// <param name=""b"">Second.</param>
        /// <returns>The sum.</returns>
        public static function Add(a: i32, b: i32): i32
        {
            return a + b
        }
    }
}";

        private static string NewDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-doc-emit-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void EmitCocoa_WritesXmlSidecar_WithClassAndMethodDocs()
        {
            var dir = NewDir();
            var compilation = Compilation.Create(SyntaxTree.Parse(Source));
            var coaPath = Path.Combine(dir, "MyLib.coa");
            var docPath = Path.Combine(dir, "MyLib.coa.xml");

            var diagnostics = compilation.EmitCocoa("MyLib", coaPath, docPath);
            Assert.Empty(diagnostics);

            Assert.True(File.Exists(docPath));
            var doc = XDocument.Load(docPath);

            Assert.Contains(doc.Descendants("member"), m => (string?)m.Attribute("name") == "T:MyLib.Calc");
            var method = doc.Descendants("member")
                .Single(m => (string?)m.Attribute("name") == "M:MyLib.Calc.Add(System.Int32,System.Int32)");
            Assert.Equal("Adds two numbers.", (string?)method.Element("summary"));
            Assert.Equal("a", (string?)method.Elements("param").First().Attribute("name"));
        }

        [Fact]
        public void EmitCocoa_EmbedsDocsSegment_AndReadBackfillsSymbols()
        {
            var dir = NewDir();
            var compilation = Compilation.Create(SyntaxTree.Parse(Source));
            var coaPath = Path.Combine(dir, "MyLib.coa");

            var diagnostics = compilation.EmitCocoa("MyLib", coaPath);
            Assert.Empty(diagnostics);

            var program = CoaSerializer.Load(coaPath, ImmutableArray<CoaProgram>.Empty);

            Assert.Contains("M:MyLib.Calc.Add(System.Int32,System.Int32)", program.Docs.Keys);

            var calc = program.Classes.Single(c => c.Name == "Calc");
            Assert.Contains("Simple calculator.", calc.DocumentationText);

            var add = calc.GetDeclaredMethod("Add");
            Assert.NotNull(add);
            Assert.Contains("Adds two numbers.", add!.DocumentationText);
        }

        [Fact]
        public void EmitCocoa_WithoutDocPath_DoesNotWriteXml()
        {
            var dir = NewDir();
            var compilation = Compilation.Create(SyntaxTree.Parse(Source));
            var coaPath = Path.Combine(dir, "MyLib.coa");

            compilation.EmitCocoa("MyLib", coaPath);

            Assert.False(File.Exists(Path.Combine(dir, "MyLib.coa.xml")));
        }
    }
}
