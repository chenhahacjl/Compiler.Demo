using System;

namespace Cocoa.Targeting
{
    /// <summary>
    /// ABI 描述符：把调用约定（参数寄存器序/影子空间/栈对齐/参数区布局/栈限检查/返回值槽）数据化，
    /// 供 native 后端（LirToAssembler）经此展开，消除 LIR/汇编器对具体平台调用约定的硬编码。
    /// WinX64Abi（Windows x64 fastcall+shadow space）/ SysVAmd64Abi（Linux x64，T3）为两实现。
    /// </summary>
    public interface IAbiDescriptor
    {
        /// <summary>参数寄存器序（ordinal → 物理寄存器）。下标 0..3 对应 SetArg/InitRegArg 的 ordinal。</summary>
        AbiRegister[] ArgumentRegisters { get; }

        /// <summary>槽位大小（x64=8，x86=4）。</summary>
        int SlotSize { get; }

        /// <summary>参数区相对 RBP 的起始偏移（x64=16，x86=8）。</summary>
        int ParamOffset { get; }

        /// <summary>TEB 栈限字段的 GS 段偏移（x64=0x10，x86=0x08）。</summary>
        int StackLimitOffset { get; }

        /// <summary>调用是否需 16 字节栈对齐（x64 需；x86 非）。</summary>
        bool RequiresStackAlign { get; }

        /// <summary>参数寄存器数（x64=4；x86=3，第 4 参走栈）。</summary>
        int ArgumentRegisterCount { get; }

        /// <summary>调用时每参的栈槽字节数（shadow space / 压栈间隔；x64=8，x86=4）。</summary>
        int SlotBytes { get; }

        /// <summary>入口是否需 TEB 栈限检查（x64 需；x86 由 prolog 差异决定）。</summary>
        bool RequiresStackCheck { get; }

        /// <summary>SysCall 的寄存器序号（对应 ArgumentRegisters；当前第 5 参恒 0 语义由调用方处理）。</summary>
        int SysCallRegisterCount { get; }
    }
}
