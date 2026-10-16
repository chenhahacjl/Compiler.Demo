namespace Cocoa.Targeting
{
    /// <summary>
    /// 平台 DLL 解析器：把 `import <dll>` 逻辑名解析为当前 TargetOS 的实际库名。
    /// 现状（T0c 入口）：Windows 恒等（kernel32 → kernel32.dll）；Linux（libc）为 T3 预留。
    /// 消费端（native LirToAssembler 导入表 / IL MetadataBuilder ModuleRef）经此统一取库名，消除硬编码。
    /// </summary>
    public static class PlatformDllResolver
    {
        /// <summary>kernel32 逻辑名 → 平台库（Windows 恒等；Linux 预留 libc 解析）。</summary>
        public static string Kernel32 => Resolve("kernel32.dll", TargetOS.Windows);

        /// <summary>逻辑名解析：Windows 恒等映射，Linux 映射 libc 族（T3 实施）。未实现组合返回原逻辑名。</summary>
        public static string Resolve(string logicalDll, TargetOS os)
        {
            return os switch
            {
                TargetOS.Windows => logicalDll,
                // T3：Linux glibc 动态链接——kernel32 语义 → libc 家族（close/open/read/write…）。
                // 当前无 Linux 实现，返回原逻辑名（行为与现状一致，不报错自欺）。
                TargetOS.Linux => logicalDll,
                _ => logicalDll,
            };
        }

        /// <summary>
        /// T3：Linux kernel32 符号 → 运行时 rt 函数名 shim（零导入重定向）。
        /// 语言层 `import kernel32.dll { ... }` 声明与运行时既有 syscall 实现同语义（签名一致）时，
        /// extern 调用直接落到运行时函数（rt:TickCount/rt:Sleep/rt:Beep/rt:ExitProcess），不进导入表。
        /// 未命中返回 null（Linux 侧仍编译期拒绝）。
        /// </summary>
        public static string? LinuxKernel32Shim(string symbol)
        {
            return symbol switch
            {
                "GetTickCount" or "GetTickCount64" => "TickCount",
                "Sleep" => "Sleep",
                "Beep" => "Beep",
                "ExitProcess" => "ExitProcess",
                _ => null,
            };
        }
    }
}