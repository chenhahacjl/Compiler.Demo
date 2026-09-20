using Cocoa.CodeAnalysis.Symbols;

namespace Cocoa.CodeAnalysis.Binding
{
    public enum PrimitiveEvalStatus
    {
        /// <summary>result 有效。</summary>
        Computed,
        /// <summary>整数模零：折叠层跳过折叠、运行时层抛 DivideByZeroException。</summary>
        NotComputable,
        /// <summary>本核不含（string+double 定点拼接、引用相等）：调用方自行处理。</summary>
        Unsupported,
    }

    /// <summary>
    /// 单一求值核（5.4b）：ConstantFolding（编译期折叠）与 Interpreter（运行时求值）共用的
    /// 原生值运算语义唯一来源，消除双表人肉同步。
    /// 计算域约定（6e-M21）：整数按符号域——有符号在 long、无符号在 ulong（右移为逻辑移位），
    /// 结果按目标类型位宽归位（NumericBox.Box）；移位计数按位宽掩码；f32 在 float 域、f64 在 double 域；
    /// 相等比较对浮点用 IEEE 语义（NaN != NaN）、其余 object.Equals（值语义）。
    /// </summary>
    public static class PrimitiveEval
    {
        public static PrimitiveEvalStatus TryBinary(BoundBinaryOperatorKind kind, TypeSymbol type, object? left, object? right, out object? result)
        {
            result = null;

            if (type.IsInteger && !type.IsPlaceholder128)
            {
                return TryIntegerBinary(kind, type, left!, right!, out result);
            }

            if (type == TypeSymbol.Float)
            {
                return TryFloat32Binary(kind, left!, right!, out result);
            }

            switch (kind)
            {
                case BoundBinaryOperatorKind.Addition:
                    if (type == TypeSymbol.Int32)
                    {
                        result = (int)left! + (int)right!;
                        return PrimitiveEvalStatus.Computed;
                    }

                    if (type == TypeSymbol.Int64)
                    {
                        result = (long)left! + (long)right!;
                        return PrimitiveEvalStatus.Computed;
                    }

                    if (type == TypeSymbol.Double)
                    {
                        result = (double)left! + (double)right!;
                        return PrimitiveEvalStatus.Computed;
                    }

                    if (type == TypeSymbol.String)
                    {
                        // string + double：运行时按定点格式拼接（native/IL 后端），编译期不折叠、
                        // 解释器自行拼接——语义由调用方定夺，核不越权
                        if (right is double || left is double)
                        {
                            return PrimitiveEvalStatus.Unsupported;
                        }

                        result = (string?)left + (string?)right;
                        return PrimitiveEvalStatus.Computed;
                    }

                    break;
                case BoundBinaryOperatorKind.Subtraction:
                    if (type == TypeSymbol.Double)
                    {
                        result = (double)left! - (double)right!;
                        return PrimitiveEvalStatus.Computed;
                    }

                    if (type == TypeSymbol.Int64)
                    {
                        result = (long)left! - (long)right!;
                        return PrimitiveEvalStatus.Computed;
                    }

                    if (type == TypeSymbol.Int32)
                    {
                        result = (int)left! - (int)right!;
                        return PrimitiveEvalStatus.Computed;
                    }

                    break;
                case BoundBinaryOperatorKind.Multiplication:
                    if (type == TypeSymbol.Double)
                    {
                        result = (double)left! * (double)right!;
                        return PrimitiveEvalStatus.Computed;
                    }

                    if (type == TypeSymbol.Int64)
                    {
                        result = (long)left! * (long)right!;
                        return PrimitiveEvalStatus.Computed;
                    }

                    if (type == TypeSymbol.Int32)
                    {
                        result = (int)left! * (int)right!;
                        return PrimitiveEvalStatus.Computed;
                    }

                    break;
                case BoundBinaryOperatorKind.Division:
                    if (type == TypeSymbol.Double)
                    {
                        result = (double)left! / (double)right!;
                        return PrimitiveEvalStatus.Computed;
                    }

                    if (type == TypeSymbol.Int64)
                    {
                        if ((long)right! == 0)
                        {
                            return PrimitiveEvalStatus.NotComputable;
                        }

                        result = (long)left! / (long)right!;
                        return PrimitiveEvalStatus.Computed;
                    }

                    // 其余（Int32 及理论上不应出现的类型）按 int 域兜底——与两侧既有行为一致；
                    // 模零返回 NotComputable：折叠层跳过、运行时层抛 DivideByZeroException
                    if ((int)right! == 0)
                    {
                        return PrimitiveEvalStatus.NotComputable;
                    }

                    result = (int)left! / (int)right!;
                    return PrimitiveEvalStatus.Computed;
                case BoundBinaryOperatorKind.Modulo:
                    if (type == TypeSymbol.Int64)
                    {
                        if ((long)right! == 0)
                        {
                            return PrimitiveEvalStatus.NotComputable;
                        }

                        result = (long)left! % (long)right!;
                        return PrimitiveEvalStatus.Computed;
                    }

                    // 同 Division：int 域兜底
                    if ((int)right! == 0)
                    {
                        return PrimitiveEvalStatus.NotComputable;
                    }

                    result = (int)left! % (int)right!;
                    return PrimitiveEvalStatus.Computed;
                case BoundBinaryOperatorKind.ShiftLeft:
                    if (type == TypeSymbol.Int64)
                    {
                        result = (long)left! << (int)right!;
                        return PrimitiveEvalStatus.Computed;
                    }

                    if (type == TypeSymbol.Int32)
                    {
                        result = (int)left! << (int)right!;
                        return PrimitiveEvalStatus.Computed;
                    }

                    break;
                case BoundBinaryOperatorKind.ShiftRight:
                    if (type == TypeSymbol.Int64)
                    {
                        result = (long)left! >> (int)right!;
                        return PrimitiveEvalStatus.Computed;
                    }

                    if (type == TypeSymbol.Int32)
                    {
                        result = (int)left! >> (int)right!;
                        return PrimitiveEvalStatus.Computed;
                    }

                    break;
                case BoundBinaryOperatorKind.BitwiseAnd:
                case BoundBinaryOperatorKind.BitwiseOr:
                case BoundBinaryOperatorKind.BitwiseXor:
                    return TryBitwiseBinary(kind, type, left, right, out result);
                case BoundBinaryOperatorKind.LogicalAnd:
                    result = (bool)left! && (bool)right!;
                    return PrimitiveEvalStatus.Computed;
                case BoundBinaryOperatorKind.LogicalOr:
                    result = (bool)left! || (bool)right!;
                    return PrimitiveEvalStatus.Computed;
                case BoundBinaryOperatorKind.Equals:
                case BoundBinaryOperatorKind.NotEquals:
                    return TryEqualityBinary(kind, type, left, right, out result);
                case BoundBinaryOperatorKind.Less:
                case BoundBinaryOperatorKind.LessOrEquals:
                case BoundBinaryOperatorKind.Greater:
                case BoundBinaryOperatorKind.GreaterOrEquals:
                    return TryComparisonBinary(kind, type, left, right, out result);
            }

            return PrimitiveEvalStatus.Unsupported;
        }

