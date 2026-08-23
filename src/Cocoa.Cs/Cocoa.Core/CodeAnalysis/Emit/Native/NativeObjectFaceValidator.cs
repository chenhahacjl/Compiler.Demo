using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Text;

namespace Cocoa.CodeAnalysis.Emit.Native
{
    /// <summary>
    /// 6e-M19 M2-c：native 后端 Object 成员面调用守卫。native 对象模型（vtable 虚分派）随 M4 落地；
    /// 此前 Object 内建方法（ToString/GetHashCode/Equals/GetType + 静态 Equals/ReferenceEquals）在
    /// native 编译期给出明确"未实现"诊断（循 charset=ansi 先例），不静默错编。
    /// 复用 BoundTreeRewriter 全量遍历，保证嵌套表达式（if/while 体内等）全覆盖。
    /// </summary>
    internal sealed class NativeObjectFaceValidator : BoundTreeRewriter
    {
        private NativeObjectFaceValidator(DiagnosticBag diagnostics, TextLocation fallbackLocation)
        {
            _diagnostics = diagnostics;
            _fallbackLocation = fallbackLocation;
        }

        private readonly DiagnosticBag _diagnostics;
        private readonly TextLocation _fallbackLocation;
        private bool _found;

        /// <summary>
        /// 扫描程序全部函数体；命中 Object 成员面调用的函数逐一报错
        /// （`requires:dotnet` 语义之外的显式占位，M4 落地后移除本守卫）。
        /// </summary>
        public static void Validate(BoundProgram program, DiagnosticBag diagnostics, TextLocation fallbackLocation)
        {
            var validator = new NativeObjectFaceValidator(diagnostics, fallbackLocation);

            foreach (var function in program.Functions.Keys)
            {
                if (!program.Functions.TryGetValue(function, out var body))
                {
                    continue;
                }

                validator._found = false;
                validator.RewriteStatement(body);

                if (validator._found)
                {
                    var location = function.Syntax?.Location ?? fallbackLocation;
                    var name = function.ContainingClass != null ? function.ContainingClass.Name + "." + function.Name : function.Name;
                    diagnostics.ReportError(location, $"函数 '{name}' 使用了 System.Object 成员方法/静态相等（ToString/GetHashCode/Equals/GetType/ReferenceEquals）：native 对象模型未实现，暂不支持（见 docs-dev/对象模型设计.md M4）。");
                }
            }
        }

        public override BoundExpression RewriteExpression(BoundExpression node)
        {
            switch (node.Kind)
            {
                case BoundNodeKind.CallExpression:
                    if (IsObjectFace(((BoundCallExpression)node).Function.BuiltinKind))
                    {
                        _found = true;
                    }

                    break;
                case BoundNodeKind.MemberCallExpression:
                    if (IsObjectFace(((BoundMemberCallExpression)node).Method?.BuiltinKind))
                    {
                        _found = true;
                    }

                    break;
            }

            return base.RewriteExpression(node);
        }

        private static bool IsObjectFace(BuiltinKind? builtinKind) => builtinKind switch
        {
            BuiltinKind.ObjectToString => true,
            BuiltinKind.ObjectGetHashCode => true,
            BuiltinKind.ObjectEquals => true,
            BuiltinKind.ObjectGetType => true,
            BuiltinKind.ObjectStaticEquals => true,
            BuiltinKind.ObjectReferenceEquals => true,
            BuiltinKind.TypeName => true,
            BuiltinKind.TypeFullName => true,
            _ => false,
        };
    }
}
