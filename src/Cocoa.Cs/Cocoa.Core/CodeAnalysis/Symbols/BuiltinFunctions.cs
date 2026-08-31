using System.Collections.Immutable;
using System.Linq;

namespace Cocoa.CodeAnalysis.Symbols
{
    /// <summary>
    /// 鍐呯疆鍑芥暟绉嶇被锛堝姛鑳藉眰鍘熻锛夈€備笁鍚庣锛圗valuator/IL/native锛夋寜 <see cref="FunctionSymbol.BuiltinKind"/> 鍒嗗彂锛?
    /// 涓嶄緷璧?`== BuiltinFunctions.X` 寮曠敤鐩哥瓑銆?
    /// </summary>
    public enum BuiltinKind
    {
        WriteLine,
        Write,
        ReadLine,
        ReadKey,
        Random,
        Sleep,
        TickCount,
        Exit,
        Sqrt,
        Beep,
        DoubleToString,
        StringFromChars,

        // ---- 加密（6e-G7 ⑤a）----
        Sha256Hash,

        // ---- 文件 IO（6e-G7 ④）----
        FileReadAllText,
        FileWriteAllText,
        FileExists,
        FileDelete,
        FileCopy,
        DirectoryExists,
        GetEnvironmentVariable,
        GetCurrentDirectory,
        SetCurrentDirectory,
        GetExecutablePath,

        // ---- 进程（P1-9）----
        LaunchProcess,

        // 6e-M19 M2-c：System.Object 内建成员（实例虚四方法 + 静态二方法）。
        // 不进 _specs 表——由 SystemObjectMembers 自持 spec/单例，避免污染 GetByName 全局名表；
        // `.cod` 序列化经 GetByKindName → SystemObjectMembers.GetByKindName 解析。
        ObjectToString,
        ObjectGetHashCode,
        ObjectEquals,
        ObjectGetType,
        ObjectStaticEquals,
        ObjectReferenceEquals,
        TypeName,
        TypeFullName,
    }

    /// <summary>鍐呯疆鍑芥暟瑙勬牸锛氬悕绉?绛惧悕 + 绉嶇被锛堝姛鑳藉眰澹版槑锛夈€?/summary>
    internal sealed record BuiltinSpec(BuiltinKind Kind, string Name, TypeSymbol ReturnType, (string Name, TypeSymbol Type)[] Parameters);

