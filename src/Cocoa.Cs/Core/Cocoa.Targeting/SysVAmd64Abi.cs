namespace Cocoa.Targeting
{
    /// <summary>
    /// System V AMD64 ABI（Linux x64）：
    /// 参数寄存器 rdi,rsi,rdx,rcx,r8,r9（6 个），无 shadow space；槽 8 字节；参数区偏移 16；栈限检查预留。
    /// 数据待 T3 ELF 后端核对；寄存器序符合 SysV 规范。
    /// </summary>
    public sealed class SysVAmd64Abi : IAbiDescriptor
    {
        public static readonly SysVAmd64Abi Instance = new();

        public AbiRegister[] ArgumentRegisters { get; } =
        {
            AbiRegister.Rdi, AbiRegister.Rsi, AbiRegister.Rdx, AbiRegister.Rcx, AbiRegister.R8, AbiRegister.R9,
        };

        public int SlotSize => 8;
        public int ParamOffset => 16;
        public int StackLimitOffset => 0;
        public bool RequiresStackAlign => true;
        public int ArgumentRegisterCount => 6;
        public int SlotBytes => 8;
        public bool RequiresStackCheck => false;
        public int SysCallRegisterCount => 6;
    }
}
