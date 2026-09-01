using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Coa;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Binding
{    /// <summary>
    /// 单态化展开器（6e-M20 G2）：
    /// <list type="number">
    /// <item><b>活实例化收集</b>：扫描本编译全部声明语法（类/接口/函数/全局语句）中的 GenericTypeClauseSyntax，
    /// 经辅助 Binder 重绑实参 → Instantiate 命中缓存壳（与正常绑定同实例）；再沿实例化成员签名 BFS 闭包
    /// （字段/属性/方法签名中的嵌套实例化，如 `Wrapper&lt;Box&lt;int&gt;&gt;`）。</item>
    /// <item><b>方法体重绑</b>：对每个活实例化，以实例化方法符号为容器、类型参数名→具体实参映射注入 Binder，
    /// 从泛型定义的<b>语法</b>重新绑定方法体——成员查找命中替换后的实例化成员，三后端拿到全具体类型的绑定树。</item>
    /// <item><b>发射清单</b>：过滤泛型定义类（模板不发射），并入全部活实例化类。</item>
    /// </list>
    /// </summary>
    internal static class Monomorphizer
    {
        public static (ImmutableArray<NamedTypeSymbol> Classes, ImmutableArray<NamedTypeSymbol> GenericDefinitions) Expand(
            BoundGlobalScope globalScope,
            BoundScope parentScope,
            bool isScript,
            ImmutableArray<CoaProgram> codLibraries,
            Language dialect,
            ImmutableDictionary<FunctionSymbol, BoundBlockStatement>.Builder functionBodies,
            ImmutableArray<Diagnostic>.Builder diagnostics)
        {
            // 1. 活实例化种子：语法扫描泛型类型子句 + new/调用站点的显式实参
            var helperBinder = dialect.CreateBinder(isScript, parentScope, null, globalScope.References, globalScope.UsingNamespaces, dialect.LookupBuiltinType, globalScope.UsingStatics, globalScope.UsingAliases, codLibraries);
            // 6e 跨库里程碑：源码泛型定义先注册（同名占位，cod 后注册静默跳过）——保证源内联集合类
            // 优先于 System.Collections.coa 同名 gcls。
            helperBinder.RegisterSourceGenericDefinitionsForSeed(globalScope);
            helperBinder.RegisterCodGenericDefinitionsForSeed(codLibraries);
            var seeded = new HashSet<InstantiatedTypeSymbol>();
            var methodSeeds = new List<(FunctionSymbol Instantiated, FunctionSymbol Definition, ImmutableArray<TypeSymbol> Arguments)>();
            var seenMethods = new HashSet<FunctionSymbol>();

            foreach (var (identifier, argumentClauses) in CollectGenericUsages(globalScope))
            {
                var result = helperBinder.BindGenericTypeNameForExpansion(identifier, argumentClauses);
                if (result is InstantiatedTypeSymbol instantiated)
                {
                    seeded.Add(instantiated);
                }
            }

            // 2. 泛型方法种子（6e-M22 C1）：主绑定已把 `Swap<i32>(…)`/`list.Pick<T>(…)` 落成具体方法符号
            //    （顶层调用 / 类静态 / 实例接收者三路同构），走查已绑定函数体经实例化溯源表取 (定义, 实参)。
            //    重绑产出的新函数体可能再引用其他泛型方法——工作表循环至不动点。
            var pendingBodies = new Queue<BoundNode>();
            foreach (var statement in globalScope.Statements)
            {
                pendingBodies.Enqueue(statement);
            }

            foreach (var fn in functionBodies.Keys)
            {
                pendingBodies.Enqueue(functionBodies[fn]);
            }

            var processedMethodSeeds = new HashSet<FunctionSymbol>();

            while (true)
            {
                var discovered = false;

                while (pendingBodies.Count > 0)
                {
                    foreach (var node in WalkBoundNodes(pendingBodies.Dequeue()))
                    {
                        var instantiated = node switch
                        {
                            BoundCallExpression call => call.Function,
                            BoundMemberCallExpression memberCall => memberCall.Method,
                            _ => null,
                        };

                        if (instantiated != null && seenMethods.Add(instantiated) &&
                            GenericMethodInstantiator.TryGetProvenance(instantiated, out var seedDefinition, out var seedArguments))
                        {
                            methodSeeds.Add((instantiated, seedDefinition, seedArguments));
                            discovered = true;
                        }
                    }
                }

                if (!discovered)
                {
                    break;
                }

                for (var i = 0; i < methodSeeds.Count; i++)
                {
                    var (instantiatedMethod, methodDefinition, methodArguments) = methodSeeds[i];
                    if (!processedMethodSeeds.Add(instantiatedMethod))
                    {
                        continue;
                    }

                    var typeArgumentsByName = BuildNameMap(methodDefinition.TypeParameters, methodArguments, methodDefinition.Declaration, diagnostics);
                    if (typeArgumentsByName == null)
                    {
                        continue;
                    }

                    var (methodBody, methodBodyDiagnostics) = Binder.BuildFunctionBodyForMonomorphization(
                        isScript, parentScope, instantiatedMethod, globalScope, codLibraries, dialect, typeArgumentsByName);

                    functionBodies[instantiatedMethod] = methodBody;
                    diagnostics.AddRange(methodBodyDiagnostics);

                    pendingBodies.Enqueue(methodBody);
                }
            }

            if (seeded.Count == 0 && methodSeeds.Count == 0)
            {
                // 6e-G7 S1：无活实例化时泛型定义仍需携带（.coa gcls）
                return (FilterDeclaredClasses(globalScope),
                        globalScope.Classes.Where(c => c.IsGenericDefinition).ToImmutableArray());
            }

            // 2. 成员签名 BFS 闭包（嵌套实例化）
            var live = new List<InstantiatedTypeSymbol>();
            var visited = new HashSet<InstantiatedTypeSymbol>();
            var queue = new Queue<InstantiatedTypeSymbol>(seeded);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!visited.Add(current))
                {
                    continue;
                }

                live.Add(current);

                foreach (var nested in CollectSignatureInstantiations(current))
                {
                    queue.Enqueue(nested);
                }
            }


            // 接口实例化仅作编译期能力标记/类型检查：不进发射清单（native 无接口分派、
            // IL 接口定义成员携带类型参数无法按具体类发射）；foreach 走具体枚举器类
            live.RemoveAll(i => i.GenericDefinition.IsInterface);

            // 3. 方法体重绑（定义语法 + T→实参映射；构造链/字段初始化器复用 BuildFunctionBody 管道）
            foreach (var instantiated in live)
            {
                var definition = instantiated.GenericDefinition;
                var typeArgumentsByName = BuildNameMap(definition.TypeParameters, instantiated.TypeArguments, instantiated.Declaration, diagnostics);
                if (typeArgumentsByName == null)
                {
                    continue;
                }

                for (var i = 0; i < definition.Methods.Length && i < instantiated.Methods.Length; i++)
                {
                    var instantiatedMethod = instantiated.Methods[i];
                    var definitionMethod = definition.Methods[i];

                    // 方法级泛型模板（6e-M22 C1）：类实例化携带的 <U> 模板不重绑不发射——
                    // 开放类型参数签名无法编码，调用点经 GenericMethodInstantiator 产出具体副本走溯源不动点
                    if (instantiatedMethod.IsGenericMethod)
                    {
                        continue;
                    }

                    if (instantiatedMethod.IsExtern || instantiatedMethod.IsAbstract || instantiatedMethod.BuiltinKind != null)
                    {
                        functionBodies[instantiatedMethod] = new BoundBlockStatement(instantiatedMethod.Declaration!, ImmutableArray<BoundStatement>.Empty);
                        continue;
                    }

                    // 6e 跨库里程碑：instantiated.Methods 与 definition.Methods 可能异序（Populate 先方法后
                    // 属性访问器，与 cod 读侧 fn 条目序不同）——按「方法名 + 元数 + 类」重定位定义方法，
                    // 避免索引错配（cod=get_Count 落到 inst=Add）。
                    definitionMethod = FindDefinitionMethod(definition, instantiatedMethod)
                        ?? definition.Methods.FirstOrDefault(m => SameMethodSignature(m, instantiatedMethod))
                        ?? definitionMethod;

                    // 6e-G7 S5/S6：cod 来源定义（无语法树）→ 用库携带的开放绑定体做替换展开
                    if (definition.Declaration == null)
                    {
                        BoundBlockStatement? openBody = null;
                        foreach (var library in codLibraries)
                        {
                            if (library.Bodies.TryGetValue(definitionMethod, out var candidate))
                            {
                                openBody = candidate;
                                break;
                            }
                        }

                        if (openBody != null)
                        {
                            functionBodies[instantiatedMethod] =
                                BoundTreeSubstituter.SubstituteMethodBody(openBody, definition, instantiated, instantiatedMethod, definitionMethod);
                            continue;
                        }
                    }

                    var (body, bodyDiagnostics) = Binder.BuildFunctionBodyForMonomorphization(
                        isScript, parentScope, instantiatedMethod, globalScope, codLibraries, dialect, typeArgumentsByName);

                    functionBodies[instantiatedMethod] = body;
                    diagnostics.AddRange(bodyDiagnostics);
                }

                // 属性访问器（getter/setter）同样重绑：索引与定义对齐
                for (var i = 0; i < definition.Properties.Length && i < instantiated.Properties.Length; i++)
                {
                    var definitionProperty = definition.Properties[i];
                    var instantiatedProperty = instantiated.Properties[i];

                    if (definitionProperty.Getter != null && instantiatedProperty.Getter != null &&
                        !instantiatedProperty.Getter.IsExtern && !instantiatedProperty.Getter.IsAbstract)
                    {
                        // 6e 跨库里程碑：cod 库属性访问器（getter 无 Declaration/Syntax）→ 与 177-194 同款捷径：
                        // 用库携带的开放绑定体做替换展开，避免 BuildFunctionBodyForMonomorphization 在 null 语法上 NRE。
                        if (TrySubstituteCodAccessor(definitionProperty.Getter, definition, instantiated, instantiatedProperty.Getter, codLibraries, out var codGetterBody))
                        {
                            functionBodies[instantiatedProperty.Getter] = codGetterBody;
                        }
                        else
                        {
                            var (getterBody, getterDiagnostics) = Binder.BuildFunctionBodyForMonomorphization(
                                isScript, parentScope, instantiatedProperty.Getter, globalScope, codLibraries, dialect, typeArgumentsByName);
                            functionBodies[instantiatedProperty.Getter] = getterBody;
                            diagnostics.AddRange(getterDiagnostics);
                        }
                    }

                    if (definitionProperty.Setter != null && instantiatedProperty.Setter != null &&
                        !instantiatedProperty.Setter.IsExtern && !instantiatedProperty.Setter.IsAbstract)
                    {
                        // 同上：cod 库 setter 开放体替换捷径
                        if (TrySubstituteCodAccessor(definitionProperty.Setter, definition, instantiated, instantiatedProperty.Setter, codLibraries, out var codSetterBody))
                        {
                            functionBodies[instantiatedProperty.Setter] = codSetterBody;
                        }
                        else
                        {
                            var (setterBody, setterDiagnostics) = Binder.BuildFunctionBodyForMonomorphization(
                                isScript, parentScope, instantiatedProperty.Setter, globalScope, codLibraries, dialect, typeArgumentsByName);
                            functionBodies[instantiatedProperty.Setter] = setterBody;
                            diagnostics.AddRange(setterDiagnostics);
                        }
                    }
                }
            }

            // 4. 发射清单：过滤泛型定义（模板）+ 并入活实例化；
            //    6e-G7 S1：泛型定义单独携带（仅 .coa 发射消费 gcls；IL/native 清单仍排除模板）
            var builder = FilterDeclaredClasses(globalScope).ToBuilder();
            builder.AddRange(live);

            var genericDefinitions = globalScope.Classes.Where(c => c.IsGenericDefinition).ToImmutableArray();

            return (builder.ToImmutable(), genericDefinitions);
        }

        /// <summary>
        /// 6e 跨库里程碑：cod 库属性访问器（getter/setter，无 Declaration/Syntax）开放体替换捷径——
        /// 从库携带的 Bodies 取定义访问器开放体，经 BoundTreeSubstituter 替换为实例化访问器体。
        /// 与 176-194 方法分支同款逻辑；cod 来源定义走捷径避免 BuildFunctionBodyForMonomorphization NRE。
        /// </summary>
        private static bool TrySubstituteCodAccessor(
            FunctionSymbol definitionAccessor,
            NamedTypeSymbol definition,
            InstantiatedTypeSymbol instantiated,
            FunctionSymbol instantiatedAccessor,
            ImmutableArray<CoaProgram> codLibraries,
            out BoundBlockStatement body)
        {
            body = null!;

            // cod 来源定义（无语法树）
            if (definitionAccessor.Declaration != null)
            {
                return false;
            }

            foreach (var library in codLibraries)
            {
                if (library.Bodies.TryGetValue(definitionAccessor, out var openBody))
                {
                    body = BoundTreeSubstituter.SubstituteMethodBody(openBody, definition, instantiated, instantiatedAccessor, definitionAccessor);
                    return true;
                }
            }

            return false;
        }

        private static ImmutableArray<NamedTypeSymbol> FilterDeclaredClasses(BoundGlobalScope globalScope)
        {
            return globalScope.Classes.Where(c => !c.IsGenericDefinition).ToImmutableArray();
        }

        /// <summary>
        /// 6e 跨库里程碑：在定义类方法集中定位实例化方法的对应定义（名称 + 元数匹配；
        /// 构造器按身份匹配——实例化构造器名被 Populate 改为 mangle 名）。索引错配兜底。
        /// </summary>
        private static FunctionSymbol? FindDefinitionMethod(NamedTypeSymbol definition, FunctionSymbol instantiatedMethod)
        {
            if (instantiatedMethod.IsConstructor)
            {
                foreach (var candidate in definition.Methods)
                {
                    if (candidate.IsConstructor)
                    {
                        return candidate;
                    }
                }
            }

            foreach (var candidate in definition.Methods)
            {
                if (candidate.Name == instantiatedMethod.Name &&
                    candidate.Parameters.Length == instantiatedMethod.Parameters.Length)
                {
                    return candidate;
                }
            }

            return null;
        }

        private static bool SameMethodSignature(FunctionSymbol a, FunctionSymbol b)
        {
            return a.Name == b.Name && a.Parameters.Length == b.Parameters.Length;
        }

        /// <summary>定义类型参数名 → 实例化实参。实参若仍为类型参数（嵌套泛型上下文）暂不支持重绑，报明确诊断。</summary>
        private static Dictionary<string, TypeSymbol>? BuildNameMap(ImmutableArray<TypeParameterSymbol> parameters, ImmutableArray<TypeSymbol> arguments, SyntaxNode? declaration, ImmutableArray<Diagnostic>.Builder diagnostics)
        {
            var map = new Dictionary<string, TypeSymbol>();

            for (var i = 0; i < parameters.Length && i < arguments.Length; i++)
            {
                if (arguments[i] is TypeParameterSymbol parameterArgument)
                {
                    var location = declaration?.Location ?? default;
                    diagnostics.Add(Diagnostic.Error(location, $"泛型嵌套上下文中的实例化暂不支持（外层类型参数 '{parameterArgument.Name}' 作实参，6e-M20 后续）。"));
                    return null;
                }

                map[parameters[i].Name] = arguments[i];
            }

            return map;
        }

        /// <summary>绑定树先序走查（6e-M22 C1）：泛型方法种子收集用；子节点经 Compilation.BoundChildren。</summary>
        private static IEnumerable<BoundNode> WalkBoundNodes(BoundNode root)
        {
            yield return root;

            foreach (var child in Compilation.BoundChildren(root))
            {
                foreach (var descendant in WalkBoundNodes(child))
                {
                    yield return descendant;
                }
            }
        }

        /// <summary>声明语法根集合（类声明 / 函数语法），供泛型用法扫描共用。</summary>
        private static IEnumerable<SyntaxNode> CollectDeclarationRoots(BoundGlobalScope globalScope)
        {
            var roots = new HashSet<SyntaxNode>();

            foreach (var @class in globalScope.Classes)
            {
                if (@class.Declaration != null)
                {
                    roots.Add(@class.Declaration);
                }
            }

            foreach (var function in globalScope.Functions)
            {
                var root = (SyntaxNode?)function.Syntax ?? (SyntaxNode?)function.Declaration;
                if (root != null)
                {
                    roots.Add(root);
                }
            }

            return roots;
        }

        /// <summary>
        /// 扫描本编译全部声明语法中的泛型用法站点（6e-M20）：
        /// ① 类型位置的 GenericTypeClauseSyntax（`var x: List&lt;int&gt;` / extends / where）；
        /// ② 对象创建站点的显式实参（`new Box&lt;i32&gt;(…)`——泛型信息在 TypeArguments 上，无子句节点）。
        /// 返回 (类型名, 实参子句列表) 对。
        /// </summary>
        private static IEnumerable<(SyntaxToken Identifier, ImmutableArray<TypeClauseSyntax> Arguments)> CollectGenericUsages(BoundGlobalScope globalScope)
        {
            foreach (var root in CollectDeclarationRoots(globalScope))
            {
                foreach (var node in Walk(root))
                {
                    if (node is GenericTypeClauseSyntax genericClause)
                    {
                        yield return (genericClause.Identifier, genericClause.TypeArguments);
                    }
                    else if (node is ObjectCreationExpressionSyntax creation && creation.TypeArguments != null)
                    {
                        yield return (creation.Identifier, creation.TypeArguments.Arguments);
                    }
                }
            }
        }

        private static IEnumerable<SyntaxNode> Walk(SyntaxNode node)
        {
            yield return node;

            foreach (var child in node.GetChildren())
            {
                foreach (var descendant in Walk(child))
                {
                    yield return descendant;
                }
            }
        }

        /// <summary>实例化成员签名中的嵌套实例化（基类/接口/字段/属性/方法参数与返回）。</summary>
        private static IEnumerable<InstantiatedTypeSymbol> CollectSignatureInstantiations(InstantiatedTypeSymbol instantiated)
        {
            void Collect(TypeSymbol? type, List<InstantiatedTypeSymbol> sink)
            {
                switch (type)
                {
                    case InstantiatedTypeSymbol nested:
                        sink.Add(nested);
                        foreach (var argument in nested.TypeArguments)
                        {
                            Collect(argument, sink);
                        }

                        break;

                    case TypeParameterSymbol:
                        break;

                    default:
                        if (type is ArrayTypeSymbol)
                        {
                            Collect(type.ElementType, sink);
                        }

                        break;
                }
            }

            var found = new List<InstantiatedTypeSymbol>();

            Collect(instantiated.BaseType, found);

            foreach (var iface in instantiated.Interfaces)
            {
                Collect(iface, found);
            }

            foreach (var field in instantiated.Fields)
            {
                Collect(field.Type, found);
            }

            foreach (var property in instantiated.Properties)
            {
                Collect(property.Type, found);
            }

            foreach (var method in instantiated.Methods)
            {
                Collect(method.ReturnType, found);
                foreach (var parameter in method.Parameters)
                {
                    Collect(parameter.Type, found);
                }
            }

            return found;
        }
    }
}
