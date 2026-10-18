using System;
using System.Collections.Generic;
using System.IO;

namespace Cocoa.Build
{
    /// <summary>
    /// 增量构建判定（.NET MSBuild Inputs/Outputs 时间戳模型，无 .cache/.stamp 文件）：
    /// <c>up-to-date ⇔ 主产物存在 且 产物.mtime ≥ max(输入.mtime)</c>，输入 = 源文件 ∪ 引用文件 ∪ .coproj[.user]。
    /// 改路径的选项（config/os/arch/backend/tfm/format）会落到不同 bin 子目录 → 产物缺失 → 天然重编；
    /// 改内容不改路径的选项经 .coproj 当输入（改项目文件即刷新 mtime）。与 .NET 相同的边界：
    /// 时钟回拨 / 一次性 CLI 开关（如 --doc）不改文件时可能不触发重编。
    /// </summary>
    public static class IncrementalCheck
    {
        /// <summary>主产物是否已是最新（输入全部不比产物新）。任一输入缺失 → 视为过期（重编）。</summary>
        public static bool IsUpToDate(string outputFile, IEnumerable<string> inputPaths)
        {
            if (string.IsNullOrEmpty(outputFile) || !File.Exists(outputFile))
            {
                return false;
            }

            long outputTicks;
            try
            {
                outputTicks = File.GetLastWriteTimeUtc(outputFile).Ticks;
            }
            catch (IOException)
            {
                return false;
            }

            foreach (var input in inputPaths)
            {
                if (string.IsNullOrEmpty(input))
                {
                    continue;
                }

                if (!File.Exists(input))
                {
                    return false;
                }

                long inputTicks;
                try
                {
                    inputTicks = File.GetLastWriteTimeUtc(input).Ticks;
                }
                catch (IOException)
                {
                    return false;
                }

                if (inputTicks > outputTicks)
                {
                    return false;
                }
            }

            return true;
        }
    }
}