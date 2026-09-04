using System.Collections.Generic;

namespace Cocoa.CodeGen.Managed.Structure
{
    /// <summary>签名中的类型（ECMA-335 III.1.1 元素类型编码所需的最小集）。</summary>
    public enum IlTypeKind
    {
        Void,
        Boolean,
        Int32,
        Int64,
        Char,
        U1,
        Double,
        String,
        Object,
        Class,
        SzArray,
        GenericInst,
        NativeInt,
        GenericParameter,
        I1,
        I2,
        U2,
        U4,
        U8,
        R4,
        ByRef,
    }

    /// <summary>P/Invoke 调用约定（对应 ECMA-335 II.23.1.10 ImplMapFlags.CallConvMask）。</summary>
    public enum IlCallingConvention
    {
        Winapi,
        Cdecl,
        StdCall,
    }

    /// <summary>自研元数据引用：签名与 token 分配的最小描述。</summary>
    public sealed class IlType
    {
        public IlType(IlTypeKind kind, IlTypeRef? reference = null, IlType? elementType = null, IlTypeDef? typeDef = null, bool isValueType = false, IReadOnlyList<IlType>? genericArguments = null, int genericOrdinal = -1)
        {
            Kind = kind;
            Reference = reference;
            ElementType = elementType;
            TypeDef = typeDef;
            IsValueType = isValueType;
            GenericArguments = genericArguments;
            GenericOrdinal = genericOrdinal;
        }

        public IlTypeKind Kind { get; }
        public IlTypeRef? Reference { get; }
        public IlType? ElementType { get; }
        public IlTypeDef? TypeDef { get; }
        public IReadOnlyList<IlType>? GenericArguments { get; }
        public int GenericOrdinal { get; }
        public bool IsValueType { get; }

        public static readonly IlType Void = new IlType(IlTypeKind.Void);
        public static readonly IlType Boolean = new IlType(IlTypeKind.Boolean);
        public static readonly IlType Int32 = new IlType(IlTypeKind.Int32);
        public static readonly IlType Int64 = new IlType(IlTypeKind.Int64);
        public static readonly IlType Char = new IlType(IlTypeKind.Char);
        public static readonly IlType Byte = new IlType(IlTypeKind.U1);
        public static readonly IlType Double = new IlType(IlTypeKind.Double);
        public static readonly IlType String = new IlType(IlTypeKind.String);
        public static readonly IlType Object = new IlType(IlTypeKind.Object);
        public static readonly IlType SByte = new IlType(IlTypeKind.I1);
        public static readonly IlType Int16 = new IlType(IlTypeKind.I2);
        public static readonly IlType UInt16 = new IlType(IlTypeKind.U2);
        public static readonly IlType UInt32 = new IlType(IlTypeKind.U4);
        public static readonly IlType UInt64 = new IlType(IlTypeKind.U8);
        public static readonly IlType Float = new IlType(IlTypeKind.R4);

        public static IlType Class(IlTypeRef reference, bool isValueType = false) => new IlType(IlTypeKind.Class, reference, isValueType: isValueType);
        public static IlType Class(IlTypeDef typeDef, bool isValueType = false) => new IlType(IlTypeKind.Class, typeDef: typeDef, isValueType: isValueType);
        public static IlType SzArrayOf(IlType elementType) => new IlType(IlTypeKind.SzArray, elementType: elementType);
        public static IlType GenericInstance(IlTypeRef definition, IReadOnlyList<IlType> arguments) => new IlType(IlTypeKind.GenericInst, definition, genericArguments: arguments);
        public static readonly IlType NativeInt = new IlType(IlTypeKind.NativeInt);
        public static IlType GenericVar(int ordinal) => new IlType(IlTypeKind.GenericParameter, genericOrdinal: ordinal);
        public static IlType ByRefOf(IlType elementType) => new IlType(IlTypeKind.ByRef, elementType: elementType);

