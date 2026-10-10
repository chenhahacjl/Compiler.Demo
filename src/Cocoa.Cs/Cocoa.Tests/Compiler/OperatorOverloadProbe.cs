using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Xunit;
using Xunit.Abstractions;

namespace Cocoa.Tests.Compiler
{
    /// <summary>
    /// 运算符重载端到端：声明（`function operator +` / `operator -` / `operator ==` / `implicit operator int`）
    /// → 绑定回落（内建表未命中查 <c>OperatorRegistry</c>）→ IL 发射（静态 call）→ 加载执行。
    /// </summary>
    public class OperatorOverloadProbe
    {
        private readonly ITestOutputHelper _out;
        public OperatorOverloadProbe(ITestOutputHelper o) { _out = o; }

        private static string Nl => Environment.NewLine;

        /// <summary>C# 轨参考路径：Compilation.Emit 直出 PE，再反射加载执行 Main。</summary>
        private static int RunMain(string source, string tag)
        {
            var compilation = Compilation.Create(SyntaxTree.Parse(source));
            var path = Path.Combine(Path.GetTempPath(), "cocoa-operatorprobe", tag + ".dll");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var diagnostics = compilation.Emit(
                "Main",
                References,
                path,
                Cocoa.Targeting.IlTarget.Parse("net9.0"),
                emitLibrary: true);

            Assert.Empty(string.Join("\n", diagnostics.Where(d => d.IsError)));

            var asm = System.Reflection.Assembly.LoadFile(path);
            return (int)asm.EntryPoint!.Invoke(null, new object[] { new[] { "x" } })!;
        }

        private static readonly string[] References =
        {
            typeof(object).Assembly.Location,
            typeof(System.Console).Assembly.Location,
        };

        /// <summary>按 <c>Compilation</c> 的常规路径绑定单棵树（与 SemanticModel 一致）。</summary>
        private static BoundProgram Bind(string source)
        {
            var compilation = CompilationOf(source);
            return compilation.BindProgram(
                isScript: false,
                previous: null,
                compilation.GlobalScope,
                ImmutableArray<Cocoa.CodeAnalysis.Serialization.CoaProgram>.Empty,
                linkCodDynamically: false,
                globalNamespace: null);
        }

        private static Cocoa.CodeAnalysis.Compilation CompilationOf(string source)
        {
            return Compilation.Create(new[] { SyntaxTree.Parse(source) });
        }

        /// <summary>解析 + 绑定的全部诊断（<c>Compilation.GetDiagnostics</c> 口径，含函数体错误）。</summary>
        private static System.Collections.Generic.IEnumerable<Diagnostic> Diagnostics(string source)
        {
            return CompilationOf(source).GetDiagnostics();
        }

        // ------------------------------------------------------------------
        // 绑定层：符号与解析
        // ------------------------------------------------------------------

        [Fact]
        public void Operator_Declaration_SynthesizesMetadataName()
        {
            var program = Bind((
                    "class Vec {" + Nl +
                    "    public field X: i32" + Nl +
                    "    public static function operator +(a: Vec, b: Vec): Vec { return a }" + Nl +
                    "}"));

            Assert.Empty(program.Diagnostics.Where(d => d.IsError));

            var vec = program.Classes.Single(c => c.Name == "Vec");
            var op = vec.GetMethods("op_Addition").Single();
            Assert.True(op.IsStatic);
            Assert.Equal(OperatorKind.Addition, op.OperatorKind);
            Assert.Equal(2, op.Parameters.Length);
            Assert.Equal("Vec", op.ReturnType.Name);
        }

        [Fact]
        public void Operator_Use_ResolvesToUserDefinedMethod()
        {
            var program = Bind((
                    "class Vec {" + Nl +
                    "    public field X: i32" + Nl +
                    "    public static function operator +(a: Vec, b: Vec): Vec { return a }" + Nl +
                    "}" + Nl +
                    "function Main(): i32 {" + Nl +
                    "    var a = new Vec()" + Nl +
                    "    var b = new Vec()" + Nl +
                    "    var c = a + b" + Nl +
                    "    return c.X" + Nl +
                    "}"));

            Assert.Empty(program.Diagnostics.Where(d => d.IsError));

            var binary = FindBinary(program);
            Assert.True(binary.Op.IsUserDefined,
                "IsUserDefined=false; Kind=" + binary.Op.Kind +
                " Left=" + binary.Left.Type.Name + " Right=" + binary.Right.Type.Name);
            Assert.Equal("op_Addition", binary.Op.UserDefinedMethod!.Name);
        }

