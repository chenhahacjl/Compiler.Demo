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

            var ordered = TopologicalOrder(builder.ToImmutable());
            return DetectAmbiguousTypes(ordered);
        }

        /// <summary>
        /// 6e-Step E（同名歧义，其一）：跨库同名类型检测——两个及以上「已被引用」的 `.coa` 公开同一
        /// 「库名!全名」规范类型（类/枚举/泛型定义）时，装载即报 CS0104 式歧义（首个命中仍有效，其余丢弃的
        /// 现状 → 显式报错，不再静默 first-wins）。别名消歧（`using X = Lib1.Foo;` / 全名限定）留档。
        /// </summary>
        private static ImmutableArray<CoaProgram> DetectAmbiguousTypes(ImmutableArray<CoaProgram> libraries)
        {
            // 仅约束到「当前已加载的用户库」间；系统库（System*）作为权威内置、允许用户覆盖/补充，不参与同名判定。
            var byFullName = new Dictionary<string, CoaProgram>(StringComparer.Ordinal);
            List<CoaProgram>? conflicting = null;
            foreach (var library in libraries)
            {
                if (library.Name.StartsWith("System", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var type in IterateLibraryTypes(library))
                {
                    var key = type.FullName;
                    if (byFullName.TryGetValue(key, out var first) && !ReferenceEquals(first, library))
                    {
                        if (conflicting == null)
                        {
                            conflicting = new List<CoaProgram> { first };
                        }

                        if (!conflicting.Contains(library))
                        {
                            conflicting.Add(library);
                        }
                    }
                    else
                    {
                        byFullName[key] = library;
                    }
                }
            }

            if (conflicting != null && conflicting.Count >= 2)
            {
                throw new InvalidDataException(
                    "CS0104 式歧义：以下 cod 库同时声明了同名类型，消费者无法决定取用哪份（消歧手段：别名库 / 全名限定，留档后续）:\n" +
                    string.Join("\n", conflicting.Select((c, i) => $"  {i + 1}. {c.Name}")));
            }

            return libraries;
        }

        private static IEnumerable<NamedTypeSymbol> IterateLibraryTypes(CoaProgram library)
        {
            foreach (var t in library.Classes)
            {
                yield return t;
            }

            foreach (var t in library.Enums)
            {
                yield return t;
            }

            foreach (var t in library.GenericDefinitions)
            {
                yield return t;
            }
        }

        /// <summary>
        /// 6e-Step E：`.coa` 库引用拓扑序（Kahn）——按各自 `CodReferences`（refcod 清单）构造依赖图，
        /// 被依赖库先行（读侧 external 按序合并，首次命中实例复用）。环检测报错；未加载依赖计为无约束（宽松）。
        /// 排序稳定：同层保持原相对顺序。
        /// </summary>
        public static ImmutableArray<CoaProgram> TopologicalOrder(ImmutableArray<CoaProgram> libraries)
        {
            if (libraries.Length < 2)
            {
                return libraries;
            }

            // 名 → 已加载程序（同库多个文件取首；重名场景由歧义诊断层负责，此处只保证依赖序）。
            var byName = new Dictionary<string, CoaProgram>(StringComparer.Ordinal);
            foreach (var library in libraries)
            {
                byName.TryAdd(LibraryKey(library), library);
            }

            var dependencyCount = new int[libraries.Length];
            var dependents = new List<int>[libraries.Length];
            for (var i = 0; i < dependents.Length; i++)
            {
                dependents[i] = new List<int>();
            }

            for (var i = 0; i < libraries.Length; i++)
            {
                foreach (var reference in libraries[i].CodReferences)
                {
                    if (!byName.TryGetValue(NormalizeReference(reference), out var dependency))
                    {
                        continue; // 未加载依赖（消费者名单外）——不做约束，待消费解析层报缺失
                    }

                    var j = libraries.IndexOf(dependency);
                    if (j < 0 || j == i)
                    {
                        continue;
                    }

                    dependencyCount[i]++;
                    dependents[j].Add(i);
                }
            }

            var order = ImmutableArray.CreateBuilder<CoaProgram>(libraries.Length);
            var scheduled = new bool[libraries.Length];
            var priority = new Queue<int>();
            for (var i = 0; i < libraries.Length; i++)
            {
                if (dependencyCount[i] == 0)
                {
                    priority.Enqueue(i);
                }
            }

            var emitted = 0;
            while (priority.Count > 0)
            {
                var index = priority.Dequeue();
                if (scheduled[index])
                {
                    continue;
                }

                scheduled[index] = true;
                order.Add(libraries[index]);
                emitted++;

                foreach (var dependent in dependents[index])
                {
                    dependencyCount[dependent]--;
                    if (dependencyCount[dependent] == 0)
                    {
                        priority.Enqueue(dependent);
                    }
                }
            }

            if (emitted < libraries.Length)
            {
                var cyclic = new List<string>();
                for (var i = 0; i < libraries.Length; i++)
                {
                    if (!scheduled[i])
                    {
                        cyclic.Add(LibraryKey(libraries[i]));
                    }
                }

                throw new InvalidOperationException("cod 库循环引用: " + string.Join(", ", cyclic) + "。refcod 依赖不能成环。");
            }

            return order.ToImmutable();
        }

        private static string LibraryKey(CoaProgram library)
        {
            var name = library.Name;
            if (name.Length > 0)
            {
                return name;
            }

            return Path.GetFileNameWithoutExtension(library.SourcePath ?? "");
        }

        private static string NormalizeReference(string reference)
        {
            // refcod 清单可能带 `X.Managed` 形式（命名对齐），压平为库键
            var baseName = reference;
            if (baseName.EndsWith(".coa", StringComparison.OrdinalIgnoreCase))
            {
                baseName = Path.GetFileNameWithoutExtension(baseName);
            }

            if (baseName.EndsWith(".Managed", StringComparison.Ordinal))
            {
                baseName = baseName.Substring(0, baseName.Length - ".Managed".Length);
            }

            return baseName;
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

            // 6e-Step D-b：普通实例类（如事件类：实例字段 + 实例方法体）入 .coa ——
            // base 限制 System.Object（无多继承依赖），仍需真实实例语义（否则落入纯容器判定，杜绝容器默认构造器泄漏）。
            if (!classType.IsInterface &&
                (classType.BaseType == null || classType.BaseType.IsSystemObjectRoot) &&
                classType.Properties.Length == 0 &&
                (classType.Fields.Any(f => !f.IsStatic) ||
                 classType.Events.Length > 0 ||
                 classType.Methods.Any(m => !m.IsStatic && !m.IsConstructor)))
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
