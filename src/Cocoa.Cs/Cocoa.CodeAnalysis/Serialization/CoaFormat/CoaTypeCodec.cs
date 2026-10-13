using Cocoa.CodeAnalysis.Symbols;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Cocoa.CodeAnalysis.Serialization.CoaFormat
{
    /// <summary>类型记法读写对称编解码（3.2）：写侧类型→文本（<c>TypeRef</c>）与读侧文本→类型（<c>ResolveTypeRef</c>）
    /// 同处一类，共享同一份类型记法知识（`@`基元权威记法 / `!`开放参数 / `fnty{...}`函数类型 /
    /// backtick 元数 + # + $ 实例化 mangle / 库限定 `库名!全名` / `[]` 数组后缀）。
    /// 符号解析（函数/变量/属主）在 <see cref="CoaSymbolResolver"/>。</summary>
    internal sealed class CoaTypeCodec
    {
        private readonly CoaReadContext _context;

        public CoaTypeCodec(CoaReadContext context)
        {
            _context = context;
        }

        // ---------------------------------------------------------------- write side（纯类型记法）

        /// <summary>类型的文本引用：内建/数组用短名（int / int[][]），类/枚举用全名。</summary>
        public static string TypeRef(TypeSymbol type)
        {
            // 6e 跨库里程碑：基元内建 → `@` 权威记法（@i32/@string/@bool…，Rust/LLVM 式位宽名）。
            // 引用相等键（单例稳定），先于 NamedTypeSymbol 分支命中，避免输出 C# 短名 int/string。
            if (GenericTypeInstantiator.TryGetPrimitiveName(type, out var primitiveName))
            {
                return primitiveName;
            }

            // 6e-G7 S1：开放类型参数 → 限定权威键 `!属主全名.参数名`（方法级无属主回落裸名）。
            // 实例化类型 → Encode v3 完整 mangle（backtick 元数 + # + $ 分隔递归实参）。
            if (type is TypeParameterSymbol openParameter)
            {
                return openParameter.OwningClass != null
                    ? "!" + openParameter.OwningClass.FullName + "." + openParameter.Name
                    : "!" + openParameter.Name;
            }

            if (type is InstantiatedTypeSymbol instantiated)
            {
                return EncodeInstantiatedTypeRef(instantiated);
            }

            if (type is NamedTypeSymbol { TypeKind: TypeKind.Enum } enumType)
            {
                return LibraryQualify(enumType);
            }

            if (type is NamedTypeSymbol classType)
            {
                return LibraryQualify(classType);
            }

            // 6e-M22/M0-1b：函数类型 `fnty{参数,;返回}`（递归 TypeRef；参数逗号分隔、分号接返回、{} 嵌套）。
            // .coa 词法仅以空白与 () 切分，故嵌套用 {} 避开结构括号。
            if (type is FunctionTypeSymbol functionType)
            {
                var builder = new StringBuilder();
                builder.Append("fnty{");
                for (var i = 0; i < functionType.ParameterTypes.Length; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }

                    builder.Append(TypeRef(functionType.ParameterTypes[i]));
                }

                builder.Append(';');
                builder.Append(TypeRef(functionType.ReturnType));
                builder.Append('}');
                return builder.ToString();
            }

            if (type.ElementType != null)
            {
                // 数组：递归元素 TypeRef（元素为开放类型参数时限定为 !属主.名，如 K[] → !System.Collections.Generic.Dictionary.K[]）
                return TypeRef(type.ElementType) + "[]";
            }

            return type.Name;
        }

        /// <summary>6f-3：库限定全名（复合键写侧）——读入库的类型带来源库名，`库名!全名` 引用唯一化；
        /// 源码声明/系统内建（ContainingLibrary 空）回落裸全名。</summary>
        public static string LibraryQualify(NamedTypeSymbol type)
        {
            return string.IsNullOrEmpty(type.ContainingLibrary)
                ? type.FullName
                : type.ContainingLibrary + "!" + type.FullName;
        }

        /// <summary>
        /// 实例化类型的 .coa 编码（6e-G7 S1）：定义全名 + backtick 元数 + # + $ 分隔实参。
        /// 实参递归走 <see cref="TypeRef"/>——开放参数为限定键 !属主.名（区别于 mangle 缓存键的裸 !T），
        /// 保证跨定义无歧义且读侧可独立解析；基元/类用平名（不含 $、` 等，分隔安全）；嵌套实例化递归。
        /// </summary>
        public static string EncodeInstantiatedTypeRef(InstantiatedTypeSymbol instantiated)
        {
            var builder = new StringBuilder();
            builder.Append(instantiated.GenericDefinition.FullName);
            builder.Append('`');
            builder.Append(instantiated.TypeArguments.Length);
            builder.Append('#');

            for (var i = 0; i < instantiated.TypeArguments.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append('$');
                }

                builder.Append(TypeRef(instantiated.TypeArguments[i]));
            }

            return builder.ToString();
        }

        /// <summary>6e-M26：泛型开放绑定体确定性排序键（GenericOpenBodies 为 ImmutableDictionary，枚举不稳定）。</summary>
        public static string GenericOpenSortKey(FunctionSymbol function)
        {
            var owner = function.ContainingClass?.FullName ?? "";
            var parameters = string.Join(",", function.Parameters.Select(p => p.Type.ToString()));
            return $"{owner}|{function.Namespace}|{function.Name}|{parameters}";
        }

        // ---------------------------------------------------------------- read side（文本 → 类型）

        public TypeSymbol ResolveTypeRef(string reference)
        {
            // 6e-M22/M0-1b：函数类型 `fnty{...}`（递归解析，{} 内参数/返回可能再含 fnty）
            if (reference.StartsWith("fnty{", StringComparison.Ordinal))
            {
                return ParseFunctionTypeRef(reference);
            }

            var baseName = reference;
            var dims = 0;
            while (baseName.EndsWith("[]", StringComparison.Ordinal))
            {
                baseName = baseName.Substring(0, baseName.Length - 2);
                dims++;
            }

            var core = ResolveNamedType(baseName);
            for (var i = 0; i < dims; i++)
            {
                core = TypeSymbol.ArrayOf(core);
            }

            return core;
        }

        /// <summary>6e-M22/M0-1b：解析 `fnty{参数,;返回}`（参数/返回递归 ResolveTypeRef，{} 深度感知）。</summary>
        private TypeSymbol ParseFunctionTypeRef(string reference)
        {
            var position = "fnty{".Length;
            var parameterTypes = ImmutableArray.CreateBuilder<TypeSymbol>();

            while (true)
            {
                var (part, next) = ReadUntilTopLevel(reference, position, ',', ';');

                // 零参数函数类型写侧为 `fnty{;返回}`：首段为空，跳过（6e-M25 阶段 5）。
                if (part.Length > 0)
                {
                    parameterTypes.Add(ResolveTypeRef(part));
                }

                position = next;

                if (position >= reference.Length || (reference[position] != ',' && reference[position] != ';'))
                {
                    throw new InvalidDataException($"Malformed function type ref '{reference}'");
                }

                if (reference[position] == ';')
                {
                    position++;
                    break;
                }

                position++; // 跳过 ','
            }

            var (returnPart, end) = ReadUntilTopLevel(reference, position, '}', '}');
            if (end >= reference.Length || reference[end] != '}')
            {
                throw new InvalidDataException($"Malformed function type ref '{reference}'");
            }

            var returnType = ResolveTypeRef(returnPart);
            return FunctionTypeSymbol.Get(parameterTypes.ToImmutable(), returnType);
        }

        /// <summary>从 position 读到深度 0 处 stop1/stop2 之一（或外层 `}`），返回 (子串, 停止位置)。</summary>
        private static (string Part, int Next) ReadUntilTopLevel(string text, int position, char stop1, char stop2)
        {
            var start = position;
            var depth = 0;

            while (position < text.Length)
            {
                var c = text[position];
                if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    if (depth == 0)
                    {
                        break;
                    }

                    depth--;
                }
                else if (depth == 0 && (c == stop1 || c == stop2))
                {
                    break;
                }

                position++;
            }

            return (text.Substring(start, position - start), position);
        }

        public TypeSymbol ResolveNamedType(string name)
        {
            // 6f-3：库限定类型引用 `库名!全名`（复合键读侧）——按归属库 TypesByName 解析；
            // 库名==当前模块时走本地表（round-trip 自限定引用）。
            var bangIndex = name.IndexOf('!');
            if (bangIndex > 0 && bangIndex < name.Length - 1)
            {
                var libraryToken = name.Substring(0, bangIndex);
                var fullName = name.Substring(bangIndex + 1);
                foreach (var library in _context.ExternalLibraries)
                {
                    if (IsLibraryMatch(library, libraryToken) &&
                        library.TypesByName.TryGetValue(fullName, out var scoped))
                    {
                        return scoped;
                    }
                }

                if (!string.IsNullOrEmpty(_context.ModuleName) &&
                    string.Equals(_context.ModuleName, libraryToken, StringComparison.Ordinal) &&
                    _context.TypesByName.TryGetValue(fullName, out var selfScoped))
                {
                    return selfScoped;
                }
            }

            if (_context.TypesByName.TryGetValue(name, out var known))
            {
                return known;
            }

            // 6e-G7 S1：开放类型参数限定键（!属主.名）或基元权威编码（!System.Int32 等，实例化实参位置出现）
            if (name.StartsWith("!", StringComparison.Ordinal))
            {
                if (_context.OpenTypeParametersByKey.TryGetValue(name, out var openParameter))
                {
                    return openParameter;
                }

                if (GenericTypeInstantiator.TryDecodePrimitive(name, out var primitive))
                {
                    return primitive;
                }

                throw new InvalidDataException($"Unknown open type parameter '{name}'");
            }

            // 6e 跨库里程碑：基元 `@` 权威记法（@i32/@string/@bool…，Rust/LLVM 式位宽名）。
            if (name.StartsWith("@", StringComparison.Ordinal))
            {
                if (GenericTypeInstantiator.TryDecodePrimitive(name, out var primitive))
                {
                    return primitive;
                }

                throw new InvalidDataException($"Unknown primitive type '{name}'");
            }

            // 6e-G7 S1：实例化类型 mangle（backtick 元数 + # + $ 分隔递归实参）
            if (name.Contains('`') && name.Contains('#'))
            {
                return ParseInstantiatedTypeRef(name);
            }

            return name switch
            {
                "object" => TypeSymbol.Any,
                "null" => TypeSymbol.Null, // 6e-M19 M5-a
                "bool" => TypeSymbol.Boolean,
                "byte" => TypeSymbol.UInt8,
                "sbyte" => TypeSymbol.Int8,
                "short" => TypeSymbol.Int16,
                "ushort" => TypeSymbol.UInt16,
                "int" => TypeSymbol.Int32,
                "uint" => TypeSymbol.UInt32,
                "long" => TypeSymbol.Int64,
                "ulong" => TypeSymbol.UInt64,
                "float" => TypeSymbol.Float,
                "double" => TypeSymbol.Double,
                "char" => TypeSymbol.Char,
                "string" => TypeSymbol.String,
                "void" => TypeSymbol.Void,
                // N1：object 字段/参数（Lock._owner 等）TypeRef 以全名落盘 → 回指对象根 object（System.Object 全名）
                "System.Object" => TypeSymbol.Any,
                "i128" => TypeSymbol.Int128,
                "u128" => TypeSymbol.UInt128,
                "f128" => TypeSymbol.Float128,
                "decimal" => TypeSymbol.Decimal,
                "f16" => TypeSymbol.Float16,
                "?" => TypeSymbol.Error,
                _ => throw new InvalidDataException($"Unknown type '{name}'"),
            };
        }

        /// <summary>
        /// 实例化类型 mangle 递归解析（6e-G7 S1）：`定义全名\`N#实参1$...$实参N`，
        /// 按 arity 递归消费（嵌套实例化的内层 $ 归属内层分组）；叶子经
        /// !开放参数/!基元反解或既有名字解析；`[]` 后缀按数组还原。
        /// </summary>
        private TypeSymbol ParseInstantiatedTypeRef(string text)
        {
            var position = 0;
            var type = ParseEncodedType(text, ref position);
            if (position != text.Length)
            {
                throw new InvalidDataException($"Trailing characters in instantiated type '{text}'");
            }

            return type;
        }

        private TypeSymbol ParseEncodedType(string text, ref int position)
        {
            // ! 前缀：开放类型参数限定键
            if (position < text.Length && text[position] == '!')
            {
                var start = position;
                position++;
                while (position < text.Length && IsEncodedNameChar(text[position]))
                {
                    position++;
                }

                var key = text.Substring(start, position - start);
                if (_context.OpenTypeParametersByKey.TryGetValue(key, out var openParameter))
                {
                    return ConsumeArraySuffixes(key, openParameter, text, ref position);
                }

                if (GenericTypeInstantiator.TryDecodePrimitive(key, out var primitive))
                {
                    return ConsumeArraySuffixes(key, primitive, text, ref position);
                }

                throw new InvalidDataException($"Unknown encoded type '{key}' in '{text}'");
            }

            // 6e 跨库里程碑：@ 前缀 —— 基元权威记法（@i32/@string…，mangle 实参）。
            if (position < text.Length && text[position] == '@')
            {
                var start = position;
                position++;
                while (position < text.Length && (char.IsLetterOrDigit(text[position])))
                {
                    position++;
                }

                var key = text.Substring(start, position - start);
                if (GenericTypeInstantiator.TryDecodePrimitive(key, out var primitive))
                {
                    return ConsumeArraySuffixes(key, primitive, text, ref position);
                }

                throw new InvalidDataException($"Unknown primitive '{key}' in '{text}'");
            }

            // 名字段：字母数字._ （实例化头在此处截断于 backtick）
            var nameStart = position;
            while (position < text.Length && IsEncodedNameChar(text[position]))
            {
                position++;
            }

            var fullName = text.Substring(nameStart, position - nameStart);

            // 实例化：backtick 元数 + # + N 个递归实参（$ 分隔）
            if (position < text.Length && text[position] == '`')
            {
                position++;
                var arityStart = position;
                while (position < text.Length && text[position] >= '0' && text[position] <= '9')
                {
                    position++;
                }

                if (!int.TryParse(text.Substring(arityStart, position - arityStart), NumberStyles.Integer, CultureInfo.InvariantCulture, out var arity) ||
                    posAt(text, position) != '#')
                {
                    throw new InvalidDataException($"Malformed instantiation arity in '{text}'");
                }

                position++; // skip '#'
                // N1：同名不同元数的泛型定义（ValueTuple<T1>..<T1..T7>）——优先按 `定义`元数` 键查，
                // 旧格式（单泛型定义）回退裸全名键 + 元数校验
                if (!_context.TypesByName.TryGetValue(fullName + "`" + arity, out var definitionObject) &&
                    !_context.TypesByName.TryGetValue(fullName, out definitionObject))
                {
                    throw new InvalidDataException($"Unknown generic definition '{fullName}`{arity}' in '{text}'");
                }

                if (definitionObject is not NamedTypeSymbol definition ||
                    !definition.IsGenericDefinition ||
                    definition.TypeParameters.Length != arity)
                {
                    throw new InvalidDataException($"Unknown generic definition or arity mismatch '{fullName}`{arity}' in '{text}'");
                }

                var arguments = ImmutableArray.CreateBuilder<TypeSymbol>(arity);
                for (var i = 0; i < arity; i++)
                {
                    if (i > 0)
                    {
                        if (posAt(text, position) != '$')
                        {
                            throw new InvalidDataException($"Expected '$' separator in '{text}'");
                        }

                        position++;
                    }

                    arguments.Add(ParseEncodedType(text, ref position));
                }

                var instantiated = GenericTypeInstantiator.Instantiate(definition, arguments.ToImmutable());
                return ConsumeArraySuffixes(fullName + "`" + arity, instantiated, text, ref position);
            }

            // 平名：类/枚举全名或别名，走既有解析
            var resolved = ResolveNamedType(fullName);
            return ConsumeArraySuffixes(fullName, resolved, text, ref position);
        }

        private static TypeSymbol ConsumeArraySuffixes(string debugName, TypeSymbol type, string text, ref int position)
        {
            while (position + 1 < text.Length && text[position] == '[' && text[position + 1] == ']')
            {
                position += 2;
                type = TypeSymbol.ArrayOf(type);
            }

            return type;
        }

        private static char posAt(string text, int index) => index < text.Length ? text[index] : '\0';

        private static bool IsEncodedNameChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '.' || c == '_';
        }

        /// <summary>6f-3：库名匹配——键前缀/引用首段（coa 基名，如 "LibOne"）与已加载库（Name="LibOne.Managed"）对齐。</summary>
        private static bool IsLibraryMatch(CoaProgram library, string libraryName)
        {
            if (string.IsNullOrEmpty(libraryName))
            {
                return false;
            }

            if (string.Equals(library.Name, libraryName, StringComparison.Ordinal))
            {
                return true;
            }

            if (library.Name.EndsWith(CoaAssemblyNaming.ManagedSuffix, StringComparison.Ordinal) &&
                string.Equals(
                    library.Name.Substring(0, library.Name.Length - CoaAssemblyNaming.ManagedSuffix.Length),
                    libraryName,
                    StringComparison.Ordinal))
            {
                return true;
            }

            return false;
        }
    }
}