        [Fact]
        public void UnaryOperator_Declaration_SynthesizesMetadataName()
        {
            var program = Bind((
                    "class Vec {" + Nl +
                    "    public field X: i32" + Nl +
                    "    public static function operator -(a: Vec): Vec { return a }" + Nl +
                    "}"));

            Assert.Empty(program.Diagnostics.Where(d => d.IsError));

            var vec = program.Classes.Single(c => c.Name == "Vec");
            var op = vec.GetMethods("op_UnaryNegation").Single();
            Assert.Equal(OperatorKind.UnaryNegation, op.OperatorKind);
            Assert.Single(op.Parameters);
        }

        [Fact]
        public void ComparisonOperator_ResultTypeIsBool()
        {
            var program = Bind((
                    "class Vec {" + Nl +
                    "    public field X: i32" + Nl +
                    "    public static function operator ==(a: Vec, b: Vec): bool { return true }" + Nl +
                    "}" + Nl +
                    "function Main(): i32 {" + Nl +
                    "    var a = new Vec()" + Nl +
                    "    var b = new Vec()" + Nl +
                    "    if a == b { return 1 }" + Nl +
                    "    return 0" + Nl +
                    "}"));

            Assert.Empty(program.Diagnostics.Where(d => d.IsError));

            var binary = FindBinary(program);
            Assert.True(binary.Op.IsUserDefined);
            Assert.Equal("op_Equality", binary.Op.UserDefinedMethod!.Name);
        }

        [Fact]
        public void ImplicitConversion_Declaration_SynthesizesMetadataName()
        {
            var program = Bind((
                    "class Vec {" + Nl +
                    "    public field X: i32" + Nl +
                    "    public static function implicit operator int(v: Vec): i32 { return v.X }" + Nl +
                    "}"));

            Assert.Empty(program.Diagnostics.Where(d => d.IsError));

            var vec = program.Classes.Single(c => c.Name == "Vec");
            var op = vec.GetMethods("op_Implicit").Single();
            Assert.Equal(OperatorKind.ImplicitConversion, op.OperatorKind);
            Assert.Equal("int", op.ReturnType.Name);
            Assert.Equal(1, op.Parameters.Length);
            Assert.Equal("Vec", op.Parameters[0].Type.Name);
        }

        // ------------------------------------------------------------------
        // 优先级：内建优先
        // ------------------------------------------------------------------

        [Fact]
        public void BuiltinOperators_TakePrecedenceOverUserDefined()
        {
            var program = Bind((
                    "class Vec {" + Nl +
                    "    public field X: i32" + Nl +
                    "    public static function operator +(a: i32, b: i32): Vec { return new Vec() }" + Nl +
                    "}" + Nl +
                    "function Main(): i32 {" + Nl +
                    "    return 1 + 2" + Nl +
                    "}"));

            Assert.Empty(program.Diagnostics.Where(d => d.IsError));

            var binary = FindBinary(program);
            Assert.False(binary.Op.IsUserDefined);
            Assert.Equal(BoundBinaryOperatorKind.Addition, binary.Op.Kind);
        }

        // ------------------------------------------------------------------
        // 诊断
        // ------------------------------------------------------------------

        [Fact]
        public void Operator_BinaryWrongArity_ReportsError()
        {
            // `==` 是二元运算符却只给 1 个操作数
            var diagnostics = Diagnostics(
                "class Vec {" + Nl +
                "    public static function operator ==(a: Vec): bool { return true }" + Nl +
                "}");

            Assert.Contains(diagnostics, d => d.IsError && d.Message.Contains("需要 2 个操作数"));
        }

