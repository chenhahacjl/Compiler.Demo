using Cocoa.CodeAnalysis.Emit.IL;
using Cocoa.CodeAnalysis.Lowering;
using Cocoa.CodeAnalysis.Cod;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// Partial member surface of the binder.
    /// </summary>
    internal sealed partial class Binder
    {
        private TypeSymbol? LookupType(string name)
        {
            var builtin = LookupBuiltinType(name);
            if (builtin != null)
            {
                return builtin;
            }

            // 单态化重绑：类型参数名 → 具体实参（6e-M20 Monomorphizer 注入）
            if (_typeArgumentsByName.TryGetValue(name, out var typeArgument))
            {
                return typeArgument;
            }

            // 泛型类型参数（6e-M20）：当前方法/声明上下文中的 T/U 优先（定义期"不透明类型"）
            if (_currentClass != null)
            {
                foreach (var typeParameter in _currentClass.TypeParameters)
                {
                    if (typeParameter.Name == name)
                    {
                        return typeParameter;
                    }
                }
            }

            if (_bindingClass != null)
            {
                foreach (var typeParameter in _bindingClass.TypeParameters)
                {
                    if (typeParameter.Name == name)
                    {
                        return typeParameter;
                    }
                }
            }

            // 泛型方法签名绑定上下文（6e-M20）
            foreach (var methodTypeParameter in _declaringMethodTypeParameters)
            {
                if (methodTypeParameter.Name == name)
                {
                    return methodTypeParameter;
                }
            }

            // using 别名（6e-M18）：`using Rt = System.Runtime;` + 类型位置 / Rt.StaticMethod()
            if (_usingAliases.TryGetValue(name, out var aliasTarget))
            {
                return LookupType(aliasTarget);
            }

            var lookup = _scope.TryLookupSymbol(name);
            if (lookup is TypeSymbol declaredType)
            {
                return declaredType;
            }

            // 6e-M19 M2-a：System.Object 内建单例（用户同名类已由上方 scope 命中短路；小写关键字与 C# 原名皆可）
            if (name is "object" or "Object")
            {
                return NamedTypeSymbol.SystemObject;
            }

            // 点号全名（`Foo.Bar.Point` / `Foo.Bar.Color`）：内部类/枚举按 FullName 匹配，或外部类型直查
            if (name.IndexOf('.') >= 0)
            {
                var fullNameClass = FindDeclaredClassByFullName(name);
                if (fullNameClass != null)
                {
                    return fullNameClass;
                }

                var fullNameEnum = FindDeclaredEnumByFullName(name);
                if (fullNameEnum != null)
                {
                    return fullNameEnum;
                }

                // 6e-M19 M2-a：System.Object / System.Type 内建（用户同名类优先）
                var systemType = ResolveBuiltInSystemType(name);
                if (systemType != null)
                {
                    return systemType;
                }

                return ExternalTypeResolver.TryResolve(name, _references);
            }

            // using 前缀：`using Foo.Bar;` 后 `LookupType("Point")` → 内部命名空间类/枚举 + 引用程序集
            foreach (var ns in _usingNamespaces)
            {
                var fullName = ns.Length == 0 ? name : ns + "." + name;
                var internalClass = FindDeclaredClassByFullName(fullName);
                if (internalClass != null)
                {
                    return internalClass;
                }

                var internalEnum = FindDeclaredEnumByFullName(fullName);
                if (internalEnum != null)
                {
                    return internalEnum;
                }

                var systemType = ResolveBuiltInSystemType(fullName);
                if (systemType != null)
                {
                    return systemType;
                }

                var externalType = ExternalTypeResolver.TryResolve(fullName, _references);
                if (externalType != null)
                {
                    return externalType;
                }
            }

            return null;
        }

        /// <summary>6e-M19 M2-b：facade 类全名 → 承载类型映射（null 值 = 自身，Object/Type facade）。</summary>
        private static readonly Dictionary<string, TypeSymbol?> FacadeTargets = new Dictionary<string, TypeSymbol?>
        {
            ["System.String"] = TypeSymbol.String,
            ["System.SByte"] = TypeSymbol.Int8,
            ["System.Int16"] = TypeSymbol.Int16,
            ["System.Int32"] = TypeSymbol.Int32,
            ["System.Int64"] = TypeSymbol.Int64,
            ["System.Byte"] = TypeSymbol.UInt8,
            ["System.UInt16"] = TypeSymbol.UInt16,
            ["System.UInt32"] = TypeSymbol.UInt32,
            ["System.UInt64"] = TypeSymbol.UInt64,
            ["System.Single"] = TypeSymbol.Float,
            ["System.Double"] = TypeSymbol.Double,
            ["System.Boolean"] = TypeSymbol.Boolean,
            ["System.Char"] = TypeSymbol.Char,
            ["System.Object"] = null,
            ["System.Type"] = null,
            ["System.Exception"] = null,
        };

        /// <summary>6e-M19 M2-b：facade 静态常量表（i32.MaxValue 等，编译期折叠为字面量）。</summary>
        private static readonly Dictionary<string, Dictionary<string, object>> FacadeConstants = new Dictionary<string, Dictionary<string, object>>
        {
            ["System.Int32"] = new Dictionary<string, object>
            {
                ["MaxValue"] = int.MaxValue,
                ["MinValue"] = int.MinValue,
            },
            ["System.Int64"] = new Dictionary<string, object>
            {
                ["MaxValue"] = long.MaxValue,
                ["MinValue"] = long.MinValue,
            },
            ["System.Byte"] = new Dictionary<string, object>
            {
                // 归一为 i32（u8 常量值域安全，且字面量发射器不识别 byte 装箱）
                ["MaxValue"] = (int)byte.MaxValue,
                ["MinValue"] = (int)byte.MinValue,
            },
            ["System.Double"] = new Dictionary<string, object>
            {
                ["MaxValue"] = double.MaxValue,
                ["MinValue"] = double.MinValue,
                ["Epsilon"] = double.Epsilon,
                ["NaN"] = double.NaN,
                ["PositiveInfinity"] = double.PositiveInfinity,
                ["NegativeInfinity"] = double.NegativeInfinity,
            },
            ["System.Boolean"] = new Dictionary<string, object>
            {
                ["TrueString"] = "True",
                ["FalseString"] = "False",
            },
            ["System.Int16"] = new Dictionary<string, object>
            {
                ["MaxValue"] = (short)32767,
                ["MinValue"] = (short)(-32768),
            },
            ["System.UInt16"] = new Dictionary<string, object>
            {
                ["MaxValue"] = (ushort)65535,
                ["MinValue"] = (ushort)0,
            },
            ["System.UInt32"] = new Dictionary<string, object>
            {
                ["MaxValue"] = (uint)4294967295,
                ["MinValue"] = (uint)0,
            },
            ["System.UInt64"] = new Dictionary<string, object>
            {
                ["MaxValue"] = ulong.MaxValue,
                ["MinValue"] = (ulong)0,
            },
            ["System.SByte"] = new Dictionary<string, object>
            {
                ["MaxValue"] = (sbyte)127,
                ["MinValue"] = (sbyte)(-128),
            },
            ["System.Char"] = new Dictionary<string, object>
            {
                ["MaxValue"] = (char)0xFFFF,
                ["MinValue"] = (char)0x0000,
            },
            ["System.Single"] = new Dictionary<string, object>
            {
                ["MaxValue"] = float.MaxValue,
                ["MinValue"] = float.MinValue,
                ["Epsilon"] = float.Epsilon,
                ["NaN"] = float.NaN,
                ["PositiveInfinity"] = float.PositiveInfinity,
                ["NegativeInfinity"] = float.NegativeInfinity,
            },
        };

        /// <summary>基元类型 → facade 全名（全基元集 + facade 类符号自身——dotted 类名形式解析产物）。</summary>
        private static string? FacadeNameOfType(TypeSymbol receiverType)
        {
            if (receiverType is NamedTypeSymbol classSymbol &&
                (classSymbol.IsFacadeClass || FacadeTargets.ContainsKey(classSymbol.FullName)))
            {
                return classSymbol.FullName;
            }

            if (receiverType == TypeSymbol.String) return "System.String";
            if (receiverType == TypeSymbol.Boolean) return "System.Boolean";
            if (receiverType == TypeSymbol.Char) return "System.Char";
            if (receiverType == TypeSymbol.Int8) return "System.SByte";
            if (receiverType == TypeSymbol.Int16) return "System.Int16";
            if (receiverType == TypeSymbol.Int32) return "System.Int32";
            if (receiverType == TypeSymbol.Int64) return "System.Int64";
            if (receiverType == TypeSymbol.UInt8) return "System.Byte";
            if (receiverType == TypeSymbol.UInt16) return "System.UInt16";
            if (receiverType == TypeSymbol.UInt32) return "System.UInt32";
            if (receiverType == TypeSymbol.UInt64) return "System.UInt64";
            if (receiverType == TypeSymbol.Float) return "System.Single";
            if (receiverType == TypeSymbol.Double) return "System.Double";
            return null;
        }

        /// <summary>6e-M19 M2-a：System.Object / System.Type 内建单例按名解析（裸 Type 不在此列，避免劫持 using 导入的同名类型）。</summary>
        private static TypeSymbol? ResolveBuiltInSystemType(string fullName)
        {
            switch (fullName)
            {
                case "object":
                case "Object":
                case "System.Object":
                    return NamedTypeSymbol.SystemObject;
                case "System.Type":
                    return NamedTypeSymbol.SystemType;
                default:
                    return null;
            }
        }

        /// <summary>
        /// 按语言解析内建类型名（M2 收敛至 <see cref="Language.LookupBuiltinType"/>）：
        /// 共享 any/bool/char/string/void；CO 简写 i8/u8/.../f32/f64（+128 占位），
        /// C# 原名 sbyte/byte/short/.../float/double——词汇表各由语言实现承载。
        /// </summary>
        private TypeSymbol? LookupBuiltinType(string name)
        {
            return _language.LookupBuiltinType(name);
        }

        /// <summary>按全名（`Namespace.ClassName`）沿作用域链查找内部声明的类。
        /// Phase 1-5：函数体绑定阶段优先经全局命名空间树定位（O(命名空间成员)），未命中回退作用域链扫描
        /// （树只索引全局静态声明；动态/单态化/委托/父作用域类型等以回退兜底）。</summary>
        private NamedTypeSymbol? FindDeclaredClassByFullName(string fullName)
        {
            var viaTree = ResolveDeclaredTypeByFullName(fullName, wantEnum: false);
            if (viaTree != null)
            {
                return viaTree;
            }

            for (var scope = _scope; scope != null; scope = scope.Parent)
            {
                foreach (var cls in scope.GetDeclaredClasses())
                {
                    if (cls.FullName == fullName)
                    {
                        return cls;
                    }
                }
            }

            return null;
        }

        /// <summary>按全名（`Namespace.EnumName`）沿作用域链查找内部声明的枚举。</summary>
        private NamedTypeSymbol? FindDeclaredEnumByFullName(string fullName)
        {
            var viaTree = ResolveDeclaredTypeByFullName(fullName, wantEnum: true);
            if (viaTree != null)
            {
                return viaTree;
            }

            for (var scope = _scope; scope != null; scope = scope.Parent)
            {
                foreach (var enumType in scope.GetDeclaredEnums())
                {
                    if (enumType.FullName == fullName)
                    {
                        return enumType;
                    }
                }
            }

            return null;
        }

        /// <summary>经全局命名空间树按全名定位命名类型（类或枚举，按 wantEnum 过滤）；树不可用或未命中返回 null。</summary>
        private NamedTypeSymbol? ResolveDeclaredTypeByFullName(string fullName, bool wantEnum)
        {
            if (_globalNamespace?.TryGetType(fullName) is NamedTypeSymbol named)
            {
                var isEnum = named.TypeKind == TypeKind.Enum;
                if (isEnum == wantEnum)
                {
                    return named;
                }
            }

            return null;
        }

        /// <summary>纯标识符成员链拍平成点号字符串（`Foo.Bar.Program`）；含调用/索引等非纯链返回 null。</summary>
        private static string? ResolveDottedTypeName(ExpressionSyntax expr)
        {
            if (expr is NameExpressionSyntax nameExpr)
            {
                return nameExpr.IdentifierToken.Text;
            }

            if (expr is MemberAccessExpressionSyntax memberAccess &&
                memberAccess.IdentifierToken.Text.Length > 0)
            {
                var left = ResolveDottedTypeName(memberAccess.Expression);
                return left == null ? null : left + "." + memberAccess.IdentifierToken.Text;
            }

            return null;
        }

        /// <summary>限定入口解析：`ClassName.Method` / `Namespace.ClassName.Method` → 类静态方法。</summary>
        private static FunctionSymbol? ResolveQualifiedEntryPoint(Binder binder, string entryPointName, TextLocation location)
        {
            var lastDot = entryPointName.LastIndexOf('.');
            var className = entryPointName.Substring(0, lastDot);
            var methodName = entryPointName.Substring(lastDot + 1);

            var classMatches = new List<NamedTypeSymbol>();
            for (var scope = binder._scope; scope != null; scope = scope.Parent)
            {
                foreach (var cls in scope.GetDeclaredClasses())
                {
                    if (cls.Name == className || cls.FullName == className)
                    {
                        classMatches.Add(cls);
                    }
                }
            }

            if (classMatches.Count == 0)
            {
                binder.Diagnostics.ReportEntryClassNotFound(location, className);
                return null;
            }

            if (classMatches.Count > 1)
            {
                binder.Diagnostics.ReportEntryClassAmbiguous(location, className);
                return null;
            }

            var classType = classMatches[0];
            var method = classType.IsInterface ? null : classType.GetDeclaredMethod(methodName);
            if (method == null || !method.IsStatic)
            {
                binder.Diagnostics.ReportEntryMethodNotFound(location, className, methodName);
                return null;
            }

            return method;
        }

        private static bool TryGetIntConstant(BoundExpression expression, out int value)
        {
            if (expression.ConstantValue?.Value is int intValue)
            {
                value = intValue;
                return true;
            }

            if (expression is BoundUnaryExpression unary &&
                unary.Op.Kind == BoundUnaryOperatorKind.Negation &&
                unary.Operand.ConstantValue?.Value is int operandValue)
            {
                value = -operandValue;
                return true;
            }

            value = 0;
            return false;
        }

        private static bool IsNumeric(TypeSymbol type)
        {
            return type.IsNumeric && !type.IsPlaceholder128;
        }

        /// <summary>6e-M19 M5-a：可空引用型（类/接口/string/数组/any）——null 字面量的合法转换目标。</summary>
        private static bool IsNullableReferenceType(TypeSymbol type)
        {
            return !type.IsValueType && (type is NamedTypeSymbol || type == TypeSymbol.String ||
                   type == TypeSymbol.Any || type.ElementType != null);
        }

        /// <summary>6e-M21 Phase 4/6：可接受范围内常量隐式窄化的目标整型（含 64 位：ulong y = 2 与 C# 同构）。</summary>
        private static bool IsNarrowIntegerTarget(TypeSymbol type)
        {
            return type == TypeSymbol.Int8 || type == TypeSymbol.Int16 ||
                   type == TypeSymbol.UInt8 || type == TypeSymbol.UInt16 ||
                   type == TypeSymbol.UInt32 || type == TypeSymbol.Int64 ||
                   type == TypeSymbol.UInt64;
        }

        private static bool FitsInIntegerType(long value, TypeSymbol type)
        {
            if (type.IsSigned)
            {
                return type.BitWidth switch
                {
                    8 => value >= sbyte.MinValue && value <= sbyte.MaxValue,
                    16 => value >= short.MinValue && value <= short.MaxValue,
                    32 => value >= int.MinValue && value <= int.MaxValue,
                    _ => true,
                };
            }

            return type.BitWidth switch
            {
                8 => value >= 0 && value <= byte.MaxValue,
                16 => value >= 0 && value <= ushort.MaxValue,
                32 => value >= 0 && value <= uint.MaxValue,
                _ => value >= 0,
            };
        }

        /// <summary>取整数常量（含一元负号），任意整数装箱表示均可。</summary>
        private static bool TryGetIntegerConstant(BoundExpression expression, out long value)
        {
            var constant = expression.ConstantValue?.Value;
            if (constant is int or long or sbyte or short or byte or ushort or uint or char)
            {
                value = NumericBox.ToSigned64(constant);
                return true;
            }

            if (expression is BoundUnaryExpression unary &&
                unary.Op.Kind == BoundUnaryOperatorKind.Negation &&
                unary.Operand.ConstantValue != null)
            {
                value = unchecked(-NumericBox.ToSigned64(unary.Operand.ConstantValue.Value));
                return true;
            }

            value = 0;
            return false;
        }

        private void BindEnumDeclaration(EnumDeclarationSyntax syntax, string @namespace = "")
        {
            var members = new Dictionary<string, int>();
            var nextValue = 0;

            foreach (var member in syntax.Members)
            {
                var memberName = member.Identifier.Text;

                if (members.ContainsKey(memberName))
                {
                    _diagnostics.ReportSymbolAlreadyDeclared(member.Identifier.Location, memberName);
                }
                else if (member.Value != null)
                {
                    var boundValue = BindExpression(member.Value);
                    if (TryGetIntConstant(boundValue, out var intValue))
                    {
                        nextValue = intValue;
                        members.Add(memberName, nextValue);
                    }
                    else
                    {
                        _diagnostics.ReportEnumMemberValueMustBeInt(member.Value.Location, memberName);
                    }
                }
                else
                {
                    members.Add(memberName, nextValue);
                }

                nextValue = nextValue + 1;
            }

            var enumType = new NamedTypeSymbol(syntax.Identifier.Text, @namespace, Visibility.Public, declaration: null)
            {
                TypeKind = TypeKind.Enum,
                IsSealed = true,
            };
            enumType.SetEnumMembers(members);

if (!_scope.TryDeclareEnum(enumType))
            {
                _diagnostics.ReportSymbolAlreadyDeclared(syntax.Identifier.Location, syntax.Identifier.Text);
            }
        }
    }
}
