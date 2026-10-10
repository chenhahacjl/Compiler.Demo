using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Documentation;
using Cocoa.CodeAnalysis.Serialization;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System;
using System.Collections.Generic;
using Cocoa.CodeAnalysis.Bound;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Cocoa.CodeAnalysis.Serialization.CoaFormat
{
    /// <summary>
    /// .coa 读端协调器（源 <c>CoaSerializer</c> 的读侧收敛为实例类）：
    /// 持有 token 读取器 + 读上下文 + 类型解析器（<see cref="CoaTextReader"/>/<see cref="CoaReadContext"/>/<see cref="CoaTypeResolver"/>），
    /// 所有 Read* 为实例方法，递归不再显式传上下文。
    /// </summary>
    internal sealed partial class CoaFormatReader
    {
        private readonly CoaTextReader _tokens;
        private readonly CoaReadContext _context;
        private readonly CoaTypeResolver _resolver;

        /// <summary>.coa 反序列化不携带语法节点（设计如此，见类头注释）；nullable 单点豁免。</summary>
        private static SyntaxNode NoSyntax => null!;

        private CoaFormatReader(string[] tokens, string moduleName, ImmutableArray<CoaProgram> external)
        {
            _tokens = new CoaTextReader(tokens);
            _context = new CoaReadContext(moduleName, external);
            _resolver = new CoaTypeResolver(_context);
        }

        /// <summary>读 `.coa` 文本（兼容入口：无库名/无 external，跨库符号解析留空）。</summary>
        public static CoaProgram Read(string text)
        {
            return Read(text, moduleName: "", external: ImmutableArray<CoaProgram>.Empty);
        }

        /// <summary>
        /// 读 `.coa` 文本。`moduleName` 为库名（读入符号的 ContainingLibrary 回填，FnKey 库前缀来源）；
        /// `external` 为已加载的依赖库（System.Core 先行），供跨库符号合并解析（复用实例，非复制）。
        /// </summary>
        public static CoaProgram Read(string text, string moduleName, ImmutableArray<CoaProgram> external)
        {
            // 约束第二趟：兄弟参数已全部注册，!限定键可解析
            var marker = "(checksum " + CoaText.ChecksumTag;
            var markerIndex = text.LastIndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                throw new InvalidDataException(".coa checksum missing (expected '(checksum sha256:<hex>)' as the last line); rebuild the library");
            }

            var payload = text.Substring(0, markerIndex);
            var provided = text.Substring(markerIndex + marker.Length).TrimEnd();
            if (!provided.EndsWith(")"))
            {
                throw new InvalidDataException(".coa checksum malformed (expected '(checksum sha256:<hex>)' as the last line)");
            }

            provided = provided.Substring(0, provided.Length - 1);
            var actual = CoaText.ComputeChecksum(payload);
            if (!string.Equals(provided, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($".coa checksum mismatch: library corrupted or modified (expected {actual}, got {provided})");
            }

            var tokens = Tokenize(payload).ToArray();
            var reader = new CoaFormatReader(tokens, moduleName, external);
            return reader.ReadProgram();
        }

        /// <summary>从 `.coa` 文件加载程序集。库名由文件名回填；`external` 为已加载的依赖库（供跨库符号合并）。</summary>
        public static CoaProgram Load(string path, ImmutableArray<CoaProgram>? external = null)
        {
            var moduleName = Path.GetFileNameWithoutExtension(path);
            return Read(File.ReadAllText(path), moduleName, external ?? ImmutableArray<CoaProgram>.Empty);
        }

        private CoaProgram ReadProgram()
        {
            _tokens.Expect("cod");

            var magic = _tokens.ExpectString();
            if (magic != CoaFormatWriter.Magic)
            {
                throw new InvalidDataException($"invalid .coa magic '{magic}'");
            }

            var version = _tokens.ExpectInt();
            if (version != CoaFormatWriter.Version)
            {
                throw new InvalidDataException($".coa version {version} is not supported (expected {CoaFormatWriter.Version}); rebuild the library");
            }

            var bodies = ImmutableDictionary.CreateBuilder<FunctionSymbol, BoundBlockStatement>();
            var requires = CoaRequirement.Any;
            var platforms = ImmutableArray.CreateBuilder<string>();
            var dotnetRefs = ImmutableArray.CreateBuilder<string>();
            var codRefs = ImmutableArray.CreateBuilder<string>();
            var imports = ImmutableArray.CreateBuilder<string>();
            var namespaces = ImmutableArray.CreateBuilder<string>();
            var docs = ImmutableDictionary.CreateBuilder<string, string>();

            while (_tokens.TryExpect(out var child))
            {
                switch (child)
                {
                    case "symbols":
                        ReadSymbols();
                        ApplyPendingProperties();
                        ApplyPendingClosures();
                        ApplyPendingBaseTypes();
                        ApplyPendingFields();
                        break;
                    case "bodies":
                        ReadBodies(bodies);
                        break;
                    case "manifest":
                        while (_tokens.TryExpect(out var item))
                        {
                            switch (item)
                            {
                                case "requires":
                                    requires = CoaText.ParseRequirement(_tokens.ExpectString());
                                    break;
                                case "platform":
                                    platforms.Add(CoaText.Unescape(_tokens.ExpectString()));
                                    break;
                                case "refdll":
                                    dotnetRefs.Add(CoaText.Unescape(_tokens.ExpectString()));
                                    break;
                                case "refcod":
                                    codRefs.Add(CoaText.Unescape(_tokens.ExpectString()));
                                    break;
                                case "import":
                                    imports.Add(CoaText.Unescape(_tokens.ExpectString()));
                                    break;
                                case "ns":
                                    namespaces.Add(CoaText.Unescape(_tokens.ExpectString()));
                                    break;
                            }

                            _tokens.End();
                        }

                        _tokens.End();
                        break;
                    case "docs":
                        while (_tokens.TryExpect(out var docItem))
                        {
                            switch (docItem)
                            {
                                case "doc":
                                    var docId = CoaText.Unescape(_tokens.ExpectString());
                                    var docText = CoaText.Unescape(_tokens.ExpectString());
                                    docs[docId] = docText;
                                    break;
                            }

                            _tokens.End();
                        }

                        _tokens.End();
                        break;
                }
            }

            // 6e 跨库里程碑：库名回填——优先取传入 moduleName；兼容入口（空）从本库 fn 键前缀恢复
            // （保证 read→write round-trip 稳定，重写时 RegisterFunction 跨库过滤不误伤本库函数）。
            var programName = _context.ModuleName.Length > 0
                ? _context.ModuleName
                : RecoverLibraryFromKeys(_context.LocalFunctionKeys.Keys);

            var program = new CoaProgram(
                _context.Functions.ToImmutable(),
                _context.Globals.ToImmutable(),
                _context.Enums.ToImmutable(),
                _context.Classes.ToImmutable(),
                bodies.ToImmutable(),
                requires,
                platforms.ToImmutable(),
                dotnetRefs.ToImmutable(),
                imports.ToImmutable(),
                codRefs.ToImmutable(),
                namespaces.ToImmutable(),
                _context.GenericDefinitions.ToImmutable(),
                functionKeys: _context.LocalFunctionKeys.ToImmutableDictionary(),
                typesByName: _context.LocalTypesByName.ToImmutableDictionary(),
                docs: docs.ToImmutable())
            {
                Name = programName,
            };

            ApplyDocumentation(program);

            return program;
        }

        /// <summary>
        /// 6e-M24：`.coa` 读侧文档回填——按 DocID 从 <see cref="CoaProgram.Docs"/> 查表挂到符号。
        /// 跨程序集符号（stdlib）的 <c>Declaration = null</c>，文档只能经序列化通道恢复（设计 §6.3）。
        /// </summary>
        private void ApplyDocumentation(CoaProgram program)
        {
            if (program.Docs.Count == 0)
            {
                return;
            }

            foreach (var symbol in EnumerateProgramSymbols(program))
            {
                if (symbol.DocumentationText != null)
                {
                    continue;
                }

                var docId = DocIdBuilder.GetDocId(symbol);
                if (docId != null && program.Docs.TryGetValue(docId, out var text))
                {
                    symbol.DocumentationText = text;
                }
            }
        }

        private static IEnumerable<Symbol> EnumerateProgramSymbols(CoaProgram program)
        {
            foreach (var fn in program.Functions)
            {
                yield return fn;
            }

            foreach (var type in program.Classes.Concat(program.GenericDefinitions))
            {
                yield return type;
                foreach (var method in type.Methods)
                {
                    yield return method;
                }

                foreach (var field in type.Fields)
                {
                    yield return field;
                }

                foreach (var property in type.Properties)
                {
                    yield return property;
                }

                foreach (var evt in type.Events)
                {
                    yield return evt;
                }
            }

            foreach (var global in program.Globals)
            {
                yield return global;
            }

            foreach (var en in program.Enums)
            {
                yield return en;
            }
        }

        /// <summary>6e 跨库里程碑：从本库 fn 键集合恢复库名（首键前缀 `库名!`；无则空）。</summary>
        private static string RecoverLibraryFromKeys(IEnumerable<string> keys)
        {
            foreach (var key in keys)
            {
                var bangIndex = key.IndexOf('!');
                if (bangIndex > 0 && key.IndexOf('[') > bangIndex)
                {
                    return key.Substring(0, bangIndex);
                }
            }

            return "";
        }

        // ---------------------------------------------------------------- read: symbols
        private BoundUnaryOperator ReadUnaryOperator()
        {
            _tokens.Expect("uop");
            var unaryKind = CoaText.ParseUnaryOpText(_tokens.ExpectString());
            var operandType = _resolver.ResolveTypeRef(_tokens.ExpectString());
            var op = BoundUnaryOperator.Bind(unaryKind, operandType);
            _tokens.End();
            return op ?? throw new InvalidDataException($"Cannot bind unary operator {unaryKind} on {operandType}");
        }

        private BoundBinaryOperator ReadBinaryOperator()
        {
            _tokens.Expect("bop");
            var binaryKind = CoaText.ParseBinaryOpText(_tokens.ExpectString());
            var leftType = _resolver.ResolveTypeRef(_tokens.ExpectString());
            var rightType = _resolver.ResolveTypeRef(_tokens.ExpectString());
            var op = BoundBinaryOperator.Bind(binaryKind, leftType, rightType);
            _tokens.End();
            return op ?? throw new InvalidDataException($"Cannot bind binary operator {binaryKind} on {leftType} and {rightType}");
        }

        private static BoundLabel GetLabel(Dictionary<string, BoundLabel> labels, string name)
        {
            if (!labels.TryGetValue(name, out var label))
            {
                label = new BoundLabel(name);
                labels[name] = label;
            }

            return label;
        }

        // ---------------------------------------------------------------- read: tokenizer / helpers

        private static IEnumerable<string> Tokenize(string text)
        {
            var tokens = new List<string>();
            var sb = new StringBuilder();
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];

                // 6e-M35：反斜杠转义序列（`\(`/`\)`/`\s`/`\\` 等）整体入 token——
                // 否则字符串含 `(`/`)` 时（如 Handle.ToString "Handle("）构造定界符切碎，
                // Escape 已产出 `\(` 但 tokenizer 未识别 → System.Core.coa 加载失败。
                if (c == '\\' && i + 1 < text.Length)
                {
                    sb.Append(c).Append(text[i + 1]);
                    i++;
                    continue;
                }

                if (c == '(' || c == ')')
                {
                    if (sb.Length > 0)
                    {
                        tokens.Add(sb.ToString());
                        sb.Clear();
                    }

                    tokens.Add(c.ToString());
                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    if (sb.Length > 0)
                    {
                        tokens.Add(sb.ToString());
                        sb.Clear();
                    }

                    continue;
                }

                sb.Append(c);
            }

            if (sb.Length > 0)
            {
                tokens.Add(sb.ToString());
            }

            return tokens;
        }

        /// <summary>全名拆分为（命名空间, 名）；无点号时命名空间为空。</summary>
        private static (string Namespace, string Name) SplitFullName(string fullName)
        {
            var lastDot = fullName.LastIndexOf('.');
            return lastDot < 0 ? ("", fullName) : (fullName.Substring(0, lastDot), fullName.Substring(lastDot + 1));
        }
    }
}
