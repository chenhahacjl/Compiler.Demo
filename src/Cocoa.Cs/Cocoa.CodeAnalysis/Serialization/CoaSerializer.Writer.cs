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
    internal static partial class CoaSerializer
    {
        private sealed class Writer
        {
            private readonly TextWriter _w;
            private readonly List<bool> _hasChild = new();
            private int _depth;
            private bool _lineStart = true;

            public Writer(TextWriter writer)
            {
                _w = writer;
            }

            public void Open(string kind)
            {
                if (_hasChild.Count > 0)
                {
                    // 标记父节点含子节点：其闭括号换行缩进，而非行内闭合
                    _hasChild[_hasChild.Count - 1] = true;
                }

                Indent();
                _w.Write('(');
                _w.Write(kind);
                _lineStart = false;
                _hasChild.Add(false);
                _depth++;
            }

            public void Field(object value)
            {
                _w.Write(' ');
                _w.Write(value);
                _lineStart = false;
            }

            public void End()
            {
                var hasChild = _hasChild[_hasChild.Count - 1];
                _hasChild.RemoveAt(_hasChild.Count - 1);
                _depth--;

                if (hasChild && !_lineStart)
                {
                    // 多行节点：先回到行首，闭括号与开括号同列
                    _w.WriteLine();
                    _w.Write(new string(' ', _depth * 2));
                }

                // 行内闭合（无子节点）或定位后闭合均不主动换行——由下一个 Open/Field/End 按需定位。
                _w.Write(')');
                _lineStart = false;
            }

            /// <summary>子节点开括号前定位到下一行缩进列（已在行首则不再换行）。</summary>
            private void Indent()
            {
                if (_depth == 0)
                {
                    return;
                }

                if (!_lineStart)
                {
                    _w.WriteLine();
                }

                _w.Write(new string(' ', _depth * 2));
                _lineStart = true;
            }
        }

        /// <summary>写侧符号注册表：去重 + 发射顺序（id 仅用于排序，不写入文件）。</summary>
        private sealed class Registry
        {
            private readonly Dictionary<object, int> _ids = new(ReferenceEqualityComparer.Instance);
            private readonly List<FunctionSymbol> _functions = new();
            private readonly List<(VariableSymbol Symbol, FunctionSymbol? Owner)> _variables = new();
            private readonly Dictionary<FunctionSymbol, string> _fnKeys = new(ReferenceEqualityComparer.Instance);
            private readonly Dictionary<object, string> _varKeys = new(ReferenceEqualityComparer.Instance);

            /// <summary>当前模块名（`.coa` 库名）：FnKey 库维度前缀的回退归属（符号未带 ContainingLibrary 时）。</summary>
            private readonly string _moduleName;

            public Registry(string moduleName)
            {
                _moduleName = moduleName;
            }

            /// <summary>调试：当前序列化函数名（WriteBodyEntry 设置，供 Unserializable 错误定位）。</summary>
            public string? CurrentFunctionName { get; set; }

            public List<Action<Writer, Registry>> Emitters { get; } = new();

            public string FnKey(FunctionSymbol fn)
        {
            // 6e-G7：开放体携带后，部分符号（如 cod 注入链上的实例化副本）不经 RegisterFunction——
            // 缺键时回退动态计算（公式与 Seal 一致），读写两侧对称即自洽
            return _fnKeys.TryGetValue(fn, out var key) ? key : ComputeFnKey(fn);
        }

            public string VarKey(VariableSymbol v) => _varKeys[v];

            public void RegisterType(TypeSymbol type)
            {
                if (_ids.ContainsKey(type))
                {
                    return;
                }

                // 6e-G7 S1：开放类型参数自描述（gcls 内 tpar 声明 + !属主.名引用），无独立条目。
                if (type is TypeParameterSymbol)
                {
                    return;
                }

                // 6e-G7 S1：实例化类型 → 注册泛型定义与全部实参（依赖先行）；本体无独立条目（引用处 mangle 自描述）
                if (type is InstantiatedTypeSymbol instantiated)
                {
                    _ids[type] = _ids.Count;
                    RegisterType(instantiated.GenericDefinition);
                    foreach (var argument in instantiated.TypeArguments)
                    {
                        RegisterType(argument);
                    }

                    return;
                }

                _ids[type] = _ids.Count;

                if (type is NamedTypeSymbol { TypeKind: not TypeKind.Enum } classType && type.SpecialType == SpecialType.None)
                {
                    RegisterClassCore(classType);
                }
                else if (type is NamedTypeSymbol { TypeKind: TypeKind.Enum } enumType)
                {
                    Emitters.Add((w, r) => EmitEnumSymbol(w, r, enumType));
                }
                // 其余（内建/数组）自描述，无需独立条目
            }

            private void RegisterClassCore(NamedTypeSymbol classType)
            {
                // 6e-M19 M2-c：内建单例（System.Object/System.Type）不发 cls——读侧会造出新类破坏单例同一性；
                // 改以 systype 按全名映射回单例（成员面由 Ensure 内建注入，不序列化）。
                if (SystemObjectMembers.IsBuiltinSystemClass(classType))
                {
                    Emitters.Add((w, r) => EmitBuiltinSystemClass(w, r, classType));
                    return;
                }

                // 6e-G7 S1：泛型定义走 gcls 专属节点；gcls 必须先于其静态方法 fn 落盘
                // （fn 的 ret/par 引用 !开放参数，读侧需先经 gcls 注册限定键）；连带注册非开放类型依赖
                if (classType.IsGenericDefinition)
                {
                    foreach (var iface in classType.Interfaces)
                    {
                        RegisterType(iface);
                    }

                    Emitters.Add((w, r) => EmitGenericClassSymbol(w, r, classType));

                    foreach (var typeParameter in classType.TypeParameters)
                    {
                        foreach (var constraint in typeParameter.ConstraintTypes)
                        {
                            RegisterType(constraint);
                        }
                    }

                    foreach (var field in classType.Fields)
                    {
                        RegisterType(field.Type);
                    }

                    foreach (var method in classType.Methods.Where(m => m.IsStatic))
                    {
                        RegisterFunction(method);
                    }

                    return;
                }

                foreach (var iface in classType.Interfaces)
                {
                    RegisterType(iface);
                }

                Emitters.Add((w, r) => EmitClassSymbol(w, r, classType));
            }

            public void RegisterFunction(FunctionSymbol fn)
            {
                if (_ids.ContainsKey(fn))
                {
                    return;
                }

                // 6e 跨库里程碑：非本库符号不入本库 fn 条目——跨库 callee 由依赖库（external）提供符号，
                // 本库只引用其键；避免重复声明致符号身份分裂（Binder 按引用相等合并函数体）。
                if (fn.ContainingLibrary != null &&
                    !string.Equals(fn.ContainingLibrary, _moduleName, StringComparison.Ordinal))
                {
                    return;
                }

                // 类方法：容器类全静态（syscall/extern 及带体静态方法，6e-M18）作为独立 fn 序列化；实例方法/构造由类壳过滤。
                // 例外：Object 内建方法（M2-c）按 BuiltinKind，读侧经单例复用重建，须随引用序列化。
                // 6e-G7 S1/S2：泛型定义的实例方法/构造也随库携带（消费方单态化素材）；其余实例方法仍由类壳过滤
                if (fn.ContainingClass != null && !fn.IsStatic &&
                    !SystemObjectMembers.IsBuiltinSystemClass(fn.ContainingClass) &&
                    !fn.ContainingClass.IsGenericDefinition)
                {
                    return;
                }

                _ids[fn] = _ids.Count;
                _functions.Add(fn);

                RegisterType(fn.ReturnType);
                foreach (var p in fn.Parameters)
                {
                    RegisterType(p.Type);
                }

                Emitters.Add((w, r) => EmitFunctionSymbol(w, r, fn));

                foreach (var p in fn.Parameters)
                {
                    _ids[p] = _ids.Count;
                    _variables.Add((p, fn));
                }
            }

            public void RegisterVariable(VariableSymbol v, FunctionSymbol? owner = null)
            {
                if (_ids.ContainsKey(v))
                {
                    return;
                }

                RegisterType(v.Type);

                _ids[v] = _ids.Count;
                _variables.Add((v, owner));
                Emitters.Add((w, r) => EmitVariableSymbol(w, r, v));
            }

            /// <summary>收集完成后统一命名：函数键与变量键（全局 global:名字；局部（参数）函数键/名字；冲突加 #2/#3）。</summary>
            /// <summary>FnKey 计算（6e-G7 抽取）：owner/ns 前缀 + 名 + [参数类型]；仅有 out/ref 的重载键不同。</summary>
            private string ComputeFnKey(FunctionSymbol fn)
            {
                var paramTypes = string.Join(",", fn.Parameters.Select(p =>
                    (p.IsOut ? "out:" : p.IsRef ? "ref:" : "") + TypeRef(p.Type)));
                var head = fn.ContainingClass != null
                    ? fn.ContainingClass.FullName + "." + fn.Name
                    : fn.Namespace.Length > 0 ? fn.Namespace + "." + fn.Name : fn.Name;
                // 6e 跨库里程碑：FnKey 带库维度前缀（`库名!head[...]`）。归属 = 符号带库名则用其库名
                // （跨库 callee：从其库加载的符号），否则回退当前模块名（本库声明的函数/编译期单例）。
                var library = fn.ContainingLibrary ?? _moduleName;
                return (library.Length > 0 ? library + "!" : "") + head + "[" + paramTypes + "]";
            }

            public void Seal()
            {
                foreach (var fn in _functions)
                {
                    _fnKeys[fn] = ComputeFnKey(fn);
                }

                var used = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (symbol, owner) in _variables)
                {
                    var baseKey = owner == null
                        ? "global:" + symbol.Name
                        : _fnKeys[owner] + "/" + symbol.Name;
                    var key = baseKey;
                    var suffix = 2;
                    while (!used.Add(key))
                    {
                        key = baseKey + "#" + suffix;
                        suffix++;
                    }

                    _varKeys[symbol] = key;
                }
            }
        }

        // ---------------------------------------------------------------- read

        /// <summary>从 `.coa` 文件加载程序集。</summary>
        /// <summary>Load `.coa` 文件。库名由文件名回填；`external` 为已加载的依赖库（供跨库符号合并）。</summary>
    }
}
