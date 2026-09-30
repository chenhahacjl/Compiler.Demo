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

        [Fact]
        public void NestedClass_DeclaredInsideClass()
        {
            // 类体内直接声明 class，并按 `new Outer.Inner()` 的 C# 式限定名构造
            var result = RunMain(
                "class Outer {" + Nl +
                "    public field Tag: i32" + Nl +
                "    class Inner {" + Nl +
                "        public field V: i32" + Nl +
                "    }" + Nl +
                "    public function Make(v: i32): Outer.Inner {" + Nl +
                "        var i = new Outer.Inner()" + Nl +
                "        i.V = v" + Nl +
                "        return i" + Nl +
                "    }" + Nl +
                "}" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var o = new Outer()" + Nl +
                "    return o.Make(7).V" + Nl +
                "}", "NestedClass");
            Assert.Equal(7, result);
        }

        [Fact]
        public void NestedClass_TwoLevelsDeep()
        {
            // 两层嵌套：A.B.C 的限定名要能一层层拼出来
            var result = RunMain(
                "class A {" + Nl +
                "    class B {" + Nl +
                "        class C {" + Nl +
                "            public field V: i32" + Nl +
                "        }" + Nl +
                "    }" + Nl +
                "    public function Make(): A.B.C {" + Nl +
                "        var c = new A.B.C()" + Nl +
                "        c.V = 5" + Nl +
                "        return c" + Nl +
                "    }" + Nl +
                "}" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var a = new A()" + Nl +
                "    return a.Make().V" + Nl +
                "}", "NestedClassDeep");

            Assert.Equal(5, result);
        }

        [Fact]
        public void NestedClass_InsideNamespace()
        {
            // 命名空间内的嵌套类：全名应为 Ns.Outer.Inner
            var result = RunMain(
                "namespace Ns {" + Nl +
                "    class Outer {" + Nl +
                "        class Inner {" + Nl +
                "            public field V: i32" + Nl +
                "        }" + Nl +
                "        public function Make(): Ns.Outer.Inner {" + Nl +
                "            var i = new Ns.Outer.Inner()" + Nl +
                "            i.V = 9" + Nl +
                "            return i" + Nl +
                "        }" + Nl +
                "    }" + Nl +
                "}" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var o = new Ns.Outer()" + Nl +
                "    return o.Make().V" + Nl +
                "}", "NestedClassNs");

            Assert.Equal(9, result);
        }

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
        public void PrimitiveToString_OnParameter_BoxesCorrectType_NotInt32()
        {
            // 回归护栏（阶段 8 自举经 ilverify 暴露 EvaluatorRuntime::DoubleToString）：
            // 对 **f64 形参** 调 ToString() 时，发射器发出
            //   `box System.Int32; call Convert::ToString(Object)`
            // 把双精度当 Int32 装箱，CLR 判 `found Double [expected Int32]`。
            // 注意：对「字面量初始化的局部量」调 ToString() 是正确的
            // （见 PrimitiveToString_BoxesCorrectType_NotInt32）——
            // 差别在接收者是形参，其类型来自签名而非推断。
            var result = RunMain(
                "class R {" + Nl +
                "    public static function F(v: f64): string {" + Nl +
                "        return v.ToString()" + Nl +
                "    }" + Nl +
                "}" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var s = R.F(1.5)" + Nl +
                "    if s == \"1.5\" { return 1 }" + Nl +
                "    return 0" + Nl +
                "}", "PrimToStringParam");

            Assert.Equal(1, result);
        }

        [Fact]
        public void PrimitiveToString_BoxesCorrectType_NotInt32()
        {
            // 回归护栏（阶段 8 自举经 ilverify 暴露 EvaluatorRuntime::DoubleToString）：
            // 对 f64 值调 ToString() 时，发射器发出
            //   `box System.Int32; call Convert::ToString(Object)`
            // 即把双精度**当 Int32 装箱**（TypeRef 指向 System.Int32），CLR 判非法：
            // `found Double [expected Int32]`。正确行为是 box System.Double。
            var result = RunMain(
                "function Main(args: string[]): i32 {" + Nl +
                "    var v = 1.5" + Nl +
                "    var s = v.ToString()" + Nl +
                "    if s == \"1.5\" { return 1 }" + Nl +
                "    return 0" + Nl +
                "}", "PrimToString");

            Assert.Equal(1, result);
        }

        [Fact]
        public void ArrayLoad_BoolArrayFieldElement_EmitsLdelemU1_NotRef()
        {
            // C 类线索（阶段 8 自举经 ilverify 暴露 Binder::GetClassMethodIsStatic）：
            // B1 中 `return _classMethodStatic[_classMethodOffsets[index] + methodIndex]`
            // 发出 ldelem.ref，而字段声明是 `bool[]`、方法返回 bool。
            // 发射器分派本身正确（Boolean -> Ldelem_U1），且 IsReferenceElement(Boolean)
            // 明确返回 false——所以要么元素类型被推成了非基元（object/Any），
            // 要么 bool[] 的数组类型映射有别。本用例直接验 bool[] 字段索引。
            var result = RunMain(
                "class Holder {" + Nl +
                "    private field _flags: bool[]" + Nl +
                "    public function Init() {" + Nl +
                "        _flags = new bool[8]" + Nl +
                "    }" + Nl +
                "    public function At(index: i32): bool {" + Nl +
                "        return _flags[index]" + Nl +
                "    }" + Nl +
                "    public function Set(index: i32, v: bool) {" + Nl +
                "        _flags[index] = v" + Nl +
                "    }" + Nl +
                "}" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var h = new Holder()" + Nl +
                "    h.Init()" + Nl +
                "    h.Set(3, true)" + Nl +
                "    if h.At(3) { return 1 }" + Nl +
                "    return 0" + Nl +
                "}", "BoolArrField2");

            Assert.Equal(1, result);
        }


        [Fact]
        public void AssignToParameter_ByValue_EmitsStarg()
        {
            // 回归护栏：赋值给**普通（非 byref）形参**此前在 IL 发射器里没有分支，
            // 会落到 _locals[node.Variable] 抛 KeyNotFoundException。
            // 真实触发例是自举语料里的 `usTexts = ug2`（形参重绑定，C# 语义合法）。
            // 它阻塞了「用 Compilation.Emit 快速迭代 .co 轨」这条路径。
            var result = RunMain(
                "function Rebind(xs: i32[], n: i32): i32[] {" + Nl +
                "    xs = new i32[n]" + Nl +
                "    xs[0] = 42" + Nl +
                "    return xs" + Nl +
                "}" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    let src = new i32[1]" + Nl +
                "    let out2 = Rebind(src, 1)" + Nl +
                "    if out2[0] == 42 { return 1 }" + Nl +
                "    return 0" + Nl +
                "}", "AssignParam");

            Assert.Equal(1, result);
        }

        [Fact]
        public void LocalSig_LetInsideLoopInClassMethod_KeepsStringType()
        {
            // B 类根因（阶段 8 自举经 ilverify + metadata dump 定位）：
            // B1 中 DeclWordOf / BinaryGlyphOf / UnaryGlyphOf / MethodReturnTypeOf /
            // FieldTypeOf 的 IL 序列完全一致，且 callvirt token 经验证**正确**
            // （0x060002BA = Node::Child、0x060002B6 = Node::Kind，返回 String）——
            // 唯一错误是 locals 签名写成 Int32,Int32：循环体内的 String 局部量
            // 被声明成 Int32。三个已排除的变量：
            //   · 顶层函数中的 let      → 正确
            //   · 顶层函数循环体内的 let → 正确（LocalSig_LetInsideLoop）
            //   · 形参而非局部量         → 正确
            // 本用例补上最后一个未测变量：**类方法**内循环体中的 let。
            var result = RunMain(
                "class Node {" + Nl +
                "    public function ChildCount(): i32 { return 2 }" + Nl +
                "    public function Child(i: i32): Node { return this }" + Nl +
                "    public function Kind(): string { return \"TypeClause\" }" + Nl +
                "}" + Nl +
                "class Finder {" + Nl +
                "    private function Scan(node: Node): string {" + Nl +
                "        var i = 0" + Nl +
                "        while i < node.ChildCount() {" + Nl +
                "            let k = node.Child(i).Kind()" + Nl +
                "            if k == \"TypeClause\" { return k }" + Nl +
                "            i = i + 1" + Nl +
                "        }" + Nl +
                "        return \"\"" + Nl +
                "    }" + Nl +
                "    public function Run(n: Node): string { return Scan(n) }" + Nl +
                "}" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    if new Finder().Run(new Node()) == \"TypeClause\" { return 1 }" + Nl +
                "    return 0" + Nl +
                "}", "LocalSigLoopMethod");

            Assert.Equal(1, result);
        }

        [Fact]
        public void LocalSig_LetInsideLoop_KeepsStringType()
        {
            // 回归护栏（阶段 8 自举经 ilverify + metadata dump 暴露）：
            // B1 中 DeclWordOf / BinaryGlyphOf / UnaryGlyphOf / MethodReturnTypeOf /
            // FieldTypeOf 五个方法形状完全一致——
            //   ldarg.1 / ldloc.0 / callvirt(Node 方法) / callvirt(Node 方法)
            //   / stloc.1 / ldloc.1 / ldstr / call String::op_Equality
            // 而 locals 签名是 Int32,Int32：**loop 体内 let 声明的 String 局部量
            // 被声明成 Int32**。函数顶层的同类 let 是正确的（见
            // LocalSig_MethodCallReturningString_NotMarkedInt32），
            // 差别就在于它位于循环体内。
            var result = RunMain(
                "class Node {" + Nl +
                "    public function ChildCount(): i32 { return 2 }" + Nl +
                "    public function Child(i: i32): Node { return this }" + Nl +
                "    public function Kind(): string { return \"TypeClause\" }" + Nl +
                "}" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    let node = new Node()" + Nl +
                "    var i = 0" + Nl +
                "    while i < node.ChildCount() {" + Nl +
                "        let k = node.Child(i).Kind()" + Nl +
                "        if k == \"TypeClause\" { return 1 }" + Nl +
                "        i = i + 1" + Nl +
                "    }" + Nl +
                "    return 0" + Nl +
                "}", "LocalSigLoop");

            Assert.Equal(1, result);
        }

        [Fact]
        public void MemberCall_ResolvesOnReceiverType_NotEnclosingClass()
        {
            // 回归护栏（阶段 8 自举经 ilverify + metadata dump 暴露）：
            // B1 中 Binder::FieldTypeOf 的 callvirt 落到了 Binder 自己的方法
            // （MethodReturnTypeOf / FieldNameOf），而源码此处调的是 Node::Child / Node::Kind。
            // 即「类间成员调用」疑似按名串到了**外层类**的方法上。
            // 本用例让外层类与接收者类**方法同名但签名不同**，看解析落在哪个。
            var result = RunMain(
                "class Node {" + Nl +
                "    public function Kind(): string { return \"NodeKind\" }" + Nl +
                "}" + Nl +
                "class Binder {" + Nl +
                "    public function Kind(): string { return \"BinderKind\" }" + Nl +
                "    public static function Probe(n: Node): string {" + Nl +
                "        return n.Kind()" + Nl +
                "    }" + Nl +
                "}" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    if Binder.Probe(new Node()) == \"NodeKind\" { return 1 }" + Nl +
                "    return 0" + Nl +
                "}", "MemberRecv");

            Assert.Equal(1, result);
        }

        [Fact]
        public void LocalSig_MethodCallReturningString_NotMarkedInt32()
        {
            // 回归护栏（阶段 8 自举经 ilverify 暴露）：发射器曾把「方法调用返回的 String」
            // 局部量在 locals 签名里写成 Int32，随后 String.op_Equality 收到 Int32 操作数，
            // CLR 判 InvalidProgramException。
            // 最小复现：局部量类型必须来自方法调用的**实际返回类型**，而非字面量 0 的类型。
            var result = RunMain(
                "class Node {" + Nl +
                "    public function Child(i: i32): Node { return this }" + Nl +
                "    public function Kind(): string { return \"TypeClause\" }" + Nl +
                "}" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var node = new Node()" + Nl +
                "    var i = 0" + Nl +
                "    let k = node.Child(i).Kind()" + Nl +
                "    if k == \"TypeClause\" { return 1 }" + Nl +
                "    return 0" + Nl +
                "}", "LocSigStr");

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

        /// <summary>
        /// 集合表达式 `[1, 2, 3]`（C# 12）——**未实现**，测试体已写好待启用。
        ///
        /// 实测缺口：`var a: i32[] = [1, 2, 3]` 报
        /// `Unexpected token &lt;OpenBracketToken&gt;, expected &lt;IdentifierToken&gt;`，
        /// 因为 `[` 在主表达式位置不被当作起始符（它只在下标后置位置有含义）。
        ///
        /// 计划做法（**尚未实施**，据评估涉及三处、且绑定器改动有风险）：
        /// 1. 解析：在主表达式 switch 里加 `OpenBracketToken` 分支，解析
        ///    `[e1, e2, ...]`，**脱糖**成已有的 <c>ArrayCreationExpressionSyntax</c>
        ///    （Elements 装元素、Size 留空、new 关键字用默认 token）——
        ///    刻意不新建语法节点类型，避免重蹈 green node 槽位承载的覆辙。
        /// 2. 绑定：需要从**目标类型**推出元素类型（`i32[]` → i32）。
        ///    这一步是主要风险点：绑定器在多数表达式位置拿不到目标类型上下文，
        ///    可能需要引入"期望类型"参数或在声明绑定处特判。
        /// 3. 发射：复用既有数组创建发射（newarr + dup + stelem）。
        ///
        /// 本次只覆盖「目标是数组」这一种最直接形态；List/集合目标、
        /// 展开运算符 `..`、切片 `[1..3]` 都另算。
        /// </summary>
        [Fact(Skip = "集合表达式未实现（测试体已备好）：缺口已实测为 "
                        + "Unexpected token <OpenBracketToken>, expected <IdentifierToken>。"
                        + "实现需改 解析+绑定+发射 三处，其中「从目标类型推元素类型」有风险，详见本方法注释。")]
        public void CollectionExpression_BuildsArray()
        {
            // C# 12 集合表达式：`int[] a = [1, 2, 3];`
            // 先只覆盖「目标是数组」这一种最直接的形态——元素类型由目标类型推出。
            var result = RunMain(
                "function Main(args: string[]): i32 {" + Nl +
                "    var a: i32[] = [1, 2, 3]" + Nl +
                "    var s = 0" + Nl +
                "    var i = 0" + Nl +
                "    while i < 3 { s = s + a[i]" + Nl +
                "      i = i + 1 }" + Nl +
                "    return s" + Nl +
                "}", "CollectionExprArray");

            Assert.Equal(6, result);
        }
        /// <summary>
        /// 显式接口实现 `public function IReader.Read(): i32` —— **语义仍未实现**，
        /// 但本用例守的是「解析层不再报误导性错误」这一条。
        ///
        /// 实测改动前：报 `Unexpected token &lt;DotToken&gt;, expected &lt;OpenParenthesisToken&gt;`
        /// 外加一串错误恢复噪声（实测 6 条以上）。原用例名叫 `..._NotSupported`，
        /// **那个名字是误导的**——它暗示「语义层判定不支持」，实际是**解析层压根读不出
        /// 带点的成员名**，于是错误现场看起来像解析器崩在一个不该出现的位置。
        ///
        /// 改动后：方法名位置也走限定名解析（与 <c>ParseTypeClause</c> 对称），
        /// 诊断降到 **0 条**——名字 `IReader.Read` 被当作普通方法名绑定。
        ///
        /// **注意这不等于显式接口实现可用**：`Doc` 并没有真的去实现 `IReader`，
        /// 把 `Doc` 当 `IReader` 用依然不成立（那需要 MemberRef 名带点的
        /// `IReader.Read` 发射路径 + 接口槽位映射）。这里只锁住
        /// 「限定名成员声明能干净解析」这一条已完成的行为。
        /// </summary>
        [Fact]
        public void ExplicitInterfaceImplementation_QualifiedMemberNameParsesCleanly()
        {
            var diagnostics = Diagnostics(
                "interface IReader { function Read(): i32 }" + Nl +
                "class Doc {" + Nl +
                "    public function IReader.Read(): i32 { return 1 }" + Nl +
                "}" + Nl +
                "function Main(args: string[]): i32 { return 0 }");

            Assert.Empty(diagnostics.Where(d => d.IsError));
        }
    }
}
