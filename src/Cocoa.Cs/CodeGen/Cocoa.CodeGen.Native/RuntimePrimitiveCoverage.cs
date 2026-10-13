using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Cocoa.CodeGen.Native.Lir;
using Cocoa.Targeting;

namespace Cocoa.CodeGen.Native
{
    /// <summary>运行时原语的平台覆盖平台。<see cref="RuntimePrimitiveCoverage"/> 一致性锁用。</summary>
    public enum RuntimePrimitivePlatform
    {
        /// <summary>x64 与 x86 双平台均发射。</summary>
        All,

        /// <summary>仅 x86（x64 走内联指令，如 64 位除/余）。</summary>
        X86,

        /// <summary>仅 x64（预留：目前无 x64 独占运行时原语）。</summary>
        X64,
    }

    /// <summary>
    /// 运行时原语 × { IR 发射分片, 导入声明, 平台差异分支 } 一致性锁（3.4）：
    /// 仿 <c>BuiltinCoverage</c>，新增一个 <c>RuntimeFunctionEmitter</c> 发射的运行时原语必须同时在此
    /// 登记一行，否则 <c>RuntimePrimitiveCoverageTests</c> 报"发射的原语未声明覆盖"。
    /// 权威源是 <see cref="RuntimeEmitterLir.Append"/> 实际发射的函数集（经 <see cref="EmittedPrimitiveNames"/>），
    /// 声明表只记录人工维护的元数据（分片/平台/导入）；双向交叉由测试校验。
    /// </summary>
    public static class RuntimePrimitiveCoverage
    {
        /// <summary>声明表行：运行时原语名 → 发射分片 / 平台差异 / 导入声明。</summary>
        public sealed record RuntimePrimitiveCoverageRow(string Name, string Shard, RuntimePrimitivePlatform Platform, string Import);

