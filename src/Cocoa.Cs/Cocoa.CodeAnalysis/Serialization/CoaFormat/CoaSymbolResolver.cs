using Cocoa.CodeAnalysis.Symbols;
using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Cocoa.CodeAnalysis.Serialization.CoaFormat
{
    /// <summary>.coa 读侧符号解析（3.2 收窄）：函数/变量/属主键反解。
    /// 类型记法读写对称在 <see cref="CoaTypeCodec"/>。实例化：构造收 <see cref="CoaReadContext"/>，
    /// 跨库符号经其 external 表合并。</summary>
    internal sealed class CoaSymbolResolver
    {
        private readonly CoaReadContext _context;

        public CoaSymbolResolver(CoaReadContext context)
        {
            _context = context;
        }

        public NamedTypeSymbol ResolveOwnerClass(string fullName)
        {
            // 6e-M19 M2-c：内建系统类（System.Object/System.Type）按单例解析（不从 cod 类型表读）。
            // 6f：对象根统一后符号名为 object；System.Object 为 .NET 互操作全名，等价映射。
            if (fullName == "System.Object" || fullName == "object")
            {
                return TypeSymbol.Any;
            }

            if (fullName == "System.Type")
            {
                return NamedTypeSymbol.SystemType;
            }

            if (!_context.TypesByName.TryGetValue(fullName, out var type) || type is not NamedTypeSymbol classType)
            {
                // 6e-G7/fix：实例化 mangle owner（`MyLib.Box`1#…` 或含多前缀的副本）直查无果 →
                // 复用 mangle 头反解（最长匹配 key+反引号子串），兜底回落定义类。
                var fromHead = ResolveOwnerClassFromHead(fullName);
                if (fromHead != null)
                {
                    return fromHead;
                }

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
        public NamedTypeSymbol? ResolveOwnerClassFromHead(string ownerText, string libraryName = "")
        {
            if (ownerText == "System.Object" || ownerText == "object")
            {
                return TypeSymbol.Any;
            }

            if (ownerText == "System.Type")
            {
                return NamedTypeSymbol.SystemType;
            }

            // 6f-3：库前缀优先——跨库同名类型按键归属库查其 TypesByName（复合键解析）
            if (!string.IsNullOrEmpty(libraryName))
            {
                foreach (var library in _context.ExternalLibraries)
                {
                    if (IsLibraryMatch(library, libraryName) &&
                        library.TypesByName.TryGetValue(ownerText, out var scoped) && scoped is NamedTypeSymbol scopedClass)
                    {
                        return scopedClass;
                    }
                }
            }

            if (_context.TypesByName.TryGetValue(ownerText, out var direct))
            {
                return direct as NamedTypeSymbol;
            }

            if (ownerText.Contains('`'))
            {
                // 实例化副本 mangle 双缀：找 TypesByName 中「最长 key + backtick」为 ownerText 子串
                // （InstanceTypeSymbol.FullName = ns + mangle，定义全名出现在 mangle 头部）。
                string? bestKey = null;
                foreach (var key in _context.TypesByName.Keys)
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

                if (bestKey != null && _context.TypesByName.TryGetValue(bestKey, out var best) && best is NamedTypeSymbol bestClass)
                {
                    return bestClass;
                }
            }

            return null;
        }

        public VariableSymbol ResolveVariable(string key)
        {
            if (!_context.VariablesByKey.TryGetValue(key, out var variable))
            {
                throw new InvalidDataException($"Unknown variable '{key}'");
            }

            return variable;
        }

        public FunctionSymbol ResolveFunction(string key)
        {
            if (_context.FunctionsByKey.TryGetValue(key, out var function))
            {
                return function;
            }

            // 6e 跨库里程碑：键带库前缀（`库名!head[...]`）——先剥离前缀，再按方法名+元数在
            // 本库 + external 库函数集中归一（消费方替换期再映射回实例化副本）。
            // 6f-3：库前缀参与消歧——同名跨库符号按归属库确定性择优（杜绝 first-load-wins）。
            var searchKey = key;
            var libraryName = "";
            var bangIndex = key.IndexOf('!');
            if (bangIndex > 0 && key.IndexOf('[') > bangIndex)
            {
                libraryName = key.Substring(0, bangIndex);
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
                    var ownerClass = ResolveOwnerClassFromHead(ownerText, libraryName);

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

                    var candidates = _context.Functions.Where(f =>
                        f.Name == methodName &&
                        f.Parameters.Length == parameterCount).ToList();

                    if (candidates.Count == 0)
                    {
                        foreach (var library in _context.ExternalLibraries)
                        {
                            candidates.AddRange(library.Functions.Where(f =>
                                f.Name == methodName &&
                                f.Parameters.Length == parameterCount));
                        }
                    }

                    // 6f-3：键归属库候选优先——同名跨库函数按库前缀确定性择优（只在该库内取唯一匹配）
                    if (candidates.Count > 1 && !string.IsNullOrEmpty(libraryName))
                    {
                        foreach (var library in _context.ExternalLibraries)
                        {
                            if (!IsLibraryMatch(library, libraryName))
                            {
                                continue;
                            }

                            var scoped = library.Functions.Where(f =>
                                f.Name == methodName &&
                                f.Parameters.Length == parameterCount).ToList();
                            if (scoped.Count > 1)
                            {
                                throw new InvalidDataException($"Ambiguous library function '{key}' in library '{libraryName}'");
                            }

                            if (scoped.Count == 1)
                            {
                                return scoped[0];
                            }

                            break;
                        }
                    }

                    if (candidates.Count == 1)
                    {
                        return candidates[0];
                    }

                    // 6f-2：外部库属主类方法兜底——本库 `(cls System.Console)` 桩只带方法名、无方法符号，
                    // 属主方法以 external（系统/依赖库）已加载的同名类为权威：按名+元数命中即复用其符号
                    // （跨库符号回归统一，Binder 按引用相等合并函数体）。6f-3：库前缀过滤避免跨库同名误配。
                    foreach (var library in _context.ExternalLibraries)
                    {
                        if (!string.IsNullOrEmpty(libraryName) && !IsLibraryMatch(library, libraryName))
                        {
                            continue;
                        }

                        if (!library.TypesByName.TryGetValue(ownerText, out var extType) ||
                            extType is not NamedTypeSymbol extOwner)
                        {
                            continue;
                        }

                        var extCandidates = extOwner.Methods.Where(m =>
                            m.Name == methodName &&
                            m.Parameters.Length == parameterCount).ToList();

                        if (extCandidates.Count == 1)
                        {
                            return extCandidates[0];
                        }

                        if (extCandidates.Count > 1)
                        {
                            throw new InvalidDataException($"Ambiguous external owner-class method '{key}'");
                        }
                    }

                    throw new InvalidDataException($"Unknown function '{key}' [debug head={head} owner={ownerText} ownerCandidates={(ownerClass?.Methods.Count(m => m.Name == methodName && m.Parameters.Length == parameterCount) ?? -1)} candidates={candidates.Count}]");
                }
            }

            throw new InvalidDataException($"Unknown function '{key}' [outer]");
        }

        /// <summary>6e 跨库里程碑：从 fn 键提取库名（键格式 `库名!head[...]`；`!` 界符在 `[` 之前）。
        /// 兼容旧格式（无 `!`）与兼容入口（moduleName 为空）：回退 moduleName。</summary>
        public string ExtractLibraryFromKey(string key)
        {
            var bangIndex = key.IndexOf('!');
            if (bangIndex > 0 && key.IndexOf('[') > bangIndex)
            {
                return key.Substring(0, bangIndex);
            }

            return _context.ModuleName;
        }

        /// <summary>变量键还原真实符号名：去掉 global:/函数键前缀与 #N 冲突后缀。</summary>
        public string KeyToName(string key)
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

            return CoaText.Unescape(name);
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
