using System;
using System.Collections.Generic;

namespace Cocoa.CodeGen.Managed.Structure
{
    /// <summary>IL 元数据可见性（值域对齐符号模型 Visibility：Public/Internal/Protected/Private）。</summary>
    public enum IlVisibility
    {
        Public,
        Internal,
        Protected,
        Private,
    }

    /// <summary>extern 函数编码格式（ImplMap CharSet 位；值域对齐符号模型 CharSet）。</summary>
    public enum IlCharSet
    {
        Unicode,
        Ansi,
        Auto,
    }

    /// <summary>我们自己的类型定义（TypeDef 表行）。顶层函数挂在 Program，class 各占一行。</summary>
    public sealed class IlTypeDef
    {
        public IlTypeDef(string name, string @namespace, IlTypeRef? baseTypeRef, bool isPublic = true, IlTypeDef? baseTypeDef = null)
        {
            Name = name;
            Namespace = @namespace ?? "";
            _baseTypeRef = baseTypeRef;
            _baseTypeDef = baseTypeDef;
            IsPublic = isPublic;
            Fields = new List<IlFieldDef>();
            Methods = new List<IlMethodDef>();
        }

        public string Name { get; }
        public string Namespace { get; }
        private IlTypeRef? _baseTypeRef;
        public IlTypeRef? BaseTypeRef => _baseTypeRef;

        /// <summary>本程序集内的基类 TypeDef（优先于 BaseTypeRef）。</summary>
        private IlTypeDef? _baseTypeDef;
        public IlTypeDef? BaseTypeDef => _baseTypeDef;

        /// <summary>延迟填充基类（6e-M20：泛型实例化类的字段可前向引用兄弟实例化类，壳先注册、基类后填）。</summary>
        public void SetBase(IlTypeRef? baseTypeRef, IlTypeDef? baseTypeDef)
        {
            _baseTypeRef = baseTypeRef;
            _baseTypeDef = baseTypeDef;
        }

        public bool IsPublic { get; }
        public bool IsAbstract { get; set; }
        public bool IsSealed { get; set; }
        public bool IsInterface { get; set; }
        public bool IsValueType { get; set; }
        public List<IlInterfaceImpl> Interfaces { get; } = new List<IlInterfaceImpl>();
        public List<IlFieldDef> Fields { get; }
        public List<IlPropertyDef> Properties { get; } = new List<IlPropertyDef>();
        public List<IlMethodDef> Methods { get; }
    }

    /// <summary>InterfaceImpl 表行：类 → 接口（TypeDefOrRef：本程序集 TypeDef 或外部 TypeRef）。</summary>
    public sealed class IlInterfaceImpl
    {
        public IlInterfaceImpl(IlTypeDef? typeDef, IlTypeRef? typeRef)
        {
            TypeDef = typeDef;
            TypeRef = typeRef;
        }

        public IlTypeDef? TypeDef { get; }
        public IlTypeRef? TypeRef { get; }
    }

    /// <summary>我们自己的属性定义（Property 表行 + MethodSemantics）。</summary>
    public sealed class IlPropertyDef
    {
        public IlPropertyDef(string name, IlType type, IlMethodDef? getter, IlMethodDef? setter)
        {
            Name = name;
            Type = type;
            Getter = getter;
            Setter = setter;
        }

        public string Name { get; }
        public IlType Type { get; }
        public IlMethodDef? Getter { get; }
        public IlMethodDef? Setter { get; }
    }

    /// <summary>我们自己的字段定义（FieldDef 表行）。</summary>
    public sealed class IlFieldDef
    {
        public IlFieldDef(string name, IlType type, IlVisibility visibility, bool isStatic = false)
        {
            Name = name;
            Type = type;
            Visibility = visibility;
            IsStatic = isStatic;
        }

        public string Name { get; }
        public IlType Type { get; }
        public IlVisibility Visibility { get; }
        public bool IsStatic { get; }
    }

    /// <summary>我们自己的方法定义（MethodDef 表行 + 方法体）。</summary>
    public sealed class IlMethodDef
    {
        public IlMethodDef(string name, IlType returnType, IReadOnlyList<IlType> parameterTypes, IlMethodBody? body, string? dllName = null, string? importName = null, IlCallingConvention callingConvention = IlCallingConvention.Winapi, bool isStatic = true, IlCharSet charSet = IlCharSet.Unicode)
        {
            Name = name;
            ReturnType = returnType;
            ParameterTypes = parameterTypes;
            Body = body;
            DllName = dllName;
            ImportName = importName;
            CallingConvention = callingConvention;
            IsStatic = isStatic;
            CharSet = charSet;
        }

        public string Name { get; }
        public IlType ReturnType { get; }
        public IReadOnlyList<IlType> ParameterTypes { get; }
        public IlMethodBody? Body { get; }

        /// <summary>P/Invoke 目标 DLL（null = 普通方法，不产生 ImplMap 行）。</summary>
        public string? DllName { get; }
        /// <summary>入口点名称（null = 与方法同名）。</summary>
        public string? ImportName { get; }
        public IlCallingConvention CallingConvention { get; }
        /// <summary>实例方法（含 this，签名 HAS_THIS）。</summary>
        public bool IsStatic { get; set; }

        /// <summary>值类型实例方法：this 以 EXPLICITTHIS 显式给出（首位 byref 参数）。</summary>
        public bool IsExplicitThis { get; set; }

        /// <summary>P/Invoke 编码格式（ImplMap CharSet 位）。6e-M17 Step 5。</summary>
        public IlCharSet CharSet { get; }

        public IlVisibility Visibility { get; set; } = IlVisibility.Public;

        public bool IsVirtual { get; set; }

        public bool IsAbstract { get; set; }

        public bool IsSealed { get; set; }
    }

    /// <summary>
    /// ECMA-335 元数据写入器（最小子集）：Module/TypeRef/TypeDef/MethodDef/Param/MemberRef/
    /// CustomAttribute/Assembly/AssemblyRef/StandAloneSig 表 + #Strings/#US/#GUID/#Blob 堆。布局细节对照 Roslyn MetadataWriter / System.Reflection.Metadata.Ecma335.MetadataBuilder。
    /// </summary>
}
