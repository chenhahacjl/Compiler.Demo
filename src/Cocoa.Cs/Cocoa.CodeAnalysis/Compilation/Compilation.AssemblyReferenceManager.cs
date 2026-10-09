using Cocoa.CodeAnalysis.Serialization;
using Cocoa.CodeAnalysis.Symbols;
using System.Collections.Immutable;
using System.IO;

namespace Cocoa.CodeAnalysis
{
    /// <summary>
    /// 程序集与 .coa 库引用管理的实例部分：SourceAssembly/ReferencedAssemblies（惰性缓存）+ ValidateCodBackendRequirements。
    /// 静态部分（库加载/拓扑序/序列化门禁判据）已拆至 <see cref="AssemblyReferenceManager"/>。
    /// </summary>
    public partial class Compilation
    {
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
    }
}
