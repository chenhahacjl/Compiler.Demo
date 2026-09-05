using System;
using System.IO;
using System.Linq;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Syntax;
using Xunit;

namespace Cocoa.Tests.Compiler
{
    /// <summary>
    /// 阶段 6 Step E：跨库同名类型歧义（CS0104 式）——两个用户 `.coa` 公开同一全名类型时装载即报错，
    /// 单库引用不受影响（与 refcod 拓扑序联动：先序后检）。
    /// </summary>
    public class CodAmbiguousNameTests
    {
        private static (string Dir, string Lib1, string Lib2) BuildConflictLibs(string conflictNamespace, string conflictName)
        {
            var dir = Path.Combine(Path.GetTempPath(), "coa-conflict-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            string WriteLib(string libName, string value)
            {
                var source = $@"
namespace {conflictNamespace}
{{
    public class {conflictName}
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

            return (dir, Write("LibOne", "1"), Write("LibTwo", "2"));
        }

        [Fact]
        public void Same_Type_Across_Two_Libraries_Reports_Ambiguity_On_Load()
        {
            var (_, lib1, lib2) = WriteConflictPrepare();

            var exception = Assert.Throws<InvalidDataException>(() =>
            {
                Compilation.Create(new[] { lib1, lib2 }, SyntaxTree.Parse("function Main(): i32 { return 0 }"));
            });
            Assert.Contains("歧义", exception.Message);
            Assert.Contains("LibOne", exception.Message);
            Assert.Contains("LibTwo", exception.Message);
        }

        [Fact]
        public void Single_Library_Reference_Binds_Normally()
        {
            var (_, lib1, _) = WriteConflictPrepare();
            var compilation = Compilation.Create(new[] { lib1 }, SyntaxTree.Parse(@"
using Shared

function Main(): i32
{
    let c = new Conflict()
    return c.Value()
}
"));
            Assert.False(compilation.EmitCocoa("app", Path.Combine(Path.GetTempPath(), "app-" + Guid.NewGuid().ToString("N") + ".coa")).HasErrors());
        }
    }
}