    /// <summary>
    /// 鍐呯疆鍑芥暟锛堝姛鑳藉眰锛夛細瑙勬牸琛ㄧ敓鎴愮鍙凤紝涓夊悗绔寜 <see cref="BuiltinKind"/> 鏄犲皠瀹炵幇銆?
    /// 瑙勮寖鍚嶄负 C# 椋庢牸 PascalCase锛圥rint/Input/Random/Sleep/Now/Exit锛?e-M17锛夆€斺€?
    /// syscall 澹版槑 `syscall function Print(...)` 绮剧‘鍛戒腑锛涙棫灏忓啓璋冪敤 `print(...)` 鐢?
    /// <see cref="GetByName"/> 澶у皬鍐欎笉鏁忔劅鍥為€€鍏煎锛圫tep 3 杩佺Щ鍚庣Щ闄わ級銆?
    /// 鏂板鍔熻兘灞傚師璇?= 1 琛岃鏍?+ 涓夊悗绔悇 1 涓?kind case + 1 涓?IL 鏂规硶寮曠敤銆?
    /// </summary>
    internal static class BuiltinFunctions
    {
        private static readonly ImmutableArray<BuiltinSpec> _specs = ImmutableArray.Create(
            new BuiltinSpec(BuiltinKind.WriteLine, "WriteLine", TypeSymbol.Void, new[] { ("text", TypeSymbol.Any) }),
            new BuiltinSpec(BuiltinKind.Write, "Write", TypeSymbol.Void, new[] { ("text", TypeSymbol.Any) }),
            new BuiltinSpec(BuiltinKind.ReadLine, "ReadLine", TypeSymbol.String, System.Array.Empty<(string, TypeSymbol)>()),
            new BuiltinSpec(BuiltinKind.ReadKey, "ReadKey", TypeSymbol.Char, new[] { ("intercept", TypeSymbol.Boolean) }),
            new BuiltinSpec(BuiltinKind.Random, "Random", TypeSymbol.Int32, new[] { ("max", TypeSymbol.Int32) }),
            new BuiltinSpec(BuiltinKind.Sleep, "Sleep", TypeSymbol.Void, new[] { ("ms", TypeSymbol.Int32) }),
            new BuiltinSpec(BuiltinKind.TickCount, "TickCount", TypeSymbol.Int32, System.Array.Empty<(string, TypeSymbol)>()),
            new BuiltinSpec(BuiltinKind.Exit, "Exit", TypeSymbol.Void, new[] { ("code", TypeSymbol.Int32) }),
            new BuiltinSpec(BuiltinKind.Sqrt, "Sqrt", TypeSymbol.Double, new[] { ("x", TypeSymbol.Double) }),
            new BuiltinSpec(BuiltinKind.Beep, "Beep", TypeSymbol.Void, new[] { ("frequency", TypeSymbol.Int32), ("duration", TypeSymbol.Int32) }),
            new BuiltinSpec(BuiltinKind.DoubleToString, "DoubleToString", TypeSymbol.String, new[] { ("value", TypeSymbol.Double) }),
            new BuiltinSpec(BuiltinKind.StringFromChars, "StringFromChars", TypeSymbol.String, new[] { ("chars", TypeSymbol.ArrayOf(TypeSymbol.Char)) }),
            new BuiltinSpec(BuiltinKind.FileReadAllText, "ReadAllText", TypeSymbol.String, new[] { ("path", TypeSymbol.String) }),
            new BuiltinSpec(BuiltinKind.FileWriteAllText, "WriteAllText", TypeSymbol.Void, new[] { ("path", TypeSymbol.String), ("text", TypeSymbol.String) }),
            new BuiltinSpec(BuiltinKind.FileExists, "Exists", TypeSymbol.Boolean, new[] { ("path", TypeSymbol.String) }),
            new BuiltinSpec(BuiltinKind.GetEnvironmentVariable, "GetEnvironmentVariable", TypeSymbol.String, new[] { ("name", TypeSymbol.String) }),
            new BuiltinSpec(BuiltinKind.GetCurrentDirectory, "GetCurrentDirectory", TypeSymbol.String, System.Array.Empty<(string, TypeSymbol)>()),
            new BuiltinSpec(BuiltinKind.GetExecutablePath, "GetExecutablePath", TypeSymbol.String, System.Array.Empty<(string, TypeSymbol)>()),
            new BuiltinSpec(BuiltinKind.FileDelete, "Delete", TypeSymbol.Void, new[] { ("path", TypeSymbol.String) }),
            new BuiltinSpec(BuiltinKind.FileCopy, "Copy", TypeSymbol.Void, new[] { ("src", TypeSymbol.String), ("dst", TypeSymbol.String) }),
            new BuiltinSpec(BuiltinKind.DirectoryExists, "DirectoryExists", TypeSymbol.Boolean, new[] { ("path", TypeSymbol.String) }),
            new BuiltinSpec(BuiltinKind.SetCurrentDirectory, "SetCurrentDirectory", TypeSymbol.Void, new[] { ("path", TypeSymbol.String) }),
            new BuiltinSpec(BuiltinKind.Sha256Hash, "Sha256Hash", TypeSymbol.ArrayOf(TypeSymbol.UInt8), new[] { ("data", TypeSymbol.ArrayOf(TypeSymbol.UInt8)) }),
            new BuiltinSpec(BuiltinKind.LaunchProcess, "LaunchProcess", TypeSymbol.Int32, new[] { ("path", TypeSymbol.String), ("args", TypeSymbol.String) }));

