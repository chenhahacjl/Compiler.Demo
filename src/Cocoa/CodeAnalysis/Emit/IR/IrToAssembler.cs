using System;
using System.Collections.Generic;
using System.Linq;
using Cocoa.CodeAnalysis.Emit.Native;
using Cocoa.CodeAnalysis.Emit.Native.Assembler;
using Cocoa.CodeAnalysis.Emit.Native.Assembler.X64;
using Cocoa.CodeAnalysis.Emit.Native.PEFile;

namespace Cocoa.CodeAnalysis.Emit.IR
{
    /// <summary>IR → IAssembler 的发射结果：全部函数/特殊函数 label 与入口 stub label。</summary>
    internal sealed class IrEmitResult
    {
        public IrEmitResult(Dictionary<string, int> labels, int stubLabel, List<PefileImport> imports)
        {
            Labels = labels;
            StubLabel = stubLabel;
            Imports = imports;
        }

        public Dictionary<string, int> Labels { get; }
        public int StubLabel { get; }
        public List<PefileImport> Imports { get; }
    }

    /// <summary>
    /// IR 到 IAssembler 的映射。寄存器分配策略：每个虚拟寄存器 → 函数帧内唯一栈槽
    /// （slot k @ [rbp - 16 - slotSize*k]），物理寄存器（eax/ecx/edx…）仅作瞬时运算载体。
    /// 帧布局、参数传递、TEB 栈限检查、x64 16 字节对齐与现有 NativeCodeEmitter 完全一致。
    /// </summary>
    internal sealed class IrToAssembler
    {
        private readonly IAssembler _a;
        private readonly IrProgram _program;
        private readonly int _entryLabel;
        private readonly TargetPlatform _platform;
        private readonly bool _isX64;
        private readonly int _slotSize;
        private readonly int _paramOffset;
        private readonly int _stackLimitOffset;
        private readonly Action<IReadOnlyList<PefileImport>, int>? _emitStub;

        private readonly Dictionary<IrFunction, int> _functionLabels = new();
        private readonly Dictionary<string, int> _nameToLabel = new();
        private readonly Dictionary<int, int> _asmLabelCache = new();
        private readonly Dictionary<string, int> _dataSymbols = new();
        private readonly Dictionary<IrImport, int> _importSlots = new();
        private readonly List<PefileImport> _imports = new();
        private readonly List<IrVirtualRegister> _sysArgs = new();

        private Dictionary<IrVirtualRegister, int> _slots = new();
        private int _stackDepth;
        private int _frameBytes;
        private readonly Stack<bool> _alignStack = new();
        private IrFunction? _currentFunction;
        private int _stubLabel;

        private IrToAssembler(IAssembler a, IrProgram program, int entryLabel, TargetPlatform platform,
            Action<IReadOnlyList<PefileImport>, int>? emitStub)
        {
            _a = a;
            _program = program;
            _entryLabel = entryLabel;
            _platform = platform;
            _isX64 = platform.Arch == Architecture.X64;
            _slotSize = _isX64 ? 8 : 4;
            _paramOffset = _isX64 ? 16 : 8;
            _stackLimitOffset = _isX64 ? 0x10 : 0x08;
            _emitStub = emitStub;
        }

        private X64Size SlotSize => _slotSize == 8 ? X64Size.Qword : X64Size.Dword;

        public static IrEmitResult Emit(IAssembler a, IrProgram program, int entryLabel, TargetPlatform platform,
            Action<IReadOnlyList<PefileImport>, int>? emitStub)
        {
            var emitter = new IrToAssembler(a, program, entryLabel, platform, emitStub);
            emitter.EmitProgram();
            return new IrEmitResult(emitter._nameToLabel, emitter._stubLabel, emitter._imports);
        }

        private void EmitProgram()
        {
            EmitData();
            EmitStub();
            EmitFunctions();
        }

        // ------------------------------------------------------------------
        // 数据段：语言层字符串 + 运行时数据项 + 导入槽
        // ------------------------------------------------------------------

