using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Bound;
using Cocoa.Metadata;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
namespace Cocoa.CodeGen.Managed.Writer
{
    /// <summary>
    /// 数值转换 IL 发射（自 IlEmitter.Statements 拆出：decimal/half/数值转换）。
    /// </summary>
    internal sealed partial class IlEmitter
    {
        /// <summary>
        /// decimal/half（非 IL 基元）转换：经 System.Decimal/System.Half 静态 op_* 调用（C# 语义：
        /// decimal→整数 截断取整、decimal→浮点 保精度；op_Explicit 重载按返回类型消歧）。
        /// </summary>
        private void EmitDecimalHalfConversion(IlAssembler il, TypeSymbol from, TypeSymbol to)
        {
            if (to == TypeSymbol.Decimal)
            {
                // 整数/char → decimal：op_Implicit(源类型)；float/double → decimal：op_Explicit(源类型)
                var sourceName = BoxedTypeName(from) ?? throw new System.Exception($"Unexpected decimal source '{from}'");
                var opName = from.IsFloat ? "op_Explicit" : "op_Implicit";
                il.Emit(IlOpCodeTable.Get("Call"), _framework.RequireMethod("System.Decimal", opName, new[] { sourceName }));
                return;
            }

            if (from == TypeSymbol.Decimal)
            {
                if (to == TypeSymbol.String)
                {
                    il.Emit(IlOpCodeTable.Get("Box"), _framework.RequireType("System.Decimal"));
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.ConvertToString);
                    return;
                }

                il.Emit(IlOpCodeTable.Get("Call"), _framework.RequireMethod("System.Decimal", "op_Explicit", new[] { "System.Decimal" }, returnTypeName: BoxedTypeName(to)));
                return;
            }

            // half：float/double → half = op_Explicit(源)；half → float/double = op_Explicit(Half) 按返回消歧；half → string = box + Convert.ToString
            if (from == TypeSymbol.Float16 || to == TypeSymbol.Float16)
            {
                EmitHalfConversion(il, from, to);
                return;
            }

            throw new System.Exception($"Unexpected decimal/half conversion ({from} → {to})");
        }

        /// <summary>half 转换：System.Half::op_Explicit 双向（float/double→half 按源参数唯一；half→float/double 按返回类型消歧）。</summary>
        private void EmitHalfConversion(IlAssembler il, TypeSymbol from, TypeSymbol to)
        {
            if (from == TypeSymbol.Float16)
            {
                if (to == TypeSymbol.Float || to == TypeSymbol.Double)
                {
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.RequireMethod("System.Half", "op_Explicit", new[] { "System.Half" }, returnTypeName: to == TypeSymbol.Float ? "System.Single" : "System.Double"));
                    return;
                }

                if (to == TypeSymbol.String)
                {
                    il.Emit(IlOpCodeTable.Get("Box"), _framework.RequireType("System.Half"));
                    il.Emit(IlOpCodeTable.Get("Call"), _framework.ConvertToString);
                    return;
                }

                throw new System.Exception($"Unexpected half conversion target '{to}'");
            }

            if (to == TypeSymbol.Float16 && (from == TypeSymbol.Float || from == TypeSymbol.Double))
            {
                il.Emit(IlOpCodeTable.Get("Call"), _framework.RequireMethod("System.Half", "op_Explicit", new[] { from == TypeSymbol.Float ? "System.Single" : "System.Double" }));
                return;
            }

