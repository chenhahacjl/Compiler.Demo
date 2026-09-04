using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Serialization;
using Cocoa.CodeAnalysis.Evaluation;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Text;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis
{
    /// <summary>
    /// 程序集与 .coa 库引用管理（4.2 自 Compilation.cs 拆出，partial 分文件）：LoadCodLibraries、SourceAssembly/ReferencedAssemblies、cod 序列化门禁。
    /// </summary>
    public abstract partial class Compilation
    {
        private static ImmutableArray<CoaProgram> LoadCodLibraries(string[]? references)
        {
            var builder = ImmutableArray.CreateBuilder<CoaProgram>();

            // 内建系统库（System.Core.coa 等，目录发现 `System*.coa`）先行：用户引用可覆盖/补充同名符号
            builder.AddRange(SystemLibrary.Load());

            if (references != null)
            {
                foreach (var reference in references)
                {
                    if (reference.EndsWith(".coa", StringComparison.OrdinalIgnoreCase))
                    {
                        // 6e 跨库里程碑：用户 `.coa` 以「系统库 + 已加载用户库」为 external——
                        // 跨库符号合并复用实例（按依赖序加载；本轮最小实现：按传入序，refcod 拓扑留待完善）
                        var external = builder.ToImmutable();
                        var library = CoaSerializer.Load(reference, external);
                        library.Name = Cocoa.CodeAnalysis.Serialization.CoaAssemblyNaming.ManagedAssemblyName(Path.GetFileNameWithoutExtension(reference));
                        library.SourcePath = Path.GetFullPath(reference);
                        builder.Add(library);
                    }
                }
            }

            return builder.ToImmutable();
        }

        private AssemblySymbol? _sourceAssembly;

        /// <summary>本编译的源程序集（对齐 Roslyn <c>Compilation.SourceAssembly</c>）。</summary>
        public AssemblySymbol SourceAssembly
        {
            get
            {
                var source = _sourceAssembly;
                if (source == null)
                {
                    source = new AssemblySymbol("Cocoa", isSource: true);
                    Interlocked.CompareExchange(ref _sourceAssembly, source, null);
                    source = _sourceAssembly;
                }

                return source;
            }
        }

        private ImmutableArray<AssemblySymbol> _referencedAssemblies;

        /// <summary>引用的元数据程序集（对齐 Roslyn <c>Compilation.References</c>）：程序集路径引用 + 已加载的 `.coa` 库；
        /// <see cref="AssemblySymbol.Display"/> 携带路径，供 Emit 解析 BCL/引用。</summary>
        public ImmutableArray<AssemblySymbol> ReferencedAssemblies
        {
            get
            {
                if (_referencedAssemblies.IsDefault && (_references.Length > 0 || _codLibraries.Length > 0))
                {
                    var builder = ImmutableArray.CreateBuilder<AssemblySymbol>(_references.Length + _codLibraries.Length);
                    foreach (var path in _references)
                    {
                        builder.Add(new AssemblySymbol(Path.GetFileNameWithoutExtension(path), isSource: false, display: path));
                    }

                    foreach (var library in _codLibraries)
                    {
                        var name = string.IsNullOrEmpty(library.Name)
                            ? Path.GetFileNameWithoutExtension(library.SourcePath ?? "reference")
                            : library.Name;
                        builder.Add(new AssemblySymbol(name, isSource: false, display: library.SourcePath));
                    }

                    ImmutableInterlocked.InterlockedInitialize(ref _referencedAssemblies, builder.MoveToImmutable());
                }

                return _referencedAssemblies.IsDefault ? ImmutableArray<AssemblySymbol>.Empty : _referencedAssemblies;
            }
        }

        /// <summary>校验 `.coa` 库的 `requires` 与消费方后端匹配。</summary>
        public ImmutableArray<Diagnostic> ValidateCodBackendRequirements(bool isNative)
        {
            if (!isNative || _codLibraries.IsDefaultOrEmpty)
            {
                return ImmutableArray<Diagnostic>.Empty;
            }

            foreach (var library in _codLibraries)
            {
                if (library.Requires == CoaRequirement.DotNet)
                {
                    var ns = library.Namespaces.Length > 0 ? library.Namespaces[0] : "library";
                    return ImmutableArray.Create(Diagnostic.Error(ZeroLocation, $"库 '{ns}' requires dotnet（含 .NET API/OOP），native 后端不支持（阶段 9 CLR Hosting 前）"));
                }
            }

            return ImmutableArray<Diagnostic>.Empty;
        }

        /// <summary>
        /// 纯容器类判定（6e-M17，.coa 库放行判据）：类只含 syscall/静态 extern 方法，
        /// 无实例字段/实例构造/属性/显式基类/实例方法。等价"编译期透明的互操作分组"，
        /// 不涉对象模型。
        /// </summary>
        private bool IsCodSerializableClass(NamedTypeSymbol classType)
        {
            // 6e-Step D-a：闭包环境类（Binder 合成 __Env_*，捕获成员为实例字段）随库携带——
            // 含实例字段/隐式构造，但其本体的捕获字段列与 lambda 方法（归 fn）序列化路径已具备。
            if (classType.Name.StartsWith("__Env_", StringComparison.Ordinal))
            {
                return true;
            }

            return IsPureContainerClass(classType) || classType.IsFacadeClass || DeclaredFacade(classType);
        }

        /// <summary>类声明是否带 `facade` 修饰符（未命中 FacadeTargets 的 facade 类映射 BCL 前也按符号序列化）。</summary>
        private bool DeclaredFacade(NamedTypeSymbol classType)
        {
            // 部分类任一部分声明含 facade 关键字即算；Declaration 为 null（纯 cod 重建/外部类）按 IsFacadeClass 判定
            return Language.HasDeclaredFacadeModifier(classType.Declaration);
        }

        private bool IsPureContainerClass(NamedTypeSymbol classType)
        {
            if (classType.IsInterface)
            {
                // 6e-G7/M0-1a：接口声明放行——仅抽象方法签名（无体），无字段/属性/实现代码，可入 .coa
                return classType.Fields.Length == 0 && classType.Properties.Length == 0;
            }

            if ((classType.BaseType != null && !classType.BaseType.IsSystemObjectRoot) || classType.Fields.Any(f => !f.IsStatic))
            {
                return false;
            }

            if (classType.Properties.Length > 0)
            {
                return false;
            }

            foreach (var method in classType.Methods)
            {
                // 隐式默认实例构造（无声明、0 参）→ 允许（容器类不必实例化，发射端忽略）；显式实例构造 → 非容器
                if (method.IsConstructor && !method.IsStatic)
                {
                    if (method.Declaration != null || method.Parameters.Length != 0)
                    {
                        return false;
                    }

                    continue;
                }

                if (!method.IsStatic)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 类是否携带静态初始化语义（静态字段初始化器合成的 .cctor 或显式静态构造）。
        /// Binder 仅在存在静态初始化器或显式声明时创建 .cctor 符号，故符号存在即需运行期触发——
        /// native 后端无该时机，门禁拒绝并提示改写为显式赋值。
        /// </summary>
        public static bool HasStaticInitializer(NamedTypeSymbol classType)
        {
            foreach (var method in classType.Methods)
            {
                if (method.IsConstructor && method.IsStatic && method.Parameters.Length == 0)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