        public string FullName => Kind switch
        {
            IlTypeKind.Void => "System.Void",
            IlTypeKind.Boolean => "System.Boolean",
            IlTypeKind.Int32 => "System.Int32",
            IlTypeKind.Int64 => "System.Int64",
            IlTypeKind.Char => "System.Char",
            IlTypeKind.U1 => "System.Byte",
            IlTypeKind.Double => "System.Double",
            IlTypeKind.String => "System.String",
            IlTypeKind.Object => "System.Object",
            IlTypeKind.I1 => "System.SByte",
            IlTypeKind.I2 => "System.Int16",
            IlTypeKind.U2 => "System.UInt16",
            IlTypeKind.U4 => "System.UInt32",
            IlTypeKind.U8 => "System.UInt64",
            IlTypeKind.R4 => "System.Single",
            IlTypeKind.Class => TypeDef != null
                ? (TypeDef.Namespace.Length == 0 ? TypeDef.Name : TypeDef.Namespace + "." + TypeDef.Name)
                : Reference!.FullName,
            IlTypeKind.SzArray => ElementType!.FullName + "[]",
            IlTypeKind.ByRef => ElementType!.FullName + "&",
            _ => "?",
        };
    }

    /// <summary>元数据引用基类（作为 Dictionary 键用于去重）。</summary>
    public abstract class IlReference
    {
    }

    /// <summary>TypeRef：对另一个程序集/模块中类型的引用。</summary>
    public sealed class IlTypeRef : IlReference
    {
        public IlTypeRef(string? namespaceName, string name, IlAssemblyRef? scope)
        {
            Namespace = namespaceName ?? "";
            Name = name;
            Scope = scope;
        }

        public string Namespace { get; }
        public string Name { get; }
        public IlAssemblyRef? Scope { get; }
        public string FullName => Namespace.Length == 0 ? Name : Namespace + "." + Name;

        public override bool Equals(object? obj) =>
            obj is IlTypeRef other && other.Namespace == Namespace && other.Name == Name && Equals(other.Scope, Scope);

        public override int GetHashCode() => System.HashCode.Combine(Namespace, Name, Scope);
    }

    /// <summary>AssemblyRef：对引用程序集的描述。</summary>
    public sealed class IlAssemblyRef : IlReference
    {
        public IlAssemblyRef(string name, System.Version version, byte[] publicKeyOrToken, string? culture, uint flags)
        {
            Name = name;
            Version = version;
            PublicKeyOrToken = publicKeyOrToken;
            Culture = culture ?? "";
            Flags = flags;
        }

        public string Name { get; }
        public System.Version Version { get; }
        public byte[] PublicKeyOrToken { get; }
        public string Culture { get; }
        public uint Flags { get; }

        public override bool Equals(object? obj) =>
            obj is IlAssemblyRef other &&
            other.Name == Name && other.Version == Version && other.Flags == Flags &&
            System.Linq.Enumerable.SequenceEqual(other.PublicKeyOrToken, PublicKeyOrToken) &&
            other.Culture == Culture;

        public override int GetHashCode() => System.HashCode.Combine(Name, Version);
    }

    /// <summary>MemberRef：对另一个程序集/模块中方法的引用。</summary>
    public sealed class IlMethodRef : IlReference
    {
        public IlMethodRef(IlTypeRef declaringType, string name, IlType returnType, IReadOnlyList<IlType> parameterTypes, bool isStatic = true)
        {
            DeclaringType = declaringType;
            Name = name;
            ReturnType = returnType;
            ParameterTypes = parameterTypes;
            IsStatic = isStatic;
        }

        public IlMethodRef(IlTypeSpec declaringTypeSpec, string name, IlType returnType, IReadOnlyList<IlType> parameterTypes, bool isStatic)
        {
            DeclaringTypeSpec = declaringTypeSpec;
            Name = name;
            ReturnType = returnType;
            ParameterTypes = parameterTypes;
            IsStatic = isStatic;
        }

        public IlTypeRef? DeclaringType { get; }
        public IlTypeSpec? DeclaringTypeSpec { get; }
        public string Name { get; }
        public IlType ReturnType { get; }
        public IReadOnlyList<IlType> ParameterTypes { get; }
        public bool IsStatic { get; set; }

