using Cocoa.CodeAnalysis.Cod;
using System;
using System.IO;

namespace Cocoa.CodeAnalysis
{
    /// <summary>
    /// 系统标准库（System.cod）加载器（标准库设计 §8）：路径解析 `COCOA_STDLIB` env →
    /// `AppContext.BaseDirectory/System.cod`，缓存；缺文件降级（stdlib 不可用但不崩，
    /// 绑定走空表）。编译器内建嵌入，与用户 `-r` 引用的 `.cod` 库（_codLibraries）独立。
    /// </summary>
    internal static class SystemLibrary
    {
        private static CodProgram? _cache;
        private static bool _loaded;

        /// <summary>加载系统库（幂等，缓存）；找不到返回 null（降级，不抛）。</summary>
        public static CodProgram? Load()
        {
            if (_loaded)
            {
                return _cache;
            }

            _loaded = true;
            var path = ResolvePath();
            if (path == null || !File.Exists(path))
            {
                return null;
            }

            try
            {
                _cache = CodSerializer.Load(path);
            }
            catch (Exception)
            {
                // 系统库损坏 → 降级为不可用，不影响用户程序编译
                _cache = null;
            }

            return _cache;
        }

        private static string? ResolvePath()
        {
            var env = Environment.GetEnvironmentVariable("COCOA_STDLIB");
            if (!string.IsNullOrEmpty(env) && File.Exists(env))
            {
                return env;
            }

            var baseDir = Path.Combine(AppContext.BaseDirectory, "System.cod");
            return File.Exists(baseDir) ? baseDir : null;
        }

        /// <summary>测试用：清缓存（便于指向不同 System.cod）。</summary>
        internal static void Reset()
        {
            _cache = null;
            _loaded = false;
        }
    }
}