        private static readonly ImmutableArray<RuntimePrimitiveCoverageRow> Rows = ImmutableArray.Create<RuntimePrimitiveCoverageRow>(
            // 主壳（RuntimeEmitterLir.cs）：声明式 import 派发
            new("FileCloseHandle", "Core", RuntimePrimitivePlatform.All, "kernel32"),

            // System 分片（RuntimeEmitterLir.System.cs）：控制台/随机/对象面/Object 虚表
            new("Input", "System", RuntimePrimitivePlatform.All, "kernel32"),
            new("ReadKey", "System", RuntimePrimitivePlatform.All, "kernel32"),
            new("Random", "System", RuntimePrimitivePlatform.All, "kernel32"),
            new("ExitProcess", "System", RuntimePrimitivePlatform.All, "kernel32"),
            new("TickCount", "System", RuntimePrimitivePlatform.All, "kernel32"),
            new("Sleep", "System", RuntimePrimitivePlatform.All, "kernel32"),
            new("Beep", "System", RuntimePrimitivePlatform.All, "kernel32"),
            new("ObjectEquals", "System", RuntimePrimitivePlatform.All, "-"),
            new("ObjectToString", "System", RuntimePrimitivePlatform.All, "-"),
            new("ObjectGetHashCode", "System", RuntimePrimitivePlatform.All, "-"),
            new("ObjectGetType", "System", RuntimePrimitivePlatform.All, "-"),
            new("TypeSimpleName", "System", RuntimePrimitivePlatform.All, "-"),
            new("ExternWidePtr0", "System", RuntimePrimitivePlatform.All, "-"),
            new("ExternWidePtr1", "System", RuntimePrimitivePlatform.All, "-"),
            new("ExternWidePtr2", "System", RuntimePrimitivePlatform.All, "-"),
            new("ExternWidePtr3", "System", RuntimePrimitivePlatform.All, "-"),

            // Strings 分片（RuntimeEmitterLir.Strings.cs）：字符串运算/解析/格式化
            new("ParseInt", "Strings", RuntimePrimitivePlatform.All, "-"),
            new("ParseBool", "Strings", RuntimePrimitivePlatform.All, "-"),
            new("Concat", "Strings", RuntimePrimitivePlatform.All, "-"),
            new("StrEquals", "Strings", RuntimePrimitivePlatform.All, "-"),
            new("Substring", "Strings", RuntimePrimitivePlatform.All, "-"),
            new("CharToString", "Strings", RuntimePrimitivePlatform.All, "-"),
            new("StringFormat", "Strings", RuntimePrimitivePlatform.All, "-"),
            new("PadString", "Strings", RuntimePrimitivePlatform.All, "-"),
            new("AllocStringFromBuf", "Strings", RuntimePrimitivePlatform.All, "-"),
            new("FormatHex", "Strings", RuntimePrimitivePlatform.All, "-"),
            new("FormatDecPad", "Strings", RuntimePrimitivePlatform.All, "-"),
            new("ScaleAssemble", "Strings", RuntimePrimitivePlatform.All, "-"),
            new("ApplyAlignment", "Strings", RuntimePrimitivePlatform.All, "-"),

            // Numerics 分片（RuntimeEmitterLir.Numerics.cs）：整数/浮点/格式化/缓冲
            new("WriteStr", "Numerics", RuntimePrimitivePlatform.All, "-"),
            new("BuildInt", "Numerics", RuntimePrimitivePlatform.All, "-"),
            new("Alloc", "Numerics", RuntimePrimitivePlatform.All, "-"),
            new("CopyChars", "Numerics", RuntimePrimitivePlatform.All, "-"),
            new("PrintString", "Numerics", RuntimePrimitivePlatform.All, "kernel32"),
            new("PrintInt", "Numerics", RuntimePrimitivePlatform.All, "kernel32"),
            new("WriteString", "Numerics", RuntimePrimitivePlatform.All, "kernel32"),
            new("WriteInt", "Numerics", RuntimePrimitivePlatform.All, "kernel32"),
            new("IntToString", "Numerics", RuntimePrimitivePlatform.All, "-"),
            new("DoubleToString", "Numerics", RuntimePrimitivePlatform.All, "-"),
            new("FormatFixed", "Numerics", RuntimePrimitivePlatform.All, "-"),
            new("FormatSci", "Numerics", RuntimePrimitivePlatform.All, "-"),
            new("DivChain", "Numerics", RuntimePrimitivePlatform.All, "-"),
            new("BigDiv", "Numerics", RuntimePrimitivePlatform.All, "-"),
            new("DoubleFixed", "Numerics", RuntimePrimitivePlatform.All, "-"),

            // Int64 分片（RuntimeEmitterLir.Int64.cs）：64 位运算（Idiv/Irem/Udiv/Urem 仅 x86）
            new("Int64ToString", "Int64", RuntimePrimitivePlatform.All, "-"),
            new("UInt64ToString", "Int64", RuntimePrimitivePlatform.All, "-"),
            new("ParseInt64", "Int64", RuntimePrimitivePlatform.All, "-"),
            new("Idiv64", "Int64", RuntimePrimitivePlatform.X86, "-"),
            new("Irem64", "Int64", RuntimePrimitivePlatform.X86, "-"),
            new("Udiv64", "Int64", RuntimePrimitivePlatform.X86, "-"),
            new("Urem64", "Int64", RuntimePrimitivePlatform.X86, "-"),

            // Arrays 分片（RuntimeEmitterLir.Arrays.cs）：数组/缓冲/错误/Bcrypt/进程
            new("NewArray", "Arrays", RuntimePrimitivePlatform.All, "-"),
            new("SliceArray", "Arrays", RuntimePrimitivePlatform.All, "-"),
            new("ArrayBoundsCheck", "Arrays", RuntimePrimitivePlatform.All, "-"),
            new("ArrayCopyElements", "Arrays", RuntimePrimitivePlatform.All, "-"),
            new("BuildArgs", "Arrays", RuntimePrimitivePlatform.All, "-"),
            new("DivByZero", "Arrays", RuntimePrimitivePlatform.All, "-"),
            new("StackOverflow", "Arrays", RuntimePrimitivePlatform.All, "-"),
            new("Sha256Hash", "Arrays", RuntimePrimitivePlatform.All, "bcrypt"),
            new("LaunchProcess", "Arrays", RuntimePrimitivePlatform.All, "kernel32"),

            // IO 分片（RuntimeEmitterLir.IO.cs）：文件/目录/环境
            new("StringFromChars", "IO", RuntimePrimitivePlatform.All, "-"),
            new("FileExists", "IO", RuntimePrimitivePlatform.All, "kernel32"),
            new("DirectoryExists", "IO", RuntimePrimitivePlatform.All, "kernel32"),
            new("CreateDirectory", "IO", RuntimePrimitivePlatform.All, "kernel32"),
            new("FileDelete", "IO", RuntimePrimitivePlatform.All, "kernel32"),
            new("FileCopy", "IO", RuntimePrimitivePlatform.All, "kernel32"),
            new("GetEnvironmentVariable", "IO", RuntimePrimitivePlatform.All, "kernel32"),
            new("GetCurrentDirectory", "IO", RuntimePrimitivePlatform.All, "kernel32"),
            new("GetExecutablePath", "IO", RuntimePrimitivePlatform.All, "kernel32"),
            new("SetCurrentDirectory", "IO", RuntimePrimitivePlatform.All, "kernel32"),
            new("FileReadAllBytes", "IO", RuntimePrimitivePlatform.All, "kernel32"),
            new("FileWriteAllBytes", "IO", RuntimePrimitivePlatform.All, "kernel32"),
            new("FileOpenHandle", "IO", RuntimePrimitivePlatform.All, "kernel32"),
            new("FileSizeHandle", "IO", RuntimePrimitivePlatform.All, "kernel32"),
            new("FileSeekHandle", "IO", RuntimePrimitivePlatform.All, "kernel32"),
            new("FileTellHandle", "IO", RuntimePrimitivePlatform.All, "kernel32"),
            new("FileReadHandle", "IO", RuntimePrimitivePlatform.All, "kernel32"),
            new("FileWriteHandle", "IO", RuntimePrimitivePlatform.All, "kernel32"),

            // Introspection 分片（RuntimeEmitterLir.Introspection.cs）
            new("SelfIntrospect", "Introspection", RuntimePrimitivePlatform.All, "kernel32"),

            // Delegates 分片（RuntimeEmitterLir.Delegates.cs）
            new("DelegateCombine", "Delegates", RuntimePrimitivePlatform.All, "-"),
            new("DelegateRemove", "Delegates", RuntimePrimitivePlatform.All, "-"),
            new("DelegateEquals", "Delegates", RuntimePrimitivePlatform.All, "-"));

