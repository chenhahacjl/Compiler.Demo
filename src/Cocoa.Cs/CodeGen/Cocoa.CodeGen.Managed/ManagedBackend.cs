using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Binder;
using Cocoa.CodeAnalysis.Bound;
using Cocoa.IlMetadata;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Syntax.Nodes;
using Cocoa.CodeAnalysis.Syntax.Green;
using Cocoa.Targeting;
using System.Collections.Immutable;
using System.Linq;
namespace Cocoa.CodeGen.Managed
{
    /// <summary>
    /// managed（dotnet/IL）后端发射入口（拆分后独立项目 Cocoa.CodeGen.Managed）。
    /// 承载原 <c>Compilation.Emit</c> 的 IL 发射逻辑；
    /// 经 <see cref="Compilation.RegisterManagedEmitter"/> 注册到 Core，Core 自身不引用本后端。
    /// </summary>
    public static class ManagedBackend
    {
        /// <summary>注册 managed 后端到 Core（进程启动时调用一次）。</summary>
        public static void Register()
        {
            Compilation.RegisterManagedEmitter(Emit);
        }

        private static ImmutableArray<Diagnostic> Emit(BoundProgram program, string moduleName, string[] references, string outputPath, IlTarget target, bool emitLibrary, ImmutableDictionary<object, string>? coaAssemblies, bool publishPublicSurface, Cocoa.CodeAnalysis.PeVersionInfo? versionInfo, Cocoa.CodeAnalysis.PeResourceOptions? resourceOptions)
        {
            return IlEmitter.Emit(program, moduleName, references, outputPath, target, emitLibrary, coaAssemblies, versionInfo, resourceOptions, publishPublicSurface);
        }
    }
}
