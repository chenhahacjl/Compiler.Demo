namespace Cocoa.Targeting
{
    /// <summary>
    /// Windows x86 ABI（stdcall/cdecl 兼容，ecx,edx,esi,edi 参数寄存器，无 shadow space）：
    /// 槽 4 字节；参数区偏移 8；TEB 栈限 GS:0x08。数据照抄 LirToAssembler 现行为。
    /// </summary>
    public sealed class WinX86Abi : IAbiDescriptor
    {
        public static readonly WinX86Abi Instance = new();

        public AbiRegister[] ArgumentRegisters { get; } =
        {
            AbiRegister.Ecx, AbiRegister.Edx, AbiRegister.Esi, AbiRegister.Edi,
        };

        public int SlotSize => 4;
        public int ParamOffset => 8;
        public int StackLimitOffset => 0x08;
        public bool RequiresStackAlign => false;
        public int ArgumentRegisterCount => 4;
        public int SlotBytes => 4;
        public bool RequiresStackCheck => true;
        public int SysCallRegisterCount => 3;
        public int FloatArgRegisterCount => 0;
    }
}
