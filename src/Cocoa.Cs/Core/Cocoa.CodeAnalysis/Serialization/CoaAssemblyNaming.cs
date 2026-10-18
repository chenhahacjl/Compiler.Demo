namespace Cocoa.CodeAnalysis.Serialization
{
    /// <summary>
    /// `.coa` 库的产物命名规则（动态链接阶段 A）：
    /// 托管 dll = 库名 + 实现后缀——<c>X.Managed.dll</c>（IL）/ <c>X.Native.dll</c>（native，native-s1 起落地）。
    /// 后缀本身即防碰撞机制：.NET 加载器的门面/统一映射按精确简单名匹配（如 System.Core），
    /// 带 <c>.Managed</c>/<c>.Native</c> 的名字不在任何映射表中，任何库名都天然安全。
    /// arch 不进契约名（避免同名 map 表/导入串错位），由部署目录 <c>bin/&lt;Config&gt;/native/&lt;arch&gt;/</c> 区分。
    ///
    /// `.coa` 文件名与目录发现约定（System*.coa 等）不受影响；消费方构建时按需生成并部署
    /// （缺失或比 coa 源旧即现场再生，时间戳判定无 .stamp，见 ProjectBuilder EnsureManagedDlls）。
    /// </summary>
    public static class CoaAssemblyNaming
    {
        public const string ManagedSuffix = ".Managed";

        /// <summary>托管库程序集名（= dll 文件基名）：X → X.Managed。</summary>
        public static string ManagedAssemblyName(string coaBaseName)
        {
            return coaBaseName + ManagedSuffix;
        }

        /// <summary>native 库文件基名（契约名）：X → X.Native（每 `.coa` 一 DLL 形态，S1）。</summary>
        public static string NativeAssemblyName(string coaBaseName)
        {
            return coaBaseName + ".Native";
        }
    }
}
