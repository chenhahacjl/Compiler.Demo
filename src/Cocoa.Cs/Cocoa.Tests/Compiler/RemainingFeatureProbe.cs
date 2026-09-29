using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Syntax;
using Xunit;
using Xunit.Abstractions;

namespace Cocoa.Tests.Compiler
{
    /// <summary>
    /// 剩余特性族批量探针（对照表 §7 未完成项）。
    /// 逐形态跑「解析 → 绑定 → IL 发射 → CLR 执行」，以**实测**判定状态。
    ///
    /// 纪律：本文件存在的意义就是纠正「凭 token/kind 名称推断功能是否存在」的误判——
    /// 索引器、元组、sizeof 三项都曾因形态不同而被误判为未实现。故每例都跑到执行。
    /// </summary>
    public class RemainingFeatureProbe
    {
        private readonly ITestOutputHelper _out;
        public RemainingFeatureProbe(ITestOutputHelper o) { _out = o; }

        private static string Nl => Environment.NewLine;

        private static readonly string[] References =
        {
            typeof(object).Assembly.Location,
            typeof(System.Console).Assembly.Location,
        };

        private static int RunMain(string source, string tag)
        {
            var compilation = Compilation.Create(SyntaxTree.Parse(source));
            var path = Path.Combine(Path.GetTempPath(), "cocoa-remprobe", tag + ".dll");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var diagnostics = compilation.Emit("Main", References, path, Cocoa.Targeting.IlTarget.Parse("net9.0"), emitLibrary: true);
            Assert.Empty(string.Join("\n", diagnostics.Where(d => d.IsError)));

            var asm = System.Reflection.Assembly.LoadFile(path);
            return (int)asm.EntryPoint!.Invoke(null, new object[] { new[] { "x" } })!;
        }

        private static IEnumerable<Diagnostic> Diagnostics(string source)
        {
            // 须传 references：属性类解析（ResolveAttributeClass → LookupType）依赖全局作用域，
            // 缺 BCL 引用时连 System.ObsoleteAttribute 都查不到
            return Compilation.Create(References, SyntaxTree.Parse(source)).GetDiagnostics();
        }

        // ------------------------------------------------------------------
        // record
        // ------------------------------------------------------------------

