namespace Cocoa.Targeting
{
    /// <summary>
    /// CPU 架构（公开属性枚举，决定 PE 形态与 COR20 flags）：
    /// AnyCPU 仅 IL 合法（PE32 + ILONLY，运行时决定位数）；x86/x64 为具体架构。
    /// native 后端拒绝 AnyCPU（native 需定 ISA）。
    /// </summary>
    public enum Cpu
    {
        AnyCPU,
        X86,
        X64,
    }
}
