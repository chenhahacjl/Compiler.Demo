using System;
using System.IO;

namespace Cocoa.Build
{
    /// <summary>
    /// 默认应用图标定位：生成可执行产物时若项目未显式配置 <c>ApplicationIcon</c>，
    /// 依次查找「项目目录内 EXE.ico」→「从项目目录逐级上溯的 resources/EXE.ico」——
    /// 复制 EXE.ico 到项目下即可自动生效；仓库根 resources/EXE.ico 作为兜底。
    /// </summary>
    public static class AppIconLocator
    {
        public const string FileName = "EXE.ico";

        /// <summary>返回存在的默认图标路径；找不到返回 null（构建方应优雅跳过，产物不带图标）。</summary>
        public static string? Locate(string? startDirectory)
        {
            if (string.IsNullOrWhiteSpace(startDirectory))
            {
                return null;
            }

            string full;
            try
            {
                full = Path.GetFullPath(startDirectory);
            }
            catch (Exception)
            {
                return null;
            }

            for (var dir = new DirectoryInfo(full); dir != null; dir = dir.Parent)
            {
                var local = Path.Combine(dir.FullName, FileName);
                if (File.Exists(local))
                {
                    return local;
                }

                var repoResources = Path.Combine(dir.FullName, "resources", FileName);
                if (File.Exists(repoResources))
                {
                    return repoResources;
                }
            }

            return null;
        }

        /// <summary>把默认图标复制到目标目录（返回是否成功；失败不抛——调用方可选忽略）。</summary>
        public static bool CopyInto(string targetDirectory, string? startDirectory)
        {
            try
            {
                var source = Locate(startDirectory);
                if (source == null)
                {
                    return false;
                }

                Directory.CreateDirectory(targetDirectory);
                File.Copy(source, Path.Combine(targetDirectory, FileName), overwrite: true);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}