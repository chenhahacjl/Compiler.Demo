using System;
using System.Collections.Immutable;
using System.Linq;

namespace Cocoa.CodeAnalysis.Symbols
{
    [Flags]
    public enum BuiltinBackend
    {
        None = 0,
        Evaluator = 1 << 0,
        Il = 1 << 1,
        Native = 1 << 2,
        All = Evaluator | Il | Native,
    }

    public sealed record BuiltinCoverageRow(BuiltinKind Kind, BuiltinBackend Backends, string? GapReason);

    /// <summary>
    /// 内建原语的三后端覆盖表——新增一个 <see cref="BuiltinKind"/> 必须同时在此加一行，
    /// 否则 <c>BuiltinCoverageTests</c> 报"枚举值未声明覆盖"。
    /// 这是"新增原语忘记某个后端"从运行期 <c>InvalidOperationException</c> 提前为构建期失败的唯一执行点。
    ///
    /// 分派点来源（Backends 列的依据）：
    ///   Evaluator → Evaluator.Calls.cs / Evaluator.Members.cs
    ///   Il        → Emit/IL/IlEmitter.Expressions.cs（Random 为 switch 前的 if 分支）
    ///   Native    → Emit/Native/Lir/MirToLir.Builtins.cs / .Expressions.cs
    /// </summary>
    public static class BuiltinCoverage
    {
        private static readonly ImmutableArray<BuiltinCoverageRow> Rows = ImmutableArray.Create<BuiltinCoverageRow>(
            new(BuiltinKind.WriteLine, BuiltinBackend.All, null),
            new(BuiltinKind.Write, BuiltinBackend.All, null),
            new(BuiltinKind.ReadLine, BuiltinBackend.All, null),
            new(BuiltinKind.ReadKey, BuiltinBackend.All, null),
            new(BuiltinKind.Random, BuiltinBackend.All, null),
            new(BuiltinKind.Sleep, BuiltinBackend.All, null),
            new(BuiltinKind.TickCount, BuiltinBackend.All, null),
            new(BuiltinKind.Exit, BuiltinBackend.All, null),
            new(BuiltinKind.Sqrt, BuiltinBackend.All, null),
            new(BuiltinKind.Beep, BuiltinBackend.All, null),
            new(BuiltinKind.DoubleToString, BuiltinBackend.All, null),
            new(BuiltinKind.StringFromChars, BuiltinBackend.All, null),
            new(BuiltinKind.FileReadAllText, BuiltinBackend.All, null),
            new(BuiltinKind.FileWriteAllText, BuiltinBackend.All, null),
            new(BuiltinKind.FileExists, BuiltinBackend.All, null),
            new(BuiltinKind.FileDelete, BuiltinBackend.All, null),
            new(BuiltinKind.FileCopy, BuiltinBackend.All, null),
            new(BuiltinKind.DirectoryExists, BuiltinBackend.All, null),
            new(BuiltinKind.GetEnvironmentVariable, BuiltinBackend.All, null),
            new(BuiltinKind.GetCurrentDirectory, BuiltinBackend.All, null),
            new(BuiltinKind.SetCurrentDirectory, BuiltinBackend.All, null),
            new(BuiltinKind.GetExecutablePath, BuiltinBackend.All, null),
            new(BuiltinKind.ObjectToString, BuiltinBackend.All, null),
            new(BuiltinKind.ObjectGetHashCode, BuiltinBackend.All, null),
            new(BuiltinKind.ObjectEquals, BuiltinBackend.All, null),
            new(BuiltinKind.ObjectGetType, BuiltinBackend.All, null),
            new(BuiltinKind.ObjectStaticEquals, BuiltinBackend.All, null),
            new(BuiltinKind.ObjectReferenceEquals, BuiltinBackend.All, null),
            new(BuiltinKind.TypeName, BuiltinBackend.All, null),
            new(BuiltinKind.TypeFullName, BuiltinBackend.All, null),
            new(BuiltinKind.Sha256Hash, BuiltinBackend.All, null),
            new(BuiltinKind.LaunchProcess, BuiltinBackend.All, null));

        public static BuiltinCoverageRow? Get(BuiltinKind kind) => Rows.FirstOrDefault(r => r.Kind == kind);

        public static bool Supports(BuiltinBackend backend, BuiltinKind kind)
            => Get(kind)?.Backends.HasFlag(backend) == true;

        public static ImmutableArray<BuiltinCoverageRow> AllRows => Rows;

        public static ImmutableArray<BuiltinKind> AllKinds => Rows.Select(r => r.Kind).ToImmutableArray();

        public static ImmutableArray<BuiltinCoverageRow> KnownGaps =>
            Rows.Where(r => r.Backends != BuiltinBackend.All).ToImmutableArray();

        /// <summary>未声明原因的覆盖缺口——必须为空，否则说明新增了缺口但未记录。</summary>
        public static ImmutableArray<BuiltinCoverageRow> UnexplainedGaps =>
            KnownGaps.Where(r => r.GapReason is null).ToImmutableArray();

        /// <summary>未在覆盖表中声明的枚举值——必须为空，否则说明新增了 BuiltinKind 但忘记登记。</summary>
        public static ImmutableArray<BuiltinKind> UndeclaredKinds =>
            Enum.GetValues<BuiltinKind>().Cast<BuiltinKind>()
                .Except(Rows.Select(r => r.Kind))
                .ToImmutableArray();
    }
}
