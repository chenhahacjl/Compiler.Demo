using Cocoa.CodeAnalysis.Symbols;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>二元运算符签名（规则表条目）：语义 kind + 操作数/返回类型 + 发射键（阶段 2 符号化预留）。</summary>
    public readonly record struct BinaryOperatorSignature(
        BoundBinaryOperatorKind Kind,
        TypeSymbol LeftType,
        TypeSymbol RightType,
        TypeSymbol ResultType,
        Cocoa.CodeAnalysis.Symbols.BuiltinKind? EmitKind = null);

    /// <summary>一元运算符签名（规则表条目）。</summary>
    public readonly record struct UnaryOperatorSignature(
        BoundUnaryOperatorKind Kind,
        TypeSymbol OperandType,
        TypeSymbol ResultType,
        Cocoa.CodeAnalysis.Symbols.BuiltinKind? EmitKind = null);

    /// <summary>
    /// 内置运算符规则表（对齐 Roslyn <c>BuiltInOperators</c> 形态，职责 1-4）：静态签名表 + 按 kind 查询 +
    /// 「组合运算符」候选合成（enum/delegate/string/null/引用相等/??）。合成结果用于重载解析的候选集枚举
    /// （阶段 2 起符号化经 <see cref="EmitKind"/> 映射）。不携带发射逻辑——发射仍由
    /// <see cref="BoundBinaryOperator.Bind"/> / <see cref="BoundUnaryOperator.Bind"/> 按既有 kind 直分派。
    /// </summary>
    public static class BuiltInOperators
    {
        private static readonly BinaryOperatorSignature[] _binary = BuildBinaryOperators();
        private static readonly UnaryOperatorSignature[] _unary = BuildUnaryOperators();

        /// <summary>按 (kind, left, right) 查询精确匹配的内建二元签名（不含后果类型组合——组合由 <see cref="BindComposite"/> 处理）。</summary>
        public static BinaryOperatorSignature? GetSignature(BoundBinaryOperatorKind kind, TypeSymbol leftType, TypeSymbol rightType)
        {
            foreach (var s in _binary)
            {
                if (s.Kind == kind && s.LeftType == leftType && s.RightType == rightType)
                {
                    return s;
                }
            }

            return null;
        }

        /// <summary>批量取某一 kind 的全部内建二元签名（候选集枚举源）。</summary>
        public static IReadOnlyList<BinaryOperatorSignature> GetSimpleBuiltInOperators(BoundBinaryOperatorKind kind)
        {
            var list = new List<BinaryOperatorSignature>();
            foreach (var s in _binary)
            {
                if (s.Kind == kind)
                {
                    list.Add(s);
                }
            }

            return list;
        }

        /// <summary>按 (kind, operandType) 查询内建一元签名。</summary>
        public static UnaryOperatorSignature? GetSignature(BoundUnaryOperatorKind kind, TypeSymbol operandType)
        {
            foreach (var s in _unary)
            {
                if (s.Kind == kind && s.OperandType == operandType)
                {
                    return s;
                }
            }

            return null;
        }

        /// <summary>批量取某一 kind 的全部内建一元签名。</summary>
        public static IReadOnlyList<UnaryOperatorSignature> GetSimpleBuiltInOperators(BoundUnaryOperatorKind kind)
        {
            var list = new List<UnaryOperatorSignature>();
            foreach (var s in _unary)
            {
                if (s.Kind == kind)
                {
                    list.Add(s);
                }
            }

            return list;
        }

        /// <summary>
        /// 6e-M21 Phase 1：程序化生成二元运算符表——10 个数值类型（i8/i16/i32/i64/u8/u16/u32/u64/f32/f64）
        /// 各一份完整集合；混合精度由 Binder 的二元提升先归一到公共类型再查表。
        /// </summary>
        private static BinaryOperatorSignature[] BuildBinaryOperators()
        {
            var ops = new List<BinaryOperatorSignature>
            {
                // bool：逻辑/位/相等
                new(BoundBinaryOperatorKind.BitwiseAnd, TypeSymbol.Boolean, TypeSymbol.Boolean, TypeSymbol.Boolean),
                new(BoundBinaryOperatorKind.LogicalAnd, TypeSymbol.Boolean, TypeSymbol.Boolean, TypeSymbol.Boolean),
                new(BoundBinaryOperatorKind.BitwiseOr, TypeSymbol.Boolean, TypeSymbol.Boolean, TypeSymbol.Boolean),
                new(BoundBinaryOperatorKind.LogicalOr, TypeSymbol.Boolean, TypeSymbol.Boolean, TypeSymbol.Boolean),
                new(BoundBinaryOperatorKind.BitwiseXor, TypeSymbol.Boolean, TypeSymbol.Boolean, TypeSymbol.Boolean),
                new(BoundBinaryOperatorKind.Equals, TypeSymbol.Boolean, TypeSymbol.Boolean, TypeSymbol.Boolean),
                new(BoundBinaryOperatorKind.NotEquals, TypeSymbol.Boolean, TypeSymbol.Boolean, TypeSymbol.Boolean),

                // string：拼接与相等
                new(BoundBinaryOperatorKind.Addition, TypeSymbol.String, TypeSymbol.String, TypeSymbol.String),
                new(BoundBinaryOperatorKind.Addition, TypeSymbol.String, TypeSymbol.Double, TypeSymbol.String),
                new(BoundBinaryOperatorKind.Equals, TypeSymbol.String, TypeSymbol.String, TypeSymbol.Boolean),
                new(BoundBinaryOperatorKind.NotEquals, TypeSymbol.String, TypeSymbol.String, TypeSymbol.Boolean),

                // char：相等
                new(BoundBinaryOperatorKind.Equals, TypeSymbol.Char, TypeSymbol.Char, TypeSymbol.Boolean),
                new(BoundBinaryOperatorKind.NotEquals, TypeSymbol.Char, TypeSymbol.Char, TypeSymbol.Boolean),
            };

            var numericTypes = new[]
            {
                TypeSymbol.Int8, TypeSymbol.Int16, TypeSymbol.Int32, TypeSymbol.Int64,
                TypeSymbol.UInt8, TypeSymbol.UInt16, TypeSymbol.UInt32, TypeSymbol.UInt64,
                TypeSymbol.Float, TypeSymbol.Double,
            };

            foreach (var t in numericTypes)
            {
                if (t.IsInteger)
                {
                    // 6e-M21 Phase 6：<32 位窄整型不注册算术/移位/位运算条目——
                    // 二元运算先经 GetBinaryNumericResultType 升到 32/64 位域再查表（C# 先升后算同构），
                    // 否则 i16*i16 等会在窄域静默截断（如 (i16)300*(i16)300=24464 假象）。
                    if (t.BitWidth >= 32)
                    {
                        ops.Add(new(BoundBinaryOperatorKind.Addition, t, t, t));
                        ops.Add(new(BoundBinaryOperatorKind.Subtraction, t, t, t));
                        ops.Add(new(BoundBinaryOperatorKind.Multiplication, t, t, t));
                        ops.Add(new(BoundBinaryOperatorKind.Division, t, t, t));
                        ops.Add(new(BoundBinaryOperatorKind.Modulo, t, t, t));
                        ops.Add(new(BoundBinaryOperatorKind.ShiftLeft, t, t, t));
                        ops.Add(new(BoundBinaryOperatorKind.ShiftRight, t, t, t));
                        ops.Add(new(BoundBinaryOperatorKind.BitwiseAnd, t, t, t));
                        ops.Add(new(BoundBinaryOperatorKind.BitwiseOr, t, t, t));
                        ops.Add(new(BoundBinaryOperatorKind.BitwiseXor, t, t, t));
                    }
                }
                else
                {
                    ops.Add(new(BoundBinaryOperatorKind.Addition, t, t, t));
                    ops.Add(new(BoundBinaryOperatorKind.Subtraction, t, t, t));
                    ops.Add(new(BoundBinaryOperatorKind.Multiplication, t, t, t));
                    ops.Add(new(BoundBinaryOperatorKind.Division, t, t, t));
                }

                ops.Add(new(BoundBinaryOperatorKind.Equals, t, t, TypeSymbol.Boolean));
                ops.Add(new(BoundBinaryOperatorKind.NotEquals, t, t, TypeSymbol.Boolean));
                ops.Add(new(BoundBinaryOperatorKind.Less, t, t, TypeSymbol.Boolean));
                ops.Add(new(BoundBinaryOperatorKind.LessOrEquals, t, t, TypeSymbol.Boolean));
                ops.Add(new(BoundBinaryOperatorKind.Greater, t, t, TypeSymbol.Boolean));
                ops.Add(new(BoundBinaryOperatorKind.GreaterOrEquals, t, t, TypeSymbol.Boolean));
            }

            // nint/nuint（原生整型，平台自适应）：相等比较
            ops.Add(new(BoundBinaryOperatorKind.Equals, TypeSymbol.NativeInt32, TypeSymbol.NativeInt32, TypeSymbol.Boolean));
            ops.Add(new(BoundBinaryOperatorKind.NotEquals, TypeSymbol.NativeInt32, TypeSymbol.NativeInt32, TypeSymbol.Boolean));
            ops.Add(new(BoundBinaryOperatorKind.Equals, TypeSymbol.NativeUInt32, TypeSymbol.NativeUInt32, TypeSymbol.Boolean));
            ops.Add(new(BoundBinaryOperatorKind.NotEquals, TypeSymbol.NativeUInt32, TypeSymbol.NativeUInt32, TypeSymbol.Boolean));

            // decimal（128 位高精度）：算术 + 比较
            ops.Add(new(BoundBinaryOperatorKind.Addition, TypeSymbol.Decimal, TypeSymbol.Decimal, TypeSymbol.Decimal));
            ops.Add(new(BoundBinaryOperatorKind.Subtraction, TypeSymbol.Decimal, TypeSymbol.Decimal, TypeSymbol.Decimal));
            ops.Add(new(BoundBinaryOperatorKind.Multiplication, TypeSymbol.Decimal, TypeSymbol.Decimal, TypeSymbol.Decimal));
            ops.Add(new(BoundBinaryOperatorKind.Division, TypeSymbol.Decimal, TypeSymbol.Decimal, TypeSymbol.Decimal));
            ops.Add(new(BoundBinaryOperatorKind.Modulo, TypeSymbol.Decimal, TypeSymbol.Decimal, TypeSymbol.Decimal));
            ops.Add(new(BoundBinaryOperatorKind.Equals, TypeSymbol.Decimal, TypeSymbol.Decimal, TypeSymbol.Boolean));
            ops.Add(new(BoundBinaryOperatorKind.NotEquals, TypeSymbol.Decimal, TypeSymbol.Decimal, TypeSymbol.Boolean));
            ops.Add(new(BoundBinaryOperatorKind.Less, TypeSymbol.Decimal, TypeSymbol.Decimal, TypeSymbol.Boolean));
            ops.Add(new(BoundBinaryOperatorKind.LessOrEquals, TypeSymbol.Decimal, TypeSymbol.Decimal, TypeSymbol.Boolean));
            ops.Add(new(BoundBinaryOperatorKind.Greater, TypeSymbol.Decimal, TypeSymbol.Decimal, TypeSymbol.Boolean));
            ops.Add(new(BoundBinaryOperatorKind.GreaterOrEquals, TypeSymbol.Decimal, TypeSymbol.Decimal, TypeSymbol.Boolean));

            // any：相等
            ops.Add(new(BoundBinaryOperatorKind.Equals, TypeSymbol.Any, TypeSymbol.Any, TypeSymbol.Boolean));
            ops.Add(new(BoundBinaryOperatorKind.NotEquals, TypeSymbol.Any, TypeSymbol.Any, TypeSymbol.Boolean));

            // null == null / null != null（恒 true/false）
            ops.Add(new(BoundBinaryOperatorKind.Equals, TypeSymbol.Null, TypeSymbol.Null, TypeSymbol.Boolean));
            ops.Add(new(BoundBinaryOperatorKind.NotEquals, TypeSymbol.Null, TypeSymbol.Null, TypeSymbol.Boolean));

            // ?? null 合并：引用类型 x ?? y
            foreach (var t in new[] { TypeSymbol.Any, TypeSymbol.String })
            {
                ops.Add(new(BoundBinaryOperatorKind.NullCoalescing, t, t, t));
            }

            return ops.ToArray();
        }

        /// <summary>
        /// 6e-M21 Phase 1：程序化生成一元运算符表——整数类型支持 +x / -x / ~x，浮点支持 +x / -x。
        /// </summary>
        private static UnaryOperatorSignature[] BuildUnaryOperators()
        {
            var ops = new List<UnaryOperatorSignature>
            {
                new(BoundUnaryOperatorKind.LogicalNegation, TypeSymbol.Boolean, TypeSymbol.Boolean),
            };

            var numericTypes = new[]
            {
                TypeSymbol.Int8, TypeSymbol.Int16, TypeSymbol.Int32, TypeSymbol.Int64,
                TypeSymbol.UInt8, TypeSymbol.UInt16, TypeSymbol.UInt32, TypeSymbol.UInt64,
                TypeSymbol.Float, TypeSymbol.Double,
            };

            foreach (var t in numericTypes)
            {
                // 6e-M21 Phase 7：<32 位整数一元 +/-/~ 结果升 Int32（C# 同构）
                var result = t.IsInteger && t.BitWidth < 32 ? TypeSymbol.Int32 : t;
                ops.Add(new(BoundUnaryOperatorKind.Identity, t, result));
                ops.Add(new(BoundUnaryOperatorKind.Negation, t, result));

                if (t.IsInteger)
                {
                    ops.Add(new(BoundUnaryOperatorKind.OnesComplement, t, result));
                }
            }

            // decimal：正号/负号
            ops.Add(new(BoundUnaryOperatorKind.Identity, TypeSymbol.Decimal, TypeSymbol.Decimal));
            ops.Add(new(BoundUnaryOperatorKind.Negation, TypeSymbol.Decimal, TypeSymbol.Decimal));

            return ops.ToArray();
        }

        /// <summary>
        /// 内建二元签名 → 语义符号（对齐 Roslyn <c>CommonCreateBuiltinOperator</c>）：按 CLR 约定合成
        /// `op_*` 方法符号（<see cref="OperatorNames.ToMetadataName"/>）。语义视图用——供反射/诊断/
        /// 重载解析候选比较，不携带方法体，发射仍按 kind 直分派。构建结果缓存（同签名共享实例）。
        /// </summary>
        private static readonly Dictionary<(BoundBinaryOperatorKind, TypeSymbol, TypeSymbol), FunctionSymbol> _binarySymbolCache = new();

        public static FunctionSymbol? CreateOperatorSymbol(BinaryOperatorSignature signature)
        {
            var key = (signature.Kind, signature.LeftType, signature.RightType);
            if (_binarySymbolCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var kind = ToOperatorKind(signature.Kind);
            if (kind == null)
            {
                return null; // ReferenceEquals/NullCoalescing 等无用户对应运算符 → 无符号视图
            }

            var parameters = ImmutableArray.Create(
                new ParameterSymbol("left", signature.LeftType, 0),
                new ParameterSymbol("right", signature.RightType, 1));
            var symbol = new FunctionSymbol(
                OperatorNames.ToMetadataName(kind.Value),
                parameters,
                signature.ResultType,
                builtinKind: signature.EmitKind);

            _binarySymbolCache[key] = symbol;
            return symbol;
        }

        /// <summary>内建一元签名 → 语义符号（`op_UnaryPlus` 等）。</summary>
        private static readonly Dictionary<(BoundUnaryOperatorKind, TypeSymbol), FunctionSymbol> _unarySymbolCache = new();

        public static FunctionSymbol? CreateOperatorSymbol(UnaryOperatorSignature signature)
        {
            var key = (signature.Kind, signature.OperandType);
            if (_unarySymbolCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var kind = ToOperatorKind(signature.Kind);
            if (kind == null)
            {
                return null;
            }

            var parameters = ImmutableArray.Create(new ParameterSymbol("operand", signature.OperandType, 0));
            var symbol = new FunctionSymbol(
                OperatorNames.ToMetadataName(kind.Value),
                parameters,
                signature.ResultType,
                builtinKind: signature.EmitKind);

            _unarySymbolCache[key] = symbol;
            return symbol;
        }

        /// <summary>内建二元 kind → 用户可声明运算符种类（合成符号按 CLR 约定命名；无对应用户运算符的返回 null）。</summary>
        public static OperatorKind? ToOperatorKind(BoundBinaryOperatorKind kind)
        {
            return kind switch
            {
                BoundBinaryOperatorKind.Addition => OperatorKind.Addition,
                BoundBinaryOperatorKind.Subtraction => OperatorKind.Subtraction,
                BoundBinaryOperatorKind.Multiplication => OperatorKind.Multiplication,
                BoundBinaryOperatorKind.Division => OperatorKind.Division,
                BoundBinaryOperatorKind.Modulo => OperatorKind.Modulo,
                BoundBinaryOperatorKind.ShiftLeft => OperatorKind.LeftShift,
                BoundBinaryOperatorKind.ShiftRight => OperatorKind.RightShift,
                BoundBinaryOperatorKind.BitwiseAnd => OperatorKind.BitwiseAnd,
                BoundBinaryOperatorKind.BitwiseOr => OperatorKind.BitwiseOr,
                BoundBinaryOperatorKind.BitwiseXor => OperatorKind.BitwiseXor,
                BoundBinaryOperatorKind.Equals => OperatorKind.Equality,
                BoundBinaryOperatorKind.NotEquals => OperatorKind.Inequality,
                BoundBinaryOperatorKind.Less => OperatorKind.LessThan,
                BoundBinaryOperatorKind.LessOrEquals => OperatorKind.LessThanOrEqual,
                BoundBinaryOperatorKind.Greater => OperatorKind.GreaterThan,
                BoundBinaryOperatorKind.GreaterOrEquals => OperatorKind.GreaterThanOrEqual,
                _ => null,
            };
        }

        /// <summary>内建一元 kind → 用户可声明运算符种类。</summary>
        public static OperatorKind? ToOperatorKind(BoundUnaryOperatorKind kind)
        {
            return kind switch
            {
                BoundUnaryOperatorKind.Identity => OperatorKind.UnaryPlus,
                BoundUnaryOperatorKind.Negation => OperatorKind.UnaryNegation,
                BoundUnaryOperatorKind.LogicalNegation => OperatorKind.LogicalNot,
                BoundUnaryOperatorKind.OnesComplement => OperatorKind.BitwiseComplement,
                _ => null,
            };
        }
    }
}