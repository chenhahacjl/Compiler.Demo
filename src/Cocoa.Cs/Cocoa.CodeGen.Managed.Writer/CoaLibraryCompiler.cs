using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Serialization;
using Cocoa.CodeGen.Managed.Structure;
 using Cocoa.CodeGen.Managed.Reader;
using Cocoa.CodeAnalysis.Lowering;
using System.Collections.Immutable;
using System.IO;

using Cocoa.CodeAnalysis;
using Cocoa.Targeting;

namespace Cocoa.CodeGen.Managed.Writer
{
    /// <summary>
    /// `.coa` → 托管库 dll 发射（动态链接阶段 A1）：加载语义层程序集，直接构造无入口的
    /// BoundProgram（顶层函数由 IlEmitter 挂 <see cref="IlEmitter"/> 的 &lt;CocoaTopLevel&gt; 容器；
    /// 枚举按 int32 表示；容器类为普通静态类），复用既有 emitLibrary 管线产出标准 .NET 库——
    /// 供消费方 exe 运行期依赖（阶段 A 动态链接）与 C# 互操作。
    /// </summary>
    internal static class CoaLibraryCompiler
    {
        /// <summary>从 `.coa` 文件发射同名托管库 dll。返回诊断（含错误时调用方不应使用产物）。</summary>
        public static ImmutableArray<Diagnostic> EmitManagedDll(string coaPath, string dllPath, IlTarget target)
        {
            return EmitManagedDll(CoaSerializer.Load(coaPath), dllPath, target);
        }

        /// <summary>从内存中的 CoaProgram 发射托管库 dll。</summary>
        public static ImmutableArray<Diagnostic> EmitManagedDll(CoaProgram cod, string dllPath, IlTarget target)
        {
            // 6e 跨库里程碑：gcls 开放方法（泛型定义/泛型方法，开放类型参数无法编码 IL）不进库发射——
            // 否则其 ContainingClass（泛型定义类）被当作普通类发射，遇 K/T 报 Unexpected type K。
            // S-7：.coa 库体为 raw 结构化 HIR —— 送入 BoundProgram 前统一 Lower 为 MIR（IlEmitter 消费契约）。
            var functionsBuilder = ImmutableDictionary.CreateBuilder<CodeAnalysis.Symbols.FunctionSymbol, BoundBlockStatement>();
            foreach (var pair in cod.Bodies)
            {
                if (pair.Key.ContainingClass?.IsGenericDefinition == true || pair.Key.IsGenericMethod)
                {
                    continue;
                }

                functionsBuilder.Add(pair.Key, Lowerer.Lower(pair.Key, pair.Value));
            }

            // 无入口的纯库程序集：Main/Script 均空，emitLibrary 走库 PE 形态
            var program = new BoundProgram(
                previous: null,
                diagnostics: ImmutableArray<Diagnostic>.Empty,
                mainFunction: null,
                scriptFunction: null,
                functions: functionsBuilder.ToImmutable(),
                classes: cod.Classes);

            var moduleName = Path.GetFileNameWithoutExtension(dllPath);
            var references = IlReferenceResolver.ResolveDefaultReferences(target)
                ?? new[] { typeof(object).Assembly.Location, typeof(Console).Assembly.Location };

            // 动态链接库：分发面即公共契约——internal 门面也发布为 public（消费方跨程序集调用必需）
            return IlEmitter.Emit(program, moduleName, references, dllPath, target, emitLibrary: true, codAssemblies: null, publishPublicSurface: true);
        }
    }
}
