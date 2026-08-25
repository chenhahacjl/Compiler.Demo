using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Emit.IL;
using System.Collections.Immutable;
using System.IO;

namespace Cocoa.CodeAnalysis.Cod
{
    /// <summary>
    /// `.cod` → 托管库 dll 发射（动态链接阶段 A1）：加载语义层程序集，直接构造无入口的
    /// BoundProgram（顶层函数由 IlEmitter 挂 <see cref="IlEmitter"/> 的 &lt;CocoaTopLevel&gt; 容器；
    /// 枚举按 int32 表示；容器类为普通静态类），复用既有 emitLibrary 管线产出标准 .NET 库——
    /// 供消费方 exe 运行期依赖（阶段 A 动态链接）与 C# 互操作。
    /// </summary>
    internal static class CodLibraryCompiler
    {
        /// <summary>从 `.cod` 文件发射同名托管库 dll。返回诊断（含错误时调用方不应使用产物）。</summary>
        public static ImmutableArray<Diagnostic> EmitManagedDll(string codPath, string dllPath, IlTarget target)
        {
            return EmitManagedDll(CodSerializer.Load(codPath), dllPath, target);
        }

        /// <summary>从内存中的 CodProgram 发射托管库 dll。</summary>
        public static ImmutableArray<Diagnostic> EmitManagedDll(CodProgram cod, string dllPath, IlTarget target)
        {
            // 无入口的纯库程序集：Main/Script 均空，emitLibrary 走库 PE 形态
            var program = new BoundProgram(
                previous: null,
                diagnostics: ImmutableArray<Diagnostic>.Empty,
                mainFunction: null,
                scriptFunction: null,
                functions: cod.Bodies,
                classes: cod.Classes);

            var moduleName = Path.GetFileNameWithoutExtension(dllPath);
            var references = IlReferenceResolver.ResolveDefaultReferences(target)
                ?? new[] { typeof(object).Assembly.Location, typeof(Console).Assembly.Location };

            // 动态链接库：分发面即公共契约——internal 门面也发布为 public（消费方跨程序集调用必需）
            return IlEmitter.Emit(program, moduleName, references, dllPath, target, emitLibrary: true, codAssemblies: null, publishPublicSurface: true);
        }
    }
}
