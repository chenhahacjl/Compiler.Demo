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
        Print,
        Input,
        Random,
    }

    /// <summary>内置函数规格：名称/签名 + 种类（功能层声明）。</summary>
    internal sealed record BuiltinSpec(BuiltinKind Kind, string Name, TypeSymbol ReturnType, (string Name, TypeSymbol Type)[] Parameters);

    /// <summary>
    /// 内置函数（功能层）：规格表生成符号，三后端按 <see cref="BuiltinKind"/> 映射实现。
    /// 新增功能层原语 = 1 行规格 + 三后端各 1 个 kind case + 1 个 IL 方法引用。
    /// </summary>
    internal static class BuiltinFunctions
    {
        private static readonly ImmutableArray<BuiltinSpec> _specs = ImmutableArray.Create(
            new BuiltinSpec(BuiltinKind.Print, "print", TypeSymbol.Void, new[] { ("text", TypeSymbol.Any) }),
            new BuiltinSpec(BuiltinKind.Input, "input", TypeSymbol.String, System.Array.Empty<(string, TypeSymbol)>()),
            new BuiltinSpec(BuiltinKind.Random, "random", TypeSymbol.Int32, new[] { ("max", TypeSymbol.Int32) }));

        /// <summary>
        /// 输出字符串: void print(string text)
        /// </summary>
        public static readonly FunctionSymbol Print = Create(BuiltinKind.Print);

        /// <summary>
        /// 输入字符串: string input()
        /// </summary>
        public static readonly FunctionSymbol Input = Create(BuiltinKind.Input);

        /// <summary>
        /// 随机数: int random(int max)
        /// </summary>
        public static readonly FunctionSymbol Random = Create(BuiltinKind.Random);

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
                BuiltinKind.Print => Print,
                BuiltinKind.Input => Input,
                BuiltinKind.Random => Random,
                _ => null,
            };
        }

        /// <summary>按名查找内置函数（`.cod` 反序列化时复用单例，保证发射器识别内置）。</summary>
        internal static FunctionSymbol? GetByName(string name)
        {
            foreach (var spec in _specs)
            {
                if (spec.Name == name)
                {
                    return GetByKind(spec.Kind);
                }
            }

            return null;
        }
    }
}
