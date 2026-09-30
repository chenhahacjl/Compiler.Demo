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
    /// 模式匹配语义探针：5 个 pattern kind（constant / declaration / relational / logical / property）
    /// + `when` 子句，逐形态跑「绑定 → IL 发射 → CLR 执行」，用实际结果判定实现状态而非文档标记。
    ///
    /// 实测基线（2026-09-29）：常量/关系/逻辑/属性 4 类已跑通；声明模式与 when 子句待补（见各自 Skip 原因）。
    /// </summary>
    public class PatternMatchProbe
    {
        private readonly ITestOutputHelper _out;
        public PatternMatchProbe(ITestOutputHelper o) { _out = o; }

        private static string Nl => Environment.NewLine;

        private static readonly string[] References =
        {
            typeof(object).Assembly.Location,
            typeof(System.Console).Assembly.Location,
        };

        private static string[] Compile(string source, string tag)
        {
            var compilation = Compilation.Create(SyntaxTree.Parse(source));
            return compilation.GetDiagnostics().Where(d => d.IsError).Select(d => d.Message).ToArray();
        }

        private static int RunMain(string source, string tag)
        {
            var compilation = Compilation.Create(SyntaxTree.Parse(source));
            var path = Path.Combine(Path.GetTempPath(), "cocoa-patternprobe", tag + ".dll");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var diagnostics = compilation.Emit("Main", References, path, Cocoa.Targeting.IlTarget.Parse("net9.0"), emitLibrary: true);
            Assert.Empty(string.Join("\n", diagnostics.Where(d => d.IsError)));

            var asm = System.Reflection.Assembly.LoadFile(path);
            return (int)asm.EntryPoint!.Invoke(null, new object[] { new[] { "x" } })!;
        }

        // ------------------------------------------------------------------
        // 常量模式
        // ------------------------------------------------------------------

        [Fact]
        public void ConstantPattern_Null_Matches()
        {
            var result = RunMain(
                "class V { public field X: i32 }" + Nl +
                "function IsNull(v: V): bool { return v is null }" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var a = new V()" + Nl +
                "    if IsNull(a) { return 1 }" + Nl +
                "    return 0" + Nl +
                "}", "ConstNull");

            Assert.Equal(0, result);
        }

        [Fact]
        public void ConstantPattern_LiteralValue_Matches()
        {
            var result = RunMain(
                "function IsFive(x: i32): bool { return x is 5 }" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    if IsFive(5) { return 1 }" + Nl +
                "    return 0" + Nl +
                "}", "ConstLiteral");

            Assert.Equal(1, result);
        }

        // ------------------------------------------------------------------
        // 声明模式（类型模式 + 变量绑定）
        // ------------------------------------------------------------------

        [Fact]
        public void DeclarationPattern_TypeTestAndBinding()
        {
            var result = RunMain(
                "class A { public field X: i32 }" + Nl +
                "class B { public field Y: i32 }" + Nl +
                "function Describe(o: any): i32 {" + Nl +
                "    if o is A a { return a.X }" + Nl +
                "    if o is B b { return b.Y }" + Nl +
                "    return -1" + Nl +
                "}" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var b = new B()" + Nl +
                "    b.Y = 42" + Nl +
                "    return Describe(b)" + Nl +
                "}", "DeclPattern");

            Assert.Equal(42, result);
        }

        // ------------------------------------------------------------------
        // 关系模式
        // ------------------------------------------------------------------

        [Fact]
        public void RelationalPattern_GreaterThan()
        {
            var result = RunMain(
                "function Big(x: i32): bool { return x is > 10 }" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    if Big(20) { return 1 }" + Nl +
                "    if Big(5) { return 2 }" + Nl +
                "    return 0" + Nl +
                "}", "RelPattern");

            Assert.Equal(1, result);
        }

        [Fact]
        public void RelationalPattern_Range()
        {
            var result = RunMain(
                "function InRange(x: i32): bool { return x is >= 1 and <= 10 }" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    if InRange(5) { return 1 }" + Nl +
                "    return 0" + Nl +
                "}", "RelRange");

            Assert.Equal(1, result);
        }

        // ------------------------------------------------------------------
        // 逻辑模式（and / or / not）
        // ------------------------------------------------------------------

        [Fact]
        public void LogicalPattern_AndOrNot()
        {
            var result = RunMain(
                "function F(x: i32): bool { return x is > 0 and not 7 }" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    if F(3) { return 1 }" + Nl +
                "    return 0" + Nl +
                "}", "LogicalPattern");

            Assert.Equal(1, result);
        }

        // ------------------------------------------------------------------
        // 属性模式
        // ------------------------------------------------------------------

        [Fact]
        public void PropertyPattern_NestedAccess()
        {
            var result = RunMain(
                "class P { public field X: i32 }" + Nl +
                "function HasBigX(p: P): bool { return p is { X: > 5 } }" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var p = new P()" + Nl +
                "    p.X = 9" + Nl +
                "    if HasBigX(p) { return 1 }" + Nl +
                "    return 0" + Nl +
                "}", "PropPattern");

            Assert.Equal(1, result);
        }

        // ------------------------------------------------------------------
        // when 子句
        // ------------------------------------------------------------------

        [Fact]
        public void DeclarationPattern_ValueTypeTarget()
        {
            // 值类型目标：无 null 哨兵，匹配恒成立（unbox.any 完成转换）
            var result = RunMain(
                "class Box { public field V: i32 }" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var o: any = 42" + Nl +
                "    if o is i32 n { return n }" + Nl +
                "    return -1" + Nl +
                "}", "DeclValueType");

            Assert.Equal(42, result);
        }

        [Fact]
        public void DeclarationPattern_ValueTypeTarget_NonMatchTakesNoMatch()
        {
            // 反向路径：isinst 返回 null 时必须走 brfalse 那条支路（跳过 i64 那个分支），
            // 且**不能**执行 unbox.any（否则会抛 NullReferenceException）。
            // 只测匹配成功那一条是测不出这个分支的。
            var result = RunMain(
                "class Box { public field V: i32 }" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var o: any = 7" + Nl +
                "    if o is bool b { return 1 }" + Nl +
                "    if o is i32 n { return n }" + Nl +
                "    return -1" + Nl +
                "}", "DeclValueTypeNoMatch");

            Assert.Equal(7, result);
        }

        [Fact]
        public void DeclarationPattern_ValueTypeTarget_ReferenceIsNoMatch()
        {
            // 装箱里的引用类型不匹配值类型：isinst 应返回 null 并走 no-match 支路。
            var result = RunMain(
                "class Box { public field V: i32 }" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var o: any = new Box()" + Nl +
                "    if o is i32 n { return 1 }" + Nl +
                "    return -1" + Nl +
                "}", "DeclValueTypeRefNoMatch");

            Assert.Equal(-1, result);
        }

        [Fact]
        public void WhenClause_WithRelationalPattern()
        {
            // when 可叠在关系模式上：x is > 0 when x < 10
            var result = RunMain(
                "function F(x: i32): bool { return x is > 0 when x < 10 }" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    if F(5) { return 1 }" + Nl +
                "    return 0" + Nl +
                "}", "WhenRelational");

            Assert.Equal(1, result);
        }

        [Fact]
        public void WhenClause_ReferencesPatternVariable()
        {
            // when 条件里可按名引用声明模式的模式变量（已先行声明）
            var result = RunMain(
                "class A { public field X: i32 }" + Nl +
                "class B { public field Y: i32 }" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var a = new A()" + Nl +
                "    a.X = 7" + Nl +
                "    if a is A v when v.X > 5 { return 1 }" + Nl +
                "    return 0" + Nl +
                "}", "WhenVarRef");

            Assert.Equal(1, result);
        }

        [Fact]
        public void WhenClause_FiltersCandidate()
        {
            var result = RunMain(
                "class A { public field X: i32 }" + Nl +
                "class B { public field Y: i32 }" + Nl +
                "function Describe(o: any): i32 {" + Nl +
                "    if o is A a when a.X > 100 { return 1 }" + Nl +
                "    if o is A a { return 2 }" + Nl +
                "    if o is B b { return 3 }" + Nl +
                "    return -1" + Nl +
                "}" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var a = new A()" + Nl +
                "    a.X = 5" + Nl +
                "    return Describe(a)" + Nl +
                "}", "WhenClause");

            Assert.Equal(2, result);
        }
    }
}
