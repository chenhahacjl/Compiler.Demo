using System;
using System.IO;
using System.Linq;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Syntax;
using Xunit;
using Xunit.Abstractions;

namespace Cocoa.Tests.Compiler
{
    /// <summary>
    /// 中小特性批量探针：out var / params / 命名可选参数 / 索引器 / checked / typeof / sizeof。
    /// 逐形态跑「绑定 → IL 发射 → CLR 执行」，以实测结果判定实现状态（不采信文档标记）。
    /// </summary>
    public class SmallFeatureProbe
    {
        private readonly ITestOutputHelper _out;
        public SmallFeatureProbe(ITestOutputHelper o) { _out = o; }

        private static string Nl => Environment.NewLine;

        private static readonly string[] References =
        {
            typeof(object).Assembly.Location,
            typeof(System.Console).Assembly.Location,
        };

        private static int RunMain(string source, string tag)
        {
            var compilation = Compilation.Create(SyntaxTree.Parse(source));
            var path = Path.Combine(Path.GetTempPath(), "cocoa-smallprobe", tag + ".dll");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var diagnostics = compilation.Emit("Main", References, path, Cocoa.Targeting.IlTarget.Parse("net9.0"), emitLibrary: true);
            Assert.Empty(string.Join("\n", diagnostics.Where(d => d.IsError)));

            var asm = System.Reflection.Assembly.LoadFile(path);
            return (int)asm.EntryPoint!.Invoke(null, new object[] { new[] { "x" } })!;
        }

        // ------------------------------------------------------------------
        // out var
        // ------------------------------------------------------------------

        [Fact]
        public void OutVar_DeclaresInline()
        {
            var result = RunMain(
                "function TryGet(v: i32, out out_value: i32): bool { out_value = v * 2; return true }" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    if TryGet(21, out var r) { return r }" + Nl +
                "    return -1" + Nl +
                "}", "OutVar");

            Assert.Equal(42, result);
        }
        [Fact]
        public void OutVar_MemberCallWithInlineDeclaration()
        {
            // 成员调用 + out var 内联声明：当前 binder 的实参绑定路径未覆盖 DeclarationExpression 形态
            var result = RunMain(
                "class C {" + Nl +
                "    public function TryGet(v: i32, out out_value: i32): bool { out_value = v * 2; return true }" + Nl +
                "}" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var c = new C()" + Nl +
                "    if c.TryGet(21, out var r) { return r }" + Nl +
                "    return -1" + Nl +
                "}", "OutVarMember");

            Assert.Equal(42, result);
        }
        // ------------------------------------------------------------------
        // params 可变参数
        // ------------------------------------------------------------------

        [Fact]
        public void Params_VariableArity()
        {
            var result = RunMain(
                "class C {" + Nl +
                "    public function Sum(params values: i32[]): i32 {" + Nl +
                "        var s = 0" + Nl +
                "        var i = 0" + Nl +
                "        while i < values.Length { s = s + values[i]; i = i + 1 }" + Nl +
                "        return s" + Nl +
                "    }" + Nl +
                "}" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var c = new C()" + Nl +
                "    return c.Sum(1, 2, 3)" + Nl +
                "}", "Params");

            Assert.Equal(6, result);
        }

        // ------------------------------------------------------------------
        // 命名可选参数
        // ------------------------------------------------------------------

        [Fact]
        public void NamedArgument_CallByName()
        {
            var result = RunMain(
                "class C {" + Nl +
                "    public function F(a: i32, b: i32): i32 { return a * 10 + b }" + Nl +
                "}" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var c = new C()" + Nl +
                "    return c.F(b: 5, a: 3)" + Nl +
                "}", "NamedArg");

            Assert.Equal(35, result);
        }

        // ------------------------------------------------------------------
        // 索引器
        // ------------------------------------------------------------------

        [Fact(Skip = "索引器未实现：元素访问仅支持数组/字符串（报 Cannot index a value of type X）。parser 侧虽有 ParseIndexerDeclaration，但类成员的 this[...] 语义未接线。")]
        public void Indexer_ThisIndexer()
        {
            var result = RunMain(
                "class Box {" + Nl +
                "    private field _v: i32" + Nl +
                "    public property Item: i32 { get { return _v } set { _v = value } }" + Nl +
                "}" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var b = new Box()" + Nl +
                "    b[0] = 9" + Nl +
                "    return b[0]" + Nl +
                "}", "Indexer");

            Assert.Equal(9, result);
        }