        private static PrimitiveEvalStatus TryBitwiseBinary(BoundBinaryOperatorKind kind, TypeSymbol type, object? left, object? right, out object? result)
        {
            result = null;
            if (type == TypeSymbol.Int32 || type == TypeSymbol.Int64)
            {
                var a = type == TypeSymbol.Int64 ? (long)left! : (int)left!;
                var b = type == TypeSymbol.Int64 ? (long)right! : (int)right!;
                result = kind switch
                {
                    BoundBinaryOperatorKind.BitwiseAnd => a & b,
                    BoundBinaryOperatorKind.BitwiseOr => a | b,
                    _ => a ^ b,
                };
                return PrimitiveEvalStatus.Computed;
            }

            if (type == TypeSymbol.Boolean)
            {
                var a = (bool)left!;
                var b = (bool)right!;
                result = kind switch
                {
                    BoundBinaryOperatorKind.BitwiseAnd => a & b,
                    BoundBinaryOperatorKind.BitwiseOr => a | b,
                    _ => a ^ b,
                };
                return PrimitiveEvalStatus.Computed;
            }

            return PrimitiveEvalStatus.Unsupported;
        }

        private static PrimitiveEvalStatus TryEqualityBinary(BoundBinaryOperatorKind kind, TypeSymbol type, object? left, object? right, out object? result)
        {
            result = null;
            var equals = kind == BoundBinaryOperatorKind.Equals;

            // f64/f32 遵循 IEEE 相等性（NaN != NaN），不能走装箱值比较
            if (type == TypeSymbol.Double)
            {
                result = ((double)left! == (double)right!) == equals;
                return PrimitiveEvalStatus.Computed;
            }

            if (type == TypeSymbol.Float)
            {
                result = ((float)left! == (float)right!) == equals;
                return PrimitiveEvalStatus.Computed;
            }

            result = Equals(left, right) == equals;
            return PrimitiveEvalStatus.Computed;
        }

