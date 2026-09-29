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

        [Fact(Skip = "checked 溢出检查发射待补：IlOpCode 表用紧凑内部编码（Add=0x58…Not=0x66 连续，非 ECMA-335 实际字节），"
                        + "需先补入 ovf 族编码并验证编码表与 PE 写出的一致性。checked 的绑定期/树/遍历/发射上下文标记已全部就位，"
                        + "IL 端现报明确异常，不静默发出无检查算术。")]
        public void Checked_OverflowThrows()
        {
            var compilation = Compilation.Create(SyntaxTree.Parse(
                "function Main(args: string[]): i32 {" + Nl +
                "    checked {" + Nl +
                "        var a: i32 = 2147483647" + Nl +
                "        var b = a + 1" + Nl +
                "        return b" + Nl +
                "    }" + Nl +
                "}"));

            var path = Path.Combine(Path.GetTempPath(), "cocoa-smallprobe", "Checked.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var emit = compilation.Emit("Main", References, path, Cocoa.Targeting.IlTarget.Parse("net9.0"), emitLibrary: true);
            Assert.Empty(string.Join("\n", emit.Where(d => d.IsError)));

            var asm = System.Reflection.Assembly.LoadFile(path);
            var threw = false;
            try
            {
                asm.EntryPoint!.Invoke(null, new object[] { new[] { "x" } });
            }
            catch (System.Reflection.TargetInvocationException e)
            {
                threw = e.InnerException is OverflowException;
            }

            Assert.True(threw, "checked 上下文的整数溢出应抛 OverflowException");
        }

        // ------------------------------------------------------------------
        // typeof / sizeof
        // ------------------------------------------------------------------

        [Fact(Skip = "typeof 未实现：无 TypeofKeyword token，被当普通函数名解析（报 Function typeof doesn't exist）。需新增 token + 语法 + ldtoken/Type.GetTypeFromHandle 发射 + 三后端。")]
        public void Typeof_ReturnsTypeName()
        {
            var result = RunMain(
                "class V { public field X: i32 }" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var t = typeof(V)" + Nl +
                "    if t.Name == \"V\" { return 1 }" + Nl +
                "    return 0" + Nl +
                "}", "TypeOf");

            Assert.Equal(1, result);
        }

        [Fact(Skip = "sizeof 未实现：无 SizeofKeyword token，同 typeof。")]
        public void Sizeof_Primitive()
        {
            var result = RunMain(
                "function Main(args: string[]): i32 {" + Nl +
                "    return sizeof(i32)" + Nl +
                "}", "SizeOf");

            Assert.Equal(4, result);
        }
    }
}
