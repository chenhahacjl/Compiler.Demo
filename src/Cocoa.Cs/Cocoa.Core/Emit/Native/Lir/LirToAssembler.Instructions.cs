using System;
using System.Collections.Generic;
using System.Linq;
using Cocoa.CodeAnalysis.Emit.Native.Assembler;
using Cocoa.CodeAnalysis.Emit.Native.Assembler.X64;
using Cocoa.CodeAnalysis.Emit.Native.PEFile;

namespace Cocoa.CodeAnalysis.Emit.Native.Lir
{
    /// <summary>
    /// IR 到 IAssembler 的映射。寄存器分配策略：每个虚拟寄存器 → 函数帧内唯一栈槽
    /// （slot k @ [rbp - 16 - slotSize*k]），物理寄存器（eax/ecx/edx…）仅作瞬时运算载体。
    /// 帧布局、参数传递、TEB 栈限检查、x64 16 字节对齐与现有 NativeCodeEmitter 完全一致。
    /// </summary>
    internal sealed partial class LirToAssembler
    {
        private void EmitInstruction(LirInstruction instruction)
        {
            switch (instruction.OpCode)
            {
                case LirOpCode.Const:
                    EmitConst(instruction);
                    break;
                case LirOpCode.Mov:
                    EmitMov(instruction);
                    break;
                case LirOpCode.Load:
                    EmitLoad(instruction);
                    break;
                case LirOpCode.LoadSlotField:
                    EmitLoadSlotField(instruction);
                    break;
                case LirOpCode.StoreSlotField:
                    EmitStoreSlotField(instruction);
                    break;
                case LirOpCode.Store:
                    EmitStore(instruction);
                    break;
                case LirOpCode.LeaData:
                    EmitLeaData(instruction);
                    break;
                case LirOpCode.Lea:
                    EmitLea(instruction);
                    break;
                case LirOpCode.LeaSlot:
                    EmitLeaSlot(instruction);
                    break;
                case LirOpCode.LeaVar:
                    EmitLeaVar(instruction);
                    break;
                case LirOpCode.InitParam:
                    EmitInitParam(instruction);
                    break;
                case LirOpCode.InitRegArg:
                    EmitInitRegArg(instruction);
                    break;
                case LirOpCode.Add:
                case LirOpCode.Sub:
                case LirOpCode.Imul:
                case LirOpCode.And:
                case LirOpCode.Or:
                case LirOpCode.Xor:
                    EmitBinary(instruction);
                    break;
                case LirOpCode.Idiv:
                    EmitIdiv(instruction);
                    break;
                case LirOpCode.Irem:
                    EmitIrem(instruction);
                    break;
                case LirOpCode.Udiv:
                    EmitUdiv(instruction);
                    break;
                case LirOpCode.Urem:
                    EmitUrem(instruction);
                    break;
                case LirOpCode.Neg:
                case LirOpCode.Not:
                    EmitUnary(instruction);
                    break;
                case LirOpCode.Add64:
                case LirOpCode.Sub64:
                case LirOpCode.Imul64:
                case LirOpCode.And64:
                case LirOpCode.Or64:
                case LirOpCode.Xor64:
                    EmitBinary64(instruction);
                    break;
                case LirOpCode.Idiv64:
                    EmitIdiv64(instruction);
                    break;
                case LirOpCode.Irem64:
                    EmitIrem64(instruction);
                    break;
                case LirOpCode.Udiv64:
                    EmitUdiv64(instruction);
                    break;
                case LirOpCode.Urem64:
                    EmitUrem64(instruction);
                    break;
                case LirOpCode.Neg64:
                case LirOpCode.Not64:
                    EmitUnary64(instruction);
                    break;
                case LirOpCode.Shl64:
                case LirOpCode.Shr64:
                case LirOpCode.Sar64:
                    EmitShift64(instruction);
                    break;
                case LirOpCode.Cmp64:
                    EmitCmp64(instruction);
                    break;
                case LirOpCode.Shl:
                case LirOpCode.Shr:
                case LirOpCode.Sar:
                    EmitShift(instruction);
                    break;
                case LirOpCode.Cmp:
                    EmitCmp(instruction);
                    break;
                case LirOpCode.Setcc:
                    EmitSetcc(instruction);
                    break;
                case LirOpCode.Call:
                    EmitCall(instruction);
                    break;
                case LirOpCode.CallReg:
                    EmitCallReg(instruction);
                    break;
                case LirOpCode.ReserveArgs:
                    EmitReserveArgs(instruction);
                    break;
                case LirOpCode.FreeArgs:
                    EmitFreeArgs(instruction);
                    break;
                case LirOpCode.StoreArg:
                    EmitStoreArg(instruction);
                    break;
                case LirOpCode.SetArg:
                    EmitSetArg(instruction);
                    break;
                case LirOpCode.SetArg64:
                    EmitSetArg64(instruction);
                    break;
                case LirOpCode.FConst:
                    EmitFConst(instruction);
                    break;
                case LirOpCode.FMov:
                    EmitFMove(instruction);
                    break;
                case LirOpCode.FAdd:
                case LirOpCode.FSub:
                case LirOpCode.FMul:
                case LirOpCode.FDiv:
                    EmitFBinary(instruction);
                    break;
                case LirOpCode.FNeg:
                    EmitFNeg(instruction);
                    break;
                case LirOpCode.FSqrt:
                case LirOpCode.FFloor:
                case LirOpCode.FCeiling:
                case LirOpCode.FTruncate:
                case LirOpCode.FRound:
                    EmitFUnary(instruction);
                    break;
                case LirOpCode.FCmp:
                    EmitFCmp(instruction);
                    break;
                case LirOpCode.FCvtSI:
                    EmitFCvtSI(instruction);
                    break;
                case LirOpCode.FCvtSD:
                    EmitFCvtSD(instruction);
                    break;
                case LirOpCode.FCvtSI64:
                    EmitFCvtSI64(instruction);
                    break;
                case LirOpCode.FCvtSD64:
                    EmitFCvtSD64(instruction);
                    break;
                case LirOpCode.FCvtDS:
                    EmitFCvtDS(instruction);
                    break;
                case LirOpCode.FCvtSSD:
                    EmitFCvtSSD(instruction);
                    break;
                case LirOpCode.FCvtSI64U:
                    EmitFCvtSI64U(instruction);
                    break;
                case LirOpCode.Movsx64:
                    EmitMovsx64(instruction);
                    break;
                case LirOpCode.Movzx64:
                    EmitMovzx64(instruction);
                    break;
                case LirOpCode.Trunc64:
                    EmitTrunc64(instruction);
                    break;
                case LirOpCode.StoreRet:
                    EmitStoreRet(instruction);
                    break;
                case LirOpCode.StackCheck:
                    EmitStackCheck();
                    break;
                case LirOpCode.SysCall:
                    EmitSysCall(instruction);
                    break;
                case LirOpCode.Push:
                    EmitPush(instruction);
                    break;
                case LirOpCode.Pop:
                    EmitPop(instruction);
                    break;
                case LirOpCode.Nop:
                    _a.Nop();
                    break;
                case LirOpCode.SeqPoint:
                    break;
                default:
                    throw new Exception($"Unexpected IR opcode: {instruction.OpCode}");
            }
        }

        // ------------------------------------------------------------------
        // 数据移动
        // ------------------------------------------------------------------

