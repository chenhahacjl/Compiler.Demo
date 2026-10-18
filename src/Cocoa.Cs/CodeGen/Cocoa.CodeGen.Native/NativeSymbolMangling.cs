using System.Collections.Generic;
using System.Text;

namespace Cocoa.CodeGen.Native
{
    /// <summary>
    /// Native 共享框架命名契约（[Native共享框架设计.md §4] + [native-s1-实施计划.md S0]）——单一来源，producer 与 consumer 共用。
    /// 契约面：
    ///   模块名：每 `.coa` 一 DLL → <c>X.Native</c>（owner = <c>System.Core.Native</c>；arch 移出契约，仅部署目录区分）。
    ///   库函数导出：<c>cocoa_ + Sanitize(FnKey)</c>（FnKey 由 <c>CoaRegistry.ComputeFnKey</c> 序列化，同一 coa 加载后重建符号即得同键）。
    ///   运行时原语导出：<c>cocoa_rt_ + CamelToSnake(原语名)</c>（经 <see cref="RuntimePrimitiveCoverage.AllNames"/> 覆盖一致性锁校验）。
    /// 唯一性来源：class 方法/访问器/构造均含 ContainingClass.FullName 前缀；重载由 FnKey 参数段天然唯一。
    /// </summary>
    public static class NativeSymbolMangling
    {
        /// <summary>native 库文件基名后缀：X → X.Native。</summary>
        public const string NativeSuffix = ".Native";

        /// <summary>库函数导出前缀。</summary>
        public const string ExportPrefix = "cocoa_";

        /// <summary>运行时原语导出前缀。</summary>
        public const string RuntimeExportPrefix = "cocoa_rt_";

        /// <summary>
        /// owner 模块 PE 导入名（运行时 + System.Core 库体 + 类型表合并后的唯一状态持有者，恒被加载）。
        /// **必须以 `.dll` 结尾**：加载器按「扩展名有无」决定是否追加 `.dll`——名如 <c>System.Core.Native</c>
        /// 会被视为扩展名为 <c>Native</c>，搜索无扩展名的字面文件（STATUS_DLL_NOT_FOUND）；带 <c>.dll</c> 后按原样
        /// 匹配 <c>System.Core.Native.dll</c> 文件。
        /// </summary>
        public const string SystemCoreNativeName = "System.Core.Native.dll";

        // 运行时导出的「新增原语」（非 RuntimeFunctionEmitter 原语，S1 由 EmitNativeLibrary 注入）。
        public const string RtTypeGet = "cocoa_rt_type_get";
        public const string RtTypeRegister = "cocoa_rt_type_register";
        public const string RtHeapPtr = "cocoa_rt_heap_ptr";

        /// <summary>native 库文件基名（模块契约名）：X → X.Native。</summary>
        public static string LibraryModuleName(string coaBaseName) => coaBaseName + NativeSuffix;

        /// <summary>库函数导出名：cocoa_ + Sanitize(FnKey)。</summary>
        public static string ExportName(string fnKey) => ExportPrefix + Sanitize(fnKey);

        /// <summary>
        /// FnKey → 白名单导出段：保留 [0-9A-Za-z_.]；`!`/`[`/`]`/`-` 各映射一个 `_`（不跨字符合并，
        /// 契约以设计样例 <c>System.Core!System.Math.Abs[-@f64]</c> → <c>System.Core_System.Math.Abs__f64</c> 为准）；
        /// 其余字符（如 <c>@</c>）剔除；末尾分隔符裁去。
        /// </summary>
        public static string Sanitize(string fnKey)
        {
            var sb = new StringBuilder(fnKey.Length);
            var lastSep = false;
            for (var i = 0; i < fnKey.Length; i++)
            {
                var c = fnKey[i];
                if (IsKeep(c))
                {
                    sb.Append(c);
                    lastSep = false;
                }
                else if (IsSeparator(c))
                {
                    sb.Append('_');
                    lastSep = true;
                }
                else
                {
                    // 剔除（不触发分隔符折叠，也保持 lastSep 原状；末尾剔除自然裁去尾部分隔符）
                    _ = lastSep;
                }
            }

            // 裁去末尾残留分隔符（如参数段收尾的 `]`）
            var result = sb.ToString();
            if (result.Length > 0 && result[^1] == '_')
            {
                result = result[..^1];
            }

            return result;
        }

        /// <summary>保留字符：ASCII 字母/数字/下划线/点。</summary>
        private static bool IsKeep(char c)
            => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_' || c == '.';

        /// <summary>映射为 `_` 的分隔字符（`!` 库前缀 / `[]` 参数段 / `-` 参数前缀；`@` 剔除）。</summary>
        private static bool IsSeparator(char c) => c == '!' || c == '[' || c == ']' || c == '-';

        /// <summary>运行时原语导出名特例表（默认 = cocoa_rt_ + CamelToSnake）。</summary>
        private static readonly Dictionary<string, string> RuntimeExportOverrides = new()
        {
            ["Sha256Hash"] = "sha256_hash",
        };

        /// <summary>运行时原语导出名：cocoa_rt_ + CamelToSnake(name)。</summary>
        public static string RtExportName(string primitiveName)
        {
            var snake = RuntimeExportOverrides.TryGetValue(primitiveName, out var exportOverride)
                ? exportOverride
                : CamelToSnake(primitiveName);
            return RuntimeExportPrefix + snake;
        }

        /// <summary>CamelCase → snake_case：Alloc→alloc、NewArray→new_array、StackOverflow→stack_overflow。</summary>
        public static string CamelToSnake(string value)
        {
            var sb = new StringBuilder(value.Length + 4);
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (i > 0 && char.IsUpper(c) && !char.IsUpper(value[i - 1]))
                {
                    sb.Append('_');
                }

                sb.Append(char.ToLowerInvariant(c));
            }

            return sb.ToString();
        }
    }
}