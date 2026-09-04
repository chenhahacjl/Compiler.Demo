using System;
using System.Collections.Generic;
using System.Linq;
using Cocoa.CodeGen.Native.Assembler;
using Cocoa.CodeGen.Native.Assembler.X64;
using Cocoa.CodeGen.PE;

using Cocoa.CodeAnalysis;


using Cocoa.CodeGen.Native.Lir;

namespace Cocoa.CodeGen.Native
{
    /// <summary>
    /// IR 到 IAssembler 的映射。寄存器分配策略：每个虚拟寄存器 → 函数帧内唯一栈槽
    /// （slot k @ [rbp - 16 - slotSize*k]），物理寄存器（eax/ecx/edx…）仅作瞬时运算载体。
    /// 帧布局、参数传递、TEB 栈限检查、x64 16 字节对齐与现有 NativeCodeEmitter 完全一致。
    /// </summary>
    internal sealed partial class LirToAssembler
    {
        private void LoadOperand(X64Register reg, LirOperand operand, X64Size size)
        {
            if (operand.Kind == LirOperandKind.Constant)
            {
                _a.Mov(size == X64Size.Qword ? X64Size.Qword : X64Size.Dword, reg, (int)operand.Imm);
            }
            else
            {
                LoadSlot(reg, operand.Register!, RegisterSize(operand.Register!));
            }
        }

        private void LoadSlot(X64Register reg, LirVirtualRegister register, int size)
        {
            var operand = new X64MemoryOperand(X64Register.RBP, GetSlotOffset(register));
            _a.Mov(ToSize(size), reg, operand);
        }

        private void StoreSlot(LirVirtualRegister register, X64Register eax)
        {
            var operand = new X64MemoryOperand(X64Register.RBP, GetSlotOffset(register));
            _a.Mov(ToSize(RegisterSize(register)), operand, eax);
        }

        /// <summary>把 double 槽的 64 位位模式装入 XMM 寄存器（x86 槽宽 4：高 dword 在低槽位；x64 槽宽 8：槽内 +4）。
        /// single=true 时按 4 字节 float 位模式 movss 直读（6e-M21 Phase 5b）。</summary>
        private void LoadSlotXmm(X64Register xmm, LirVirtualRegister register, bool single = false)
        {
            var slot = GetSlotOffset(register);
            if (single)
            {
                _a.Movss(xmm, new X64MemoryOperand(X64Register.RBP, slot));
                return;
            }

            var hi = slot + (_isX64 ? 4 : -4);
            _a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.RBP, slot));
            _a.MovdGprToXmm(xmm, X64Register.EAX);
            _a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.RBP, hi));
            _a.Pinsrd(xmm, X64Register.EAX, 1);
        }

        /// <summary>把 XMM 寄存器的 64 位位模式存入 double 槽（pextrd + movd 拆分，两架构通用）。
        /// single=true 时按 4 字节 float 位模式 movss 直写。</summary>
        private void StoreSlotXmm(LirVirtualRegister register, X64Register xmm, bool single = false)
        {
            var slot = GetSlotOffset(register);
            if (single)
            {
                _a.Movss(new X64MemoryOperand(X64Register.RBP, slot), xmm);
                return;
            }

            var hi = slot + (_isX64 ? 4 : -4);
            _a.Pextrd(X64Register.EAX, xmm, 1);
            _a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, hi), X64Register.EAX);
            _a.MovdXmmToGpr(X64Register.EAX, xmm);
            _a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, slot), X64Register.EAX);
        }

        private int GetSlotOffset(LirVirtualRegister register)
        {
            var slot = _slots[register];
            return -16 - _slotSize * slot;
        }

        private int GetLabel(int irLabelId)
        {
            if (!_asmLabelCache.TryGetValue(irLabelId, out var label))
            {
                label = _a.CreateLabel();
                _asmLabelCache.Add(irLabelId, label);
            }

            return label;
        }

        private int GetFunctionLabel(LirFunction function) => _functionLabels[function];

        private static X64Size ToSize(int byteSize) => byteSize switch
        {
            8 => X64Size.Qword,
            2 => X64Size.Word,
            _ => X64Size.Dword,
        };

        private static X64CondCode MapCond(LirCond cond)
        {
            switch (cond)
            {
                case LirCond.Equal: return X64CondCode.Equal;
                case LirCond.NotEqual: return X64CondCode.NotEqual;
                case LirCond.Less: return X64CondCode.Less;
                case LirCond.LessOrEqual: return X64CondCode.LessOrEqual;
                case LirCond.Greater: return X64CondCode.Greater;
                case LirCond.GreaterOrEqual: return X64CondCode.GreaterOrEqual;
                case LirCond.Below: return X64CondCode.Below;
                case LirCond.BelowOrEqual: return X64CondCode.BelowOrEqual;
                case LirCond.Above: return X64CondCode.Above;
                case LirCond.AboveOrEqual: return X64CondCode.AboveOrEqual;
                case LirCond.Parity: return X64CondCode.Parity;
                case LirCond.NoParity: return X64CondCode.NoParity;
                default:
                    throw new Exception($"Unknown IR cond: {cond}");
            }
        }
    }
}
