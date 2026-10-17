namespace Cocoa.Targeting
{
    /// <summary>
    /// CPU 架构（单一枚举，统一 IL/native 目标形态）：
    /// AnyCPU 仅 IL 合法（PE32 + ILONLY，运行时决定位数）；x86/x64 为具体架构。
    /// native 后端拒绝 AnyCPU（native 需定 ISA）。
    /// </summary>
    public enum Architecture
    {
        AnyCPU,
        X64,
        X86,
    }
}
