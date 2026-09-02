using System;
using System.Collections.Generic;
using System.IO;

using Cocoa.CodeAnalysis.Emit.IL;
namespace Cocoa.CodeAnalysis.Emit.Managed
{
    /// <summary>
    /// IL 编码器：IlInstruction 序列 → 方法体字节。按 OperandType 全集分派；
    /// 分支 fixup 用目标指令偏移回填；元数据/字符串 token 留占位由 <see cref="PatchTokens"/> 回填。
    /// </summary>
    internal sealed class IlAssembler
    {
        public List<IlInstruction> Instructions { get; } = new List<IlInstruction>();

        /// <summary>SEH 异常子句（.try/.catch/.finally）。标签用 IlInstruction 占位，Assemble 第一遍后取其 .Offset。</summary>
        public List<ExceptionClause> ExceptionClauses { get; } = new List<ExceptionClause>();
        private readonly List<(int Offset, object Key)> _tokenFixups = new List<(int, object)>();
        private readonly List<(int Offset, string Value)> _stringFixups = new List<(int, string)>();

        public void Emit(IlOpCode opCode, object? operand = null)
        {
            Instructions.Add(new IlInstruction(opCode, operand));
        }

        public void Emit(IlInstruction instruction)
        {
            Instructions.Add(instruction);
        }

        /// <summary>把指令序列编码为字节，回填分支偏移。</summary>
        public byte[] Assemble(List<IlInstruction>? instructions = null)
        {
            instructions ??= Instructions;

            // 第一遍：计算每条指令偏移
            var offset = 0;
            foreach (var instruction in instructions)
            {
                instruction.Offset = offset;
                offset += instruction.OpCode.Size + OperandSize(instruction);
            }

            _tokenFixups.Clear();
            _stringFixups.Clear();

            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);

            foreach (var instruction in instructions)
            {
                var opCode = instruction.OpCode;
                if (opCode.IsTwoByte)
                {
                    writer.Write((byte)0xFE);
                    writer.Write((byte)(opCode.Value & 0xFF));
                }
                else
                {
                    writer.Write((byte)opCode.Value);
                }

                EmitOperand(writer, instruction);
            }

            return stream.ToArray();
        }

        /// <summary>回填元数据 token（MethodDef/TypeDef/TypeRef/MemberRef/StandAloneSig）。</summary>
        public void PatchTokens(byte[] code, IReadOnlyDictionary<object, uint> tokens)
        {
            foreach (var fixup in _tokenFixups)
            {
                var key = NormalizeTokenKey(fixup.Key);
                var token = tokens[key];
                var offset = fixup.Offset;
                code[offset] = (byte)token;
                code[offset + 1] = (byte)(token >> 8);
                code[offset + 2] = (byte)(token >> 16);
                code[offset + 3] = (byte)(token >> 24);
            }
        }

        /// <summary>InlineType 指令（Box/Newarr/Castclass/Isinst 等）的 operand 可能是 IlType 包裹对象，规范化到其底层 TypeDef/TypeRef。</summary>
        private static object NormalizeTokenKey(object key)
        {
            if (key is IlType { TypeDef: not null } typeWithDef)
            {
                return typeWithDef.TypeDef;
            }

            if (key is IlType { Reference: not null } typeWithRef)
            {
                return typeWithRef.Reference;
            }

            return key;
        }

        /// <summary>回填 #US 字符串 token。</summary>
        public void PatchStrings(byte[] code, IReadOnlyDictionary<string, uint> stringTokens)
        {
            foreach (var fixup in _stringFixups)
            {
                var token = stringTokens[fixup.Value];
                var offset = fixup.Offset;
                code[offset] = (byte)token;
                code[offset + 1] = (byte)(token >> 8);
                code[offset + 2] = (byte)(token >> 16);
                code[offset + 3] = (byte)(token >> 24);
            }
        }

        /// <summary>Ldstr 引用的字符串（供 #US 堆注册）。</summary>
        public IEnumerable<string> StringFixupValues
        {
            get
            {
                foreach (var fixup in _stringFixups)
                {
                    yield return fixup.Value;
                }
            }
        }