        [Fact]
        public void Operator_SingleParameterPlus_IsUnaryPlus()
        {
            // C# 语义：`operator +` 单参 = 一元正号（op_UnaryPlus），非错误
            var program = Bind(
                "class Vec {" + Nl +
                "    public field X: i32" + Nl +
                "    public static function operator +(a: Vec): Vec { return a }" + Nl +
                "}");

            Assert.Empty(program.Diagnostics.Where(d => d.IsError));

            var vec = program.Classes.Single(c => c.Name == "Vec");
            var op = vec.GetMethods("op_UnaryPlus").Single();
            Assert.Equal(OperatorKind.UnaryPlus, op.OperatorKind);
        }

        [Fact]
        public void Operator_DuplicateSignature_ReportsError()
        {
            var diagnostics = Diagnostics(
                "class Vec {" + Nl +
                "    public static function operator +(a: Vec, b: Vec): Vec { return a }" + Nl +
                "    public static function operator +(a: Vec, b: Vec): Vec { return b }" + Nl +
                "}");

            Assert.Contains(diagnostics, d => d.IsError);
        }

        [Fact]
        public void Operator_InInterface_ReportsError()
        {
            var diagnostics = Diagnostics(
                "interface IBox {" + Nl +
                "    public static function operator +(a: IBox, b: IBox): IBox { return a }" + Nl +
                "}");

            Assert.Contains(diagnostics, d => d.IsError);
        }

        [Fact]
        public void Operator_ConversionReturningVoid_ReportsError()
        {
            var diagnostics = Diagnostics(
                "class Vec {" + Nl +
                "    public static function implicit operator void(v: Vec): void { }" + Nl +
                "}");

            Assert.Contains(diagnostics, d => d.IsError && d.Message.Contains("不能为 void"));
        }

        [Fact]
        public void MismatchedOperands_DoNotBindToOverload()
        {
            // Vec 有 op_Addition(Vec, Vec)，但 a + b 的右侧是 Other —— 不得误配到 (Vec,Vec)，
            // 也不得把 Vec 的 + 借给 Other（否则跨类型运算静默通过）。
            var program = Bind(
                "class Vec {" + Nl +
                "    public field X: i32" + Nl +
                "    public static function operator +(a: Vec, b: Vec): Vec { return a }" + Nl +
                "}" + Nl +
                "class Other {" + Nl +
                "    public field Y: i32" + Nl +
                "}" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var a = new Vec()" + Nl +
                "    var b = new Other()" + Nl +
                "    var c = a + b" + Nl +
                "    return 0" + Nl +
                "}");

            // 要么绑定期报错（运算符未定义），要么 c 的类型不是 Vec —— 两者都算「没有误配」
            var diagnostics = Diagnostics(
                "class Vec {" + Nl +
                "    public field X: i32" + Nl +
                "    public static function operator +(a: Vec, b: Vec): Vec { return a }" + Nl +
                "}" + Nl +
                "class Other {" + Nl +
                "    public field Y: i32" + Nl +
                "}" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var a = new Vec()" + Nl +
                "    var b = new Other()" + Nl +
                "    var c = a + b" + Nl +
                "    return 0" + Nl +
                "}");

            if (diagnostics.Any(d => d.IsError))
            {
                return;
            }

            var declared = program.Functions
                .First(f => f.Key.Name == "Main")
                .Value.Statements
                .SelectMany(WalkStatement)
                .ToList();

            Assert.DoesNotContain(declared, b => b.Op.IsUserDefined);
        }

        // ------------------------------------------------------------------
        // 端到端执行（IL 发射 + CLR 加载）
        // ------------------------------------------------------------------

        [Fact]
        public void Baseline_NoOperator_SameShape_Compiles()
        {
            // 对照组：与 BinaryOperator_EndToEnd 同样的类/字段/静态方法/构造/调用形状，但无运算符重载。
            // 用于区分「运算符路径问题」与「该程序形状本身触发 Evaluator 断言」。
            var result = RunMain(
                "class Vec {" + Nl +
                "    public field X: i32" + Nl +
                "    public static function Make(a: Vec, b: Vec): Vec {" + Nl +
                "        var r = new Vec()" + Nl +
                "        r.X = a.X + b.X" + Nl +
                "        return r" + Nl +
                "    }" + Nl +
                "}" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var a = new Vec()" + Nl +
                "    a.X = 1" + Nl +
                "    var b = new Vec()" + Nl +
                "    b.X = 2" + Nl +
                "    var c = Vec.Make(a, b)" + Nl +
                "    return c.X" + Nl +
                "}", "OpBaseline");

            Assert.Equal(3, result);
        }

