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
        private readonly List<IlInstruction> _instructions = new List<IlInstruction>();
        private readonly List<(int Offset, object Key)> _tokenFixups = new List<(int, object)>();
        private readonly List<(int Offset, string Value)> _stringFixups = new List<(int, string)>();

        public void Emit(IlOpCode opCode, object? operand = null)
        {
            _instructions.Add(new IlInstruction(opCode, operand));
        }

        /// <summary>把指令序列编码为字节，回填分支偏移。</summary>
        public byte[] Assemble(List<IlInstruction> instructions)
        {
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
