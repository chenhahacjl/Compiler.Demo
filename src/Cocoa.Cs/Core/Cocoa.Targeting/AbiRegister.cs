namespace Cocoa.Targeting
{
    /// <summary>
    /// 物理寄存器抽象标识（ABI 无关，描述符用）：由后端（LirToAssembler）映射到具体 ISA 的寄存器类型。
    /// 涵盖 x64/x86 常见参数/返回值寄存器；仅列出调用约定涉及者。
    /// </summary>
    public enum AbiRegister
    {
        Rax,
        Rcx,
        Rdx,
        R8,
        R9,
        Rdi,
        Rsi,
        Eax,
        Ecx,
        Edx,
        Esi,
        Edi,
    }
}
