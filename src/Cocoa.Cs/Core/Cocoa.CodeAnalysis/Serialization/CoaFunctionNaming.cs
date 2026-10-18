using System.Linq;
using Cocoa.CodeAnalysis.Serialization.CoaFormat;
using Cocoa.CodeAnalysis.Symbols;

namespace Cocoa.CodeAnalysis.Serialization
{
    /// <summary>
    /// 函数键（FnKey）公共契约（native-s1 S0 提取）：`.coa` v3 序列化函数键的统一公式。
    /// 写侧 <see cref="CoaRegistry"/> 与 native 库发射 / 消费侧（producer 导出名 `cocoa_+Sanitize(FnKey)`、
    /// consumer 导入名）共用同一公式——同一 coa 加载后重建同类符号必得同键，导出/导入自动对齐。
    /// 公式与既有 <c>CoaRegistry.ComputeFnKey</c> 逐字节一致（占位迁移，不改序列化字节）。
    /// </summary>
    public static class CoaFunctionNaming
    {
        /// <summary>计算 FnKey：`[库名!]head[参数类型]`。库名取符号自带（跨库归属），缺省回退调用方模块名。</summary>
        public static string FnKey(FunctionSymbol fn, string moduleName)
        {
            var paramTypes = string.Join(",", fn.Parameters.Select(p =>
                (p.IsOut ? "out:" : p.IsRef ? "ref:" : "-") + CoaTypeCodec.TypeRef(p.Type)));
            var head = fn.ContainingClass != null
                ? fn.ContainingClass.FullName + "." + fn.Name
                : fn.Namespace.Length > 0 ? fn.Namespace + "." + fn.Name : fn.Name;
            var library = fn.ContainingLibrary ?? moduleName;
            return (library.Length > 0 ? library + "!" : "") + head + "[" + paramTypes + "]";
        }

        /// <summary>计算 FnKey（符号携带库名时使用；未带库名（如顶层编译期单例）为裸 head）。</summary>
        public static string FnKey(FunctionSymbol fn)
        {
            return FnKey(fn, fn.ContainingLibrary ?? "");
        }
    }
}