        private void EmitData()
        {
            foreach (var item in _program.DataItems)
            {
                var symbol = _a.CreateDataSymbol();
                _a.MarkDataSymbol(symbol);
                _dataSymbols.Add(item.Key, symbol);

                switch (item.Kind)
                {
                    case IrDataKind.Int32:
                        _a.WriteDataInt32(item.IntValue);
                        break;
                    case IrDataKind.Pointer:
                        if (_isX64)
                        {
                            _a.WriteDataInt64(0);
                        }
                        else
                        {
                            _a.WriteDataInt32(0);
                        }

                        break;
                    case IrDataKind.Utf16:
                        _a.WriteDataUtf16(item.Text!);
                        _a.AlignData(4);
                        break;
                    case IrDataKind.Bytes:
                        _a.WriteDataBytes(item.Bytes!);
                        break;
                    default:
                        throw new Exception($"Unexpected data kind: {item.Kind}");
                }
            }

            // 分组内聚：kernel32 组（运行时基础）全部在前，其余 DLL 组按首见顺序聚合，组内保持相对顺序。
            // IAT 由 OS 加载器按描述符 FirstThunk 连续填充，槽数组必须与 specs 分组顺序一致。
            var seenImports = new HashSet<IrImport>();
            var imports = _program.Imports.Where(seenImports.Add).ToList();
            var kernel32Group = imports.Where(i => string.Equals(i.DllName, "kernel32.dll", StringComparison.OrdinalIgnoreCase)).ToList();
            var otherGroups = imports.Where(i => !string.Equals(i.DllName, "kernel32.dll", StringComparison.OrdinalIgnoreCase))
                                     .GroupBy(i => i.DllName, StringComparer.OrdinalIgnoreCase)
                                     .SelectMany(g => g)
                                     .ToList();
            var reordered = kernel32Group.Concat(otherGroups).ToList();

            foreach (var import in reordered)
            {
                var symbol = _a.CreateDataSymbol();
                _a.MarkDataSymbol(symbol);
                _importSlots.Add(import, symbol);

                // IAT 槽（8/4 字节）：磁盘初值 0，OS 加载器启动时按导入描述符填充
                if (_isX64)
                {
                    _a.WriteDataInt64(0);
                }
                else
                {
                    _a.WriteDataInt32(0);
                }
            }

            foreach (var import in reordered)
            {
                _imports.Add(new PefileImport(import.DllName, import.Name, _a.GetDataOffset(_importSlots[import])));
            }
        }

        private void EmitStub()
        {
            if (_emitStub == null)
            {
                // 无自解析 stub（6c-2）：入口即用户入口
                _stubLabel = _entryLabel;
                return;
            }

            _stubLabel = _a.CreateLabel();
            _a.MarkLabel(_stubLabel);
            _emitStub(_imports, _stubLabel);
        }

        private void EmitFunctions()
        {
            foreach (var function in _program.Functions)
            {
                var label = function.Name == _program.EntryFunctionName ? _entryLabel : _a.CreateLabel();
                _functionLabels.Add(function, label);
                _nameToLabel[function.Name] = label;
            }

            foreach (var kvp in _program.SpecialFunctions)
            {
                _nameToLabel[kvp.Key] = _functionLabels[kvp.Value];
            }

            foreach (var function in _program.Functions)
            {
                EmitFunction(function);
            }
        }

        private void EmitFunction(IrFunction function)
        {
            _currentFunction = function;
            _asmLabelCache.Clear();
            _sysArgs.Clear();

            _slots = new Dictionary<IrVirtualRegister, int>();
            var registers = new List<IrVirtualRegister>(function.RegisterSizes.Keys);
            registers.Sort((x, y) => x.Id.CompareTo(y.Id));
            var slotCount = 0;
            foreach (var register in registers)
            {
                _slots.Add(register, slotCount);
                // x86 槽宽 4 字节：8 字节虚拟寄存器（double）占两个连续槽（低地址=低 32 位）
                slotCount += !_isX64 && function.RegisterSize(register) == 8 ? 2 : 1;
            }

            _stackDepth = 0;
            _a.MarkLabel(_functionLabels[function]);

            _a.Push(X64Register.RBP);
            _a.Mov(X64Size.Qword, X64Register.RBP, X64Register.RSP);

            // 栈帧：variables.Count 个虚拟寄存器槽 + 1 个返回值槽（与现有 ABI 一致）。
            // 若函数使用 LeaSlot（把槽当临时缓冲，如 BuildInt 需要 buf+44），在帧底预留缓冲空间。
            if (_isX64)
            {
                var frameBytes = 8 * (_slots.Count + 1);
                if (function.Instructions.Any(i => i.OpCode == IrOpCode.LeaSlot))
                {
                    frameBytes += 0x80;
                }

                if (frameBytes % 16 == 8)
                {
                    frameBytes += 8;
                }

                // DivByZero/StackOverflow 由 jcc（不压栈）进入，入口 rsp 与 call 进入相差 8，
                // 帧大小需 ≡8（而非 ≡0），使 sub 后函数内 rsp 对齐与 EmitAlign 假设一致（避免调用 kernel32 时对齐崩溃）。
                if (function.Name == "DivByZero" || function.Name == "StackOverflow")
                {
                    frameBytes += 8;
                }

                _a.Sub(X64Size.Qword, X64Register.RSP, frameBytes);
                _frameBytes = frameBytes;
            }
            else
            {
                var frameBytes = 4 * (slotCount + 3);
                if (function.Instructions.Any(i => i.OpCode == IrOpCode.LeaSlot))
                {
                    frameBytes += 0x80;
                }

                _a.Sub(X64Size.Dword, X64Register.RSP, frameBytes);
                _frameBytes = frameBytes;
            }

            foreach (var instruction in function.Instructions)
            {
                EmitInstruction(instruction);
            }
        }