        /// <summary>
        /// 计算方法体的最大栈深度（Fat 头 MaxStack 字段）。
        /// 线性扫描各指令净栈增量；分支处栈深为 0（条件先求值后分支），
        /// 单路径峰值对全路径恒为高估，满足运行时"只可高估不可低估"的要求。
        /// </summary>
        public int ComputeMaxStack(IReadOnlyList<IlInstruction>? instructions = null)
        {
            instructions ??= Instructions;

            var max = 0;
            var depth = 0;
            foreach (var instruction in instructions)
            {
                var delta = StackDelta(instruction);
                depth += delta;
                if (depth > max)
                {
                    max = depth;
                }
            }

            return max;
        }

        /// <summary>单条指令的净栈增量（正=入栈，负=出栈）。</summary>
        private static int StackDelta(IlInstruction instruction)
        {
            var name = instruction.OpCode.OperandType;
            switch (name)
            {
                case IlOperandType.InlineNone:
                    return StackDeltaInlineNone(instruction);
                case IlOperandType.InlineI:
                case IlOperandType.InlineI8:
                case IlOperandType.InlineR:
                case IlOperandType.ShortInlineR:
                case IlOperandType.ShortInlineI:
                case IlOperandType.InlineString:
                    return 1; // Ldstr 入栈 1
                case IlOperandType.InlineVar:
                case IlOperandType.ShortInlineVar:
                    return IsLoadInstruction(instruction) ? 1 : -1; // Ldarg/Ldloc 入栈；Stloc/Starg 出栈
                case IlOperandType.InlineMethod:
                    return StackDeltaInlineMethod(instruction);
                case IlOperandType.InlineField:
                    return instruction.OpCode.Value switch
                    {
                        0x7E => 1,  // Ldsfld
                        0x80 => -1, // Stsfld
                        0x7F => 1,  // Ldsflda（6e-M23 R6）
                        0x7C => 1,  // Ldflda（6e-M23 R6）
                        _ => 0,     // Ldfld（净 0）/Stfld（弹 2 但保守 0）
                    };
                case IlOperandType.InlineType:
                    return instruction.OpCode.Value == 0x8F ? -1 : 0; // Ldelema 弹数组+索引压地址；其余净 0
                case IlOperandType.InlineBrTarget:
                case IlOperandType.ShortInlineBrTarget:
                    return 0; // Br/Leave 不动栈；Brtrue/Brfalse 见 InlineNone 分派
                case IlOperandType.InlineSwitch:
                    return -1; // Switch 弹条件
                default:
                    return 0;
            }
        }

