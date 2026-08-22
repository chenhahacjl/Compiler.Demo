using Cocoa.CodeAnalysis.Cod;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;

namespace Cocoa.CodeAnalysis
{
    /// <summary>
    /// 系统标准库加载器（标准库设计 §8）：目录内 `System*.cod` 自动发现加载（核心程序集
    /// `System.Core.cod` 强制首位），缓存。路径解析 `COCOA_STDLIB` env → 文件（仅加载该单文件，
    /// 向后兼容）或目录（枚举 `System*.cod`）；缺省 `AppContext.BaseDirectory`。缺文件降级
    /// （stdlib 不可用但不崩，绑定走空表）。编译器内建嵌入，与用户 `-r` 引用的 `.cod` 库
    /// （_codLibraries）独立。
    ///
    /// 多程序集模型（仿 .NET 共享框架）：核心实现集中单一 `System.Core.cod`（Object/String/Math/
    /// Console 等，对应 C# System.Private.CoreLib）；未来大功能模块（System.Net.cod / System.Json.cod，
    /// 对应 C# System.Net.Http / System.Text.Json）作为独立实现程序集放入目录即自动加载。
    /// 约束：库体内禁止跨 `.cod` 调用（各模块须自包含；跨库符号调和为独立里程碑）。
    /// </summary>
    internal static class SystemLibrary
    {
        private static readonly object _sync = new();
        private static ImmutableArray<CodProgram> _cache = ImmutableArray<CodProgram>.Empty;
        private static bool _loaded;

        /// <summary>加载系统库（幂等，缓存，线程安全）；无可用文件返回空数组（降级，不抛）。</summary>
        public static ImmutableArray<CodProgram> Load()
        {
            lock (_sync)
            {
                if (!_loaded)
                {
                    _loaded = true;
                    _cache = LoadCore();
                }

                return _cache;
            }
        }

        private static ImmutableArray<CodProgram> LoadCore()
        {
            var builder = ImmutableArray.CreateBuilder<CodProgram>();
            var env = Environment.GetEnvironmentVariable("COCOA_STDLIB");

            // COCOA_STDLIB 指向单个文件 → 仅加载该文件（Step 2 语义向后兼容）
            if (!string.IsNullOrEmpty(env) && File.Exists(env))
            {
                if (TryLoad(env, out var single))
                {
                    builder.Add(single);
                }

                return builder.ToImmutable();
            }

            var directory = !string.IsNullOrEmpty(env) && Directory.Exists(env)
                ? env
                : AppContext.BaseDirectory;

            var files = Directory.EnumerateFiles(directory, "System*.cod")
                                 .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                                 .ToList();

            // 核心程序集强制首位（C# 核心库先行语义；注入本身序无关，此为准入防御）
            var core = files.FirstOrDefault(f => Path.GetFileName(f).Equals("System.Core.cod", StringComparison.OrdinalIgnoreCase));
            if (core != null)
            {
                files.Remove(core);
                files.Insert(0, core);
            }

            foreach (var file in files)
            {
                if (TryLoad(file, out var program))
                {
                    builder.Add(program);
                }
            }

            return builder.ToImmutable();
        }

        private static bool TryLoad(string path, out CodProgram program)
        {
            try
            {
                program = CodSerializer.Load(path);
                return true;
            }
            catch (Exception)
            {
                // 系统库损坏 → 逐文件降级（跳过该文件，不影响其余），不影响用户程序编译
                program = null!;
                return false;
            }
        }

        /// <summary>测试用：清缓存（便于指向不同 System.cod 目录/文件）。</summary>
        internal static void Reset()
        {
            lock (_sync)
            {
                _cache = ImmutableArray<CodProgram>.Empty;
                _loaded = false;
            }
        }
    }
}
