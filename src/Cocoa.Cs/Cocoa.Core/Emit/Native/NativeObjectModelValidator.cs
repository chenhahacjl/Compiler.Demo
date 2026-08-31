using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Text;

namespace Cocoa.CodeAnalysis.Emit.Native
{
    /// <summary>
    /// 6e-M19 M4：native 对象模型放行校验。M4 后用户类（字段/方法/继承/多态）全面支持，
    /// 仍不支持的形状给出明确诊断（不静默错编）：
    ///   - Object/Type 成员面调用的 receiver 为 any / 数组 / 枚举（无 vtable 表示，装箱未实现）
    ///   - 接口声明与接口分派（M5/后续里程碑）
    ///   - 静态构造函数/静态字段初始化器（native 无 .cctor 触发时机）
    /// </summary>
    internal sealed class NativeObjectModelValidator : BoundTreeRewriter
    {
        private NativeObjectModelValidator(DiagnosticBag diagnostics, TextLocation fallbackLocation)
        {
            _diagnostics = diagnostics;
            _fallbackLocation = fallbackLocation;
        }

        private readonly DiagnosticBag _diagnostics;
        private readonly TextLocation _fallbackLocation;

        /// <summary>扫描程序全部函数体，报告 native 不支持的对象模型形状。</summary>
        public static void Validate(BoundProgram program, DiagnosticBag diagnostics, TextLocation fallbackLocation)
        {
            var validator = new NativeObjectModelValidator(diagnostics, fallbackLocation);

            foreach (var function in program.Functions.Keys)
            {
                if (!program.Functions.TryGetValue(function, out var body))
                {
                    continue;
                }

                validator.RewriteStatement(body);
            }
        }

        public override BoundExpression RewriteExpression(BoundExpression node)
        {
            if (node.Kind == BoundNodeKind.MemberCallExpression &&
                ((BoundMemberCallExpression)node).Method?.BuiltinKind != null &&
                !((BoundMemberCallExpression)node).Method!.IsStatic)
            {
                var receiverType = ((BoundMemberCallExpression)node).Expression.Type;
                if (!IsSupportedReceiver(receiverType))
                {
                    var location = node.Syntax?.Location ?? _fallbackLocation;
                    _diagnostics.ReportError(location, $"类型 '{Describe(receiverType)}' 的 Object 成员调用（ToString/GetHashCode/Equals/GetType）：native 后端不支持该接收者形状（any/数组/枚举需装箱表示）。");
                }
            }

            return base.RewriteExpression(node);
        }

        private static bool IsSupportedReceiver(TypeSymbol type)
        {
            if (type == TypeSymbol.String || type == NamedTypeSymbol.SystemType)
            {
                return true;
            }

            if (type is NamedTypeSymbol { TypeKind: TypeKind.Enum } || type.ElementType != null)
            {
                return false;
            }

            if (type is NamedTypeSymbol classType)
            {
                return true; // 用户类/facade 类均受支持（facade 走基元路径）
            }

            return type != TypeSymbol.Any && type != TypeSymbol.Void && type != TypeSymbol.Error;
        }

        private static string Describe(TypeSymbol type)
        {
            if (type.ElementType != null)
            {
                return type.ElementType.Name + "[]";
            }

            return type.Name;
        }
    }
}