        private void EmitInstruction(IrInstruction instruction)
        {
            switch (instruction.OpCode)
            {
                case IrOpCode.Const:
                    EmitConst(instruction);
                    break;
                case IrOpCode.Mov:
                    EmitMov(instruction);
                    break;
                case IrOpCode.Load:
                    EmitLoad(instruction);
                    break;
                case IrOpCode.Store:
                    EmitStore(instruction);
                    break;
                case IrOpCode.LeaData:
                    EmitLeaData(instruction);
                    break;
                case IrOpCode.Lea:
                    EmitLea(instruction);
                    break;
                case IrOpCode.LeaSlot:
                    EmitLeaSlot(instruction);
                    break;
                case IrOpCode.InitParam:
                    EmitInitParam(instruction);
                    break;
                case IrOpCode.InitRegArg:
                    EmitInitRegArg(instruction);
                    break;
                case IrOpCode.Add:
                case IrOpCode.Sub:
                case IrOpCode.Imul:
                case IrOpCode.And:
                case IrOpCode.Or:
                case IrOpCode.Xor:
                    EmitBinary(instruction);
                    break;
                case IrOpCode.Idiv:
                    EmitIdiv(instruction);
                    break;
                case IrOpCode.Udiv:
                    EmitUdiv(instruction);
                    break;
                case IrOpCode.Urem:
                    EmitUrem(instruction);
                    break;
                case IrOpCode.Neg:
                case IrOpCode.Not:
                    EmitUnary(instruction);
                    break;
                case IrOpCode.Shl:
                case IrOpCode.Shr:
                case IrOpCode.Sar:
                    EmitShift(instruction);
                    break;
                case IrOpCode.Cmp:
                    EmitCmp(instruction);
                    break;
                case IrOpCode.Setcc:
                    EmitSetcc(instruction);
                    break;
                case IrOpCode.Label:
                    _a.MarkLabel(GetLabel((int)instruction.A.Imm));
                    break;
                case IrOpCode.Jmp:
                    _a.Jmp(GetLabel((int)instruction.A.Imm));
                    break;
                case IrOpCode.Jcc:
                    _a.Jcc(MapCond((IrCond)instruction.A.Imm), GetLabel((int)instruction.B.Imm));
                    break;
                case IrOpCode.Call:
                    EmitCall(instruction);
                    break;
                case IrOpCode.CallReg:
                    EmitCallReg(instruction);
                    break;
                case IrOpCode.Ret:
                    EmitRet(instruction);
                    break;
                case IrOpCode.ReserveArgs:
                    EmitReserveArgs(instruction);
                    break;
                case IrOpCode.FreeArgs:
                    EmitFreeArgs(instruction);
                    break;
                case IrOpCode.StoreArg:
                    EmitStoreArg(instruction);
                    break;
                case IrOpCode.SetArg:
                    EmitSetArg(instruction);
                    break;
                case IrOpCode.SetArg64:
                    EmitSetArg64(instruction);
                    break;
                case IrOpCode.FConst:
                    EmitFConst(instruction);
                    break;
                case IrOpCode.FMov:
                    EmitFMove(instruction);
                    break;
                case IrOpCode.FAdd:
                case IrOpCode.FSub:
                case IrOpCode.FMul:
                case IrOpCode.FDiv:
                    EmitFBinary(instruction);
                    break;
                case IrOpCode.FNeg:
                    EmitFNeg(instruction);
                    break;
                case IrOpCode.FCmp:
                    EmitFCmp(instruction);
                    break;
                case IrOpCode.FCvtSI:
                    EmitFCvtSI(instruction);
                    break;
                case IrOpCode.FCvtSD:
                    EmitFCvtSD(instruction);
                    break;
                case IrOpCode.StoreRet:
                    EmitStoreRet(instruction);
                    break;
                case IrOpCode.StackCheck:
                    EmitStackCheck();
                    break;
                case IrOpCode.SysCall:
                    EmitSysCall(instruction);
                    break;
                case IrOpCode.Push:
                    EmitPush(instruction);
                    break;
                case IrOpCode.Pop:
                    EmitPop(instruction);
                    break;
                case IrOpCode.Nop:
                    _a.Nop();
                    break;
                case IrOpCode.SeqPoint:
                    break;
                default:
                    throw new Exception($"Unexpected IR opcode: {instruction.OpCode}");
            }
        }

