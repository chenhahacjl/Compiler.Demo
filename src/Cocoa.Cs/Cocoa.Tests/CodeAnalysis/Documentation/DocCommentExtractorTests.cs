using System.Collections.Immutable;
using System.Linq;
using Cocoa.CodeAnalysis.Documentation;
using Cocoa.CodeAnalysis.Syntax;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Documentation
{
    public class DocCommentExtractorTests
    {
        private static DocComment? Extract(string code, out ImmutableArray<string> warnings)
        {
            var tokens = SyntaxTree.ParseTokens(code, includeEndOfFile: false);
            var leading = tokens[0].LeadingTrivia;
            return DocCommentExtractor.Extract(leading, out warnings);
        }

        private static DocComment? Extract(string code) => Extract(code, out _);

        [Fact]
        public void Extract_MultiLine_SummaryParamReturns()
        {
            var code = "/// <summary>计算两数之和。</summary>\n" +
                       "/// <param name=\"a\">第一个加数。</param>\n" +
                       "/// <param name=\"b\">第二个加数。</param>\n" +
                       "/// <returns>两数之和。</returns>\n" +
                       "x";

            var doc = Extract(code);

            Assert.NotNull(doc);
            Assert.Equal("计算两数之和。", doc!.Summary);
            Assert.Equal("两数之和。", doc.Returns);
            Assert.Equal(2, doc.Parameters.Length);
            Assert.Equal("a", doc.Parameters[0].Name);
            Assert.Equal("第一个加数。", doc.Parameters[0].Description);
            Assert.Equal("b", doc.Parameters[1].Name);
        }

        [Fact]
        public void Extract_MultiLine_OpenCloseAcrossLines()
        {
            var code = "/// <summary>\n" +
                       "/// 第一行\n" +
                       "/// 第二行\n" +
                       "/// </summary>\n" +
                       "x";

            var doc = Extract(code);

            Assert.NotNull(doc);
            Assert.Equal("第一行\n第二行", doc!.Summary);
        }

        [Fact]
        public void Extract_Remarks()
        {
            var code = "/// <summary>摘要</summary>\n/// <remarks>备注内容</remarks>\nx";

            var doc = Extract(code);

            Assert.NotNull(doc);
            Assert.Equal("摘要", doc!.Summary);
            Assert.Equal("备注内容", doc.Remarks);
        }

        [Fact]
        public void Extract_UnknownTag_PreservedInRawText()
        {
            var code = "/// <summary>摘要</summary>\n/// <example>示例</example>\nx";

            var doc = Extract(code);

            Assert.NotNull(doc);
            Assert.Contains("<example>示例</example>", doc!.RawText);
        }

        [Fact]
        public void Extract_StopsAtNonDocComment()
        {
            var code = "/// <summary>摘要</summary>\n// 普通注释\n/// <remarks>不应包含</remarks>\nx";

            var doc = Extract(code);

            Assert.NotNull(doc);
            Assert.Equal("摘要", doc!.Summary);
            Assert.Null(doc.Remarks);
        }

        [Fact]
        public void Extract_NoDoc_ReturnsNull()
        {
            Assert.Null(Extract("// 普通注释\nx"));
            Assert.Null(Extract("x"));
        }

        [Fact]
        public void Extract_FourSlashes_TreatedAsOrdinaryComment()
        {
            // //// 是普通单行注释，不产生文档
            Assert.Null(Extract("//// not a doc\nx"));
        }

        [Fact]
        public void Extract_ParamMissingName_ReportsWarning()
        {
            var code = "/// <param>缺少名称</param>\nx";

            var doc = Extract(code, out var warnings);

            Assert.NotNull(doc);
            Assert.Contains(warnings, w => w.Contains("<param>"));
        }

        [Fact]
        public void Extract_UnclosedTag_ReportsWarning()
        {
            var code = "/// <summary>未闭合\nx";

            Extract(code, out var warnings);

            Assert.Contains(warnings, w => w.Contains("未闭合"));
        }
    }
}
