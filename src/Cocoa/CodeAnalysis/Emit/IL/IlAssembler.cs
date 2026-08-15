using System;
using System.Collections.Generic;
using System.IO;

namespace Cocoa.CodeAnalysis.Emit.IL
{
    /// <summary>
    /// IL 编码器：IlInstruction 序列 → 方法体字节。按 OperandType 全集分派；
    /// 分支 fixup 用目标指令偏移回填；元数据/字符串 token 留占位由 <see cref="PatchTokens"/> 回填。
    /// </summary>
    internal sealed class IlAssembler
    {
        public List<IlInstruction> Instructions { get; } = new List<IlInstruction>();
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
                var token = tokens[fixup.Key];
                var offset = fixup.Offset;
                code[offset] = (byte)token;
                code[offset + 1] = (byte)(token >> 8);
                code[offset + 2] = (byte)(token >> 16);
                code[offset + 3] = (byte)(token >> 24);
            }
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
                depth += StackDelta(instruction);
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
                case IlOperandType.InlineType:
                    return 0; // Box/Newarr 等进出相抵
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
                case 0x5C: // Rem
                case 0x5F: // And
                case 0x60: // Or
                case 0x61: // Xor
                case 0x62: // Shl
                case 0x63: // Shr
                case 0x8E: // Ldlen
                case 0x94: // Ldelem_I4
                case 0x92: // Ldelem_I2
                case 0x93: // Ldelem_U2
                case 0xA0: // Ldelem_Ref
                    return -1;
                case 0x9D: // Stelem_I4
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
            var parameterCount = instruction.Operand switch
            {
                IlMethodRef methodRef => methodRef.ParameterTypes.Count,
                IlMethodDef methodDef => methodDef.ParameterTypes.Count,
                _ => 0,
            };

            var returnsValue = instruction.Operand switch
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
                case IlOperandType.InlineR:
                case IlOperandType.InlineMethod:
                case IlOperandType.InlineType:
                case IlOperandType.InlineField:
                case IlOperandType.InlineTok:
                case IlOperandType.InlineSig:
                case IlOperandType.InlineString:
                case IlOperandType.InlineBrTarget:
                    return 4;
                case IlOperandType.InlineI8:
                    return 8;
                case IlOperandType.InlineVar:
                    return 2;
                case IlOperandType.ShortInlineI:
                case IlOperandType.ShortInlineR:
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
}
