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
                    // 閺嶅洩顔囬悥鎯板Ν閻愮懓鎯堢€涙劘濡悙鐧哥窗閸忓爼妫撮幏顒€褰块幑銏ｎ攽缂傗晞绻橀敍宀冣偓宀勬姜鐞涘苯鍞撮梻顓炴値
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
                    // 婢舵俺顢戦懞鍌滃仯閿涙艾鍘涢崶鐐插煂鐞涘矂顩婚敍宀勬４閹奉剙褰挎稉搴＄磻閹奉剙褰块崥灞藉灙
                    _w.WriteLine();
                    _w.Write(new string(' ', _depth * 2));
                }

                // 鐞涘苯鍞撮梻顓炴値閿涘牊妫ょ€涙劘濡悙鐧哥礆閹存牕鐣炬担宥呮倵闂傤厼鎮庨崸鍥︾瑝娑撹濮╅幑銏ｎ攽閳ユ柡鈧梻鏁辨稉瀣╃濞?Open/Field/End 閹稿娓剁€规矮缍?
                _w.Write(')');
                _lineStart = false;
            }

            /// <summary>鐎涙劘濡悙鐟扮磻閹奉剙褰块崜宥呯暰娴ｅ秴鍩屾稉瀣╃鐞涘瞼缂夋潻娑樺灙閿涘牆鍑￠崷銊攽妫ｆ牕鍨稉宥呭晙閹广垼顢戦敍澶堚偓?/summary>
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

        /// <summary>閸愭瑤鏅剁粭锕€褰垮▔銊ュ斀鐞涱煉绱伴崢濠氬櫢 + 閸欐垵鐨犳い鍝勭碍閿涘潟d 娴犲懐鏁ゆ禍搴㈠笓鎼村骏绱濇稉宥呭晸閸忋儲鏋冩禒璁圭礆閵?/summary>
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
            // 6e-G7锛氬紑鏀句綋鎼哄甫鍚庯紝閮ㄥ垎绗﹀彿锛堝 cod 娉ㄥ叆閾句笂鐨勫疄渚嬪寲鍓湰锛変笉缁?RegisterFunction鈥斺€?
            // 缂洪敭鏃跺洖閫€鍔ㄦ€佽绠楋紙鍏紡涓?Seal 涓€鑷达級锛岃鍐欎袱渚у绉板嵆鑷唇
            return _fnKeys.TryGetValue(fn, out var key) ? key : ComputeFnKey(fn);
        }

            public string VarKey(VariableSymbol v) => _varKeys[v];

            public void RegisterType(TypeSymbol type)
            {
                if (_ids.ContainsKey(type))
                {
                    return;
                }

                // 6e-G7 S1锛氬紑鏀剧被鍨嬪弬鏁拌嚜鎻忚堪锛坓cls 鍐?tpar 澹版槑 + !灞炰富.鍚?寮曠敤锛夛紝鏃犵嫭绔嬫潯鐩?
                if (type is TypeParameterSymbol)
                {
                    return;
                }

                // 6e-G7 S1锛氬疄渚嬪寲绫诲瀷 鈫?娉ㄥ唽娉涘瀷瀹氫箟涓庡叏閮ㄥ疄鍙傦紙渚濊禆鍏堣锛夛紱鏈綋鏃犵嫭绔嬫潯鐩紙寮曠敤澶?mangle 鑷弿杩帮級
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
                // 閸忔湹缍戦敍鍫濆敶瀵?閺佹壆绮嶉敍澶庡殰閹诲繗鍫敍灞炬￥闂団偓閻欘剛鐝涢弶锛勬窗
            }

            private void RegisterClassCore(NamedTypeSymbol classType)
            {
                // 6e-M19 M2-c閿涙艾鍞村鍝勫礋娓氬绱橲ystem.Object/System.Type閿涘绗夐崣?cls閳ユ柡鈧棁顕版笟褌绱伴柅鐘插毉閺傛壆琚惍鏉戞綎閸楁洑绶ラ崥灞肩閹嶇幢
                // 閸?systype 閹稿鍙忛崥宥嗘Ё鐏忓嫬娲栭崡鏇氱伐閿涘牊鍨氶崨姗€娼伴悽?Ensure 閸愬懎缂撳▔銊ュ弳閿涘奔绗夋惔蹇撳灙閸栨牭绱?
                if (SystemObjectMembers.IsBuiltinSystemClass(classType))
                {
                    Emitters.Add((w, r) => EmitBuiltinSystemClass(w, r, classType));
                    return;
                }

                // 6e-G7 S1锛氭硾鍨嬪畾涔夎蛋 gcls 涓撳睘鑺傜偣锛沢cls 蹇呴』鍏堜簬鍏堕潤鎬佹柟娉?fn 钀界洏
                // 锛坒n 鐨?ret/par 寮曠敤 !寮€鏀惧弬鏁帮紝璇讳晶闇€鍏堢粡 gcls 娉ㄥ唽闄愬畾閿級锛涜繛甯︽敞鍐岄潪寮€鏀剧被鍨嬩緷璧?
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

                // 缁粯鏌熷▔鏇窗鐎圭懓娅掔猾璇插弿闂堟瑦鈧緤绱檚yscall/extern 閸欏﹤鐢担鎾绘饯閹焦鏌熷▔鏇礉6e-M18閿涘缍旀稉铏瑰缁?fn 鎼村繐鍨崠鏍电幢鐎圭偘绶ラ弬瑙勭《/閺嬪嫰鈧姷鏁辩猾璇诧紦鏉╁洦鎶ら妴?
                // 娓氬顦婚敍姝刡ject 閸愬懎缂撻弬瑙勭《閿涘湣2-c閿涘鐢?BuiltinKind閿涘矁顕版笟褏绮￠崡鏇氱伐婢跺秶鏁ら柌宥呯紦閿涘矂銆忛梾蹇撶穿閻劌绨崚妤€瀵?
                // 6e-G7 S1/S2锛氭硾鍨嬪畾涔夌殑瀹炰緥鏂规硶/鏋勯€犱篃闅忓簱鎼哄甫锛堟秷璐规柟鍗曟€佸寲绱犳潗锛夛紱鍏朵綑瀹炰緥鏂规硶浠嶇敱绫诲３杩囨护
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

            /// <summary>閺€鍫曟肠鐎瑰本鍨氶崥搴ｇ埠娑撯偓閸涜棄鎮曢敍姘毐閺佷即鏁稉搴″綁闁插繘鏁敍鍫濆弿鐏炩偓 global:閸氬秴鐡ч敍娑樼湰闁?閸欏倹鏆?閸戣姤鏆熼柨?閸氬秴鐡ч敍娑樺暱缁愪礁濮?#2/#3閿涘鈧?/summary>
            /// <summary>FnKey 璁＄畻锛?e-G7 鎶藉彇锛夛細owner/ns 鍓嶇紑 + 鍚?+ [鍙傛暟绫诲瀷]锛涗粎宸?out/ref 鐨勯噸杞介敭涓嶅悓銆?/summary>
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

        /// <summary>娴?`.coa` 閺傚洣娆㈤崝鐘烘祰缁嬪绨梿鍡愨偓?/summary>
        /// <summary>Load `.coa` 文件。库名由文件名回填；`external` 为已加载的依赖库（供跨库符号合并）。</summary>
    }
}
