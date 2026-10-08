using System.Linq;

namespace Cocoa.CodeAnalysis.Symbols
{
    /// <summary>
    /// 符号确定性排序辅助：函数按（ContainingClass.FullName + 命名空间 + 名字 + 参数签名）Ordinal 排序键。
    /// IL（IlEmitter）与 Native（MirToLir）两后端发射顺序须可复现——
    /// program.Functions 是 ImmutableDictionary，引用哈希进程随机，先后端各自实现同键（陈旧一步即漂移）。
    /// </summary>
    public static class SymbolSorting
    {
        public static string FunctionSortKey(FunctionSymbol function)
        {
            var owner = function.ContainingClass?.FullName ?? "";
            var parameters = string.Join(",", function.Parameters.Select(p => p.Type.ToString()));
            return $"{owner}|{function.Namespace}|{function.Name}|{parameters}";
        }
    }
}