        private static PrimitiveEvalStatus TryComparisonBinary(BoundBinaryOperatorKind kind, TypeSymbol type, object? left, object? right, out object? result)
        {
            result = null;

            if (type == TypeSymbol.Double)
            {
                var a = (double)left!;
                var b = (double)right!;
                result = kind switch
                {
                    BoundBinaryOperatorKind.Less => a < b,
                    BoundBinaryOperatorKind.LessOrEquals => a <= b,
                    BoundBinaryOperatorKind.Greater => a > b,
                    _ => a >= b,
                };
                return PrimitiveEvalStatus.Computed;
            }

            if (type == TypeSymbol.Int64)
            {
                var a = (long)left!;
                var b = (long)right!;
                result = kind switch
                {
                    BoundBinaryOperatorKind.Less => a < b,
                    BoundBinaryOperatorKind.LessOrEquals => a <= b,
                    BoundBinaryOperatorKind.Greater => a > b,
                    _ => a >= b,
                };
                return PrimitiveEvalStatus.Computed;
            }

            // Int32 及其余可比域（枚举等按底层值装箱）直取 int 比较——与两侧既有行为一致
            var x = (int)left!;
            var y = (int)right!;
            result = kind switch
            {
                BoundBinaryOperatorKind.Less => x < y,
                BoundBinaryOperatorKind.LessOrEquals => x <= y,
                BoundBinaryOperatorKind.Greater => x > y,
                _ => x >= y,
            };
            return PrimitiveEvalStatus.Computed;
        }