        [Fact]
        public void BinaryOperator_EndToEnd_Executes()
        {
            // Vec(1) + Vec(2) → Vec(X=3)；取 X = 3
            var result = RunMain(
                "class Vec {" + Nl +
                "    public field X: i32" + Nl +
                "    public static function operator +(a: Vec, b: Vec): Vec {" + Nl +
                "        var r = new Vec()" + Nl +
                "        r.X = a.X + b.X" + Nl +
                "        return r" + Nl +
                "    }" + Nl +
                "}" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var a = new Vec()" + Nl +
                "    a.X = 1" + Nl +
                "    var b = new Vec()" + Nl +
                "    b.X = 2" + Nl +
                "    var c = a + b" + Nl +
                "    return c.X" + Nl +
                "}", "OpBinary");

            Assert.Equal(3, result);
        }

        [Fact]
        public void UnaryOperator_EndToEnd_Executes()
        {
            // -(Vec(7)) → Vec(X=-7)；取 X = -7
            var result = RunMain(
                "class Vec {" + Nl +
                "    public field X: i32" + Nl +
                "    public static function operator -(a: Vec): Vec {" + Nl +
                "        var r = new Vec()" + Nl +
                "        r.X = 0 - a.X" + Nl +
                "        return r" + Nl +
                "    }" + Nl +
                "}" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var a = new Vec()" + Nl +
                "    a.X = 7" + Nl +
                "    var c = -a" + Nl +
                "    return c.X" + Nl +
                "}", "OpUnary");

            Assert.Equal(-7, result);
        }

        [Fact]
        public void CompoundAssignment_FallsBackToUserDefinedAddition_EndToEnd()
        {
            // `c += b` 无显式 op_+= → 脱糖 `c = c + b` 回落用户 op_+（C# §12.21.2 语义）
            var result = RunMain(
                "class Vec {" + Nl +
                "    public field X: i32" + Nl +
                "    public static function operator +(a: Vec, b: Vec): Vec {" + Nl +
                "        var r = new Vec()" + Nl +
                "        r.X = a.X + b.X" + Nl +
                "        return r" + Nl +
                "    }" + Nl +
                "}" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var a = new Vec()" + Nl +
                "    a.X = 1" + Nl +
                "    var b = new Vec()" + Nl +
                "    b.X = 2" + Nl +
                "    var c = new Vec()" + Nl +
                "    c.X = 4" + Nl +
                "    c += b" + Nl +
                "    return c.X" + Nl +
                "}", "OpCompoundAdd");

            Assert.Equal(6, result);
        }

        [Fact]
        public void CompoundAssignment_FallsBackToUserDefinedSubtraction_EndToEnd()
        {
            // `c -= b` → 脱糖 `c = c - b` 回落用户 op_
            var result = RunMain(
                "class Vec {" + Nl +
                "    public field X: i32" + Nl +
                "    public static function operator -(a: Vec, b: Vec): Vec {" + Nl +
                "        var r = new Vec()" + Nl +
                "        r.X = a.X - b.X" + Nl +
                "        return r" + Nl +
                "    }" + Nl +
                "}" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var a = new Vec()" + Nl +
                "    a.X = 10" + Nl +
                "    var b = new Vec()" + Nl +
                "    b.X = 3" + Nl +
                "    a -= b" + Nl +
                "    return a.X" + Nl +
                "}", "OpCompoundSub");

            Assert.Equal(7, result);
        }

