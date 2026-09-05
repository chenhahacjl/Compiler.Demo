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

            // 真用途：动态链接消费（linkCodDynamically）——实例化库类、调用实例方法并产出可执行 dll 引用
            var compilation = Compilation.Create(new[] { lib1 }, linkCodDynamically: true,
                SyntaxTree.Parse(@"
using Shared
function Main(): i32
{
    let c = new Conflict()
    return c.Value()
}
"));
            var exePath = Path.Combine(dir, "app.exe");
            var diagnostics = compilation.Emit("app-exe",
                new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location, lib1 },
                exePath, Cocoa.Targeting.IlTarget.Parse("net9.0"));
            Assert.False(diagnostics.HasErrors(),
                "单库实例类消费应无诊断：" + string.Join(" | ", diagnostics.Select(d => d.Message)));
            Assert.True(File.Exists(exePath));
        }
    }
}