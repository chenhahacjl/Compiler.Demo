using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Bound;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System;
using System.Linq;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// 6e-M32 Tier-2 attribute 缁戝畾寮曟搸锛氱渷鐣ヨВ鏋愩€佸睘鎬х被鍒ゅ畾锛堝熀閾?System.Attribute锛夈€?    /// 瀹炲弬 .ctor 鏍￠獙銆佹涔?鏈煡鍚?鍙傛暟涓嶅尮閰嶈瘖鏂€佸叏鐩爣鎸傝浇锛堝嚱鏁?绫?鏋氫妇/瀛楁/灞炴€э級銆?    /// </summary>
    public class AttributeBindingTests
    {
private static (BoundGlobalScope Scope, string[] Errors) Bind(string source)
        {
            var compilation = Compilation.Create(SyntaxTree.Parse(source));
            var scope = compilation.GlobalScope;
            var errors = scope.Diagnostics.Where(d => d.IsError).Select(d => d.Message).ToArray();
            return (scope, errors);
        }

        private const string AttrDef = "using System\n\nclass TestAttribute extends Attribute\n{\n}\n";

        [Fact]
        public void Function_Elision_Resolves_TestAttribute()
        {
            var (scope, errors) = Bind(AttrDef + "\n[Test] function F(): void\n{\n}\n");
            Assert.Empty(errors);
            var f = scope.Functions.Single(x => x.Name == "F");
            var attr = Assert.Single(f.Attributes);
            Assert.Equal("TestAttribute", attr.Type.Name);
        }

        [Fact]
        public void Function_FullName_Resolves()
        {
            var (scope, errors) = Bind(AttrDef + "\n[TestAttribute] function F(): void\n{\n}\n");
            Assert.Empty(errors);
            Assert.Equal("TestAttribute", scope.Functions.Single(x => x.Name == "F").Attributes.Single().Type.Name);
        }

        [Fact]
        public void UnknownAttribute_Diagnostics()
        {
            var (_, errors) = Bind("\n[NoSuch] function F(): void\n{\n}\n");
            Assert.Contains(errors, e => e.Contains("NoSuch"));
        }

        [Fact]
        public void Ambiguous_Test_And_TestAttribute_Diagnostics()
        {
            var src = "using System\n\nclass Test extends Attribute\n{\n}\nclass TestAttribute extends Attribute\n{\n}\n\n[Test] function F(): void\n{\n}\n";
var (_, errors) = Bind(src);
            // 歧义诊断：恰一条，且同时提及 Test 与 TestAttribute（非"不存在"单名诊断）
            Assert.True(errors.Length == 1 && errors[0].Contains("TestAttribute") && errors[0].Contains("Test"), "DIAG-ERRORS: [" + string.Join("] [", errors) + "]");
        }

        [Fact]
        public void ArgumentTypeMismatch_Diagnostics()
        {
            var src = "using System\n\nclass AAttribute extends Attribute\n{\n    public constructor(x: i32)\n    {\n    }\n}\n\n[A(\"str\")] function F(): void\n{\n}\n";
            var (_, errors) = Bind(src);
            Assert.Contains(errors, e => e.Contains("A"));
        }

        [Fact]
        public void ValidArguments_Bound()
        {
            var src = "using System\n\nclass AAttribute extends Attribute\n{\n    public constructor(x: i32, b: bool, s: string, c: char)\n    {\n    }\n}\n\n[A(1, true, \"s\", 'c')] function F(): void\n{\n}\n";
            var (scope, errors) = Bind(src);
            Assert.Empty(errors);
            Assert.Equal(4, scope.Functions.Single(x => x.Name == "F").Attributes.Single().Arguments.Length);
        }

        [Fact]
        public void ClassLevel_Attribute_Attached()
        {
            var (scope, errors) = Bind(AttrDef + "\n[Test] class C\n{\n}\n");
            Assert.Empty(errors);
            var cls = scope.Classes.Single(x => x.Name == "C");
            Assert.Equal("TestAttribute", cls.Attributes.Single().Type.Name);
        }

        [Fact]
        public void EnumFieldPropertyLevel_Attributes_Attached()
        {
            var src = AttrDef + @"
[Test] enum E { A }
[Test] class C
{
    [Test] public field f: i32
    [Test] public property P: i32 { get }
}
function Main(): i32
{
    return 0
}
";
            var (scope, errors) = Bind(src);
            Assert.Empty(errors);
Assert.Equal("TestAttribute", scope.Enums.Single(x => x.Name == "E").Attributes.Single().Type.Name);
            var cls = scope.Classes.Single(x => x.Name == "C");
            Assert.Equal("TestAttribute", cls.Fields.First(f => f.Name == "f").Attributes.Single().Type.Name);
            Assert.Equal("TestAttribute", cls.Properties.Single().Attributes.Single().Type.Name);
        }
    }
}