        // ------------------------------------------------------------------
        // 数据移动
        // ------------------------------------------------------------------

        private void EmitConst(IrInstruction instruction)
        {
            if (RegisterSize(instruction.Dst!) == 8)
            {
                _a.Mov(X64Register.RAX, instruction.A.Imm);
            }
            else
            {
                _a.Mov(X64Size.Dword, X64Register.EAX, (int)instruction.A.Imm);
            }

            StoreSlot(instruction.Dst!, X64Register.EAX);
        }

        private void EmitMov(IrInstruction instruction)
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

        private void EmitLoad(IrInstruction instruction)
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

        private void EmitStore(IrInstruction instruction)
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

        private void EmitLeaData(IrInstruction instruction)
        {
            var key = (string)instruction.A.Symbol!;
            _a.LeaRip(X64Register.RAX, _dataSymbols[key]);
            StoreSlot(instruction.Dst!, X64Register.RAX);
        }

        private void EmitLea(IrInstruction instruction)
        {
            var size = RegisterSize(instruction.A.Register!);
            var reg = size == 8 ? X64Register.RAX : X64Register.EAX;
            LoadSlot(reg, instruction.A.Register!, size);
            _a.Add(size == 8 ? X64Size.Qword : X64Size.Dword, reg, instruction.Offset);
            StoreSlot(instruction.Dst!, reg);
        }

        private void EmitLeaSlot(IrInstruction instruction)
        {
            // 指向帧底部的缓冲槽（远离返回地址；配合 EmitFunction 的 LeaSlot 缓冲空间）
            var offset = -_frameBytes + _slotSize;
            _a.Lea(X64Register.EAX, new X64MemoryOperand(X64Register.RBP, offset));
            StoreSlot(instruction.Dst!, X64Register.EAX);
        }

        private void EmitInitParam(IrInstruction instruction)
        {
            var ordinal = (int)instruction.A.Imm;
            var size = RegisterSize(instruction.Dst!);
            var operand = new X64MemoryOperand(X64Register.RBP, _paramOffset + _slotSize * ordinal);
            _a.Mov(ToSize(size), X64Register.EAX, operand);
            StoreSlot(instruction.Dst!, X64Register.EAX);
        }

        private void EmitInitRegArg(IrInstruction instruction)
        {
            var size = RegisterSize(instruction.Dst!);
            var ordinal = (int)instruction.A.Imm;
            var source = ordinal switch
            {
                0 => X64Register.ECX,
                1 => X64Register.EDX,
                2 => _isX64 ? X64Register.R8 : X64Register.ESI,
                _ => _isX64 ? X64Register.R9 : throw new Exception($"x86 运行时函数不支持第 {(ordinal + 1)} 个寄存器参数"),
            };
            _a.Mov(ToSize(size), X64Register.EAX, source);
            StoreSlot(instruction.Dst!, X64Register.EAX);
        }

        // ------------------------------------------------------------------
        // 算术/逻辑/位
        // ------------------------------------------------------------------

