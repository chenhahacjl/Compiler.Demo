using System.Collections.Generic;
using System.Text;

using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Emit;

namespace Cocoa.CodeGen.Native.Lir
{
    /// <summary>IR 文本打印器：输出平台无关的中间表示（.coa 雏形）。</summary>
    internal static class LirPrinter
    {
        public static string Format(LirInstruction instruction)
        {
            var sb = new StringBuilder();
            sb.Append(instruction.OpCode.ToString().ToLowerInvariant());

            if (instruction.Dst != null)
            {
                sb.Append(' ').Append(instruction.Dst);
            }

            if (instruction.OpCode == LirOpCode.Jcc || instruction.OpCode == LirOpCode.Setcc)
            {
                sb.Append(' ').Append(((LirCond)instruction.A.Imm).ToString());
                if (instruction.B.IsNone == false)
                {
                    sb.Append(", ").Append(instruction.B.ToString());
                }

                return sb.ToString();
            }

            if (instruction.A.IsNone == false)
            {
                sb.Append(' ').Append(FormatOperand(instruction));
            }

            if (instruction.B.IsNone == false)
            {
                sb.Append(", ").Append(instruction.B.ToString());
            }

            if (instruction.OpCode != LirOpCode.Load && instruction.OpCode != LirOpCode.LoadSlotField && instruction.OpCode != LirOpCode.Store && instruction.OpCode != LirOpCode.StoreSlotField && instruction.Offset != 0)
            {
                sb.Append(instruction.Offset > 0 ? " +" : " ").Append(instruction.Offset);
            }

            if (instruction.ByteSize > 0)
            {
                sb.Append(" :").Append(instruction.ByteSize * 8).Append("bit");
            }

            return sb.ToString();
        }

        private static string FormatOperand(LirInstruction instruction)
        {
            var a = instruction.A;
            if (instruction.OpCode == LirOpCode.Load || instruction.OpCode == LirOpCode.LoadSlotField || instruction.OpCode == LirOpCode.Store || instruction.OpCode == LirOpCode.StoreSlotField)
            {
                var offset = instruction.Offset == 0 ? "" : (instruction.Offset > 0 ? "+" + instruction.Offset : instruction.Offset.ToString());
                return "[" + a.ToString() + offset + "]";
            }

            return a.ToString();
        }

        public static string Format(LirFunction function)
        {
            var sb = new StringBuilder();
            sb.Append("FUNCTION ").Append(function.Name);
            var paramNames = new List<string>();
            foreach (var parameter in function.Parameters)
            {
                paramNames.Add(parameter.Name ?? "p" + parameter.Ordinal);
            }

            if (paramNames.Count > 0)
            {
                sb.Append(" (").Append(string.Join(", ", paramNames)).Append(')');
            }

            sb.AppendLine();

            var blockIndex = 0;
            foreach (var block in function.Blocks)
            {
                sb.Append("bb").Append(blockIndex).Append(':');
                foreach (var labelId in block.Labels)
                {
                    sb.Append(" #L").Append(labelId);
                }

                sb.AppendLine();

                foreach (var instruction in block.Instructions)
                {
                    sb.Append("  ").AppendLine(Format(instruction));
                }

                if (block.Terminator != null)
                {
                    sb.Append("  ").AppendLine(Format(block.Terminator));
                }

                blockIndex++;
            }

            return sb.ToString();
        }

        public static string Format(LirTerminator terminator)
        {
            switch (terminator.Kind)
            {
                case LirTerminatorKind.Jump:
                    return "jmp L" + terminator.TargetLabelId;
                case LirTerminatorKind.CondJump:
                    return "jcc " + terminator.Cond + ", L" + terminator.TargetLabelId;
                case LirTerminatorKind.Return:
                    return "ret L" + terminator.TargetLabelId;
                default:
                    return "terminator";
            }
        }

        public static string Format(LirProgram program)
        {
            var sb = new StringBuilder();
            sb.AppendLine("PROGRAM entry = " + program.EntryFunctionName);

            if (program.Data.Count > 0)
            {
                sb.AppendLine(".data");
                foreach (var pair in program.Data)
                {
                    var text = pair.Value.Text ?? "";
                    sb.AppendLine("  D$" + text + " = \"" + Escape(text) + "\"");
                }
            }

            foreach (var function in program.Functions)
            {
                sb.AppendLine();
                sb.Append(Format(function));
            }

            return sb.ToString();
        }

        private static string Escape(string text)
        {
            var sb = new StringBuilder();
            foreach (var c in text)
            {
                sb.Append(c == '"' ? "\\\"" : c == '\\' ? "\\\\" : c.ToString());
            }

            return sb.ToString();
        }
    }
}