        /// <summary>
        /// 杈撳嚭瀛楃涓插苟鎹㈣: void WriteLine(any text)锛? Console.WriteLine锛?
        /// </summary>
        public static readonly FunctionSymbol WriteLine = Create(BuiltinKind.WriteLine);

        /// <summary>
        /// 杈撳嚭瀛楃涓蹭笉鎹㈣: void Write(any text)锛? Console.Write锛?
        /// </summary>
        public static readonly FunctionSymbol Write = Create(BuiltinKind.Write);

        /// <summary>
        /// 杈撳叆瀛楃涓? string ReadLine()锛? Console.ReadLine锛?
        /// </summary>
        public static readonly FunctionSymbol ReadLine = Create(BuiltinKind.ReadLine);

        /// <summary>
        /// 璇诲彇鎸夐敭: char ReadKey(bool intercept)锛? Console.ReadKey(...).KeyChar锛?
        /// </summary>
        public static readonly FunctionSymbol ReadKey = Create(BuiltinKind.ReadKey);

        /// <summary>
        /// 闅忔満鏁? int Random(int max)
        /// </summary>
        public static readonly FunctionSymbol Random = Create(BuiltinKind.Random);

        /// <summary>
        /// 浼戠湢: void Sleep(int ms)
        /// </summary>
        public static readonly FunctionSymbol Sleep = Create(BuiltinKind.Sleep);

        /// <summary>
        /// 绯荤粺鍚姩鍚庢绉掓暟: int TickCount()锛圗nvironment.TickCount锛屽榻愬簳灞?GetTickCount锛?
        /// </summary>
        public static readonly FunctionSymbol TickCount = Create(BuiltinKind.TickCount);

        /// <summary>
        /// 閫€鍑鸿繘绋? void Exit(int code)
        /// </summary>
        public static readonly FunctionSymbol Exit = Create(BuiltinKind.Exit);

        /// <summary>
        /// 骞虫柟鏍? double Sqrt(double x)锛? Math.Sqrt锛?
        /// </summary>
        public static readonly FunctionSymbol Sqrt = Create(BuiltinKind.Sqrt);

        /// <summary>
        /// 鎵０鍣ㄨ渹楦? void Beep(int frequency, int duration)锛? Console.Beep锛?
        /// </summary>
        public static readonly FunctionSymbol Beep = Create(BuiltinKind.Beep);

        /// <summary>鍙岀簿搴﹁浆瀛楃涓? string DoubleToString(double value)锛坒acade System.Double.ToString 鐨勫簳灞傚師璇級</summary>
        public static readonly FunctionSymbol DoubleToString = Create(BuiltinKind.DoubleToString);

        /// <summary>字符数组构造字符串: string StringFromChars(char[] chars)（6e-G7 ③a：StringBuilder 底座）。</summary>
        public static readonly FunctionSymbol StringFromChars = Create(BuiltinKind.StringFromChars);

        // ---- 文件 IO / 环境（6e-G7 ④）----
        public static readonly FunctionSymbol FileReadAllText = Create(BuiltinKind.FileReadAllText);
        public static readonly FunctionSymbol FileWriteAllText = Create(BuiltinKind.FileWriteAllText);
        public static readonly FunctionSymbol FileExists = Create(BuiltinKind.FileExists);
        public static readonly FunctionSymbol GetEnvironmentVariable = Create(BuiltinKind.GetEnvironmentVariable);
        public static readonly FunctionSymbol GetCurrentDirectory = Create(BuiltinKind.GetCurrentDirectory);
        public static readonly FunctionSymbol GetExecutablePath = Create(BuiltinKind.GetExecutablePath);
        public static readonly FunctionSymbol FileDelete = Create(BuiltinKind.FileDelete);
        public static readonly FunctionSymbol FileCopy = Create(BuiltinKind.FileCopy);
        public static readonly FunctionSymbol DirectoryExists = Create(BuiltinKind.DirectoryExists);
        public static readonly FunctionSymbol SetCurrentDirectory = Create(BuiltinKind.SetCurrentDirectory);
        public static readonly FunctionSymbol Sha256Hash = Create(BuiltinKind.Sha256Hash);
        public static readonly FunctionSymbol LaunchProcess = Create(BuiltinKind.LaunchProcess);