        private void EmitBinary(IrInstruction instruction)
        {
            var size = ToSize(RegisterSize(instruction.Dst!));
            LoadOperand(X64Register.EAX, instruction.A, size);
            LoadOperand(X64Register.ECX, instruction.B, size);

            switch (instruction.OpCode)
            {
                case IrOpCode.Add:
                    _a.Add(size, X64Register.EAX, X64Register.ECX);
                    break;
                case IrOpCode.Sub:
                    _a.Sub(size, X64Register.EAX, X64Register.ECX);
                    break;
                case IrOpCode.Imul:
                    _a.Imul(size, X64Register.EAX, X64Register.ECX);
                    break;
                case IrOpCode.And:
                    _a.And(size, X64Register.EAX, X64Register.ECX);
                    break;
                case IrOpCode.Or:
                    _a.Or(size, X64Register.EAX, X64Register.ECX);
                    break;
                case IrOpCode.Xor:
                    _a.Xor(size, X64Register.EAX, X64Register.ECX);
                    break;
            }

            StoreSlot(instruction.Dst!, X64Register.EAX);
        }

        private void EmitIdiv(IrInstruction instruction)
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

        private void EmitUdiv(IrInstruction instruction)
        {
            LoadSlot(X64Register.EAX, instruction.Dst!, RegisterSize(instruction.Dst!));
            LoadSlot(X64Register.ECX, instruction.A.Register!, RegisterSize(instruction.A.Register!));

            _a.Cmp(X64Size.Dword, X64Register.ECX, 0);
            _a.Jcc(X64CondCode.Equal, _nameToLabel["DivByZero"]);

            _a.Mov(X64Size.Dword, X64Register.EDX, 0);
            _a.Div(X64Size.Dword, X64Register.ECX);
            StoreSlot(instruction.Dst!, X64Register.EAX);
        }

        private void EmitUrem(IrInstruction instruction)
        {
            LoadSlot(X64Register.EAX, instruction.Dst!, RegisterSize(instruction.Dst!));
            LoadSlot(X64Register.ECX, instruction.A.Register!, RegisterSize(instruction.A.Register!));

            _a.Mov(X64Size.Dword, X64Register.EDX, 0);
            _a.Div(X64Size.Dword, X64Register.ECX);
            StoreSlot(instruction.Dst!, X64Register.EDX);
        }

        private void EmitUnary(IrInstruction instruction)
        {
            var size = ToSize(RegisterSize(instruction.Dst!));
            LoadSlot(X64Register.EAX, instruction.Dst!, RegisterSize(instruction.Dst!));

            if (instruction.OpCode == IrOpCode.Neg)
            {
                _a.Neg(size, X64Register.EAX);
            }
            else
            {
                _a.Not(size, X64Register.EAX);
            }

            StoreSlot(instruction.Dst!, X64Register.EAX);
        }

        private void EmitShift(IrInstruction instruction)
        {
            var size = ToSize(RegisterSize(instruction.Dst!));
            LoadSlot(X64Register.EAX, instruction.A.Register!, RegisterSize(instruction.A.Register!));

            if (instruction.B.Kind == IrOperandKind.Constant)
            {
                var count = (int)instruction.B.Imm;
                switch (instruction.OpCode)
                {
                    case IrOpCode.Shl:
                        _a.Shl(size, X64Register.EAX, count);
                        break;
                    case IrOpCode.Shr:
                        _a.Shr(size, X64Register.EAX, count);
                        break;
                    case IrOpCode.Sar:
                        _a.Sar(size, X64Register.EAX, count);
                        break;
                }
            }
            else
            {
                LoadSlot(X64Register.ECX, instruction.B.Register!, RegisterSize(instruction.B.Register!));
                switch (instruction.OpCode)
                {
                    case IrOpCode.Shl:
                        _a.Shl(size, X64Register.EAX);
                        break;
                    case IrOpCode.Shr:
                        _a.Shr(size, X64Register.EAX);
                        break;
                    case IrOpCode.Sar:
                        _a.Sar(size, X64Register.EAX);
                        break;
                }
            }

            StoreSlot(instruction.Dst!, X64Register.EAX);
        }

        // ------------------------------------------------------------------
        // 比较/标志
        // ------------------------------------------------------------------

        private void EmitCmp(IrInstruction instruction)
        {
            var size = ToSize(RegisterSize(instruction.A.Register!));
            LoadSlot(X64Register.EAX, instruction.A.Register!, RegisterSize(instruction.A.Register!));
            if (instruction.B.Kind == IrOperandKind.Constant)
            {
                _a.Cmp(size, X64Register.EAX, (int)instruction.B.Imm);
            }
            else
            {
                var operand = new X64MemoryOperand(X64Register.RBP, GetSlotOffset(instruction.B.Register!));
                _a.Cmp(ToSize(RegisterSize(instruction.B.Register!)), X64Register.EAX, operand);
            }
        }

