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
    /// 类/事件/委托/隐式构造/字段初始化声明绑定（自 Declarations 拆出）。
    /// </summary>
    public partial class CocoaBinder
    {
        private NamedTypeSymbol DeclareClassGroup(List<(ClassDeclarationSyntax Syntax, string Namespace)> parts)
        {
            var primary = parts[0];
            var name = primary.Syntax.Identifier.Text;
            var visibility = GetVisibility(primary.Syntax.Modifiers, Visibility.Internal);

            // `facade` 修饰符（6e-M20）：类须命中 FacadeTargets 才被认领为基元成员面载体；
            // struct 的 facade 为 6e-M26 Phase3 形态（映射 CO struct 到 BCL 值类型），不要求命中 FacadeTargets。
            var isStructDecl = primary.Syntax.ClassKeyword.Kind == CoreSyntax.SyntaxKind.StructKeyword;
            if (HasFacadeAttribute(primary.Syntax.Attributes) &&
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

                    if (!part.Syntax.Modifiers.Any(m => m.Kind == CoreSyntax.SyntaxKind.PartialKeyword))
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
            classType.IsAbstract = parts.Any(p => p.Syntax.Modifiers.Any(m => m.Kind == CoreSyntax.SyntaxKind.AbstractKeyword));
            classType.IsSealed = isStruct || parts.Any(p => p.Syntax.Modifiers.Any(m => m.Kind == CoreSyntax.SyntaxKind.SealedKeyword));
            classType.IsFileScoped = parts.Any(p => p.Syntax.Modifiers.Any(m => m.Kind == CoreSyntax.SyntaxKind.FileKeyword));
            DocumentationBackfill.BackfillDocumentation(classType, primary.Syntax, _diagnostics);

            // struct 约束（MVP）：常规 struct 不可有基类/接口、不可 abstract、不可 facade；
            // 但 `facade struct : <BCL值类型>` 是允许的特殊形态（6e-M26 Phase3：映射 CO struct 到 BCL）。
            if (isStruct)
            {
                var isFacadeStruct = parts.Any(p => HasFacadeAttribute(p.Syntax.Attributes));
                foreach (var (syntax, _) in parts)
                {
                    if (syntax.Modifiers.Any(m => m.Kind == CoreSyntax.SyntaxKind.AbstractKeyword))
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

                        if (HasFacadeAttribute(syntax.Attributes))
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

            foreach (var parameterSyntax in syntax.Parameters)
            {
                var parameterName = parameterSyntax.Identifier.Text ?? "";
                if (parameterName.Length == 0)
                {
                    continue;
                }

                if (parameterSyntax.VarianceKeyword != null)
                {
                    _diagnostics.ReportError(parameterSyntax.VarianceKeyword.Location, $"型变注解 '{VarianceKeywordText(parameterSyntax)}' 仅适用于 delegate/接口类型参数（类类型参数保持不变）。");
                    continue;
                }

                if (!seen.Add(parameterName))
                {
                    _diagnostics.ReportError(parameterSyntax.Identifier.Location, $"类型参数 '{parameterName}' 重复。");
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
                if (syntax.Parameters[i].Identifier.Text != expected[i].Name)
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
                if (member.Modifiers.Any(m => m.Kind == CoreSyntax.SyntaxKind.PartialKeyword))
                {
                    _diagnostics.ReportError(member.Location, "partial 只能用于类声明。");
                    continue;
                }

                if (classType.IsStatic &&
                    (member is ClassFieldDeclarationSyntax && !member.Modifiers.Any(m => m.Kind == CoreSyntax.SyntaxKind.StaticKeyword) ||
                     member is FunctionDeclarationSyntax && !member.Modifiers.Any(m => m.Kind == CoreSyntax.SyntaxKind.StaticKeyword)))
                {
                    _diagnostics.ReportError(member.Location, $"静态类 {classType.Name} 只能包含静态成员。");
                }

                if (member is ClassFieldDeclarationSyntax fieldDeclaration)
                {
                    var fieldType = BindTypeClause(fieldDeclaration.Type);
                    var fieldVisibility = GetVisibility(fieldDeclaration.Modifiers, Visibility.Private);
                    var fieldIsReadonly = fieldDeclaration.Modifiers.Any(m => m.Kind == CoreSyntax.SyntaxKind.ReadonlyKeyword);
                    var fieldIsStatic = fieldDeclaration.Modifiers.Any(m => m.Kind == CoreSyntax.SyntaxKind.StaticKeyword);
                    var fieldIsRequired = fieldDeclaration.Modifiers.Any(m => m.Kind == CoreSyntax.SyntaxKind.RequiredKeyword);

                    if (classType.GetDeclaredField(fieldDeclaration.Identifier.Text) == null)
                    {
                        var fieldSym = new FieldSymbol(fieldDeclaration.Identifier.Text, fieldType!, fieldVisibility, classType, isReadonly: fieldIsReadonly, isStatic: fieldIsStatic) { IsRequired = fieldIsRequired };
                        fieldSym.Attributes = BindAttributes(fieldDeclaration.Attributes, fieldDeclaration);
                        DocumentationBackfill.BackfillDocumentation(fieldSym, fieldDeclaration, _diagnostics);
                        classType.AddField(fieldSym);
                        if (fieldVisibility == Visibility.Public)
                        {
                            ValidateFacadeMemberAgainstBcl(classType, fieldDeclaration.Identifier.Text, FacadeMemberKind.Field, 0, fieldDeclaration.Identifier.Location);
                        }
                    }
                    else
                    {
                        _diagnostics.ReportSymbolAlreadyDeclared(fieldDeclaration.Identifier.Location, fieldDeclaration.Identifier.Text);
                    }
                }
                else if (member is ConstructorDeclarationSyntax constructorDeclaration)
                {
                    var isStatic = constructorDeclaration.Modifiers.Any(m => m.Kind == CoreSyntax.SyntaxKind.StaticKeyword);

                    if (isStatic)
                    {
                        // 静态构造函数（`static Foo()` / `static constructor()`）→ `.cctor` 符号
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
                        var ctor = new FunctionSymbol(classType.Name, parameters, TypeSymbol.Void, null, syntax: constructorDeclaration, containingClass: classType, visibility: ctorVisibility) { IsConstructor = true };
                        DocumentationBackfill.BackfillDocumentation(ctor, constructorDeclaration, _diagnostics);

                        if (!classType.HasDeclaredMethodSignature(classType.Name, ctor))
                        {
                            classType.AddMethod(ctor);
                            classFunctions.Add(ctor);
                            if (ctorVisibility == Visibility.Public)
                            {
                                ValidateFacadeMemberAgainstBcl(classType, classType.Name, FacadeMemberKind.Constructor, parameters.Length, constructorDeclaration.Location);
                            }
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
                        if (method.Visibility == Visibility.Public)
                        {
                            var publicArity = method.Parameters.Length - (method.Parameters.Length > 0 && method.Parameters[0].IsThisParameter ? 1 : 0);
                            ValidateFacadeMemberAgainstBcl(classType, methodDeclaration.Identifier.Text, FacadeMemberKind.Method, publicArity, methodDeclaration.Identifier.Location);
                        }
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
                    BindEventDeclaration(eventDeclaration, classType, classFunctions);
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
        private void BindEventDeclaration(EventDeclarationSyntax syntax, NamedTypeSymbol classType, List<FunctionSymbol> classFunctions)
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
            if (syntax.Modifiers.Any(m => m.Kind == CoreSyntax.SyntaxKind.StaticKeyword))
            {
                _diagnostics.ReportStaticEventNotSupported(syntax.Identifier.Location, eventName);
                return;
            }

            var visibility = GetVisibility(syntax.Modifiers, Visibility.Public);
            var eventSymbol = new EventSymbol(eventName, resolvedHandler, visibility, classType);
            DocumentationBackfill.BackfillDocumentation(eventSymbol, syntax, _diagnostics);
            classType.AddEvent(eventSymbol);

            // 多播存储（6e-M22 委托真实类型化 M4）：
            // 具名 delegate 处理器 → 委托类实例字段（add/remove：Combine/Remove）；fnty 处理器 → 函数值数组（既有脱糖）
            if (handlerType is NamedTypeSymbol { TypeKind: TypeKind.Delegate } delegateHandler)
            {
                classType.AddField(new FieldSymbol("_" + eventName, delegateHandler, visibility, classType));
            }
            else
            {
                classType.AddField(new FieldSymbol("_" + eventName, TypeSymbol.ArrayOf(resolvedHandler), visibility, classType));
            }

            // 访问器式事件：`event E: T { add {…} remove {…} }` —— 自定义 add/remove 方法对。
            // 订阅 `+=`/`-=` 分派到 add_Name/remove_Name（TryBindEventSubscription 拦截）；其体可写后备字段 `_<name>`（触发仍走它）。
            if (syntax.HasCustomAccessors)
            {
                BuildEventAccessor(syntax, classType, classFunctions, eventName, handlerType, syntax.AddBody, isAdd: true);
                BuildEventAccessor(syntax, classType, classFunctions, eventName, handlerType, syntax.RemoveBody, isAdd: false);
            }
        }

        /// <summary>合成事件访问器方法（add_Name / remove_Name）：value 隐式参数（类型 = 声明处理器类型，delegate 事件即 delegate 类）+ 用户体（经 PropertyAccessorSyntax 承载体，复用方法体绑定路径）。</summary>
        private void BuildEventAccessor(EventDeclarationSyntax syntax, NamedTypeSymbol classType, List<FunctionSymbol> classFunctions, string eventName, TypeSymbol handlerType, BlockStatementSyntax? body, bool isAdd)
        {
            if (body == null)
            {
                return;
            }

            var accessorName = (isAdd ? "add_" : "remove_") + eventName;
            var valueParameter = new ParameterSymbol("value", handlerType, 0);
            var keywordToken = new SyntaxToken(syntax.SyntaxTree, CoreSyntax.SyntaxKind.IdentifierToken, syntax.Span.Start, isAdd ? "add" : "remove", isAdd ? "add" : "remove", ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
            var accessorSyntax = new PropertyAccessorSyntax(syntax.SyntaxTree, ImmutableArray<SyntaxToken>.Empty, keywordToken, body, semicolonToken: null);
            var accessorMethod = new FunctionSymbol(accessorName, ImmutableArray.Create(valueParameter), TypeSymbol.Void,
                syntax: accessorSyntax, containingClass: classType, visibility: Visibility.Public)
            {
                IsPropertyAccessor = true,
            };
            classType.AddMethod(accessorMethod);
            classFunctions.Add(accessorMethod);
        }

        /// <summary>
        /// 事件订阅脱糖（6e-M22 C5+ 多播）：`e += f` / `e -= f` → 语句块。
        /// += 尾插（null → 单元素数组；否则复制扩容）；-= 按引用相等移除首个匹配（清空后回置 null）。
        /// 处理器表达式只求值一次（提升隐藏局部）。返回 null 表示目标不是事件（走通用绑定）。
        /// </summary>
        private BoundStatement? TryBindEventSubscription(AssignmentExpressionSyntax syntax)
        {
            var operatorKind = syntax.AssignmentToken.Kind;
            if (operatorKind != CoreSyntax.SyntaxKind.PlusEqualsToken && operatorKind != CoreSyntax.SyntaxKind.MinusEqualsToken)
            {
                return null;
            }

            // 目标形态：`obj.e` / `this.e` / 类内裸名 `e`
            string? eventName = null;
            NamedTypeSymbol? ownerClass = null;
            BoundExpression? receiver = null;

            if (syntax.Target.Kind == CoreSyntax.SyntaxKind.MemberAccessExpression)
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
            else if (syntax.Target.Kind == CoreSyntax.SyntaxKind.NameExpression && _currentClass != null)
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

            // 访问器式事件（自定义 add/remove）：`+=`/`-=` 分派到 add_Name/remove_Name 方法（C# 语义）。
            // 处理器绑定与字段式同一路径（裸函数名已是函数值、按签名归一）。
            var accessorAdd = ownerClass.GetMethod("add_" + eventName);
            if (accessorAdd != null)
            {
                return BuildAccessorEventSubscription(syntax, operatorKind, receiver, ownerClass, eventSymbol, accessorAdd);
            }

            var signature = eventSymbol.HandlerType;
            var backingField = ownerClass.GetField("_" + eventName)!;
            var handlerArray = TypeSymbol.ArrayOf(signature);

            // 6e-M22 委托真实类型化 M4：事件迁移——后备字段为具名 delegate 类时，
            // 订阅/退订走 Delegate.Combine/Remove（`_<e> = Combine(_<e>, h)` / `Remove`），触发走 `Invoke`。
            if (backingField.Type is NamedTypeSymbol { TypeKind: TypeKind.Delegate } delegateBacking)
            {
                return BuildDelegateEventSubscription(syntax, operatorKind, receiver, backingField, delegateBacking);
            }

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

            var nullLiteral = new BoundLiteralExpression(syntax, null!, TypeSymbol.Null);

            if (operatorKind == CoreSyntax.SyntaxKind.PlusEqualsToken)
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
                    CoreSyntax.SyntaxKind.EqualsEqualsToken,
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
                    CoreSyntax.SyntaxKind.BangEqualsToken,
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
                    CoreSyntax.SyntaxKind.EqualsEqualsToken,
                    BoundNodeFactory.Variable(syntax, handlerLocal));
                var notYetFound = BoundNodeFactory.Binary(syntax,
                    BoundNodeFactory.Variable(syntax, matchIndexLocal),
                    CoreSyntax.SyntaxKind.EqualsEqualsToken,
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
                        CoreSyntax.SyntaxKind.LessToken,
                        LengthOf(syntax, oldListLocal)),
                    new BoundBlockStatement(syntax, loopBody.ToImmutable()),
                    scanBreak, scanContinue);

                scanStatements.Add(scanLoop);

                // 命中后重建
                var rebuildStatements = new List<BoundStatement>();

                var lengthIsOne = BoundNodeFactory.Binary(syntax,
                    LengthOf(syntax, oldListLocal),
                    CoreSyntax.SyntaxKind.EqualsEqualsToken,
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
                            CoreSyntax.SyntaxKind.MinusToken,
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
                    CoreSyntax.SyntaxKind.EqualsEqualsToken,
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
                    CoreSyntax.SyntaxKind.LessToken,
                    LengthOf(syntax, oldListLocal)),
                new BoundBlockStatement(syntax, copyBody.ToImmutable()),
                compactBreak, compactContinue);

                compactStatements.Add(compactLoop);
                compactStatements.Add(new BoundExpressionStatement(syntax, new BoundMemberAssignmentExpression(
                    syntax, receiver, backingField, BoundNodeFactory.Variable(syntax, compactedListLocal))));                var hitCondition = BoundNodeFactory.Binary(syntax,
                    BoundNodeFactory.Variable(syntax, matchIndexLocal),
                    CoreSyntax.SyntaxKind.GreaterOrEqualsToken,
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
        /// 访问器式事件订阅：`e += f` / `e -= f` → `this.add_E(f)` / `this.remove_E(f)`（C# 语义——
        /// 自定义 add/remove 体控制订阅行为）。处理器按事件签名归一（方法组/lambda → delegate/fnty）。
        /// </summary>
        private BoundStatement BuildAccessorEventSubscription(AssignmentExpressionSyntax syntax, CoreSyntax.SyntaxKind operatorKind, BoundExpression receiver, NamedTypeSymbol ownerClass, EventSymbol eventSymbol, FunctionSymbol addMethod)
        {
            var isAdd = operatorKind == CoreSyntax.SyntaxKind.PlusEqualsToken;
            var targetMethod = isAdd ? addMethod : ownerClass.GetMethod("remove_" + eventSymbol.Name)!;
            var handlerParamType = targetMethod.Parameters[0].Type;

            var boundHandler = BindConversion(syntax.Expression, handlerParamType);
            if (boundHandler is BoundFunctionValueExpression && boundHandler.Type != handlerParamType)
            {
                // 方法组/lambda → 访问器 value 类型（delegate 类）：包装 BoundConversion（IL newobj Handler::.ctor）
                boundHandler = new BoundConversionExpression(syntax, handlerParamType, boundHandler);
            }
            else if (boundHandler.Type != handlerParamType && boundHandler.Type != TypeSymbol.Error)
            {
                boundHandler = BindConversion(boundHandler.Syntax.Location, boundHandler, handlerParamType);
            }

            var arguments = ImmutableArray.Create(boundHandler);
            return new BoundExpressionStatement(syntax,
                new BoundMemberCallExpression(syntax, receiver, targetMethod.Name, arguments, TypeSymbol.Void, targetMethod));
        }

        /// <summary>6e-M22 委托真实类型化 M4：事件订阅/退订——后备字段为具名 delegate 类时走
        /// `_&lt;e&gt; = Combine(_&lt;e&gt;, h)` / `Remove(_&lt;e&gt;, h)`（+= / -= 绑定层合成 delegate 二元运算，三后端经委托管道）。</summary>
        private BoundStatement BuildDelegateEventSubscription(AssignmentExpressionSyntax syntax, CoreSyntax.SyntaxKind operatorKind, BoundExpression receiver, FieldSymbol backingField, NamedTypeSymbol delegateBacking)
        {
            _labelCounter++;
            var sequence = _labelCounter;
            var handlerLocal = new LocalVariableSymbol($"__evt{sequence}_h", isReadOnly: true, delegateBacking, null);

            var boundHandler = BindConversion(syntax.Expression, delegateBacking);
            if (boundHandler is BoundFunctionValueExpression && boundHandler.Type != delegateBacking)
            {
                // 方法组/lambda → 具名 delegate：包装 BoundConversion（IL newobj Handler::.ctor）
                boundHandler = new BoundConversionExpression(syntax, delegateBacking, boundHandler);
            }
            else if (boundHandler.Type != delegateBacking && boundHandler.Type != TypeSymbol.Error)
            {
                boundHandler = BindConversion(boundHandler.Syntax.Location, boundHandler, delegateBacking);
            }

            var statements = ImmutableArray.CreateBuilder<BoundStatement>();
            statements.Add(new BoundVariableDeclaration(syntax, handlerLocal, boundHandler));

            var fieldAccess = new BoundMemberAccessExpression(syntax, delegateBacking, receiver, backingField.Name, backingField);
            var nullLiteral = new BoundLiteralExpression(syntax, null!, TypeSymbol.Null);

            if (operatorKind == CoreSyntax.SyntaxKind.PlusEqualsToken)
            {
                // if _e == null { _e = h } else { _e = _e + h }
                var isNullCondition = BoundNodeFactory.Binary(syntax, fieldAccess, CoreSyntax.SyntaxKind.EqualsEqualsToken, nullLiteral);
                var storeHandler = new BoundExpressionStatement(syntax,
                    new BoundMemberAssignmentExpression(syntax, receiver, backingField, BoundNodeFactory.Variable(syntax, handlerLocal)));
                var combined = BoundNodeFactory.Binary(syntax, fieldAccess, CoreSyntax.SyntaxKind.PlusToken,
                    BoundNodeFactory.Variable(syntax, handlerLocal));
                var storeCombine = new BoundExpressionStatement(syntax,
                    new BoundMemberAssignmentExpression(syntax, receiver, backingField, combined));
                statements.Add(new BoundIfStatement(syntax, isNullCondition, storeHandler, storeCombine));
            }
            else
            {
                // if _e != null { _e = _e - h }
                var notNullCondition = BoundNodeFactory.Binary(syntax, fieldAccess, CoreSyntax.SyntaxKind.BangEqualsToken, nullLiteral);
                var removed = BoundNodeFactory.Binary(syntax, fieldAccess, CoreSyntax.SyntaxKind.MinusToken,
                    BoundNodeFactory.Variable(syntax, handlerLocal));
                var storeRemove = new BoundExpressionStatement(syntax,
                    new BoundMemberAssignmentExpression(syntax, receiver, backingField, removed));
                statements.Add(new BoundIfStatement(syntax, notNullCondition, storeRemove, elseStatement: null));
            }

            return BoundNodeFactory.Block(syntax, statements.ToArray());
        }

        /// <summary>
        /// 类内触发脱糖（6e-M22 C5+ 多播）：`e(args)` → 判空 + 快照遍历逐个调用。
        /// 实参只求值一次（提升隐藏局部，防遍历期间重复执行副作用）。
        /// </summary>
        private BoundStatement BindEventRaise(ExpressionStatementSyntax syntax, TextLocation errorLocation, string eventName, CoreSyntax.SeparatedSyntaxList<ExpressionSyntax> argumentSyntaxes)
        {
            var eventSymbol = _currentClass!.GetEvent(eventName)!;
            var signature = eventSymbol.HandlerType;

            if (signature.ParameterTypes.Length != argumentSyntaxes.Count)
            {
                _diagnostics.ReportWrongArgumentCount(errorLocation, eventName, signature.ParameterTypes.Length, argumentSyntaxes.Count);
                return new BoundBlockStatement(syntax, ImmutableArray<BoundStatement>.Empty);
            }

            var backingField = _currentClass.GetField("_" + eventName)!;

            // 6e-M22 委托真实类型化 M4：后备字段为具名 delegate 类 → 触发 = 判空 + `_e(args)`（Invoke 快照遍历）
            if (backingField.Type is NamedTypeSymbol { TypeKind: TypeKind.Delegate })
            {
                var dlgReceiver = new BoundThisExpression(syntax.Expression, _currentClass);
                var dlgFieldAccess = new BoundMemberAccessExpression(syntax, backingField.Type, dlgReceiver, backingField.Name, backingField);
                var dlgStatements = ImmutableArray.CreateBuilder<BoundStatement>();

                var dlgArgumentLocals = new LocalVariableSymbol[argumentSyntaxes.Count];
                for (var i = 0; i < argumentSyntaxes.Count; i++)
                {
                    dlgArgumentLocals[i] = new LocalVariableSymbol($"__evt{_labelCounter}_a{i}", isReadOnly: true, signature.ParameterTypes[i], null);
                    dlgStatements.Add(new BoundVariableDeclaration(
                        syntax, dlgArgumentLocals[i],
                        BindConversion(argumentSyntaxes[i], signature.ParameterTypes[i])));
                }

                _labelCounter++;
                var dlgNotNull = BoundNodeFactory.Binary(syntax, dlgFieldAccess,
                    CoreSyntax.SyntaxKind.BangEqualsToken,
                    new BoundLiteralExpression(syntax, null!, TypeSymbol.Null));
                var dlgInvocationArguments = dlgArgumentLocals
                    .Select(local => (BoundExpression)BoundNodeFactory.Variable(syntax, local))
                    .ToImmutableArray();
                var dlgInvocation = new BoundInvocationExpression(syntax.Expression, dlgFieldAccess, dlgInvocationArguments, signature.ReturnType);
                dlgStatements.Add(new BoundIfStatement(syntax, dlgNotNull,
                    new BoundExpressionStatement(syntax, dlgInvocation), elseStatement: null));

                return BoundNodeFactory.Block(syntax, dlgStatements.ToArray());
            }

            _labelCounter++;
            var sequence = _labelCounter;
            var snapshotLocal = new LocalVariableSymbol($"__evt{sequence}_snap", isReadOnly: true, TypeSymbol.ArrayOf(signature), null);
            var indexLocal = new LocalVariableSymbol($"__evt{sequence}_i", isReadOnly: false, TypeSymbol.Int32, null);
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

            statements.Add(new BoundVariableDeclaration(syntax, indexLocal, BoundNodeFactory.Literal(syntax, 0)));

            var notNullCondition = BoundNodeFactory.Binary(syntax,
                BoundNodeFactory.Variable(syntax, snapshotLocal),
                CoreSyntax.SyntaxKind.BangEqualsToken,
                new BoundLiteralExpression(syntax, null!, TypeSymbol.Null));

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
                    CoreSyntax.SyntaxKind.LessToken,
                    LengthOf(syntax, snapshotLocal)),
                new BoundBlockStatement(syntax, loopBody.ToImmutable()),
                breakLabel, continueLabel);

            statements.Add(new BoundIfStatement(syntax, notNullCondition, raiseLoop, elseStatement: null));

            return BoundNodeFactory.Block(syntax, statements.ToArray());
        }

        /// <summary>`__local.Length` 成员访问合成。</summary>
        private static BoundMemberAccessExpression LengthOf(CoreSyntax.SyntaxNode syntax, LocalVariableSymbol arrayLocal)
        {
            return new BoundMemberAccessExpression(syntax, TypeSymbol.Int32, BoundNodeFactory.Variable(syntax, arrayLocal), "Length");
        }

        /// <summary>`__local[index]` 元素访问合成。</summary>
        private static BoundElementAccessExpression ElementOf(CoreSyntax.SyntaxNode syntax, LocalVariableSymbol arrayLocal, BoundExpression index)
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

        /// <summary>当前函数是否为事件 X 的自定义访问器（add_X / remove_X，PropertyAccessor 承载体）——
        /// 访问器体内允许写事件后备字段 `_X`（自定义访问器管理存储）。</summary>
        private bool IsInEventAccessor(FieldSymbol field)
        {
            if (_function == null || !_function.IsPropertyAccessor || _function.IsStatic)
            {
                return false;
            }

            var eventName = field.Name.Length > 1 ? field.Name[1..] : "";
            return _function.Name == "add_" + eventName || _function.Name == "remove_" + eventName;
        }

        /// <summary>数组复制循环合成：`while i < source.Length { target[i] = source[i]; i++ }`（target 与 source 等长或更长）。</summary>
        private IEnumerable<BoundStatement> BuildElementCopyLoop(CoreSyntax.SyntaxNode syntax, LocalVariableSymbol targetLocal, LocalVariableSymbol indexLocal, LocalVariableSymbol sourceLocal, string labelSuffix)
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
                    CoreSyntax.SyntaxKind.LessToken,
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
            if (ReportByRefDelegateParameters(syntax))
            {
                return;
            }

            var visibility = GetVisibility(syntax.Modifiers, Visibility.Public);
            var delegateName = syntax.Identifier.Text;

            // 合成 sealed class extends MulticastDelegate
            var delegateClass = new NamedTypeSymbol(delegateName, classType.Namespace, visibility, declaration: null)
            {
                BaseType = NamedTypeSymbol.SystemMulticastDelegate,
                IsSealed = true,
                TypeKind = TypeKind.Delegate,
            };
            DocumentationBackfill.BackfillDocumentation(delegateClass, syntax, _diagnostics);
            delegateClass.TypeParameters = BindDelegateTypeParameters(syntax.TypeParameters, delegateClass);

            // 6e-M22 delegate 真实类型化：签名绑定期间 delegate 类为类型参数查找语境（外层宿主不遮蔽）
            var previousBindingClass = _bindingClass;
            _bindingClass = delegateClass;
            try
            {
                var returnType = syntax.ReturnType == null ? TypeSymbol.Void : BindTypeClause(syntax.ReturnType);
                if (returnType == null)
                {
                    return;
                }

                var parameters = BindParameters(syntax.Parameters);
                CheckDelegateVariancePositions(delegateClass.TypeParameters, returnType, parameters, syntax);

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
            finally
            {
                _bindingClass = previousBindingClass;
            }
        }

        /// <summary>顶层（命名空间级）delegate 声明：同 BindDelegateDeclaration 但注册到全局作用域。</summary>
        internal void BindTopLevelDelegateDeclaration(DelegateDeclarationSyntax syntax, string ns)
        {
            if (ReportByRefDelegateParameters(syntax))
            {
                return;
            }

            var visibility = GetVisibility(syntax.Modifiers, Visibility.Public);
            var delegateName = syntax.Identifier.Text;

            var delegateClass = new NamedTypeSymbol(delegateName, ns, visibility, declaration: null)
            {
                BaseType = NamedTypeSymbol.SystemMulticastDelegate,
                IsSealed = true,
                TypeKind = TypeKind.Delegate,
            };
            DocumentationBackfill.BackfillDocumentation(delegateClass, syntax, _diagnostics);
            delegateClass.TypeParameters = BindDelegateTypeParameters(syntax.TypeParameters, delegateClass);

            // 6e-M22 delegate 真实类型化：签名绑定期间 delegate 类为类型参数查找语境
            var previousBindingClass = _bindingClass;
            _bindingClass = delegateClass;
            try
            {
                var returnType = syntax.ReturnType == null ? TypeSymbol.Void : BindTypeClause(syntax.ReturnType);
                if (returnType == null)
                {
                    return;
                }

                var parameters = BindParameters(syntax.Parameters);
                CheckDelegateVariancePositions(delegateClass.TypeParameters, returnType, parameters, syntax);

                var invokeParams = parameters.Select(p => new ParameterSymbol(p.Name, p.Type, p.Ordinal)).ToImmutableArray();
                var invokeFn = new FunctionSymbol("Invoke", invokeParams, returnType, null, containingClass: delegateClass, visibility: Visibility.Public)
                {
                    IsStatic = false,
                };
                delegateClass.AddMethod(invokeFn);

                // 命名空间级 delegate 直接注册进当前作用域（Namespace 属性承载限定）
                _scope.TryDeclareClass(delegateClass);
            }
            finally
            {
                _bindingClass = previousBindingClass;
            }
        }

        /// <summary>delegate 类型参数绑定（6e-M22 真实类型化）：建 TypeParameterSymbol 列表（含型变注解；
        /// 外层类型参数先入集禁遮蔽/重名；与 delegate 名冲突诊断；类参数同规格）。</summary>
        private ImmutableArray<TypeParameterSymbol> BindDelegateTypeParameters(TypeParameterListSyntax? syntax, NamedTypeSymbol delegateClass)
        {
            if (syntax == null)
            {
                return ImmutableArray<TypeParameterSymbol>.Empty;
            }

            var parameters = ImmutableArray.CreateBuilder<TypeParameterSymbol>();
            var seen = new HashSet<string>();

            foreach (var outer in _currentClass?.TypeParameters ?? ImmutableArray<TypeParameterSymbol>.Empty)
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

                if (!seen.Add(parameterName))
                {
                    _diagnostics.ReportError(parameterSyntax.Identifier.Location, $"类型参数 '{parameterName}' 重复或与外层类型参数同名。");
                    continue;
                }

                parameters.Add(new TypeParameterSymbol(parameterName, parameters.Count, delegateClass)
                {
                    Variance = VarianceOf(parameterSyntax),
                });
            }

            if (parameters.Any(p => p.Name == delegateClass.Name))
            {
                _diagnostics.ReportError(syntax.Location, $"类型参数不能与 delegate '{delegateClass.Name}' 同名。");
            }

            return parameters.ToImmutable();
        }

        private static VarianceKind VarianceOf(TypeParameterSyntax syntax)
        {
            if (syntax.VarianceKeyword == null)
            {
                return VarianceKind.Invariant;
            }

            return syntax.VarianceKeyword.Kind == CoreSyntax.SyntaxKind.InKeyword ? VarianceKind.In : VarianceKind.Out;
        }

        private static string VarianceKeywordText(TypeParameterSyntax syntax)
        {
            return syntax.VarianceKeyword?.Kind == CoreSyntax.SyntaxKind.InKeyword ? "in" : "out";
        }

        /// <summary>delegate 型变安全位诊断（6e-M22 真实类型化，对齐 C# CS1961/CS1962）：
        /// `in` 仅可出现在逆变（入）位、`out` 仅可出现在协变（出）位。</summary>
        private void CheckDelegateVariancePositions(ImmutableArray<TypeParameterSymbol> typeParameters, TypeSymbol returnType, ImmutableArray<ParameterSymbol> parameters, DelegateDeclarationSyntax syntax)
        {
            foreach (var typeParameter in typeParameters)
            {
                if (typeParameter.Variance == VarianceKind.In && TypeContainsParameter(returnType, typeParameter))
                {
                    _diagnostics.ReportError(syntax.Identifier.Location, $"in 逆变类型参数 '{typeParameter.Name}' 出现在协变（返回）位置，禁止（对齐 CS1962）。");
                }

                if (typeParameter.Variance == VarianceKind.Out)
                {
                    foreach (var parameter in parameters)
                    {
                        if (TypeContainsParameter(parameter.Type, typeParameter))
                        {
                            _diagnostics.ReportError(syntax.Identifier.Location, $"out 协变类型参数 '{typeParameter.Name}' 出现在逆变（参数）位置，禁止（对齐 CS1961）。");
                            break;
                        }
                    }
                }
            }
        }

        /// <summary>递归判定类型是否包含给定类型参数（泛型实参 / 函数类型参数与返回递归）。</summary>
        private static bool TypeContainsParameter(TypeSymbol type, TypeParameterSymbol typeParameter)
        {
            if (ReferenceEquals(type, typeParameter))
            {
                return true;
            }

            switch (type)
            {
                case InstantiatedTypeSymbol instantiated when instantiated.TypeArguments.Length > 0:
                    return instantiated.TypeArguments.Any(argument => TypeContainsParameter(argument, typeParameter));
                case FunctionTypeSymbol functionType:
                    return functionType.ParameterTypes.Any(p => TypeContainsParameter(p, typeParameter))
                        || TypeContainsParameter(functionType.ReturnType, typeParameter);
                default:
                    return false;
            }
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
            if (classType.GetDeclaredMethods(classType.Name).IsEmpty)
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
    }
}
