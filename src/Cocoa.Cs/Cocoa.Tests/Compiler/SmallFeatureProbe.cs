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

        /// <summary>解析 + 绑定的全部诊断（<c>Compilation.GetDiagnostics</c> 口径，含函数体错误）。</summary>
        private static System.Collections.Generic.IEnumerable<Diagnostic> Diagnostics(string source)
        {
            return Compilation.Create(SyntaxTree.Parse(source)).GetDiagnostics();
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

        [Fact]
        public void Indexer_ReadOnly_HasNoSetter()
        {
            // 只读索引器：写操作应报诊断（不得静默丢弃）
            var result = RunMain(
                "class Counter {" + Nl +
                "    private field _n: i32" + Nl +
                "    public property this[i: i32]: i32 { get { return _n } }" + Nl +
                "}" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var c = new Counter()" + Nl +
                "    return c[0]" + Nl +
                "}", "IndexerRO");

            Assert.Equal(0, result);
        }

        [Fact]
        public void Indexer_StrIndex_ConvertsToParameterType()
        {
            // 索引参数走形参类型转换：传 i32 字面量到 i64 形参
            var result = RunMain(
                "class M {" + Nl +
                "    public property this[i: i64]: i32 { get { return 7 } set { } }" + Nl +
                "}" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var m = new M()" + Nl +
                "    return m[3]" + Nl +
                "}", "IndexerWide");

            Assert.Equal(7, result);
        }

        [Fact]
        public void Indexer_Assignment_WithoutSetter_ReportsError()
        {
            var diagnostics = Diagnostics(
                "class Counter {" + Nl +
                "    private field _n: i32" + Nl +
                "    public property this[i: i32]: i32 { get { return _n } }" + Nl +
                "}" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var c = new Counter()" + Nl +
                "    c[0] = 5" + Nl +
                "    return 0" + Nl +
                "}");

            Assert.Contains(diagnostics, d => d.IsError);
        }

        // ------------------------------------------------------------------
        // 元组 / 解构
        // ------------------------------------------------------------------

        [Fact]
        public void Tuple_LiteralAndAccess()
        {
            var result = RunMain(
                "function Main(args: string[]): i32 {" + Nl +
                "    var t = (1, 2)" + Nl +
                "    return t.Item1 * 10 + t.Item2" + Nl +
                "}", "TupleLiteral");

            Assert.Equal(12, result);
        }

        [Fact]
        public void Tuple_DeconstructionInAssignment()
        {
            var result = RunMain(
                "function Main(args: string[]): i32 {" + Nl +
                "    var t = (3, 4)" + Nl +
                "    var a = 0" + Nl +
                "    var b = 0" + Nl +
                "    (a, b) = t" + Nl +
                "    return a * 10 + b" + Nl +
                "}", "TupleDeconstruct");

            Assert.Equal(34, result);
        }

        [Fact]
        public void Tuple_MemberAccessInsideForLoop()
        {
            var result = RunMain(
                "function Main(args: string[]): i32 {" + Nl +
                "    var sum = 0" + Nl +
                "    var t = (1, 2)" + Nl +
                "    for (var i = 0; i < 3; i = i + 1) { sum = sum + t.Item1 + t.Item2 }" + Nl +
                "    return sum" + Nl +
                "}", "TupleForLoop");

            Assert.Equal(9, result);
        }

        [Fact]
        public void Tuple_PassedAsArgument()
        {
            var result = RunMain(
                "function Add(t: any): i32 { return 1 }" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    return Add((5, 6))" + Nl +
                "}", "TupleArg");

            Assert.Equal(1, result);
        }

        [Fact]
        public void Indexer_ThisIndexer_ReadAndWrite()
        {
            // 本语言索引器语法是 property this[i: i32]: T（不是 C# 式的 property Item）
            var result = RunMain(
                "class Box {" + Nl +
                "    private field _v: i32" + Nl +
                "    public property this[i: i32]: i32 {" + Nl +
                "        get { return _v }" + Nl +
                "        set { _v = value }" + Nl +
                "    }" + Nl +
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

        [Fact]
        public void Typeof_ReturnsTypeName()
        {
            // typeof(V) → ldtoken V; call Type::GetTypeFromHandle；结果可与字符串比较
            var result = RunMain(
                "class V { public field X: i32 }" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var t = typeof(V)" + Nl +
                "    if t.Name == \"V\" { return 1 }" + Nl +
                "    return 0" + Nl +
                "}", "TypeOf");

            Assert.Equal(1, result);
        }        [Fact]
        public void Typeof_FullName()
        {
            // 命名空间类型取 FullName
            var result = RunMain(
                "namespace Ns { class Deep { } }" + Nl +
                "using Ns" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var d = typeof(Deep)" + Nl +
                "    if d.FullName == \"Ns.Deep\" { return 1 }" + Nl +
                "    return 0" + Nl +
                "}", "TypeOfFull");

            Assert.Equal(1, result);
        }

        [Fact]
        public void Typeof_Primitive_EmitsAndRuns()
        {
            // typeof(基元) 可发射并执行；但其 Name/FullName 与 CLR Type 的对应尚未核对
            // （IsInstTypeToken 借框架 TypeRef 取得 System.Int32，Name getter 是
            //  FullName 去命名空间前缀，理论应为 "Int32"，此处只锁定「不抛且可执行」）
            var result = RunMain(
                "function Main(args: string[]): i32 {" + Nl +
                "    var i = typeof(i32)" + Nl +
                "    var j = i" + Nl +
                "    return 2" + Nl +
                "}", "TypeOfPrim");

            Assert.Equal(2, result);
        }

        [Fact]
        public void Typeof_EqualsComparison()
        {
            // 同一类型的两次 typeof 结果是同一对象（引用相等）
            var result = RunMain(
                "class V { }" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var a = typeof(V)" + Nl +
                "    var b = typeof(V)" + Nl +
                "    if a == b { return 1 }" + Nl +
                "    return 0" + Nl +
                "}", "TypeOfEq");

            Assert.Equal(1, result);
        }

        [Fact]
        public void Typeof_MatchesGetType()
        {
            // typeof(T) 与实例上的 GetType() 结果同一类型
            var result = RunMain(
                "class V { }" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var v = new V()" + Nl +
                "    var a = typeof(V)" + Nl +
                "    var b = v.GetType()" + Nl +
                "    if a == b { return 1 }" + Nl +
                "    return 0" + Nl +
                "}", "TypeOfVsGetType");

            Assert.Equal(1, result);
        }

        [Fact]
        public void Sizeof_UserStruct_GoesThroughEmitter()
        {
            // 基元/枚举的 sizeof 在**绑定期**折叠为常量，走不到发射器；
            // 用户结构体才会真的发 sizeof 指令——该路径曾因 opcode 声明成 0x1C（应为 0xFE1C）而不可用
            var result = RunMain(
                "struct P { public field X: i32; public field Y: i32 }" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    if sizeof(P) == 8 { return 1 }" + Nl +
                "    return 0" + Nl +
                "}", "SizeofStruct");

            Assert.Equal(1, result);
        }

        [Fact]
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

        /// <summary>
        /// 引用相等 `===` / `!==` —— **未实现**（审计中发现的缺口，不在原「未完成盘点」清单里）。
        ///
        /// 审计方式：把 <c>BoundBinaryOperatorKind</c> 的 21 个种类逐个对照
        /// 「IL 发射器」与「求值器」两处的分支，结果：
        ///   - 发射器缺：NullCoalescing
        ///   - 求值器缺：NullCoalescing、ReferenceEquals、ReferenceNotEquals
        /// 再实测定性：
        ///   - <c>===</c> **连词法都没有** —— 实测报
        ///     <c>Unexpected token &lt;EqualsToken&gt;, expected &lt;IdentifierToken&gt;</c>，
        ///     所以它比「发射器漏了分支」更早失败。
        ///   - <c>??</c> 能解析（走的是不经 <c>NullCoalescing</c> 的路径），
        ///     但结果类型不参与统一：`var v = a ?? b` 得到 <c>any</c>，
        ///     再 <c>return v</c> 到 <c>i32</c> 报 <c>Cannot convert type 'any' to 'int'</c>。
        ///     **注意这一条不是 bug**——C# 里 <c>object a; object b;</c> 的
        ///     <c>a ?? b</c> 结果同样是 <c>object</c>，返回到 <c>int</c> 一样报错。
        ///
        /// 实现 `===` 需要动：词法 → SyntaxKind → parser 的二元运算符 switch →
        /// 运算符 kind 映射 → 发射器（<c>ceq</c> + 非 <c>ceq</c>）→ 求值器，
        /// 共 6 处，属于「漏一处就废」的多点改动，尚未实施。
        /// </summary>
        [Fact(Skip = "引用相等 === 未实现：实测连词法都没有（Unexpected token <EqualsToken>），"
                        + "需 6 处改动（词法/SyntaxKind/parser switch/运算符映射/发射器/求值器），尚未实施。")]
        public void Operator_Audit_ReferenceEquality()
        {
            var result = RunMain(
                "class Box { public field V: i32 }" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var a = new Box()" + Nl +
                "    var b = new Box()" + Nl +
                "    var same = 0" + Nl +
                "    var diff = 0" + Nl +
                "    if a === a { same = 1 }" + Nl +
                "    if a === b { diff = 1 }" + Nl +
                "    return same * 10 + diff" + Nl +
                "}", "RefEquality");

            Assert.Equal(10, result);
        }

        [Fact(Skip = "实测缺口：`??=` 报 Binary operator '??=' is not defined for types 'any'。需算子注册 + 发射两处")]
        public void Probe_CoalesceAssignment()
        {
            var r = RunMain(
                "function Main(args: string[]): i32 {" + Nl +
                "    var a: any = null" + Nl +
                "    var b = 0" + Nl +
                "    a ??= 7" + Nl +
                "    if a != null { b = 1 }" + Nl +
                "    return b" + Nl +
                "}", "CoalesceAssign");

            Assert.Equal(1, r);
        }

        [Fact]
        public void Probe_NullConditional()
        {
            // 接收者用**具体可空类型**而不是 any：`any` 上的成员访问会先撞
            // 「成员 V 是 private 的」这类访问性检查，测的就不是 ?. 本身了。
            // 只测非 null 路径：null 路径需要把结果当可空类型比较，而 ?. 的**结果类型
            // 推断**目前直接取成员类型（这里是 i32），`v == null` 因此不合法——
            // 那是另一个缺口（?. 结果应为可空/引用类型），不在本用例范围。
            var r = RunMain(
                "class Box { public field V: i32 }" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var b = new Box()" + Nl +
                "    b.V = 5" + Nl +
                "    var v = b?.V" + Nl +
                "    return v" + Nl +
                "}", "NullConditional");

            Assert.Equal(5, r);
        }

        [Fact(Skip = "实测缺口：`v == 9`（v: any）报 '==' is not defined for types 'any' and 'int'。属 any 与具体类型比较的转换推导缺口，非单点可修")]
        public void Probe_CoalesceRight()
        {
            var r = RunMain(
                "function Main(args: string[]): i32 {" + Nl +
                "    var a: any = null" + Nl +
                "    var b: any = 9" + Nl +
                "    var v = a ?? b" + Nl +
                "    if v == 9 { return 1 }" + Nl +
                "    return 0" + Nl +
                "}", "CoalesceRight");

            Assert.Equal(1, r);
        }
    }
}