        private void EmitSetcc(IrInstruction instruction)
        {
            _a.Setcc(MapCond((IrCond)instruction.A.Imm), X64Register.RAX);
            _a.Movzx(X64Size.Dword, X64Register.RAX, X64Register.RAX);
            StoreSlot(instruction.Dst!, X64Register.EAX);
        }

        // ------------------------------------------------------------------
        // 调用/返回
        // ------------------------------------------------------------------

        private void EmitCall(IrInstruction instruction)
        {
            _sysArgs.Clear();

            var aligned = false;

            if (instruction.A.Kind == IrOperandKind.Runtime)
            {
                aligned = EmitAlign(0);
                _a.Call(_nameToLabel[(string)instruction.A.Symbol!]);
            }
            else
            {
                // 锟矫伙拷锟斤拷锟斤拷锟斤拷锟矫的诧拷锟斤拷锟斤拷锟斤拷锟诫补锟斤拷锟斤拷锟斤拷 ReserveArgs 锟斤拷锟斤拷锟戒，FreeArgs 锟皆称恢革拷
                _a.Call(GetFunctionLabel((IrFunction)instruction.A.Symbol!));
            }

            if (aligned)
            {
                _a.Add(X64Size.Qword, X64Register.RSP, 8);
                _stackDepth--;
            }

            if (instruction.Dst != null)
            {
                StoreSlot(instruction.Dst, X64Register.EAX);
            }
        }

        private void EmitCallReg(IrInstruction instruction)
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
                StoreSlot(instruction.Dst, X64Register.EAX);
            }
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

        /// <summary>x64 参数区预留：对齐补丁（sub rsp, 8）在预留前发射（与参数区同属一个调用单元），
        /// 恢复由配对的 FreeArgs 完成（对齐判定与现有 EmitUserCall 一致：求值前深度 + 参数个数）。</summary>
        private void EmitReserveArgs(IrInstruction instruction)
        {
            var count = (int)instruction.A.Imm;
            var patch = _isX64 && (_stackDepth + count) % 2 != 0;
            if (patch)
            {
                _a.Sub(X64Size.Qword, X64Register.RSP, 8);
                _stackDepth++;
            }

            _alignStack.Push(patch);

            _a.Sub(SlotSize, X64Register.RSP, _slotSize * count);
            _stackDepth += count;
        }

        private void EmitFreeArgs(IrInstruction instruction)
        {
            var count = (int)instruction.A.Imm;
            _a.Add(SlotSize, X64Register.RSP, _slotSize * count);
            _stackDepth -= count;

            if (_alignStack.Pop())
            {
                _a.Add(X64Size.Qword, X64Register.RSP, 8);
                _stackDepth--;
            }
        }

        private void EmitRet(IrInstruction instruction)
        {
            _a.MarkLabel(GetLabel((int)instruction.A.Imm));

            var returnSize = _currentFunction!.ReturnSize;
            if (returnSize > 0)
            {
                var operand = new X64MemoryOperand(X64Register.RBP, -_slotSize);
                _a.Mov(ToSize(returnSize), X64Register.EAX, operand);
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

        private void EmitStoreRet(IrInstruction instruction)
        {
            LoadSlot(X64Register.EAX, instruction.A.Register!, RegisterSize(instruction.A.Register!));
            var operand = new X64MemoryOperand(X64Register.RBP, -_slotSize);
            _a.Mov(ToSize(RegisterSize(instruction.A.Register!)), operand, X64Register.EAX);
        }

        private void EmitStoreArg(IrInstruction instruction)
        {
            LoadSlot(X64Register.EAX, instruction.B.Register!, RegisterSize(instruction.B.Register!));
            var ordinal = (int)instruction.A.Imm;
            var operand = new X64MemoryOperand(X64Register.RSP, _slotSize * ordinal);
            _a.Mov(ToSize(RegisterSize(instruction.B.Register!)), operand, X64Register.EAX);
        }

        private void EmitSetArg(IrInstruction instruction)
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
                    break;
            }
        }

