using System.Collections.Immutable;
using Cocoa.CodeAnalysis.Symbols;
using CoreSyntax = Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 6e-M32 Tier-2：attribute 绑定引擎。
    /// 省略解析（`[X]` → X / XAttribute）、属性类判定（基链含 System.Attribute）、
    /// 字面量实参 → 属性类 .ctor 校验、未知名/歧义/参数不匹配诊断。
    /// </summary>
    public partial class CocoaBinder
    {
        private ImmutableArray<AttributeSymbol> BindAttributes(ImmutableArray<CoreSyntax.AttributeSyntax> attributes, Syntax.SyntaxNode target)
        {
            if (attributes.IsDefaultOrEmpty)
            {
                return ImmutableArray<AttributeSymbol>.Empty;
            }

            var result = ImmutableArray.CreateBuilder<AttributeSymbol>();
            foreach (var attribute in attributes)
            {
                var bound = BindAttribute(attribute, target);
                if (bound != null)
                {
                    result.Add(bound);
                }
            }

            return result.ToImmutable();
        }

        private AttributeSymbol? BindAttribute(CoreSyntax.AttributeSyntax attribute, Syntax.SyntaxNode target)
        {
            var name = attribute.Name.Text;
            var attributeType = ResolveAttributeClass(name, attribute.Name.Location);
            if (attributeType == null)
            {
                return null;
            }

            // 实参字面量 → (类型, 值)
            var argTypes = ImmutableArray.CreateBuilder<TypeSymbol>();
            var argValues = ImmutableArray.CreateBuilder<object>();
            var items = new System.Collections.Generic.List<object>();
            foreach (var argument in attribute.Arguments)
            {
                if (argument.Kind == CoreSyntax.SyntaxKind.CommaToken)
                {
                    continue;
                }

                var typed = BindAttributeLiteral(argument);
                if (typed == null)
                {
                    _diagnostics.ReportError(argument.Location, $"attribute '{name}' 实参必须是字面量（string/i32/f64/bool/char）。");
                }
                else
                {
                    argTypes.Add(typed.Value.Type);
                    argValues.Add(typed.Value.Value);
                }
            }

            // 属性类 .ctor 校验：构造器按类名命名（IsConstructor 标志），无实参 → 隐式无参构造语义；
            // 有实参 → 须命中参数类型匹配的构造器，否则诊断。
            if (argTypes.Count == 0)
            {
                return new AttributeSymbol(attributeType, ImmutableArray<(TypeSymbol, object)>.Empty);
            }

            var ctors = attributeType.Methods.Where(m => m.IsConstructor && !m.IsStatic && ParametersMatch(m, argTypes.ToImmutable())).ToArray();
            if (ctors.Length > 0)
            {
                return new AttributeSymbol(attributeType, Zip(argTypes.ToImmutable(), argValues.ToImmutable()));
            }

            _diagnostics.ReportError(attribute.Name.Location, $"attribute '{attributeType.FullName}' 的构造器参数类型与实参不匹配。");
            return null;
        }

        private bool ParametersMatch(FunctionSymbol ctor, ImmutableArray<TypeSymbol> argTypes)
        {
            if (ctor.Parameters.Length != argTypes.Length)
            {
                return false;
            }

            for (var i = 0; i < argTypes.Length; i++)
            {
                if (!SymbolsEqual(ctor.Parameters[i].Type, argTypes[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool SymbolsEqual(TypeSymbol a, TypeSymbol b)
        {
            if (a == b)
            {
                return true;
            }

            return a is NamedTypeSymbol na && b is NamedTypeSymbol nb && na.FullName == nb.FullName;
        }

        private static ImmutableArray<(TypeSymbol Type, object Value)> Zip(ImmutableArray<TypeSymbol> types, ImmutableArray<object> values)
        {
            var b = ImmutableArray.CreateBuilder<(TypeSymbol, object)>();
            for (var i = 0; i < types.Length; i++)
            {
                b.Add((types[i], values[i]));
            }

            return b.ToImmutable();
        }

        /// <summary>字面量 token → 类型化值（string/i32/f64/bool/char）。</summary>
        private (TypeSymbol Type, object Value)? BindAttributeLiteral(CoreSyntax.SyntaxToken token)
        {
            switch (token.Kind)
            {
                case CoreSyntax.SyntaxKind.StringToken:
                case CoreSyntax.SyntaxKind.VerbatimStringToken:
                    var text = token.Text.Length >= 2 ? token.Text.Substring(1, token.Text.Length - 2) : "";
                    return (TypeSymbol.String, text);
                case CoreSyntax.SyntaxKind.NumberToken:
                    if (int.TryParse(token.Text, out var i32))
                    {
                        return (TypeSymbol.Int32, i32);
                    }

                    if (double.TryParse(token.Text, out var f64))
                    {
                        return (TypeSymbol.Double, f64);
                    }

                    return null;
                case CoreSyntax.SyntaxKind.TrueKeyword:
                    return (TypeSymbol.Boolean, true);
                case CoreSyntax.SyntaxKind.FalseKeyword:
                    return (TypeSymbol.Boolean, false);
                case CoreSyntax.SyntaxKind.CharToken:
                    var ctext = token.Text.Length >= 3 ? token.Text[1] : '\0';
                    return (TypeSymbol.Char, ctext);
                default:
                    return null;
            }
        }

        /// <summary>省略解析：`[X]` → 先 X 再 XAttribute；两者皆属性类 → 歧义诊断。</summary>
        private NamedTypeSymbol? ResolveAttributeClass(string name, Cocoa.CodeAnalysis.Text.TextLocation location)
        {
            if (name == "Facade")
            {
                // 编译器识别的 [Facade(...)]：TryGetFacadeAttribute 按名处理（类级 BCL 重定向），无需属性类
                return null;
            }

            var direct = LookupType(name) as NamedTypeSymbol;
            var suffixed = LookupType(name + "Attribute") as NamedTypeSymbol;

            var directIsAttr = direct != null && IsAttributeClass(direct);
            var suffixedIsAttr = suffixed != null && IsAttributeClass(suffixed);

            if (directIsAttr && suffixedIsAttr)
            {
                _diagnostics.ReportError(location, $"attribute 名 '{name}' 在 '{name}' 与 '{name}Attribute' 间存在歧义。");
                return null;
            }

            if (directIsAttr)
            {
                return direct;
            }

            if (suffixedIsAttr)
            {
                return suffixed;
            }

            _diagnostics.ReportError(location, $"attribute '{name}' 不存在或未继承 System.Attribute。");
            return null;
        }

        /// <summary>基链上溯含 System.Attribute 即属性类。</summary>
        private bool IsAttributeClass(NamedTypeSymbol type)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                if (t.FullName == "System.Attribute")
                {
                    return true;
                }
            }

            return false;
        }
    }
}