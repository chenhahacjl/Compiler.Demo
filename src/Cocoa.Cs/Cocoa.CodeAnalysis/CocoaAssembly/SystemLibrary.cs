using Cocoa.CodeAnalysis.CocoaAssembly;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;

namespace Cocoa.CodeAnalysis
{
    /// <summary>
    /// 系统标准库加载器（标准库设计 §8）：目录内 `System*.coa` 自动发现加载（核心程序集
    /// `System.Core.coa` 强制首位），缓存。路径解析 `COCOA_STDLIB` env → 文件（仅加载该单文件，
    /// 向后兼容）或目录（枚举 `System*.coa`）；缺省时先从 exe 目录向上探测仓库中央库仓
    /// `libs/`（开发期 bins 副本缺失/过旧时的兜底，见 <see cref="FindLibsStore"/>），
    /// 未命中回落 `AppContext.BaseDirectory`。缺文件降级（stdlib 不可用但不崩，绑定走空表）。
    /// 编译器内建嵌入，与用户 `-r` 引用的 `.coa` 库（_codLibraries）独立。
    ///
    /// 多程序集模型（仿 .NET 共享框架）：核心实现集中单一 `System.Core.coa`（Object/String/Math/
    /// Console 等，对应 C# System.Private.CoreLib）；未来大功能模块（System.Net.coa / System.Json.coa，
    /// 对应 C# System.Net.Http / System.Text.Json）作为独立实现程序集放入目录即自动加载。
    /// 6e 跨库里程碑：各模块可跨库调用——加载按依赖序（System.Core 首位），后续库以已加载库为
    /// external 合并符号表（复用实例），FnKey 带库维度前缀消歧。
    /// </summary>
    internal static class SystemLibrary
    {
        private static readonly object _sync = new();
        private static ImmutableArray<CoaProgram> _cache = ImmutableArray<CoaProgram>.Empty;
        private static bool _loaded;

        /// <summary>加载系统库（幂等，缓存，线程安全）；无可用文件返回空数组（降级，不抛）。</summary>
        public static ImmutableArray<CoaProgram> Load()
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

        private static ImmutableArray<CoaProgram> LoadCore()
        {
            var builder = ImmutableArray.CreateBuilder<CoaProgram>();
            var env = Environment.GetEnvironmentVariable("COCOA_STDLIB");

            // COCOA_STDLIB 指向单个文件 → 仅加载该文件（Step 2 语义向后兼容）
            if (!string.IsNullOrEmpty(env) && File.Exists(env))
            {
                if (TryLoad(env, ImmutableArray<CoaProgram>.Empty, out var single))
                {
                    builder.Add(single);
                }

                return builder.ToImmutable();
            }

            var directory = !string.IsNullOrEmpty(env) && Directory.Exists(env)
                ? env
                : FindLibsStore(AppContext.BaseDirectory) ?? AppContext.BaseDirectory;

            var files = Directory.EnumerateFiles(directory, "System*.coa")
                                 .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                                 .ToList();

            // 核心程序集强制首位（C# 核心库先行语义；注入本身序无关，此为准入防御）
            var core = files.FirstOrDefault(f => Path.GetFileName(f).Equals("System.Core.coa", StringComparison.OrdinalIgnoreCase));
            if (core != null)
            {
                files.Remove(core);
                files.Insert(0, core);
            }

            // 6e 跨库里程碑：累加式加载——已加载库作为 external 传给后续文件（跨库符号合并复用实例）
            var loaded = new List<CoaProgram>();
            foreach (var file in files)
            {
                if (TryLoad(file, loaded.ToImmutableArray(), out var program))
                {
                    // 程序集名 = 库名 + .Managed 后缀（动态链接 AssemblyRef 与按需生成部署依据，阶段 A）
                    var baseName = Path.GetFileNameWithoutExtension(file);
                    program.Name = CocoaAssembly.CoaAssemblyNaming.ManagedAssemblyName(baseName);
                    program.SourcePath = Path.GetFullPath(file);
                    builder.Add(program);
                    loaded.Add(program);
                }
            }

            return builder.ToImmutable();
        }

        private static bool TryLoad(string path, ImmutableArray<CoaProgram> external, out CoaProgram program)
        {
            try
            {
                program = CoaSerializer.Load(path, external);
                return true;
            }
            catch (Exception)
            {
                // 系统库损坏 → 逐文件降级（跳过该文件，不影响其余），不影响用户程序编译
                program = null!;
                return false;
            }
        }

        /// <summary>
        /// 从 startDirectory 逐级向上探测仓库中央库仓：名为 `libs` 且含 `System.Core.coa`
        /// 的祖先目录。开发期 bins 副本被构建清空/过旧时的兜底发现路径；仓库外部署
        /// （无 libs 祖先）自然回落 exe 旁目录。测试可直接注入起始目录。
        /// </summary>
        internal static string? FindLibsStore(string? startDirectory)
        {
            try
            {
                var current = Path.GetFullPath(startDirectory ?? ".");
                while (true)
                {
                    var libs = Path.Combine(current, "libs");
                    if (File.Exists(Path.Combine(libs, "System.Core.coa")))
                    {
                        return libs;
                    }

                    var parent = Directory.GetParent(current);
                    if (parent == null || string.Equals(parent.FullName, current, StringComparison.OrdinalIgnoreCase))
                    {
                        return null;
                    }

                    current = parent.FullName;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>测试用：清缓存（便于指向不同 System.coa 目录/文件）。</summary>
        internal static void Reset()
        {
            lock (_sync)
            {
                _cache = ImmutableArray<CoaProgram>.Empty;
                _loaded = false;
            }
        }
    }
}
