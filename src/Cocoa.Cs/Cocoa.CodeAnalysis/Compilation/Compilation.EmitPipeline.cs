using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Serialization;
using Cocoa.CodeAnalysis.Emit;
using Cocoa.CodeGen.PE;
using Cocoa.CodeAnalysis.Evaluation;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Text;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis
{
    /// <summary>
    /// 发射管线（4.2 自 Compilation.cs 拆出，partial 分文件）：EmitTree/Emit/EmitNative/EmitCocoa 与函数值/OOP 门禁扫描。
    /// </summary>
    public abstract partial class Compilation
    {

        public void EmitTree(TextWriter writer)
        {
            var program = GetProgram();

            if (GlobalScope.MainFunction != null)
            {
                EmitTree(GlobalScope.MainFunction, writer);
            }
            else if (GlobalScope.ScriptFunction != null)
            {
                EmitTree(GlobalScope.ScriptFunction, writer);
            }
        }

        public void EmitTree(FunctionSymbol symbol, TextWriter writer)
        {
            var program = GetProgram();

            symbol.WriteTo(writer);
            writer.WriteLine();

            if (!program.Functions.TryGetValue(symbol, out var body))
            {
                return;
            }

            body.WriteTo(writer);
        }

        // TODO: References should be part of the compilation, not arguments for Emit
        public ImmutableArray<Diagnostic> Emit(string moduleName, string[] references, string outputPath)
            => Emit(moduleName, references, outputPath, IlTarget.Default, emitLibrary: false);

        public ImmutableArray<Diagnostic> Emit(string moduleName, string[] references, string outputPath, IlTarget target)
            => Emit(moduleName, references, outputPath, target, emitLibrary: false);

        // 引用作为编译组成部分（对齐 Roslyn：Emit 不接收引用参数，经 Compilation.References 提供）
        public ImmutableArray<Diagnostic> Emit(string moduleName, string outputPath)
            => Emit(moduleName, this.References.Select(r => r.Display).ToArray(), outputPath, IlTarget.Default, emitLibrary: false);

        public ImmutableArray<Diagnostic> Emit(string moduleName, string outputPath, IlTarget target)
            => Emit(moduleName, this.References.Select(r => r.Display).ToArray(), outputPath, target, emitLibrary: false);

        public ImmutableArray<Diagnostic> Emit(string moduleName, string outputPath, IlTarget target, bool emitLibrary)
            => Emit(moduleName, this.References.Select(r => r.Display).ToArray(), outputPath, target, emitLibrary);

        // MetadataReference 形态重载（Roslyn 形态引用参数）
        public ImmutableArray<Diagnostic> Emit(string moduleName, IReadOnlyList<MetadataReference> references, string outputPath)
            => Emit(moduleName, references.Select(r => r.Display).ToArray(), outputPath, IlTarget.Default, emitLibrary: false);

        public ImmutableArray<Diagnostic> Emit(string moduleName, IReadOnlyList<MetadataReference> references, string outputPath, IlTarget target)
            => Emit(moduleName, references.Select(r => r.Display).ToArray(), outputPath, target, emitLibrary: false);

        // AssemblySymbol 形态重载（Emit 内部消费 AssemblySymbol：经 Display 派生路径）
        public ImmutableArray<Diagnostic> Emit(string moduleName, IReadOnlyList<AssemblySymbol> references, string outputPath)
            => Emit(moduleName, references.Select(r => r.Display ?? r.Name).ToArray(), outputPath, IlTarget.Default, emitLibrary: false);

        public ImmutableArray<Diagnostic> Emit(string moduleName, IReadOnlyList<AssemblySymbol> references, string outputPath, IlTarget target)
            => Emit(moduleName, references.Select(r => r.Display ?? r.Name).ToArray(), outputPath, target, emitLibrary: false);

        public ImmutableArray<Diagnostic> Emit(string moduleName, IReadOnlyList<AssemblySymbol> references, string outputPath, IlTarget target, bool emitLibrary)
            => Emit(moduleName, references.Select(r => r.Display ?? r.Name).ToArray(), outputPath, target, emitLibrary);

        public ImmutableArray<Diagnostic> Emit(string moduleName, string[] references, string outputPath, IlTarget target, bool emitLibrary)
        {
            var parseDiagnostics = SyntaxTrees.SelectMany(st => st.Diagnostics);

            var diagnostics = parseDiagnostics.Concat(GlobalScope.Diagnostics).ToImmutableArray();
            if (diagnostics.HasErrors())
            {
                return diagnostics;
            }

            var program = GetProgram();

            // 与 Evaluate/EmitCocoa 一致的门禁（重构阶段 1a/A3）：绑定/单态化产出的错误
            // 不得进入发射——否则带错程序会被静默生成为 dll/exe
            if (program.Diagnostics.HasErrors())
            {
                return diagnostics.Concat(program.Diagnostics).ToImmutableArray();
            }

            // 6e-M22 C4-b：IL 后端已支持函数值（Func`N 委托映射），门禁移除；native 见 EmitNative

            var ilReferences = references
                .Where(r => !r.EndsWith(".coa", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            var backendDiagnostics = _managedEmitter == null
                ? ImmutableArray.Create(Diagnostic.Error(ZeroLocation, "managed 后端未注册（Cocoa.CodeGen.IL 未初始化）"))
                : _managedEmitter(program, moduleName, ilReferences, outputPath, target, emitLibrary, program.CodAssemblies, false);

            // 成功路径也带上 GlobalScope 警告（using 未解析等），供 CLI 打印
            return diagnostics.Concat(backendDiagnostics).ToImmutableArray();
        }

        /// <summary>
        /// 把程序直接生成为原生可执行文件，不依赖 .NET 运行时。
        /// 实现经 <see cref="RegisterNativeEmitter"/> 注入的 native 后端（Core 自身不引用后端）。
        /// </summary>
        internal ImmutableArray<Diagnostic> EmitNative(string moduleName, string outputPath, TargetPlatform platform = default)
        {
            if (_nativeEmitter == null)
            {
                return ImmutableArray.Create(Diagnostic.Error(ZeroLocation, "native 后端未注册（Cocoa.CodeGen.Native 未初始化）"));
            }

            return _nativeEmitter(this, moduleName, outputPath, platform);
        }

        /// <summary>
        /// 把库编译为 `.coa` 语义层程序集（编译到 BoundProgram 即停，不走 IR/机器码/IL）。
        /// </summary>
        internal ImmutableArray<Diagnostic> EmitCocoa(string moduleName, string outputPath)
        {
            var parseDiagnostics = SyntaxTrees.SelectMany(st => st.Diagnostics);

            var diagnostics = parseDiagnostics.Concat(GlobalScope.Diagnostics).ToImmutableArray();
            if (diagnostics.HasErrors())
            {
                return diagnostics;
            }

            var program = GetProgram();

            if (program.Diagnostics.HasErrors())
            {
                return program.Diagnostics;
            }

            // 6e-M22：lambda/函数值节点入 `.coa` 序列化于 C6 接入——先行明确诊断
            var cocoaFunctionValueDiagnostic = FindFunctionValueDiagnostic(program);
            if (cocoaFunctionValueDiagnostic != null)
            {
                return ImmutableArray.Create(cocoaFunctionValueDiagnostic);
            }

            // 校验 1：库无入口
            if (program.MainFunction != null || program.ScriptFunction != null)
            {
                return ImmutableArray.Create(Diagnostic.Error(ZeroLocation, "output = cocoa 的库不允许入口函数（Main/script）"));
            }

            // 校验 2：无内部 OOP（.coa 6e-M17 起放行纯容器类：仅 syscall/extern 静态方法；6b 起放行 facade 实例类
            // ——facade 映射 BCL（System.Exception 等），体内不经 cod 执行，仅需符号+成员签名；非 facade 实例类仍 6b 后置）
            if (program.Classes.Length > 0)
            {
                var offendingClass = program.Classes.FirstOrDefault(c => !IsCodSerializableClass(c));
                if (offendingClass != null)
                {
                    var location = Language.GetDeclarationNameLocation(offendingClass.Declaration) ?? ZeroLocation;
                    return ImmutableArray.Create(Diagnostic.Error(location, $"库含实例类 '{offendingClass.Name}'（OOP），.coa 序列化阶段 6b 后置（requires:dotnet）；纯 syscall/extern 容器类与 facade 类已支持"));
                }
            }

            // 校验 3：库体不含 OOP/.NET API 节点（类字段/方法/对象创建/this/base/静态类型等）
            // S-7：.coa 持久化 raw（未 Lower）体——校验对象与序列化源一致（program.RawFunctions）
            foreach (var (fn, body) in program.RawFunctions)
            {
                if (HasOopNode(body))
                {
                    return ImmutableArray.Create(Diagnostic.Error(ZeroLocation, $"库函数 '{fn.Name}' 含 class/OOP 或 .NET API 调用，.coa 阶段 6b 后置（requires:dotnet）"));
                }
            }

            // 校验 4：必须声明 namespace
            var namespaces = CollectNamespaceNames();
            if (namespaces.Length == 0)
            {
                return ImmutableArray.Create(Diagnostic.Error(ZeroLocation, "output = cocoa 库必须声明 namespace（如 `namespace MyLib { ... }`）"));
            }

            var functions = GlobalScope.Functions;
            var globals = GlobalScope.Variables.OfType<GlobalVariableSymbol>().ToImmutableArray();
            var enums = GlobalScope.Enums;

            if (globals.Length > 0)
            {
                return ImmutableArray.Create(Diagnostic.Error(ZeroLocation, "库含全局变量，发射暂不支持（阶段 6b 后置）"));
            }

            var imports = functions
                .Where(f => f.IsExtern && f.DllName != null)
                .Select(f => f.DllName!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray();

            var containerClasses = program.Classes.Where(IsCodSerializableClass).ToImmutableArray();

            var codProgram = new CoaProgram(
                functions,
                globals,
                enums,
                containerClasses,
                // S-7：.coa bodies 序列化 raw（未 Lower 结构化 HIR：for/while/if 保留），
                // 非 program.Functions（lowered/MIR）。消费方链接/动态发射处统一补 Lower。
                program.RawFunctions,
                CoaRequirement.Any,
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty,
                imports,
                ImmutableArray<string>.Empty,
                namespaces,
                program.GenericDefinitions,
                program.GenericOpenBodies)
            {
                // 程序集名 = 模块名：动态链接时消费方据此合成 AssemblyRef 指向同名 dll（阶段 A2）
                Name = moduleName,
            };

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            using (var writer = new StreamWriter(outputPath))
            {
                CoaSerializer.Write(writer, codProgram);
            }

            return ImmutableArray<Diagnostic>.Empty;
        }

        private TextLocation ZeroLocation
        {
            get
            {
                if (SyntaxTrees.Length > 0)
                {
                    return new TextLocation(SyntaxTrees[0].Text, new TextSpan(0, 0));
                }

                // 无语法树场景（如纯库引用编译）本就无 Text 可言；TextLocation.Text 允许 null 由消费方判空
                return new TextLocation(null!, new TextSpan(0, 0));
            }
        }

        /// <summary>函数值节点扫描（6e-M22 C4 + M0-1b）：函数类型签名（参数/返回/字段）经 fnty 序列化已放行；
        /// lambda/函数值表达式/间接调用的库体序列化未接入前仍拒绝。S-7：扫 raw（序列化源）。</summary>
        private Diagnostic? FindFunctionValueDiagnostic(BoundProgram program)
        {
            foreach (var (function, body) in program.RawFunctions)
            {
                if (HasFunctionValueNode(body))
                {
                    var location = function.Syntax?.Location ?? ZeroLocation;
                    return Diagnostic.Error(location, "lambda/函数值的库体序列化（fnty 并轨）未接入前，cod 库暂不支持。");
                }
            }

            return null;
        }

        private static bool HasFunctionValueNode(BoundNode node)
        {
            if (node.Kind == BoundNodeKind.FunctionValueExpression || node.Kind == BoundNodeKind.InvocationExpression)
            {
                return true;
            }

            foreach (var child in BoundChildren(node))
            {
                if (HasFunctionValueNode(child))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>库体是否含 OOP/.NET API 节点（v1 拒绝：序列化阶段 6b 后置）。</summary>
        private static bool HasOopNode(BoundNode node)
        {
            switch (node.Kind)
            {
                case BoundNodeKind.ObjectCreationExpression:
                case BoundNodeKind.ThisExpression:
                case BoundNodeKind.BaseExpression:
                case BoundNodeKind.ConstructorChainExpression:
                case BoundNodeKind.MemberAssignmentExpression:
                case BoundNodeKind.ErrorExpression:
                    return true;
                case BoundNodeKind.StaticTypeExpression:
                    // 容器类静态类型表达式（System.Runtime.Print 的目标）不是 OOP
                    return false;
                case BoundNodeKind.MemberAccessExpression:
                    return ((BoundMemberAccessExpression)node).Field != null;
                case BoundNodeKind.MemberCallExpression:
                    {
                        var call = (BoundMemberCallExpression)node;
                        // 静态容器类方法调用（syscall/extern/带体静态方法，6e-M18）不是 OOP；实例方法/继承仍是
                        return call.IsBase || (call.Method != null && !call.Method.IsStatic);
                    }
                default:
                    foreach (var child in BoundChildren(node))
                    {
                        if (HasOopNode(child))
                        {
                            return true;
                        }
                    }

                    return false;
            }
        }
    }
}
