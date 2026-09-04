using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Cocoa.CodeAnalysis.Serialization
{
    public static partial class CoaSerializer
    {
        private static TypeSymbol ResolveTypeRef(string reference, ReadContext context)
        {
            // 6e-M22/M0-1b：函数类型 `fnty{...}`（递归解析，{} 内参数/返回可能再含 fnty）
            if (reference.StartsWith("fnty{", StringComparison.Ordinal))
            {
                return ParseFunctionTypeRef(reference, context);
            }

            var baseName = reference;
            var dims = 0;
            while (baseName.EndsWith("[]", StringComparison.Ordinal))
            {
                baseName = baseName.Substring(0, baseName.Length - 2);
                dims++;
            }

            var core = ResolveNamedType(baseName, context);
            for (var i = 0; i < dims; i++)
            {
                core = TypeSymbol.ArrayOf(core);
            }

            return core;
        }

        /// <summary>6e-M22/M0-1b：解析 `fnty{参数,;返回}`（参数/返回递归 ResolveTypeRef，{} 深度感知）。</summary>
        private static TypeSymbol ParseFunctionTypeRef(string reference, ReadContext context)
        {
            var position = "fnty{".Length;
            var parameterTypes = ImmutableArray.CreateBuilder<TypeSymbol>();

            while (true)
            {
                var (part, next) = ReadUntilTopLevel(reference, position, ',', ';');
                parameterTypes.Add(ResolveTypeRef(part, context));
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

            var returnType = ResolveTypeRef(returnPart, context);
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

        private static TypeSymbol ResolveNamedType(string name, ReadContext context)
        {
            if (context.TypesByName.TryGetValue(name, out var known))
            {
                return known;
            }

            // 6e-G7 S1：开放类型参数限定键（!属主.名）或基元权威编码（!System.Int32 等，实例化实参位置出现）
            if (name.StartsWith("!", StringComparison.Ordinal))
            {
                if (context.OpenTypeParametersByKey.TryGetValue(name, out var openParameter))
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
                return ParseInstantiatedTypeRef(name, context);
            }

            return name switch
            {
                "any" => TypeSymbol.Any,
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
                "i128" => TypeSymbol.Int128,
                "u128" => TypeSymbol.UInt128,
                "f128" => TypeSymbol.Float128,
                "?" => TypeSymbol.Error,
                _ => throw new InvalidDataException($"Unknown type '{name}'"),
            };
        }

        /// <summary>
        /// 实例化类型 mangle 递归解析（6e-G7 S1）：`定义全名\`N#实参1$...$实参N`，
        /// 按 arity 递归消费（嵌套实例化的内层 $ 归属内层分组）；叶子经
        /// !开放参数/!基元反解或既有名字解析；`[]` 后缀按数组还原。
        /// </summary>
        private static TypeSymbol ParseInstantiatedTypeRef(string text, ReadContext context)
        {
            var position = 0;
            var type = ParseEncodedType(text, ref position, context);
            if (position != text.Length)
            {
                throw new InvalidDataException($"Trailing characters in instantiated type '{text}'");
            }

            return type;
        }

        private static TypeSymbol ParseEncodedType(string text, ref int position, ReadContext context)
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
                if (context.OpenTypeParametersByKey.TryGetValue(key, out var openParameter))
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
                if (!context.TypesByName.TryGetValue(fullName, out var definitionObject) ||
                    definitionObject is not NamedTypeSymbol definition ||
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

                    arguments.Add(ParseEncodedType(text, ref position, context));
                }

                var instantiated = GenericTypeInstantiator.Instantiate(definition, arguments.ToImmutable());
                return ConsumeArraySuffixes(fullName + "`" + arity, instantiated, text, ref position);
            }

            // 平名：类/枚举全名或别名，走既有解析
            var resolved = ResolveNamedType(fullName, context);
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

        private static NamedTypeSymbol ResolveOwnerClass(string fullName, ReadContext context)
        {
            // 6e-M19 M2-c：内建系统类（System.Object/System.Type）按单例解析（不从 cod 类型表读）。
            if (fullName == "System.Object")
            {
                return NamedTypeSymbol.SystemObject;
            }

            if (fullName == "System.Type")
            {
                return NamedTypeSymbol.SystemType;
            }

            if (!context.TypesByName.TryGetValue(fullName, out var type) || type is not NamedTypeSymbol classType)
            {
                throw new InvalidDataException($"Unknown owner class '{fullName}'");
            }

            return classType;
        }

        /// <summary>
        /// 6e 跨库里程碑：从成员键的属主段解析定义类。属主段可为：
        /// 普通全名（`System.Collections.Generic.List`）或实例化副本 mangle 双缀
        /// （`Lib!...System.Collections.Generic.List`1#...T`，InstanceTypeSymbol.FullName = ns + mangle）。
        /// 从 TypesByName（含 external 预播种）按「最长匹配 key + backtick」反解泛型定义类。
        /// </summary>
        private static NamedTypeSymbol? ResolveOwnerClassFromHead(string ownerText, ReadContext context)
        {
            if (ownerText == "System.Object")
            {
                return NamedTypeSymbol.SystemObject;
            }

            if (ownerText == "System.Type")
            {
                return NamedTypeSymbol.SystemType;
            }

            if (context.TypesByName.TryGetValue(ownerText, out var direct))
            {
                return direct as NamedTypeSymbol;
            }

            if (ownerText.Contains('`'))
            {
                // 实例化副本 mangle 双缀：找 TypesByName 中「最长 key + backtick」为 ownerText 子串
                // （InstanceTypeSymbol.FullName = ns + mangle，定义全名出现在 mangle 头部）。
                string? bestKey = null;
                foreach (var key in context.TypesByName.Keys)
                {
                    if (key.IndexOf('`') >= 0)
                    {
                        continue;
                    }

                    if (ownerText.Contains(key + "`", StringComparison.Ordinal) &&
                        (bestKey == null || key.Length > bestKey.Length))
                    {
                        bestKey = key;
                    }
                }

                if (bestKey != null && context.TypesByName.TryGetValue(bestKey, out var best) && best is NamedTypeSymbol bestClass)
                {
                    return bestClass;
                }
            }

            return null;
        }

        private static VariableSymbol ResolveVariable(string key, ReadContext context)
        {
            if (!context.VariablesByKey.TryGetValue(key, out var variable))
            {
                throw new InvalidDataException($"Unknown variable '{key}'");
            }

            return variable;
        }

        private static FunctionSymbol ResolveFunction(string key, ReadContext context)
        {
            if (context.FunctionsByKey.TryGetValue(key, out var function))
            {
                return function;
            }

            // 6e 跨库里程碑：键带库前缀（`库名!head[...]`）——先剥离前缀，再按方法名+元数在
            // 本库 + external 库函数集中归一（消费方替换期再映射回实例化副本）。
            var searchKey = key;
            var bangIndex = key.IndexOf('!');
            if (bangIndex > 0 && key.IndexOf('[') > bangIndex)
            {
                searchKey = key.Substring(bangIndex + 1);
            }

            var bracketIndex = searchKey.LastIndexOf('[');
            if (bracketIndex > 0)
            {
                var head = searchKey.Substring(0, bracketIndex);
                var dotIndex = head.LastIndexOf('.');
                if (dotIndex > 0)
                {
                    var methodName = head.Substring(dotIndex + 1);
                    var parameterCountText = searchKey.Substring(bracketIndex + 1, searchKey.Length - bracketIndex - 2);
                    var parameterCount = parameterCountText.Length == 0
                        ? 0
                        : parameterCountText.Split(',').Length;

                    // 6e 跨库里程碑：优先按属主类精确解析——head 的 backtick 前段即泛型定义全名
                    // （实例化副本键 `Lib!...List`1#...T.get_Count[]`），在 TypesByName（含 external 预播种）
                    // 定义类内按名+元数匹配。避免全集搜索歧义（多个类同签名方法时非唯一）。
                    var ownerText = head.Substring(0, dotIndex);
                    var ownerClass = ResolveOwnerClassFromHead(ownerText, context);

                    if (ownerClass != null)
                    {
                        var ownerCandidates = ownerClass.Methods.Where(m =>
                            m.Name == methodName &&
                            m.Parameters.Length == parameterCount).ToList();

                        if (ownerCandidates.Count == 1)
                        {
                            return ownerCandidates[0];
                        }

                        if (ownerCandidates.Count > 1)
                        {
                            throw new InvalidDataException($"Ambiguous owner-class method '{key}'");
                        }
                    }

                    var candidates = context.Functions.Where(f =>
                        f.Name == methodName &&
                        f.Parameters.Length == parameterCount).ToList();

                    if (candidates.Count == 0)
                    {
                        foreach (var library in context.ExternalLibraries)
                        {
                            candidates.AddRange(library.Functions.Where(f =>
                                f.Name == methodName &&
                                f.Parameters.Length == parameterCount));
                        }
                    }

                    if (candidates.Count == 1)
                    {
                        return candidates[0];
                    }
                }
            }

            throw new InvalidDataException($"Unknown function '{key}'");
        }

        private static bool ParseBoolWord(string text)
        {
            return text switch
            {
                "true" => true,
                "false" => false,
                _ => throw new InvalidDataException($"Expected 'true'/'false' but found '{text}'"),
            };
        }

        /// <summary>读取 label:value 形式的字段并校验标签。</summary>
        private static string ReadLabeledField(Reader reader, string label)
        {
            var token = reader.ExpectString();
            if (!token.StartsWith(label, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Expected field '{label}' but found '{token}'");
            }

            return Unescape(token.Substring(label.Length));
        }

        /// <summary>读取 count:N 形式的计数字段。</summary>
        private static int ReadCountField(Reader reader, string label)
        {
            var token = reader.ExpectString();
            if (!token.StartsWith(label, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Expected field '{label}' but found '{token}'");
            }

            return int.Parse(token.Substring(label.Length), CultureInfo.InvariantCulture);
        }

        /// <summary>全名拆分为（命名空间, 名）；无点号时命名空间为空。</summary>
        private static (string Namespace, string Name) SplitFullName(string fullName)
        {
            var lastDot = fullName.LastIndexOf('.');
            return lastDot < 0 ? ("", fullName) : (fullName.Substring(0, lastDot), fullName.Substring(lastDot + 1));
        }

        /// <summary>6e 跨库里程碑：从 fn 键提取库名（键格式 `库名!head[...]`；`!` 界符在 `[` 之前）。
        /// 兼容旧格式（无 `!`）与兼容入口（moduleName 为空）：回退 moduleName。</summary>
        private static string ExtractLibraryFromKey(string key, ReadContext context)
        {
            var bangIndex = key.IndexOf('!');
            if (bangIndex > 0 && key.IndexOf('[') > bangIndex)
            {
                return key.Substring(0, bangIndex);
            }

            return context.ModuleName;
        }

        /// <summary>变量键还原真实符号名：去掉 global:/函数键前缀与 #N 冲突后缀。</summary>
        private static string KeyToName(string key)
        {
            var name = key;
            var slash = name.LastIndexOf('/');
            if (slash >= 0)
            {
                name = name.Substring(slash + 1);
            }

            var hash = name.LastIndexOf('#');
            if (hash >= 0 && int.TryParse(name.Substring(hash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                name = name.Substring(0, hash);
            }

            return Unescape(name);
        }

    }
}