        [Fact]
        public void ComparisonOperator_EndToEnd_Executes()
        {
            // a.X == b.X → 走 op_Equality，返回 42 表明确实进了用户运算符
            var result = RunMain(
                "class Vec {" + Nl +
                "    public field X: i32" + Nl +
                "    public static function operator ==(a: Vec, b: Vec): bool { return a.X == b.X }" + Nl +
                "}" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var a = new Vec()" + Nl +
                "    a.X = 1" + Nl +
                "    var b = new Vec()" + Nl +
                "    b.X = 1" + Nl +
                "    if a == b { return 42 }" + Nl +
                "    return 0" + Nl +
                "}", "OpCompare");

            Assert.Equal(42, result);
        }

        [Fact]
        public void MultiplicationOperator_EndToEnd_Executes()
        {
            // Vec(3) * Vec(4) → X = 12
            var result = RunMain(
                "class Vec {" + Nl +
                "    public field X: i32" + Nl +
                "    public static function operator *(a: Vec, b: Vec): Vec {" + Nl +
                "        var r = new Vec()" + Nl +
                "        r.X = a.X * b.X" + Nl +
                "        return r" + Nl +
                "    }" + Nl +
                "}" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var a = new Vec()" + Nl +
                "    a.X = 3" + Nl +
                "    var b = new Vec()" + Nl +
                "    b.X = 4" + Nl +
                "    var c = a * b" + Nl +
                "    return c.X" + Nl +
                "}", "OpMul");

            Assert.Equal(12, result);
        }

        [Fact]
        public void ImplicitConversion_IsAppliedInImplicitContext_EndToEnd()
        {
            // Vec → i32 隐式转换在「赋值/传参/算术」等隐式位置自动生效：a + 1 应得 10
            var result = RunMain(
                "class Vec {" + Nl +
                "    public field X: i32" + Nl +
                "    public static function implicit operator int(v: Vec): i32 { return v.X }" + Nl +
                "}" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var a = new Vec()" + Nl +
                "    a.X = 9" + Nl +
                "    var s: i32 = a" + Nl +
                "    return s + 1" + Nl +
                "}", "OpImplicitUse");

            Assert.Equal(10, result);
        }

        [Fact]
        public void ExplicitConversion_IsAppliedInCastContext_EndToEnd()
        {
            // i32 → Vec 显式转换在强制转换位置生效：Var b = (Vec)5 取 X = 5
            var result = RunMain(
                "class Vec {" + Nl +
                "    public field X: i32" + Nl +
                "    public static function explicit operator Vec(v: i32): Vec {" + Nl +
                "        var r = new Vec()" + Nl +
                "        r.X = v" + Nl +
                "        return r" + Nl +
                "    }" + Nl +
                "}" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var b: Vec = (Vec)5" + Nl +
                "    return b.X" + Nl +
                "}", "OpExplicitUse");

            Assert.Equal(5, result);
        }

        [Fact]
        public void ExplicitConversion_RejectedInImplicitContext()
        {
            // 只有 op_Explicit 时，隐式位置不得使用
            var diagnostics = Diagnostics(
                "class Vec {" + Nl +
                "    public field X: i32" + Nl +
                "    public static function explicit operator Vec(v: i32): Vec { return new Vec() }" + Nl +
                "}" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var b: Vec = 5" + Nl +
                "    return 0" + Nl +
                "}");

            Assert.Contains(diagnostics, d => d.IsError);
        }

