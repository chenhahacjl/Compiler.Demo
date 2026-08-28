using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis
{
    /// <summary>
    /// 语义模型（对齐 Roslyn <see cref="Microsoft.CodeAnalysis.SemanticModel"/>）。
    /// 基于绑定树（经 <see cref="Compilation"/> 惰性绑定全部函数体，Syntax→BoundNode 映射）提供
    /// <c>GetTypeInfo</c>/<c>GetSymbolInfo</c>：任意函数体内的表达式（含局部变量/参数/实例成员）返回真实绑定结果；
    /// 未命中（如类型名、声明节点）回落名称/声明解析。
    /// </summary>
    public sealed class SemanticModel
    {
        private readonly Compilation _compilation;
        private readonly SyntaxTree _syntaxTree;

        private Dictionary<SyntaxNode, BoundNode>? _boundBySyntax;

        internal SemanticModel(Compilation compilation, SyntaxTree syntaxTree)
        {
            _compilation = compilation;
            _syntaxTree = syntaxTree;
        }

        /// <summary>所属编译。</summary>
        public Compilation Compilation => _compilation;

        /// <summary>所属语法树。</summary>
        public SyntaxTree SyntaxTree => _syntaxTree;

        /// <summary>惰性绑定全部函数体并建 Syntax→BoundNode 映射（A-1/A-2：实例成员/局部变量/参数解析）。</summary>
        private Dictionary<SyntaxNode, BoundNode> BoundBySyntax
        {
            get
            {
                if (_boundBySyntax != null)
                {
                    return _boundBySyntax;
                }

                var program = Binder.BindProgram(
                    _compilation.IsScript,
                    null,
                    _compilation.GlobalScope,
                    _compilation.CodLibraries,
                    _syntaxTree.Dialect,
                    false,
                    _compilation.GlobalNamespace);

                var map = new Dictionary<SyntaxNode, BoundNode>();
                var collector = new BoundNodeCollector(map);
                foreach (var body in program.Functions.Values)
                {
                    collector.Walk(body);
                }

                _boundBySyntax = map;
                return map;
            }
        }

        private sealed class BoundNodeCollector : BoundTreeWalker
        {
            private readonly Dictionary<SyntaxNode, BoundNode> _map;

            public BoundNodeCollector(Dictionary<SyntaxNode, BoundNode> map)
            {
                _map = map;
            }

            protected override void VisitStatement(BoundStatement node)
            {
                Record(node);
            }

            protected override void VisitExpression(BoundExpression node)
            {
                Record(node);
            }

            private void Record(BoundNode node)
            {
                if (node.Syntax != null)
                {
                    _map[node.Syntax] = node;
                }
            }
        }

        /// <summary>表达式类型：优先绑定树（任意表达式，含局部变量/参数/实例成员）；类型名节点回落名称解析。</summary>
        public TypeSymbol? GetTypeInfo(SyntaxNode node)
        {
            if (node != null && BoundBySyntax.TryGetValue(node, out var bound) && bound is BoundExpression expression)
            {
                return expression.Type;
            }

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

        /// <summary>绑定树操作（对齐 Roslyn <c>SemanticModel.GetOperation</c>）：返回语法节点对应的绑定节点。
        /// <see cref="BoundNode"/> 与 <see cref="BoundNodeKind"/> 已公开；具体节点类仍 internal，
        /// 调用方可经 <see cref="BoundNode.Kind"/>/<see cref="BoundNode.Syntax"/> 检查。</summary>
        public BoundNode? GetOperation(SyntaxNode node)
        {
            if (node == null)
            {
                return null;
            }

            return BoundBySyntax.TryGetValue(node, out var bound) ? bound : null;
        }

        /// <summary>表达式对应符号（对齐 Roslyn <c>SemanticModel.GetSymbolInfo</c>）。
        /// 优先绑定树（局部变量/参数/实例成员等返回真实绑定符号）；未命中回落名称/成员解析。</summary>
        public Symbol? GetSymbolInfo(SyntaxNode node)
        {
            if (node != null && BoundBySyntax.TryGetValue(node, out var bound))
            {
                switch (bound)
                {
                    case BoundVariableExpression variableExpression:
                        return variableExpression.Variable;
                    case BoundCallExpression callExpression:
                        return callExpression.Function;
                    case BoundMemberCallExpression memberCallExpression:
                        return memberCallExpression.Method;
                    case BoundMemberAccessExpression memberAccessExpression:
                        return memberAccessExpression.Field;
                }
            }

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