        public override bool Equals(object? obj) =>
            obj is IlMethodRef other &&
            other.Name == Name &&
            other.ReturnType.Kind == ReturnType.Kind && other.IsStatic == IsStatic &&
            System.Linq.Enumerable.SequenceEqual(other.ParameterTypes, ParameterTypes, ReferenceEqualityComparer.Instance) &&
            (other.DeclaringTypeSpec == null
                ? DeclaringTypeSpec == null && other.DeclaringType!.Equals(DeclaringType)
                : DeclaringTypeSpec != null && DeclaringTypeSpec.Equals(other.DeclaringTypeSpec));

        public override int GetHashCode() => System.HashCode.Combine(DeclaringType?.GetHashCode() ?? 0, DeclaringTypeSpec, Name, ReturnType.Kind, ParameterTypes.Count, IsStatic);
    }

    /// <summary>外部字段引用。</summary>
    public sealed class IlFieldRef : IlReference
    {
        public IlFieldRef(IlTypeRef declaringType, string name, IlType fieldType)
        {
            DeclaringType = declaringType;
            Name = name;
            FieldType = fieldType;
        }

        public IlTypeRef DeclaringType { get; }
        public string Name { get; }
        public IlType FieldType { get; }

        public override bool Equals(object? obj) =>
            obj is IlFieldRef other && other.Name == Name && other.FieldType.Kind == FieldType.Kind &&
            other.DeclaringType.Equals(DeclaringType);

        public override int GetHashCode() => System.HashCode.Combine(DeclaringType.GetHashCode(), Name, FieldType.Kind);
    }

    /// <summary>TypeSpec：类型规格签名（GENERICINST blob）。</summary>
    public sealed class IlTypeSpec : IlReference
    {
        public IlTypeSpec(byte[] signature) => Signature = signature;

        public byte[] Signature { get; }

        public override bool Equals(object? obj) =>
            obj is IlTypeSpec other && System.Linq.Enumerable.SequenceEqual(other.Signature, Signature);

        public override int GetHashCode()
        {
            var hash = 19;
            foreach (var b in Signature)
            {
                hash = hash * 31 + b;
            }
            return hash;
        }
    }

    /// <summary>自定义特性（CustomAttribute 表行）。</summary>
    public sealed class IlCustomAttribute
    {
        public IlCustomAttribute(IlMethodRef constructor, byte[] fixedArguments)
        {
            Constructor = constructor;
            FixedArguments = fixedArguments;
        }

        public IlMethodRef Constructor { get; }
        public byte[] FixedArguments { get; }
    }

    /// <summary>StandAloneSig 引用（局部变量签名等）。</summary>
    public interface IIlRefIssuer
    {
        IlAssemblyRef DefineAssemblyRef(string name, Version version, byte[] publicKeyOrToken, string? culture, uint flags);
        IlTypeRef DefineTypeRef(IlAssemblyRef? scope, string? namespaceName, string name);
    }

    public sealed class IlStandAloneSig : IlReference
    {
        public IlStandAloneSig(byte[] signature) => Signature = signature;

        public byte[] Signature { get; }

        public override bool Equals(object? obj) =>
            obj is IlStandAloneSig other && System.Linq.Enumerable.SequenceEqual(other.Signature, Signature);

        public override int GetHashCode()
        {
            var hash = 17;
            foreach (var b in Signature)
            {
                hash = hash * 31 + b;
            }
            return hash;
        }
    }

    /// <summary>自研 IL 指令：OpCode + 可选操作数。</summary>
    public sealed class IlInstruction
    {
        public IlInstruction(IlOpCode opCode, object? operand)
        {
            OpCode = opCode;
            Operand = operand;
        }

        public IlOpCode OpCode { get; }
        public object? Operand { get; }
        public int Offset { get; set; }
    }

    /// <summary>方法体：指令序列 + 局部变量签名 + 最大栈深度。</summary>
    public sealed class IlMethodBody
    {
        public IlMethodBody(List<IlInstruction> instructions, IReadOnlyList<IlType> locals, int maxStack)
        {
            Instructions = instructions;
            Locals = locals;
            MaxStack = maxStack;
        }

        public List<IlInstruction> Instructions { get; }
        public IReadOnlyList<IlType> Locals { get; }
        public int MaxStack { get; }
    }
}