        private static int StackDeltaInlineNone(IlInstruction instruction)
        {
            switch (instruction.OpCode.Value)
            {
                case 0x2A: // Ret（弹返回值；深度钳制在调用方）
                    return -1;
                case 0x25: // Dup
                    return 1;
                case 0x26: // Pop
                case 0x39: // Brfalse
                case 0x3A: // Brtrue
                case 0x2C: // Brfalse_S
                case 0x2D: // Brtrue_S
                    return -1;
                case 0x15: // Ldc_I4_M1
                case 0x16: // Ldc_I4_0
                case 0x17: // Ldc_I4_1
                case 0x18: // Ldc_I4_2
                case 0x19: // Ldc_I4_3
                case 0x1A: // Ldc_I4_4
                case 0x1B: // Ldc_I4_5
                case 0x1C: // Ldc_I4_6
                case 0x1D: // Ldc_I4_7
                case 0x1E: // Ldc_I4_8
                case 0x14: // Ldnull
                case 0x02: // Ldarg_0
                case 0x03: // Ldarg_1
                case 0x04: // Ldarg_2
                case 0x05: // Ldarg_3
                case 0x06: // Ldloc_0
                case 0x07: // Ldloc_1
                case 0x08: // Ldloc_2
                case 0x09: // Ldloc_3
                    return 1;
                case 0x0A: // Stloc_0
                case 0x0B: // Stloc_1
                case 0x0C: // Stloc_2
                case 0x0D: // Stloc_3
                    return -1;
                case 0x58: // Add
                case 0x59: // Sub
                case 0x5A: // Mul
                case 0x5B: // Div
                case 0x5D: // Rem
                case 0x5F: // And
                case 0x60: // Or
                case 0x61: // Xor
                case 0x62: // Shl
                case 0x63: // Shr
                case 0x94: // Ldelem_I4
                case 0x90: // Ldelem_I1
                case 0x91: // Ldelem_U1
                case 0x92: // Ldelem_I2
                case 0x93: // Ldelem_U2
                case 0x97: // Ldelem_I8
                case 0x98: // Ldelem_R4
                case 0x99: // Ldelem_R8
                case 0x9A: // Ldelem_Ref
                    return -1;
                case 0x46: // Ldind_I1
                case 0x47: // Ldind_U1
                case 0x48: // Ldind_I2
                case 0x49: // Ldind_U2
                case 0x4A: // Ldind_I4
                case 0x4B: // Ldind_U4
                case 0x4C: // Ldind_I8
                case 0x4E: // Ldind_R4
                case 0x4F: // Ldind_R8
                case 0x50: // Ldind_Ref
                    return 1;
                case 0x51: // Stind_Ref
                case 0x52: // Stind_I1
                case 0x53: // Stind_I2
                case 0x54: // Stind_I4
                case 0x55: // Stind_I8
                case 0x56: // Stind_R4
                case 0x57: // Stind_R8
                    return -2;
                case 0x8E: // Ldlen（弹数组引用，压长度）
                    return 0;
                case 0x9C: // Stelem_I1
                case 0x9D: // Stelem_I2
                case 0x9E: // Stelem_I4
                case 0x9F: // Stelem_I8
                case 0xA1: // Stelem_R8
                case 0xA2: // Stelem_Ref
                    return -3;
                case 0x65: // Neg
                case 0x66: // Not
                case 0x67: // Conv_I1
                case 0x68: // Conv_I2
                case 0x69: // Conv_I4
                case 0x6A: // Conv_I8
                case 0x6B: // Conv_R4
                case 0x6C: // Conv_R8
                case 0x6D: // Conv_U4
                case 0x6E: // Conv_U8
                case 0xD2: // Conv_U1
                case 0xD1: // Conv_U2
                    return 0;
                case 0xFE01: // Ceq
                case 0xFE02: // Cgt
                case 0xFE04: // Clt
                    return -1;
                case 0xFE06: // Ldftn
                    return 1;
                default:     // Nop/Leave/Endfinally 等
                    return 0;
            }
        }

        /// <summary>InlineVar/ShortInlineVar 操作数中仅加载类指令入栈。</summary>
        private static bool IsLoadInstruction(IlInstruction instruction)
        {
            switch (instruction.OpCode.Value)
            {
                case 0x0E: // Ldarg_S
                case 0x11: // Ldloc_S
                case 0xFE09: // Ldarg
                case 0xFE0A: // Ldarga
                case 0xFE0C: // Ldloc
                case 0xFE0D: // Ldloca
                    return true;
                default: // Stloc_S(0x13)/Stloc(0xFE0E)/Starg 等
                    return false;
            }
        }

        private static int StackDeltaInlineMethod(IlInstruction instruction)
        {
            // Ldftn：压入方法指针（与目标签名无关，净 +1）
            if (instruction.OpCode.Value == 0xFE06)
            {
                return 1;
            }

            var parameterCount = instruction.Operand switch
            {
                IlMethodRef methodRef => methodRef.ParameterTypes.Count,
                IlMethodDef methodDef => methodDef.ParameterTypes.Count,
                _ => 0,
            };

            var returnsValue = instruction.OpCode.Value == 0x73 // Newobj：压入新对象实例（.ctor 返回 void 但压栈 1）
                ? true
                : instruction.Operand switch
                {
                    IlMethodRef methodRef => methodRef.ReturnType.Kind != IlTypeKind.Void,
                    IlMethodDef methodDef => methodDef.ReturnType.Kind != IlTypeKind.Void,
                    _ => false,
                };

            var instanceCount = instruction.OpCode.Value == 0x6F ? 1 : 0; // Callvirt 额外弹 this
            return (returnsValue ? 1 : 0) - parameterCount - instanceCount;
        }

