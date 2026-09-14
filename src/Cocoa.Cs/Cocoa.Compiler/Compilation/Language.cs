using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis
{
    /// <summary>
    /// 语言（M2 设计 X）。去 C# 方言 + Cocoa.Dialects.Cocoa 并入本程序集（2026-09-13）后，
    /// 语言中间层塌缩为本具体类（Cocoa 宿主语言单一实现），无 dialects 差异分派。
    /// 内建类型简写 i8/u8/i16/u16/i32/u32/i64/u64/f32/f64（+ i128/u128/f128 占位）。
    /// </summary>
    public sealed class Language
    {
        /// <summary>单例（Cocoa 宿主语言，默认 `.co`）。</summary>
        public readonly static Language Instance = new Language();

        private Language()
        {
        }

        /// <summary>Cocoa 宿主语言（默认，`.co`；等价 <see cref="Instance"/>）。</summary>
        public static Language Cocoa => Instance;

        /// <summary>内建类型名（any/bool/char/string/void 共享 + CO 简写词汇）。</summary>
        public TypeSymbol? LookupBuiltinType(string name) => name switch
        {
            "any" => TypeSymbol.Any,
            "bool" => TypeSymbol.Boolean,
            "char" => TypeSymbol.Char,
            "string" => TypeSymbol.String,
            "void" => TypeSymbol.Void,
            _ => LookupSpecificBuiltinType(name),
        };

        private TypeSymbol? LookupSpecificBuiltinType(string name) => name switch
        {
            "i8" => TypeSymbol.Int8,
            "u8" => TypeSymbol.UInt8,
            "i16" => TypeSymbol.Int16,
            "u16" => TypeSymbol.UInt16,
            "i32" => TypeSymbol.Int32,
            "u32" => TypeSymbol.UInt32,
            "i64" => TypeSymbol.Int64,
            "u64" => TypeSymbol.UInt64,
            "f32" => TypeSymbol.Float,
            "f64" => TypeSymbol.Double,
            "i128" => TypeSymbol.Int128,
            "u128" => TypeSymbol.UInt128,
            "f128" => TypeSymbol.Float128,
            "nint" => TypeSymbol.NativeInt32,
            "nuint" => TypeSymbol.NativeUInt32,
            _ => null,
        };

        /// <summary>关键字识别（P1-A）：文本 → 关键字 kind，未命中返回 <see cref="SyntaxKind.IdentifierToken"/>。</summary>
        public SyntaxKind GetKeywordKind(string text)
        {
            return SyntaxFacts.GetKeywordKind(text);
        }

        /// <summary>创建解析器（完整树）。</summary>
        internal CocoaParser CreateParser(SyntaxTree syntaxTree) => new CocoaParser(syntaxTree);

        /// <summary>创建解析器（预词法 token，插值洞子解析用）。</summary>
        internal CocoaParser CreateParser(SyntaxTree syntaxTree, ImmutableArray<SyntaxToken> tokens)
            => new CocoaParser(syntaxTree, tokens);

        /// <summary>词法分析器。</summary>
        internal LexerBase CreateLexer(SyntaxTree syntaxTree)
            => new CocoaLexer(syntaxTree);

        /// <summary>从指定位置开始词法（插值洞子解析，位置须指向洞首）。</summary>
        internal LexerBase CreateLexer(SyntaxTree syntaxTree, int start)
            => new CocoaLexer(syntaxTree, start);

        /// <summary>绑定器工厂（Monomorphizer 经此创建绑定器）。</summary>
        public IBinder CreateBinder(bool isScript, Binding.BoundScope? parent, Symbols.FunctionSymbol? function, ImmutableArray<string> references, ImmutableArray<string> usingNamespaces, Func<string, Symbols.TypeSymbol?> builtinTypeResolver, ImmutableArray<string> usingStatics = default, ImmutableDictionary<string, string> usingAliases = null!, ImmutableArray<Serialization.CoaProgram> codLibraries = default, Symbols.NamespaceSymbol? globalNamespace = null)
            => new CocoaBinder(isScript, parent, function, references, usingNamespaces, builtinTypeResolver, usingStatics, usingAliases, codLibraries, globalNamespace);

        /// <summary>单态化重绑函数体。<see cref="Binder.Monomorphizer"/> 经此调用。</summary>
        public (Binding.BoundBlockStatement Body, ImmutableArray<Diagnostic> Diagnostics) BuildFunctionBodyForMonomorphization(bool isScript, Binding.BoundScope parentScope, Symbols.FunctionSymbol function, Binding.BoundGlobalScope globalScope, ImmutableArray<Serialization.CoaProgram> codLibraries, Dictionary<string, Symbols.TypeSymbol> typeArgumentsByName)
            => CocoaBinder.BuildFunctionBodyForMonomorphization(isScript, parentScope, function, globalScope, codLibraries, this, typeArgumentsByName);

        /// <summary>绿→类型化红节点。</summary>
        public SyntaxNode CreateTypedRed(GreenNode green, SyntaxTree syntaxTree, int position)
            => new CocoaGreenNodeFactory(green).CreateTypedRed(syntaxTree, position);

        /// <summary>泛型用法扫描（返回语言中性的 (类型名, 实参列表) 对）。</summary>
        public IEnumerable<(SyntaxToken Identifier, ImmutableArray<SyntaxNode> Arguments)> CollectGenericUsages(Binding.BoundGlobalScope globalScope)
        {
            foreach (var root in Binding.Monomorphizer.CollectDeclarationRoots(globalScope))
            {
                foreach (var node in Binding.Monomorphizer.Walk(root))
                {
                    if (node is GenericTypeClauseSyntax genericClause)
                    {
                        yield return (genericClause.Identifier, genericClause.TypeArguments.Cast<SyntaxNode>().ToImmutableArray());
                    }
                    else if (node is ObjectCreationExpressionSyntax creation && creation.TypeArguments != null)
                    {
                        yield return (creation.Identifier, creation.TypeArguments.Arguments.Cast<SyntaxNode>().ToImmutableArray());
                    }
                }
            }
        }

        /// <summary>声明的命名空间名集合。</summary>
        public ImmutableArray<string> GetDeclaredNamespaceNames(SyntaxTree syntaxTree)
        {
            var names = new List<string>();
            CollectNamespaceNames(((CompilationUnitSyntax)syntaxTree.Root).Members, names);
            return names.ToImmutableArray();
        }

        private static void CollectNamespaceNames(ImmutableArray<MemberSyntax> members, List<string> names)
        {
            foreach (var member in members)
            {
                if (member is NamespaceDeclarationSyntax ns)
                {
                    names.Add(ns.Name);
                    CollectNamespaceNames(ns.Members, names);
                }
            }
        }

        /// <summary>根成员集合。</summary>
        public ImmutableArray<SyntaxNode> GetRootMembers(SyntaxTree syntaxTree)
            => ((CompilationUnitSyntax)syntaxTree.Root).Members.Cast<SyntaxNode>().ToImmutableArray();

        /// <summary>语义模型。</summary>
        public SemanticModel CreateSemanticModel(Compilation compilation, SyntaxTree syntaxTree)
            => new SemanticModel(compilation, syntaxTree);

        /// <summary>不可达代码位置解析。</summary>
        public TextLocation? GetUnreachableCodeLocation(SyntaxNode node)
        {
            var kind = (node as SyntaxNode)?.Kind;
            switch (kind)
            {
                case SyntaxKind.BlockStatement:
                {
                    var firstStatement = ((BlockStatementSyntax)node).Statements.FirstOrDefault();
                    return firstStatement == null ? null : GetUnreachableCodeLocation(firstStatement);
                }
                case SyntaxKind.VariableDeclaration:
                {
                    var variableDeclaration = (VariableDeclarationSyntax)node;
                    return variableDeclaration.Keyword?.Location ?? variableDeclaration.Location;
                }
                case SyntaxKind.IfStatement:
                    return ((IfStatementSyntax)node).Keyword.Location;
                case SyntaxKind.WhileStatement:
                    return ((WhileStatementSyntax)node).Keyword.Location;
                case SyntaxKind.DoWhileStatement:
                    return ((DoWhileStatementSyntax)node).DoKeyword.Location;
                case SyntaxKind.ForStatement:
                    return ((ForStatementSyntax)node).Keyword.Location;
                case SyntaxKind.ForeachStatement:
                    return ((ForeachStatementSyntax)node).Keyword.Location;
                case SyntaxKind.SwitchStatement:
                    return ((SwitchStatementSyntax)node).Keyword.Location;
                case SyntaxKind.BreakStatement:
                    return ((BreakStatementSyntax)node).Keyword.Location;
                case SyntaxKind.ContinueStatement:
                    return ((ContinueStatementSyntax)node).Keyword.Location;
                case SyntaxKind.ReturnStatement:
                    return ((ReturnStatementSyntax)node).Keyword.Location;
                case SyntaxKind.ExpressionStatement:
                    return GetUnreachableCodeLocation(((ExpressionStatementSyntax)node).Expression);
                case SyntaxKind.CallExpression:
                    return ((CallExpressionSyntax)node).Identifier.Location;
                case SyntaxKind.MemberCallExpression:
                    return ((MemberCallExpressionSyntax)node).IdentifierToken.Location;
                default:
                    throw new Exception($"Unexpected syntax {node.Kind}");
            }
        }

        /// <summary>声明名 token 位置（供 Compilation/NativeImportValidator 语言中性获取）。</summary>
        public TextLocation? GetDeclarationNameLocation(SyntaxNode? declaration)
        {
            if (declaration is FunctionDeclarationSyntax fn)
                return fn.Identifier.Location;
            if (declaration is ClassDeclarationSyntax cls)
                return cls.Identifier.Location;
            return declaration?.Location;
        }

        /// <summary>类声明是否带 facade 修饰符。</summary>
        public bool HasDeclaredFacadeModifier(SyntaxNode? declaration)
            => declaration is ClassDeclarationSyntax cls
                && cls.Modifiers.Any(m => m.Kind == SyntaxKind.FacadeKeyword);
    }
}