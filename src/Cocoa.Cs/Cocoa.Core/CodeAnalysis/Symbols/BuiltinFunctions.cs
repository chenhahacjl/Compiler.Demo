using System.Collections.Immutable;
using System.Linq;

namespace Cocoa.CodeAnalysis.Symbols
{
    /// <summary>
    /// 内置函数种类（功能层原语）。三后端（Evaluator/IL/native）按 <see cref="FunctionSymbol.BuiltinKind"/> 分发，
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
        Floor,
        Ceiling,
        Truncate,
        Round,
        Beep,
        Int32ToString,
        Int64ToString,
        DoubleToString,
        BooleanToString,
        CharToString,
    }

    /// <summary>内置函数规格：名称/签名 + 种类（功能层声明）。</summary>
    internal sealed record BuiltinSpec(BuiltinKind Kind, string Name, TypeSymbol ReturnType, (string Name, TypeSymbol Type)[] Parameters);

    /// <summary>
    /// 内置函数（功能层）：规格表生成符号，三后端按 <see cref="BuiltinKind"/> 映射实现。
    /// 规范名为 C# 风格 PascalCase（Print/Input/Random/Sleep/Now/Exit，6e-M17）——
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
            new BuiltinSpec(BuiltinKind.Floor, "Floor", TypeSymbol.Double, new[] { ("x", TypeSymbol.Double) }),
            new BuiltinSpec(BuiltinKind.Ceiling, "Ceiling", TypeSymbol.Double, new[] { ("x", TypeSymbol.Double) }),
            new BuiltinSpec(BuiltinKind.Truncate, "Truncate", TypeSymbol.Double, new[] { ("x", TypeSymbol.Double) }),
            new BuiltinSpec(BuiltinKind.Round, "Round", TypeSymbol.Double, new[] { ("x", TypeSymbol.Double) }),
            new BuiltinSpec(BuiltinKind.Beep, "Beep", TypeSymbol.Void, new[] { ("frequency", TypeSymbol.Int32), ("duration", TypeSymbol.Int32) }),
            new BuiltinSpec(BuiltinKind.Int32ToString, "Int32ToString", TypeSymbol.String, new[] { ("value", TypeSymbol.Int32) }),
            new BuiltinSpec(BuiltinKind.Int64ToString, "Int64ToString", TypeSymbol.String, new[] { ("value", TypeSymbol.Int64) }),
            new BuiltinSpec(BuiltinKind.DoubleToString, "DoubleToString", TypeSymbol.String, new[] { ("value", TypeSymbol.Double) }),
            new BuiltinSpec(BuiltinKind.BooleanToString, "BooleanToString", TypeSymbol.String, new[] { ("value", TypeSymbol.Boolean) }),
            new BuiltinSpec(BuiltinKind.CharToString, "CharToString", TypeSymbol.String, new[] { ("value", TypeSymbol.Char) }));

        /// <summary>
        /// 输出字符串并换行: void WriteLine(any text)（= Console.WriteLine）
        /// </summary>
        public static readonly FunctionSymbol WriteLine = Create(BuiltinKind.WriteLine);

        /// <summary>
        /// 输出字符串不换行: void Write(any text)（= Console.Write）
        /// </summary>
        public static readonly FunctionSymbol Write = Create(BuiltinKind.Write);

        /// <summary>
        /// 输入字符串: string ReadLine()（= Console.ReadLine）
        /// </summary>
        public static readonly FunctionSymbol ReadLine = Create(BuiltinKind.ReadLine);

        /// <summary>
        /// 读取按键: char ReadKey(bool intercept)（= Console.ReadKey(...).KeyChar）
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
        /// 平方根: double Sqrt(double x)（= Math.Sqrt）
        /// </summary>
        public static readonly FunctionSymbol Sqrt = Create(BuiltinKind.Sqrt);

        /// <summary>
        /// 向下取整: double Floor(double x)（= Math.Floor）
        /// </summary>
        public static readonly FunctionSymbol Floor = Create(BuiltinKind.Floor);

        /// <summary>
        /// 向上取整: double Ceiling(double x)（= Math.Ceiling）
        /// </summary>
        public static readonly FunctionSymbol Ceiling = Create(BuiltinKind.Ceiling);

        /// <summary>
        /// 向零截断: double Truncate(double x)（= Math.Truncate）
        /// </summary>
        public static readonly FunctionSymbol Truncate = Create(BuiltinKind.Truncate);

        /// <summary>
        /// 四舍五入（最近偶数）: double Round(double x)（= Math.Round，banker's rounding）
        /// </summary>
        public static readonly FunctionSymbol Round = Create(BuiltinKind.Round);

        /// <summary>
        /// 扬声器蜂鸣: void Beep(int frequency, int duration)（= Console.Beep）
        /// </summary>
        public static readonly FunctionSymbol Beep = Create(BuiltinKind.Beep);

        /// <summary>整数转字符串: string Int32ToString(int value)（facade System.Int32.ToString 的底层原语）</summary>
        public static readonly FunctionSymbol Int32ToString = Create(BuiltinKind.Int32ToString);

        /// <summary>长整数转字符串: string Int64ToString(long value)（facade System.Int64.ToString 的底层原语）</summary>
        public static readonly FunctionSymbol Int64ToString = Create(BuiltinKind.Int64ToString);

        /// <summary>双精度转字符串: string DoubleToString(double value)（facade System.Double.ToString 的底层原语）</summary>
        public static readonly FunctionSymbol DoubleToString = Create(BuiltinKind.DoubleToString);

        /// <summary>布尔转字符串: string BooleanToString(bool value)（"True"/"False"，facade System.Boolean.ToString 的底层原语）</summary>
        public static readonly FunctionSymbol BooleanToString = Create(BuiltinKind.BooleanToString);

        /// <summary>字符转字符串: string CharToString(char value)（facade System.Char.ToString 的底层原语）</summary>
        public static readonly FunctionSymbol CharToString = Create(BuiltinKind.CharToString);

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
                BuiltinKind.Floor => Floor,
                BuiltinKind.Ceiling => Ceiling,
                BuiltinKind.Truncate => Truncate,
                BuiltinKind.Round => Round,
                BuiltinKind.Beep => Beep,
                BuiltinKind.Int32ToString => Int32ToString,
                BuiltinKind.Int64ToString => Int64ToString,
                BuiltinKind.DoubleToString => DoubleToString,
                BuiltinKind.BooleanToString => BooleanToString,
                BuiltinKind.CharToString => CharToString,
                _ => null,
            };
        }

        /// <summary>按名查找内置函数（`.cod` 反序列化时复用单例，保证发射器识别内置；大小写不敏感——syscall 声明可用 PascalCase 如 `Random` 命中 `random`）。</summary>
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

        /// <summary>按 BuiltinKind 枚举名解析（`.cod` v3 序列化用名称字符串，替代 int——改名不再依赖枚举顺序）。</summary>
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
