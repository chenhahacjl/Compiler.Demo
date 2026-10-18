using System.IO;
using System.Text;
using System.Collections.Immutable;
using Cocoa.CodeAnalysis.Bound;
using Cocoa.CodeAnalysis.Symbols;

namespace Cocoa.CodeAnalysis
{
    /// <summary>
    /// 构建中间件落盘（obj/ 目录约定；无 .cache/.stamp）：把 HIR / MIR 绑定树以文本写出——
    ///   <c>&lt;name&gt;.hir</c> ← <see cref="BoundProgram.RawFunctions"/>（绑定后未降级树）
    ///   <c>&lt;name&gt;.mir</c> ← <see cref="BoundProgram.Functions"/>（Lowerer 输出 goto 化规范树）
    /// LIR（native 3-地址码）由 NativeCodeEmitter 单独写 <c>&lt;name&gt;.lir</c>。
    /// </summary>
    internal static class IrDumper
    {
        public static void WriteProgram(BoundProgram program, string? irOutputDirectory, string name)
        {
            if (string.IsNullOrEmpty(irOutputDirectory))
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(irOutputDirectory);
                WriteTreeMap(Path.Combine(irOutputDirectory, name + ".hir"), program.RawFunctions);
                WriteTreeMap(Path.Combine(irOutputDirectory, name + ".mir"), program.Functions);
            }
            catch (Exception)
            {
                // 中间件 dump 属诊断工件：打印失败（含 BoundNodePrinter 未覆盖节点）绝不阻断构建
            }
        }

        private static void WriteTreeMap(string path, ImmutableDictionary<FunctionSymbol, BoundBlockStatement> bodies)
        {
            var sb = new StringBuilder();
            foreach (var (function, body) in bodies)
            {
                sb.Append("### ").Append(function.Name);
                if (function.ContainingClass != null)
                {
                    sb.Append(" @").Append(function.ContainingClass.FullName);
                }

                sb.AppendLine();
                try
                {
                    var buffer = new StringWriter();
                    BoundNodePrinter.WriteTo(body, buffer);
                    sb.AppendLine(buffer.ToString());
                }
                catch (Exception ex)
                {
                    // 单函数打印失败不中断整份 dump：落占位行
                    sb.AppendLine("// <unprintable: " + ex.Message + ">");
                }
            }

            File.WriteAllText(path, sb.ToString());
        }
    }
}