        // ------------------------------------------------------------------
        // 浮点（double）
        // 值在槽中以 64 位位模式存放：x64 单 8 字节槽；x86 双 4 字节槽（低地址=低 32 位）。
        // SSE 只做瞬时运算（XMM0/XMM1），与整型路径共用 eax/ecx/edx 装载/存储惯例。
        // ------------------------------------------------------------------

        private void EmitFConst(IrInstruction instruction)
        {
            var key = (string)instruction.A.Symbol!;
            _a.MovsdRip(X64Register.XMM0, _dataSymbols[key]);
            StoreSlotXmm(instruction.Dst!, X64Register.XMM0);
        }

        private void EmitFMove(IrInstruction instruction)
        {
            LoadSlotXmm(X64Register.XMM0, instruction.A.Register!);
            StoreSlotXmm(instruction.Dst!, X64Register.XMM0);
        }

        private void EmitFBinary(IrInstruction instruction)
        {
            LoadSlotXmm(X64Register.XMM0, instruction.A.Register!);
            LoadSlotXmm(X64Register.XMM1, instruction.B.Register!);

            switch (instruction.OpCode)
            {
                case IrOpCode.FAdd:
                    _a.Addsd(X64Register.XMM0, X64Register.XMM1);
                    break;
                case IrOpCode.FSub:
                    _a.Subsd(X64Register.XMM0, X64Register.XMM1);
                    break;
                case IrOpCode.FMul:
                    _a.Mulsd(X64Register.XMM0, X64Register.XMM1);
                    break;
                case IrOpCode.FDiv:
                    _a.Divsd(X64Register.XMM0, X64Register.XMM1);
                    break;
            }

            StoreSlotXmm(instruction.Dst!, X64Register.XMM0);
        }

