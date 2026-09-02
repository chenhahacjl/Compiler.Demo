using Cocoa.CodeAnalysis.Lowering;
using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.CocoaAssembly;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Cocoa.Syntax;
using SSyntax = Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

using Cocoa.CodeAnalysis.Emit.IL;
namespace Cocoa.CodeAnalysis.Cocoa.Binding
{
    /// <summary>
    /// Partial member surface of the binder.
    /// </summary>
    internal partial class CocoaBinder
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
                        var parameter = CreateParameterSymbol(parameterName, parameterType, parameterSyntax, parameters.Count);
                        parameters.Add(parameter);
                    }
                }

                var type = BindTypeClause(syntax.Type) ?? TypeSymbol.Void;

                var isExtern = syntax.Modifiers.Any(m => m.Kind == SSyntax.SyntaxKind.CdeclKeyword || m.Kind == SSyntax.SyntaxKind.StdcallKeyword);
                var isSyscall = syntax.Modifiers.Any(m => m.Kind == SSyntax.SyntaxKind.SyscallKeyword);

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
                    .FirstOrDefault(k => k == SSyntax.SyntaxKind.CdeclKeyword || k == SSyntax.SyntaxKind.StdcallKeyword) switch
                {
                    SSyntax.SyntaxKind.CdeclKeyword => CallingConvention.Cdecl,
                    SSyntax.SyntaxKind.StdcallKeyword => CallingConvention.StdCall,
                    _ => CallingConvention.Winapi,
                };

                var function = new FunctionSymbol(syntax.Identifier.Text, parameters.ToImmutable(), type, syntax, isExtern, importedDll, callingConvention, @namespace: namespaceName ?? "")
                {
                    TypeParameters = _declaringMethodTypeParameters,
                };
                BindWhereClauses(syntax.WhereClauses, function.TypeParameters);

                if (syntax.Identifier.Text != null && !_scope.TryDeclareFunction(function))
                {
                    _diagnostics.ReportSymbolAlreadyDeclared(syntax.Identifier.Location, function.Name);
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

        private ImmutableArray<ParameterSymbol> BindParameters(SSyntax.SeparatedSyntaxList<ParameterSyntax> parameterSyntaxList)
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
                    var parameter = CreateParameterSymbol(parameterName, parameterType, parameterSyntax, parameters.Count);
                    parameters.Add(parameter);
                }
            }

            return parameters.ToImmutable();
        }

        /// <summary>形参符号构造（6e-M23 R2）：携带 out/ref 修饰符；普通形参可赋值（对齐 C#），this 保持只读。</summary>
        private ParameterSymbol CreateParameterSymbol(string name, TypeSymbol type, ParameterSyntax syntax, int ordinal)
        {
            var isOut = syntax.Modifier?.Kind == SSyntax.SyntaxKind.OutKeyword;
            var isRef = syntax.Modifier?.Kind == SSyntax.SyntaxKind.RefKeyword;

            return new ParameterSymbol(name, type, ordinal, isOut, isRef);
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

            foreach (var parameterToken in syntax.Parameters)
            {
                var parameterName = parameterToken.Text ?? "";
                if (parameterName.Length == 0)
                {
                    continue;
                }

                if (!seen.Add(parameterName))
                {
                    _diagnostics.ReportError(parameterToken.Location, $"类型参数 '{parameterName}' 重复或与外层类型参数同名。");
                    continue;
                }

                parameters.Add(new TypeParameterSymbol(parameterName, parameters.Count, owningClass: null));
            }

            return parameters.ToImmutable();
        }

        /// <summary>从修饰符列表解析可见性（public &gt; internal &gt; protected &gt; private；无修饰符取默认值）。</summary>
        private static Visibility GetVisibility(ImmutableArray<SSyntax.SyntaxToken> modifiers, Visibility defaultVisibility)
        {
            if (modifiers.Any(m => m.Kind == SSyntax.SyntaxKind.PublicKeyword))
            {
                return Visibility.Public;
            }

            if (modifiers.Any(m => m.Kind == SSyntax.SyntaxKind.InternalKeyword))
            {
                return Visibility.Internal;
            }

            if (modifiers.Any(m => m.Kind == SSyntax.SyntaxKind.ProtectedKeyword))
            {
                return Visibility.Protected;
            }

            if (modifiers.Any(m => m.Kind == SSyntax.SyntaxKind.PrivateKeyword))
            {
                return Visibility.Private;
            }

            return defaultVisibility;
        }

        private static bool IsVisibilityModifier(SSyntax.SyntaxKind kind)
        {
            return kind == SSyntax.SyntaxKind.PublicKeyword ||
                   kind == SSyntax.SyntaxKind.InternalKeyword ||
                   kind == SSyntax.SyntaxKind.ProtectedKeyword ||
                   kind == SSyntax.SyntaxKind.PrivateKeyword;
        }

        private static bool HasVisibilityModifier(ImmutableArray<SSyntax.SyntaxToken> modifiers)
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
                var location = (syntax.Setter?.Keyword ?? syntax.Getter?.Keyword).Location;
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

        /// <summary>创建类符号；部分类（partial）的多段声明合并为同一符号（各段成员分别绑定）。</summary>
        private NamedTypeSymbol DeclareClassGroup(List<(ClassDeclarationSyntax Syntax, string Namespace)> parts)
        {
            var primary = parts[0];
            var name = primary.Syntax.Identifier.Text;
            var visibility = GetVisibility(primary.Syntax.Modifiers, Visibility.Internal);

            // `facade` 修饰符（6e-M20）：类须命中 FacadeTargets 才被认领为基元成员面载体；
            // struct 的 facade 为 6e-M26 Phase3 形态（映射 CO struct 到 BCL 值类型），不要求命中 FacadeTargets。
            var isStructDecl = primary.Syntax.ClassKeyword.Kind == SSyntax.SyntaxKind.StructKeyword;
            if (primary.Syntax.Modifiers.Any(m => m.Kind == SSyntax.SyntaxKind.FacadeKeyword) &&
                !isStructDecl &&
                !FacadeTargets.ContainsKey(primary.Namespace.Length == 0 ? name : primary.Namespace + "." + name))
            {
                _diagnostics.ReportInvalidFacadeMarker(
                    primary.Syntax.Identifier.Location,
                    primary.Namespace.Length == 0 ? name : primary.Namespace + "." + name);
            }

            if (parts.Count > 1)
            {
                for (var i = 1; i < parts.Count; i++)
                {
                    var part = parts[i];

                    if (!part.Syntax.Modifiers.Any(m => m.Kind == SSyntax.SyntaxKind.PartialKeyword))
                    {
                        _diagnostics.ReportSymbolAlreadyDeclared(part.Syntax.Identifier.Location, name);
                    }

                    var partVisibility = GetVisibility(part.Syntax.Modifiers, Visibility.Internal);
                    if (partVisibility != visibility)
                    {
                        _diagnostics.ReportError(part.Syntax.Identifier.Location, $"部分类 '{name}' 的多个部分可见性不一致。");
                    }
                }
            }

            foreach (var (syntax, ns) in parts)
            {
                if (GetVisibility(syntax.Modifiers, Visibility.Internal) is Visibility.Private or Visibility.Protected)
                {
                    _diagnostics.ReportError(syntax.Identifier.Location, $"类 '{name}' 的可见性只能为 public 或 internal。");
                }
            }

            // 6e-M26：struct（值类型）与 class 共用同一 NamedTypeSymbol，TypeKind 判别
            var isStruct = primary.Syntax.IsStruct;
            NamedTypeSymbol classType = new NamedTypeSymbol(name, primary.Namespace, visibility, primary.Syntax);
            classType.TypeKind = isStruct ? TypeKind.Struct : TypeKind.Class;
            classType.IsAbstract = parts.Any(p => p.Syntax.Modifiers.Any(m => m.Kind == SSyntax.SyntaxKind.AbstractKeyword));
            classType.IsSealed = isStruct || parts.Any(p => p.Syntax.Modifiers.Any(m => m.Kind == SSyntax.SyntaxKind.SealedKeyword));

            // struct 约束（MVP）：常规 struct 不可有基类/接口、不可 abstract、不可 facade；
            // 但 `facade struct : <BCL值类型>` 是允许的特殊形态（6e-M26 Phase3：映射 CO struct 到 BCL）。
            if (isStruct)
            {
                var isFacadeStruct = parts.Any(p => p.Syntax.Modifiers.Any(m => m.Kind == SSyntax.SyntaxKind.FacadeKeyword));
                foreach (var (syntax, _) in parts)
                {
                    if (syntax.Modifiers.Any(m => m.Kind == SSyntax.SyntaxKind.AbstractKeyword))
                    {
                        _diagnostics.ReportError(syntax.Identifier.Location, $"struct '{name}' 不能声明为 abstract。");
                    }
                }

                if (isFacadeStruct)
                {
                    foreach (var (syntax, _) in parts)
                    {
                        if (syntax.BaseTypes.Length > 1)
                        {
                            _diagnostics.ReportError(syntax.Identifier.Location, $"facade struct '{name}' 至多只能指定一个基类（目标 BCL 值类型）。");
                        }
                    }
                }
                else
                {
                    foreach (var (syntax, _) in parts)
                    {
                        if (syntax.BaseTypes.Length > 0)
                        {
                            _diagnostics.ReportError(syntax.Identifier.Location, $"struct '{name}' 不能有基类或实现接口（MVP 阶段仅支持值字段/构造器）。");
                        }

                        if (syntax.Modifiers.Any(m => m.Kind == SSyntax.SyntaxKind.FacadeKeyword))
                        {
                            _diagnostics.ReportError(syntax.Identifier.Location, $"struct '{name}' 不能声明为 facade（除非同时指定 BCL 值类型基类）。");
                        }
                    }
                }
            }

            // 泛型类型参数声明（6e-M20）：`class Box<T, U>`——部分类各段须一致
            var typeParameters = BindClassTypeParameters(primary.Syntax.TypeParameters, classType, name);
            foreach (var (syntax, _) in parts.Skip(1))
            {
                if (!SyntaxTypeParametersMatch(syntax.TypeParameters, typeParameters))
                {
                    _diagnostics.ReportError(syntax.Identifier.Location, $"部分类 '{name}' 的多个部分的类型参数列表不一致。");
                }
            }

            classType.TypeParameters = typeParameters;

            // where 约束解析在阶段 3.2（接口全部声明后）——约束可引用后置接口
            if (!_scope.TryDeclareClass(classType))
            {
                _diagnostics.ReportSymbolAlreadyDeclared(primary.Syntax.Identifier.Location, name);
            }

            return classType;
        }

        /// <summary>阶段 3.2：类泛型 where 约束解析（6e-M20；接口/类符号均已就位）。</summary>
        private void BindClassWhereClauses(List<(ClassDeclarationSyntax Syntax, string Namespace)> parts, NamedTypeSymbol classType)
        {
            var previous = _bindingClass;
            _bindingClass = classType;

            try
            {
                BindWhereClauses(parts.SelectMany(p => p.Syntax.WhereClauses), classType.TypeParameters);
            }
            finally
            {
                _bindingClass = previous;
            }
        }

        /// <summary>类泛型类型参数绑定：建 TypeParameterSymbol 列表（重名/与类名冲突诊断）。</summary>
        private ImmutableArray<TypeParameterSymbol> BindClassTypeParameters(TypeParameterListSyntax? syntax, NamedTypeSymbol classType, string className)
        {
            if (syntax == null)
            {
                return ImmutableArray<TypeParameterSymbol>.Empty;
            }

            var parameters = ImmutableArray.CreateBuilder<TypeParameterSymbol>();
            var seen = new HashSet<string>();

            foreach (var parameterToken in syntax.Parameters)
            {
                var parameterName = parameterToken.Text ?? "";
                if (parameterName.Length == 0)
                {
                    continue;
                }

                if (!seen.Add(parameterName))
                {
                    _diagnostics.ReportError(parameterToken.Location, $"类型参数 '{parameterName}' 重复。");
                    continue;
                }

                parameters.Add(new TypeParameterSymbol(parameterName, parameters.Count, classType));
            }

            if (parameters.Any(p => p.Name == className))
            {
                _diagnostics.ReportError(syntax.Location, $"类型参数不能与类 '{className}' 同名。");
            }

            return parameters.ToImmutable();
        }

        /// <summary>部分类各段类型参数列表一致性（按名字逐一比较）。</summary>
        private static bool SyntaxTypeParametersMatch(TypeParameterListSyntax? syntax, ImmutableArray<TypeParameterSymbol> expected)
        {
            if (syntax == null)
            {
                return expected.IsEmpty;
            }

            if (syntax.Parameters.Length != expected.Length)
            {
                return false;
            }

            for (var i = 0; i < syntax.Parameters.Length; i++)
            {
                if (syntax.Parameters[i].Text != expected[i].Name)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// where 约束子句解析（6e-M20）：约束类型经 LookupType 解析（可为接口/基类/其他类型参数）；
        /// `new()` / `class` 走标志位。未知类型参数名报错。实例化期校验实参满足约束。
        /// </summary>
        private void BindWhereClauses(IEnumerable<WhereClauseSyntax> clauses, ImmutableArray<TypeParameterSymbol> typeParameters)
        {
            foreach (var clause in clauses)
            {
                var parameterName = clause.Identifier.Text;
                var target = typeParameters.FirstOrDefault(p => p.Name == parameterName);

                if (target == null)
                {
                    _diagnostics.ReportError(clause.Identifier.Location, $"'{parameterName}' 不是本声明的类型参数。");
                    continue;
                }

                var constraints = ImmutableArray.CreateBuilder<TypeSymbol>();
                foreach (var constraintSyntax in clause.ConstraintTypes)
                {
                    var text = constraintSyntax.Identifier.Text;
                    if (text == "new()")
                    {
                        target.HasNewConstraint = true;
                        continue;
                    }

                    if (text == "class")
                    {
                        if (target.HasValueTypeConstraint)
                        {
                            _diagnostics.ReportError(constraintSyntax.Location, $"类型参数 '{parameterName}' 不能同时具有 'struct' 与 'class' 约束。");
                            continue;
                        }

                        target.HasReferenceTypeConstraint = true;
                        continue;
                    }

                    // struct 值类型约束（6e-M22 C1）：非关键字，按约束文本特判（与 C# 一致保留字面）
                    if (text == "struct")
                    {
                        if (target.HasReferenceTypeConstraint)
                        {
                            _diagnostics.ReportError(constraintSyntax.Location, $"类型参数 '{parameterName}' 不能同时具有 'class' 与 'struct' 约束。");
                            continue;
                        }

                        target.HasValueTypeConstraint = true;
                        continue;
                    }

                    var constraintType = BindTypeClause(constraintSyntax);
                    if (constraintType == null)
                    {
                        continue;
                    }

                    constraints.Add(constraintType);
                }

                target.ConstraintTypes = target.ConstraintTypes.AddRange(constraints);
            }
        }

        private void BindClassBase(ClassDeclarationSyntax syntax, NamedTypeSymbol classType)
        {
            // 6e-M20：声明上下文（泛型基类 `class MyList<T> extends List<T>` 的 T 解析）
            var previousBindingClass = _bindingClass;
            _bindingClass = classType;

            try
            {
                BindClassBaseCore(syntax, classType);
            }
            finally
            {
                _bindingClass = previousBindingClass;
            }
        }

        private void BindClassBaseCore(ClassDeclarationSyntax syntax, NamedTypeSymbol classType)
        {
            // 基类型解析（`class Foo: Bar, IA, IB`；首个非接口 = 基类，其余须为接口；部分类多段声明时基类必须一致）
            // 6e-M20：泛型基类/基接口经实参实例化
            var seenNonInterface = false;
            foreach (var baseClause in syntax.BaseTypes)
            {
                var baseName = baseClause.Identifier.Text;
                var baseType = BindBaseTypeClause(baseClause);

                if (baseType == null)
                {
                    continue;
                }
                else if (baseType.IsInterface)
                {
                    // 类实现接口：`class Rectangle: IShape`
                    classType.AddInterface(baseType);
                }
                else
                {
                    // 非接口基类：至多一个
                    if (seenNonInterface)
                    {
                        _diagnostics.ReportError(baseClause.Location, $"类 '{classType.Name}' 只能有一个非接口基类。");
                    }
                    else if (classType.BaseType != null)
                    {
                        if (classType.BaseType != baseType)
                        {
                            _diagnostics.ReportError(syntax.Identifier.Location, $"部分类 '{classType.Name}' 的多个部分声明的基类不一致。");
                        }
                    }
                    else if (baseType.IsSealed)
                    {
                        _diagnostics.ReportCannotInheritSealed(syntax.Identifier.Location, baseName);
                    }
                    else
                    {
                        classType.BaseType = baseType;

                        // 循环继承检测：沿基类链查找本类
                        var seen = new HashSet<NamedTypeSymbol>();
                        var circular = false;
                        for (var current = baseType; current != null && seen.Add(current); current = current.BaseType)
                        {
                            if (current == classType)
                            {
                                circular = true;
                                break;
                            }
                        }

                        if (circular)
                        {
                            _diagnostics.ReportCircularInheritance(syntax.Identifier.Location, baseName);
                            classType.BaseType = null;
                        }
                    }

                    seenNonInterface = true;
                }
            }
        }

        /// <summary>
        /// 是否有可用基类（6e-M19 M2-c 反转）：内建 System.Object 携带真实成员面（虚四方法），
        /// 视为真基类——override 解析、base 表达式、成员沿链上溯均正常工作。
        /// 仅接口（BaseType=null）无基类。
        /// </summary>
        private static bool HasBaseClass(NamedTypeSymbol classType)
            => classType.BaseType != null;

        /// <summary>
        /// 基类/基接口子句绑定（6e-M20 泛型感知）：`extends List&lt;T&gt;` / `: Collection&lt;int&gt;`
        /// 经泛型名解析实例化；裸泛型定义报错并返回 null。
        /// </summary>
        private NamedTypeSymbol? BindBaseTypeClause(TypeClauseSyntax syntax)
        {
            TypeSymbol? resolved;

            if (syntax is GenericTypeClauseSyntax generic)
            {
                resolved = BindGenericTypeClause(generic);
            }
            else
            {
                var lookup = LookupType(syntax.Identifier.Text);
                if (lookup is NamedTypeSymbol { IsGenericDefinition: true } nakedGeneric)
                {
                    _diagnostics.ReportGenericDefinitionRequiresTypeArguments(syntax.Identifier.Location, nakedGeneric.Name);
                    return null;
                }

                resolved = lookup;
            }

            return resolved as NamedTypeSymbol;
        }

        private void BindClassMembers(ClassDeclarationSyntax syntax, NamedTypeSymbol classType, List<FunctionSymbol> classFunctions, string @namespace)
        {
            // 6e-M20：声明上下文（字段/方法签名的 T 解析）
            var previousBindingClass = _bindingClass;
            _bindingClass = classType;

            try
            {
                BindClassMembersCore(syntax, classType, classFunctions, @namespace);
            }
            finally
            {
                _bindingClass = previousBindingClass;
            }
        }

        private void BindClassMembersCore(ClassDeclarationSyntax syntax, NamedTypeSymbol classType, List<FunctionSymbol> classFunctions, string @namespace)
        {
            foreach (var member in syntax.Members)
            {
                if (member.Modifiers.Any(m => m.Kind == SSyntax.SyntaxKind.PartialKeyword))
                {
                    _diagnostics.ReportError(member.Location, "partial 只能用于类声明。");
                    continue;
                }

                if (classType.IsStatic &&
                    (member is ClassFieldDeclarationSyntax && !member.Modifiers.Any(m => m.Kind == SSyntax.SyntaxKind.StaticKeyword) ||
                     member is FunctionDeclarationSyntax && !member.Modifiers.Any(m => m.Kind == SSyntax.SyntaxKind.StaticKeyword)))
                {
                    _diagnostics.ReportError(member.Location, $"静态类 {classType.Name} 只能包含静态成员。");
                }

                if (member is ClassFieldDeclarationSyntax fieldDeclaration)
                {
                    var fieldType = BindTypeClause(fieldDeclaration.Type);
                    var fieldVisibility = GetVisibility(fieldDeclaration.Modifiers, Visibility.Private);
                    var fieldIsReadonly = fieldDeclaration.Modifiers.Any(m => m.Kind == SSyntax.SyntaxKind.ReadonlyKeyword);
                    var fieldIsStatic = fieldDeclaration.Modifiers.Any(m => m.Kind == SSyntax.SyntaxKind.StaticKeyword);

                    if (classType.GetDeclaredField(fieldDeclaration.Identifier.Text) == null)
                    {
                        classType.AddField(new FieldSymbol(fieldDeclaration.Identifier.Text, fieldType, fieldVisibility, classType, isReadonly: fieldIsReadonly, isStatic: fieldIsStatic));
                    }
                    else
                    {
                        _diagnostics.ReportSymbolAlreadyDeclared(fieldDeclaration.Identifier.Location, fieldDeclaration.Identifier.Text);
                    }
                }
                else if (member is ConstructorDeclarationSyntax constructorDeclaration)
                {
                    var isStatic = constructorDeclaration.Modifiers.Any(m => m.Kind == SSyntax.SyntaxKind.StaticKeyword);

                    if (isStatic)
                    {
                        // 静态构造函数（C# 式 `static Foo()` / Cocoa 式 `static constructor()`）→ `.cctor` 符号
                        var location = constructorDeclaration.ConstructorKeyword != null
                            ? constructorDeclaration.ConstructorKeyword.Location
                            : constructorDeclaration.OpenParenthesisToken.Location;

                        if (HasVisibilityModifier(constructorDeclaration.Modifiers))
                        {
                            _diagnostics.ReportError(location, "静态构造函数不能有可见性修饰符（public/private/internal/protected）。");
                        }

                        if (constructorDeclaration.Parameters.Count > 0)
                        {
                            _diagnostics.ReportError(constructorDeclaration.OpenParenthesisToken.Location, "静态构造函数不能有参数。");
                        }

                        if (constructorDeclaration.InitializerKeyword != null)
                        {
                            _diagnostics.ReportError(constructorDeclaration.InitializerKeyword.Location, "静态构造函数不能有构造链（base/this）。");
                        }

                        if (classType.GetDeclaredMethod(".cctor") == null)
                        {
                            var cctor = new FunctionSymbol(".cctor", ImmutableArray<ParameterSymbol>.Empty, TypeSymbol.Void, null,
                                syntax: constructorDeclaration, containingClass: classType, visibility: Visibility.Private) { IsConstructor = true, IsStatic = true };
                            classType.AddMethod(cctor);
                            classFunctions.Add(cctor);
                        }
                        else
                        {
                            _diagnostics.ReportSymbolAlreadyDeclared(location, ".cctor");
                        }
                    }
                    else
                    {
                        var parameters = BindParameters(constructorDeclaration.Parameters);
                        var ctorVisibility = GetVisibility(constructorDeclaration.Modifiers, Visibility.Private);

                        if (classType.GetDeclaredMethod(classType.Name) == null)
                        {
                            var ctor = new FunctionSymbol(classType.Name, parameters, TypeSymbol.Void, null, syntax: constructorDeclaration, containingClass: classType, visibility: ctorVisibility) { IsConstructor = true };
                            classType.AddMethod(ctor);
                            classFunctions.Add(ctor);
                        }
                        else
                        {
                            var location = constructorDeclaration.ConstructorKeyword != null
                                ? constructorDeclaration.ConstructorKeyword.Location
                                : constructorDeclaration.OpenParenthesisToken.Location;
                            _diagnostics.ReportSymbolAlreadyDeclared(location, classType.Name);
                        }
                    }
                }
                else if (member is FunctionDeclarationSyntax methodDeclaration)
                {
                    var method = BindClassMethodDeclaration(methodDeclaration, classType, dllName: null);

                    if (!classType.HasDeclaredMethodSignature(methodDeclaration.Identifier.Text, method))
                    {
                        classType.AddMethod(method);
                        classFunctions.Add(method);
                    }
                    else
                    {
                        _diagnostics.ReportSymbolAlreadyDeclared(methodDeclaration.Identifier.Location, methodDeclaration.Identifier.Text);
                    }
                }
                else if (member is ImportBlockSyntax importBlock)
                {
                    BindImportBlock(importBlock, classType, classFunctions);
                }
                else if (member is PropertyDeclarationSyntax propertyDeclaration)
                {
                    BindPropertyDeclaration(propertyDeclaration, classType, classFunctions);
                }
                else if (member is EventDeclarationSyntax eventDeclaration)
                {
                    BindEventDeclaration(eventDeclaration, classType);
                }
                else if (member is DelegateDeclarationSyntax delegateDeclaration)
                {
                    BindDelegateDeclaration(delegateDeclaration, classType, classFunctions);
                }
            }
        }

        /// <summary>
        /// 事件声明绑定（6e-M22 C5+ 多播）：解析处理器类型为 FunctionTypeSymbol → 创建 EventSymbol 挂到类，
        /// 合成隐藏后备字段 `_<eventName>`（类型 = 处理器签名的数组，初值 null）。
        /// 订阅/触发的多播语义在语句级脱糖（TryBindEventSubscription / BindEventRaise），三后端零改动。
        /// </summary>
        private void BindEventDeclaration(EventDeclarationSyntax syntax, NamedTypeSymbol classType)
        {
            var handlerType = BindTypeClause(syntax.HandlerType);
            if (handlerType == null)
                return;

            // 6e-M22 D-B：delegate 类处理器 → 提取 Invoke 签名作为 FunctionTypeSymbol
            FunctionTypeSymbol resolvedHandler;
            if (handlerType is FunctionTypeSymbol fts)
            {
                resolvedHandler = fts;
            }
            else if (handlerType is NamedTypeSymbol { TypeKind: TypeKind.Delegate } dc)
            {
                var sig = dc.DelegateSignature();
                if (sig == null)
                {
                    _diagnostics.ReportError(syntax.Identifier.Location, $"delegate 类 '{dc.Name}' 缺少 Invoke 方法。");
                    return;
                }

                resolvedHandler = sig;
            }
            else
            {
                _diagnostics.ReportError(syntax.Identifier.Location, $"事件处理器类型 '{handlerType.Name}' 不是函数类型或 delegate。");
                return;
            }

            var eventName = syntax.Identifier.Text;

            // 静态事件后置（设计 §7.3）：当前多播存储为实例字段，明确拒绝
            if (syntax.Modifiers.Any(m => m.Kind == SSyntax.SyntaxKind.StaticKeyword))
            {
                _diagnostics.ReportStaticEventNotSupported(syntax.Identifier.Location, eventName);
                return;
            }

            var visibility = GetVisibility(syntax.Modifiers, Visibility.Public);
            var eventSymbol = new EventSymbol(eventName, resolvedHandler, visibility, classType);
            classType.AddEvent(eventSymbol);

            // 多播存储：函数值数组字段（初值 null；+= 尾插 / -= 引用相等移除首匹配 / 触发判空快照遍历）
            classType.AddField(new FieldSymbol("_" + eventName, TypeSymbol.ArrayOf(resolvedHandler), visibility, classType));
        }

        /// <summary>
        /// 事件订阅脱糖（6e-M22 C5+ 多播）：`e += f` / `e -= f` → 语句块。
        /// += 尾插（null → 单元素数组；否则复制扩容）；-= 按引用相等移除首个匹配（清空后回置 null）。
        /// 处理器表达式只求值一次（提升隐藏局部）。返回 null 表示目标不是事件（走通用绑定）。
        /// </summary>
        private BoundStatement? TryBindEventSubscription(AssignmentExpressionSyntax syntax)
        {
            var operatorKind = syntax.AssignmentToken.Kind;
            if (operatorKind != SSyntax.SyntaxKind.PlusEqualsToken && operatorKind != SSyntax.SyntaxKind.MinusEqualsToken)
            {
                return null;
            }

            // 目标形态：`obj.e` / `this.e` / 类内裸名 `e`
            string? eventName = null;
            NamedTypeSymbol? ownerClass = null;
            BoundExpression? receiver = null;

            if (syntax.Target.Kind == SSyntax.CocoaSyntaxKind.MemberAccessExpression)
            {
                var memberAccess = (MemberAccessExpressionSyntax)syntax.Target;
                var boundReceiver = BindExpression(memberAccess.Expression);

                if (boundReceiver.Type is NamedTypeSymbol candidate &&
                    candidate.GetEvent(memberAccess.IdentifierToken.Text) is EventSymbol)
                {
                    receiver = boundReceiver;
                    eventName = memberAccess.IdentifierToken.Text;
                    ownerClass = candidate;
                }
            }
            else if (syntax.Target.Kind == SSyntax.CocoaSyntaxKind.NameExpression && _currentClass != null)
            {
                var nameIdentifier = ((NameExpressionSyntax)syntax.Target).IdentifierToken.Text;

                if (_currentClass.GetEvent(nameIdentifier) is EventSymbol)
                {
                    receiver = new BoundThisExpression(syntax.Target, _currentClass);
                    eventName = nameIdentifier;
                    ownerClass = _currentClass;
                }
            }

            if (ownerClass == null || receiver == null || eventName == null)
            {
                return null;
            }

            var eventSymbol = ownerClass.GetEvent(eventName)!;

            if (!IsAccessibleMember(eventSymbol.Visibility, ownerClass))
            {
                _diagnostics.ReportCannotAccessMember(syntax.AssignmentToken.Location, eventName, eventSymbol.Visibility);
                return new BoundBlockStatement(syntax, ImmutableArray<BoundStatement>.Empty);
            }

            var signature = eventSymbol.HandlerType;
            var backingField = ownerClass.GetField("_" + eventName)!;
            var handlerArray = TypeSymbol.ArrayOf(signature);

            _labelCounter++;
            var sequence = _labelCounter;
            var handlerLocal = new LocalVariableSymbol($"__evt{sequence}_h", isReadOnly: true, signature, null);
            var oldListLocal = new LocalVariableSymbol($"__evt{sequence}_old", isReadOnly: true, handlerArray, null);

            var fieldAccess = new BoundMemberAccessExpression(syntax, handlerArray, receiver, backingField.Name, backingField);

            // 处理器绑定：先常规绑定（裸函数名已是函数值），再归一化类型——
            // delegate 类变量/表达式提取 Invoke 签名核对；不匹配时回退语法级转换（方法组/期望类型下推）。
            var boundHandler = BindExpression(syntax.Expression);
            var handlerType = boundHandler.Type switch
            {
                NamedTypeSymbol { TypeKind: TypeKind.Delegate } delegateClass => delegateClass.DelegateSignature(),
                var other => other,
            };

            if (handlerType != TypeSymbol.Error && handlerType != signature)
            {
                boundHandler = BindConversion(syntax.Expression, signature);
            }

            var statements = ImmutableArray.CreateBuilder<BoundStatement>();
            statements.Add(new BoundVariableDeclaration(syntax, handlerLocal, boundHandler));
            statements.Add(new BoundVariableDeclaration(syntax, oldListLocal, fieldAccess));

            var nullLiteral = new BoundLiteralExpression(syntax, null, TypeSymbol.Null);

            if (operatorKind == SSyntax.SyntaxKind.PlusEqualsToken)
            {
                // += 尾插：
                // if __old == null { _<e> = new Fn[1] { __h } }
                // else {
                //     __n = new Fn[__old.Length + 1]
                //     while __i < __old.Length { __n[__i] = __old[__i]; __i++ }
                //     __n[__old.Length] = __h
                //     _<e> = __n
                // }
                var isNullCondition = BoundNodeFactory.Binary(syntax,
                    BoundNodeFactory.Variable(syntax, oldListLocal),
                    SSyntax.SyntaxKind.EqualsEqualsToken,
                    nullLiteral);

                var singleItem = new BoundArrayCreationExpression(
                    syntax, handlerArray,
                    BoundNodeFactory.Literal(syntax, 1),
                    ImmutableArray.Create<BoundExpression>(BoundNodeFactory.Variable(syntax, handlerLocal)));
                var storeSingle = new BoundExpressionStatement(
                    syntax,
                    new BoundMemberAssignmentExpression(syntax, receiver, backingField, singleItem));

                var growStatements = new List<BoundStatement>();
                var newListLocal = new LocalVariableSymbol($"__evt{sequence}_new", isReadOnly: false, handlerArray, null);
                var indexLocal = new LocalVariableSymbol($"__evt{sequence}_i", isReadOnly: false, TypeSymbol.Int32, null);

                growStatements.Add(new BoundVariableDeclaration(
                    syntax, newListLocal,
                    new BoundArrayCreationExpression(
                        syntax, handlerArray,
                        BoundNodeFactory.Add(syntax,
                            LengthOf(syntax, oldListLocal),
                            BoundNodeFactory.Literal(syntax, 1)),
                        ImmutableArray<BoundExpression>.Empty)));
                growStatements.Add(new BoundVariableDeclaration(syntax, indexLocal, BoundNodeFactory.Literal(syntax, 0)));

                var copyLoop = BuildElementCopyLoop(syntax, newListLocal, indexLocal, oldListLocal, $"__evt{sequence}_br");
                foreach (var statement in copyLoop)
                {
                    growStatements.Add(statement);
                }

                growStatements.Add(new BoundExpressionStatement(syntax, new BoundElementAssignmentExpression(
                    syntax, signature,
                    ElementOf(syntax, newListLocal, LengthOf(syntax, oldListLocal)),
                    BoundNodeFactory.Variable(syntax, handlerLocal))));

                growStatements.Add(new BoundExpressionStatement(syntax, new BoundMemberAssignmentExpression(
                    syntax, receiver, backingField, BoundNodeFactory.Variable(syntax, newListLocal))));

                var ifStatement = new BoundIfStatement(
                    syntax, isNullCondition,
                    storeSingle,
                    BoundNodeFactory.Block(syntax, growStatements.ToArray()));

                statements.Add(ifStatement);
            }
            else
            {
                // -= 移除首个引用相等匹配：
                // if __old != null {
                //     __idx = -1; __i = 0
                //     while __i < __old.Length { if __idx == -1 && __old[__i] == __h { __idx = __i }; __i++ }
                //     if __idx >= 0 {
                //         if __old.Length == 1 { _<e> = null }
                //         else { 双游标复制跳过 __idx → _<e> = __n }
                //     }
                // }
                var notNullCondition = BoundNodeFactory.Binary(syntax,
                    BoundNodeFactory.Variable(syntax, oldListLocal),
                    SSyntax.SyntaxKind.BangEqualsToken,
                    nullLiteral);

                var scanStatements = new List<BoundStatement>();
                var matchIndexLocal = new LocalVariableSymbol($"__evt{sequence}_idx", isReadOnly: false, TypeSymbol.Int32, null);
                var scanIndexLocal = new LocalVariableSymbol($"__evt{sequence}_j", isReadOnly: false, TypeSymbol.Int32, null);

                scanStatements.Add(new BoundVariableDeclaration(syntax, matchIndexLocal, BoundNodeFactory.Literal(syntax, -1)));
                scanStatements.Add(new BoundVariableDeclaration(syntax, scanIndexLocal, BoundNodeFactory.Literal(syntax, 0)));

                _labelCounter++;
                var scanBreak = new BoundLabel($"__evt{sequence}_scan_br");
                var scanContinue = new BoundLabel($"__evt{sequence}_scan_ct");

                var loopBody = ImmutableArray.CreateBuilder<BoundStatement>();
                var elementEqualsHandler = BoundNodeFactory.Binary(syntax,
                    ElementOf(syntax, oldListLocal, BoundNodeFactory.Variable(syntax, scanIndexLocal)),
                    SSyntax.SyntaxKind.EqualsEqualsToken,
                    BoundNodeFactory.Variable(syntax, handlerLocal));
                var notYetFound = BoundNodeFactory.Binary(syntax,
                    BoundNodeFactory.Variable(syntax, matchIndexLocal),
                    SSyntax.SyntaxKind.EqualsEqualsToken,
                    BoundNodeFactory.Literal(syntax, -1));

                loopBody.Add(new BoundIfStatement(
                    syntax, notYetFound,
                    BoundNodeFactory.Block(syntax, new BoundIfStatement(
                        syntax, elementEqualsHandler,
                        new BoundExpressionStatement(syntax,
                            BoundNodeFactory.Assignment(syntax, matchIndexLocal, BoundNodeFactory.Variable(syntax, scanIndexLocal))),
                        elseStatement: null)),
                    elseStatement: null));
                loopBody.Add(BoundNodeFactory.Increment(syntax, BoundNodeFactory.Variable(syntax, scanIndexLocal)));

                var scanLoop = BoundNodeFactory.While(
                    syntax,
                    BoundNodeFactory.Binary(syntax,
                        BoundNodeFactory.Variable(syntax, scanIndexLocal),
                        SSyntax.SyntaxKind.LessToken,
                        LengthOf(syntax, oldListLocal)),
                    new BoundBlockStatement(syntax, loopBody.ToImmutable()),
                    scanBreak, scanContinue);

                scanStatements.Add(scanLoop);

                // 命中后重建
                var rebuildStatements = new List<BoundStatement>();

                var lengthIsOne = BoundNodeFactory.Binary(syntax,
                    LengthOf(syntax, oldListLocal),
                    SSyntax.SyntaxKind.EqualsEqualsToken,
                    BoundNodeFactory.Literal(syntax, 1));
                var storeNull = new BoundExpressionStatement(
                    syntax,
                    new BoundMemberAssignmentExpression(syntax, receiver, backingField, nullLiteral));

                var compactStatements = new List<BoundStatement>();
                var compactedListLocal = new LocalVariableSymbol($"__evt{sequence}_new", isReadOnly: false, handlerArray, null);
                var targetIndexLocal = new LocalVariableSymbol($"__evt{sequence}_k", isReadOnly: false, TypeSymbol.Int32, null);
                var sourceIndexLocal = new LocalVariableSymbol($"__evt{sequence}_m", isReadOnly: false, TypeSymbol.Int32, null);

                compactStatements.Add(new BoundVariableDeclaration(
                    syntax, compactedListLocal,
                    new BoundArrayCreationExpression(
                        syntax, handlerArray,
                        BoundNodeFactory.Binary(syntax,
                            LengthOf(syntax, oldListLocal),
                            SSyntax.SyntaxKind.MinusToken,
                            BoundNodeFactory.Literal(syntax, 1)),
                        ImmutableArray<BoundExpression>.Empty)));
                compactStatements.Add(new BoundVariableDeclaration(syntax, targetIndexLocal, BoundNodeFactory.Literal(syntax, 0)));
                compactStatements.Add(new BoundVariableDeclaration(syntax, sourceIndexLocal, BoundNodeFactory.Literal(syntax, 0)));

                _labelCounter++;
                var compactBreak = new BoundLabel($"__evt{sequence}_cp_br");
                var compactContinue = new BoundLabel($"__evt{sequence}_cp_ct");

                var copyBody = ImmutableArray.CreateBuilder<BoundStatement>();
                var sourceIsMatch = BoundNodeFactory.Binary(syntax,
                    BoundNodeFactory.Variable(syntax, sourceIndexLocal),
                    SSyntax.SyntaxKind.EqualsEqualsToken,
                    BoundNodeFactory.Variable(syntax, matchIndexLocal));
                var advanceTarget = ImmutableArray.Create<BoundStatement>(
                    new BoundExpressionStatement(syntax, new BoundElementAssignmentExpression(
                        syntax, signature,
                        ElementOf(syntax, compactedListLocal, BoundNodeFactory.Variable(syntax, targetIndexLocal)),
                        ElementOf(syntax, oldListLocal, BoundNodeFactory.Variable(syntax, sourceIndexLocal)))),
                    BoundNodeFactory.Increment(syntax, BoundNodeFactory.Variable(syntax, targetIndexLocal)));

                copyBody.Add(new BoundIfStatement(
                    syntax, sourceIsMatch,
                    BoundNodeFactory.Nop(syntax),
                    BoundNodeFactory.Block(syntax, advanceTarget.ToArray())));
                copyBody.Add(BoundNodeFactory.Increment(syntax, BoundNodeFactory.Variable(syntax, sourceIndexLocal)));

            var compactLoop = BoundNodeFactory.While(
                syntax,
                BoundNodeFactory.Binary(syntax,
                    BoundNodeFactory.Variable(syntax, sourceIndexLocal),
                    SSyntax.SyntaxKind.LessToken,
                    LengthOf(syntax, oldListLocal)),
                new BoundBlockStatement(syntax, copyBody.ToImmutable()),
                compactBreak, compactContinue);

                compactStatements.Add(compactLoop);
                compactStatements.Add(new BoundExpressionStatement(syntax, new BoundMemberAssignmentExpression(
                    syntax, receiver, backingField, BoundNodeFactory.Variable(syntax, compactedListLocal))));                var hitCondition = BoundNodeFactory.Binary(syntax,
                    BoundNodeFactory.Variable(syntax, matchIndexLocal),
                    SSyntax.SyntaxKind.GreaterOrEqualsToken,
                    BoundNodeFactory.Literal(syntax, 0));

                rebuildStatements.Add(new BoundIfStatement(
                    syntax, hitCondition,
                    BoundNodeFactory.Block(syntax,
                        new BoundIfStatement(syntax, lengthIsOne, storeNull, BoundNodeFactory.Block(syntax, compactStatements.ToArray()))),
                    elseStatement: null));

                scanStatements.AddRange(rebuildStatements);

                statements.Add(new BoundIfStatement(
                    syntax, notNullCondition,
                    BoundNodeFactory.Block(syntax, scanStatements.ToArray()),
                    elseStatement: null));
            }

            return BoundNodeFactory.Block(syntax, statements.ToArray());
        }

        /// <summary>
        /// 类内触发脱糖（6e-M22 C5+ 多播）：`e(args)` → 判空 + 快照遍历逐个调用。
        /// 实参只求值一次（提升隐藏局部，防遍历期间重复执行副作用）。
        /// </summary>
        private BoundStatement BindEventRaise(ExpressionStatementSyntax syntax, TextLocation errorLocation, string eventName, SSyntax.SeparatedSyntaxList<ExpressionSyntax> argumentSyntaxes)
        {
            var eventSymbol = _currentClass!.GetEvent(eventName)!;
            var signature = eventSymbol.HandlerType;

            if (signature.ParameterTypes.Length != argumentSyntaxes.Count)
            {
                _diagnostics.ReportWrongArgumentCount(errorLocation, eventName, signature.ParameterTypes.Length, argumentSyntaxes.Count);
                return new BoundBlockStatement(syntax, ImmutableArray<BoundStatement>.Empty);
            }

            _labelCounter++;
            var sequence = _labelCounter;
            var snapshotLocal = new LocalVariableSymbol($"__evt{sequence}_snap", isReadOnly: true, TypeSymbol.ArrayOf(signature), null);
            var indexLocal = new LocalVariableSymbol($"__evt{sequence}_i", isReadOnly: false, TypeSymbol.Int32, null);

            var backingField = _currentClass.GetField("_" + eventName)!;
            var thisReceiver = new BoundThisExpression(syntax.Expression, _currentClass);
            var fieldAccess = new BoundMemberAccessExpression(syntax, snapshotLocal.Type, thisReceiver, backingField.Name, backingField);

            var statements = ImmutableArray.CreateBuilder<BoundStatement>();
            statements.Add(new BoundVariableDeclaration(syntax, snapshotLocal, fieldAccess));

            // 实参求值提升
            var argumentLocals = new LocalVariableSymbol[argumentSyntaxes.Count];
            for (var i = 0; i < argumentSyntaxes.Count; i++)
            {
                argumentLocals[i] = new LocalVariableSymbol($"__evt{sequence}_a{i}", isReadOnly: true, signature.ParameterTypes[i], null);
                statements.Add(new BoundVariableDeclaration(
                    syntax, argumentLocals[i],
                    BindConversion(argumentSyntaxes[i], signature.ParameterTypes[i])));
            }

            // 快照遍历计数器（判空通过后才进入循环，声明置于其前保证线性执行序）
            statements.Add(new BoundVariableDeclaration(syntax, indexLocal, BoundNodeFactory.Literal(syntax, 0)));

            var notNullCondition = BoundNodeFactory.Binary(syntax,
                BoundNodeFactory.Variable(syntax, snapshotLocal),
                SSyntax.SyntaxKind.BangEqualsToken,
                new BoundLiteralExpression(syntax, null, TypeSymbol.Null));

            _labelCounter++;
            var breakLabel = new BoundLabel($"__evt{sequence}_raise_br");
            var continueLabel = new BoundLabel($"__evt{sequence}_raise_ct");

            var loopBody = ImmutableArray.CreateBuilder<BoundStatement>();

            var elementAccess = ElementOf(syntax, snapshotLocal, BoundNodeFactory.Variable(syntax, indexLocal));
            var invocationArguments = argumentLocals
                .Select(local => (BoundExpression)BoundNodeFactory.Variable(syntax, local))
                .ToImmutableArray();
            var invocation = new BoundInvocationExpression(syntax.Expression, elementAccess, invocationArguments, signature.ReturnType);
            loopBody.Add(new BoundExpressionStatement(syntax, invocation));
            loopBody.Add(BoundNodeFactory.Increment(syntax, BoundNodeFactory.Variable(syntax, indexLocal)));

            var raiseLoop = BoundNodeFactory.While(
                syntax,
                BoundNodeFactory.Binary(syntax,
                    BoundNodeFactory.Variable(syntax, indexLocal),
                    SSyntax.SyntaxKind.LessToken,
                    LengthOf(syntax, snapshotLocal)),
                new BoundBlockStatement(syntax, loopBody.ToImmutable()),
                breakLabel, continueLabel);

            statements.Add(new BoundIfStatement(syntax, notNullCondition, raiseLoop, elseStatement: null));

            return BoundNodeFactory.Block(syntax, statements.ToArray());
        }

        /// <summary>`__local.Length` 成员访问合成。</summary>
        private static BoundMemberAccessExpression LengthOf(SSyntax.SyntaxNode syntax, LocalVariableSymbol arrayLocal)
        {
            return new BoundMemberAccessExpression(syntax, TypeSymbol.Int32, BoundNodeFactory.Variable(syntax, arrayLocal), "Length");
        }

        /// <summary>`__local[index]` 元素访问合成。</summary>
        private static BoundElementAccessExpression ElementOf(SSyntax.SyntaxNode syntax, LocalVariableSymbol arrayLocal, BoundExpression index)
        {
            return new BoundElementAccessExpression(syntax, arrayLocal.Type.ElementType!, BoundNodeFactory.Variable(syntax, arrayLocal), index);
        }

        /// <summary>判断字段是否为事件合成后备字段（`_<eventName>`，多播存储）——禁止直接赋值/读取。</summary>
        private static bool IsEventBackingField(FieldSymbol field)
        {
            return field.Name.StartsWith("_", StringComparison.Ordinal) &&
                   field.ContainingClass != null &&
                   field.ContainingClass.GetEvent(field.Name[1..]) != null;
        }

        /// <summary>数组复制循环合成：`while i < source.Length { target[i] = source[i]; i++ }`（target 与 source 等长或更长）。</summary>
        private IEnumerable<BoundStatement> BuildElementCopyLoop(SSyntax.SyntaxNode syntax, LocalVariableSymbol targetLocal, LocalVariableSymbol indexLocal, LocalVariableSymbol sourceLocal, string labelSuffix)
        {
            _labelCounter++;
            var breakLabel = new BoundLabel($"{labelSuffix}{_labelCounter}");
            var continueLabel = new BoundLabel($"{labelSuffix}ct{_labelCounter}");

            var elementType = targetLocal.Type.ElementType!;
            var loopBody = ImmutableArray.Create<BoundStatement>(
                new BoundExpressionStatement(syntax, new BoundElementAssignmentExpression(
                    syntax, elementType,
                    ElementOf(syntax, targetLocal, BoundNodeFactory.Variable(syntax, indexLocal)),
                    ElementOf(syntax, sourceLocal, BoundNodeFactory.Variable(syntax, indexLocal)))),
                BoundNodeFactory.Increment(syntax, BoundNodeFactory.Variable(syntax, indexLocal)));

            yield return BoundNodeFactory.While(
                syntax,
                BoundNodeFactory.Binary(syntax,
                    BoundNodeFactory.Variable(syntax, indexLocal),
                    SSyntax.SyntaxKind.LessToken,
                    LengthOf(syntax, sourceLocal)),
                new BoundBlockStatement(syntax, loopBody),
                breakLabel, continueLabel);
        }

        /// <summary>
        /// delegate 声明绑定（6e-M22 D-A）：合成为 sealed class extends MulticastDelegate + Invoke 方法。
        /// 复用全部类机制（类型查找/is-as/继承链/三后端发射）。
        /// </summary>
        private void BindDelegateDeclaration(DelegateDeclarationSyntax syntax, NamedTypeSymbol classType, List<FunctionSymbol> classFunctions)
        {
            var returnType = syntax.ReturnType == null ? TypeSymbol.Void : BindTypeClause(syntax.ReturnType);
            if (returnType == null)
                return;

            if (ReportByRefDelegateParameters(syntax))
            {
                return;
            }

            var parameters = BindParameters(syntax.Parameters);

            var visibility = GetVisibility(syntax.Modifiers, Visibility.Public);
            var delegateName = syntax.Identifier.Text;

            // 合成 sealed class extends MulticastDelegate
            var delegateClass = new NamedTypeSymbol(delegateName, classType.Namespace, visibility, declaration: null)
            {
                BaseType = NamedTypeSymbol.SystemMulticastDelegate,
                IsSealed = true,
                TypeKind = TypeKind.Delegate,
            };

            // Invoke 方法签名匹配 delegate 声明
            var invokeParams = parameters.Select(p => new ParameterSymbol(p.Name, p.Type, p.Ordinal)).ToImmutableArray();
            var invokeFn = new FunctionSymbol("Invoke", invokeParams, returnType, null, containingClass: delegateClass, visibility: Visibility.Public)
            {
                IsStatic = false,
            };
            delegateClass.AddMethod(invokeFn);

            // 注册到类的事件/委托集合（类内 delegate）
            if (!_scope.TryDeclareClass(delegateClass))
            {
                _diagnostics.ReportError(syntax.Identifier.Location, $"delegate '{delegateName}' 已声明。");
            }
        }

        /// <summary>顶层（命名空间级）delegate 声明：同 BindDelegateDeclaration 但注册到全局作用域。</summary>
        internal void BindTopLevelDelegateDeclaration(DelegateDeclarationSyntax syntax, string ns)
        {
            var returnType = syntax.ReturnType == null ? TypeSymbol.Void : BindTypeClause(syntax.ReturnType);
            if (returnType == null)
                return;

            if (ReportByRefDelegateParameters(syntax))
            {
                return;
            }

            var parameters = BindParameters(syntax.Parameters);
            var visibility = GetVisibility(syntax.Modifiers, Visibility.Public);
            var delegateName = syntax.Identifier.Text;
            var fullName = ns.Length == 0 ? delegateName : ns + "." + delegateName;

            var delegateClass = new NamedTypeSymbol(delegateName, ns, visibility, declaration: null)
            {
                BaseType = NamedTypeSymbol.SystemMulticastDelegate,
                IsSealed = true,
                TypeKind = TypeKind.Delegate,
            };

            var invokeParams = parameters.Select(p => new ParameterSymbol(p.Name, p.Type, p.Ordinal)).ToImmutableArray();
            var invokeFn = new FunctionSymbol("Invoke", invokeParams, returnType, null, containingClass: delegateClass, visibility: Visibility.Public)
            {
                IsStatic = false,
            };
            delegateClass.AddMethod(invokeFn);

            // 命名空间级 delegate 直接注册进当前作用域（Namespace 属性承载限定）
            _scope.TryDeclareClass(delegateClass);
        }

        /// <summary>delegate 声明 byref 形参拦截（6e-M23 R3）：函数值签名无修饰符概念。有则报诊断并返回 true。</summary>
        private bool ReportByRefDelegateParameters(DelegateDeclarationSyntax syntax)
        {
            foreach (var parameter in syntax.Parameters)
            {
                if (parameter.Modifier != null)
                {
                    _diagnostics.ReportFunctionTypeByRefParameter(parameter.Modifier.Location);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 6e-G7 S6：种子收集辅助 binder 注册 cod 库的泛型定义类名——
        /// 使 BindGenericTypeNameForExpansion 能解析消费方站点的 `Box&lt;i32&gt;` 为实例化类型。
        /// </summary>
        /// <summary>注册本编译声明的泛型定义（源码优先于 cod：同名占位后 cod 注册静默跳过）。</summary>
        public void RegisterSourceGenericDefinitionsForSeed(BoundGlobalScope globalScope)
        {
            foreach (var classType in globalScope.Classes)
            {
                if (classType.IsGenericDefinition)
                {
                    _scope.TryDeclareClass(classType);
                }
            }
        }

        public void RegisterCodGenericDefinitionsForSeed(ImmutableArray<CoaProgram> libraries)
        {
            // 6e 跨库里程碑：cod 泛型定义注册为单态化种子解析候选（源码同名泛型定义已先注册占位，
            // TryDeclareClass 同名静默跳过——源码优先于 cod，源内联集合测试不被打扰；
            // cod 仅兜底源码未声明的泛型集合，跨库消费方 seed 经此发现 HashSet/Dictionary 等）。
            foreach (var library in libraries)
            {
                foreach (var genericDefinition in library.GenericDefinitions)
                {
                    _scope.TryDeclareClass(genericDefinition);
                }
            }
        }

        /// <summary>隐式默认构造：类所有部分均未声明构造时生成无参构造。</summary>
        private void DeclareImplicitConstructor(NamedTypeSymbol classType, List<FunctionSymbol> classFunctions, ClassDeclarationSyntax syntax)
        {
            if (classType.GetDeclaredMethod(classType.Name) == null)
            {                var ctor = new FunctionSymbol(classType.Name, ImmutableArray<ParameterSymbol>.Empty, TypeSymbol.Void, null, syntax: syntax, containingClass: classType, visibility: Visibility.Public) { IsConstructor = true };
                classType.AddMethod(ctor);
                classFunctions.Add(ctor);
            }
        }

        /// <summary>隐式静态构造（.cctor）：类含静态字段/静态自动属性初始化器时生成。</summary>
        private void DeclareImplicitStaticConstructor(NamedTypeSymbol classType, List<FunctionSymbol> classFunctions, ClassDeclarationSyntax syntax)
        {
            if (classType.GetDeclaredMethod(".cctor") != null)
            {
                return;
            }

            var hasStaticInitializers = CollectFieldInitializers(classType).Any(fi => fi.Field.IsStatic);
            if (!hasStaticInitializers)
            {
                return;
            }

            var cctor = new FunctionSymbol(".cctor", ImmutableArray<ParameterSymbol>.Empty, TypeSymbol.Void, null,
                syntax: syntax, containingClass: classType, visibility: Visibility.Private) { IsConstructor = true, IsStatic = true };
            classType.AddMethod(cctor);
            classFunctions.Add(cctor);
        }

        /// <summary>收集类的字段/自动属性初始化器（语法级，未绑定）。</summary>
        private static ImmutableArray<(FieldSymbol Field, ExpressionSyntax Initializer)> CollectFieldInitializers(NamedTypeSymbol classType)
        {
            var result = ImmutableArray.CreateBuilder<(FieldSymbol, ExpressionSyntax)>();
            if (classType.Declaration == null)
            {
                return result.ToImmutable();
            }

            foreach (var member in ((ClassDeclarationSyntax)classType.Declaration).Members)
            {
                if (member is ClassFieldDeclarationSyntax fieldDecl && fieldDecl.Initializer != null)
                {
                    var field = classType.GetDeclaredField(fieldDecl.Identifier.Text);
                    if (field != null)
                    {
                        result.Add((field, fieldDecl.Initializer));
                    }
                }
                else if (member is PropertyDeclarationSyntax propDecl && propDecl.Initializer != null && propDecl.IsAuto)
                {
                    var backing = classType.GetDeclaredField("_" + propDecl.Identifier.Text);
                    if (backing != null)
                    {
                        result.Add((backing, propDecl.Initializer));
                    }
                }
            }

            return result.ToImmutable();
        }

        /// <summary>绑定字段初始化器为赋值语句（静态或实例，取决于 isStatic）。</summary>
        private static ImmutableArray<BoundStatement> BindFieldInitializerStatements(CocoaBinder binder, NamedTypeSymbol classType, bool isStatic)
        {
            var result = ImmutableArray.CreateBuilder<BoundStatement>();
            foreach (var (field, initializer) in CollectFieldInitializers(classType))
            {
                if (field.IsStatic == isStatic)
                {
                    result.Add(BindFieldInitializer(binder, field, initializer));
                }
            }

            return result.ToImmutable();
        }

        /// <summary>合成字段初始化赋值：`this.field = init`（实例）/ `Class.field = init`（静态）。</summary>
        private static BoundStatement BindFieldInitializer(CocoaBinder binder, FieldSymbol field, ExpressionSyntax initializerSyntax)
        {
            var boundInit = binder.BindExpression(initializerSyntax);
            var converted = binder.BindConversion(initializerSyntax.Location, boundInit, field.Type);

            BoundExpression target = field.IsStatic
                ? new BoundStaticTypeExpression(initializerSyntax, field.ContainingClass!)
                : new BoundThisExpression(initializerSyntax, field.ContainingClass!);

            return new BoundExpressionStatement(initializerSyntax, new BoundMemberAssignmentExpression(initializerSyntax, target, field, converted));
        }

        /// <summary>创建接口符号（不可实例化、成员无实现）。</summary>
        private NamedTypeSymbol DeclareInterfaceSymbol(InterfaceDeclarationSyntax syntax, string @namespace)
        {
            var name = syntax.Identifier.Text;
            var visibility = GetVisibility(syntax.Modifiers, Visibility.Internal);

            if (visibility is Visibility.Private or Visibility.Protected)
            {
                _diagnostics.ReportError(syntax.Identifier.Location, $"接口 '{name}' 的可见性只能为 public 或 internal。");
            }

            var classType = new NamedTypeSymbol(name, @namespace, visibility, declaration: null)
            {
                TypeKind = TypeKind.Interface,
                IsAbstract = true,
            };

            // 泛型类型参数声明（6e-M20）：`interface IEnumerable<T>`（where 子句在阶段 3 绑定）
            classType.TypeParameters = BindClassTypeParameters(syntax.TypeParameters, classType, name);

            if (!_scope.TryDeclareClass(classType))
            {
                _diagnostics.ReportSymbolAlreadyDeclared(syntax.Identifier.Location, name);
            }

            return classType;
        }

        /// <summary>绑定接口声明：基接口列表 + 抽象成员（函数签名/属性访问器）。</summary>
        private void BindInterfaceDeclaration(InterfaceDeclarationSyntax syntax, NamedTypeSymbol interfaceType, List<FunctionSymbol> classFunctions)
        {
            var previousBindingClass = _bindingClass;
            _bindingClass = interfaceType;

            try
            {
                // where 约束（6e-M20；接口符号已全部声明）
                BindWhereClauses(syntax.WhereClauses, interfaceType.TypeParameters);

                // 基接口（仅允许接口；泛型基接口经实参实例化，6e-M20）
                foreach (var baseClause in syntax.BaseTypes)
                {
                    var baseType = BindBaseTypeClause(baseClause);

                    if (baseType == null)
                    {
                        continue;
                    }

                    if (!baseType.IsInterface)
                    {
                        _diagnostics.ReportError(baseClause.Location, $"接口 '{interfaceType.Name}' 只能继承接口，不能继承类 '{baseType.Name}'。");
                    }
                    else
                    {
                        interfaceType.AddBaseInterface(baseType);
                    }
                }

                // 成员：函数签名（抽象）+ 属性访问器（抽象）
                foreach (var member in syntax.Members)
                {
                    if (member is FunctionDeclarationSyntax methodDeclaration)
                    {
                        var visibility = GetVisibility(methodDeclaration.Modifiers, Visibility.Public);

                        // 泛型接口方法类型参数（6e-M20）先行：签名的 T 解析依赖此上下文
                        var previousInterfaceMethodTypeParameters = _declaringMethodTypeParameters;
                        _declaringMethodTypeParameters = BindFunctionTypeParameters(methodDeclaration.TypeParameters);

                        try
                        {
                            var parameters = BindParameters(methodDeclaration.Parameters);
                            var returnType = BindTypeClause(methodDeclaration.Type) ?? TypeSymbol.Void;

                            if (interfaceType.GetDeclaredMethod(methodDeclaration.Identifier.Text) == null)
                            {
                                var method = new FunctionSymbol(methodDeclaration.Identifier.Text, parameters, returnType, methodDeclaration, containingClass: interfaceType, visibility: visibility)
                                {
                                    IsAbstract = true,
                                    IsVirtual = true,
                                    TypeParameters = _declaringMethodTypeParameters,
                                };
                                BindWhereClauses(methodDeclaration.WhereClauses, method.TypeParameters);

                                interfaceType.AddMethod(method);
                                classFunctions.Add(method);
                            }
                            else
                            {
                                _diagnostics.ReportSymbolAlreadyDeclared(methodDeclaration.Identifier.Location, methodDeclaration.Identifier.Text);
                            }
                        }
                        finally
                        {
                            _declaringMethodTypeParameters = previousInterfaceMethodTypeParameters;
                        }
                    }
                    else if (member is PropertyDeclarationSyntax propertyDeclaration)
                    {
                        BindInterfacePropertyDeclaration(propertyDeclaration, interfaceType, classFunctions);
                    }
                }
            }
            finally
            {
                _bindingClass = previousBindingClass;
            }
        }

        /// <summary>接口属性：getter/setter 访问器（无实现、抽象）。</summary>
        private void BindInterfacePropertyDeclaration(PropertyDeclarationSyntax syntax, NamedTypeSymbol interfaceType, List<FunctionSymbol> classFunctions)
        {
            var propertyType = BindTypeClause(syntax.Type);
            var visibility = GetVisibility(syntax.Modifiers, Visibility.Public);

            // 索引器在类侧命名为 "Item"（见 BindPropertyDeclaration），接口侧须保持一致，
            // 否则 IList<T>.this[] 与 List<T>.this[] 因名称（"this" vs "Item"）不匹配，
            // 导致 CheckInterfaceImplementation 报"未实现属性 this"。
            var isIndexer = syntax.Identifier.Text == "this";
            var propertyName = isIndexer ? "Item" : syntax.Identifier.Text;

            // 索引器参数（this[index: i32]）：getter 接收；setter 额外接收 value。
            var indexParams = ImmutableArray<ParameterSymbol>.Empty;
            if (isIndexer)
            {
                indexParams = BindIndexerParameters(syntax.Parameters);
            }

            // 访问器可见性：独立计算 + 严格 C# 校验（CS0273 / 至多一个访问器带修饰符）
            ValidateAccessorVisibility(syntax, visibility);
            var getterVisibility = syntax.Getter != null ? GetVisibility(syntax.Getter.Modifiers, visibility) : visibility;
            var setterVisibility = syntax.Setter != null ? GetVisibility(syntax.Setter.Modifiers, visibility) : visibility;

            if (interfaceType.GetProperty(propertyName) != null)
            {
                _diagnostics.ReportSymbolAlreadyDeclared(syntax.Identifier.Location, propertyName);
                return;
            }

            FunctionSymbol? getter = null;
            if (syntax.Getter != null)
            {
                var getterParams = isIndexer ? indexParams : ImmutableArray<ParameterSymbol>.Empty;
                getter = new FunctionSymbol("get_" + propertyName, getterParams, propertyType, null,
                    syntax: syntax.Getter, containingClass: interfaceType, visibility: getterVisibility)
                {
                    IsAbstract = true,
                    IsVirtual = true,
                    IsPropertyAccessor = true,
                };
                interfaceType.AddMethod(getter);
                classFunctions.Add(getter);
            }

            FunctionSymbol? setter = null;
            if (syntax.Setter != null)
            {
                var valueParameter = new ParameterSymbol("value", propertyType, isIndexer ? indexParams.Length : 0);
                var setterParams = isIndexer ? indexParams.Add(valueParameter) : ImmutableArray.Create(valueParameter);
                setter = new FunctionSymbol("set_" + propertyName, setterParams, TypeSymbol.Void, null,
                    syntax: syntax.Setter, containingClass: interfaceType, visibility: setterVisibility)
                {
                    IsAbstract = true,
                    IsVirtual = true,
                    IsPropertyAccessor = true,
                };
                interfaceType.AddMethod(setter);
                classFunctions.Add(setter);
            }

            interfaceType.AddProperty(new PropertySymbol(propertyName, propertyType, interfaceType, getter, setter, visibility, isStatic: false, isIndexer: isIndexer));
        }

        /// <summary>接口实现完整性：类（含继承链）须实现其全部接口的每个成员（方法签名/属性访问器）。</summary>
        private void CheckInterfaceImplementation(NamedTypeSymbol classType)
        {
            foreach (var iface in classType.GetAllInterfaces())
            {
                foreach (var method in iface.Methods)
                {
                    if (FindImplementation(classType, method) == null)
                    {
                        _diagnostics.ReportError(((ClassDeclarationSyntax?)classType.Declaration)?.Identifier.Location ?? default, $"类 '{classType.Name}' 未实现接口 '{iface.Name}' 的方法 '{method.Name}'。");
                    }
                }

                foreach (var property in iface.Properties)
                {
                    var implementation = classType.GetProperty(property.Name);
                    if (implementation == null)
                    {
                        _diagnostics.ReportError(((ClassDeclarationSyntax?)classType.Declaration)?.Identifier.Location ?? default, $"类 '{classType.Name}' 未实现接口 '{iface.Name}' 的属性 '{property.Name}'。");
                        continue;
                    }

                    if (property.Getter != null && implementation.Getter == null)
                    {
                        _diagnostics.ReportError(((ClassDeclarationSyntax?)classType.Declaration)?.Identifier.Location ?? default, $"类 '{classType.Name}' 的属性 '{property.Name}' 缺少接口 '{iface.Name}' 要求的 getter。");
                    }

                    if (property.Setter != null && implementation.Setter == null)
                    {
                        _diagnostics.ReportError(((ClassDeclarationSyntax?)classType.Declaration)?.Identifier.Location ?? default, $"类 '{classType.Name}' 的属性 '{property.Name}' 缺少接口 '{iface.Name}' 要求的 setter。");
                    }
                }
            }
        }

        /// <summary>查找类（含继承链）中对接口方法的实现：名称 + 参数类型 + 返回类型匹配且 public。</summary>
        private static FunctionSymbol? FindImplementation(NamedTypeSymbol classType, FunctionSymbol interfaceMethod)
        {
            for (var current = classType; current != null; current = current.BaseType)
            {
                foreach (var method in current.GetDeclaredMethods(interfaceMethod.Name))
                {
                    if (method.Visibility != Visibility.Public)
                    {
                        continue;
                    }

                    if (method.Parameters.Length != interfaceMethod.Parameters.Length)
                    {
                        continue;
                    }

                    var parametersMatch = true;
                    for (var i = 0; i < method.Parameters.Length; i++)
                    {
                        if (!TypesMatchForInterfaceImplementation(method.Parameters[i].Type, interfaceMethod.Parameters[i].Type))
                        {
                            parametersMatch = false;
                            break;
                        }
                    }

                    if (!parametersMatch || !TypesMatchForInterfaceImplementation(method.ReturnType, interfaceMethod.ReturnType))
                    {
                        continue;
                    }

                    return method;
                }
            }

            return null;
        }

        /// <summary>
        /// 接口实现签名匹配（6e-M20）：泛型接口的成员签名携带接口自身的类型参数符号，
        /// 与实现类的类型参数符号必然引用不等——结构化递归比较，任一层为类型参数即视为通配。
        /// </summary>
        private static bool TypesMatchForInterfaceImplementation(TypeSymbol implementationType, TypeSymbol interfaceType)
        {
            if (ReferenceEquals(implementationType, interfaceType))
            {
                return true;
            }

            if (implementationType is TypeParameterSymbol || interfaceType is TypeParameterSymbol)
            {
                return true;
            }

            // 协变返回（6e-M20）：实现返回具体枚举器类、接口声明返回接口实例——
            // 按「实现类型的全部接口包含该接口实例（实参通配）」判定
            if (interfaceType is InstantiatedTypeSymbol requiredInterface &&
                requiredInterface.GenericDefinition.IsInterface &&
                implementationType is NamedTypeSymbol implementationClass)
            {
                foreach (var iface in implementationClass.GetAllInterfaces())
                {
                    if (iface is InstantiatedTypeSymbol implemented &&
                        ReferenceEquals(implemented.GenericDefinition, requiredInterface.GenericDefinition) &&
                        implemented.TypeArguments.Length == requiredInterface.TypeArguments.Length)
                    {
                        var argumentsMatch = true;
                        for (var i = 0; i < implemented.TypeArguments.Length; i++)
                        {
                            if (!TypesMatchForInterfaceImplementation(implemented.TypeArguments[i], requiredInterface.TypeArguments[i]))
                            {
                                argumentsMatch = false;
                                break;
                            }
                        }

                        if (argumentsMatch)
                        {
                            return true;
                        }
                    }
                }
            }

            // 嵌套泛型实参逐位递归（IEnumerator$T vs IEnumerator$T' 等）
            if (implementationType is InstantiatedTypeSymbol implInstantiated &&
                interfaceType is InstantiatedTypeSymbol ifaceInstantiated &&
                ReferenceEquals(implInstantiated.GenericDefinition, ifaceInstantiated.GenericDefinition) &&
                implInstantiated.TypeArguments.Length == ifaceInstantiated.TypeArguments.Length)
            {
                for (var i = 0; i < implInstantiated.TypeArguments.Length; i++)
                {
                    if (!TypesMatchForInterfaceImplementation(implInstantiated.TypeArguments[i], ifaceInstantiated.TypeArguments[i]))
                    {
                        return false;
                    }
                }

                return true;
            }

            // 数组元素递归
            if (implementationType is ArrayTypeSymbol && interfaceType is ArrayTypeSymbol)
            {
                return TypesMatchForInterfaceImplementation(implementationType.ElementType, interfaceType.ElementType);
            }

            return false;
        }

        /// <summary>自动属性合成体：getter → return _Name；setter → _Name = value。</summary>
        private BoundBlockStatement BindAutoPropertyBody(PropertyAccessorSyntax accessor, FunctionSymbol function)
        {
            var classType = function.ContainingClass!;
            var propName = function.Name.Substring(4); // get_X / set_X → X
            var field = classType.GetDeclaredField("_" + propName);
            if (field == null)
            {
                _diagnostics.ReportError(accessor.Keyword.Location, $"自动属性 '{propName}' 缺少后备字段。");
                return new BoundBlockStatement(accessor, ImmutableArray<BoundStatement>.Empty);
            }

            var thisExpression = new BoundThisExpression(accessor, classType);

            if (accessor.IsGet)
            {
                var memberAccess = new BoundMemberAccessExpression(accessor, field.Type, thisExpression, field.Name, field);
                return new BoundBlockStatement(accessor, ImmutableArray.Create<BoundStatement>(new BoundReturnStatement(accessor, memberAccess)));
            }

            var valueVariable = function.Parameters[0];
            var valueExpression = new BoundVariableExpression(accessor, valueVariable);
            var memberAssignment = new BoundMemberAssignmentExpression(accessor, thisExpression, field, valueExpression);
            return new BoundBlockStatement(accessor, ImmutableArray.Create<BoundStatement>(new BoundExpressionStatement(accessor, memberAssignment)));
        }

        private void BindPropertyDeclaration(PropertyDeclarationSyntax syntax, NamedTypeSymbol classType, List<FunctionSymbol> classFunctions)
        {
            var isIndexer = syntax.Identifier.Text == "this";
            var propertyType = BindTypeClause(syntax.Type);
            var visibility = GetVisibility(syntax.Modifiers, Visibility.Private);
            var isStatic = isIndexer ? false : syntax.Modifiers.Any(m => m.Kind == SSyntax.SyntaxKind.StaticKeyword);
            var isAuto = syntax.IsAuto;

            if (isIndexer && isAuto)
            {
                _diagnostics.ReportError(syntax.Getter?.Body?.Location ?? syntax.Location, "索引器不支持自动属性，必须提供 get/set 访问器主体。");
            }

            // 自动属性：合成后备字段 _Name（索引器禁用自动属性）
            if (isAuto && !isIndexer)
            {
                var backingField = new FieldSymbol("_" + syntax.Identifier.Text, propertyType, visibility, classType, isReadonly: false, isStatic: isStatic);
                classType.AddField(backingField);
            }

            // 访问器可见性：独立计算 + 严格 C# 校验（CS0273 / 至多一个访问器带修饰符）
            ValidateAccessorVisibility(syntax, visibility);
            var getterVisibility = syntax.Getter != null ? GetVisibility(syntax.Getter.Modifiers, visibility) : visibility;
            var setterVisibility = syntax.Setter != null ? GetVisibility(syntax.Setter.Modifiers, visibility) : visibility;

            // 索引器参数（this[a: T]）：getter 接收全部；setter 额外接收 value
            var indexParams = ImmutableArray<ParameterSymbol>.Empty;
            if (isIndexer)
            {
                indexParams = BindIndexerParameters(syntax.Parameters);
            }

            // facade 实例方法降级（隐藏首参 this + 强制静态）；索引器亦遵循
            var lower = !isStatic && classType.IsFacadeClass;

            // getter：get_Name / get_Item
            FunctionSymbol? getter = null;
            if (syntax.Getter != null)
            {
                var getterParams = isIndexer ? indexParams : ImmutableArray<ParameterSymbol>.Empty;
                if (lower)
                {
                    var thisParam = new ParameterSymbol("this", classType.FacadeThisType ?? classType, 0, isThis: true);
                    getterParams = new[] { thisParam }.Concat(getterParams.Select(p => new ParameterSymbol(p.Name, p.Type, p.Ordinal + 1))).ToImmutableArray();
                }

                getter = new FunctionSymbol(isIndexer ? "get_Item" : "get_" + syntax.Identifier.Text, getterParams, propertyType, null,
                    syntax: syntax.Getter, containingClass: classType, visibility: getterVisibility) { IsStatic = isStatic || lower, IsPropertyAccessor = true };
                classType.AddMethod(getter);
                classFunctions.Add(getter);
            }

            // setter：set_Name / set_Item（value 隐式参数）
            FunctionSymbol? setter = null;
            if (syntax.Setter != null)
            {
                // 6e 跨库里程碑：setter 克隆索引参数（独立 ParameterSymbol 实例）——否则与 getter 共享
                // 同一 `index` 参数，Registry `_varKeys` 冲突（get_Item 的参数键误落 set_Item 名下），
                // 读侧 body 变量解析错位（cod 泛型集合索引器求值 KeyNotFound）。
                var setterIndexParams = isIndexer
                    ? indexParams.Select(p => new ParameterSymbol(p.Name, p.Type, p.Ordinal)).ToImmutableArray()
                    : indexParams;
                var valueParameter = new ParameterSymbol("value", propertyType, isIndexer ? indexParams.Length : 0);
                var setterParams = isIndexer ? setterIndexParams.Add(valueParameter) : ImmutableArray.Create(valueParameter);
                if (lower)
                {
                    var thisParam = new ParameterSymbol("this", classType.FacadeThisType ?? classType, 0, isThis: true);
                    setterParams = new[] { thisParam }.Concat(setterParams.Select(p => new ParameterSymbol(p.Name, p.Type, p.Ordinal + 1))).ToImmutableArray();
                }

                setter = new FunctionSymbol(isIndexer ? "set_Item" : "set_" + syntax.Identifier.Text, setterParams, TypeSymbol.Void, null,
                    syntax: syntax.Setter, containingClass: classType, visibility: setterVisibility) { IsStatic = isStatic || lower, IsPropertyAccessor = true };
                classType.AddMethod(setter);
                classFunctions.Add(setter);
            }

            var propertyName = isIndexer ? "Item" : syntax.Identifier.Text;
            if (classType.GetDeclaredProperty(propertyName) == null)
            {
                var property = new PropertySymbol(propertyName, propertyType, classType, getter, setter, visibility, isStatic, isIndexer: isIndexer);
                if (getter != null) getter.ContainingProperty = property;
                if (setter != null) setter.ContainingProperty = property;
                classType.AddProperty(property);
            }
            else
            {
                _diagnostics.ReportSymbolAlreadyDeclared(syntax.Identifier.Location, propertyName);
            }
        }

        private ImmutableArray<ParameterSymbol> BindIndexerParameters(ImmutableArray<ParameterSyntax> parameters)
        {
            var builder = ImmutableArray.CreateBuilder<ParameterSymbol>();
            var ordinal = 0;
            foreach (var p in parameters)
            {
                var type = BindTypeClause(p.Type);
                builder.Add(new ParameterSymbol(p.Identifier.Text, type, ordinal));
                ordinal++;
            }

            return builder.ToImmutable();
        }

        private void CollectClasses(NamespaceDeclarationSyntax syntax, string parentNamespace, List<(ClassDeclarationSyntax Syntax, string Namespace)> allClasses)        {
            var ns = parentNamespace.Length == 0 ? syntax.Name : parentNamespace + "." + syntax.Name;

            foreach (var member in syntax.Members)
            {
                if (member is ClassDeclarationSyntax classDeclaration)
                {
                    allClasses.Add((classDeclaration, ns));
                }
                else if (member is NamespaceDeclarationSyntax nested)
                {
                    CollectClasses(nested, ns, allClasses);
                }
            }
        }

        private void CollectInterfaces(NamespaceDeclarationSyntax syntax, string parentNamespace, List<(InterfaceDeclarationSyntax Syntax, string Namespace)> allInterfaces)
        {
            var ns = parentNamespace.Length == 0 ? syntax.Name : parentNamespace + "." + syntax.Name;

            foreach (var member in syntax.Members)
            {
                if (member is InterfaceDeclarationSyntax interfaceDeclaration)
                {
                    allInterfaces.Add((interfaceDeclaration, ns));
                }
                else if (member is NamespaceDeclarationSyntax nested)
                {
                    CollectInterfaces(nested, ns, allInterfaces);
                }
            }
        }

        private void CollectEnums(NamespaceDeclarationSyntax syntax, string parentNamespace, List<(EnumDeclarationSyntax Syntax, string Namespace)> allEnums)
        {
            var ns = parentNamespace.Length == 0 ? syntax.Name : parentNamespace + "." + syntax.Name;

            foreach (var member in syntax.Members)
            {
                if (member is EnumDeclarationSyntax enumDeclaration)
                {
                    allEnums.Add((enumDeclaration, ns));
                }
                else if (member is NamespaceDeclarationSyntax nested)
                {
                    CollectEnums(nested, ns, allEnums);
                }
            }
        }

        private void CollectNamespaceFunctions(NamespaceDeclarationSyntax syntax, string parentNamespace, string? importedDll, List<(FunctionDeclarationSyntax Syntax, string Namespace, string? Dll)> functions)
        {
            var ns = parentNamespace.Length == 0 ? syntax.Name : parentNamespace + "." + syntax.Name;

            foreach (var member in syntax.Members)
            {
                if (member is FunctionDeclarationSyntax functionDeclaration)
                {
                    functions.Add((functionDeclaration, ns, importedDll));
                }
                else if (member is NamespaceDeclarationSyntax nested)
                {
                    CollectNamespaceFunctions(nested, ns, importedDll, functions);
                }
            }
        }

        /// <summary>递归收集命名空间内（含文件作用域 `namespace Foo;`）的 using 指令，供名称解析与 6e-M15 警告。</summary>
        private void CollectNamespaceUsings(NamespaceDeclarationSyntax syntax, List<string> usingNamespaces, List<UsingDirectiveSyntax> usingDirectives)
        {
            foreach (var member in syntax.Members)
            {
                if (member is UsingDirectiveSyntax usingDirective)
                {
                    CollectUsingDirective(usingDirective);
                    usingDirectives.Add(usingDirective);
                }
                else if (member is NamespaceDeclarationSyntax nested)
                {
                    CollectNamespaceUsings(nested, usingNamespaces, usingDirectives);
                }
            }
        }

        /// <summary>按形态收集 using：`using static <类>` → _usingStatics；`using <别名> = <名>` → _usingAliases；否则 → _usingNamespaces。</summary>
        private void CollectUsingDirective(UsingDirectiveSyntax directive)
        {
            if (directive.StaticKeyword != null)
            {
                _usingStatics.Add(directive.Name);
            }
            else if (directive.Alias.Length > 0)
            {
                _usingAliases[directive.Alias] = directive.Name;
            }
            else
            {
                _usingNamespaces.Add(directive.Name);
            }
        }

        /// <summary>using 未解析警告（6e-M15）：命名空间在程序声明 / 引用程序集 / .coa 库中都找不到时发警告（提示不绑定 .NET BCL）。</summary>
        private void ReportUnresolvedUsings(
            List<UsingDirectiveSyntax> usingDirectives,
            List<(ClassDeclarationSyntax Syntax, string Namespace)> allClasses,
            List<(InterfaceDeclarationSyntax Syntax, string Namespace)> allInterfaces,
            List<(EnumDeclarationSyntax Syntax, string Namespace)> allEnums,
            List<(FunctionDeclarationSyntax Syntax, string Namespace, string? Dll)> pendingFunctions,
            ImmutableArray<CoaProgram> codLibraries)
        {
            if (usingDirectives.Count == 0)
            {
                return;
            }

            var knownNamespaces = new HashSet<string>(StringComparer.Ordinal);
            void AddNamespacePrefixes(string ns)
            {
                while (ns.Length > 0)
                {
                    knownNamespaces.Add(ns);
                    var dot = ns.LastIndexOf('.');
                    if (dot < 0)
                    {
                        break;
                    }

                    ns = ns.Substring(0, dot);
                }
            }

            var knownClasses = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (syntax, ns) in allClasses)
            {
                knownClasses.Add(ns.Length == 0 ? syntax.Identifier.Text : ns + "." + syntax.Identifier.Text);
            }

            foreach (var (_, ns) in allClasses) AddNamespacePrefixes(ns);
            foreach (var (_, ns) in allInterfaces) AddNamespacePrefixes(ns);
            foreach (var (_, ns) in allEnums) AddNamespacePrefixes(ns);
            foreach (var (_, ns, _) in pendingFunctions) AddNamespacePrefixes(ns);
            foreach (var library in codLibraries)
            {
                foreach (var ns in library.Namespaces)
                {
                    AddNamespacePrefixes(ns);
                }

                foreach (var cls in library.Classes)
                {
                    knownClasses.Add(cls.FullName);
                }
            }

            var metadataReader = _references.Length == 0 ? null : new MetadataReader(_references.ToArray());
            foreach (var directive in usingDirectives)
            {
                var name = directive.Name;

                // `using static <类>`：目标必须是类（6e-M18）
                if (directive.StaticKeyword != null)
                {
                    if (!knownClasses.Contains(name))
                    {
                        _diagnostics.ReportUsingStaticTargetNotClass(directive.Location, name);
                    }

                    continue;
                }

                // `using <别名> = <名>`：目标须为命名空间或类（无论解析成功与否都终止于本分支）
                if (directive.Alias.Length > 0)
                {
                    if (!knownNamespaces.Contains(name) && !knownClasses.Contains(name))
                    {
                        _diagnostics.ReportUnresolvedUsing(directive.Location, name);
                    }

                    continue;
                }

                if (knownNamespaces.Contains(name))
                {
                    continue;
                }

                if (metadataReader != null && metadataReader.NamespaceExists(name))
                {
                    continue;
                }

                _diagnostics.ReportUnresolvedUsing(directive.Location, name);
            }
        }

        private FunctionSymbol BindClassMethodDeclaration(FunctionDeclarationSyntax syntax, NamedTypeSymbol classType, string? dllName = null, CharSet? blockCharSet = null)
        {
            // 泛型方法类型参数（6e-M20）先行落符号：签名 T 解析依赖此上下文
            var previousMethodTypeParameters = _declaringMethodTypeParameters;
            _declaringMethodTypeParameters = BindFunctionTypeParameters(syntax.TypeParameters);

            try
            {
                return BindClassMethodDeclarationCore(syntax, classType, dllName, blockCharSet);
            }
            finally
            {
                _declaringMethodTypeParameters = previousMethodTypeParameters;
            }
        }

        private FunctionSymbol BindClassMethodDeclarationCore(FunctionDeclarationSyntax syntax, NamedTypeSymbol classType, string? dllName = null, CharSet? blockCharSet = null)
        {
            var parameters = BindParameters(syntax.Parameters);
            var type = BindTypeClause(syntax.Type) ?? TypeSymbol.Void;
            var isSyscall = syntax.Modifiers.Any(m => m.Kind == SSyntax.SyntaxKind.SyscallKeyword);
            var isExtern = syntax.Modifiers.Any(m => m.Kind == SSyntax.SyntaxKind.CdeclKeyword || m.Kind == SSyntax.SyntaxKind.StdcallKeyword) ||
                           syntax.ExternMetadata != null;
            // syscall/extern 方法缺省 public（System.Runtime.Runtime.Print 供 System.Console 封装层调用；extern 供类外限定调用）
            var visibility = GetVisibility(syntax.Modifiers, (isSyscall || isExtern) ? Visibility.Public : Visibility.Private);
            var isStatic = syntax.Modifiers.Any(m => m.Kind == SSyntax.SyntaxKind.StaticKeyword);

            // 6e-M19 M2-b：facade 类实例方法编译期降级——隐藏首参 this（类型 = 承载类型）+ 强制静态，
            // 三后端按普通静态容器方法发射（对齐 C# 基元别名模型：Int32.ToString 等成员面载体）。
            // 声明参数 ordinal 整体 +1（真静态无 instance offset，this 占据 arg0）
            if (!isStatic && !isSyscall && !isExtern && classType.IsFacadeClass)
            {
                isStatic = true;
                var thisParameter = new ParameterSymbol("this", classType.FacadeThisType ?? classType, 0, isThis: true);
                var shifted = parameters.Select(p => new ParameterSymbol(p.Name, p.Type, p.Ordinal + 1)).ToArray();
                parameters = new[] { thisParameter }.Concat(shifted).ToImmutableArray();
            }

            var isVirtual = syntax.Modifiers.Any(m => m.Kind == SSyntax.SyntaxKind.VirtualKeyword);
            var isOverride = syntax.Modifiers.Any(m => m.Kind == SSyntax.SyntaxKind.OverrideKeyword);
            var isAbstract = syntax.Modifiers.Any(m => m.Kind == SSyntax.SyntaxKind.AbstractKeyword);
            var isSealed = syntax.Modifiers.Any(m => m.Kind == SSyntax.SyntaxKind.SealedKeyword);

            BuiltinKind? builtinKind = null;
            if (isSyscall)
            {
                var builtin = BuiltinFunctions.GetByName(syntax.Identifier.Text);
                if (builtin == null)
                {
                    _diagnostics.ReportSyscallFunctionUnknown(syntax.Identifier.Location, syntax.Identifier.Text);
                }
                else
                {
                    builtinKind = builtin.BuiltinKind;
                }

                if (syntax.Body != null)
                {
                    _diagnostics.ReportSyscallFunctionCannotHaveBody(syntax.Body.Location);
                }
            }

            // 6e-M17 Step 4：extern 校验 —— 在 import 块内（dllName != null）必须 static 且不能有 body；
            // 在 import 块外声明 extern（stdcall/cdecl 方法）→ 报错（须进 import 块）
            if (isExtern)
            {
                if (dllName == null)
                {
                    _diagnostics.ReportExternFunctionMustBeInImportBlock(syntax.Identifier.Location);
                }

                if (!isStatic)
                {
                    _diagnostics.ReportExternFunctionMustBeStatic(syntax.Identifier.Location);
                }

                if (syntax.Body != null)
                {
                    _diagnostics.ReportExternFunctionCannotHaveBody(syntax.Body.Location);
                }
            }

            // 6e-M17 Step 5：extern 元数据（entry 别名 + charset 编码）——函数级覆盖块级/缺省
            string? entryPoint = null;
            CharSet? charSet = blockCharSet;
            if (syntax.ExternMetadata != null)
            {
                foreach (var argument in syntax.ExternMetadata.Arguments)
                {
                    switch (argument.Key.Text)
                    {
                        case "entry":
                            entryPoint = argument.Value.Text;
                            break;
                        case "charset":
                            charSet = ParseCharSetValue(argument.Value);
                            break;
                        default:
                            _diagnostics.ReportError(argument.Key.Location, $"未知 extern 元数据键 '{argument.Key.Text}'（支持 entry / charset，未来 setlasterror/exactspelling 预留）。");
                            break;
                    }
                }
            }

            // syscall 方法隐含 static（System.Runtime.Runtime.Print 类名调用）
            var method = new FunctionSymbol(syntax.Identifier.Text, parameters, type, syntax, isExtern: isExtern, dllName: dllName, callingConvention: GetCallingConvention(syntax), containingClass: classType, visibility: visibility, builtinKind: builtinKind, entryPoint: entryPoint, charSet: charSet)
            {
                IsStatic = isStatic || isSyscall,
                IsVirtual = isVirtual,
                IsOverride = isOverride,
                IsAbstract = isAbstract,
                IsSealed = isSealed,
            };

            // 泛型方法类型参数（6e-M20）：`function Map<U>(…)` 类内声明 + where 子句落符号
            method.TypeParameters = _declaringMethodTypeParameters;
            BindWhereClauses(syntax.WhereClauses, method.TypeParameters);

            // override 语义（6e-M19 M2-c 升级）：沿基类链找同签名 virtual/abstract 方法——
            // 参数个数/类型逐一相同 + 返回类型相同（C# CS0115/CS1715 对齐，协变返回不做）
            if (isOverride)
            {
                if (!HasBaseClass(classType))
                {
                    _diagnostics.ReportError(syntax.Identifier.Location, $"方法 '{syntax.Identifier.Text}' 标记 override，但类型没有基类。");
                }
                else
                {
                    var candidates = classType.BaseType!.GetMethods(syntax.Identifier.Text)
                        .Where(m => (m.IsVirtual || m.IsAbstract) && !m.IsSealed)
                        .ToImmutableArray();

                    FunctionSymbol? baseMethod = null;
                    foreach (var candidate in candidates)
                    {
                        if (IsOverrideSignatureMatch(candidate, method))
                        {
                            baseMethod = candidate;
                            break;
                        }
                    }

                    if (baseMethod == null)
                    {
                        if (candidates.IsEmpty)
                        {
                            _diagnostics.ReportError(syntax.Identifier.Location, $"基类中找不到可重写的 virtual/abstract 方法 '{syntax.Identifier.Text}'。");
                        }
                        else
                        {
                            var nearest = classType.BaseType.GetMethod(syntax.Identifier.Text);
                            _diagnostics.ReportOverrideSignatureMismatch(syntax.Identifier.Location, syntax.Identifier.Text, nearest?.ReturnType ?? method.ReturnType, method.ReturnType);
                        }
                    }
                    else
                    {
                        method.OverriddenMethod = baseMethod;
                    }
                }
            }
            else if (isVirtual && classType.BaseType?.GetMethod(syntax.Identifier.Text)?.IsOverride == true)
            {
                // 隐藏基类 override 方法（允许，IL newslot）
            }

            return method;
        }

        private static bool IsOverrideSignatureMatch(FunctionSymbol baseMethod, FunctionSymbol overrideMethod)
        {
            if (baseMethod.ReturnType != overrideMethod.ReturnType)
            {
                return false;
            }

            if (baseMethod.Parameters.Length != overrideMethod.Parameters.Length)
            {
                return false;
            }

            for (var i = 0; i < baseMethod.Parameters.Length; i++)
            {
                if (baseMethod.Parameters[i].Type != overrideMethod.Parameters[i].Type ||
                    baseMethod.Parameters[i].IsOut != overrideMethod.Parameters[i].IsOut ||
                    baseMethod.Parameters[i].IsRef != overrideMethod.Parameters[i].IsRef)
                {
                    return false;
                }
            }

            return true;
        }

        private static CallingConvention GetCallingConvention(FunctionDeclarationSyntax syntax)
        {
            return syntax.Modifiers.Select(m => m.Kind)
                .FirstOrDefault(k => k == SSyntax.SyntaxKind.CdeclKeyword || k == SSyntax.SyntaxKind.StdcallKeyword) switch
            {
                SSyntax.SyntaxKind.CdeclKeyword => CallingConvention.Cdecl,
                SSyntax.SyntaxKind.StdcallKeyword => CallingConvention.StdCall,
                _ => CallingConvention.Winapi,
            };
        }

        /// <summary>
        /// 绑定 import 块（6e-M17 Step 4）：`import <dll> { static extern ... }`。
        /// 块内成员只允许 extern 函数声明，DLL 归属由块声明式绑定；外部使用类名限定调用（`Kernel32.GetTickCount()`）。
        /// </summary>
        private void BindImportBlock(ImportBlockSyntax importBlock, NamedTypeSymbol classType, List<FunctionSymbol> classFunctions)
        {
            // 块级 charset 键（6e-M17 Step 5）：块内函数缺省编码；缺省 unicode
            var blockCharSet = importBlock.CharsetKey != null
                ? ParseCharSetValue(importBlock.CharsetValue)
                : CharSet.Unicode;

            foreach (var blockMember in importBlock.Members)
            {
                if (blockMember is FunctionDeclarationSyntax functionDeclaration)
                {
                    // 块内只允许 extern 函数声明（stdcall/cdecl 或带 extern 元数据）；普通带体函数 → 诊断
                    var isExternDecl = functionDeclaration.Modifiers.Any(m => m.Kind == SSyntax.SyntaxKind.CdeclKeyword || m.Kind == SSyntax.SyntaxKind.StdcallKeyword) ||
                                       functionDeclaration.ExternMetadata != null;
                    if (!isExternDecl)
                    {
                        _diagnostics.ReportImportBlockOnlyExternFunctions(functionDeclaration.Identifier.Location);
                    }

                    var method = BindClassMethodDeclaration(functionDeclaration, classType, dllName: importBlock.DllName, blockCharSet: blockCharSet);

                    if (!classType.HasDeclaredMethodSignature(functionDeclaration.Identifier.Text, method))
                    {
                        classType.AddMethod(method);
                        classFunctions.Add(method);
                    }
                    else
                    {
                        _diagnostics.ReportSymbolAlreadyDeclared(functionDeclaration.Identifier.Location, functionDeclaration.Identifier.Text);
                    }
                }
                else
                {
                    _diagnostics.ReportImportBlockOnlyExternFunctions(blockMember.Location);
                }
            }
        }

        /// <summary>解析 charset 值文本（`ansi` / `unicode` / `auto`）；未知值 → unicode + 诊断。</summary>
        private CharSet ParseCharSetValue(SSyntax.SyntaxToken? valueToken)
        {
            if (valueToken == null)
            {
                return CharSet.Unicode;
            }

            switch (valueToken.Text)
            {
                case "ansi":
                    return CharSet.Ansi;
                case "auto":
                    return CharSet.Auto;
                case "unicode":
                    return CharSet.Unicode;
                default:
                    _diagnostics.ReportError(valueToken.Location, $"未知 charset 值 '{valueToken.Text}'（支持 ansi / unicode / auto）。");
                    return CharSet.Unicode;
            }
        }

        private BoundConstructorChainExpression? BindConstructorChain(ConstructorDeclarationSyntax syntax, NamedTypeSymbol classType)
        {
            var isBase = syntax.InitializerKeyword!.Kind == SSyntax.SyntaxKind.BaseKeyword;
            var targetClass = isBase ? classType.BaseType : classType;

            if (targetClass == null)
            {
                _diagnostics.ReportError(syntax.InitializerKeyword!.Location, "类型没有基类，不能调用 base(...)。");
                return null;
            }

            // 6e-M19 M2-c：显式链到内建 System.Object——仅 0 参（无 .ctor 符号，等价 CLR 隐式基构造 no-op）
            if (isBase && SystemObjectMembers.IsBuiltinSystemClass(targetClass))
            {
                if (syntax.InitializerArguments.Count == 0)
                {
                    return new BoundConstructorChainExpression(syntax, ConstructorInitializerKind.Base, constructor: null, ImmutableArray<BoundExpression>.Empty);
                }

                _diagnostics.ReportError(syntax.InitializerKeyword!.Location, "System.Object 没有带参数的构造函数。");
                return null;
            }

            var arguments = ImmutableArray.CreateBuilder<BoundExpression>();
            foreach (var argumentSyntax in syntax.InitializerArguments)
            {
                arguments.Add(BindExpression(argumentSyntax));
            }

            var ctorName = targetClass.Name;
            var candidates = targetClass.Methods.Where(m => m.Name == ctorName && (isBase || m != _function)).ToArray();

            FunctionSymbol? target = null;
            foreach (var candidate in candidates)
            {
                if (candidate.Parameters.Length != arguments.Count)
                {
                    continue;
                }

                var match = true;
                for (var i = 0; i < arguments.Count; i++)
                {
                    if (arguments[i].Type != candidate.Parameters[i].Type)
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    target = candidate;
                    break;
                }
            }

            if (target == null)
            {
                _diagnostics.ReportWrongArgumentCount(syntax.InitializerKeyword!.Location, (isBase ? "base" : "this"), candidates.Length > 0 ? candidates[0].Parameters.Length : 0, arguments.Count);
                return null;
            }

            for (var i = 0; i < arguments.Count; i++)
            {
                arguments[i] = BindConversion(arguments[i].Syntax.Location, arguments[i], target.Parameters[i].Type);
            }

            return new BoundConstructorChainExpression(syntax, isBase ? ConstructorInitializerKind.Base : ConstructorInitializerKind.This, target, arguments.ToImmutable());
        }

        private static BoundScope CreateParentScope(BoundGlobalScope? previous)
        {
            var stack = new Stack<BoundGlobalScope>();
            while (previous != null)
            {
                stack.Push(previous);
                previous = previous.Previous;
            }

            var parent = CreateRootScope();

            while (stack.Count > 0)
            {
                previous = stack.Pop();
                var scope = new BoundScope(parent);

                foreach (var f in previous.Functions)
                {
                    // class 方法/构造不进入全局函数作用域（用限定访问/this 解析），仅顶层函数可裸调用
                    if (f.ContainingClass != null)
                    {
                        continue;
                    }

                    scope.TryDeclareFunction(f);

                    // 命名空间函数同步进命名空间表（`Foo.Add(...)` 限定访问）
                    if (f.Namespace.Length > 0)
                    {
                        scope.TryDeclareNamespaceFunction(f.Namespace, f);
                    }
                }

                foreach (var e in previous.Enums)
                {
                    scope.TryDeclareEnum(e);
                }

                foreach (var c in previous.Classes)
                {
                    scope.TryDeclareClass(c);
                }

                foreach (var v in previous.Variables)
                {
                    scope.TryDeclareVariable(v);
                }

                parent = scope;
            }

            return parent;
        }

        private static BoundScope CreateRootScope()
        {
            var result = new BoundScope(null);

            // 6e-M17 Step 3：移除内置函数隐式注入（C# 式强隔离）——print/input/random 等
            // 不再全局裸可用；用户须 `using System.Console` 后 WriteLine/ReadLine，或
            // 经 System.Runtime（syscall 容器类，SystemLibrary 内建嵌入）显式调用。

            return result;
        }

        /// <summary>把 `.coa` 库的公共符号注入作用域（v1 无命名空间 → 裸注册；非空命名空间留扩展位，.coa v2 时启用）。</summary>
        private static void InjectCodSymbols(BoundScope scope, ImmutableArray<CoaProgram> codLibraries)
        {
            if (codLibraries.IsDefaultOrEmpty)
            {
                return;
            }

            foreach (var library in codLibraries)
            {
                foreach (var function in library.Functions)
                {
                    if (function.ContainingClass == null)
                    {
                        if (function.Namespace.Length == 0)
                        {
                            scope.TryDeclareFunction(function);
                        }
                        else
                        {
                            scope.TryDeclareNamespaceFunction(function.Namespace, function);
                        }
                    }
                }

                foreach (var enumType in library.Enums)
                {
                    scope.TryDeclareEnum(enumType);
                }

                // 容器类注入（6e-M17）：类壳注册进类型表；其方法已随 Functions 注入（ContainingClass 指向本类）
                foreach (var classType in library.Classes)
                {
                    // 6e 跨库里程碑：跳过泛型定义（gcls）——其入 scope 会泄漏进发射清单
                    // （IL/native 遇开放类型参数抛 Unexpected type K），且遮蔽源码同名集合类；
                    // 泛型定义经 GlobalNamespace 树 + Monomorphizer 种子消费。
                    if (classType.IsGenericDefinition)
                    {
                        continue;
                    }

                    // 6e-M19 M2-b：facade 标记不序列化，注入侧按全名映射表补齐
                    if (!classType.IsFacadeClass && FacadeTargets.ContainsKey(classType.FullName))
                    {
                        classType.IsFacadeClass = true;

                        // Phase 1-3 facade 合并：基元用类型表登记为 facade 全名（System.Int32 → TypeSymbol.Int32），
                        // 成员面经 FacadeCompanion 委托到本类（System.Core 缓存实例，进程内共享，赋值幂等）。
                        var target = FacadeTargets[classType.FullName];
                        classType.FacadeThisType = target;
                        if (target is NamedTypeSymbol primitiveTarget)
                        {
                            primitiveTarget.FacadeCompanion = classType;
                            scope.TryDeclareClass(primitiveTarget);
                            continue;
                        }
                    }

                    scope.TryDeclareClass(classType);
                }
            }
        }

        public DiagnosticBag Diagnostics => _diagnostics;

    }
}
