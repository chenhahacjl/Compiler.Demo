using Cocoa.CodeAnalysis.Symbols;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Serialization.CoaFormat
{
    /// <summary>读侧共享状态：按名字/键索引的符号表 + 程序集符号清单（源 <c>CoaSerializer.ReadContext</c> 提升）。</summary>
    internal sealed class CoaReadContext
    {
        /// <summary>当前库名（读入符号的 ContainingLibrary 回填；FnKey 库前缀）。6e 跨库里程碑。</summary>
        public string ModuleName { get; }

        /// <summary>外部队列：已加载的依赖库（System.Core 先行），符号经 FunctionKeys/TypesByName 合并复用实例。</summary>
        public ImmutableArray<CoaProgram> ExternalLibraries { get; }

        public CoaReadContext(string moduleName, ImmutableArray<CoaProgram> external)
        {
            ModuleName = moduleName;
            ExternalLibraries = external;

            // 6e 跨库里程碑：预播种 external 库的符号表（复用实例，非复制）——
            // FunctionsByKey（键含库前缀）/TypesByName（全名）。本地注册（indexer 赋值）优先。
            foreach (var library in external)
            {
                foreach (var pair in library.FunctionKeys)
                {
                    if (!FunctionsByKey.ContainsKey(pair.Key))
                    {
                        FunctionsByKey[pair.Key] = pair.Value;
                    }
                }

                foreach (var pair in library.TypesByName)
                {
                    if (!TypesByName.ContainsKey(pair.Key))
                    {
                        TypesByName[pair.Key] = pair.Value;
                    }
                }
            }
        }

        /// <summary>类/枚举全名 → 类型符号（内建类型不经此表，直接解析）。</summary>
        public Dictionary<string, TypeSymbol> TypesByName { get; } = new(StringComparer.Ordinal);

        /// <summary>6e 跨库里程碑：本库自持类型表（全名 → 符号）——CoaProgram.TypesByName 导出源，
        /// 供其他库读侧 external 合并。与 TypesByName 的区别：不含预播种的 external 符号。</summary>
        public Dictionary<string, TypeSymbol> LocalTypesByName { get; } = new(StringComparer.Ordinal);

        /// <summary>6e-G7 S1：开放类型参数限定键（!属主全名.参数名）→ 符号。文件级平铺——限定键天然无碰撞。</summary>
        public Dictionary<string, TypeParameterSymbol> OpenTypeParametersByKey { get; } = new(StringComparer.Ordinal);

        /// <summary>函数键 → 函数符号。</summary>
        public Dictionary<string, FunctionSymbol> FunctionsByKey { get; } = new(StringComparer.Ordinal);

        /// <summary>6e 跨库里程碑：本库自持函数键（含库前缀）→ 符号——CoaProgram.FunctionKeys 导出源。</summary>
        public Dictionary<string, FunctionSymbol> LocalFunctionKeys { get; } = new(StringComparer.Ordinal);

        /// <summary>变量键 → 变量/参数符号。</summary>
        public Dictionary<string, VariableSymbol> VariablesByKey { get; } = new(StringComparer.Ordinal);

        public ImmutableArray<FunctionSymbol>.Builder Functions { get; } = ImmutableArray.CreateBuilder<FunctionSymbol>();

        public ImmutableArray<GlobalVariableSymbol>.Builder Globals { get; } = ImmutableArray.CreateBuilder<GlobalVariableSymbol>();

        public ImmutableArray<NamedTypeSymbol>.Builder Enums { get; } = ImmutableArray.CreateBuilder<NamedTypeSymbol>();

        public ImmutableArray<NamedTypeSymbol>.Builder Classes { get; } = ImmutableArray.CreateBuilder<NamedTypeSymbol>();

        /// <summary>6e-G7 S1：泛型定义类（gcls 读入）。</summary>
        public ImmutableArray<NamedTypeSymbol>.Builder GenericDefinitions { get; } = ImmutableArray.CreateBuilder<NamedTypeSymbol>();

        /// <summary>6b：facade 类属性待挂接声明（访问器 fns 读毕后重建 PropertySymbol）。
        /// 6e-M25：类型以原始 ref 字符串暂存，待全类注册后再解析（属性类型可能指向后声明的类）。</summary>
        public List<(NamedTypeSymbol ClassType, string Name, string TypeRef, bool HasGet, bool HasSet, Visibility Visibility, bool IsStatic)> PendingProperties { get; } = new();

        /// <summary>6f-4：捕获闭包元数据待回填（捕获变量 loc 晚于 fn 记录——全符号读毕后再解析）。</summary>
        public List<(FunctionSymbol Function, bool IsLambdaWithEnvironment, NamedTypeSymbol? EnvironmentClass, List<string> CapturedKeys)> PendingClosures { get; } = new();

        /// <summary>6e-M33：待回填基类（base 类声明可能晚于子类——全类注册后 pass2 解析，仿 PendingClosures）。</summary>
        public List<(NamedTypeSymbol ClassType, string BaseRef)> PendingBaseTypes { get; } = new();

        /// <summary>6e-M25：待回填类字段（字段类型可能指向文件中后声明的类——全类注册后解析）。</summary>
        public List<(NamedTypeSymbol ClassType, string Name, string TypeRef, Visibility Visibility, bool IsReadonly, bool IsStatic)> PendingFields { get; } = new();

        public void AddNamedType(string fullName, TypeSymbol type)
        {
            TypesByName[fullName] = type;
            LocalTypesByName[fullName] = type;
        }
    }
}