        /// <summary>整数二元：有符号在 long 域、无符号在 ulong 域；模零返回 NotComputable。</summary>
        private static PrimitiveEvalStatus TryIntegerBinary(BoundBinaryOperatorKind kind, TypeSymbol type, object left, object right, out object? result)
        {
            result = null;

            if (type.IsSigned)
            {
                var a = NumericBox.ToSigned64(left);
                var b = NumericBox.ToSigned64(right);
                switch (kind)
                {
                    case BoundBinaryOperatorKind.Addition: result = NumericBox.Box(type, unchecked(a + b)); return PrimitiveEvalStatus.Computed;
                    case BoundBinaryOperatorKind.Subtraction: result = NumericBox.Box(type, unchecked(a - b)); return PrimitiveEvalStatus.Computed;
                    case BoundBinaryOperatorKind.Multiplication: result = NumericBox.Box(type, unchecked(a * b)); return PrimitiveEvalStatus.Computed;
                    case BoundBinaryOperatorKind.Division:
                        if (b == 0)
                        {
                            return PrimitiveEvalStatus.NotComputable;
                        }

                        result = NumericBox.Box(type, a / b);
                        return PrimitiveEvalStatus.Computed;
                    case BoundBinaryOperatorKind.Modulo:
                        if (b == 0)
                        {
                            return PrimitiveEvalStatus.NotComputable;
                        }

                        result = NumericBox.Box(type, a % b);
                        return PrimitiveEvalStatus.Computed;
                    case BoundBinaryOperatorKind.ShiftLeft: result = NumericBox.Box(type, a << ((int)b & (type.BitWidth - 1))); return PrimitiveEvalStatus.Computed;
                    case BoundBinaryOperatorKind.ShiftRight: result = NumericBox.Box(type, a >> ((int)b & (type.BitWidth - 1))); return PrimitiveEvalStatus.Computed;
                    case BoundBinaryOperatorKind.BitwiseAnd: result = NumericBox.Box(type, a & b); return PrimitiveEvalStatus.Computed;
                    case BoundBinaryOperatorKind.BitwiseOr: result = NumericBox.Box(type, a | b); return PrimitiveEvalStatus.Computed;
                    case BoundBinaryOperatorKind.BitwiseXor: result = NumericBox.Box(type, a ^ b); return PrimitiveEvalStatus.Computed;
                    case BoundBinaryOperatorKind.Equals: result = a == b; return PrimitiveEvalStatus.Computed;
                    case BoundBinaryOperatorKind.NotEquals: result = a != b; return PrimitiveEvalStatus.Computed;
                    case BoundBinaryOperatorKind.Less: result = a < b; return PrimitiveEvalStatus.Computed;
                    case BoundBinaryOperatorKind.LessOrEquals: result = a <= b; return PrimitiveEvalStatus.Computed;
                    case BoundBinaryOperatorKind.Greater: result = a > b; return PrimitiveEvalStatus.Computed;
                    case BoundBinaryOperatorKind.GreaterOrEquals: result = a >= b; return PrimitiveEvalStatus.Computed;
                }
            }
            else
            {
                var a = NumericBox.ToUnsigned64(left);
                var b = NumericBox.ToUnsigned64(right);
                switch (kind)
                {
                    case BoundBinaryOperatorKind.Addition: result = NumericBox.Box(type, unchecked(a + b)); return PrimitiveEvalStatus.Computed;
                    case BoundBinaryOperatorKind.Subtraction: result = NumericBox.Box(type, unchecked(a - b)); return PrimitiveEvalStatus.Computed;
                    case BoundBinaryOperatorKind.Multiplication: result = NumericBox.Box(type, unchecked(a * b)); return PrimitiveEvalStatus.Computed;
                    case BoundBinaryOperatorKind.Division:
                        if (b == 0UL)
                        {
                            return PrimitiveEvalStatus.NotComputable;
                        }

                        result = NumericBox.Box(type, a / b);
                        return PrimitiveEvalStatus.Computed;
                    case BoundBinaryOperatorKind.Modulo:
                        if (b == 0UL)
                        {
                            return PrimitiveEvalStatus.NotComputable;
                        }

                        result = NumericBox.Box(type, a % b);
                        return PrimitiveEvalStatus.Computed;
                    case BoundBinaryOperatorKind.ShiftLeft: result = NumericBox.Box(type, a << ((int)b & (type.BitWidth - 1))); return PrimitiveEvalStatus.Computed;
                    case BoundBinaryOperatorKind.ShiftRight: result = NumericBox.Box(type, a >> ((int)b & (type.BitWidth - 1))); return PrimitiveEvalStatus.Computed;
                    case BoundBinaryOperatorKind.BitwiseAnd: result = NumericBox.Box(type, a & b); return PrimitiveEvalStatus.Computed;
                    case BoundBinaryOperatorKind.BitwiseOr: result = NumericBox.Box(type, a | b); return PrimitiveEvalStatus.Computed;
                    case BoundBinaryOperatorKind.BitwiseXor: result = NumericBox.Box(type, a ^ b); return PrimitiveEvalStatus.Computed;
                    case BoundBinaryOperatorKind.Equals: result = a == b; return PrimitiveEvalStatus.Computed;
                    case BoundBinaryOperatorKind.NotEquals: result = a != b; return PrimitiveEvalStatus.Computed;
                    case BoundBinaryOperatorKind.Less: result = a < b; return PrimitiveEvalStatus.Computed;
                    case BoundBinaryOperatorKind.LessOrEquals: result = a <= b; return PrimitiveEvalStatus.Computed;
                    case BoundBinaryOperatorKind.Greater: result = a > b; return PrimitiveEvalStatus.Computed;
                    case BoundBinaryOperatorKind.GreaterOrEquals: result = a >= b; return PrimitiveEvalStatus.Computed;
                }
            }

            return PrimitiveEvalStatus.Unsupported;
        }

