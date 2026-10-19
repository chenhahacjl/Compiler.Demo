using System;
using System.IO;

namespace Cocoa.Build
{
    /// <summary>
    /// 内嵌默认应用图标（EXE.ico，嵌入 Cocoa.Build.dll 的 Resources\DefaultExe.ico）。
    /// 供"未配置任何图标文件"的可执行产物兜底——无需在工程/仓库目录放 EXE.ico 文件。
    /// </summary>
    public static class DefaultIcon
    {
        private const string EmbeddedNameSuffix = "DefaultExe.ico";

        /// <summary>返回内嵌 .ico 字节；资源缺失时抛 FileNotFoundException（构建配置错误，宁可暴露）。</summary>
        public static byte[] GetBytes()
        {
            var assembly = typeof(DefaultIcon).Assembly;
            foreach (var name in assembly.GetManifestResourceNames())
            {
                if (name.EndsWith(EmbeddedNameSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    using var stream = assembly.GetManifestResourceStream(name)!;
                    using var memory = new MemoryStream();
                    stream.CopyTo(memory);
                    return memory.ToArray();
                }
            }

            throw new FileNotFoundException(
                "内嵌默认图标缺失：请在 Cocoa.Build.csproj 以 EmbeddedResource 链接 src/Cocoa.Cs/Resources/EXE.ico 为 Resources/DefaultExe.ico。");
        }
    }
}
