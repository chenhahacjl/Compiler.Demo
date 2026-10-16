using System.Collections.Immutable;
using System.Linq;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Bound;
using Cocoa.CodeAnalysis.FlowAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Syntax.Nodes;
using Cocoa.CodeAnalysis.Syntax.Green;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.FlowAnalysis
{
    /// <summary>
    /// 独立 Flow Analysis 测试（3.3）：不依赖 Binder 实例，直接构造降级 MIR 调用
    /// <see cref="Cocoa.CodeAnalysis.FlowAnalysis.FlowAnalysis.Run"/>，验证明确赋值三诊断。
    /// 输入为扁平语句序列（Label/Goto/Return/赋值），与 ControlFlowGraph 的 MIR 契约一致。
    /// </summary>
    public class FlowAnalysisTests
    {
        private static readonly SyntaxNode NoSyntax = SyntaxTree.Parse("return 1").Root;

        private static ImmutableArray<Diagnostic> Run(string body, params ParameterSymbol[] outParameters)
        {
            var statements = body.Split('\n')
                .Where(line => line.Length > 0)
                .Select(ParseStatement)
                .ToImmutableArray();

            var block = new BoundBlockStatement(NoSyntax, statements);
            return Cocoa.CodeAnalysis.FlowAnalysis.FlowAnalysis.Run(block, outParameters.ToImmutableArray());
        }

        /// <summary>把单行 DSL 转成 MIR 语句（供独立测试用）。</summary>
        private static BoundStatement ParseStatement(string line)
        {
            line = line.Trim();
            if (line.StartsWith("ret ", System.StringComparison.Ordinal))
            {
                var expr = ParseExpression(line.Substring(4));
                return new BoundReturnStatement(NoSyntax, expr);
            }

            if (line.StartsWith("return", System.StringComparison.Ordinal))
            {
                return new BoundReturnStatement(NoSyntax, null);
            }

            if (line.StartsWith("assign ", System.StringComparison.Ordinal))
            {
                var parts = line.Substring(7).Split(' ');
                var variable = FindVariable(parts[0]);
                return new BoundExpressionStatement(NoSyntax,
                    new BoundAssignmentExpression(NoSyntax, variable, ParseExpression(parts[1])));
            }

            if (line.StartsWith("read ", System.StringComparison.Ordinal))
            {
                var variable = FindVariable(line.Substring(5));
                return new BoundExpressionStatement(NoSyntax,
                    BoundNodeFactory.Variable(NoSyntax, variable));
            }

            throw new System.ArgumentException($"Unknown statement DSL '{line}'");
        }

        private static BoundExpression ParseExpression(string text)
        {
            return BoundNodeFactory.Literal(NoSyntax, int.Parse(text, System.Globalization.CultureInfo.InvariantCulture));
        }

        private static VariableSymbol FindVariable(string name)
        {
            if (!Variables.TryGetValue(name, out var variable))
            {
                throw new System.ArgumentException($"Unknown variable '{name}'");
            }

            return variable;
        }

        private static readonly System.Collections.Generic.Dictionary<string, VariableSymbol> Variables =
            new System.Collections.Generic.Dictionary<string, VariableSymbol>();

        private static ParameterSymbol OutParam(string name)
        {
            var parameter = new ParameterSymbol(name, TypeSymbol.Int32, 0, isOut: true);
            Variables[name] = parameter;
            return parameter;
        }

        [Fact]
        public void OutParameterNotAssignedOnReturn_IsReported()
        {
            Variables.Clear();
            var v = OutParam("v");

            var diagnostics = Run(
                "return",
                v);

            Assert.Single(diagnostics);
            Assert.Contains("必须在返回前赋值", diagnostics[0].Message);
        }

        [Fact]
        public void AssignedOutParameter_IsNotReported()
        {
            Variables.Clear();
            var v = OutParam("v");

            var diagnostics = Run(
                "assign v 1\nreturn",
                v);

            Assert.Empty(diagnostics);
        }

        [Fact]
        public void UseOfUnassignedOutParameter_IsReported()
        {
            Variables.Clear();
            var v = OutParam("v");

            var diagnostics = Run(
                "read v\nassign v 1\nreturn",
                v);

            Assert.Single(diagnostics);
            Assert.Contains("使用了未赋值的 out 参数", diagnostics[0].Message);
        }

        [Fact]
        public void NoOutParameters_ReturnsEmpty()
        {
            Variables.Clear();
            var diagnostics = Run("return");
            Assert.Empty(diagnostics);
        }
    }
}
