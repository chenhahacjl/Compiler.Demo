using Cocoa.CodeAnalysis.Syntax;
using System.Linq;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// 6e-M32 Tier-2 attribute 璇硶锛氬叏鎴愬憳浣嶆寕杞斤紙绫?鍑芥暟/瀛楁/灞炴€?鏋氫妇锛? 瀛楅潰閲忓疄鍙傘€?    /// P1锛氳В鏋愬眰楠岃瘉銆?    /// </summary>
    public class AttributeSyntaxTests
    {
        [Fact]
        public void Parse_ClassAttribute_Attached()
        {
            var tree = SyntaxTree.Parse("[Facade(\"System.IntPtr\")] public class NativeInt32 { }");
            Assert.True(!tree.Diagnostics.Any(d => d.IsError), string.Join("\n", tree.Diagnostics.Select(d => d.Message)));
            var cls = tree.GetRootMembers().OfType<ClassDeclarationSyntax>().Single();
            Assert.Single(cls.Attributes);
            Assert.Equal("Facade", cls.Attributes[0].Name.Text);
        }

        [Fact]
        public void Parse_FunctionAttribute_Attached()
        {
            var tree = SyntaxTree.Parse("[Test] public function F(): void { }");
            Assert.True(!tree.Diagnostics.Any(d => d.IsError), string.Join("\n", tree.Diagnostics.Select(d => d.Message)));
            var fn = tree.GetRootMembers().OfType<FunctionDeclarationSyntax>().Single();
            Assert.Single(fn.Attributes);
            Assert.Equal("Test", fn.Attributes[0].Name.Text);
        }

        [Fact]
        public void Parse_EnumAttribute_Attached()
        {
            var tree = SyntaxTree.Parse("[Flags] public enum E { A, B }");
            Assert.True(!tree.Diagnostics.Any(d => d.IsError), string.Join("\n", tree.Diagnostics.Select(d => d.Message)));
            var en = tree.GetRootMembers().OfType<EnumDeclarationSyntax>().Single();
            Assert.Single(en.Attributes);
            Assert.Equal("Flags", en.Attributes[0].Name.Text);
        }

        [Fact]
        public void Parse_FieldAndPropertyAttributes_Attached()
        {
            var tree = SyntaxTree.Parse("[Marker] public class C { [A] public field x: i32\n[B] public property P: i32 { get }\n} ");
            Assert.True(!tree.Diagnostics.Any(d => d.IsError), string.Join("\n", tree.Diagnostics.Select(d => d.Message)));
            var field = (ClassFieldDeclarationSyntax)tree.GetRootMembers().OfType<ClassDeclarationSyntax>().Single().Members.Single(m => m is ClassFieldDeclarationSyntax);
            var prop = (PropertyDeclarationSyntax)tree.GetRootMembers().OfType<ClassDeclarationSyntax>().Single().Members.Single(m => m is PropertyDeclarationSyntax);
            Assert.Equal("A", field.Attributes[0].Name.Text);
            Assert.Equal("B", prop.Attributes[0].Name.Text);
        }

        [Fact]
        public void Parse_LiteralArguments_Accepted()
        {
            var tree = SyntaxTree.Parse("[Attr(\"s\", 1, true, false, 'c')] public class C { }");
            Assert.True(!tree.Diagnostics.Any(d => d.IsError), string.Join("\n", tree.Diagnostics.Select(d => d.Message)));
            var attr = tree.GetRootMembers().OfType<ClassDeclarationSyntax>().Single().Attributes.Single();
            Assert.Equal(5, attr.Arguments.Count(a => a.Kind != SyntaxKind.CommaToken));
        }
    }
}
