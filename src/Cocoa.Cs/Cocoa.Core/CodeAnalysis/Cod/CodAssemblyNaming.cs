using System;

namespace Cocoa.CodeAnalysis.Cod
{
    /// <summary>
    /// `.cod` 库的托管程序集命名规则（动态链接阶段 A）：
    /// 托管 dll 名需同时避开两类冲突——① .NET 框架门面程序集名（<c>System.Core</c>/<c>System.Net</c> 等，
    /// 加载器按策略绑到框架门面导致 TypeLoadException）；② 本仓库引擎程序集名（<c>Cocoa.Core</c>/<c>Cocoa.Compiler</c>）。
    /// 规则：<c>System.X → CocoaStd.X</c>（如 <c>System.Core → CocoaStd.Core</c>）；用户库保持原名
    /// （若用户库恰取保留名，构建时由部署警告提示）。`.cod` 文件名与目录发现约定（System*.cod）不变。
    /// </summary>
    internal static class CodAssemblyNaming
    {
        private const string Prefix = "CocoaStd.";

        public static string ManagedAssemblyName(string codBaseName)
        {
            if (string.IsNullOrEmpty(codBaseName))
            {
                return codBaseName;
            }

            return codBaseName.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
                ? Prefix + codBaseName.Substring("System.".Length)
                : codBaseName;
        }
    }
}
