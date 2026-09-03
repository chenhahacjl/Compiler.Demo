using System.Collections.Immutable;
using System.Linq;

namespace Cocoa.CodeAnalysis.Symbols
{
    /// <summary>
    /// 内置函数种类（功能层原语）。三后端（Evaluator/IL/native）按 <see cref="FunctionSymbol.BuiltinKind"/> 分发；
        /// 不依赖 `== BuiltinFunctions.X` 引用相等。
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
        // `.coa` 序列化经 GetByKindName → SystemObjectMembers.GetByKindName 解析。
        ObjectToString,
        ObjectGetHashCode,
        ObjectEquals,
        ObjectGetType,
        ObjectStaticEquals,
        ObjectReferenceEquals,
        TypeName,
        TypeFullName,
    }

        /// <summary>内置函数规格：名称/签名 + 种类（功能层声明）。</summary>
    internal sealed record BuiltinSpec(BuiltinKind Kind, string Name, TypeSymbol ReturnType, (string Name, TypeSymbol Type)[] Parameters);

    /// <summary>
    /// 内置函数（功能层）：规格表生成符号，三后端按 <see cref="BuiltinKind"/> 映射实现。
        /// 规范名为 C# 风格 PascalCase（Print/Input/Random/Sleep/Now/Exit，6e-M17）—
    /// syscall 声明 `syscall function Print(...)` 精确命中；旧小写调用 `print(...)` 由
    /// <see cref="GetByName"/> 大小写不敏感回退兼容（Step 3 迁移后移除）。
        /// 新增功能层原语 = 1 行规格 + 三后端各 1 个 kind case + 1 个 IL 方法引用。
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
        /// 输出字符串并换行: void WriteLine(any text)（Console.WriteLine）
        /// </summary>
        public static readonly FunctionSymbol WriteLine = Create(BuiltinKind.WriteLine);

        /// <summary>
        /// 输出字符串不换行: void Write(any text)（Console.Write）
        /// </summary>
        public static readonly FunctionSymbol Write = Create(BuiltinKind.Write);

        /// <summary>
        /// 输入字符串: string ReadLine()（Console.ReadLine）
        /// </summary>
        public static readonly FunctionSymbol ReadLine = Create(BuiltinKind.ReadLine);

        /// <summary>
        /// 读取按键: char ReadKey(bool intercept)（Console.ReadKey(...).KeyChar）
        /// </summary>
        public static readonly FunctionSymbol ReadKey = Create(BuiltinKind.ReadKey);

        /// <summary>
        /// 随机数: int Random(int max)
        /// </summary>
        public static readonly FunctionSymbol Random = Create(BuiltinKind.Random);

        /// <summary>
        /// 休眠: void Sleep(int ms)
        /// </summary>
        public static readonly FunctionSymbol Sleep = Create(BuiltinKind.Sleep);

        /// <summary>
        /// 系统启动后毫秒数: int TickCount()（Environment.TickCount，对齐底层 GetTickCount）
        /// </summary>
        public static readonly FunctionSymbol TickCount = Create(BuiltinKind.TickCount);

        /// <summary>
        /// 退出进程: void Exit(int code)
        /// </summary>
        public static readonly FunctionSymbol Exit = Create(BuiltinKind.Exit);

        /// <summary>
        /// 平方根: double Sqrt(double x)（Math.Sqrt）
        /// </summary>
        public static readonly FunctionSymbol Sqrt = Create(BuiltinKind.Sqrt);

        /// <summary>
        /// 扬声器蜂鸣: void Beep(int frequency, int duration)（Console.Beep）
        /// </summary>
        public static readonly FunctionSymbol Beep = Create(BuiltinKind.Beep);

        /// <summary>双精度转字符串: string DoubleToString(double value)（facade System.Double.ToString 的底层原语）</summary>
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
        /// 获取所有内置函数
        /// </summary>
        /// <returns></returns>
        internal static IEnumerable<FunctionSymbol> GetAll()
            => _specs.Select(s => GetByKind(s.Kind)!);

        /// <summary>按种类查找内置函数。</summary>
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

        /// <summary>按名查找内置函数（`.coa` 反序列化时复用单例，保证发射器识别内置；大小写不敏感——syscall 声明可用 PascalCase 如 `Random` 命中 `random`）。</summary>
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

        /// <summary>按 BuiltinKind 枚举名解析（`.coa` v3 序列化用名称字符串，替代 int——改名不再依赖枚举顺序）。</summary>
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

