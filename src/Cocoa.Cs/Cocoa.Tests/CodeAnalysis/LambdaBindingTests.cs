using System.Collections.Generic;
using System.Linq;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// Lambda / 函数值绑定与求值测试（6e-M22 C4-a，Evaluator 后端）：提升、变量存储、间接调用、
    /// 方法组转换、函数类型参数传递。
    /// IL / native 发射于 C4-b/C4-c 接入（当前统一报门禁诊断）。
    /// </summary>
    public class LambdaBindingTests
    {
        private const string VariableInvokeProgram = @"using System

function Main(): i32
{
    var f: (i32) -> i32 = (x: i32) => x * 2
    Console.WriteLine(f(21))

    if f(5) != 10
    {
        return 1
    }

    return 0
}";

        private const string MethodGroupProgram = @"using System

function Double(x: i32): i32
{
    return x * 2
}

function Main(): i32
{
    var g: (i32) -> i32 = Double
    Console.WriteLine(g(8))

    if g(3) != 6
    {
        return 1
    }

    return 0
}";

        private const string HigherOrderProgram = @"using System

function Apply(f: (i32) -> i32, v: i32): i32
{
    return f(v)
}

function Inc(x: i32): i32
{
    return x + 1
}

function Main(): i32
{
    Console.WriteLine(Apply((x: i32) => x + 1, 41))
    Console.WriteLine(Apply(Inc, 9))

    if Apply((x: i32) => x * 3, 4) != 12
    {
        return 1
    }

    return 0
}";

        [Fact]
        public void Evaluator_LambdaVariable_Invoke()
        {
            var result = Evaluate(VariableInvokeProgram);
            Assert.Empty(result.Diagnostics.Where(d => d.IsError));
            Assert.Equal(0, result.Value);
        }

        [Fact]
        public void Evaluator_MethodGroup_ConvertsAndInvokes()
        {
            var result = Evaluate(MethodGroupProgram);
            Assert.Empty(result.Diagnostics.Where(d => d.IsError));
            Assert.Equal(0, result.Value);
        }

        [Fact]
        public void Evaluator_HigherOrder_FunctionTypeParameter()
        {
            var result = Evaluate(HigherOrderProgram);
            Assert.Empty(result.Diagnostics.Where(d => d.IsError));
            Assert.Equal(0, result.Value);
        }

        [Fact]
        public void Binder_Lambda_ImplicitParametersWithoutTarget_Diagnosed()
        {
            // .co 方言：隐式参数在解析层即拒绝（C# 方言允许，绑定层目标推导见 C4 后续）
            var tree = SyntaxTree.Parse("let f = (x: i64, y) => x");
            Assert.Contains(tree.Diagnostics, d => d.IsError && d.Message.Contains("须显式标注类型"));
        }

        [Fact]
        public void Binder_MethodGroup_OverloadAmbiguity_Diagnosed()
        {
            var code = @"using System

function Pick(x: i32): i32 { return x }
function Pick(x: string): string { return x }

function Main()
{
    var g: (i32) -> i32 = Pick
}";
            var result = Evaluate(code);
            Assert.Contains(result.Diagnostics, d => d.IsError && d.Message.Contains("重载"));
        }

        [Fact]
        public void Emit_FunctionValue_GatedWithClearDiagnostic()
        {
            var syntaxTree = SyntaxTree.Parse(VariableInvokeProgram);
            var compilation = Compilation.Create(syntaxTree);
            var diagnostics = compilation.EmitNative("fn_gate", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cocoa_fn_gate.exe"));

            Assert.Contains(diagnostics, d => d.IsError && d.Message.Contains("C4-c"));
        }

        private static EvaluationResult Evaluate(string code)
        {
            var tree = SyntaxTree.Parse(code);
            var compilation = Compilation.Create(tree);
            return compilation.Evaluate(new Dictionary<VariableSymbol, object>());
        }
    }
}
