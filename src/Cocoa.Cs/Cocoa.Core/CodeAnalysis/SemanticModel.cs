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

        /// <summary>解析名称表达式对应的符号（对齐 Roslyn <c>SemanticModel.GetSymbolInfo</c>）：
        /// 按名解析——类型（关键字/元数据全名）优先，其次全局变量，其次函数；其余/null 返回 null。
        /// 支持 NameExpression（变量/类型用法）与 CallExpression（函数调用，名字存于 Identifier token）。
        /// 基于编译级解析（非逐节点绑定），局部变量/成员访问等暂不支持。</summary>
        public Symbol? GetSymbolInfo(SyntaxNode node)
        {
            return node switch
            {
                NameExpressionSyntax nameExpression => ResolveName(nameExpression.IdentifierToken.Text),
                CallExpressionSyntax callExpression => ResolveName(callExpression.Identifier.Text),
                MemberAccessExpressionSyntax memberAccess => ResolveMemberAccess(memberAccess.Expression, memberAccess.IdentifierToken.Text),
                MemberCallExpressionSyntax memberCall => ResolveMemberAccess(memberCall.Expression, memberCall.IdentifierToken.Text),
                null => null,
                _ => null,
            };
        }

        /// <summary>成员解析：接收者解析为类型（静态成员，如 <c>Utils.Twice</c>）→ 返回成员符号；
        /// 实例接收者/嵌套命名空间（如 System.Math.Max）暂不支持。</summary>
        private Symbol? ResolveMemberAccess(ExpressionSyntax receiver, string memberName)
        {
            if (ResolveReceiverType(receiver) is not NamedTypeSymbol receiverType)
            {
                return null;
            }

            return receiverType.GetMethod(memberName)
                ?? (Symbol?)receiverType.GetField(memberName)
                ?? receiverType.GetProperty(memberName);
        }

        private TypeSymbol? ResolveReceiverType(ExpressionSyntax receiver)
        {
            return receiver switch
            {
                NameExpressionSyntax name => ResolveName(name.IdentifierToken.Text) as TypeSymbol,
                MemberAccessExpressionSyntax nested => ResolveReceiverType(nested.Expression) is NamedTypeSymbol parent
                    ? parent.GetMethod(nested.IdentifierToken.Text)?.ReturnType
                    : null,
                _ => null,
            };
        }

        private Symbol? ResolveName(string text)
        {
            if (ResolveBuiltin(text) is { } builtin)
            {
                return builtin;
            }

            if (_compilation.GetTypeByMetadataName(text) is { } type)
            {
                return type;
            }

            foreach (var variable in _compilation.Variables)
            {
                if (variable.Name == text)
                {
                    return variable;
                }
            }

            foreach (var function in _compilation.Functions)
            {
                if (function.Name == text)
                {
                    return function;
                }
            }

            return null;
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