        private void EmitFNeg(IrInstruction instruction)
        {
            var slot = GetSlotOffset(instruction.Dst!);
            if (_isX64)
            {
                _a.Mov(X64Size.Qword, X64Register.RAX, new X64MemoryOperand(X64Register.RBP, slot));
                _a.Mov(X64Register.RCX, unchecked((long)0x8000000000000000UL));
                _a.Xor(X64Size.Qword, X64Register.RAX, X64Register.RCX);
                _a.Mov(X64Size.Qword, new X64MemoryOperand(X64Register.RBP, slot), X64Register.RAX);
            }
            else
            {
                _a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.RBP, slot));
                _a.Mov(X64Size.Dword, X64Register.ECX, new X64MemoryOperand(X64Register.RBP, slot - 4));
                _a.Xor(X64Size.Dword, X64Register.ECX, unchecked((int)0x80000000));
                _a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, slot), X64Register.EAX);
                _a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, slot - 4), X64Register.ECX);
            }
        }

        private void EmitFCmp(IrInstruction instruction)
        {
            LoadSlotXmm(X64Register.XMM0, instruction.A.Register!);
            LoadSlotXmm(X64Register.XMM1, instruction.B.Register!);
            _a.Ucomisd(X64Register.XMM0, X64Register.XMM1);
        }

        private void EmitFCvtSI(IrInstruction instruction)
        {
            LoadSlot(X64Register.EAX, instruction.A.Register!, RegisterSize(instruction.A.Register!));
            _a.Cvtsi2sd(X64Register.XMM0, X64Register.EAX);
            StoreSlotXmm(instruction.Dst!, X64Register.XMM0);
        }

        private void EmitFCvtSD(IrInstruction instruction)
        {
            LoadSlotXmm(X64Register.XMM0, instruction.A.Register!);
            _a.Cvttsd2si(X64Register.EAX, X64Register.XMM0);
            StoreSlot(instruction.Dst!, X64Register.EAX);
        }

        /// <summary>double 运行时参数：x64 单 64 位寄存器（rcx/rdx/r8/r9）；x86 拆 low/high 两个 32 位寄存器。</summary>
        private void EmitSetArg64(IrInstruction instruction)
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

        private void EmitSysCall(IrInstruction instruction)
        {
            var import = (IrImport)instruction.A.Symbol!;
            var importSlot = _importSlots[import];
            var argCount = (int)instruction.B.Imm;
            var dst = instruction.Dst;

            if (_isX64)
            {
                // fastcall + shadow space：对齐补丁后 sub 0x30（shadow 0x20 + 第 5 参数槽 0x08，48≡0 mod 16）
                var aligned = EmitAlign(0);
                _a.Sub(X64Size.Qword, X64Register.RSP, 0x30);
                _stackDepth += 6;

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

                _a.CallRip(importSlot);
                _a.Add(X64Size.Qword, X64Register.RSP, 0x30);
                _stackDepth -= 6;

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

        private void EmitPush(IrInstruction instruction)
        {
            LoadSlot(X64Register.EAX, instruction.A.Register!, RegisterSize(instruction.A.Register!));
            _a.Push(X64Register.RAX);
            _stackDepth++;
        }

        private void EmitPop(IrInstruction instruction)
        {
            _a.Pop(X64Register.RAX);
            _stackDepth--;
            StoreSlot(instruction.Dst!, X64Register.EAX);
        }

        // ------------------------------------------------------------------
        // 操作数/槽
        // ------------------------------------------------------------------

        private int RegisterSize(IrVirtualRegister register) => _currentFunction!.RegisterSize(register);

        private void LoadOperand(X64Register reg, IrOperand operand, X64Size size)
        {
            if (operand.Kind == IrOperandKind.Constant)
            {
                _a.Mov(size == X64Size.Qword ? X64Size.Qword : X64Size.Dword, reg, (int)operand.Imm);
            }
            else
            {
                LoadSlot(reg, operand.Register!, RegisterSize(operand.Register!));
            }
        }

        private void LoadSlot(X64Register reg, IrVirtualRegister register, int size)
        {
            var operand = new X64MemoryOperand(X64Register.RBP, GetSlotOffset(register));
            _a.Mov(ToSize(size), reg, operand);
        }

        private void StoreSlot(IrVirtualRegister register, X64Register eax)
        {
            var operand = new X64MemoryOperand(X64Register.RBP, GetSlotOffset(register));
            _a.Mov(ToSize(RegisterSize(register)), operand, eax);
        }

        /// <summary>把 double 槽的 64 位位模式装入 XMM 寄存器（x86 槽宽 4：高 dword 在低槽位；x64 槽宽 8：槽内 +4）。</summary>
        private void LoadSlotXmm(X64Register xmm, IrVirtualRegister register)
        {
            var slot = GetSlotOffset(register);
            var hi = slot + (_isX64 ? 4 : -4);
            _a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.RBP, slot));
            _a.MovdGprToXmm(xmm, X64Register.EAX);
            _a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.RBP, hi));
            _a.Pinsrd(xmm, X64Register.EAX, 1);
        }

        /// <summary>把 XMM 寄存器的 64 位位模式存入 double 槽（pextrd + movd 拆分，两架构通用）。</summary>
        private void StoreSlotXmm(IrVirtualRegister register, X64Register xmm)
        {
            var slot = GetSlotOffset(register);
            var hi = slot + (_isX64 ? 4 : -4);
            _a.Pextrd(X64Register.EAX, xmm, 1);
            _a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, hi), X64Register.EAX);
            _a.MovdXmmToGpr(X64Register.EAX, xmm);
            _a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, slot), X64Register.EAX);
        }

        private int GetSlotOffset(IrVirtualRegister register)
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

        private int GetFunctionLabel(IrFunction function) => _functionLabels[function];

        private static X64Size ToSize(int byteSize) => byteSize switch
        {
            8 => X64Size.Qword,
            2 => X64Size.Word,
            _ => X64Size.Dword,
        };

        private static X64CondCode MapCond(IrCond cond)
        {
            switch (cond)
            {
                case IrCond.Equal: return X64CondCode.Equal;
                case IrCond.NotEqual: return X64CondCode.NotEqual;
                case IrCond.Less: return X64CondCode.Less;
                case IrCond.LessOrEqual: return X64CondCode.LessOrEqual;
                case IrCond.Greater: return X64CondCode.Greater;
                case IrCond.GreaterOrEqual: return X64CondCode.GreaterOrEqual;
                case IrCond.Below: return X64CondCode.Below;
                case IrCond.BelowOrEqual: return X64CondCode.BelowOrEqual;
                case IrCond.Above: return X64CondCode.Above;
                case IrCond.AboveOrEqual: return X64CondCode.AboveOrEqual;
                case IrCond.Parity: return X64CondCode.Parity;
                case IrCond.NoParity: return X64CondCode.NoParity;
                default:
                    throw new Exception($"Unknown IR cond: {cond}");
            }
        }
    }
}