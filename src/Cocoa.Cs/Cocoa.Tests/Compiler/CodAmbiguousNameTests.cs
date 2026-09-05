using System;
using System.IO;
using System.Linq;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Syntax;
using Xunit;

namespace Cocoa.Tests.Compiler
{
    /// <summary>
    /// 阶段 6 Step E：跨库同名类型歧义（CS0104 式）——两个用户库公开同一全名类型时装载即报错；
    /// 单库引用不受影响（与 refcod 拓扑序联动：先拓扑排序后做同名检测）。
    /// </summary>
    public class CodAmbiguousNameTests
    {
        private static string WriteLib(string dir, string libName, string value)
        {
            var source = $@"
namespace Shared
{{
    public class Conflict
    {{
        private _v: i32

        public function Value(): i32
        {{
            return {value}
        }}
    }}
}}
";
            var path = Path.Combine(dir, libName + ".coa");
            var compilation = Compilation.Create(SyntaxTree.Parse(source));
            var diagnostics = compilation.EmitCocoa(libName, path);
            Assert.False(diagnostics.HasErrors());
            return path;
        }

        private static string NewDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "coa-amb-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void Same_Type_Across_Two_Libraries_Reports_Ambiguity_On_Load()
        {
            var dir = NewDir();
            var lib1 = WriteLib(dir, "LibOne", "1");
            var lib2 = WriteLib(dir, "LibTwo", "2");

            var exception = Assert.Throws<System.IO.InvalidDataException>(() =>
                Compilation.Create(new[] { lib1, lib2 },
                    SyntaxTree.Parse("function Main(): i32 { return 0 }")));
            Assert.Contains("歧义", exception.Message);
            Assert.Contains("LibOne", exception.Message);
            Assert.Contains("LibTwo", exception.Message);
        }

        [Fact]
        public void Single_Library_Reference_Binds_Normally()
        {
            var dir = NewDir();
            var lib1 = WriteLib(dir, "LibOne", "1");

            var compilation = Compilation.Create(new[] { lib1 },
                SyntaxTree.Parse("function Main(): i32 { return 0 }"));
            // 单库装载无歧义，且类型经全局命名空间树可见
            var conflict = compilation.GetTypeByMetadataName("Shared.Conflict");
            Assert.NotNull(conflict);
        }
    }
}