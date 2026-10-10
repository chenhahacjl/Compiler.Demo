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
    /// 接口/接口实现校验/自动属性/facade 校验/属性声明绑定（自 Declarations 拆出）。
    /// </summary>
    public partial class CocoaBinder
    {
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
            DocumentationBackfill.BackfillDocumentation(classType, syntax, _diagnostics);

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
                        // 接口不能声明运算符重载（C# 同）——给精确诊断，不落符号
                        if (methodDeclaration.IsOperatorDeclaration)
                        {
                            _diagnostics.ReportError(
                                methodDeclaration.OperatorToken?.Location ??
                                methodDeclaration.FunctionKeyword?.Location ??
                                methodDeclaration.Identifier.Location,
                                "接口不能声明运算符重载方法。");
                            continue;
                        }

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
                                DocumentationBackfill.BackfillDocumentation(method, methodDeclaration, _diagnostics);
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
                getter = new FunctionSymbol("get_" + propertyName, getterParams, propertyType!, null,
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
                var valueParameter = new ParameterSymbol("value", propertyType!, isIndexer ? indexParams.Length : 0);
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

            var interfaceProperty = new PropertySymbol(propertyName, propertyType!, interfaceType, getter, setter, visibility, isStatic: false, isIndexer: isIndexer);
            interfaceType.AddProperty(interfaceProperty);
            DocumentationBackfill.BackfillDocumentation(interfaceProperty, syntax, _diagnostics);
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

        /// <summary>查找类（含继承链）中对接口方法的实现：名称 + 参数类型 + 返回类型匹配且 public；
        /// 显式接口实现按 ExplicitInterfaceMethod 引用命中（限定名方法不在同名查找内）。</summary>
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

                // 显式接口实现（`function IReader.Read()`）：名字是限定名，按 ExplicitInterfaceMethod 引用直指槽位
                foreach (var method in current.Methods)
                {
                    if (IsExplicitImplementation(method, interfaceMethod))
                    {
                        return method;
                    }
                }
            }

            return null;
        }

        /// <summary>方法是否为 interfaceMethod（或其声明接口同构成员）的显式实现。</summary>
        private static bool IsExplicitImplementation(FunctionSymbol method, FunctionSymbol interfaceMethod)
        {
            if (method.ExplicitInterfaceMethod == null)
            {
                return false;
            }

            if (ReferenceEquals(method.ExplicitInterfaceMethod, interfaceMethod))
            {
                return true;
            }

            // 防御：跨网络同构（引用不等）时按 名 + 声明接口全名 + 签名 复核
            var iface = method.ExplicitInterfaceMethod.ContainingClass;
            return iface != null && interfaceMethod.ContainingClass != null &&
                   iface.FullName == interfaceMethod.ContainingClass.FullName &&
                   method.ExplicitInterfaceMethod.Name == interfaceMethod.Name &&
                   method.ExplicitInterfaceMethod.Parameters.Length == interfaceMethod.Parameters.Length &&
                   TypesMatchForInterfaceImplementation(method.ExplicitInterfaceMethod.ReturnType, interfaceMethod.ReturnType);
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
                return TypesMatchForInterfaceImplementation(implementationType.ElementType!, interfaceType.ElementType!);
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

        private enum FacadeMemberKind { Field, Property, Method, Constructor }

        /// <summary>6e-M31：facade 类型 public 成员须与 BCL 目标一一对应（可行子集，不可多）。
        /// 守卫：非 facade / 引用集空 / '_' 前缀（实现内部状态，如 Index._value）/ 对应物不可解析 → 跳过。
        /// 方法/构造函数按「名称 + 参数个数」在 BCL 中存在性校验；属性/字段含种类匹配（property↔field 错配即报错）。</summary>
        private void ValidateFacadeMemberAgainstBcl(NamedTypeSymbol classType, string memberName, FacadeMemberKind kind, int arity, TextLocation location)
        {
            if (!classType.IsFacadeClass) return;
            if (_references == null || _references.Length == 0) return;
            if (memberName.StartsWith("_", StringComparison.Ordinal)) return;

            var bclFullName = classType.FacadeBclTargetName
                ?? (classType.FacadeThisType is NamedTypeSymbol nts && !nts.IsPrimitiveValueType && nts != TypeSymbol.String
                    ? nts.FullName
                    : classType.FullName);
            var bcl = ExternalTypeResolver.TryResolve(bclFullName, _references.ToArray());
            if (bcl == null) return;

            if (kind == FacadeMemberKind.Method)
            {
                if (!bcl.GetMethods(memberName).Any(m => m.Parameters.Length == arity))
                {
                    _diagnostics.ReportFacadeMemberNotFound(location, bclFullName, memberName);
                }
                return;
            }

            if (kind == FacadeMemberKind.Constructor)
            {
                if (!bcl.GetMethods(bcl.Name).Any(m => m.IsConstructor && m.Parameters.Length == arity))
                {
                    _diagnostics.ReportFacadeMemberNotFound(location, bclFullName, memberName);
                }
                return;
            }

            var hasField = bcl.GetField(memberName) != null;
            var bclIsProperty = bcl.GetMethods("get_" + memberName).Any() || bcl.GetMethods("set_" + memberName).Any();
            if (kind == FacadeMemberKind.Property)
            {
                if (bclIsProperty) return;
                if (hasField) _diagnostics.ReportFacadeMemberKindMismatch(location, bclFullName, memberName, "property", "field");
                else _diagnostics.ReportFacadeMemberNotFound(location, bclFullName, memberName);
            }
            else // Field
            {
                if (hasField) return;
                if (bclIsProperty) _diagnostics.ReportFacadeMemberKindMismatch(location, bclFullName, memberName, "field", "property");
                else _diagnostics.ReportFacadeMemberNotFound(location, bclFullName, memberName);
            }
        }

        private void BindPropertyDeclaration(PropertyDeclarationSyntax syntax, NamedTypeSymbol classType, List<FunctionSymbol> classFunctions)
        {
            var isIndexer = syntax.Identifier.Text == "this";
            var propertyType = BindTypeClause(syntax.Type);
            var visibility = GetVisibility(syntax.Modifiers, Visibility.Private);
            var isStatic = isIndexer ? false : syntax.Modifiers.Any(m => m.Kind == CoreSyntax.SyntaxKind.StaticKeyword);
            var isAuto = syntax.IsAuto;

            if (isIndexer && isAuto)
            {
                _diagnostics.ReportError(syntax.Getter?.Body?.Location ?? syntax.Location, "索引器不支持自动属性，必须提供 get/set 访问器主体。");
            }

            // 自动属性：合成后备字段 _Name（索引器禁用自动属性）
            if (isAuto && !isIndexer)
            {
                var backingField = new FieldSymbol("_" + syntax.Identifier.Text, propertyType!, visibility, classType, isReadonly: false, isStatic: isStatic);
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

            // facade 实例方法降级（隐藏首参 this + 强制静态）；索引器亦遵循。
            // 6f → N1：基元别名必降；同类 facade 无实例字段（纯成员面）亦降；
            // 自型 facade struct（Index/Range）保留实例形状（对齐 BCL 实例方法 + 方法体可用隐式 this）。
            var staticContainerLike = classType.FacadeThisType != null ||
                                      (classType.IsValueType == false && !classType.Fields.Any(f => !f.IsStatic));
            var lower = !isStatic && classType.IsFacadeClass && staticContainerLike;

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

                getter = new FunctionSymbol(isIndexer ? "get_Item" : "get_" + syntax.Identifier.Text, getterParams, propertyType!, null,
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
                var valueParameter = new ParameterSymbol("value", propertyType!, isIndexer ? indexParams.Length : 0);
                var setterParams = isIndexer ? setterIndexParams.Add(valueParameter) : ImmutableArray.Create(valueParameter);
                if (lower)
                {
                    var thisParam = new ParameterSymbol("this", classType.FacadeThisType ?? classType, 0, isThis: true);
                    setterParams = new[] { thisParam }.Concat(setterParams.Select(p => new ParameterSymbol(p.Name, p.Type, p.Ordinal + 1))).ToImmutableArray();
                }

                setter = new FunctionSymbol(isIndexer ? "set_Item" : "set_" + syntax.Identifier.Text, setterParams, TypeSymbol.Void, null,
                    syntax: syntax.Setter, containingClass: classType, visibility: setterVisibility) { IsStatic = isStatic || lower, IsPropertyAccessor = true, IsInitAccessor = syntax.Setter.IsInit };
                classType.AddMethod(setter);
                classFunctions.Add(setter);
            }

            var propertyName = isIndexer ? "Item" : syntax.Identifier.Text;
            if (classType.GetDeclaredProperty(propertyName) == null)
            {
                var property = new PropertySymbol(propertyName, propertyType!, classType, getter, setter, visibility, isStatic, isIndexer: isIndexer) { IsRequired = syntax.Modifiers.Any(m => m.Kind == CoreSyntax.SyntaxKind.RequiredKeyword) };
                property.Attributes = BindAttributes(syntax.Attributes, syntax);
                if (getter != null) getter.ContainingProperty = property;
                if (setter != null) setter.ContainingProperty = property;
                classType.AddProperty(property);
                DocumentationBackfill.BackfillDocumentation(property, syntax, _diagnostics);
                if (visibility == Visibility.Public)
                {
                    ValidateFacadeMemberAgainstBcl(classType, propertyName, FacadeMemberKind.Property, 0, syntax.Identifier.Location);
                }
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
                builder.Add(new ParameterSymbol(p.Identifier.Text, type!, ordinal));
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
                    CollectNestedClasses(classDeclaration, ns.Length == 0
                        ? classDeclaration.Identifier.Text
                        : ns + "." + classDeclaration.Identifier.Text, allClasses);
                }
                else if (member is NamespaceDeclarationSyntax nested)
                {
                    CollectClasses(nested, ns, allClasses);
                }
            }
        }

        /// <summary>递归收集嵌套类型：类体里的 `class Inner { … }`。
        ///
        /// 复用外层那个 `Namespace` 槽位来承载**外层类型的限定名**，于是
        /// `CocoaBinder.cs` 阶段 2 里的 `ns + "." + Identifier.Text` 自然算出
        /// `Outer.Inner`（命名空间内的嵌套类则是 `Ns.Outer.Inner`），与解析侧
        /// 把 `Outer.Inner` 合成单个标识符的做法对齐，类型查找无需额外分支。
        ///
        /// 已知偏差：内层类型的 `Namespace` 因此会带上外层类型名
        /// （`Inner.Namespace == "Outer"` 而非 `""`）。类型全名与查找是正确的，
        /// 只有反射里读 `Namespace` 的场景会看到差异——先记为已知偏差。
        private void CollectNestedClasses(ClassDeclarationSyntax owner, string ownerQualifiedName, List<(ClassDeclarationSyntax Syntax, string Namespace)> allClasses)
        {
            foreach (var member in owner.Members)
            {
                if (member is ClassDeclarationSyntax nested)
                {
                    allClasses.Add((nested, ownerQualifiedName));
                    CollectNestedClasses(nested, ownerQualifiedName + "." + nested.Identifier.Text, allClasses);
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
            var isSyscall = syntax.Modifiers.Any(m => m.Kind == CoreSyntax.SyntaxKind.SyscallKeyword);
            var isExtern = syntax.Modifiers.Any(m => m.Kind == CoreSyntax.SyntaxKind.CdeclKeyword || m.Kind == CoreSyntax.SyntaxKind.StdcallKeyword) ||
                           syntax.ExternMetadata != null;
            // syscall/extern 方法缺省 public（System.Runtime.Runtime.Print 供 System.Console 封装层调用；extern 供类外限定调用）
            var visibility = GetVisibility(syntax.Modifiers, (isSyscall || isExtern) ? Visibility.Public : Visibility.Private);
            var isStatic = syntax.Modifiers.Any(m => m.Kind == CoreSyntax.SyntaxKind.StaticKeyword);

// 6e-M19 M2-b → 6f → N1：facade 实例方法降级条件——
            //   · FacadeThisType 指向异型（Int32/Type 等基元别名）→ 必须降级；
            //   · 同类 facade（FacadeThisType==null）无实例字段（纯成员面载体，如 Exception/上下文字）→ 维持旧降级；
            //   · 同类 facade 携带实例状态（FileStream._h / Lock._owner）→ 保留真实例（成员可用字段/body，IL 端仍直链 BCL）；
            //   · N1：自型 facade struct（Index/Range，映射 BCL 值类型且 BCL 侧为实例方法）→ 保留实例形状，
            //     降级会使方法体失去隐式 this（"静态方法中不能访问实例字段"）且与 BCL 实例方法签名不匹配。
            var staticContainerLike = classType.FacadeThisType != null ||
                                      (classType.IsValueType == false && !classType.Fields.Any(f => !f.IsStatic));
            if (!isStatic && !isSyscall && !isExtern && classType.IsFacadeClass && staticContainerLike)
            {
                isStatic = true;
                var thisParameter = new ParameterSymbol("this", classType.FacadeThisType ?? classType, 0, isThis: true);
                var shifted = parameters.Select(p => new ParameterSymbol(p.Name, p.Type, p.Ordinal + 1)).ToArray();
                parameters = new[] { thisParameter }.Concat(shifted).ToImmutableArray();
            }

            var isVirtual = syntax.Modifiers.Any(m => m.Kind == CoreSyntax.SyntaxKind.VirtualKeyword);
            var isOverride = syntax.Modifiers.Any(m => m.Kind == CoreSyntax.SyntaxKind.OverrideKeyword);
            var isAbstract = syntax.Modifiers.Any(m => m.Kind == CoreSyntax.SyntaxKind.AbstractKeyword);
            var isSealed = syntax.Modifiers.Any(m => m.Kind == CoreSyntax.SyntaxKind.SealedKeyword);

            BuiltinKind? builtinKind = null;
            if (isSyscall)
            {
                var builtin = BuiltinFunctions.GetByName(syntax.Identifier.Text);
                if (builtin != null)
                {
                    builtinKind = builtin.BuiltinKind;
                }
                else if (SyscallCallbackResolver?.Invoke(syntax.Identifier.Text) != true)
                {
                    // 嵌入式引擎回调（RegisterCallback）：CocoaBinder.SyscallCallbackResolver 命中时
                    // 该 syscall 是引擎注册的 C# 回调（Evaluator 求值期查回调表），不报未知。
                    _diagnostics.ReportSyscallFunctionUnknown(syntax.Identifier.Location, syntax.Identifier.Text);
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

            // 运算符重载 / 转换运算符声明：先解出种类与合成元数据名（Name 只读，须在构造符号前定名）
            OperatorKind? operatorKind = null;
            if (syntax.IsOperatorDeclaration)
            {
                operatorKind = ResolveDeclaredOperatorKind(syntax);
            }

            var declaredName = operatorKind.HasValue ? OperatorNames.ToMetadataName(operatorKind.Value) : syntax.Identifier.Text;

            // 显式接口实现（`public function IReader.Read(): i32`）：解析层已合成限定名标识符。
            // 显式方法以限定名注册（Name = "IReader.Read"），非限定 `doc.Read()` 查找不命中（C# 语义）；
            // 三后端经 ExplicitInterfaceMethod 引用解析进接口槽（native vtable / IL MethodImpl / Evaluator 分派）。
            FunctionSymbol? explicitInterfaceMethod = null;
            if (declaredName.IndexOf('.') >= 0)
            {
                var dot = declaredName.LastIndexOf('.');
                var interfacePrefix = declaredName.Substring(0, dot);
                var memberName = declaredName.Substring(dot + 1);
                var interfaceType = LookupType(interfacePrefix) as NamedTypeSymbol;

                if (interfaceType == null || interfaceType.TypeKind != TypeKind.Interface)
                {
                    _diagnostics.ReportError(syntax.Identifier.Location, $"显式接口实现 '{declaredName}' 的前缀 '{interfacePrefix}' 不是接口类型。");
                }
                else if (!classType.GetAllInterfaces().Contains(interfaceType))
                {
                    _diagnostics.ReportError(syntax.Identifier.Location, $"类 '{classType.Name}' 未实现接口 '{interfacePrefix}'，不能显式实现其成员。");
                }
                else
                {
                    var member = interfaceType.GetMethod(memberName);
                    if (member == null)
                    {
                        _diagnostics.ReportError(syntax.Identifier.Location, $"接口 '{interfacePrefix}' 没有成员 '{memberName}'。");
                    }
                    else if (member.IsStatic || member.IsConstructor)
                    {
                        _diagnostics.ReportError(syntax.Identifier.Location, $"接口 '{interfacePrefix}' 的成员 '{memberName}' 不能显式实现（非实例方法）。");
                    }
                    else
                    {
                        var paramsMatch = parameters.Length == member.Parameters.Length;
                        for (var i = 0; paramsMatch && i < parameters.Length; i++)
                        {
                            if (!TypesMatchForInterfaceImplementation(parameters[i].Type, member.Parameters[i].Type))
                            {
                                paramsMatch = false;
                            }
                        }

                        if (!paramsMatch || !TypesMatchForInterfaceImplementation(type, member.ReturnType))
                        {
                            _diagnostics.ReportError(syntax.Identifier.Location, $"显式接口实现 '{declaredName}' 的签名与接口成员 '{memberName}' 不符。");
                        }
                        else
                        {
                            explicitInterfaceMethod = member;
                        }
                    }
                }
            }

            // syscall 方法隐含 static（System.Runtime.Runtime.Print 类名调用）
            var method = new FunctionSymbol(declaredName, parameters, type, syntax, isExtern: isExtern, dllName: dllName, callingConvention: GetCallingConvention(syntax), containingClass: classType, visibility: visibility, builtinKind: builtinKind, entryPoint: entryPoint, charSet: charSet)
            {
                IsStatic = isStatic || isSyscall || operatorKind.HasValue,
                IsVirtual = isVirtual,
                IsOverride = isOverride,
                IsAbstract = isAbstract,
                IsSealed = isSealed,
                OperatorKind = operatorKind,
            };
            DocumentationBackfill.BackfillDocumentation(method, syntax, _diagnostics);
            // 类方法级 attribute（[Obsolete] 等）——此前类方法漏绑，导致调用点无法消费
            method.Attributes = BindAttributes(syntax.Attributes, syntax);

            // 显式接口实现：绑定目标接口成员；显式实现本身非虚（sealed 语义，锁死槽位实现）
            method.ExplicitInterfaceMethod = explicitInterfaceMethod;
            if (explicitInterfaceMethod != null)
            {
                method.IsVirtual = false;
                method.IsOverride = false;
                method.IsAbstract = false;
            }

            // 运算符声明的元数/宿主/形态校验 + 登记查找表
            if (operatorKind.HasValue)
            {
                ValidateAndRegisterOperator(syntax, method, classType, parameters, type, operatorKind.Value);
            }

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
                    // 沿继承链向上查找（含当前类自身）——virtual 定义可能在当前类而非 BaseType
                    var candidates = classType.GetMethods(syntax.Identifier.Text)
                        .Where(m => (m.IsVirtual || m.IsAbstract) && !m.IsSealed && m != method)
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
                            var nearest = classType.BaseType!.GetMethod(syntax.Identifier.Text);
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

        /// <summary>解出运算符声明的种类：implicit/explicit 前缀直判；`operator X` 按 token 翻译（+ - 单参时回退一元）。</summary>
        private static OperatorKind? ResolveDeclaredOperatorKind(FunctionDeclarationSyntax syntax)
        {
            if (syntax.IsImplicitConversion)
            {
                return OperatorKind.ImplicitConversion;
            }

            if (syntax.IsExplicitConversion)
            {
                return OperatorKind.ExplicitConversion;
            }

            var token = syntax.OperatorToken;
            if (token == null)
            {
                return null;
            }

            // 元数为 1 时 `+`/`-` 是二元符号的一元形态
            if (syntax.Parameters.Count == 1)
            {
                return OperatorNames.UnaryFromToken(token.Kind);
            }

            return OperatorNames.FromToken(token.Kind);
        }

        /// <summary>
        /// 运算符重载 / 转换运算符声明校验 + 登记：
        /// 元数、转换目标非 void、禁接口、禁泛型、禁 extern/syscall、禁 virtual/override/abstract，最后入 <see cref="Operators"/>。
        /// </summary>
        private void ValidateAndRegisterOperator(
            FunctionDeclarationSyntax syntax,
            FunctionSymbol method,
            NamedTypeSymbol classType,
            ImmutableArray<ParameterSymbol> parameters,
            TypeSymbol returnType,
            OperatorKind kind)
        {
            var location = syntax.OperatorToken?.Location ?? syntax.FunctionKeyword?.Location ?? syntax.Identifier.Location;

            var arity = OperatorNames.Arity(kind);
            if (parameters.Length != arity)
            {
                _diagnostics.ReportError(location,
                    $"运算符 '{OperatorNames.ToMetadataName(kind)}' 需要 {arity} 个操作数，实际 {parameters.Length} 个。");
                return;
            }

            // 转换运算符的返回类型即目标类型；不可为 void
            if (OperatorNames.IsConversion(kind) && returnType == TypeSymbol.Void)
            {
                _diagnostics.ReportError(location, "转换运算符的返回类型不能为 void。");
                return;
            }

            if (classType.TypeKind == TypeKind.Interface)
            {
                _diagnostics.ReportError(location, "接口不能声明运算符重载方法。");
                return;
            }

            if (syntax.TypeParameters?.Parameters.Length > 0 || method.TypeParameters.Length > 0)
            {
                _diagnostics.ReportError(location, "运算符重载方法暂不支持泛型类型参数。");
                return;
            }

            if (method.IsExtern || method.BuiltinKind != null)
            {
                _diagnostics.ReportError(location, "extern/syscall 方法不能声明为运算符重载。");
                return;
            }

            if (syntax.Modifiers.Any(m => m.Kind == CoreSyntax.SyntaxKind.VirtualKeyword ||
                                          m.Kind == CoreSyntax.SyntaxKind.OverrideKeyword ||
                                          m.Kind == CoreSyntax.SyntaxKind.AbstractKeyword))
            {
                _diagnostics.ReportError(location, "运算符重载方法不能是 virtual/override/abstract。");
                return;
            }

            if (!_operators.Register(method, parameters.Select(p => (TypeSymbol?)p.Type).ToList()))
            {
                _diagnostics.ReportSymbolAlreadyDeclared(location, OperatorNames.ToMetadataName(kind));
            }
        }

        private static bool IsOverrideSignatureMatch(FunctionSymbol baseMethod, FunctionSymbol overrideMethod)        {
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
                .FirstOrDefault(k => k == CoreSyntax.SyntaxKind.CdeclKeyword || k == CoreSyntax.SyntaxKind.StdcallKeyword) switch
            {
                CoreSyntax.SyntaxKind.CdeclKeyword => CallingConvention.Cdecl,
                CoreSyntax.SyntaxKind.StdcallKeyword => CallingConvention.StdCall,
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
                    var isExternDecl = functionDeclaration.Modifiers.Any(m => m.Kind == CoreSyntax.SyntaxKind.CdeclKeyword || m.Kind == CoreSyntax.SyntaxKind.StdcallKeyword) ||
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
        private CharSet ParseCharSetValue(CoreSyntax.SyntaxToken? valueToken)
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
            var isBase = syntax.InitializerKeyword!.Kind == CoreSyntax.SyntaxKind.BaseKeyword;
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

            // 6e-M17 Step 3：移除内置函数隐式注入（强隔离）——print/input/random 等
            // 不再全局裸可用；用户须 `using System.Console` 后 WriteLine/ReadLine，或
            // 经 System.Runtime（syscall 容器类，SystemLibrary 内建嵌入）显式调用。

            return result;
        }

        /// <summary>把 `.coa` 库的公共符号注入作用域（v1 无命名空间 → 裸注册；非空命名空间留扩展位，.coa v2 时启用）。</summary>
        /// <summary>6f-3：跨用户库同名类型全名集（类/枚举/泛型定义）——同一全名被 ≥2 个不同用户库声明即歧义。
        /// 系统库（System*）权威内置、允许覆盖/补充，不参与判定（与装载侧 DetectAmbiguousTypes 一致）。</summary>
        private static ImmutableHashSet<string> ComputeAmbiguousCodTypeNames(ImmutableArray<CoaProgram> codLibraries)
        {
            var byFullName = new Dictionary<string, CoaProgram>(StringComparer.Ordinal);
            var ambiguous = new HashSet<string>(StringComparer.Ordinal);
            foreach (var library in codLibraries)
            {
                if (library.Name.StartsWith("System", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var type in library.Classes.Concat(library.Enums).Concat(library.GenericDefinitions))
                {
                    var key = type.FullName;
                    if (byFullName.TryGetValue(key, out var first) && !ReferenceEquals(first, library))
                    {
                        ambiguous.Add(key);
                    }
                    else
                    {
                        byFullName[key] = library;
                    }
                }
            }

            return ambiguous.ToImmutableHashSet();
        }

        private static void InjectCodSymbols(BoundScope scope, ImmutableArray<CoaProgram> codLibraries)
        {
            if (codLibraries.IsDefaultOrEmpty)
            {
                return;
            }

            // 6f-3：跨用户库同名类型（类/枚举/泛型定义）全名集——非限定使用拒绝注入（绑定期诊断），
            // 消歧经 `using X = 库名.全名` 库限定解析（LookupType.TryResolveLibraryScopedType）。
            var ambiguousCodTypeNames = ComputeAmbiguousCodTypeNames(codLibraries);

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
                    if (ambiguousCodTypeNames.Contains(enumType.FullName))
                    {
                        continue;
                    }

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

                    // 6f-3：跨库同名类型拒绝注入（非限定使用即诊断；库限定别名解析唯一化）
                    if (ambiguousCodTypeNames.Contains(classType.FullName))
                    {
                        continue;
                    }

                    // 6e-M19 M2-b：facade 标记不序列化，注入侧按全名映射表补齐。
                    // 编译器内建单例（System.Object/System.Type，Fn 条目的 owner 反解会引用到它们）**不得**标记——
                    // 否则 native Object 面成员分派旁落运行时默认、IL facade 降级误作直链（回归：Oop_Override_* ×4）。
                    if (!SystemObjectMembers.IsBuiltinSystemClass(classType) && !classType.IsFacadeClass && FacadeTargets.ContainsKey(classType.FullName))
                    {
                        // 幂等设置（仅首次）：标记 facade + 绑定 companionship
                        if (!classType.IsFacadeClass)
                        {
                            classType.IsFacadeClass = true;

                            // Phase 1-3 facade 合并：基元用类型表登记为 facade 全名（System.Int32 → TypeSymbol.Int32），
                            // 成员面经 FacadeCompanion 委托到本类（System.Core 缓存实例，进程内共享，赋值幂等）。
                            var target = FacadeTargets[classType.FullName];
                            classType.FacadeThisType = target;
                            if (target is NamedTypeSymbol primitiveTarget)
                            {
                                primitiveTarget.FacadeCompanion = classType;
                            }
                        }

                        // 每次编译都须向 fresh scope 注册 facade 承载类型（scope 非共享）
                        if (classType.FacadeThisType is NamedTypeSymbol facadeTarget)
                        {
                            scope.TryDeclareClass(facadeTarget);
                        }
                    }

                    scope.TryDeclareClass(classType);
                }
            }
        }

        public DiagnosticBag Diagnostics => _diagnostics;
    }
}
