using Cocoa.CodeAnalysis.Lowering;
using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Serialization;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using CoreSyntax = Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Text;
using Cocoa.CodeAnalysis.Bound;
using Cocoa.CodeAnalysis.Documentation;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

using Cocoa.Metadata;
namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// Partial member surface of the binder.
    /// </summary>
    public partial class CocoaBinder
    {
        private void BindFunctionDeclaration(FunctionDeclarationSyntax syntax, string? namespaceName = null, string? importedDll = null)
        {
            // 泛型方法类型参数（6e-M20）先行落符号：签名 `(a: T, b: T): T` 的 T 解析依赖此上下文
            var previousMethodTypeParameters = _declaringMethodTypeParameters;
            _declaringMethodTypeParameters = BindFunctionTypeParameters(syntax.TypeParameters);

            try
            {
                var parameters = ImmutableArray.CreateBuilder<ParameterSymbol>();

                var seenParameterNames = new HashSet<string>();

                foreach (var parameterSyntax in syntax.Parameters)
                {
                    var parameterName = parameterSyntax.Identifier.Text;
                    var parameterType = BindTypeClause(parameterSyntax.Type);

                    if (!seenParameterNames.Add(parameterName))
                    {
                        _diagnostics.ReportParameterAlreadyDeclared(parameterSyntax.Location, parameterName);
                    }
                    else
                    {
                        var parameter = CreateParameterSymbol(parameterName, parameterType!, parameterSyntax, parameters.Count);
                        parameters.Add(parameter);
                    }
                }

                var type = BindTypeClause(syntax.Type) ?? TypeSymbol.Void;

                var isExtern = syntax.Modifiers.Any(m => m.Kind == CoreSyntax.SyntaxKind.CdeclKeyword || m.Kind == CoreSyntax.SyntaxKind.StdcallKeyword);
                var isSyscall = syntax.Modifiers.Any(m => m.Kind == CoreSyntax.SyntaxKind.SyscallKeyword);

                if (isSyscall)
                {
                    _diagnostics.ReportSyscallFunctionTopLevel(syntax.Identifier.Location);
                }

                if (isExtern)
                {
                    // 6e-M17 Step 4：顶层位置式 extern 废弃 —— extern 必须声明在类的 import 块内
                    _diagnostics.ReportExternFunctionTopLevel(syntax.Identifier.Location);

                    if (syntax.Body != null)
                    {
                        _diagnostics.ReportExternFunctionCannotHaveBody(syntax.Body.Location);
                    }
                }

                var callingConvention = syntax.Modifiers.Select(m => m.Kind)
                    .FirstOrDefault(k => k == CoreSyntax.SyntaxKind.CdeclKeyword || k == CoreSyntax.SyntaxKind.StdcallKeyword) switch
                {
                    CoreSyntax.SyntaxKind.CdeclKeyword => CallingConvention.Cdecl,
                    CoreSyntax.SyntaxKind.StdcallKeyword => CallingConvention.StdCall,
                    _ => CallingConvention.Winapi,
                };

                var function = new FunctionSymbol(syntax.Identifier.Text, parameters.ToImmutable(), type, syntax, isExtern, importedDll, callingConvention, @namespace: namespaceName ?? "")
                {
                    TypeParameters = _declaringMethodTypeParameters,
                };
                function.Attributes = BindAttributes(syntax.Attributes, syntax);
                DocumentationBackfill.BackfillDocumentation(function, syntax, _diagnostics);
                BindWhereClauses(syntax.WhereClauses, function.TypeParameters);

                if (syntax.Identifier.Text != null && !_scope.TryDeclareFunction(function))
                {
                    _diagnostics.ReportSymbolAlreadyDeclared(syntax.Identifier.Location, function.Name);
                }

                // 阶段 4：扩展方法注册——首参带 this 修饰的静态方法
                if (syntax.Parameters.Count > 0 && syntax.Parameters[0].IsThis && function.IsStatic)
                {
                    _extensionMethods.Add(function);
                }

                // 命名空间函数同时注册进命名空间表（`Foo.Add(...)` 限定访问）；同名同签名由 TryDeclareFunction 已拦
                if (function.Namespace.Length > 0)
                {
                    _scope.TryDeclareNamespaceFunction(function.Namespace, function);
                }
            }
            finally
            {
                _declaringMethodTypeParameters = previousMethodTypeParameters;
            }
        }

        private ImmutableArray<ParameterSymbol> BindParameters(CoreSyntax.SeparatedSyntaxList<ParameterSyntax> parameterSyntaxList)
        {
            var parameters = ImmutableArray.CreateBuilder<ParameterSymbol>();

            var seenParameterNames = new HashSet<string>();

            foreach (var parameterSyntax in parameterSyntaxList)
            {
                var parameterName = parameterSyntax.Identifier.Text;
                var parameterType = BindTypeClause(parameterSyntax.Type);

                if (!seenParameterNames.Add(parameterName))
                {
                    _diagnostics.ReportParameterAlreadyDeclared(parameterSyntax.Location, parameterName);
                }
                else
                {
                    var parameter = CreateParameterSymbol(parameterName, parameterType!, parameterSyntax, parameters.Count);
                    parameters.Add(parameter);
                }
            }

            return parameters.ToImmutable();
        }

        /// <summary>形参符号构造（6e-M23 R2）：携带 out/ref 修饰符；普通形参可赋值（对齐 C#），this 保持只读。
        /// 可选参数（语言后置件）：形参默认值 `x: i32 = 10` 求值为常量存入 ParameterSymbol。</summary>
        private ParameterSymbol CreateParameterSymbol(string name, TypeSymbol type, ParameterSyntax syntax, int ordinal)
        {
            var isOut = syntax.Modifier?.Kind == CoreSyntax.SyntaxKind.OutKeyword;
            var isRef = syntax.Modifier?.Kind == CoreSyntax.SyntaxKind.RefKeyword;
            var isParams = syntax.Modifier?.Kind == CoreSyntax.SyntaxKind.ParamsKeyword;

            object? defaultValue = null;
            var hasDefault = false;
            if (syntax.HasDefaultValue)
            {
                var boundDefault = BindExpression(syntax.DefaultValue!);
                if (boundDefault.ConstantValue is { } cv)
                {
                    hasDefault = true;
                    defaultValue = cv.Value;
                }
            }

            return new ParameterSymbol(name, type, ordinal, isOut, isRef, defaultValue: defaultValue, hasDefault: hasDefault, isParams: isParams);
        }

        /// <summary>可选参数（语言后置件）：必需形参个数 = 末尾可选形参之前的数量；调用实参数不得少于该值。</summary>
        private static int RequiredParameterCount(ImmutableArray<ParameterSymbol> parameters)
        {
            var count = parameters.Length;
            while (count > 0 && parameters[count - 1].HasDefaultValue)
            {
                count--;
            }

            return count;
        }

        /// <summary>params + 可选：调用实参下限 = 末尾可选/params 之前的必需形参数。</summary>
        private static int MinArgumentCount(ImmutableArray<ParameterSymbol> parameters)
        {
            var count = parameters.Length;
            for (var i = count - 1; i >= 0; i--)
            {
                if (parameters[i].IsParams || parameters[i].HasDefaultValue)
                {
                    count--;
                    continue;
                }

                break;
            }

            return count;
        }

        private static bool HasParamsTail(ImmutableArray<ParameterSymbol> parameters) => ParamArrayIndex(parameters) >= 0;

        private static int ParamArrayIndex(ImmutableArray<ParameterSymbol> parameters)
        {
            for (var i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].IsParams)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>params 可变参数（语言后置件）：实参数需 ≥ 必需（MinArgumentCount），无 params 时不得超过形参数。</summary>
        private static bool ArgumentCountFits(ImmutableArray<ParameterSymbol> parameters, int count)
        {
            if (count < MinArgumentCount(parameters))
            {
                return false;
            }

            if (!HasParamsTail(parameters) && count > parameters.Length)
            {
                return false;
            }

            return true;
        }

        /// <summary>泛型方法类型参数绑定（6e-M20）：建 TypeParameterSymbol 列表（重名/与类类型参数同名诊断）。</summary>
        private ImmutableArray<TypeParameterSymbol> BindFunctionTypeParameters(TypeParameterListSyntax? syntax)
        {
            if (syntax == null)
            {
                return ImmutableArray<TypeParameterSymbol>.Empty;
            }

            var parameters = ImmutableArray.CreateBuilder<TypeParameterSymbol>();
            var seen = new HashSet<string>();

            // 类类型参数先入集：方法级同名遮蔽报错（对齐 C# CS0693 提示语义）
            foreach (var outer in _bindingClass?.TypeParameters ?? _currentClass?.TypeParameters ?? ImmutableArray<TypeParameterSymbol>.Empty)
            {
                seen.Add(outer.Name);
            }

            foreach (var parameterSyntax in syntax.Parameters)
            {
                var parameterName = parameterSyntax.Identifier.Text ?? "";
                if (parameterName.Length == 0)
                {
                    continue;
                }

                if (parameterSyntax.VarianceKeyword != null)
                {
                    _diagnostics.ReportError(parameterSyntax.VarianceKeyword.Location, $"型变注解 '{VarianceKeywordText(parameterSyntax)}' 仅适用于 delegate/接口类型参数（方法类型参数保持不变）。");
                    continue;
                }

                if (!seen.Add(parameterName))
                {
                    _diagnostics.ReportError(parameterSyntax.Identifier.Location, $"类型参数 '{parameterName}' 重复或与外层类型参数同名。");
                    continue;
                }

                parameters.Add(new TypeParameterSymbol(parameterName, parameters.Count, owningClass: null));
            }

            return parameters.ToImmutable();
        }

        /// <summary>从修饰符列表解析可见性（public &gt; internal &gt; protected &gt; private；无修饰符取默认值）。</summary>
        private static Visibility GetVisibility(ImmutableArray<CoreSyntax.SyntaxToken> modifiers, Visibility defaultVisibility)
        {
            if (modifiers.Any(m => m.Kind == CoreSyntax.SyntaxKind.PublicKeyword))
            {
                return Visibility.Public;
            }

            if (modifiers.Any(m => m.Kind == CoreSyntax.SyntaxKind.InternalKeyword))
            {
                return Visibility.Internal;
            }

            if (modifiers.Any(m => m.Kind == CoreSyntax.SyntaxKind.ProtectedKeyword))
            {
                return Visibility.Protected;
            }

            if (modifiers.Any(m => m.Kind == CoreSyntax.SyntaxKind.PrivateKeyword))
            {
                return Visibility.Private;
            }

            return defaultVisibility;
        }

        private static bool IsVisibilityModifier(CoreSyntax.SyntaxKind kind)
        {
            return kind == CoreSyntax.SyntaxKind.PublicKeyword ||
                   kind == CoreSyntax.SyntaxKind.InternalKeyword ||
                   kind == CoreSyntax.SyntaxKind.ProtectedKeyword ||
                   kind == CoreSyntax.SyntaxKind.PrivateKeyword;
        }

        private static bool HasVisibilityModifier(ImmutableArray<CoreSyntax.SyntaxToken> modifiers)
        {
            return modifiers.Any(m => IsVisibilityModifier(m.Kind));
        }

        /// <summary>
        /// 访问器可见性校验（严格对齐 C#）：① 访问器带可见性修饰符时必须严格更受限（CS0273，相等也报错）；
        /// ② get/set 至多一个可带可见性修饰符。可见性序：Public(0) &lt; Internal(1) &lt; Protected(2) &lt; Private(3)，数值越大越受限。
        /// </summary>
        private void ValidateAccessorVisibility(PropertyDeclarationSyntax syntax, Visibility propertyVisibility)
        {
            var hasGetModifier = syntax.Getter != null && HasVisibilityModifier(syntax.Getter.Modifiers);
            var hasSetModifier = syntax.Setter != null && HasVisibilityModifier(syntax.Setter.Modifiers);

            if (hasGetModifier && hasSetModifier)
            {
                var location = (syntax.Setter?.Keyword ?? syntax.Getter?.Keyword)!.Location;
                _diagnostics.ReportAccessorModifierOnBothAccessors(location, syntax.Identifier.Text);
            }

            if (hasGetModifier && syntax.Getter != null &&
                GetVisibility(syntax.Getter.Modifiers, propertyVisibility) <= propertyVisibility)
            {
                _diagnostics.ReportAccessorVisibilityNotMoreRestrictive(syntax.Getter.Keyword.Location, syntax.Identifier.Text);
            }

            if (hasSetModifier && syntax.Setter != null &&
                GetVisibility(syntax.Setter.Modifiers, propertyVisibility) <= propertyVisibility)
            {
                _diagnostics.ReportAccessorVisibilityNotMoreRestrictive(syntax.Setter.Keyword.Location, syntax.Identifier.Text);
            }
        }

        /// <summary>成员可见性判定（private 仅含类；protected 含类及派生类；internal 同程序集恒可访问）。</summary>
        private bool IsAccessibleMember(Visibility visibility, NamedTypeSymbol containingClass)
        {
            switch (visibility)
            {
                case Visibility.Public:
                case Visibility.Internal:
                    return true;
                case Visibility.Protected:
                    return _currentClass != null && (containingClass == _currentClass || containingClass.IsBaseOf(_currentClass));
                case Visibility.Private:
                default:
                    return _currentClass != null && containingClass == _currentClass;
            }
        }
    }
}