        /// <summary>f32 二元：float 域四则与比较。</summary>
        private static PrimitiveEvalStatus TryFloat32Binary(BoundBinaryOperatorKind kind, object left, object right, out object? result)
        {
            result = null;
            var a = (float)left;
            var b = (float)right;
            switch (kind)
            {
                case BoundBinaryOperatorKind.Addition: result = a + b; return PrimitiveEvalStatus.Computed;
                case BoundBinaryOperatorKind.Subtraction: result = a - b; return PrimitiveEvalStatus.Computed;
                case BoundBinaryOperatorKind.Multiplication: result = a * b; return PrimitiveEvalStatus.Computed;
                case BoundBinaryOperatorKind.Division: result = a / b; return PrimitiveEvalStatus.Computed;
                case BoundBinaryOperatorKind.Equals: result = a == b; return PrimitiveEvalStatus.Computed;
                case BoundBinaryOperatorKind.NotEquals: result = a != b; return PrimitiveEvalStatus.Computed;
                case BoundBinaryOperatorKind.Less: result = a < b; return PrimitiveEvalStatus.Computed;
                case BoundBinaryOperatorKind.LessOrEquals: result = a <= b; return PrimitiveEvalStatus.Computed;
                case BoundBinaryOperatorKind.Greater: result = a > b; return PrimitiveEvalStatus.Computed;
                case BoundBinaryOperatorKind.GreaterOrEquals: result = a >= b; return PrimitiveEvalStatus.Computed;
            }

            return PrimitiveEvalStatus.Unsupported;
        }

        /// <summary>
        /// 一元求值。Identity 非整数域原值返回（避免误入整型归位）；Negation/OnesComplement
        /// 对非整数域保留解释器既有的 int/long 直取回退（不可达路径，仅作兜底）。
        /// </summary>
        public static object? Unary(BoundUnaryOperatorKind kind, TypeSymbol operandType, TypeSymbol resultType, object? operand)
        {
            switch (kind)
            {
                case BoundUnaryOperatorKind.Identity:
                    // 与运行期语义一致：非整数类型（bool/string/enum 等）不做整型归位，原值直接返回
                    if (operandType.IsInteger && !operandType.IsPlaceholder128)
                    {
                        return NumericBox.Box(resultType, NumericBox.ToSigned64(operand!));
                    }

                    return operand;
                case BoundUnaryOperatorKind.Negation:
                    if (operandType.IsInteger && !operandType.IsPlaceholder128)
                    {
                        return NumericBox.Box(resultType, unchecked(-NumericBox.ToSigned64(operand!)));
                    }

                    if (operandType == TypeSymbol.Float)
                    {
                        return -(float)operand!;
                    }

                    if (operandType == TypeSymbol.Double)
                    {
                        return -(double)operand!;
                    }

                    return -(int)operand!;
                case BoundUnaryOperatorKind.LogicalNegation:
                    return !(bool)operand!;
                case BoundUnaryOperatorKind.OnesComplement:
                    if (operandType.IsInteger && !operandType.IsPlaceholder128)
                    {
                        return resultType.IsSigned
                            ? NumericBox.Box(resultType, ~NumericBox.ToSigned64(operand!))
                            : NumericBox.Box(resultType, ~NumericBox.ToUnsigned64(operand!));
                    }

                    if (operandType == TypeSymbol.Int64)
                    {
                        return ~(long)operand!;
                    }

                    return ~(int)operand!;
                default:
                    throw new Exception($"Unexpected unary operator {kind}");
            }
        }
    }
}
