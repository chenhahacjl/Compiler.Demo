using System.Linq;
using Cocoa.CodeAnalysis;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Documentation
{
    /// <summary>
    /// 6e-M24：跨程序集文档分发凭证——重建 `libs/System.Core.coa` 后，stdlib 符号的
    /// <c>DocumentationText</c> 应从 `.coa` docs 段回填（无需源码）。
    /// </summary>
    public class SystemLibraryDocumentationTests
    {
        [Fact]
        public void StdlibSymbols_LoadWithDocumentation()
        {
            SystemLibrary.Reset();
            var core = SystemLibrary.Load().Single(l => l.Name.StartsWith("System.Core"));

            var math = core.Classes.Single(c => c.Name == "Math");
            Assert.Contains(math.Methods, m => m.Name == "Abs" && m.DocumentationText != null);
            Assert.Contains("absolute", math.Methods.First(m => m.Name == "Abs").DocumentationText);

            var console = core.Classes.Single(c => c.Name == "Console");
            Assert.Contains(console.Methods, m => m.Name == "WriteLine" && m.DocumentationText != null);

            var int32 = core.Classes.Single(c => c.Name == "Int32");
            Assert.Contains(int32.Methods, m => m.Name == "TryParse" && m.DocumentationText != null);
        }
    }
}
