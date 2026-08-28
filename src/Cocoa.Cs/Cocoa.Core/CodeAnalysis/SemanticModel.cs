using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis
{
    /// <summary>
    /// 语义模型（Phase 2 起点，对齐 Roslyn <see cref="Microsoft.CodeAnalysis.SemanticModel"/>）。
    /// 当前提供「类型名语法节点」的符号解析：关键字基元（CO 方言 + C# 原名）直接映射，
    /// 其余经 <see cref="Compilation.GetTypeByMetadataName"/> 走全局命名空间树（源 + .cod 库）。
    /// 完整 <c>GetSymbolInfo</c>/<c>GetOperation</c> 属后续里程碑。
    /// </summary>
    public sealed class SemanticModel
    {
        private readonly Compilation _compilation;
        private readonly SyntaxTree _syntaxTree;

        internal SemanticModel(Compilation compilation, SyntaxTree syntaxTree)
        {
            _compilation = compilation;
            _syntaxTree = syntaxTree;
        }

        /// <summary>所属编译。</summary>
        public Compilation Compilation => _compilation;

        /// <summary>所属语法树。</summary>
        public SyntaxTree SyntaxTree => _syntaxTree;

        /// <summary>解析类型名语法节点（<see cref="NameExpressionSyntax"/> / <see cref="TypeClauseSyntax"/>）对应的类型符号；
        /// 其它形状/null 返回 null。</summary>
        public TypeSymbol? GetTypeInfo(SyntaxNode node)
        {
            string? name = node switch
            {
                NameExpressionSyntax nameExpression => nameExpression.IdentifierToken.Text,
                TypeClauseSyntax typeClause => typeClause.Identifier.Text,
                null => null,
                _ => null,
            };

            if (name == null)
            {
                return null;
            }

            return ResolveBuiltin(name) ?? _compilation.GetTypeByMetadataName(name);
        }

        /// <summary>解析声明语法节点对应的符号（对齐 Roslyn <c>SemanticModel.GetDeclaredSymbol</c>）：
        /// 类/枚举 → 命名类型（类按声明引用精确匹配、枚举按名）；顶层函数 → 函数符号；其余返回 null。
        /// 嵌套类型/构造器等不在全局命名空间树，暂不支持。</summary>
        public Symbol? GetDeclaredSymbol(SyntaxNode declaration)
        {
            if (declaration is FunctionDeclarationSyntax function)
            {
                foreach (var ns in EnumerateNamespaces(GlobalNamespace))
                {
                    foreach (var member in ns.GetFunctionMembers())
                    {
                        if (ReferenceEquals(member.Declaration, function))
                        {
                            return member;
                        }
                    }
                }

                return null;
            }

            if (declaration is ClassDeclarationSyntax classDeclaration)
            {
                foreach (var ns in EnumerateNamespaces(GlobalNamespace))
                {
                    foreach (var member in ns.GetTypeMembers())
                    {
                        if (member is NamedTypeSymbol named && ReferenceEquals(named.Declaration, classDeclaration))
                        {
                            return named;
                        }
                    }
                }

                return null;
            }

            if (declaration is EnumDeclarationSyntax enumDeclaration)
            {
                foreach (var ns in EnumerateNamespaces(GlobalNamespace))
                {
                    foreach (var member in ns.GetTypeMembers())
                    {
                        if (member is NamedTypeSymbol { TypeKind: TypeKind.Enum } named && named.Name == enumDeclaration.Identifier.Text)
                        {
                            return named;
                        }
                    }
                }

                return null;
            }

            return null;
        }

        private NamespaceSymbol GlobalNamespace => _compilation.GlobalNamespace;

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

        private static TypeSymbol? ResolveBuiltin(string name)
        {
            return name switch
            {
                "i8" => TypeSymbol.Int8,
                "sbyte" => TypeSymbol.Int8,
                "u8" => TypeSymbol.UInt8,
                "byte" => TypeSymbol.UInt8,
                "i16" => TypeSymbol.Int16,
                "short" => TypeSymbol.Int16,
                "u16" => TypeSymbol.UInt16,
                "ushort" => TypeSymbol.UInt16,
                "i32" => TypeSymbol.Int32,
                "int" => TypeSymbol.Int32,
                "u32" => TypeSymbol.UInt32,
                "uint" => TypeSymbol.UInt32,
                "i64" => TypeSymbol.Int64,
                "long" => TypeSymbol.Int64,
                "u64" => TypeSymbol.UInt64,
                "ulong" => TypeSymbol.UInt64,
                "f32" => TypeSymbol.Float,
                "float" => TypeSymbol.Float,
                "f64" => TypeSymbol.Double,
                "double" => TypeSymbol.Double,
                "i128" => TypeSymbol.Int128,
                "u128" => TypeSymbol.UInt128,
                "f128" => TypeSymbol.Float128,
                "bool" => TypeSymbol.Boolean,
                "char" => TypeSymbol.Char,
                "string" => TypeSymbol.String,
                "void" => TypeSymbol.Void,
                "any" => TypeSymbol.Any,
                _ => null,
            };
        }
    }
}