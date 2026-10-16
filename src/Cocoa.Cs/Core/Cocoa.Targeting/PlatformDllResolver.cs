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
    }
}