using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Cocoa.CodeGen.Native.Assembler;
using Cocoa.CodeGen.Native.Lir;

namespace Cocoa.CodeGen.Native
{
    /// <summary>
    /// 布局无关符号化 dump（M5-a4 续作切片）：把函数字节中的 rel32 目标归一化为符号名，
    /// 使差分不依赖 .text 整体布局（runtime 函数追加/reloc 偏移皆无影响）。
    /// 视图：每函数一段，条目 = `{名称}/{符号化hex}`，其中 fixup 位置以 `<sym>` 占位。
    /// 差分口径：C# base 与自举 LirToAssembler 输出逐一字符串比对。
    /// </summary>
    internal sealed partial class LirToAssembler
    {
        private const string SymbolicDumpEnv = "COCOA_DUMP_SYM";

        private static string DumpFile => System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "cocoa-sym-x64.txt");

        private void DumpSymbols()
        {
            var sb = new StringBuilder();
            var bytes = _a.ToArray();
            var asmFixups = _a.LabelFixups.ToArray();

            // 函数发射序（含 runtime 追加件）：入口 label 偏移排序以切段
            var functionRanges = _program.Functions
                .Select(f => (Function: f, Start: _a.GetLabelOffset(_functionLabels[f])))
                .OrderBy(t => t.Start)
                .ToArray();

            for (var i = 0; i < functionRanges.Length; i++)
            {
                var start = functionRanges[i].Start;
                var end = i + 1 < functionRanges.Length ? functionRanges[i + 1].Start : bytes.Length;
                sb.Append("@@FUNC ").Append(functionRanges[i].Function.Name).AppendLine();

                var segment = bytes.Skip(start).Take(end - start).ToArray();
                // 段内 fixup：rel32 部位置换为符号
                var relocMarker = new List<(int RelStart, string Symbol)>();
                foreach (var fix in asmFixups)
                {
                    if (fix.Offset >= start && fix.Offset + 4 <= end)
                    {
                        var symbol = _labelSymbols.TryGetValue(fix.Label, out var s) ? s : ("L?" + fix.Label);
                        relocMarker.Add((fix.Offset - start, symbol));
                    }
                }

                sb.AppendLine(FormatSymbolicSegment(segment, relocMarker));
            }

            try
            {
                File.WriteAllText(DumpFile, sb.ToString());
            }
            catch
            {
            }
        }

        private static string FormatSymbolicSegment(byte[] segment, List<(int RelStart, string Symbol)> relocs)
        {
            var tokenBuilder = new StringBuilder();
            var relocsByPos = relocs.ToDictionary(r => r.RelStart, r => r.Symbol);
            var i = 0;
            while (i < segment.Length)
            {
                if (relocsByPos.TryGetValue(i, out var symbol))
                {
                    tokenBuilder.Append('<').Append(symbol).Append('>');
                    i += 4;
                    continue;
                }

                tokenBuilder.Append(segment[i].ToString("x2"));
                i++;
            }

            return tokenBuilder.ToString();
        }
    }
}