        private void EmitOperand(BinaryWriter writer, IlInstruction instruction)
        {
            var opCode = instruction.OpCode;
            var startOffset = instruction.Offset + opCode.Size;

            switch (opCode.OperandType)
            {
                case IlOperandType.InlineNone:
                    break;
                case IlOperandType.InlineI:
                    writer.Write((int)instruction.Operand!);
                    break;
                case IlOperandType.InlineI8:
                    writer.Write((long)instruction.Operand!);
                    break;
                case IlOperandType.InlineR:
                    writer.Write((double)instruction.Operand!);
                    break;
                case IlOperandType.ShortInlineR:
                    writer.Write((float)instruction.Operand!);
                    break;
                case IlOperandType.ShortInlineI:
                    writer.Write((sbyte)instruction.Operand!);
                    break;
                case IlOperandType.InlineVar:
                    writer.Write((ushort)instruction.Operand!);
                    break;
                case IlOperandType.ShortInlineVar:
                    writer.Write((byte)instruction.Operand!);
                    break;
                case IlOperandType.InlineString:
                    _stringFixups.Add(((int)writer.BaseStream.Position, (string)instruction.Operand!));
                    writer.Write(0xFFFFFFFF);
                    break;
                case IlOperandType.InlineMethod:
                case IlOperandType.InlineType:
                case IlOperandType.InlineField:
                case IlOperandType.InlineTok:
                case IlOperandType.InlineSig:
                    _tokenFixups.Add(((int)writer.BaseStream.Position, instruction.Operand!));
                    writer.Write(0xFFFFFFFF);
                    break;
                case IlOperandType.InlineBrTarget:
                    {
                        var target = (IlInstruction)instruction.Operand!;
                        writer.Write(target.Offset - (startOffset + 4));
                        break;
                    }
                case IlOperandType.ShortInlineBrTarget:
                    {
                        var target = (IlInstruction)instruction.Operand!;
                        writer.Write((sbyte)(target.Offset - (startOffset + 1)));
                        break;
                    }
                case IlOperandType.InlineSwitch:
                    {
                        var targets = (IlInstruction[])instruction.Operand!;
                        writer.Write((uint)targets.Length);
                        var baseOffset = startOffset + 4 + 4 * targets.Length;
                        foreach (var target in targets)
                        {
                            writer.Write(target.Offset - baseOffset);
                        }
                        break;
                    }
                default:
                    throw new InvalidOperationException($"Unhandled operand type {opCode.OperandType}");
            }
        }

        private static int OperandSize(IlInstruction instruction)
        {
            switch (instruction.OpCode.OperandType)
            {
                case IlOperandType.InlineNone:
                    return 0;
                case IlOperandType.InlineI:
                case IlOperandType.InlineMethod:
                case IlOperandType.InlineType:
                case IlOperandType.InlineField:
                case IlOperandType.InlineTok:
                case IlOperandType.InlineSig:
                case IlOperandType.InlineString:
                case IlOperandType.InlineBrTarget:
                    return 4;
                case IlOperandType.InlineR:
                    return 8;
                case IlOperandType.InlineI8:
                    return 8;
                case IlOperandType.InlineVar:
                    return 2;
                case IlOperandType.ShortInlineR:
                    return 4;
                case IlOperandType.ShortInlineI:
                case IlOperandType.ShortInlineVar:
                case IlOperandType.ShortInlineBrTarget:
                    return 1;
                case IlOperandType.InlineSwitch:
                    return 4 + 4 * ((IlInstruction[])instruction.Operand!).Length;
                default:
                    throw new InvalidOperationException($"Unhandled operand type {instruction.OpCode.OperandType}");
            }
        }
    }

    /// <summary>.try/.catch/.finally 子句描述（SEH 异常表）。各边界用 IlInstruction 占位（Assemble 后取 .Offset）。</summary>
    internal sealed class ExceptionClause
    {
        public IlInstruction TryStart = null!;
        public IlInstruction TryEnd = null!;
        public IlInstruction HandlerStart = null!;
        public IlInstruction HandlerEnd = null!;
        public int HandlerKind; // 0 = catch，2 = finally
        public IlType? CatchType; // catch 用：被捕获类型（token 来自 BuildTokenMap）
    }
}
