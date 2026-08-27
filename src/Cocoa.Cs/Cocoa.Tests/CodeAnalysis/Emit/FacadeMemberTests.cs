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
            // native 路径由 CLI e2e（tools/build-sdk 后手动/冒烟）覆盖；此处锁 IL 后端回归
            var (exitCode, stdout) = CodeAnalysis.Emit.IL.IlE2eTests.EmitAndRun(Source, "FacadeToString");
            Assert.Equal(0, exitCode);
            Assert.Contains("42", stdout);
            Assert.Contains("9000000000", stdout);
            Assert.Contains("3.5", stdout);
            Assert.Contains("True", stdout);
            Assert.Contains("A", stdout);
            Assert.Contains("200", stdout);
        }

        [Fact]
        public void Facade_ParseAndCompareTo_Il()
        {
            var source = @"using System

function Main()
{
    var a = Int32.Parse(""123"")
    var b = Int64.Parse(""-456"")
    Console.WriteLine(a)
    Console.WriteLine(b)
    Console.WriteLine(a.CompareTo(100))
    Console.WriteLine(a.CompareTo(200))
    Console.WriteLine(Char.IsDigit('5'))
    Console.WriteLine(Char.ToUpper('q'))
    Console.WriteLine(u8.MaxValue)
    Console.WriteLine(i32.MinValue)
}";
            var (exitCode, stdout) = CodeAnalysis.Emit.IL.IlE2eTests.EmitAndRun(source, "FacadeParseCmp");
            Assert.Equal(0, exitCode);
            Assert.Contains("123", stdout);
            Assert.Contains("-456", stdout);
            Assert.Contains("1", stdout);
            Assert.Contains("-1", stdout);
            Assert.Contains("True", stdout);
            Assert.Contains("Q", stdout);
            Assert.Contains("255", stdout);
            Assert.Contains("-2147483648", stdout);
        }

        [Fact]
        public void Facade_NumericConstants_AllTypes_Il()
        {
            var source = @"using System

function Main()
{
    Console.WriteLine(u16.MaxValue)
    Console.WriteLine(u16.MinValue)
    Console.WriteLine(i16.MaxValue)
    Console.WriteLine(i16.MinValue)
    Console.WriteLine(u32.MaxValue)
    Console.WriteLine(i8.MaxValue)
    Console.WriteLine(i8.MinValue)
    Console.WriteLine(u64.MaxValue)
    Console.WriteLine(f32.MaxValue)
    Console.WriteLine(char.MaxValue)
}";
            var (exitCode, stdout) = CodeAnalysis.Emit.IL.IlE2eTests.EmitAndRun(source, "FacadeNumericConstants");
            Assert.Equal(0, exitCode);
            Assert.Contains("65535", stdout);
            Assert.Contains("0", stdout);
            Assert.Contains("32767", stdout);
            Assert.Contains("-32768", stdout);
            Assert.Contains("4294967295", stdout);
            Assert.Contains("127", stdout);
            Assert.Contains("-128", stdout);
            Assert.Contains("18446744073709551615", stdout);
            Assert.Contains("3.4028235", stdout);
        }

        [Fact]
        public void Facade_PrimitiveMembers_BclRedirect_Il()
        {
            var source = @"using System

function Main()
{
    var a = 5
    Console.WriteLine(a.Equals(5))
    Console.WriteLine(a.Equals(6))
    Console.WriteLine(a.GetHashCode())
    var r: i32 = 0
    var ok = Int32.TryParse(""123"", out r)
    Console.WriteLine(ok)
    Console.WriteLine(r)
    Console.WriteLine(Double.IsNaN(1.0))
    Console.WriteLine(Double.IsNaN(Double.NaN))
    var u: u64 = 0
    var ok2 = UInt64.TryParse(""18446744073709551615"", out u)
    Console.WriteLine(ok2)
    Console.WriteLine(u)
    Console.WriteLine(Double.Parse(""3.14""))
    Console.WriteLine(Char.IsDigit('7'))
}";
            var (exitCode, stdout) = CodeAnalysis.Emit.IL.IlE2eTests.EmitAndRun(source, "FacadePrimitiveMembers");
            Assert.Equal(0, exitCode);
            Assert.Contains("True", stdout);
            Assert.Contains("False", stdout);
            Assert.Contains("5", stdout);
            Assert.Contains("123", stdout);
            Assert.Contains("18446744073709551615", stdout);
            Assert.Contains("3.14", stdout);
        }
    }
}