        private static readonly ImmutableHashSet<string> KnownShards =
            ImmutableHashSet.Create("Core", "System", "Strings", "Numerics", "Int64", "Arrays", "IO", "Introspection", "Delegates");

        private static readonly ImmutableHashSet<string> KnownImports =
            ImmutableHashSet.Create("-", "kernel32", "bcrypt");

        public static ImmutableArray<RuntimePrimitiveCoverageRow> AllRows => Rows;

        public static ImmutableArray<string> AllNames => Rows.Select(r => r.Name).ToImmutableArray();

        /// <summary>权威发射集：把运行时挂接到空白 LIR 程序后枚举实际注册的函数名（与 NativeCodeEmitter 同一入口）。</summary>
        public static ImmutableArray<string> EmittedPrimitiveNames(TargetPlatform platform)
        {
            var program = new LirProgram("");
            RuntimeEmitterLir.Append(program, platform);
            return program.Functions.Select(f => f.Name).Distinct().ToImmutableArray();
        }

        /// <summary>发射了但未在声明表登记的原语名——必须为空，否则说明新增了运行时原语但忘记登记。</summary>
        public static ImmutableArray<string> UndeclaredEmittedNames(TargetPlatform platform)
        {
            var declared = AllNames.ToHashSet();
            return EmittedPrimitiveNames(platform).Where(n => !declared.Contains(n)).ToImmutableArray();
        }

        /// <summary>声明为应在该平台发射、但实际未发射的原语名——必须为空，否则说明声明表含虚假/改名残留条目。</summary>
        public static ImmutableArray<string> DeclaredButNotEmitted(TargetPlatform platform)
        {
            var emitted = EmittedPrimitiveNames(platform).ToHashSet();
            var isX64 = platform.Arch == Architecture.X64;
            return Rows
                .Where(r => r.Platform == RuntimePrimitivePlatform.All ||
                            (r.Platform == RuntimePrimitivePlatform.X86 && !isX64) ||
                            (r.Platform == RuntimePrimitivePlatform.X64 && isX64))
                .Select(r => r.Name)
                .Where(n => !emitted.Contains(n))
                .ToImmutableArray();
        }

        /// <summary>分片名不在已知发射分片集合的声明行。</summary>
        public static ImmutableArray<string> InvalidShards =>
            Rows.Where(r => !KnownShards.Contains(r.Shard)).Select(r => $"{r.Name}->{r.Shard}").ToImmutableArray();

        /// <summary>导入声明不在已知集合的声明行。</summary>
        public static ImmutableArray<string> InvalidImports =>
            Rows.Where(r => !KnownImports.Contains(r.Import)).Select(r => $"{r.Name}->{r.Import}").ToImmutableArray();

        /// <summary>声明表重复原语名。</summary>
        public static ImmutableArray<string> DuplicateNames =>
            Rows.GroupBy(r => r.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToImmutableArray();
    }
}
