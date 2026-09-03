using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Text;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Cocoa.CodeAnalysis
{
    /// <summary>
    /// 语义模型抽象基类（P1-5 拆分：对齐 Roslyn <see cref="Microsoft.CodeAnalysis.SemanticModel"/>）。
    /// 共享绑定树基础设施（Syntax→BoundNode 映射 / GetOperation / GetDiagnostics），语言专属的名字解析
    /// （GetTypeInfo/GetDeclaredSymbol/GetSymbolInfo 及其节点分派）由语言库子类
    /// <c>CocoaSemanticModel</c>/<c>CSharpSemanticModel</c> 实现（<see cref="Language.CreateSemanticModel"/> 分派）。
    /// </summary>
    public abstract class SemanticModel
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
        protected Dictionary<SyntaxNode, BoundNode> BoundBySyntax
        {
            get
            {
                if (_boundBySyntax != null)
                {
                    return _boundBySyntax;
                }

                var program = _compilation.BindProgram(
                    _compilation.IsScript,
                    null,
                    _compilation.GlobalScope,
                    _compilation.CodLibraries,
                    _syntaxTree.Language,
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
        public abstract TypeSymbol? GetTypeInfo(SyntaxNode node);

        /// <summary>解析声明语法节点对应的符号（对齐 Roslyn <c>SemanticModel.GetDeclaredSymbol</c>）：
        /// 类/枚举 → 命名类型（类按声明引用精确匹配、枚举按名）；顶层函数 → 函数符号；其余返回 null。
        /// 嵌套类型/构造器等不在全局命名空间树，暂不支持。</summary>
        public abstract Symbol? GetDeclaredSymbol(SyntaxNode declaration);

        /// <summary>表达式对应符号（对齐 Roslyn <c>SemanticModel.GetSymbolInfo</c>）。
        /// 优先绑定树（局部变量/参数/实例成员等返回真实绑定符号）；未命中回落名称/成员解析。</summary>
        public abstract Symbol? GetSymbolInfo(SyntaxNode node);

        protected NamespaceSymbol GlobalNamespace => _compilation.GlobalNamespace;

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

        /// <summary>本语法树相关的全部诊断（对齐 Roslyn <c>SemanticModel.GetDiagnostics</c>）。</summary>
        public ImmutableArray<Diagnostic> GetDiagnostics()
        {
            return _compilation.GetDiagnostics()
                .Where(d => ReferenceEquals(d.Location.Text, _syntaxTree.Text))
                .ToImmutableArray();
        }

        /// <summary>本语法树中与指定范围相交的诊断。</summary>
        public ImmutableArray<Diagnostic> GetDiagnostics(TextSpan span)
        {
            var builder = ImmutableArray.CreateBuilder<Diagnostic>();
            foreach (var diagnostic in _compilation.GetDiagnostics())
            {
                if (ReferenceEquals(diagnostic.Location.Text, _syntaxTree.Text) && diagnostic.Location.Span.OverlapsWith(span))
                {
                    builder.Add(diagnostic);
                }
            }

            return builder.ToImmutable();
        }

        /// <summary>按名解析符号（内建类型/元数据类型/全局变量/函数）。</summary>
        protected Symbol? ResolveName(string text)
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

        protected static IEnumerable<NamespaceSymbol> EnumerateNamespaces(NamespaceSymbol root)
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

        protected static TypeSymbol? ResolveBuiltin(string name)
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