        private void EmitConst(LirInstruction instruction)
        {
            if (RegisterSize(instruction.Dst!) == 8)
            {
                if (_isX64)
                {
                    _a.Mov(X64Register.RAX, instruction.A.Imm);
                }
                else
                {
                    // x86：64 位立即数拆低/高两个 dword 写入双槽
                    var slot = GetSlotOffset(instruction.Dst!);
                    var low = unchecked((int)instruction.A.Imm);
                    var high = unchecked((int)(instruction.A.Imm >> 32));
                    if (high == 0)
                    {
                        _a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, slot - 4), 0);
                        _a.Mov(X64Size.Dword, X64Register.EAX, low);
                        _a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, slot), X64Register.EAX);
                    }
                    else
                    {
                        _a.Mov(X64Size.Dword, X64Register.EAX, high);
                        _a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, slot - 4), X64Register.EAX);
                        _a.Mov(X64Size.Dword, X64Register.EAX, low);
                        _a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, slot), X64Register.EAX);
                    }

                    return;
                }
            }
            else
            {
                _a.Mov(X64Size.Dword, X64Register.EAX, (int)instruction.A.Imm);
            }

            StoreSlot(instruction.Dst!, X64Register.EAX);
        }

        private void EmitMov(LirInstruction instruction)
        {
            var srcSize = RegisterSize(instruction.A.Register!);
            var dstSize = RegisterSize(instruction.Dst!);
            if (!_isX64 && srcSize == 8 && dstSize == 8)
            {
                // x86 无 64 位通用寄存器：8 字节寄存器分两次 32 位搬运到目标双槽
                var srcSlot = GetSlotOffset(instruction.A.Register!);
                var dstSlot = GetSlotOffset(instruction.Dst!);
                _a.Mov(X64Size.Dword, X64Register.ECX, new X64MemoryOperand(X64Register.RBP, srcSlot));
                _a.Mov(X64Size.Dword, X64Register.EDX, new X64MemoryOperand(X64Register.RBP, srcSlot - 4));
                _a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, dstSlot), X64Register.ECX);
                _a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, dstSlot - 4), X64Register.EDX);
                return;
            }

            LoadSlot(X64Register.EAX, instruction.A.Register!, srcSize);
            StoreSlot(instruction.Dst!, X64Register.EAX);
        }

        private void EmitLoad(LirInstruction instruction)
        {
            var baseSize = RegisterSize(instruction.A.Register!);
            var baseReg = baseSize == 8 ? X64Register.RAX : X64Register.EAX;
            LoadSlot(baseReg, instruction.A.Register!, baseSize);
            var operand = new X64MemoryOperand(baseReg, instruction.Offset);

            if (!_isX64 && instruction.ByteSize == 8)
            {
                // x86 无 64 位通用寄存器：分两次 32 位搬运到目标双槽
                var dstSlot = GetSlotOffset(instruction.Dst!);
                _a.Mov(X64Size.Dword, X64Register.ECX, operand);
                _a.Mov(X64Size.Dword, X64Register.EDX, new X64MemoryOperand(baseReg, instruction.Offset + 4));
                _a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, dstSlot), X64Register.ECX);
                _a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, dstSlot - 4), X64Register.EDX);
                return;
            }

            if (instruction.ByteSize == 2)
            {
                _a.Movzx(X64Size.Word, X64Register.EAX, operand);
            }
            else if (instruction.ByteSize == 1)
            {
                _a.Movzx(X64Size.Byte, X64Register.EAX, operand);
            }
            else
            {
                _a.Mov(ToSize(instruction.ByteSize), X64Register.EAX, operand);
            }
            StoreSlot(instruction.Dst!, X64Register.EAX);
        }

        /// <summary>从 <paramref name="instruction.A"/> 的槽内存（而非其指向的地址）按偏移直接读取。用于取 double 槽的高 dword 等标量位模式，避免把值当指针解引用。</summary>
        private void EmitLoadSlotField(LirInstruction instruction)
        {
            var operand = new X64MemoryOperand(X64Register.RBP, GetSlotOffset(instruction.A.Register!) + instruction.Offset);
            if (instruction.ByteSize == 2)
            {
                _a.Movzx(X64Size.Word, X64Register.EAX, operand);
            }
            else if (instruction.ByteSize == 1)
            {
                _a.Movzx(X64Size.Byte, X64Register.EAX, operand);
            }
            else
            {
                _a.Mov(ToSize(instruction.ByteSize), X64Register.EAX, operand);
            }

            StoreSlot(instruction.Dst!, X64Register.EAX);
        }

        /// <summary>把 <paramref name="instruction.B"/> 的值写入 <paramref name="instruction.A"/> 的槽内存（而非其指向的地址）的偏移处。用于把 double 的低/高 dword 拼进槽。</summary>
        private void EmitStoreSlotField(LirInstruction instruction)
        {
            LoadSlot(X64Register.EAX, instruction.B.Register!, RegisterSize(instruction.B.Register!));
            var operand = new X64MemoryOperand(X64Register.RBP, GetSlotOffset(instruction.A.Register!) + instruction.Offset);
            _a.Mov(ToSize(instruction.ByteSize), operand, X64Register.EAX);
        }

        private void EmitStore(LirInstruction instruction)
        {
            var baseSize = RegisterSize(instruction.A.Register!);
            var baseReg = baseSize == 8 ? X64Register.RAX : X64Register.EAX;
            LoadSlot(baseReg, instruction.A.Register!, baseSize);
            var operand = new X64MemoryOperand(baseReg, instruction.Offset);

            if (!_isX64 && instruction.ByteSize == 8)
            {
                var srcSlot = GetSlotOffset(instruction.B.Register!);
                _a.Mov(X64Size.Dword, X64Register.ECX, new X64MemoryOperand(X64Register.RBP, srcSlot));
                _a.Mov(X64Size.Dword, X64Register.EDX, new X64MemoryOperand(X64Register.RBP, srcSlot - 4));
                _a.Mov(X64Size.Dword, operand, X64Register.ECX);
                _a.Mov(X64Size.Dword, new X64MemoryOperand(baseReg, instruction.Offset + 4), X64Register.EDX);
                return;
            }

            LoadSlot(X64Register.ECX, instruction.B.Register!, RegisterSize(instruction.B.Register!));
            _a.Mov(ToSize(instruction.ByteSize), operand, X64Register.ECX);
        }

        private void EmitLeaData(LirInstruction instruction)
        {
            var key = (string)instruction.A.Symbol!;
            _a.LeaRip(X64Register.RAX, _dataSymbols[key]);
            StoreSlot(instruction.Dst!, X64Register.RAX);
        }

        private void EmitLea(LirInstruction instruction)
        {
            var size = RegisterSize(instruction.A.Register!);
            var reg = size == 8 ? X64Register.RAX : X64Register.EAX;
            LoadSlot(reg, instruction.A.Register!, size);
            _a.Add(size == 8 ? X64Size.Qword : X64Size.Dword, reg, instruction.Offset);
            StoreSlot(instruction.Dst!, reg);
        }

        private void EmitLeaSlot(LirInstruction instruction)
        {
            // 指向帧底部的缓冲槽（远离返回地址；配合 EmitFunction 的 LeaSlot 缓冲空间）
            var offset = -_frameBytes + _slotSize;
            _a.Lea(X64Register.EAX, new X64MemoryOperand(X64Register.RBP, offset));
            StoreSlot(instruction.Dst!, X64Register.EAX);
        }

        /// <summary>LeaVar（6e-M23 R7）：dst = &amp;变量槽——byref 实参把调用方变量的帧槽地址传给被调方。</summary>
        private void EmitLeaVar(LirInstruction instruction)
        {
            var slot = GetSlotOffset(instruction.A.Register!);
            _a.Lea(X64Register.EAX, new X64MemoryOperand(X64Register.RBP, slot));
            StoreSlot(instruction.Dst!, X64Register.EAX);
        }

        private void EmitInitParam(LirInstruction instruction)
        {
            var offset = (int)instruction.A.Imm;
            var size = RegisterSize(instruction.Dst!);
            if (!_isX64 && size == 8)
            {
                InitParam8X86(instruction.Dst!, offset);
                return;
            }

            var operand = new X64MemoryOperand(X64Register.RBP, _paramOffset + offset);
            _a.Mov(ToSize(size), X64Register.EAX, operand);
            StoreSlot(instruction.Dst!, X64Register.EAX);
        }

        /// <summary>x86：把参数区（字节偏移 offset）的 8 字节 double 搬到双 dword 槽（低 dword 在 [slot]，高 dword 在 [slot-4]）。</summary>
        private void InitParam8X86(LirVirtualRegister dst, int offset)
        {
            var slot = GetSlotOffset(dst);
            _a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.RBP, _paramOffset + offset));
            _a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, slot), X64Register.EAX);
            _a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.RBP, _paramOffset + offset + 4));
            _a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, slot - 4), X64Register.EAX);
        }

        private void EmitInitRegArg(LirInstruction instruction)
        {
            var size = RegisterSize(instruction.Dst!);
            var ordinal = (int)instruction.A.Imm;
            var source = ordinal switch
            {
                0 => X64Register.ECX,
                1 => X64Register.EDX,
                2 => _isX64 ? X64Register.R8 : X64Register.ESI,
                _ => _isX64 ? X64Register.R9 : X64Register.EDI,
            };
            _a.Mov(ToSize(size), X64Register.EAX, source);
            StoreSlot(instruction.Dst!, X64Register.EAX);
        }

        // ------------------------------------------------------------------
        // 算术/逻辑/位
        // ------------------------------------------------------------------

        private void EmitBinary(LirInstruction instruction)
        {
            var size = ToSize(RegisterSize(instruction.Dst!));
            LoadOperand(X64Register.EAX, instruction.A, size);
            LoadOperand(X64Register.ECX, instruction.B, size);

            switch (instruction.OpCode)
            {
                case LirOpCode.Add:
                    _a.Add(size, X64Register.EAX, X64Register.ECX);
                    break;
                case LirOpCode.Sub:
                    _a.Sub(size, X64Register.EAX, X64Register.ECX);
                    break;
                case LirOpCode.Imul:
                    _a.Imul(size, X64Register.EAX, X64Register.ECX);
                    break;
                case LirOpCode.And:
                    _a.And(size, X64Register.EAX, X64Register.ECX);
                    break;
                case LirOpCode.Or:
                    _a.Or(size, X64Register.EAX, X64Register.ECX);
                    break;
                case LirOpCode.Xor:
                    _a.Xor(size, X64Register.EAX, X64Register.ECX);
                    break;
            }

            StoreSlot(instruction.Dst!, X64Register.EAX);
        }

        private void EmitIdiv(LirInstruction instruction)
        {
            LoadSlot(X64Register.EAX, instruction.Dst!, RegisterSize(instruction.Dst!));
            LoadSlot(X64Register.ECX, instruction.A.Register!, RegisterSize(instruction.A.Register!));

            _a.Cmp(X64Size.Dword, X64Register.ECX, 0);
            _a.Jcc(X64CondCode.Equal, _nameToLabel["DivByZero"]);

            _a.Mov(X64Size.Dword, X64Register.EDX, X64Register.EAX);
            _a.Sar(X64Size.Dword, X64Register.EDX, 31);
            _a.Idiv(X64Size.Dword, X64Register.ECX);
            StoreSlot(instruction.Dst!, X64Register.EAX);
        }

        private void EmitIrem(LirInstruction instruction)
        {
            LoadSlot(X64Register.EAX, instruction.Dst!, RegisterSize(instruction.Dst!));
            LoadSlot(X64Register.ECX, instruction.A.Register!, RegisterSize(instruction.A.Register!));

            _a.Cmp(X64Size.Dword, X64Register.ECX, 0);
            _a.Jcc(X64CondCode.Equal, _nameToLabel["DivByZero"]);

            _a.Mov(X64Size.Dword, X64Register.EDX, X64Register.EAX);
            _a.Sar(X64Size.Dword, X64Register.EDX, 31);
            _a.Idiv(X64Size.Dword, X64Register.ECX);
            StoreSlot(instruction.Dst!, X64Register.EDX);
        }

        private void EmitUdiv(LirInstruction instruction)
        {
            LoadSlot(X64Register.EAX, instruction.Dst!, RegisterSize(instruction.Dst!));
            LoadSlot(X64Register.ECX, instruction.A.Register!, RegisterSize(instruction.A.Register!));

            _a.Cmp(X64Size.Dword, X64Register.ECX, 0);
            _a.Jcc(X64CondCode.Equal, _nameToLabel["DivByZero"]);

            _a.Mov(X64Size.Dword, X64Register.EDX, 0);
            _a.Div(X64Size.Dword, X64Register.ECX);
            StoreSlot(instruction.Dst!, X64Register.EAX);
        }

        private void EmitUrem(LirInstruction instruction)
        {
            LoadSlot(X64Register.EAX, instruction.Dst!, RegisterSize(instruction.Dst!));
            LoadSlot(X64Register.ECX, instruction.A.Register!, RegisterSize(instruction.A.Register!));

            _a.Mov(X64Size.Dword, X64Register.EDX, 0);
            _a.Div(X64Size.Dword, X64Register.ECX);
            StoreSlot(instruction.Dst!, X64Register.EDX);
        }

        private void EmitUnary(LirInstruction instruction)
        {
            var size = ToSize(RegisterSize(instruction.Dst!));
            LoadSlot(X64Register.EAX, instruction.Dst!, RegisterSize(instruction.Dst!));

            if (instruction.OpCode == LirOpCode.Neg)
            {
                _a.Neg(size, X64Register.EAX);
            }
            else
            {
                _a.Not(size, X64Register.EAX);
            }

            StoreSlot(instruction.Dst!, X64Register.EAX);
        }

        private void EmitShift(LirInstruction instruction)
        {
            var size = ToSize(RegisterSize(instruction.Dst!));
            LoadSlot(X64Register.EAX, instruction.A.Register!, RegisterSize(instruction.A.Register!));

            if (instruction.B.Kind == LirOperandKind.Constant)
            {
                var count = (int)instruction.B.Imm;
                switch (instruction.OpCode)
                {
                    case LirOpCode.Shl:
                        _a.Shl(size, X64Register.EAX, count);
                        break;
                    case LirOpCode.Shr:
                        _a.Shr(size, X64Register.EAX, count);
                        break;
                    case LirOpCode.Sar:
                        _a.Sar(size, X64Register.EAX, count);
                        break;
                }
            }
            else
            {
                LoadSlot(X64Register.ECX, instruction.B.Register!, RegisterSize(instruction.B.Register!));
                switch (instruction.OpCode)
                {
                    case LirOpCode.Shl:
                        _a.Shl(size, X64Register.EAX);
                        break;
                    case LirOpCode.Shr:
                        _a.Shr(size, X64Register.EAX);
                        break;
                    case LirOpCode.Sar:
                        _a.Sar(size, X64Register.EAX);
                        break;
                }
            }

            StoreSlot(instruction.Dst!, X64Register.EAX);
        }

        // ------------------------------------------------------------------
        // 64 位整型（long，6e-M19 M1）
        // x64：值在单 8 字节槽 → qword 单指令（除法 cqo+idiv）。
        // x86：值在双 4 字节槽（[slot]=低32，[slot-4]=高32）→ 进位链 / shld 序列 /
        //      除法经运行时 Idiv64/Irem64（EDX:EAX 约定），比较经三路结果（-1/0/+1）。
        // ------------------------------------------------------------------

        private bool _pendingCmp64Trichotomy;

        private void EmitBinary64(LirInstruction instruction)
        {
            var dst = instruction.Dst!;
            if (_isX64)
            {
                LoadSlot(X64Register.RAX, instruction.A.Register!, 8);
                LoadSlot(X64Register.RCX, instruction.B.Register!, 8);

                switch (instruction.OpCode)
                {
                    case LirOpCode.Add64:
                        _a.Add(X64Size.Qword, X64Register.RAX, X64Register.RCX);
                        break;
                    case LirOpCode.Sub64:
                        _a.Sub(X64Size.Qword, X64Register.RAX, X64Register.RCX);
                        break;
                    case LirOpCode.Imul64:
                        _a.Imul(X64Size.Qword, X64Register.RAX, X64Register.RCX);
                        break;
                    case LirOpCode.And64:
                        _a.And(X64Size.Qword, X64Register.RAX, X64Register.RCX);
                        break;
                    case LirOpCode.Or64:
                        _a.Or(X64Size.Qword, X64Register.RAX, X64Register.RCX);
                        break;
                    case LirOpCode.Xor64:
                        _a.Xor(X64Size.Qword, X64Register.RAX, X64Register.RCX);
                        break;
                }

                StoreSlot(dst, X64Register.RAX);
                return;
            }

            var dstSlot = GetSlotOffset(dst);
            var aSlot = GetSlotOffset(instruction.A.Register!);
            var bSlot = GetSlotOffset(instruction.B.Register!);
            var aLo = new X64MemoryOperand(X64Register.RBP, aSlot);
            var aHi = new X64MemoryOperand(X64Register.RBP, aSlot - 4);
            var bLo = new X64MemoryOperand(X64Register.RBP, bSlot);
            var bHi = new X64MemoryOperand(X64Register.RBP, bSlot - 4);
            var dLo = new X64MemoryOperand(X64Register.RBP, dstSlot);
            var dHi = new X64MemoryOperand(X64Register.RBP, dstSlot - 4);

            switch (instruction.OpCode)
            {
                case LirOpCode.Add64:
                    // 低 32 位相加 + adc 高 32 位
                    _a.Mov(X64Size.Dword, X64Register.EAX, aLo);
                    _a.Add(X64Size.Dword, X64Register.EAX, bLo);
                    _a.Mov(X64Size.Dword, X64Register.ECX, aHi);
                    _a.Adc(X64Size.Dword, X64Register.ECX, bHi);
                    _a.Mov(X64Size.Dword, dLo, X64Register.EAX);
                    _a.Mov(X64Size.Dword, dHi, X64Register.ECX);
                    break;

                case LirOpCode.Sub64:
                    _a.Mov(X64Size.Dword, X64Register.EAX, aLo);
                    _a.Sub(X64Size.Dword, X64Register.EAX, bLo);
                    _a.Mov(X64Size.Dword, X64Register.ECX, aHi);
                    _a.Sbb(X64Size.Dword, X64Register.ECX, bHi);
                    _a.Mov(X64Size.Dword, dLo, X64Register.EAX);
                    _a.Mov(X64Size.Dword, dHi, X64Register.ECX);
                    break;

                case LirOpCode.Imul64:
                    EmitImul64X86(aLo, aHi, bLo, bHi, dLo, dHi);
                    break;

                case LirOpCode.And64:
                    _a.Mov(X64Size.Dword, X64Register.EAX, aLo);
                    _a.And(X64Size.Dword, X64Register.EAX, bLo);
                    _a.Mov(X64Size.Dword, X64Register.ECX, aHi);
                    _a.And(X64Size.Dword, X64Register.ECX, bHi);
                    _a.Mov(X64Size.Dword, dLo, X64Register.EAX);
                    _a.Mov(X64Size.Dword, dHi, X64Register.ECX);
                    break;

                case LirOpCode.Or64:
                    _a.Mov(X64Size.Dword, X64Register.EAX, aLo);
                    _a.Or(X64Size.Dword, X64Register.EAX, bLo);
                    _a.Mov(X64Size.Dword, X64Register.ECX, aHi);
                    _a.Or(X64Size.Dword, X64Register.ECX, bHi);
                    _a.Mov(X64Size.Dword, dLo, X64Register.EAX);
                    _a.Mov(X64Size.Dword, dHi, X64Register.ECX);
                    break;

                case LirOpCode.Xor64:
                    _a.Mov(X64Size.Dword, X64Register.EAX, aLo);
                    _a.Xor(X64Size.Dword, X64Register.EAX, bLo);
                    _a.Mov(X64Size.Dword, X64Register.ECX, aHi);
                    _a.Xor(X64Size.Dword, X64Register.ECX, bHi);
                    _a.Mov(X64Size.Dword, dLo, X64Register.EAX);
                    _a.Mov(X64Size.Dword, dHi, X64Register.ECX);
                    break;
            }
        }

        /// <summary>x86 有符号 64×64→64 低 64 位：无符号低积（k1 进位）+ 高位交叉积（t1、t2）；补码乘积低位即位模式无符号乘积模 2^64，无需额外符号修正。</summary>
        private void EmitImul64X86(
            X64MemoryOperand aLo, X64MemoryOperand aHi,
            X64MemoryOperand bLo, X64MemoryOperand bHi,
            X64MemoryOperand dLo, X64MemoryOperand dHi)
        {
            _a.Push(X64Register.EBX);
            _a.Push(X64Register.ESI);
            _a.Push(X64Register.EDI);

            // 低 32 × 低 32 全积
            _a.Mov(X64Size.Dword, X64Register.EAX, aLo);
            _a.Mov(X64Size.Dword, X64Register.EBX, bLo);
            _a.Mul(X64Size.Dword, X64Register.EBX);   // EDX:EAX = aLo×bLo（无符号）
            _a.Mov(X64Size.Dword, X64Register.ECX, X64Register.EAX); // 积低 32
            _a.Mov(X64Size.Dword, X64Register.ESI, X64Register.EDX); // 进位

            // 高位交叉积（imul 保号取低 32）
            _a.Mov(X64Size.Dword, X64Register.EAX, aHi);
            _a.Imul(X64Size.Dword, X64Register.EAX, X64Register.EBX);
            _a.Add(X64Size.Dword, X64Register.ESI, X64Register.EAX);
            _a.Mov(X64Size.Dword, X64Register.EAX, aLo);
            _a.Mov(X64Size.Dword, X64Register.EBX, bHi);
            _a.Imul(X64Size.Dword, X64Register.EAX, X64Register.EBX);
            _a.Add(X64Size.Dword, X64Register.ESI, X64Register.EAX);

            // 注：补码 64 位乘积低位 = 位模式无符号乘积模 2^64，高位 32 位 = k1 + t1 + t2（mod 2^32），
            // 无需额外符号修正（之前“a<0 减 bLo / b<0 减 aLo”会重复扣减而算错，见 -12345 × -2 用例）。

            _a.Mov(X64Size.Dword, dLo, X64Register.ECX);
            _a.Mov(X64Size.Dword, dHi, X64Register.ESI);

            _a.Pop(X64Register.EDI);
            _a.Pop(X64Register.ESI);
            _a.Pop(X64Register.EBX);
        }

        private void EmitIdiv64(LirInstruction instruction)
        {
            if (_isX64)
            {
                LoadSlot(X64Register.RAX, instruction.Dst!, 8);
                LoadSlot(X64Register.RCX, instruction.A.Register!, 8);

                _a.Test(X64Size.Qword, X64Register.RCX, X64Register.RCX);
                _a.Jcc(X64CondCode.Equal, _nameToLabel["DivByZero"]);

                _a.Cqo();
                _a.Idiv(X64Size.Qword, X64Register.RCX);
                StoreSlot(instruction.Dst!, X64Register.RAX);
                return;
            }

            // x86：运行时 Idiv64(aLo=ECX, aHi=EDX, bLo=ESI, bHi=EDI) → 商 EDX:EAX
            LoadIdivArgs(instruction);
            CallRuntimeHelper("Idiv64");
            StoreCallResult(instruction.Dst!);
        }

        private void EmitIrem64(LirInstruction instruction)
        {
            if (_isX64)
            {
                LoadSlot(X64Register.RAX, instruction.Dst!, 8);
                LoadSlot(X64Register.RCX, instruction.A.Register!, 8);

                _a.Test(X64Size.Qword, X64Register.RCX, X64Register.RCX);
                _a.Jcc(X64CondCode.Equal, _nameToLabel["DivByZero"]);

                _a.Cqo();
                _a.Idiv(X64Size.Qword, X64Register.RCX);
                StoreSlot(instruction.Dst!, X64Register.RDX);
                return;
            }

            // x86：运行时 Irem64(aLo=ECX, aHi=EDX, bLo=ESI, bHi=EDI) → 余数 EDX:EAX
            LoadIdivArgs(instruction);
            CallRuntimeHelper("Irem64");
            StoreCallResult(instruction.Dst!);
        }

        /// <summary>6e-M21 Phase 5：无符号 64 位除法——x64 用 xor edx + div；x86 走运行时 Udiv64 helper。</summary>
        private void EmitUdiv64(LirInstruction instruction)
        {
            if (_isX64)
            {
                LoadSlot(X64Register.RAX, instruction.Dst!, 8);
                LoadSlot(X64Register.RCX, instruction.A.Register!, 8);

                _a.Test(X64Size.Qword, X64Register.RCX, X64Register.RCX);
                _a.Jcc(X64CondCode.Equal, _nameToLabel["DivByZero"]);

                _a.Xor(X64Size.Dword, X64Register.EDX, X64Register.EDX);
                _a.Div(X64Size.Qword, X64Register.RCX);
                StoreSlot(instruction.Dst!, X64Register.RAX);
                return;
            }

            LoadIdivArgs(instruction);
            CallRuntimeHelper("Udiv64");
            StoreCallResult(instruction.Dst!);
        }

        private void EmitUrem64(LirInstruction instruction)
        {
            if (_isX64)
            {
                LoadSlot(X64Register.RAX, instruction.Dst!, 8);
                LoadSlot(X64Register.RCX, instruction.A.Register!, 8);

                _a.Test(X64Size.Qword, X64Register.RCX, X64Register.RCX);
                _a.Jcc(X64CondCode.Equal, _nameToLabel["DivByZero"]);

                _a.Xor(X64Size.Dword, X64Register.EDX, X64Register.EDX);
                _a.Div(X64Size.Qword, X64Register.RCX);
                StoreSlot(instruction.Dst!, X64Register.RDX);
                return;
            }

            LoadIdivArgs(instruction);
            CallRuntimeHelper("Urem64");
            StoreCallResult(instruction.Dst!);
        }

        /// <summary>x86：把被除数（dst 槽）与除数（A 槽）装入 ECX/EDX/ESI/EDI。</summary>
        private void LoadIdivArgs(LirInstruction instruction)
        {
            var slot = GetSlotOffset(instruction.Dst!);
            var divisorSlot = GetSlotOffset(instruction.A.Register!);
            _a.Mov(X64Size.Dword, X64Register.ECX, new X64MemoryOperand(X64Register.RBP, slot));
            _a.Mov(X64Size.Dword, X64Register.EDX, new X64MemoryOperand(X64Register.RBP, slot - 4));
            _a.Mov(X64Size.Dword, X64Register.ESI, new X64MemoryOperand(X64Register.RBP, divisorSlot));
            _a.Mov(X64Size.Dword, X64Register.EDI, new X64MemoryOperand(X64Register.RBP, divisorSlot - 4));
        }

        /// <summary>x86：直接 call 运行时辅助函数（对齐补丁 + 结果已在 EDX:EAX）。</summary>
        private void CallRuntimeHelper(string name)
        {
            var aligned = EmitAlign(0);
            _a.Call(_nameToLabel[name]);
            if (aligned)
            {
                _a.Add(X64Size.Qword, X64Register.RSP, 8);
                _stackDepth--;
            }
        }

        private void EmitUnary64(LirInstruction instruction)
        {
            var dst = instruction.Dst!;
            if (_isX64)
            {
                LoadSlot(X64Register.RAX, dst, 8);
                if (instruction.OpCode == LirOpCode.Neg64)
                {
                    _a.Neg(X64Size.Qword, X64Register.RAX);
                }
                else
                {
                    _a.Not(X64Size.Qword, X64Register.RAX);
                }

                StoreSlot(dst, X64Register.RAX);
                return;
            }

            var slot = GetSlotOffset(dst);
            var lo = new X64MemoryOperand(X64Register.RBP, slot);
            var hi = new X64MemoryOperand(X64Register.RBP, slot - 4);
            if (instruction.OpCode == LirOpCode.Neg64)
            {
                // neg lo; adc hi,0; neg hi
                _a.Mov(X64Size.Dword, X64Register.EAX, lo);
                _a.Neg(X64Size.Dword, X64Register.EAX);
                _a.Mov(X64Size.Dword, X64Register.ECX, hi);
                _a.AdcRegImm(X64Register.ECX, 0);
                _a.Neg(X64Size.Dword, X64Register.ECX);
                _a.Mov(X64Size.Dword, lo, X64Register.EAX);
                _a.Mov(X64Size.Dword, hi, X64Register.ECX);
            }
            else
            {
                _a.Mov(X64Size.Dword, X64Register.EAX, lo);
                _a.Not(X64Size.Dword, X64Register.EAX);
                _a.Mov(X64Size.Dword, X64Register.ECX, hi);
                _a.Not(X64Size.Dword, X64Register.ECX);
                _a.Mov(X64Size.Dword, lo, X64Register.EAX);
                _a.Mov(X64Size.Dword, hi, X64Register.ECX);
            }
        }

        private void EmitShift64(LirInstruction instruction)
        {
            var dst = instruction.Dst!;
            var src = instruction.A.Register!;
            var countIsConst = instruction.B.Kind == LirOperandKind.Constant;

            if (_isX64)
            {
                LoadSlot(X64Register.RAX, src, 8);
                if (countIsConst)
                {
                    var count = (int)instruction.B.Imm;
                    switch (instruction.OpCode)
                    {
                        case LirOpCode.Shl64:
                            _a.Shl(X64Size.Qword, X64Register.RAX, count);
                            break;
                        case LirOpCode.Shr64:
                            _a.Shr(X64Size.Qword, X64Register.RAX, count);
                            break;
                        case LirOpCode.Sar64:
                            _a.Sar(X64Size.Qword, X64Register.RAX, count);
                            break;
                    }
                }
                else
                {
                    LoadSlot(X64Register.ECX, instruction.B.Register!, RegisterSize(instruction.B.Register!));
                    switch (instruction.OpCode)
                    {
                        case LirOpCode.Shl64:
                            _a.Shl(X64Size.Qword, X64Register.RAX);
                            break;
                        case LirOpCode.Shr64:
                            _a.Shr(X64Size.Qword, X64Register.RAX);
                            break;
                        case LirOpCode.Sar64:
                            _a.Sar(X64Size.Qword, X64Register.RAX);
                            break;
                    }
                }

                StoreSlot(dst, X64Register.RAX);
                return;
            }

            // x86：64 位移位 = 双 dword + shld/shrd；count ≥ 32 分支
            var srcSlot = GetSlotOffset(src);
            var lo = new X64MemoryOperand(X64Register.RBP, srcSlot);
            var hi = new X64MemoryOperand(X64Register.RBP, srcSlot - 4);
            var dSlot = GetSlotOffset(dst);
            var dLo = new X64MemoryOperand(X64Register.RBP, dSlot);
            var dHi = new X64MemoryOperand(X64Register.RBP, dSlot - 4);

            var smallLabel = _a.CreateLabel();
            var doneLabel = _a.CreateLabel();

            if (countIsConst)
            {
                var count = ((int)instruction.B.Imm) & 63;
                if (count == 0)
                {
                    // 移位 0：原样
                    _a.Mov(X64Size.Dword, X64Register.EAX, lo);
                    _a.Mov(X64Size.Dword, dLo, X64Register.EAX);
                    _a.Mov(X64Size.Dword, X64Register.EAX, hi);
                    _a.Mov(X64Size.Dword, dHi, X64Register.EAX);
                    return;
                }

                if (count < 32)
                {
                    _a.Mov(X64Size.Dword, X64Register.EAX, lo);
                    _a.Mov(X64Size.Dword, X64Register.ECX, hi);
                    EmitShldShrd64(instruction.OpCode, count, X64Register.ECX, X64Register.EAX);
                    _a.Mov(X64Size.Dword, dLo, X64Register.EAX);
                    _a.Mov(X64Size.Dword, dHi, X64Register.ECX);
                }
                else
                {
                    // count ≥ 32：结果 = lo 单独移位（高/低换位）
                    var inner = count - 32;
                    _a.Mov(X64Size.Dword, X64Register.EAX, lo);
                    EmitShift32Reg(instruction.OpCode, inner, X64Register.EAX);
                    _a.Mov(X64Size.Dword, dLo, 0);
                    _a.Mov(X64Size.Dword, dHi, X64Register.EAX);
                }

                return;
            }

            // 运行时 count（ECX）
            LoadSlot(X64Register.ECX, instruction.B.Register!, RegisterSize(instruction.B.Register!));
            _a.Mov(X64Size.Dword, X64Register.EAX, lo);
            _a.Mov(X64Size.Dword, X64Register.EDX, hi);
            _a.Test(X64Size.Dword, X64Register.ECX, X64Register.ECX);
            _a.Jcc(X64CondCode.Equal, smallLabel); // count 低 32 位为 0 → 原样（count mod 2^32 语义）
            _a.Cmp(X64Size.Dword, X64Register.ECX, 32);
            _a.Jcc(X64CondCode.Below, smallLabel);
            // count ≥ 32：lo 单独移位进 hi，lo 置 0
            var bigLabel = _a.CreateLabel();
            var bigDone = _a.CreateLabel();
            _a.Sub(X64Size.Dword, X64Register.ECX, 32);
            _a.Mov(X64Size.Dword, X64Register.EDX, X64Register.EAX); // 把 lo 升为高
            EmitShift32Reg(instruction.OpCode, -1, X64Register.EDX); // cl 计数
            _a.Xor(X64Size.Dword, X64Register.EAX, X64Register.EAX);
            _a.Jmp(bigDone);
            _a.MarkLabel(smallLabel);
            _a.Mov(X64Size.Dword, X64Register.ECX, X64Register.ECX);
            // shld/shrd：dst=hi(src), src=lo
            switch (instruction.OpCode)
            {
                case LirOpCode.Shl64:
                    _a.ShldCl(X64Register.EDX, X64Register.EAX);
                    _a.Shl(X64Size.Dword, X64Register.EAX);
                    break;
                case LirOpCode.Shr64:
                    _a.ShrdCl(X64Register.EAX, X64Register.EDX);
                    _a.Shr(X64Size.Dword, X64Register.EDX);
                    break;
                case LirOpCode.Sar64:
                    _a.ShrdCl(X64Register.EAX, X64Register.EDX);
                    _a.Sar(X64Size.Dword, X64Register.EDX);
                    break;
            }

            _a.MarkLabel(bigDone);
            _a.Mov(X64Size.Dword, dLo, X64Register.EAX);
            _a.Mov(X64Size.Dword, dHi, X64Register.EDX);
        }

        /// <summary>按移位方向对单个 32 位寄存器执行 shl/shr/sar；count=-1 表示用 CL 计数。</summary>
        private void EmitShift32Reg(LirOpCode opCode, int count, X64Register reg)
        {
            var size = X64Size.Dword;
            if (count >= 0)
            {
                switch (opCode)
                {
                    case LirOpCode.Shl64:
                    case LirOpCode.Shl:
                        _a.Shl(size, reg, count);
                        break;
                    case LirOpCode.Shr64:
                    case LirOpCode.Shr:
                        _a.Shr(size, reg, count);
                        break;
                    case LirOpCode.Sar64:
                    case LirOpCode.Sar:
                        _a.Sar(size, reg, count);
                        break;
                }
            }
            else
            {
                switch (opCode)
                {
                    case LirOpCode.Shl64:
                    case LirOpCode.Shl:
                        _a.Shl(size, reg);
                        break;
                    case LirOpCode.Shr64:
                    case LirOpCode.Shr:
                        _a.Shr(size, reg);
                        break;
                    case LirOpCode.Sar64:
                    case LirOpCode.Sar:
                        _a.Sar(size, reg);
                        break;
                }
            }
        }

        private void EmitShldShrd64(LirOpCode opCode, int count, X64Register dst, X64Register src)
        {
            switch (opCode)
            {
                case LirOpCode.Shl64:
                    _a.ShldImm8(dst, src, (byte)count);
                    _a.Shl(X64Size.Dword, src, count);
                    break;
                case LirOpCode.Shr64:
                    _a.ShrdImm8(dst, src, (byte)count);
                    _a.Shr(X64Size.Dword, dst, count);
                    break;
                case LirOpCode.Sar64:
                    _a.ShrdImm8(dst, src, (byte)count);
                    _a.Sar(X64Size.Dword, dst, count);
                    break;
            }
        }

        /// <summary>64 位比较。x64：qword cmp 直接置标志；x86：计算三路结果（-1/0/+1）入 EAX，
        /// 紧随其后的 Setcc/Jcc 先 cmp eax,0 再按条件分支（_pendingCmp64Trichotomy 标记）。</summary>
        private void EmitCmp64(LirInstruction instruction)
        {
            if (_isX64)
            {
                LoadSlot(X64Register.RAX, instruction.A.Register!, 8);
                LoadSlot(X64Register.RCX, instruction.B.Register!, 8);
                _a.Cmp(X64Size.Qword, X64Register.RAX, X64Register.RCX);
                _pendingCmp64Trichotomy = false;
                return;
            }

            var aSlot = GetSlotOffset(instruction.A.Register!);
            var bSlot = GetSlotOffset(instruction.B.Register!);
            var lessLabel = _a.CreateLabel();
            var greaterLabel = _a.CreateLabel();
            var doneLabel = _a.CreateLabel();

            // 先比较高 32 位（有符号）
            _a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.RBP, aSlot - 4));
            _a.Mov(X64Size.Dword, X64Register.ECX, new X64MemoryOperand(X64Register.RBP, bSlot - 4));
            _a.Cmp(X64Size.Dword, X64Register.EAX, X64Register.ECX);
            _a.Jcc(X64CondCode.Less, lessLabel);
            _a.Jcc(X64CondCode.Greater, greaterLabel);

            // 高相等 → 比低 32 位（无符号）
            _a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.RBP, aSlot));
            _a.Mov(X64Size.Dword, X64Register.ECX, new X64MemoryOperand(X64Register.RBP, bSlot));
            _a.Cmp(X64Size.Dword, X64Register.EAX, X64Register.ECX);
            _a.Jcc(X64CondCode.Below, lessLabel);
            _a.Jcc(X64CondCode.Above, greaterLabel);

            // 相等
            _a.Mov(X64Size.Dword, X64Register.EAX, 0);
            _a.Jmp(doneLabel);
            _a.MarkLabel(lessLabel);
            _a.Mov(X64Size.Dword, X64Register.EAX, -1);
            _a.Jmp(doneLabel);
            _a.MarkLabel(greaterLabel);
            _a.Mov(X64Size.Dword, X64Register.EAX, 1);
            _a.MarkLabel(doneLabel);

            _pendingCmp64Trichotomy = true;
        }

        private void EmitCmp(LirInstruction instruction)
        {
            var size = ToSize(RegisterSize(instruction.A.Register!));
            LoadSlot(X64Register.EAX, instruction.A.Register!, RegisterSize(instruction.A.Register!));
            if (instruction.B.Kind == LirOperandKind.Constant)
            {
                _a.Cmp(size, X64Register.EAX, (int)instruction.B.Imm);
            }
            else
            {
                var operand = new X64MemoryOperand(X64Register.RBP, GetSlotOffset(instruction.B.Register!));
                _a.Cmp(ToSize(RegisterSize(instruction.B.Register!)), X64Register.EAX, operand);
            }
        }

        private void EmitSetcc(LirInstruction instruction)
        {
            if (_pendingCmp64Trichotomy)
            {
                // x86 Cmp64 三路结果在 EAX（-1/0/+1）：cmp eax,0 后按条件 setcc
                _a.Cmp(X64Size.Dword, X64Register.EAX, 0);
                _pendingCmp64Trichotomy = false;
            }

            _a.Setcc(MapCond((LirCond)instruction.A.Imm), X64Register.RAX);
            _a.Movzx(X64Size.Dword, X64Register.RAX, X64Register.RAX);
            StoreSlot(instruction.Dst!, X64Register.EAX);
        }

        // ------------------------------------------------------------------
        // 调用/返回
        // ------------------------------------------------------------------

        private void EmitCall(LirInstruction instruction)
        {
            _sysArgs.Clear();

            var aligned = false;

            if (instruction.A.Kind == LirOperandKind.Runtime)
            {
                aligned = EmitAlign(0);
                _a.Call(_nameToLabel[(string)instruction.A.Symbol!]);
            }
            else
            {
                // 锟矫伙拷锟斤拷锟斤拷锟斤拷锟矫的诧拷锟斤拷锟斤拷锟斤拷锟诫补锟斤拷锟斤拷锟斤拷 ReserveArgs 锟斤拷锟斤拷锟戒，FreeArgs 锟皆称恢革拷
                _a.Call(GetFunctionLabel((LirFunction)instruction.A.Symbol!));
            }

            if (aligned)
            {
                _a.Add(X64Size.Qword, X64Register.RSP, 8);
                _stackDepth--;
            }

            if (instruction.Dst != null)
            {
                StoreCallResult(instruction.Dst);
            }
        }

        private void EmitCallReg(LirInstruction instruction)
        {
            _sysArgs.Clear();

            LoadSlot(X64Register.EAX, instruction.B.Register!, RegisterSize(instruction.B.Register!));
            var aligned = EmitAlign(0);

            _a.Call(X64Register.RAX);

            if (aligned)
            {
                _a.Add(X64Size.Qword, X64Register.RSP, 8);
                _stackDepth--;
            }

            if (instruction.Dst != null)
            {
                StoreCallResult(instruction.Dst);
            }
        }

        /// <summary>调用后把返回值存入虚拟寄存器：x86 8 字节返回为 EDX:EAX，拆分存入双 dword 槽。</summary>
        private void StoreCallResult(LirVirtualRegister dst)
        {
            if (!_isX64 && RegisterSize(dst) == 8)
            {
                var slot = GetSlotOffset(dst);
                _a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, slot), X64Register.EAX);
                _a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, slot - 4), X64Register.EDX);
                return;
            }

            StoreSlot(dst, X64Register.EAX);
        }

        /// <summary>x64 对齐补丁（sub rsp, 8）—— 仅用于运行时调用（无参数区，补丁自包自恢复）。</summary>
        private bool EmitAlign(int count)
        {
            if (!_isX64)
            {
                return false;
            }

            if ((_stackDepth + count) % 2 != 0)
            {
                _a.Sub(X64Size.Qword, X64Register.RSP, 8);
                _stackDepth++;
                return true;
            }

            return false;
        }

        /// <summary>参数区预留（字节数）：x64 每参 8 字节，x86 按类型 4/8 字节累计。
        /// 对齐补丁（sub rsp, 8）在预留前发射（与参数区同属一个调用单元），恢复由配对的 FreeArgs 完成。</summary>
        private void EmitReserveArgs(LirInstruction instruction)
        {
            var bytes = (int)instruction.A.Imm;
            var slots = bytes / _slotSize;
            var patch = _isX64 && (_stackDepth + slots) % 2 != 0;

            // 实参区先落位（[rsp+offset] 即各实参，callee [rbp+16+] 一一对应）；
            // 对齐 pad 置于保留块上方（更高地址、调用方侧），绝不挤移实参布局。
            // （旧实现 pad 先推 → 触发对齐补位时实参整体 +8 错位，属潜伏缺陷，C4-c 间接调用首度踩中。）
            _a.Sub(SlotSize, X64Register.RSP, bytes);
            _stackDepth += slots;
            _alignStack.Push(patch);

            if (patch)
            {
                _a.Sub(X64Size.Qword, X64Register.RSP, 8);
                _stackDepth++;
            }
        }

        private void EmitFreeArgs(LirInstruction instruction)
        {
            var bytes = (int)instruction.A.Imm;
            var slots = bytes / _slotSize;
            var patched = _alignStack.Pop();

            _a.Add(SlotSize, X64Register.RSP, bytes);
            _stackDepth -= slots;

            if (patched)
            {
                _a.Add(X64Size.Qword, X64Register.RSP, 8);
                _stackDepth--;
            }
        }

        private void EmitRet(int endLabelId)
        {
            var returnSize = _currentFunction!.ReturnSize;
            if (returnSize > 0)
            {
                if (!_isX64 && returnSize == 8)
                {
                    // x86 8 字节返回值：EDX:EAX（低 dword 在 EAX）→ 槽低 dword 在 [rbp-8]，高 dword 在 [rbp-4]。
                    _a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.RBP, -8));
                    _a.Mov(X64Size.Dword, X64Register.EDX, new X64MemoryOperand(X64Register.RBP, -4));
                }
                else
                {
                    var operand = new X64MemoryOperand(X64Register.RBP, -_slotSize);
                    _a.Mov(ToSize(returnSize), X64Register.EAX, operand);
                }
            }

            if (_currentFunction.Name == _program.EntryFunctionName)
            {
                // 入口返回 = 进程退出码：int main 用返回值；void main 默认 0。
                // （Loader 直接进入 main，无 C runtime 包装，故此处显式退出进程。）
                if (returnSize > 0)
                {
                    _a.Mov(X64Size.Dword, X64Register.ECX, X64Register.EAX);
                }
                else
                {
                    _a.Xor(X64Size.Dword, X64Register.ECX, X64Register.ECX);
                }

                var aligned = EmitAlign(0);
                _a.Call(_nameToLabel["ExitProcess"]);
                if (aligned)
                {
                    _a.Add(X64Size.Qword, X64Register.RSP, 8);
                    _stackDepth--;
                }

                return;
            }

            _a.Mov(SlotSize, X64Register.RSP, X64Register.RBP);
            _a.Pop(X64Register.RBP);
            _a.Ret();
        }

        private void EmitStoreRet(LirInstruction instruction)
        {
            var size = RegisterSize(instruction.A.Register!);
            if (!_isX64 && size == 8)
            {
                // x86 8 字节返回值：槽低 dword 在 [rbp-8]，高 dword 在 [rbp-4]（与 EmitRet 的 EDX:EAX 约定对应）。
                var slot = GetSlotOffset(instruction.A.Register!);
                _a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.RBP, slot));
                _a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, -8), X64Register.EAX);
                _a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.RBP, slot - 4));
                _a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, -4), X64Register.EAX);
                return;
            }

            LoadSlot(X64Register.EAX, instruction.A.Register!, size);
            var operand = new X64MemoryOperand(X64Register.RBP, -_slotSize);
            _a.Mov(ToSize(size), operand, X64Register.EAX);
        }

        private void EmitStoreArg(LirInstruction instruction)
        {
            var size = RegisterSize(instruction.B.Register!);
            var offset = (int)instruction.A.Imm;
            if (!_isX64 && size == 8)
            {
                StoreArg8X86(instruction.B.Register!, offset);
                return;
            }

            LoadSlot(X64Register.EAX, instruction.B.Register!, size);
            var operand = new X64MemoryOperand(X64Register.RSP, offset);
            _a.Mov(ToSize(size), operand, X64Register.EAX);
        }

        /// <summary>x86：把双 dword 槽的 8 字节 double 搬到参数区（字节偏移 offset，低 dword 在前）。</summary>
        private void StoreArg8X86(LirVirtualRegister src, int offset)
        {
            var slot = GetSlotOffset(src);
            _a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.RBP, slot));
            _a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RSP, offset), X64Register.EAX);
            _a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.RBP, slot - 4));
            _a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RSP, offset + 4), X64Register.EAX);
        }

        private void EmitSetArg(LirInstruction instruction)
        {
            _sysArgs.Add(instruction.B.Register!);

            var size = ToSize(RegisterSize(instruction.B.Register!));
            LoadSlot(X64Register.EAX, instruction.B.Register!, RegisterSize(instruction.B.Register!));
            var ordinal = (int)instruction.A.Imm;
            switch (ordinal)
            {
                case 0:
                    _a.Mov(size, X64Register.RCX, X64Register.RAX);
                    break;
                case 1:
                    _a.Mov(size, X64Register.RDX, X64Register.RAX);
                    break;
                case 2:
                    _a.Mov(size, _isX64 ? X64Register.R8 : X64Register.ESI, X64Register.RAX);
                    break;
                case 3:
                    if (_isX64)
                    {
                        _a.Mov(size, X64Register.R9, X64Register.RAX);
                    }
                    else
                    {
                        _a.Mov(size, X64Register.EDI, X64Register.RAX);
                    }
                    break;
            }
        }

        // ------------------------------------------------------------------
        // 浮点（double）
        // 值在槽中以 64 位位模式存放：x64 单 8 字节槽；x86 双 4 字节槽（低地址=低 32 位）。
        // SSE 只做瞬时运算（XMM0/XMM1），与整型路径共用 eax/ecx/edx 装载/存储惯例。
        // ------------------------------------------------------------------

        private void EmitFConst(LirInstruction instruction)
        {
            var key = (string)instruction.A.Symbol!;
            if (instruction.SinglePrecision)
            {
                _a.MovssRip(X64Register.XMM0, _dataSymbols[key]);
                StoreSlotXmm(instruction.Dst!, X64Register.XMM0, single: true);
                return;
            }

            _a.MovsdRip(X64Register.XMM0, _dataSymbols[key]);
            StoreSlotXmm(instruction.Dst!, X64Register.XMM0);
        }

        private void EmitFMove(LirInstruction instruction)
        {
            LoadSlotXmm(X64Register.XMM0, instruction.A.Register!, instruction.SinglePrecision);
            StoreSlotXmm(instruction.Dst!, X64Register.XMM0, instruction.SinglePrecision);
        }

        private void EmitFBinary(LirInstruction instruction)
        {
            var single = instruction.SinglePrecision;
            LoadSlotXmm(X64Register.XMM0, instruction.A.Register!, single);
            LoadSlotXmm(X64Register.XMM1, instruction.B.Register!, single);

            switch (instruction.OpCode)
            {
                case LirOpCode.FAdd:
                    if (single) { _a.Addss(X64Register.XMM0, X64Register.XMM1); } else { _a.Addsd(X64Register.XMM0, X64Register.XMM1); }
                    break;
                case LirOpCode.FSub:
                    if (single) { _a.Subss(X64Register.XMM0, X64Register.XMM1); } else { _a.Subsd(X64Register.XMM0, X64Register.XMM1); }
                    break;
                case LirOpCode.FMul:
                    if (single) { _a.Mulss(X64Register.XMM0, X64Register.XMM1); } else { _a.Mulsd(X64Register.XMM0, X64Register.XMM1); }
                    break;
                case LirOpCode.FDiv:
                    if (single) { _a.Divss(X64Register.XMM0, X64Register.XMM1); } else { _a.Divsd(X64Register.XMM0, X64Register.XMM1); }
                    break;
            }

            StoreSlotXmm(instruction.Dst!, X64Register.XMM0, single);
        }

        private void EmitFNeg(LirInstruction instruction)
        {
            var dstSlot = GetSlotOffset(instruction.Dst!);
            if (instruction.SinglePrecision)
            {
                // 单精度：4 字节槽直接翻转符号位
                _a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.RBP, dstSlot));
                _a.Xor(X64Size.Dword, X64Register.EAX, unchecked((int)0x80000000));
                _a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, dstSlot), X64Register.EAX);
                return;
            }

            if (_isX64)
            {
                _a.Mov(X64Size.Qword, X64Register.RAX, new X64MemoryOperand(X64Register.RBP, dstSlot));
                _a.Mov(X64Register.RCX, unchecked((long)0x8000000000000000UL));
                _a.Xor(X64Size.Qword, X64Register.RAX, X64Register.RCX);
                _a.Mov(X64Size.Qword, new X64MemoryOperand(X64Register.RBP, dstSlot), X64Register.RAX);
            }
            else
            {
                _a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.RBP, dstSlot));
                _a.Mov(X64Size.Dword, X64Register.ECX, new X64MemoryOperand(X64Register.RBP, dstSlot - 4));
                _a.Xor(X64Size.Dword, X64Register.ECX, unchecked((int)0x80000000));
                _a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, dstSlot), X64Register.EAX);
                _a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, dstSlot - 4), X64Register.ECX);
            }
        }

        private void EmitFCmp(LirInstruction instruction)
        {
            LoadSlotXmm(X64Register.XMM0, instruction.A.Register!, instruction.SinglePrecision);
            LoadSlotXmm(X64Register.XMM1, instruction.B.Register!, instruction.SinglePrecision);
            if (instruction.SinglePrecision)
            {
                _a.Ucomiss(X64Register.XMM0, X64Register.XMM1);
            }
            else
            {
                _a.Ucomisd(X64Register.XMM0, X64Register.XMM1);
            }
        }

        /// <summary>浮点单参数学（SSE）：值已在槽中，载入 XMM0 计算后写回槽。</summary>
        private void EmitFUnary(LirInstruction instruction)
        {
            var single = instruction.SinglePrecision;
            LoadSlotXmm(X64Register.XMM0, instruction.A.Register!, single);

            switch (instruction.OpCode)
            {
                case LirOpCode.FSqrt:
                    if (single) { _a.Sqrtss(X64Register.XMM0, X64Register.XMM0); } else { _a.Sqrtsd(X64Register.XMM0, X64Register.XMM0); }
                    break;
                case LirOpCode.FFloor:
                    if (single) { _a.Roundss(X64Register.XMM0, X64Register.XMM0, 0x01); } else { _a.Roundsd(X64Register.XMM0, X64Register.XMM0, 0x01); }
                    break;
                case LirOpCode.FCeiling:
                    if (single) { _a.Roundss(X64Register.XMM0, X64Register.XMM0, 0x02); } else { _a.Roundsd(X64Register.XMM0, X64Register.XMM0, 0x02); }
                    break;
                case LirOpCode.FTruncate:
                    if (single) { _a.Roundss(X64Register.XMM0, X64Register.XMM0, 0x03); } else { _a.Roundsd(X64Register.XMM0, X64Register.XMM0, 0x03); }
                    break;
                case LirOpCode.FRound:
                    if (single) { _a.Roundss(X64Register.XMM0, X64Register.XMM0, 0x00); } else { _a.Roundsd(X64Register.XMM0, X64Register.XMM0, 0x00); }
                    break;
            }

            StoreSlotXmm(instruction.Dst!, X64Register.XMM0, single);
        }

        private void EmitFCvtSI(LirInstruction instruction)
        {
            LoadSlot(X64Register.EAX, instruction.A.Register!, RegisterSize(instruction.A.Register!));
            if (instruction.SinglePrecision)
            {
                _a.Cvtsi2ss(X64Register.XMM0, X64Register.EAX);
                StoreSlotXmm(instruction.Dst!, X64Register.XMM0, single: true);
            }
            else
            {
                _a.Cvtsi2sd(X64Register.XMM0, X64Register.EAX);
                StoreSlotXmm(instruction.Dst!, X64Register.XMM0);
            }
        }

        private void EmitFCvtSD(LirInstruction instruction)
        {
            LoadSlotXmm(X64Register.XMM0, instruction.A.Register!, instruction.SinglePrecision);
            if (instruction.SinglePrecision)
            {
                _a.Cvttss2si(X64Register.EAX, X64Register.XMM0);
            }
            else
            {
                _a.Cvttsd2si(X64Register.EAX, X64Register.XMM0);
            }

            StoreSlot(instruction.Dst!, X64Register.EAX);
        }

        /// <summary>float ↔ double 精度转换（6e-M21 Phase 5b）。</summary>
        private void EmitFCvtDS(LirInstruction instruction)
        {
            // double → float：读双槽位模式，cvtsd2ss，写 4 字节槽
            LoadSlotXmm(X64Register.XMM0, instruction.A.Register!, single: false);
            _a.Cvtsd2ss(X64Register.XMM0, X64Register.XMM0);
            StoreSlotXmm(instruction.Dst!, X64Register.XMM0, single: true);
        }

        private void EmitFCvtSSD(LirInstruction instruction)
        {
            // float → double：movss 读 4 字节槽，cvtss2sd，写双槽位模式
            LoadSlotXmm(X64Register.XMM0, instruction.A.Register!, single: true);
            _a.Cvtss2sd(X64Register.XMM0, X64Register.XMM0);
            StoreSlotXmm(instruction.Dst!, X64Register.XMM0, single: false);
        }

        /// <summary>long → double/float。x64：cvtsi2sd/cvtsi2ss r64；x86：fild qword + fstp qword/dword（双槽位模式直读）。</summary>
        private void EmitFCvtSI64(LirInstruction instruction)
        {
            var single = instruction.SinglePrecision;
            if (_isX64)
            {
                LoadSlot(X64Register.RAX, instruction.A.Register!, 8);
                if (single)
                {
                    // cvtsi2ss r64：经 double 中转（Cvtsi2sd64 + Cvtsd2ss），避免新增 REX.W 变体封装
                    _a.Cvtsi2sd64(X64Register.XMM0, X64Register.RAX);
                    _a.Cvtsd2ss(X64Register.XMM0, X64Register.XMM0);
                    StoreSlotXmm(instruction.Dst!, X64Register.XMM0, single: true);
                }
                else
                {
                    _a.Cvtsi2sd64(X64Register.XMM0, X64Register.RAX);
                    StoreSlotXmm(instruction.Dst!, X64Register.XMM0);
                }

                return;
            }

            // x86：long 在双槽中为大端（低32位@[slot]，高32位@[slot-4]），而 FPU 按小端读写，需重排。
            // 先把 long 以小端形式放到 [dstSlot-4..dstSlot]（低32位@[dstSlot-4]，高32位@[dstSlot]），
            // 再 fild/fstp，最后把结果重排回槽约定（低32位@[dstSlot]，高32位@[dstSlot-4]）。
            var srcSlot = GetSlotOffset(instruction.A.Register!);
            var dstSlot = GetSlotOffset(instruction.Dst!);

            _a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.RBP, srcSlot));
            _a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, dstSlot - 4), X64Register.EAX);
            _a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.RBP, srcSlot - 4));
            _a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, dstSlot), X64Register.EAX);

            _a.FildM64(new X64MemoryOperand(X64Register.RBP, dstSlot - 4));
            if (single)
            {
                _a.FstpM32(new X64MemoryOperand(X64Register.RBP, dstSlot - 4));
                _a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.RBP, dstSlot - 4));
                _a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, dstSlot), X64Register.EAX);
            }
            else
            {
                _a.FstpM64(new X64MemoryOperand(X64Register.RBP, dstSlot - 4));

                _a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.RBP, dstSlot - 4));
                _a.Mov(X64Size.Dword, X64Register.ECX, new X64MemoryOperand(X64Register.RBP, dstSlot));
                _a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, dstSlot), X64Register.EAX);
                _a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, dstSlot - 4), X64Register.ECX);
            }
        }
        /// <summary>
        /// 6e-M21 Phase 7锛歶64 鈫?double/float 绮剧‘杞崲锛堝惈 >2^63 澶у€硷級銆?        /// 鎭掔瓑寮忥細(double)u = (double)(u 娓?MSB) + hiBit路2^63锛屽悇姝ュ潎绮剧‘鍙〃绀恒€?        /// x64锛歴hl1/shr1 娓?MSB + movabs 甯搁噺 2^63锛堟棤鍒嗘敮锛夛紱x86锛欶PU 鏃犲垎鏀袱娈靛拰锛堝父閲忓啓甯т笓鐢ㄦЫ锛夈€?        /// </summary>
        private void EmitFCvtSI64U(LirInstruction instruction)
        {
            var single = instruction.SinglePrecision;
            var srcSlot = GetSlotOffset(instruction.A.Register!);
            var dstSlot = GetSlotOffset(instruction.Dst!);

            if (_isX64)
            {
                LoadSlot(X64Register.RAX, instruction.A.Register!, 8);
                _a.Mov(X64Size.Qword, X64Register.RCX, X64Register.RAX);
                _a.Shr(X64Size.Qword, X64Register.RCX, 63);                 // hiBit (logical)
                _a.Mov(X64Size.Qword, X64Register.RDX, X64Register.RAX);
                _a.Shl(X64Size.Qword, X64Register.RDX, 1);                  // 宸︾Щ涓?MSB
                _a.Shr(X64Size.Qword, X64Register.RDX, 1);                  // 鍙崇Щ琛?0 鈫?娓?MSB
                _a.Cvtsi2sd64(X64Register.XMM0, X64Register.RDX);           // 灏句綋绮剧‘
                _a.Cvtsi2sd64(X64Register.XMM1, X64Register.RCX);           // hiBit as double
                _a.Mov(X64Register.RDX, unchecked((long)0x43E0000000000000));
                _a.MovqGprToXmm(X64Register.XMM2, X64Register.RDX);         // double 浣嶆ā寮?2^63
                _a.Mulsd(X64Register.XMM1, X64Register.XMM2);
                _a.Addsd(X64Register.XMM0, X64Register.XMM1);
                if (single)
                {
                    _a.Cvtsd2ss(X64Register.XMM0, X64Register.XMM0);
                }

                StoreSlotXmm(instruction.Dst!, X64Register.XMM0, single);
                return;
            }

            // x86锛氶噸鎺?u64 鍒?[dstSlot-4..dstSlot] 灏忕缂撳啿锛堜笌鏃㈡湁 FPU 璺緞涓€鑷达級
            _a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.RBP, srcSlot));
            _a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, dstSlot - 4), X64Register.EAX);
            _a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.RBP, srcSlot - 4));
            _a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, dstSlot), X64Register.EAX);

            var bitBuf = new X64MemoryOperand(X64Register.RBP, -_frameBytes);       // hiBit 暂存（本函数不用 cw）
            var constLo = new X64MemoryOperand(X64Register.RBP, -_frameBytes + 8);  // double 2^63 低 dword
            var constHi = new X64MemoryOperand(X64Register.RBP, -_frameBytes + 12); // 高 dword

            _a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.RBP, dstSlot));
            _a.Mov(X64Size.Dword, X64Register.ECX, X64Register.EAX);
            _a.Shr(X64Size.Dword, X64Register.ECX, 31);                             // hiBit
            _a.Mov(X64Size.Dword, bitBuf, X64Register.ECX);
            _a.And(X64Size.Dword, X64Register.EAX, 0x7FFFFFFF);
            _a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, dstSlot), X64Register.EAX);
            _a.Mov(X64Size.Dword, constLo, 0);
            _a.Mov(X64Size.Dword, constHi, unchecked((int)0x43E00000));

            _a.FildM32(bitBuf);                                                     // st0 = hiBit (0.0/1.0)
            _a.FldM64(constLo);                                                    // st0 = 2^63, st1 = hiBit
            _a.Fmulp();                                                            // st0 = hiBit*2^63
            _a.FildM64(new X64MemoryOperand(X64Register.RBP, dstSlot - 4));       // st0 = 灏句綋
            _a.Faddp();                                                            // st0 = 鍜?
            if (single)
            {
                _a.FstpM32(new X64MemoryOperand(X64Register.RBP, dstSlot - 4));
                _a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.RBP, dstSlot - 4));
                _a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, dstSlot), X64Register.EAX);
            }
            else
            {
                _a.FstpM64(new X64MemoryOperand(X64Register.RBP, dstSlot - 4));
                _a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.RBP, dstSlot - 4));
                _a.Mov(X64Size.Dword, X64Register.ECX, new X64MemoryOperand(X64Register.RBP, dstSlot));
                _a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, dstSlot), X64Register.EAX);
                _a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, dstSlot - 4), X64Register.ECX);
            }
        }
        private void EmitFCvtSD64(LirInstruction instruction)
        {
            var single = instruction.SinglePrecision;
            if (_isX64)
            {
                LoadSlotXmm(X64Register.XMM0, instruction.A.Register!, single);
                if (single)
                {
                    // cvttss2si r64 经 double 中转（Cvtss2sd + Cvttsd2si64）
                    _a.Cvtss2sd(X64Register.XMM0, X64Register.XMM0);
                    _a.Cvttsd2si64(X64Register.RAX, X64Register.XMM0);
                }
                else
                {
                    _a.Cvttsd2si64(X64Register.RAX, X64Register.XMM0);
                }

                StoreSlot(instruction.Dst!, X64Register.RAX);
                return;
            }

            var srcSlot = GetSlotOffset(instruction.A.Register!);
            var dstSlot = GetSlotOffset(instruction.Dst!);

            // 浮点在双槽中为大端（低32位@[srcSlot]，高32位@[srcSlot-4]），FPU 按小端读写，
            // 先以小端形式放到 [dstSlot-4..dstSlot]（低32位@[dstSlot-4]，高32位@[dstSlot]）。
            _a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.RBP, srcSlot));
            _a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, dstSlot - 4), X64Register.EAX);
            _a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.RBP, srcSlot - 4));
            _a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, dstSlot), X64Register.EAX);

            if (single)
            {
                // float 单精度：fld m32（D9 /0）读 4 字节
                _a.FldM32(new X64MemoryOperand(X64Register.RBP, dstSlot - 4));
            }
            else
            {
                _a.FldM64(new X64MemoryOperand(X64Register.RBP, dstSlot - 4));
            }

            // 控制字缓冲：帧底专用槽（-frameBytes，与 LeaSlot 缓冲/变量槽隔离）；保存原始 cw，置 RC=11（截断），转换后恢复。
            var cwBuf = new X64MemoryOperand(X64Register.RBP, -_frameBytes);

            _a.FnstcwM16(cwBuf);
            _a.Movzx(X64Size.Dword, X64Register.EAX, cwBuf);
            _a.Mov(X64Size.Dword, X64Register.ECX, X64Register.EAX); // 保存原始 cw
            _a.Or(X64Size.Dword, X64Register.EAX, 0x0C00);
            _a.Mov(X64Size.Word, cwBuf, X64Register.EAX);
            _a.FldcwM16(cwBuf);

            _a.FistpM64(new X64MemoryOperand(X64Register.RBP, dstSlot - 4));

            // 恢复原始控制字
            _a.Mov(X64Size.Word, cwBuf, X64Register.ECX);
            _a.FldcwM16(cwBuf);

            // [dstSlot-4..dstSlot] 现为小端 long（低32位@[dstSlot-4]，高32位@[dstSlot]），重排为槽约定。
            _a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.RBP, dstSlot - 4));
            _a.Mov(X64Size.Dword, X64Register.ECX, new X64MemoryOperand(X64Register.RBP, dstSlot));
            _a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, dstSlot), X64Register.EAX);
            _a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, dstSlot - 4), X64Register.ECX);
        }

        /// <summary>int/enum → long 符号扩展。x64：movsxd；x86：cdq 后低 dword 存高槽。</summary>
        private void EmitMovsx64(LirInstruction instruction)
        {
            if (_isX64)
            {
                LoadSlot(X64Register.EAX, instruction.A.Register!, RegisterSize(instruction.A.Register!));
                _a.Movsxd(X64Register.RAX, X64Register.EAX);
                StoreSlot(instruction.Dst!, X64Register.RAX);
                return;
            }

            var srcSlot = GetSlotOffset(instruction.A.Register!);
            var dstSlot = GetSlotOffset(instruction.Dst!);
            _a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.RBP, srcSlot));
            _a.Cdq();
            _a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, dstSlot), X64Register.EAX);
            _a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, dstSlot - 4), X64Register.EDX);
        }

        /// <summary>byte/char → long 零扩展（源 32 位值无符号）。x64：mov eax,[slot] 自动清高 32；x86：写低 dword、高 dword 置 0。</summary>
        private void EmitMovzx64(LirInstruction instruction)
        {
            var srcSlot = GetSlotOffset(instruction.A.Register!);
            if (_isX64)
            {
                // 32 位加载零扩展高 32 位，全 qword 写入目标槽
                _a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.RBP, srcSlot));
                StoreSlot(instruction.Dst!, X64Register.RAX);
                return;
            }

            var dstSlot = GetSlotOffset(instruction.Dst!);
            _a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.RBP, srcSlot));
            _a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, dstSlot), X64Register.EAX);
            _a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, dstSlot - 4), 0);
        }

        /// <summary>long → int 低 32 位截断。x64：qword 清高；x86：仅写低 dword。</summary>
        private void EmitTrunc64(LirInstruction instruction)
        {
            var srcSlot = GetSlotOffset(instruction.A.Register!);
            if (_isX64)
            {
                _a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.RBP, srcSlot));
                StoreSlot(instruction.Dst!, X64Register.RAX);
                return;
            }

            var dstSlot = GetSlotOffset(instruction.Dst!);
            _a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.RBP, srcSlot));
            _a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, dstSlot), X64Register.EAX);
        }

        /// <summary>double 运行时参数：x64 单 64 位寄存器（rcx/rdx/r8/r9）；x86 拆 low/high 两个 32 位寄存器。</summary>
        private void EmitSetArg64(LirInstruction instruction)
        {
            var ordinal = (int)instruction.A.Imm;
            var register = instruction.B.Register!;
            var slot = GetSlotOffset(register);

            if (_isX64)
            {
                var target = ordinal switch
                {
                    0 => X64Register.RCX,
                    1 => X64Register.RDX,
                    2 => X64Register.R8,
                    _ => X64Register.R9,
                };
                _a.Mov(X64Size.Qword, X64Register.RAX, new X64MemoryOperand(X64Register.RBP, slot));
                _a.Mov(X64Size.Qword, target, X64Register.RAX);
                return;
            }

            var low = ordinal switch
            {
                0 => X64Register.ECX,
                1 => X64Register.EDX,
                2 => X64Register.ESI,
                _ => throw new Exception($"x86 运行时函数不支持第 {(ordinal + 1)} 个 double 参数"),
            };
            var high = ordinal switch
            {
                0 => X64Register.EDX,
                1 => X64Register.ESI,
                _ => throw new Exception($"x86 运行时函数不支持第 {(ordinal + 2)} 个寄存器参数"),
            };
            _a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.RBP, slot));
            _a.Mov(X64Size.Dword, low, X64Register.EAX);
            _a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.RBP, slot - 4));
            _a.Mov(X64Size.Dword, high, X64Register.EAX);
        }

        // ------------------------------------------------------------------
        // 系统调用（平台化模板）
        // ------------------------------------------------------------------

        private void EmitSysCall(LirInstruction instruction)
        {
            var import = (LirImport)instruction.A.Symbol!;
            var importSlot = _importSlots[import];
            var argCount = (int)instruction.B.Imm;
            var dst = instruction.Dst;

            if (_isX64)
            {
                // fastcall + shadow space：0x20 shadow + argCount-4 栈槽；向上取整到 0x20 对齐
                var stackSlots = argCount > 4 ? Math.Max(2, argCount - 4) : 2;
                var frameSize = 0x20 + stackSlots * 8;
                var aligned = EmitAlign(0);
                _a.Sub(X64Size.Qword, X64Register.RSP, frameSize);
                _stackDepth += frameSize / 8;

                for (var i = 0; i < Math.Min(argCount, 4) && i < _sysArgs.Count; i++)
                {
                    LoadSlot(X64Register.EAX, _sysArgs[i], RegisterSize(_sysArgs[i]));
                    var target = i switch
                    {
                        0 => X64Register.RCX,
                        1 => X64Register.RDX,
                        2 => X64Register.R8,
                        _ => X64Register.R9,
                    };
                    _a.Mov(ToSize(RegisterSize(_sysArgs[i])), target, X64Register.RAX);
                }

                if (argCount >= 5)
                {
                    if (_sysArgs.Count > 4)
                    {
                        LoadSlot(X64Register.EAX, _sysArgs[4], RegisterSize(_sysArgs[4]));
                        _a.Mov(ToSize(RegisterSize(_sysArgs[4])), new X64MemoryOperand(X64Register.RSP, 0x20), X64Register.RAX);
                    }
                    else
                    {
                        _a.Mov(X64Size.Qword, new X64MemoryOperand(X64Register.RSP, 0x20), 0);
                    }
                }

                if (argCount >= 6)
                {
                    // 第 6 参落在 shadow 尾（[RSP+0x28]），0x30 帧恰好容纳（48≡0 mod 16）
                    if (_sysArgs.Count > 5)
                    {
                        LoadSlot(X64Register.EAX, _sysArgs[5], RegisterSize(_sysArgs[5]));
                        _a.Mov(ToSize(RegisterSize(_sysArgs[5])), new X64MemoryOperand(X64Register.RSP, 0x28), X64Register.RAX);
                    }
                    else
                    {
                        _a.Mov(X64Size.Qword, new X64MemoryOperand(X64Register.RSP, 0x28), 0);
                    }
                }

                if (argCount >= 7)
                {
                    if (_sysArgs.Count > 6)
                    {
                        LoadSlot(X64Register.EAX, _sysArgs[6], RegisterSize(_sysArgs[6]));
                        _a.Mov(ToSize(RegisterSize(_sysArgs[6])), new X64MemoryOperand(X64Register.RSP, 0x30), X64Register.RAX);
                    }
                    else
                    {
                        _a.Mov(X64Size.Qword, new X64MemoryOperand(X64Register.RSP, 0x30), 0);
                    }
                }

                _a.CallRip(importSlot);
                _a.Add(X64Size.Qword, X64Register.RSP, frameSize);
                _stackDepth -= frameSize / 8;

                if (aligned)
                {
                    _a.Add(X64Size.Qword, X64Register.RSP, 8);
                    _stackDepth--;
                }
            }
            else
            {
                // 约定：stdcall（运行时所/默认）被调方清栈；cdecl（用户 extern 声明）调用方清栈
                var pushed = 0;
                if (argCount >= 6)
                {
                    if (_sysArgs.Count > 5)
                    {
                        LoadSlot(X64Register.EAX, _sysArgs[5], RegisterSize(_sysArgs[5]));
                        _a.Push(X64Register.EAX);
                    }
                    else
                    {
                        _a.Push(0);
                    }
                    pushed++;
                }

                if (argCount >= 5)
                {
                    if (_sysArgs.Count > 4)
                    {
                        LoadSlot(X64Register.EAX, _sysArgs[4], RegisterSize(_sysArgs[4]));
                        _a.Push(X64Register.EAX);
                    }
                    else
                    {
                        _a.Push(0);
                    }
                    pushed++;
                }

                for (var i = Math.Min(argCount, 4) - 1; i >= 0; i--)
                {
                    LoadSlot(X64Register.EAX, _sysArgs[i], RegisterSize(_sysArgs[i]));
                    _a.Push(X64Register.EAX);
                    pushed++;
                    _stackDepth++;
                }

                _a.CallRip(importSlot);
                _stackDepth -= pushed;

                if (import.Cdecl && pushed > 0)
                {
                    _a.Add(X64Size.Dword, X64Register.ESP, pushed * 4);
                }
            }

            if (dst != null)
            {
                StoreSlot(dst, X64Register.EAX);
            }

            _sysArgs.Clear();
        }

        // ------------------------------------------------------------------
        // 栈操作/检查
        // ------------------------------------------------------------------

        private void EmitStackCheck()
        {
            _a.MovGs(X64Register.RCX, _stackLimitOffset);
            _a.Add(X64Size.Dword, X64Register.ECX, 0x10000);
            _a.Cmp(X64Size.Dword, X64Register.RSP, X64Register.RCX);
            _a.Jcc(X64CondCode.Below, _nameToLabel["StackOverflow"]);
        }

        private void EmitPush(LirInstruction instruction)
        {
            LoadSlot(X64Register.EAX, instruction.A.Register!, RegisterSize(instruction.A.Register!));
            _a.Push(X64Register.RAX);
            _stackDepth++;
        }

        private void EmitPop(LirInstruction instruction)
        {
            _a.Pop(X64Register.RAX);
            _stackDepth--;
            StoreSlot(instruction.Dst!, X64Register.EAX);
        }

        // ------------------------------------------------------------------
        // 操作数/槽
        // ------------------------------------------------------------------

        private int RegisterSize(LirVirtualRegister register) => _currentFunction!.RegisterSize(register);

    }
}