        [Fact]
        public void ImplicitConversion_IsEmittedWithOpImplicitName()
        {
            // 转换运算符以 op_Implicit / op_Explicit 出现在元数据中（转换调用的接线见转换运算测试）
            var compilation = Compilation.Create(SyntaxTree.Parse(
                "class Vec {" + Nl +
                "    public field X: i32" + Nl +
                "    public static function implicit operator int(v: Vec): i32 { return v.X }" + Nl +
                "}" + Nl +
                "function Main(args: string[]): i32 {" + Nl +
                "    var a = new Vec()" + Nl +
                "    a.X = 9" + Nl +
                "    return a.X" + Nl +
                "}"));

            var path = Path.Combine(Path.GetTempPath(), "cocoa-operatorprobe", "OpImplicit.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var diagnostics = compilation.Emit("Main", References, path, Cocoa.Targeting.IlTarget.Parse("net9.0"), emitLibrary: true);
            Assert.Empty(string.Join("\n", diagnostics.Where(d => d.IsError)));

            var asm = System.Reflection.Assembly.LoadFile(path);
            var vecType = asm.GetType("Vec");
            Assert.NotNull(vecType);
            Assert.NotEmpty(vecType!.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(m => m.Name == "op_Implicit"));
        }

        // ------------------------------------------------------------------
        // 辅助
        // ------------------------------------------------------------------

        private static BoundBinaryExpression FindBinary(BoundProgram program)
        {
            // 只扫 Main 体——program.Functions 含全局作用域预置函数，会干扰「首个二元表达式」的选取。
            // 按名字取而非用 MainFunction：无参 `function Main(): i32` 不一定被登记为入口。
            var mains = program.Functions.Where(f => f.Key.Name == "Main").ToList();
            var bodies = string.Join(" ;; ", mains.Select(m => string.Join(" | ", m.Value.Statements.Select(s => s.GetType().Name))));
            var found = mains.SelectMany(m => WalkStatements(m.Value.Statements)).FirstOrDefault();
            Assert.True(found != null, "Main 体语句: [" + bodies + "]; 函数数=" + program.Functions.Count);
            return found!;
        }

        /// <summary>递归遍历 Main 体的全部语句/表达式，产出遇到的每个 <see cref="BoundBinaryExpression"/>。</summary>
        private static IEnumerable<BoundBinaryExpression> WalkStatements(IEnumerable<BoundStatement> statements)
        {
            foreach (var statement in statements)
            {
                foreach (var binary in WalkStatement(statement))
                {
                    yield return binary;
                }
            }
        }

        private static IEnumerable<BoundBinaryExpression> WalkStatement(BoundStatement statement)
        {
            switch (statement)
            {
                case BoundSequencePointStatement sequencePoint:
                    foreach (var binary in WalkStatement(sequencePoint.Statement))
                    {
                        yield return binary;
                    }

                    break;

                case BoundBlockStatement block:
                    foreach (var binary in WalkStatements(block.Statements))
                    {
                        yield return binary;
                    }
                    break;

                case BoundVariableDeclaration declaration:
                    foreach (var binary in WalkExpression(declaration.Initializer))
                    {
                        yield return binary;
                    }
                    break;

                case BoundExpressionStatement expressionStatement:
                    foreach (var binary in WalkExpression(expressionStatement.Expression))
                    {
                        yield return binary;
                    }
                    break;

                case BoundReturnStatement returnStatement when returnStatement.Expression != null:
                    foreach (var binary in WalkExpression(returnStatement.Expression))
                    {
                        yield return binary;
                    }
                    break;

                case BoundConditionalGotoStatement conditionalGoto:
                    foreach (var binary in WalkExpression(conditionalGoto.Condition))
                    {
                        yield return binary;
                    }

                    break;

                case BoundIfStatement ifStatement:
                    {
                        foreach (var binary in WalkExpression(ifStatement.Condition))
                        {
                            yield return binary;
                        }
                    }

                    foreach (var binary in WalkStatement(ifStatement.ThenStatement))
                    {
                        yield return binary;
                    }

                    if (ifStatement.ElseStatement != null)
                    {
                        foreach (var binary in WalkStatement(ifStatement.ElseStatement))
                        {
                            yield return binary;
                        }
                    }

                    break;
            }
        }

        private static IEnumerable<BoundBinaryExpression> WalkExpression(BoundExpression expression)
        {
            switch (expression)
            {
                case BoundBinaryExpression binary:
                    yield return binary;

                    foreach (var nested in WalkExpression(binary.Left))
                    {
                        yield return nested;
                    }

                    foreach (var nested in WalkExpression(binary.Right))
                    {
                        yield return nested;
                    }

                    break;

                case BoundAssignmentExpression assignment:
                    foreach (var nested in WalkExpression(assignment.Expression))
                    {
                        yield return nested;
                    }

                    break;

                case BoundMemberCallExpression call:
                    foreach (var nested in WalkExpression(call.Expression))
                    {
                        yield return nested;
                    }

                    foreach (var argument in call.Arguments)
                    {
                        foreach (var nested in WalkExpression(argument))
                        {
                            yield return nested;
                        }
                    }

                    break;
            }
        }
    }
}
