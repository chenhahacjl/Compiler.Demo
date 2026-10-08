using System.Linq;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeGen.Native;
using Cocoa.CodeGen.Native.Metadata;
using Cocoa.CodeGen.PE;
using Cocoa.Targeting;
using Xunit;

namespace Cocoa.Tests.CodeGen.Native.Metadata
{
    /// <summary>
    /// M3：CocoaMetadataBuilder → Reader round-trip——快照符号 → 四表 + 双堆 + Docs → 读回对象模型。
    /// 覆盖 类型/方法/字段/属性/文档 五类。
    /// </summary>
    public class CocoaMetadataRoundTripTests
    {
        private static readonly TargetPlatform Platform = new(TargetOS.Windows, Architecture.X64);

        private static CocoaMetadataModel BuildAndRead(string source)
        {
            var syntaxTree = SyntaxTree.Parse(source);
            var compilation = Compilation.Create(syntaxTree);
            var snapshot = NativeBackend.GenerateWithSnapshot(compilation, Platform);
            var root = new CocoaMetadataBuilder(snapshot.Symbols).Build();
            return new CocoaMetadataReader(root).Read();
        }

        [Fact]
        public void RoundTrip_Types()
        {
            var model = BuildAndRead(@"using System

public class Point
{
    public function Distance(x: i32): i32
    {
        return x * 2
    }
}

function Main(): i32
{
    var p = new Point()
    var d = p.Distance(3)
    return d - 6
}");

            var point = model.Types.Single(t => t.FullName == "Point");
            Assert.Equal((byte)TypeKind.Class, point.TypeKind);
            // Point : Object（固定根不入存活类集合，但类型行 BaseFullName 指向其基类）
            Assert.Equal("System.Object", point.BaseFullName);
            // 方法区段 = Distance 构造等
            Assert.True(point.MethodCount >= 1);
            Assert.Contains(model.Methods.Where(m => m.FullName == "Point.Distance"), m => m.OwnerType == "Point");
        }

        [Fact]
        public void RoundTrip_MethodsAndFields()
        {
            var model = BuildAndRead(@"using System

public class Point
{
    field x: i32
    public function GetX(): i32
    {
        return this.x
    }
}

function Main(): i32
{
    var p = new Point()
    var v = p.GetX()
    return v
}");

            var getX = model.Methods.Single(m => m.FullName == "Point.GetX");
            Assert.Equal("Point", getX.OwnerType);
            Assert.Equal("int", getX.ReturnType);
            Assert.True(getX.Line > 0);
            Assert.False(getX.IsStatic);

            var field = model.Fields.Single(f => f.Name == "x");
            Assert.Equal("int", field.Type);
            Assert.Equal("Point", field.OwnerType);
        }

        [Fact]
        public void RoundTrip_Attrs()
        {
            var model = BuildAndRead(@"using System

class ObsoleteAttribute extends Attribute
{
    public constructor(message: string) { }
}

[Obsolete(""已弃用"")]
public class Legacy
{
    public function Run(): i32
    {
        return 0
    }
}

function Main(): i32
{
    var l = new Legacy()
    return l.Run()
}");

            Assert.NotEmpty(model.Attrs);
            var attr = model.Attrs.Single();
            Assert.Equal("ObsoleteAttribute", attr.TypeFullName);
            Assert.Equal(1, attr.ArgCount);
            Assert.NotNull(attr.Blob);
        }

        [Fact]
        public void RoundTrip_MagicHeader()
        {
            var model = BuildAndRead(@"
function Main(): i32
{
    return 0
}");

            // 无存活类 → 类型表为空（合理）；Main 顶层函数入方法表
            Assert.Empty(model.Types);
            Assert.NotEmpty(model.Methods);
            Assert.Equal("Main", model.Methods.Single(m => m.OwnerType == null).FullName);
        }

        [Fact]
        public void RoundTrip_Docs()
        {
            var model = BuildAndRead(@"using System

public class Point
{
}

function Main(): i32
{
    var p = new Point()
    return 0
}");

            // Docs 含每符号「签名文档串」（DocID → 原文）
            Assert.Contains(model.Docs, d => d.DocId == "Point!Doc" && d.Text == "Point");
        }
    }
}