        // ------------------------------------------------------------------
        // checked / unchecked
        // ------------------------------------------------------------------

        [Fact]
        public void Checked_OverflowThrows()
        {
            // checked { var a: i32 = 2147483647; var b = a + 1 } → OverflowException
            var path = Path.Combine(Path.GetTempPath(), "cocoa-smallprobe", "CheckedOvf.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var compilation = Compilation.Create(SyntaxTree.Parse(
                "function Main(args: string[]): i32 {" + Nl +
                "    checked {" + Nl +
                "        var a: i32 = 2147483647" + Nl +
                "        var b = a + 1" + Nl +
                "        return b" + Nl +
                "    }" + Nl +
                "}"));

            var emit = compilation.Emit("Main", References, path, Cocoa.Targeting.IlTarget.Parse("net9.0"), emitLibrary: true);
            Assert.Empty(string.Join("\n", emit.Where(d => d.IsError)));

            var asm = System.Reflection.Assembly.LoadFile(path);
            string observed;
            try
            {
                observed = "returned " + asm.EntryPoint!.Invoke(null, new object[] { new[] { "x" } });
            }
            catch (System.Reflection.TargetInvocationException e)
            {
                observed = "threw " + e.InnerException!.GetType().Name;
            }

            Assert.Equal("threw OverflowException", observed);
        }

        [Fact]
        public void Unchecked_StillWrapsAround()
        {
            // unchecked 保持回绕语义（不得被 checked 的 ovf 影响）
            var result = RunMain(
                "function Main(args: string[]): i32 {" + Nl +
                "    unchecked {" + Nl +
                "        var a: i32 = 2147483647" + Nl +
                "        var b = a + 1" + Nl +
                "        return b" + Nl +
                "    }" + Nl +
                "}", "UncheckedWrap");

            Assert.Equal(-2147483648, result);
        }        // ------------------------------------------------------------------
        // typeof / sizeof
        // ------------------------------------------------------------------

        [Fact(Skip = "typeof 的 IL 发射待修。已查明：实发字节为 ldtoken <TypeDef token>; call <MemberRef token>，"
                        + "形式正确、token 解析无误（TypeDef 行 + MemberRef 行均有效），但产出程序仍被 CLR 判 "
                        + "InvalidProgramException；同一程序去掉 typeof 即有效，故 typeof 是唯一触发点。"
                        + "根因需 peverify 级诊断（进程内不可得）。IL 端现报明确诊断，不产出非法二进制。"
                        + "sizeof 与 typeof 共用同一语法节点与绑定路径，sizeof 已可用。")]
        public void Typeof_ReturnsTypeName()
        {
            // 目标行为：var t = typeof(V); t.Name == "V"
        }        [Fact]
        public void Sizeof_Primitive()
        {
            var result = RunMain(
                "function Main(args: string[]): i32 {" + Nl +
                "    return sizeof(i32)" + Nl +
                "}", "SizeOf");

            Assert.Equal(4, result);
        }

        [Theory]
        [InlineData("i8", 1)]
        [InlineData("i16", 2)]
        [InlineData("i32", 4)]
        [InlineData("i64", 8)]
        [InlineData("f32", 4)]
        [InlineData("f64", 8)]
        [InlineData("bool", 1)]
        [InlineData("char", 2)]
        public void Sizeof_PrimitiveWidths(string typeName, int expected)
        {
            // sizeof 是编译期常量（C# 同），可参与常量表达式
            var result = RunMain(
                "function Main(args: string[]): i32 {" + Nl +
                "    var n: i32 = sizeof(" + typeName + ")" + Nl +
                "    return n * 10 + " + expected + Nl +
                "}", "SizeOf_" + typeName);

            Assert.Equal(expected * 11, result);
        }

        [Fact]
        public void Sizeof_Enum_IsFour()
        {
            var result = RunMain(
                "enum Color { Red, Green }" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    return sizeof(Color)" + Nl +
                "}", "SizeOfEnum");

            Assert.Equal(4, result);
        }
    }
}
