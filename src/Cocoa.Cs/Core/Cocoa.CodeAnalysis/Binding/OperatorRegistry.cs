using System.Collections.Generic;
using System.Linq;
using Cocoa.CodeAnalysis.Symbols;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 运算符重载查找表：按 (运算符种类, 操作数类型键) 索引已登记的 <see cref="FunctionSymbol"/>。
    ///
    /// 填充时机：类方法**签名**绑定阶段（<c>BindClassMethodDeclarationCore</c>）。
    /// 查询时机：表达式体绑定阶段——此时全部声明已落 <c>globalScope</c>（见 <c>CocoaBinder.cs</c> 的
    /// <c>foreach (var function in globalScope.Functions)</c> 体绑定遍），故「运算符声明在用法之后」也能解析，
    /// 无需两遍扫描。
    ///
    /// 类型键用 <see cref="TypeSymbol"/> 的规范名（<c>TypeKey</c>）而非引用相等，避免同型跨符号实例漏配。
    /// </summary>
    public sealed class OperatorRegistry
    {
        private readonly Dictionary<(OperatorKind Kind, string Left, string Right), FunctionSymbol> _bySignature = new();
        private readonly List<FunctionSymbol> _all = new();

        /// <summary>登记一个运算符方法。<paramref name="operandTypes"/> 为声明的操作数类型（转换运算符为单参源类型）。
        /// 返回 false 表示同签名已登记（调用方报「重复定义」诊断）。</summary>
        public bool Register(FunctionSymbol method, IReadOnlyList<TypeSymbol?> operandTypes)
        {
            var kind = method.OperatorKind!.Value;
            var arity = OperatorNames.Arity(kind);

            if (operandTypes.Count != arity)
            {
                return false;
            }

            // 一元：无第二操作数；转换：无第二操作数，目标类型记在第二键；二元：双侧类型
            var leftKey = TypeKey(operandTypes[0]);
            var rightKey = kind == OperatorKind.ImplicitConversion || kind == OperatorKind.ExplicitConversion
                ? TypeKey(method.ReturnType)
                : OperatorNames.IsUnary(kind)
                    ? string.Empty
                    : TypeKey(operandTypes[1]);

            if (!_bySignature.TryAdd((kind, leftKey, rightKey), method))
            {
                return false;
            }

            _all.Add(method);
            return true;
        }

        /// <summary>已登记的全部运算符方法（供诊断/完整性检查遍历）。</summary>
        public IReadOnlyList<FunctionSymbol> All => _all;

        /// <summary>
        /// 解析二元运算符：依次尝试 (L,R) 与 (R,L)（交换重载）两个候选签名。
        /// 交换候选是 C# 惯例（`a + b` 可命中 `operator +(B, A)`），相等/关系不含交换以免语义歧义。
        /// **不做「一侧自配」回退**（`Vec + Other` 不得命中 `op_Addition(Vec, Vec)`）：
        /// 那需要先证明另一侧可隐式转换到本类型，属转换计分范畴，与 D1（重载解析无转换计分）一并后置。
        /// </summary>
        public FunctionSymbol? ResolveBinary(OperatorKind kind, TypeSymbol left, TypeSymbol right)
        {
            var l = TypeKey(left);
            var r = TypeKey(right);

            if (_bySignature.TryGetValue((kind, l, r), out var exact))
            {
                return exact;
            }

            if (kind == OperatorKind.Addition || kind == OperatorKind.Multiplication ||
                kind == OperatorKind.BitwiseAnd || kind == OperatorKind.BitwiseOr ||
                kind == OperatorKind.BitwiseXor || kind == OperatorKind.LeftShift ||
                kind == OperatorKind.RightShift)
            {
                if (_bySignature.TryGetValue((kind, r, l), out var swapped) && r != l)
                {
                    return swapped;
                }
            }

            return null;
        }

        /// <summary>解析一元运算符：候选 (T,T) / (T,)（转换运算符单参）。</summary>
        public FunctionSymbol? ResolveUnary(OperatorKind kind, TypeSymbol operand)
        {
            var t = TypeKey(operand);

            if (_bySignature.TryGetValue((kind, t, string.Empty), out var exact))
            {
                return exact;
            }

            if (_bySignature.TryGetValue((kind, t, t), out var twoArg))
            {
                return twoArg;
            }

            return null;
        }

        /// <summary>解析转换运算符：源类型 → 目标类型。</summary>
        public FunctionSymbol? ResolveConversion(OperatorKind kind, TypeSymbol source, TypeSymbol target)
        {
            return _bySignature.TryGetValue((kind, TypeKey(source), TypeKey(target)), out var method) ? method : null;
        }

        /// <summary>
        /// 类型规范键：具名/数组/函数类型取 FullName 或可判等的结构化串；基元与内置取 Name。
        /// 目的是让「同一类型的不同符号实例」得到同一键。
        /// </summary>
        public static string TypeKey(TypeSymbol? type)
        {
            switch (type)
            {
                case null:
                    return "?";

                case TypeSymbol t when ReferenceEquals(t, TypeSymbol.Error):
                    return "?";

                case ArrayTypeSymbol array:
                    return TypeKey(array.ElementType) + "[]";

                case FunctionTypeSymbol fn:
                    return "fn(" + string.Join(",", fn.ParameterTypes.Select(TypeKey)) + ")->" + TypeKey(fn.ReturnType);

                case NamedTypeSymbol named:
                    return named.FullName;

                default:
                    return type.Name;
            }
        }
    }
}