        [Fact]
        public void Record_Positional_GeneratesFieldsAndCtor()
        {
            // record 只支持位置参数形式；解析期展开为「字段 + 带参构造 + ==/!=」
            var result = RunMain(
                "record Person(name: string, age: i32)" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var p = new Person(\"a\", 30)" + Nl +
                "    return p.age" + Nl +
                "}", "Record");

            Assert.Equal(30, result);
        }
        [Fact]
        public void Record_EqualityOperator_IsGenerated()
        {
            // 解析期为 record 生成 == / !=（值相等）
            var result = RunMain(
                "record P(x: i32)" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var a = new P(5)" + Nl +
                "    var b = new P(5)" + Nl +
                "    if a == b { return 1 }" + Nl +
                "    var c = new P(6)" + Nl +
                "    if a == c { return 2 }" + Nl +
                "    return 0" + Nl +
                "}", "RecordEq");

            Assert.Equal(1, result);
        }

        // ------------------------------------------------------------------
        // struct 值语义
        // ------------------------------------------------------------------

        [Fact]
        public void Struct_ValueSemantics_CopyOnAssign()
        {
            var result = RunMain(
                "struct Point { public field X: i32 }" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var a = new Point()" + Nl +
                "    a.X = 1" + Nl +
                "    var b = a" + Nl +
                "    b.X = 9" + Nl +
                "    return a.X" + Nl +
                "}", "StructValue");

            // 值语义：b.X = 9 不应影响 a.X（a.X 仍为 1）
            Assert.Equal(1, result);
        }

        // ------------------------------------------------------------------
        // partial
        // ------------------------------------------------------------------

        [Fact]
        public void Partial_MergesTwoDeclarations()
        {
            var result = RunMain(
                "partial class C { public function A(): i32 { return 1 } }" + Nl +
                "partial class C { public function B(): i32 { return 2 } }" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var c = new C()" + Nl +
                "    return c.A() * 10 + c.B()" + Nl +
                "}", "PartialMerge");

            Assert.Equal(12, result);
        }

        // ------------------------------------------------------------------
        // 嵌套类
        // ------------------------------------------------------------------

        [Fact(Skip = "嵌套类未实现：类体内写 `class Inner { … }` 报 Unexpected token <ClassKeyword>, "
                        + "expected <IdentifierToken>。C# 式限定名 new Outer.Inner() 需等嵌套类型落地后才可用。")]
        public void NestedClass_DeclaredInsideClass()
        {
        }
        // ------------------------------------------------------------------
        // foreach 解构
        // ------------------------------------------------------------------

        [Fact(Skip = "foreach 元组解构未实现：`foreach (var a, var b) in t` 报 Unexpected token <CommaToken>, "
                        + "expected <InKeyword>——循环变量位置只接受单个 `var 名字`，"
                        + "不接受多变量解构形式。")]
        public void Foreach_DeconstructsTuple()
        {
        }
        // ------------------------------------------------------------------
        // nint / nuint
        // ------------------------------------------------------------------

        [Fact]
        public void NativeInt_EqualityOnly()
        {
            // nint/nuint 目前只注册相等/不等（句柄判定语义），算术未注册
            var result = RunMain(
                "function Main(args: string[]): i32 {" + Nl +
                "    var a: nint = 0" + Nl +
                "    if a == 0 { return 1 }" + Nl +
                "    return 0" + Nl +
                "}", "NativeInt");

            Assert.Equal(1, result);
        }
        // ------------------------------------------------------------------
        // Attribute 语义消费
        // ------------------------------------------------------------------
        // Attribute 语义消费
        //
        // 注：本语言的属性类由**用户声明**（`class XAttribute extends Attribute`），
        // 属性类解析走全局作用域 LookupType，不查 BCL 引用——与既有 CustomAttributeEmitTests 同模式。
        // System.ObsoleteAttribute 的规范定义见 src/Cocoa.SDK/System.Core/ObsoleteAttribute.co。
        // ------------------------------------------------------------------

        private static string ObsoleteDecl(string ctor) =>
            "class ObsoleteAttribute extends Attribute {" + Nl +
            "    public constructor(" + ctor + ") { }" + Nl +
            "}" + Nl;

        [Fact]
        public void Attribute_Obsolete_ProducesWarningAtCallSite()
        {
            var diagnostics = Diagnostics(
                ObsoleteDecl("") +
                "class C {" + Nl +
                "    [Obsolete] public function Old(): i32 { return 1 }" + Nl +
                "}" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var c = new C()" + Nl +
                "    return c.Old()" + Nl +
                "}");

            Assert.Contains(diagnostics, d => d.IsWarning && d.Message.Contains("已过时"));
        }

        [Fact]
        public void Attribute_Obsolete_CarriesMessage()
        {
            var diagnostics = Diagnostics(
                ObsoleteDecl("message: string") +
                "class C {" + Nl +
                "    [Obsolete(\"改用 New()\")] public function Old(): i32 { return 1 }" + Nl +
                "}" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var c = new C()" + Nl +
                "    return c.Old()" + Nl +
                "}");

            Assert.Contains(diagnostics, d => d.IsWarning && d.Message.Contains("改用 New()"));
        }

        [Fact]
        public void Attribute_Obsolete_WithErrorFlag_IsError()
        {
            // [Obsolete(msg, true)] → 错误而非警告（C# CS0619 语义）
            var diagnostics = Diagnostics(
                ObsoleteDecl("message: string, error: bool") +
                "class C {" + Nl +
                "    [Obsolete(\"已移除\", true)] public function Old(): i32 { return 1 }" + Nl +
                "}" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var c = new C()" + Nl +
                "    return c.Old()" + Nl +
                "}");

            Assert.Contains(diagnostics, d => d.IsError && d.Message.Contains("已移除"));
        }

        [Fact]
        public void Attribute_Obsolete_NoDiagnosticWhenNotCalled()
        {
            // 声明处不得报诊断——只在**调用点**报
            var diagnostics = Diagnostics(
                ObsoleteDecl("") +
                "class C {" + Nl +
                "    [Obsolete] public function Old(): i32 { return 1 }" + Nl +
                "}" + Nl +
                "function Main(args: string[]): i32 { return 0 }");

            Assert.DoesNotContain(diagnostics, d => d.Message.Contains("已过时"));
        }

        [Fact]
        public void Attribute_Obsolete_OnStaticMethod()
        {
            var diagnostics = Diagnostics(
                ObsoleteDecl("") +
                "class C {" + Nl +
                "    [Obsolete] public static function Old(): i32 { return 1 }" + Nl +
                "}" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    return C.Old()" + Nl +
                "}");

            Assert.Contains(diagnostics, d => d.IsWarning && d.Message.Contains("已过时"));
        }

        [Fact]
        public void Attribute_Obsolete_OnTopLevelFunction()
        {
            var diagnostics = Diagnostics(
                ObsoleteDecl("") +
                "[Obsolete] function Old(): i32 { return 1 }" + Nl +
                "function Main(args: string[]): i32 { return Old() }");

            Assert.Contains(diagnostics, d => d.IsWarning && d.Message.Contains("已过时"));
        }

        [Fact]
        public void Attribute_Obsolete_DoesNotBlockEmit()
        {
            // 警告不应阻断发射：程序仍应可编译并执行
            var result = RunMain(
                ObsoleteDecl("") +
                "class C {" + Nl +
                "    [Obsolete] public function Old(): i32 { return 1 }" + Nl +
                "}" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var c = new C()" + Nl +
                "    return c.Old()" + Nl +
                "}", "ObsoleteRun");

            Assert.Equal(1, result);
        }

        [Fact]
        public void Attribute_IsEmittedOnClassMember()
        {
            // Tier-2 声明位：类成员上的 [Test] 应写进 PE 的 CustomAttribute 表。
            // 用 PEReader 查表行数而非反射 —— 反射侧需构造器 ref 完全正确才读得到。
            var compilation = Compilation.Create(SyntaxTree.Parse(
                "class TestAttribute extends Attribute { }" + Nl +
                "class C { [Test] public function F(): i32 { return 0 } }" + Nl +
                "function Main(args: string[]): i32 { return 0 }"));

            var path = Path.Combine(Path.GetTempPath(), "cocoa-remprobe", "AttrEmit.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var diagnostics = compilation.Emit("Main", References, path, Cocoa.Targeting.IlTarget.Parse("net9.0"), emitLibrary: true);
            Assert.Empty(string.Join("\n", diagnostics.Where(d => d.IsError)));

            using var stream = File.OpenRead(path);
            using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
            var md = pe.GetMetadataReader();

            _out.WriteLine("CustomAttribute 行数: " + md.GetTableRowCount(TableIndex.CustomAttribute));
            Assert.True(md.GetTableRowCount(TableIndex.CustomAttribute) >= 1,
                "类成员上的 [Test] 未写进 CustomAttribute 表");
        }        // ------------------------------------------------------------------
        // 反射基础面
        // ------------------------------------------------------------------

        [Fact]
        public void Reflection_TypeNameAndFullName()
        {
            var result = RunMain(
                "class V { public field X: i32 }" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var v = new V()" + Nl +
                "    var t = v.GetType()" + Nl +
                "    if t.Name == \"V\" { return 1 }" + Nl +
                "    return 0" + Nl +
                "}", "ReflType");

            Assert.Equal(1, result);
        }

        [Fact]
        public void Reflection_TypeExposesNameAndFullName()
        {
            var result = RunMain(
                "namespace Ns { class Deep { public field X: i32 } }" + Nl +
                "using Ns" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var d = new Deep()" + Nl +
                "    var t = d.GetType()" + Nl +
                "    if t.FullName == \"Ns.Deep\" { return 1 }" + Nl +
                "    return 0" + Nl +
                "}", "ReflFullName");

            Assert.Equal(1, result);
        }

        [Fact]
        public void Reflection_TypeofYieldsSystemType()
        {
            // typeof 是获取 System.Type 的入口（前置能力已就位）
            var result = RunMain(
                "class V { public field X: i32 }" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var a = typeof(V)" + Nl +
                "    var b = new V().GetType()" + Nl +
                "    if a == b { return 1 }" + Nl +
                "    return 0" + Nl +
                "}", "ReflTypeof");

            Assert.Equal(1, result);
        }

        [Fact]
        public void TypeCheck_IntComparedToString_IsError()
        {
            // 回归护栏：C# 前端曾**静默接受** `int == string`，发出
            // `String.op_Equality(int32, string)` 这种签名不匹配的 IL，
            // 被 CLR 判 InvalidProgramException。阶段 8 自举靠 ilverify 暴露了它。
            // 正确行为：编译期报操作数类型不匹配。
            var diagnostics = Diagnostics(
                "function Main(args: string[]): i32 {" + Nl +
                "    var k = 0" + Nl +
                "    if k == \"TypeClause\" { return 1 }" + Nl +
                "    return 0" + Nl +
                "}");

            Assert.Contains(diagnostics, d => d.IsError);
        }

        [Fact(Skip = "反射完整面待补：目前只有 System.Type 的 Name/FullName 两个 getter + GetType，"
                        + "typeof（获取 Type 的入口）已就位。缺 Assembly / Type 成员（GetMethods 等）/"
                        + "MemberInfo 子集 / Activator / Enum.GetValues / Attribute.GetCustomAttribute。")]
        public void Reflection_AssemblySurface()
        {
        }
        // ------------------------------------------------------------------
        // 无语法证据的项（用最接近的 C# 形式试探；解析失败即证实该形态不被支持）
        // ------------------------------------------------------------------

        [Fact]
        public void AsyncAwait_NotSupported()
        {
            var diagnostics = Diagnostics(
                "function Main(args: string[]): i32 {" + Nl +
                "    var t = await GetAsync()" + Nl +
                "    return 0" + Nl +
                "}");
            _out.WriteLine("async 诊断: " + string.Join(" | ", diagnostics.Select(d => d.Message)));
            Assert.NotEmpty(diagnostics.Where(d => d.IsError));
        }

        [Fact]
        public void LinqQuery_NotSupported()
        {
            var diagnostics = Diagnostics(
                "using System.Linq" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var xs = [1, 2, 3]" + Nl +
                "    var ys = from x in xs select x * 2" + Nl +
                "    return 0" + Nl +
                "}");
            _out.WriteLine("LINQ 诊断: " + string.Join(" | ", diagnostics.Select(d => d.Message)));
            Assert.NotEmpty(diagnostics.Where(d => d.IsError));
        }

        [Fact(Skip = "集合表达式 `[1, 2, 3]` 未实现：无 CollectionExpression 语法节点，"
                        + "解析器把 `[` 当下标表达式起始并连报 Unexpected token。")]
        public void CollectionExpression_NotSupported()
        {
        }
        [Fact]
        public void ExplicitInterfaceImplementation_NotSupported()
        {
            var diagnostics = Diagnostics(
                "interface IReader { function Read(): i32 }" + Nl +
                "class Doc {" + Nl +
                "    public function IReader.Read(): i32 { return 1 }" + Nl +
                "}" + Nl +
                "function Main(args: string[]): i32 { return 0 }");
            _out.WriteLine("显式接口实现诊断: " + string.Join(" | ", diagnostics.Select(d => d.Message)));
            Assert.NotEmpty(diagnostics.Where(d => d.IsError));
        }
    }
}
