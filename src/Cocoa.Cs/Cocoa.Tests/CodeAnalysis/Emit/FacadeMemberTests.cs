using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Emit
{
    /// <summary>
    /// 6e-M19 M2-b：基元 facade（System.Int32 等）成员面 e2e——
    /// receiver.ToString() 经编译期降级为静态容器调用，三后端输出一致。
    /// </summary>
    public class FacadeMemberTests
    {
        private const string Source = @"using System

function Main()
{
    var n: i32 = 42
    var big: i64 = 9000000000
    var d: f64 = 3.5
    var b: bool = true
    var c: char = 'A'
    var y: u8 = 200
    Console.WriteLine(n.ToString())
    Console.WriteLine(big.ToString())
    Console.WriteLine(d.ToString())
    Console.WriteLine(b.ToString())
    Console.WriteLine(c.ToString())
    Console.WriteLine(y.ToString())
}";

        [Fact]
        public void Facade_ToString_AllPrimitiveTypes_Il()
        {
            // native 路径由 CLI e2e（tools/build-stdlib 后手动/冒烟）覆盖；此处锁 IL 后端回归
            var (exitCode, stdout) = CodeAnalysis.Emit.IL.IlE2eTests.EmitAndRun(Source, "FacadeToString");
            Assert.Equal(0, exitCode);
            Assert.Contains("42", stdout);
            Assert.Contains("9000000000", stdout);
            Assert.Contains("3.5", stdout);
            Assert.Contains("True", stdout);
            Assert.Contains("A", stdout);
            Assert.Contains("200", stdout);
        }
    }
}
