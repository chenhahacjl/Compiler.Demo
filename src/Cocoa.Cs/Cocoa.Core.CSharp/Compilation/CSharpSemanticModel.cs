using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.CSharp.Syntax;
using SSyntax = Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis
{
    /// <summary>
    /// C# 侧语义模型（P1-5 拆分：对齐 Roslyn <c>CSharpSemanticModel : SemanticModel</c>；经
    /// <see cref="Language.CreateSemanticModel"/> 分派）。P1 阶段仍以共享节点类型实现（保持行为不变），
    /// P2-5 切语言节点后改用 <c>Cocoa.CodeAnalysis.CSharp.Syntax</c> 节点。
    /// </summary>
    public sealed class CSharpSemanticModel : SemanticModel
    {
        internal CSharpSemanticModel(Compilation compilation, SSyntax.SyntaxTree syntaxTree)
            : base(compilation, syntaxTree)
        {
        }

        /// <summary>表达式类型：优先绑定树（任意表达式，含局部变量/参数/实例成员）；类型名节点回落名称解析。</summary>
        public override TypeSymbol? GetTypeInfo(SSyntax.SyntaxNode node)
        {
            if (node != null && BoundBySyntax.TryGetValue(node, out var bound) && bound is BoundExpression expression)
            {
                return expression.Type;
            }

            string? name = node switch
            {
                global::Cocoa.CodeAnalysis.CSharp.Syntax.NameExpressionSyntax nameExpression => nameExpression.IdentifierToken.Text,
                global::Cocoa.CodeAnalysis.CSharp.Syntax.TypeClauseSyntax typeClause => typeClause.Identifier.Text,
                null => null,
                _ => null,
            };

            if (name == null)
            {
                return null;
            }

            return ResolveBuiltin(name) ?? Compilation.GetTypeByMetadataName(name);
        }

        /// <summary>解析声明语法节点对应的符号：类/枚举 → 命名类型（类按声明引用精确匹配、枚举按名）；
        /// 顶层函数 → 函数符号；其余返回 null。</summary>
        public override Symbol? GetDeclaredSymbol(SSyntax.SyntaxNode declaration)
        {
            if (declaration is global::Cocoa.CodeAnalysis.CSharp.Syntax.FunctionDeclarationSyntax function)
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

            if (declaration is global::Cocoa.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax classDeclaration)
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

            if (declaration is global::Cocoa.CodeAnalysis.CSharp.Syntax.EnumDeclarationSyntax enumDeclaration)
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

        /// <summary>表达式对应符号：优先绑定树（局部变量/参数/实例成员等返回真实绑定符号）；未命中回落名称/成员解析。</summary>
        public override Symbol? GetSymbolInfo(SSyntax.SyntaxNode node)
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
                        // 属性/索引器读（getter 调用）→ 属性符号（对齐 Roslyn：obj.Prop / obj[i] 返回属性而非 getter）
                        return memberCallExpression.Method?.ContainingProperty != null
                            ? memberCallExpression.Method.ContainingProperty
                            : memberCallExpression.Method;
                    case BoundMemberAccessExpression memberAccessExpression:
                        return memberAccessExpression.Field;
                    case BoundThisExpression thisExpression:
                        return thisExpression.Type;
                    case BoundBaseExpression baseExpression:
                        return baseExpression.Type;
                }
            }

            return node switch
            {
                global::Cocoa.CodeAnalysis.CSharp.Syntax.NameExpressionSyntax nameExpression => ResolveName(nameExpression.IdentifierToken.Text),
                global::Cocoa.CodeAnalysis.CSharp.Syntax.CallExpressionSyntax callExpression => ResolveName(callExpression.Identifier.Text),
                global::Cocoa.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax memberAccess => ResolveMemberAccess(memberAccess.Expression, memberAccess.IdentifierToken.Text),
                global::Cocoa.CodeAnalysis.CSharp.Syntax.MemberCallExpressionSyntax memberCall => ResolveMemberAccess(memberCall.Expression, memberCall.IdentifierToken.Text),
                null => null,
                _ => null,
            };
        }

        /// <summary>成员解析：接收者解析为类型（静态成员，如 <c>Utils.Twice</c>）→ 返回成员符号；
        /// 实例接收者/嵌套命名空间（如 System.Math.Max）暂不支持。</summary>
        private Symbol? ResolveMemberAccess(global::Cocoa.CodeAnalysis.CSharp.Syntax.ExpressionSyntax receiver, string memberName)
        {
            if (ResolveReceiverType(receiver) is not NamedTypeSymbol receiverType)
            {
                return null;
            }

            return receiverType.GetMethod(memberName)
                ?? (Symbol?)receiverType.GetField(memberName)
                ?? receiverType.GetProperty(memberName);
        }

        private TypeSymbol? ResolveReceiverType(global::Cocoa.CodeAnalysis.CSharp.Syntax.ExpressionSyntax receiver)
        {
            return receiver switch
            {
                global::Cocoa.CodeAnalysis.CSharp.Syntax.NameExpressionSyntax name => ResolveName(name.IdentifierToken.Text) as TypeSymbol,
                global::Cocoa.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax nested => ResolveReceiverType(nested.Expression) is NamedTypeSymbol parent
                    ? parent.GetMethod(nested.IdentifierToken.Text)?.ReturnType
                    : null,
                _ => null,
            };
        }
    }
}
