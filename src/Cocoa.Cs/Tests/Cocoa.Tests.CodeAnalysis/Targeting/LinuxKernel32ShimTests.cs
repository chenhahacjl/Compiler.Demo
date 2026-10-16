using Cocoa.Targeting;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Targeting
{
    /// <summary>
    /// T3：Linux kernel32 → 运行时 rt 函数 shim 映射（extern 零导入重定向）。
    /// 语言层 `import kernel32.dll { GetTickCount/Sleep/Beep/ExitProcess }` 声明在 Linux 落到运行时实现；
    /// 其余符号返回 null（NativeBackend 编译期拒绝）。
    /// </summary>
    public class LinuxKernel32ShimTests
    {
        [Theory]
        [InlineData("GetTickCount", "TickCount")]
        [InlineData("GetTickCount64", "TickCount")]
        [InlineData("Sleep", "Sleep")]
        [InlineData("Beep", "Beep")]
        [InlineData("ExitProcess", "ExitProcess")]
        public void Linux_Shim_Maps_Supported_Kernel32(string symbol, string expected)
        {
            Assert.Equal(expected, PlatformDllResolver.LinuxKernel32Shim(symbol));
        }

        [Theory]
        [InlineData("GetTickCount2")]
        [InlineData("CreateFileW")]
        [InlineData("ReadFile")]
        [InlineData("VirtualAlloc")]
        [InlineData("MessageBox")]
        public void Linux_Shim_Unsupported_IsNull(string symbol)
        {
            Assert.Null(PlatformDllResolver.LinuxKernel32Shim(symbol));
        }
    }
}