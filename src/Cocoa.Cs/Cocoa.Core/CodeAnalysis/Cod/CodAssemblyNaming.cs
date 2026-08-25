namespace Cocoa.CodeAnalysis.Cod
{
    /// <summary>
    /// `.cod` 库的托管产物命名规则（动态链接阶段 A）：
    /// 托管 dll = 库名 + 实现后缀——<c>X.Managed.dll</c>（IL）/ <c>X.Native.x64.dll</c>（native，预留）。
    /// 后缀本身即防碰撞机制：.NET 加载器的门面/统一映射按精确简单名匹配（如 System.Core），
    /// 带 <c>.Managed</c>/<c>.Native.x64</c> 的名字不在任何映射表中，任何库名都天然安全。
    ///
    /// `.cod` 文件名与目录发现约定（System*.cod 等）不受影响；消费方构建时按需生成并部署
    /// （缺失或 stamp 过期即现场再生，见 ProjectBuilder EnsureManagedDlls）。
    /// </summary>
    internal static class CodAssemblyNaming
    {
        public const string ManagedSuffix = ".Managed";

        /// <summary>托管库程序集名（= dll 文件基名）：X → X.Managed。</summary>
        public static string ManagedAssemblyName(string codBaseName)
        {
            return codBaseName + ManagedSuffix;
        }

        /// <summary>native 库文件基名（阶段 B 预留）：X → X.Native.x64 / X.Native.x86。</summary>
        public static string NativeAssemblyName(string codBaseName, string architecture)
        {
            return codBaseName + ".Native." + architecture;
        }
    }
}
