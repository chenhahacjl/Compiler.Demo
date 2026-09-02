using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Emit.Native;
using Cocoa.CodeAnalysis.Emit.Native.Runtime.Windows.X64;
using Cocoa.CodeAnalysis.Emit.Native.Runtime.Windows.X86;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Linq;

namespace Cocoa.CodeAnalysis.Emit.Native
{
    /// <summary>
    /// native 后端发射入口（拆分后独立项目 Cocoa.Core.Native）。
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

        private static ImmutableArray<Diagnostic> EmitNative(Compilation compilation, string moduleName, string outputPath, TargetPlatform platform)
        {
            var parseDiagnostics = compilation.SyntaxTrees.SelectMany(st => st.Diagnostics);

            var diagnostics = parseDiagnostics.Concat(compilation.GlobalScope.Diagnostics).ToImmutableArray();
            if (diagnostics.HasErrors())
            {
                return diagnostics;
            }

            var program = compilation.GetProgram();

            if (program.Diagnostics.HasErrors())
            {
                return program.Diagnostics;
            }

            if (program.MainFunction == null)
            {
                var location = new TextLocation(compilation.SyntaxTrees[0].Text, new TextSpan(0, 0));
                return ImmutableArray.Create(Diagnostic.Error(location, "native code generation requires a main function"));
            }

            if (program.Classes.Length > 0)
            {
                var interfaceClass = program.Classes.FirstOrDefault(c => c.IsInterface);
                if (interfaceClass != null)
                {
                    var location = compilation.Language.GetDeclarationNameLocation(interfaceClass.Declaration)
                                   ?? new TextLocation(compilation.SyntaxTrees[0].Text, new TextSpan(0, 0));
                    return ImmutableArray.Create(Diagnostic.Error(location, $"interface '{interfaceClass.Name}' 暂不支持 native 后端（接口分派随后续里程碑落地，见 docs-dev/对象模型设计.md）"));
                }

                var staticInitClass = program.Classes.FirstOrDefault(Compilation.HasStaticInitializer);
                if (staticInitClass != null)
                {
                    var location = compilation.Language.GetDeclarationNameLocation(staticInitClass.Declaration)
                                   ?? new TextLocation(compilation.SyntaxTrees[0].Text, new TextSpan(0, 0));
                    return ImmutableArray.Create(Diagnostic.Error(location, $"class '{staticInitClass.Name}' 含静态构造函数或静态字段初始化器，native 后端暂不支持静态初始化触发（字段可声明但保持零值；请改在显式代码中赋值）"));
                }
            }

            var backendDiagnostics = compilation.ValidateCodBackendRequirements(isNative: true);
            if (backendDiagnostics.Length > 0)
            {
                return backendDiagnostics;
            }

            var objectFaceBag = new DiagnosticBag();
            NativeObjectModelValidator.Validate(program, objectFaceBag, new TextLocation(compilation.SyntaxTrees[0].Text, new TextSpan(0, 0)));
            if (objectFaceBag.Any())
            {
                return diagnostics.Concat(objectFaceBag).ToImmutableArray();
            }

            var importWarnings = NativeImportValidator.Validate(program, platform.Arch);

            NativeCodeEmitter.Emit(program, moduleName, outputPath, platform);

            return diagnostics.Concat(importWarnings).ToImmutableArray();
        }
    }
}
