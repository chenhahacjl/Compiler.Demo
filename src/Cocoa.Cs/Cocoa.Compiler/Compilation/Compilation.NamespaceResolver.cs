using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Serialization;
using Cocoa.CodeAnalysis.Evaluation;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Text;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis
{
    /// <summary>
    /// 命名空间与类型解析（4.2 自 Compilation.cs 拆出，partial 分文件）：全局命名空间树构建、GetTypeByMetadataName/GetNamespace 等查询。
    /// </summary>
    public abstract partial class Compilation
    {
        /// <summary>按名称枚举符号（对齐 Roslyn <c>Compilation.GetSymbolsWithName</c>）：
        /// 命名类型 + 顶层函数（经全局命名空间树）+ 全局变量；去重。</summary>
        public IEnumerable<Symbol> GetSymbolsWithName(string name)
        {
            var seen = new HashSet<Symbol>(ReferenceEqualityComparer.Instance);
            foreach (var ns in EnumerateNamespaces(GlobalNamespace))
            {
                foreach (var type in ns.GetTypeMembers())
                {
                    if (type.Name == name && seen.Add(type))
                    {
                        yield return type;
                    }
                }

                foreach (var function in ns.GetFunctionMembers())
                {
                    if (function.Name == name && seen.Add(function))
                    {
                        yield return function;
                    }
                }
            }

            foreach (var variable in Variables)
            {
                if (variable.Name == name && seen.Add(variable))
                {
                    yield return variable;
                }
            }
        }

        private static IEnumerable<NamespaceSymbol> EnumerateNamespaces(NamespaceSymbol root)
        {
            yield return root;
            foreach (var child in root.GetNamespaceMembers())
            {
                foreach (var nested in EnumerateNamespaces(child))
                {
                    yield return nested;
                }
            }
        }

        /// <summary>按元数据全名解析类型（对齐 Roslyn <c>CSharpCompilation.GetTypeByMetadataName</c>）。
        /// 内建特殊类型（基元/Object/Type/String/Void）优先，其次全局命名空间树（源 + 注入的 .coa 库）类/枚举/
        /// 泛型定义。支持后置 [] 数组全名、泛型定义（<c>"...List`1"</c>）与实例化 mangle（<c>"...List`1#System.Int32"</c>）。</summary>
        public TypeSymbol? GetTypeByMetadataName(string fullyQualifiedName)
        {
            var elementName = fullyQualifiedName;
            var isArray = false;
            if (fullyQualifiedName.EndsWith("[]", StringComparison.Ordinal))
            {
                isArray = true;
                elementName = fullyQualifiedName.Substring(0, fullyQualifiedName.Length - 2);
            }

            TypeSymbol? type = elementName switch
            {
                "System.Object" => NamedTypeSymbol.SystemObject,
                "System.Type" => NamedTypeSymbol.SystemType,
                "System.String" => TypeSymbol.String,
                "System.Void" => TypeSymbol.Void,
                "System.Boolean" => TypeSymbol.Boolean,
                "System.SByte" => TypeSymbol.Int8,
                "System.Byte" => TypeSymbol.UInt8,
                "System.Int16" => TypeSymbol.Int16,
                "System.UInt16" => TypeSymbol.UInt16,
                "System.Int32" => TypeSymbol.Int32,
                "System.UInt32" => TypeSymbol.UInt32,
                "System.Int64" => TypeSymbol.Int64,
                "System.UInt64" => TypeSymbol.UInt64,
                "System.Single" => TypeSymbol.Float,
                "System.Double" => TypeSymbol.Double,
                "System.Char" => TypeSymbol.Char,
                _ => null,
            };

            if (type == null)
            {
                type = ResolveNamedTypeByMetadataName(elementName);
            }

            return isArray && type != null ? TypeSymbol.ArrayOf(type) : type;
        }

        /// <summary>命名类型解析：泛型定义（<c>名称`元数</c>）/ 实例化（<c>名称`元数#实参$实参</c>）或普通声明类型。</summary>
        private TypeSymbol? ResolveNamedTypeByMetadataName(string elementName)
        {
            var backtick = elementName.IndexOf('`');
            if (backtick >= 0)
            {
                var baseName = elementName.Substring(0, backtick);
                var rest = elementName.Substring(backtick + 1);
                var hash = rest.IndexOf('#');
                var arityText = hash < 0 ? rest : rest.Substring(0, hash);
                if (int.TryParse(arityText, out var arity) && arity > 0)
                {
                    if (ResolveDeclaredType(baseName) is NamedTypeSymbol definition &&
                        definition.IsGenericDefinition && definition.TypeParameters.Length == arity)
                    {
                        if (hash < 0)
                        {
                            return definition;
                        }

                        var argsText = rest.Substring(hash + 1);
                        if (argsText.Length == 0)
                        {
                            return null;
                        }

                        var argumentNames = argsText.Split('$');
                        var arguments = ImmutableArray.CreateBuilder<TypeSymbol>(argumentNames.Length);
                        foreach (var argumentName in argumentNames)
                        {
                            if (GetTypeByMetadataName(argumentName) is not { } argumentType)
                            {
                                return null;
                            }

                            arguments.Add(argumentType);
                        }

                        return GenericTypeInstantiator.Instantiate(definition, arguments.ToImmutable());
                    }
                }

                return null;
            }

            return ResolveDeclaredType(elementName);
        }

        /// <summary>经全局命名空间树按「命名空间.简单名」定位声明类型。</summary>
        private TypeSymbol? ResolveDeclaredType(string elementName)
        {
            return GlobalNamespace.TryGetType(elementName);
        }

        private NamespaceSymbol? _globalNamespace;

        /// <summary>全局命名空间根（对齐 Roslyn <c>Compilation.GlobalNamespace</c>）：包含子命名空间与
        /// 全部已声明的命名类型（源 + 注入的 .coa 库；按符号的 <see cref="NamedTypeSymbol.Namespace"/> 归组）。</summary>
        public NamespaceSymbol GlobalNamespace
        {
            get
            {
                var global = _globalNamespace;
                if (global != null)
                {
                    return global;
                }

                var tree = NamespaceSymbol.CreateGlobal();
                AddTypesToNamespace(tree, GlobalScope.Enums);
                AddTypesToNamespace(tree, GlobalScope.Classes);
                AddTypesToNamespace(tree, _codLibraries.SelectMany(l => l.Enums));
                AddTypesToNamespace(tree, _codLibraries.SelectMany(l => l.Classes));
                AddTypesToNamespace(tree, _codLibraries.SelectMany(l => l.GenericDefinitions));
                AddFunctionsToNamespace(tree, GlobalScope.Functions.Where(f => f.ContainingClass == null));
                AddFunctionsToNamespace(tree, _codLibraries.SelectMany(l => l.Functions).Where(f => f.ContainingClass == null));
                Interlocked.CompareExchange(ref _globalNamespace, tree, null);
                // CAS 后重读（与 SourceAssembly 同模式）
                return _globalNamespace!;
            }
        }

        /// <summary>按点分全名解析命名空间符号（全局根取 ""；未命中返回 null）。</summary>
        public NamespaceSymbol? GetNamespace(string fullName)
        {
            return GlobalNamespace.GetNamespace(fullName ?? "");
        }

        private static void AddTypesToNamespace(NamespaceSymbol root, IEnumerable<NamedTypeSymbol> types)
        {
            foreach (var type in types)
            {
                var ns = NamespaceSymbol.GetOrCreateNamespace(root, type.Namespace);
                ns.AddTypeMember(type);
            }
        }

        private static void AddFunctionsToNamespace(NamespaceSymbol root, IEnumerable<FunctionSymbol> functions)
        {
            foreach (var function in functions)
            {
                var ns = NamespaceSymbol.GetOrCreateNamespace(root, function.Namespace);
                ns.AddFunctionMember(function);
            }
        }

        private ImmutableArray<string> CollectNamespaceNames()
        {
            var names = new List<string>();
            foreach (var tree in SyntaxTrees)
            {
                names.AddRange(tree.Language.GetDeclaredNamespaceNames(tree));
            }

            return names.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToImmutableArray();
        }
    }
}
