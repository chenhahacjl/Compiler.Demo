using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeGen.Native;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Text;
using Cocoa.Targeting;
using System.Collections.Immutable;
using System.Linq;

namespace Cocoa.CodeGen.Native
{
    /// <summary>
    /// native 后端发射入口（拆分后独立项目 Cocoa.CodeGen.Native）。
    /// 承载原 <c>Compilation.EmitNative</c> 的后端专属校验与发射逻辑；
    /// 经 <see cref="Compilation.RegisterNativeEmitter"/> 注册到 Core，Core 自身不引用本后端。
    /// </summary>
    public static class NativeBackend
    {
        /// <summary>注册 native 后端到 Core（进程启动时调用一次）。</summary>
        public static void Register()
        {
            Compilation.RegisterNativeEmitter(EmitNative);
        }

        /// <summary>
        /// M2 公开符号快照出口：校验 + 发射 + 捕获「发射后实际符号集」快照（不写盘）。
        /// 与 <see cref="EmitNative"/> 共享同一校验与发射路径；校验失败抛 <see cref="InvalidOperationException"/>。
        /// </summary>
        public static NativeEmitResult GenerateWithSnapshot(Compilation compilation, TargetPlatform platform)
        {
            if (TryValidate(compilation, platform, out var program, out var diagnostics) != 0)
            {
                throw new InvalidOperationException("native 校验未通过：" + string.Join("\n", diagnostics.Select(d => d.Message)));
            }

            return MirToLir.GenerateWithSnapshot(program, platform);
        }

        private static ImmutableArray<Diagnostic> EmitNative(Compilation compilation, string moduleName, string outputPath, TargetPlatform platform, ushort subsystem)
        {
            if (TryValidate(compilation, platform, out var program, out var diagnostics) != 0)
            {
                return diagnostics;
            }

            var importWarnings = NativeImportValidator.Validate(program, platform.Arch);

            NativeCodeEmitter.Emit(program, moduleName, outputPath, platform, subsystem);

            return diagnostics.Concat(importWarnings).ToImmutableArray();
        }

        /// <summary>
        /// 公共校验路径（EmitNative 与 GenerateWithSnapshot 共用）。
        /// 返回 0=通过（program 可用）；非 0=失败（diagnostics 为最终诊断）。
        /// </summary>
        private static int TryValidate(Compilation compilation, TargetPlatform platform, out BoundProgram program, out ImmutableArray<Diagnostic> diagnostics)
        {
            var parseDiagnostics = compilation.SyntaxTrees.SelectMany(st => st.Diagnostics);

            var preliminary = parseDiagnostics.Concat(compilation.GlobalScope.Diagnostics).ToImmutableArray();
            if (preliminary.HasErrors())
            {
                program = null!;
                diagnostics = preliminary;
                return 1;
            }

            program = compilation.GetProgram();

            if (program.Diagnostics.HasErrors())
            {
                diagnostics = program.Diagnostics;
                return 1;
            }

            if (program.MainFunction == null)
            {
                var location = new TextLocation(compilation.SyntaxTrees[0].Text, new TextSpan(0, 0));
                diagnostics = ImmutableArray.Create(Diagnostic.Error(location, "native code generation requires a main function"));
                return 1;
            }

            if (program.Classes.Length > 0)
            {
                var staticInitClass = program.Classes.FirstOrDefault(AssemblyReferenceManager.HasStaticInitializer);
                if (staticInitClass != null)
                {
                    var location = staticInitClass.Declaration?.GetDeclarationNameLocation()
                                   ?? new TextLocation(compilation.SyntaxTrees[0].Text, new TextSpan(0, 0));
                    diagnostics = ImmutableArray.Create(Diagnostic.Error(location, $"class '{staticInitClass.Name}' 含静态构造函数或静态字段初始化器，native 后端暂不支持静态初始化触发（字段可声明但保持零值；请改在显式代码中赋值）"));
                    return 1;
                }
            }

            var backendDiagnostics = compilation.ValidateCodBackendRequirements(isNative: true);
            if (backendDiagnostics.Length > 0)
            {
                diagnostics = backendDiagnostics;
                return 1;
            }

            var objectFaceBag = new DiagnosticBag();
            NativeObjectModelValidator.Validate(program, objectFaceBag, new TextLocation(compilation.SyntaxTrees[0].Text, new TextSpan(0, 0)));
            if (objectFaceBag.Any())
            {
                diagnostics = preliminary.Concat(objectFaceBag).ToImmutableArray();
                return 2;
            }

            diagnostics = preliminary;
            return 0;
        }
    }
}
