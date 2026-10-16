namespace Cocoa.Targeting
{
    /// <summary>
    /// Windows x64 ABI（fastcall + shadow space + 16 字节栈对齐）：
    /// 参数寄存器 rcx,rdx,r8,r9；每参 8 字节栈槽；入口 TEB 栈限检查（GS:0x10）。
    /// 数据照抄 LirToAssembler 现行为，保证 Windows 产物逐字节不变。
    /// </summary>
    public sealed class WinX64Abi : IAbiDescriptor
    {
        public static readonly WinX64Abi Instance = new();

        public AbiRegister[] ArgumentRegisters { get; } =
        {
            AbiRegister.Rcx, AbiRegister.Rdx, AbiRegister.R8, AbiRegister.R9,
        };

        public int SlotSize => 8;
        public int ParamOffset => 16;
        public int StackLimitOffset => 0x10;
        public bool RequiresStackAlign => true;
        public int ArgumentRegisterCount => 4;
        public int SlotBytes => 8;
        public bool RequiresStackCheck => true;
        public int SysCallRegisterCount => 4;
        public int FloatArgRegisterCount => 4;
    }
}
