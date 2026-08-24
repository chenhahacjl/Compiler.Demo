using Cocoa.CodeAnalysis.Cod;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
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
        public static ImmutableArray<ClassTypeSymbol> Expand(
            BoundGlobalScope globalScope,
            BoundScope parentScope,
            bool isScript,
            ImmutableArray<CodProgram> codLibraries,
            LanguageDialect dialect,
            ImmutableDictionary<FunctionSymbol, BoundBlockStatement>.Builder functionBodies,
            ImmutableArray<Diagnostic>.Builder diagnostics)
        {
            // 1. 活实例化种子：语法扫描泛型类型子句 + new/调用站点的显式实参
            var helperBinder = new Binder(isScript, parentScope, null, globalScope.References, globalScope.UsingNamespaces, dialect, globalScope.UsingStatics, globalScope.UsingAliases, codLibraries);
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

            foreach (var (identifier, argumentClauses) in CollectGenericMethodUsages(globalScope))
            {
                var definition = helperBinder.ResolveGenericMethodDefinitionForExpansion(identifier.Text, argumentClauses.Length);
                if (definition == null)
                {
                    continue;
                }

                var arguments = ImmutableArray.CreateBuilder<TypeSymbol>();
                var valid = true;
                foreach (var clause in argumentClauses)
                {
                    if (helperBinder.BindTypeClauseForExpansion(clause) is not TypeSymbol argument)
                    {
                        valid = false;
                        break;
                    }

                    arguments.Add(argument);
                }

                if (!valid)
                {
                    continue;
                }

                var instantiated = GenericMethodInstantiator.Instantiate(definition, arguments.ToImmutable());
                if (seenMethods.Add(instantiated))
                {
                    methodSeeds.Add((instantiated, definition, arguments.ToImmutable()));
                }
            }

            if (seeded.Count == 0 && methodSeeds.Count == 0)
            {
                return FilterDeclaredClasses(globalScope);
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

                    if (instantiatedMethod.IsExtern || instantiatedMethod.IsAbstract || instantiatedMethod.BuiltinKind != null)
                    {
                        functionBodies[instantiatedMethod] = new BoundBlockStatement(instantiatedMethod.Declaration!, ImmutableArray<BoundStatement>.Empty);
                        continue;
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
                        var (getterBody, getterDiagnostics) = Binder.BuildFunctionBodyForMonomorphization(
                            isScript, parentScope, instantiatedProperty.Getter, globalScope, codLibraries, dialect, typeArgumentsByName);
                        functionBodies[instantiatedProperty.Getter] = getterBody;
                        diagnostics.AddRange(getterDiagnostics);
                    }

                    if (definitionProperty.Setter != null && instantiatedProperty.Setter != null &&
                        !instantiatedProperty.Setter.IsExtern && !instantiatedProperty.Setter.IsAbstract)
                    {
                        var (setterBody, setterDiagnostics) = Binder.BuildFunctionBodyForMonomorphization(
                            isScript, parentScope, instantiatedProperty.Setter, globalScope, codLibraries, dialect, typeArgumentsByName);
                        functionBodies[instantiatedProperty.Setter] = setterBody;
                        diagnostics.AddRange(setterDiagnostics);
                    }
                }
            }

            // 3.5 泛型方法体重绑（6e-M20）：Swap<int> → Swap$int，定义语法 + 方法类型参数名→实参映射
            foreach (var (instantiatedMethod, definition, arguments) in methodSeeds)
            {
                var typeArgumentsByName = BuildNameMap(definition.TypeParameters, arguments, definition.Declaration, diagnostics);
                if (typeArgumentsByName == null)
                {
                    continue;
                }

                var (body, bodyDiagnostics) = Binder.BuildFunctionBodyForMonomorphization(
                    isScript, parentScope, instantiatedMethod, globalScope, codLibraries, dialect, typeArgumentsByName);

                functionBodies[instantiatedMethod] = body;
                diagnostics.AddRange(bodyDiagnostics);
            }

            // 4. 发射清单：过滤泛型定义（模板）+ 并入活实例化
            var builder = FilterDeclaredClasses(globalScope).ToBuilder();
            builder.AddRange(live);

            return builder.ToImmutable();
        }

        private static ImmutableArray<ClassTypeSymbol> FilterDeclaredClasses(BoundGlobalScope globalScope)
        {
            return globalScope.Classes.Where(c => !c.IsGenericDefinition).ToImmutableArray();
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

        /// <summary>
        /// 扫描泛型方法调用站点（6e-M20）：`Swap&lt;int&gt;(…)`——CallExpressionSyntax 的显式实参。
        /// 返回 (方法名 token, 实参子句列表) 对。
        /// </summary>
        private static IEnumerable<(SyntaxToken Identifier, ImmutableArray<TypeClauseSyntax> Arguments)> CollectGenericMethodUsages(BoundGlobalScope globalScope)
        {
            foreach (var root in CollectDeclarationRoots(globalScope))
            {
                foreach (var node in Walk(root))
                {
                    if (node is CallExpressionSyntax call && call.TypeArguments != null)
                    {
                        yield return (call.Identifier, call.TypeArguments.Arguments);
                    }
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
                        if (type?.ElementType != null && type.Kind == SymbolKind.Type)
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