        private static FunctionSymbol Create(BuiltinKind kind)
        {
            var spec = _specs.First(s => s.Kind == kind);
            var parameters = spec.Parameters.Select((p, i) => new ParameterSymbol(p.Name, p.Type, i)).ToImmutableArray();
            return new FunctionSymbol(spec.Name, parameters, spec.ReturnType, builtinKind: kind);
        }

        /// <summary>
        /// 鑾峰彇鎵€鏈夊唴缃嚱鏁?
        /// </summary>
        /// <returns></returns>
        internal static IEnumerable<FunctionSymbol> GetAll()
            => _specs.Select(s => GetByKind(s.Kind)!);

        /// <summary>鎸夌绫绘煡鎵惧唴缃嚱鏁般€?/summary>
        internal static FunctionSymbol? GetByKind(BuiltinKind kind)
        {
            return kind switch
            {
                BuiltinKind.WriteLine => WriteLine,
                BuiltinKind.Write => Write,
                BuiltinKind.ReadLine => ReadLine,
                BuiltinKind.ReadKey => ReadKey,
                BuiltinKind.Random => Random,
                BuiltinKind.Sleep => Sleep,
                BuiltinKind.TickCount => TickCount,
                BuiltinKind.Exit => Exit,
                BuiltinKind.Sqrt => Sqrt,
                BuiltinKind.Beep => Beep,
                BuiltinKind.DoubleToString => DoubleToString,
                BuiltinKind.StringFromChars => StringFromChars,
                BuiltinKind.FileReadAllText => FileReadAllText,
                BuiltinKind.FileWriteAllText => FileWriteAllText,
                BuiltinKind.FileExists => FileExists,
                BuiltinKind.GetEnvironmentVariable => GetEnvironmentVariable,
                BuiltinKind.GetCurrentDirectory => GetCurrentDirectory,
                BuiltinKind.GetExecutablePath => GetExecutablePath,
                BuiltinKind.FileDelete => FileDelete,
                BuiltinKind.FileCopy => FileCopy,
                BuiltinKind.DirectoryExists => DirectoryExists,
                BuiltinKind.SetCurrentDirectory => SetCurrentDirectory,
                BuiltinKind.Sha256Hash => Sha256Hash,
                BuiltinKind.LaunchProcess => LaunchProcess,
                _ => null,
            };
        }

        /// <summary>鎸夊悕鏌ユ壘鍐呯疆鍑芥暟锛坄.cod` 鍙嶅簭鍒楀寲鏃跺鐢ㄥ崟渚嬶紝淇濊瘉鍙戝皠鍣ㄨ瘑鍒唴缃紱澶у皬鍐欎笉鏁忔劅鈥斺€攕yscall 澹版槑鍙敤 PascalCase 濡?`Random` 鍛戒腑 `random`锛夈€?/summary>
        internal static FunctionSymbol? GetByName(string name)
        {
            foreach (var spec in _specs)
            {
                if (string.Equals(spec.Name, name, System.StringComparison.OrdinalIgnoreCase))
                {
                    return GetByKind(spec.Kind);
                }
            }

            return null;
        }

        /// <summary>鎸?BuiltinKind 鏋氫妇鍚嶈В鏋愶紙`.cod` v3 搴忓垪鍖栫敤鍚嶇О瀛楃涓诧紝鏇夸唬 int鈥斺€旀敼鍚嶄笉鍐嶄緷璧栨灇涓鹃『搴忥級銆?/summary>
        internal static BuiltinKind? GetByKindName(string name)
        {
            foreach (var spec in _specs)
            {
                if (string.Equals(spec.Kind.ToString(), name, System.StringComparison.OrdinalIgnoreCase))
                {
                    return spec.Kind;
                }
            }

            return null;
        }
    }
}

