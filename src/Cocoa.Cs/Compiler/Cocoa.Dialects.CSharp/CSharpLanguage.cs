using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.CSharp.Syntax;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis
{
    /// <summary>
    /// C# 方言语言（M2 设计 X）：位于独立程序集 Cocoa.CodeAnalysis.CSharp，核心零改动即可挂载。
    /// 内建类型原名映射（int/long/short/.../float/double；与 CO 的简写表（Cocoa.CodeAnalysis.Cocoa 的
    /// CocoaLanguage）解耦为两套词汇，同一 TypeSymbol）。实例经 <see cref="Language"/> 注册表暴露（"csharp"），
    /// 由 <see cref="Syntax.SyntaxTree.Load"/>（.cs 扩展名）/ <c>ParseCs</c> 消费。
    /// </summary>
    public sealed class CSharpLanguage : Language
    {
        public static readonly CSharpLanguage Instance = new CSharpLanguage();

        private CSharpLanguage()
            : base("csharp")
        {
        }

        /// <summary>`.cs` 参数为类型前置 `int x`（参数绿往返源序化依据）。</summary>
        public override bool ParametersAreTypeFirst => true;

        /// <summary>
        /// 关键字识别（P1-A 词法分家）：C# 表 = 共享全表减去 CO 独占词。
        /// CO 独占关键字（function/let/property/constructor/extends/facade/syscall/import/to/step/cdecl/stdcall）
        /// 在 `.cs` 中回落为 <see cref="SyntaxKind.IdentifierToken"/>（文档 Phase 3：CO 词在 C# 可作标识符，反之亦然）。
        /// </summary>
        public override SyntaxKind GetKeywordKind(string text)
        {
            var kind = base.GetKeywordKind(text);
            return SyntaxKindLanguageOwnership.Ownership(kind) == SyntaxLanguageOwnership.CocoaOnly
                ? SyntaxKind.IdentifierToken
                : kind;
        }

        protected override TypeSymbol? LookupSpecificBuiltinType(string name) => name switch
        {
            "int" => TypeSymbol.Int32,
            "long" => TypeSymbol.Int64,
            "short" => TypeSymbol.Int16,
            "ushort" => TypeSymbol.UInt16,
            "uint" => TypeSymbol.UInt32,
            "ulong" => TypeSymbol.UInt64,
            "sbyte" => TypeSymbol.Int8,
            "byte" => TypeSymbol.UInt8,
            "float" => TypeSymbol.Float,
            "double" => TypeSymbol.Double,
            _ => null,
        };

        public override IParser CreateParser(SyntaxTree syntaxTree) => new CSharpParser(syntaxTree);

        public override IParser CreateParser(SyntaxTree syntaxTree, ImmutableArray<SyntaxToken> tokens)
            => new CSharpParser(syntaxTree, tokens);

        /// <summary>C# 词法分析器（S-2 Lexer 分家：C# 专属词法逻辑随语言库落位）。</summary>
        public override ILexer CreateLexer(SyntaxTree syntaxTree)
            => new CSharpLexer(syntaxTree);

        public override ILexer CreateLexer(SyntaxTree syntaxTree, int start)
            => new CSharpLexer(syntaxTree, start);

        /// <summary>C# 编译对象（S-4.2 Compilation 分家：CSharpCompilation 随语言库落位）。</summary>
        public override Compilation CreateCompilation(bool isScript, Compilation? previous, string entryPointName, string[]? references, bool linkCodDynamically, SyntaxTree[] syntaxTrees)
            => new CSharpCompilation(isScript, previous, entryPointName, references, linkCodDynamically, syntaxTrees);

        /// <summary>C# 绑定器（S-4.3c 分派：返回语言库独立副本，Core 经 <see cref="IBinder"/> 窄接口消费）。</summary>
        public override IBinder CreateBinder(bool isScript, Binding.BoundScope? parent, Symbols.FunctionSymbol? function, System.Collections.Immutable.ImmutableArray<string> references, System.Collections.Immutable.ImmutableArray<string> usingNamespaces, Func<string, TypeSymbol?> builtinTypeResolver, System.Collections.Immutable.ImmutableArray<string> usingStatics = default, System.Collections.Immutable.ImmutableDictionary<string, string> usingAliases = null!, System.Collections.Immutable.ImmutableArray<global::Cocoa.CodeAnalysis.Serialization.CoaProgram> codLibraries = default, Symbols.NamespaceSymbol? globalNamespace = null)
            => new global::Cocoa.CodeAnalysis.CSharp.Binding.CSharpBinder(isScript, parent, function, references, usingNamespaces, builtinTypeResolver, usingStatics, usingAliases, codLibraries, globalNamespace);

        public override (Binding.BoundBlockStatement Body, ImmutableArray<Diagnostic> Diagnostics) BuildFunctionBodyForMonomorphization(bool isScript, Binding.BoundScope parentScope, Symbols.FunctionSymbol function, Binding.BoundGlobalScope globalScope, System.Collections.Immutable.ImmutableArray<global::Cocoa.CodeAnalysis.Serialization.CoaProgram> codLibraries, Dictionary<string, TypeSymbol> typeArgumentsByName)
            => global::Cocoa.CodeAnalysis.CSharp.Binding.CSharpBinder.BuildFunctionBodyForMonomorphization(isScript, parentScope, function, globalScope, codLibraries, this, typeArgumentsByName);

        /// <summary>绿→类型化红节点（P2-4：语言库各自持有一份构建器）。</summary>
        public override SyntaxNode CreateTypedRed(GreenNode green, SyntaxTree syntaxTree, int position)
            => new global::Cocoa.CodeAnalysis.CSharp.Syntax.CSharpGreenNodeFactory(green).CreateTypedRed(syntaxTree, position);

        /// <summary>泛型用法扫描（P2-5：语言库按语言节点扫描）。</summary>
        public override System.Collections.Generic.IEnumerable<(SyntaxToken Identifier, System.Collections.Immutable.ImmutableArray<SyntaxNode> Arguments)> CollectGenericUsages(Binding.BoundGlobalScope globalScope)
        {
            foreach (var root in Binding.Monomorphizer.CollectDeclarationRoots(globalScope))
            {
                foreach (var node in Binding.Monomorphizer.Walk(root))
                {
                    if (node is global::Cocoa.CodeAnalysis.CSharp.Syntax.GenericTypeClauseSyntax genericClause)
                    {
                        yield return (genericClause.Identifier, genericClause.TypeArguments.Cast<SyntaxNode>().ToImmutableArray());
                    }
                    else if (node is global::Cocoa.CodeAnalysis.CSharp.Syntax.ObjectCreationExpressionSyntax creation && creation.TypeArguments != null)
                    {
                        yield return (creation.Identifier, creation.TypeArguments.Arguments.Cast<SyntaxNode>().ToImmutableArray());
                    }
                }
            }
        }

        /// <summary>声明的命名空间名集合（P2-5：语言库按语言节点）。</summary>
        public override System.Collections.Immutable.ImmutableArray<string> GetDeclaredNamespaceNames(SyntaxTree syntaxTree)
        {
            var names = new System.Collections.Generic.List<string>();
            CollectNamespaceNames(((global::Cocoa.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax)syntaxTree.Root).Members, names);
            return names.ToImmutableArray();
        }

        private static void CollectNamespaceNames(System.Collections.Immutable.ImmutableArray<global::Cocoa.CodeAnalysis.CSharp.Syntax.MemberSyntax> members, System.Collections.Generic.List<string> names)
        {
            foreach (var member in members)
            {
                if (member is global::Cocoa.CodeAnalysis.CSharp.Syntax.NamespaceDeclarationSyntax ns)
                {
                    names.Add(ns.Name);
                    CollectNamespaceNames(ns.Members, names);
                }
            }
        }

        /// <summary>根成员集合（P2-5：语言库按语言节点）。</summary>
        public override System.Collections.Immutable.ImmutableArray<SyntaxNode> GetRootMembers(SyntaxTree syntaxTree)
            => ((global::Cocoa.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax)syntaxTree.Root).Members.Cast<SyntaxNode>().ToImmutableArray();

        /// <summary>语义模型（P1-5：返回语言专属 <see cref="CSharpSemanticModel"/>）。</summary>
        public override SemanticModel CreateSemanticModel(Compilation compilation, SyntaxTree syntaxTree)
            => new CSharpSemanticModel(compilation, syntaxTree);

        /// <summary>不可达代码位置（P2-5：语言库按语言节点解析）。</summary>
        public override TextLocation? GetUnreachableCodeLocation(SyntaxNode node)
        {
            var kind = (node as global::Cocoa.CodeAnalysis.CSharp.Syntax.CSharpSyntaxNode)?.Kind;
            switch (kind)
            {
                case CSharpSyntaxKind.BlockStatement:
                {
                    var firstStatement = ((global::Cocoa.CodeAnalysis.CSharp.Syntax.BlockStatementSyntax)node).Statements.FirstOrDefault();
                    return firstStatement == null ? null : GetUnreachableCodeLocation(firstStatement);
                }
                case CSharpSyntaxKind.VariableDeclaration:
                {
                    var variableDeclaration = (global::Cocoa.CodeAnalysis.CSharp.Syntax.VariableDeclarationSyntax)node;
                    return variableDeclaration.Keyword?.Location ?? variableDeclaration.Location;
                }
                case CSharpSyntaxKind.IfStatement:
                    return ((global::Cocoa.CodeAnalysis.CSharp.Syntax.IfStatementSyntax)node).Keyword.Location;
                case CSharpSyntaxKind.WhileStatement:
                    return ((global::Cocoa.CodeAnalysis.CSharp.Syntax.WhileStatementSyntax)node).Keyword.Location;
                case CSharpSyntaxKind.DoWhileStatement:
                    return ((global::Cocoa.CodeAnalysis.CSharp.Syntax.DoWhileStatementSyntax)node).DoKeyword.Location;
                case CSharpSyntaxKind.ForStatement:
                    return ((global::Cocoa.CodeAnalysis.CSharp.Syntax.ForStatementSyntax)node).Keyword.Location;
                case CSharpSyntaxKind.ForeachStatement:
                    return ((global::Cocoa.CodeAnalysis.CSharp.Syntax.ForeachStatementSyntax)node).Keyword.Location;
                case CSharpSyntaxKind.SwitchStatement:
                    return ((global::Cocoa.CodeAnalysis.CSharp.Syntax.SwitchStatementSyntax)node).Keyword.Location;
                case CSharpSyntaxKind.BreakStatement:
                    return ((global::Cocoa.CodeAnalysis.CSharp.Syntax.BreakStatementSyntax)node).Keyword.Location;
                case CSharpSyntaxKind.ContinueStatement:
                    return ((global::Cocoa.CodeAnalysis.CSharp.Syntax.ContinueStatementSyntax)node).Keyword.Location;
                case CSharpSyntaxKind.ReturnStatement:
                    return ((global::Cocoa.CodeAnalysis.CSharp.Syntax.ReturnStatementSyntax)node).Keyword.Location;
                case CSharpSyntaxKind.ExpressionStatement:
                    return GetUnreachableCodeLocation(((global::Cocoa.CodeAnalysis.CSharp.Syntax.ExpressionStatementSyntax)node).Expression);
                case CSharpSyntaxKind.CallExpression:
                    return ((global::Cocoa.CodeAnalysis.CSharp.Syntax.CallExpressionSyntax)node).Identifier.Location;
                case CSharpSyntaxKind.MemberCallExpression:
                    return ((global::Cocoa.CodeAnalysis.CSharp.Syntax.MemberCallExpressionSyntax)node).IdentifierToken.Location;
                default:
                    throw new Exception($"Unexpected syntax {node.Kind}");
            }
        }

        /// <summary>声明名 token 位置（P2-6 钩子：语言库按语言节点）。</summary>
        public override TextLocation? GetDeclarationNameLocation(SyntaxNode? declaration)
        {
            if (declaration is global::Cocoa.CodeAnalysis.CSharp.Syntax.FunctionDeclarationSyntax fn)
                return fn.Identifier.Location;
            if (declaration is global::Cocoa.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax cls)
                return cls.Identifier.Location;
            return declaration?.Location;
        }

        /// <summary>类声明是否带 facade 修饰符（P2-6 钩子：语言库按语言节点）。</summary>
        public override bool HasDeclaredFacadeModifier(SyntaxNode? declaration)
            => declaration is global::Cocoa.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax cls
                && cls.Modifiers.Any(m => m.Kind == SyntaxKind.FacadeKeyword);
    }
}