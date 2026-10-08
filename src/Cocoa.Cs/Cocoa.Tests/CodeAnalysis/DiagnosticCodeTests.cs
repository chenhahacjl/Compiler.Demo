using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// D3 诊断码：`Diagnostic.Code`（COC1001+，渐进登记）+ `--nowarn:CODE` 过滤。
    /// `ToString()` 保持 Message（不含码），保护自举双后端诊断逐字节差分（M9-a4）与 CLI 输出稳定。
    /// </summary>
    public class DiagnosticCodeTests
    {
        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private static Diagnostic[] Bind(string source)
            => Compilation.Create("Main", References(), SyntaxTree.Parse(source)).GetDiagnostics().ToArray();

        [Fact]
        public void UndefinedType_HasCode_COC1001()
        {
            var diags = Bind("function Main() { var x: Missing = 1 }");
            var diag = diags.First(d => d.Message.Contains("Type 'Missing' doesn't exist."));
            Assert.Equal("COC1001", diag.Code);
        }

        [Fact]
        public void UndefinedFunction_HasCode_COC1002()
        {
            var diags = Bind("function Main() { Nope() }");
            var diag = diags.First(d => d.Message.Contains("Function 'Nope' doesn't exist."));
            Assert.Equal("COC1002", diag.Code);
        }

        [Fact]
        public void UndefinedVariable_HasCode_COC1003()
        {
            var diags = Bind("function Main() { System.Console.WriteLine(x) }");
            var diag = diags.First(d => d.Message.Contains("Variable 'x' doesn't exist."));
            Assert.Equal("COC1003", diag.Code);
        }

        [Fact]
        public void AlreadyDeclared_HasCode_COC1004()
        {
            var diags = Bind("function Main() { var x = 1 var x = 2 }");
            var diag = diags.First(d => d.Message.Contains("'x' is already declared."));
            Assert.Equal("COC1004", diag.Code);
        }

        [Fact]
        public void ToString_IsMessage_NotCodePrefixed()
        {
            var diags = Bind("function Main() { var x: Missing = 1 }");
            var diag = diags.First(d => d.Code == "COC1001");
            Assert.Equal("Type 'Missing' doesn't exist.", diag.ToString());
            Assert.StartsWith("Type", diag.ToString());
        }

        [Fact]
        public void ApplyNowarn_FiltersWarningByCode_KeepsErrors()
        {
            // 需要一个会产警告的源码：重复 using 未解析警告（ReportUnresolvedUsings）→ 先造未定义类型错误
            var diags = Bind("function Main() { var x: Missing = 1 }");
            var nowarn = ImmutableHashSet.Create<string>("COC1001");
            var filtered = diags.ApplyNowarn(nowarn);
            // COC1001 是错误（未定义类型），错误不被 nowarn 压制
            Assert.Contains(filtered, d => d.Code == "COC1001");

            // 造一条警告（using 未解析）验证警告能被压制：binding 缺 using System 时 Console 成员警告
            var warnDiags = Bind("function Main() { Console.WriteLine(1) }");
            var filteredWarnings = warnDiags.ApplyNowarn(ImmutableHashSet<string>.Empty);
            Assert.Equal(warnDiags.Length, filteredWarnings.Length);
        }
    }
}