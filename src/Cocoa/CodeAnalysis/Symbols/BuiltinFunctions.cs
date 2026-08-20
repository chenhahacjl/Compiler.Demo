using System.Collections.Immutable;
using System.Reflection;

namespace Cocoa.CodeAnalysis.Symbols
{
    /// <summary>
    /// 内置函数
    /// </summary>
    internal static class BuiltinFunctions
    {
        /// <summary>
        /// 输出字符串: void print(string text)
        /// </summary>
        public static readonly FunctionSymbol Print = new FunctionSymbol("print", ImmutableArray.Create(new ParameterSymbol("text", TypeSymbol.Any, 0)), TypeSymbol.Void);
        /// <summary>
        /// 输入字符串: string input()
        /// </summary>
        public static readonly FunctionSymbol Input = new FunctionSymbol("input", ImmutableArray<ParameterSymbol>.Empty, TypeSymbol.String);
        /// <summary>
        /// 随机数: int random(int max)
        /// </summary>
        public static readonly FunctionSymbol Random = new FunctionSymbol("random", ImmutableArray.Create(new ParameterSymbol("max", TypeSymbol.Int32, 0)), TypeSymbol.Int32);

        /// <summary>
        /// 获取所有内置函数
        /// </summary>
        /// <returns></returns>
        internal static IEnumerable<FunctionSymbol> GetAll()
            => typeof(BuiltinFunctions).GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic)
                                       .Where(f => f.FieldType == typeof(FunctionSymbol))
                                       .Select(f => (FunctionSymbol?)f.GetValue(null))
                                       .Where(f => f != null)
                                       .Select(f => f!);

        /// <summary>按名查找内置函数（`.cod` 反序列化时复用单例，保证发射器识别内置）。</summary>
        internal static FunctionSymbol? GetByName(string name)
        {
            foreach (var function in GetAll())
            {
                if (function.Name == name)
                {
                    return function;
                }
            }

            return null;
        }
    }
}