            throw new System.Exception($"Unexpected half conversion ({from} → {to})");
        }

        /// <summary>
        /// 6e-M21 Phase 4：数值↔数值转换的系统化 CIL 发射。
        /// 栈表示：≤32 位整数均为 int32 栈；i64/u64 为 int64 栈；f32/f64 为 F 栈。
        /// 无符号宽整型转浮点先归位（Conv_U4/Conv_U8）再转，保证大值正确。
        /// </summary>
        private bool TryEmitNumericConversion(IlAssembler il, TypeSymbol from, TypeSymbol to)
        {
            if (to.IsPlaceholder128 || from.IsPlaceholder128)
            {
                return false;
            }

            var fromIsNumericLike = from.IsNumeric || from == TypeSymbol.Char || from is NamedTypeSymbol { TypeKind: TypeKind.Enum };
            if (!to.IsNumeric || !fromIsNumericLike)
            {
                return false;
            }

            if (from == TypeSymbol.String || to == TypeSymbol.String)
            {
                return false; // 字符串互转走原有专用路径
            }

            switch (to.Name)
            {
                case "sbyte":
                    il.Emit(IlOpCodeTable.Get("Conv_I1"));
                    return true;
                case "byte":
                    il.Emit(IlOpCodeTable.Get("Conv_U1"));
                    return true;
                case "short":
                    il.Emit(IlOpCodeTable.Get("Conv_I2"));
                    return true;
                case "ushort":
                    il.Emit(IlOpCodeTable.Get("Conv_U2"));
                    return true;
                case "int":
                    if (from == TypeSymbol.Int64)
                        il.Emit(IlOpCodeTable.Get("Conv_I4"));
                    else if (from == TypeSymbol.UInt64)
                        il.Emit(IlOpCodeTable.Get("Conv_U4"));
                    else if (from.IsFloat)
                        il.Emit(IlOpCodeTable.Get("Conv_I4"));
                    // ≤32 位整数/char/enum → int：栈同宽，无需指令
                    return true;
                case "uint":
                    if (from == TypeSymbol.Int64 || from == TypeSymbol.UInt64 || from.IsFloat)
                        il.Emit(IlOpCodeTable.Get("Conv_U4"));
                    return true;
                case "long":
                    if (from != TypeSymbol.Int64 && from != TypeSymbol.UInt64)
                    {
                        if (from == TypeSymbol.UInt32 || from == TypeSymbol.UInt16 || from == TypeSymbol.UInt8)
                        {
                            // 零扩展到 int64 栈
                            il.Emit(IlOpCodeTable.Get("Conv_U8"));
                        }
                        else
                        {
                            il.Emit(IlOpCodeTable.Get("Conv_I8"));
                        }
                    }

                    return true;
                case "ulong":
                    if (from != TypeSymbol.Int64 && from != TypeSymbol.UInt64)
                    {
                        if (from == TypeSymbol.Int8 || from == TypeSymbol.Int16 ||
                            from == TypeSymbol.Int32 || from == TypeSymbol.Char ||
                            from is NamedTypeSymbol { TypeKind: TypeKind.Enum })
                        {
                            // 符号扩展位模式进入 int64 栈
                            il.Emit(IlOpCodeTable.Get("Conv_I8"));
                        }
                        else if (from.IsFloat)
                        {
                            // 浮点→u64：C# 语义为截断取整后按 ulong 解释
                            il.Emit(IlOpCodeTable.Get("Conv_U8"));
                        }
                        else
                        {
                            il.Emit(IlOpCodeTable.Get("Conv_U8"));
                        }
                    }

                    return true;
                case "float":
                    if (from == TypeSymbol.Double)
                    {
                        il.Emit(IlOpCodeTable.Get("Conv_R4"));
                    }
                    else if (!from.IsFloat)
                    {
                        if (from == TypeSymbol.UInt64)
                        {
                            il.Emit(IlOpCodeTable.Get("Conv_U8"));
                        }
                        else if (from == TypeSymbol.UInt32)
                        {
                            il.Emit(IlOpCodeTable.Get("Conv_U4"));
                        }

                        il.Emit(IlOpCodeTable.Get("Conv_R4"));
                    }

                    return true;
                case "double":
                    if (from == TypeSymbol.Float)
                    {
                        il.Emit(IlOpCodeTable.Get("Conv_R8"));
                    }
                    else if (!from.IsFloat)
                    {
                        if (from == TypeSymbol.UInt64)
                        {
                            il.Emit(IlOpCodeTable.Get("Conv_U8"));
                        }
                        else if (from == TypeSymbol.UInt32)
                        {
                            il.Emit(IlOpCodeTable.Get("Conv_U4"));
                        }

                        il.Emit(IlOpCodeTable.Get("Conv_R8"));
                    }

                    return true;
            }

            return false;
        }
    }
}
