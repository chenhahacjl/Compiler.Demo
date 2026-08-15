using System.Collections.Generic;

namespace Cocoa.CodeAnalysis.Emit.IL
{
    /// <summary>签名中的类型（ECMA-335 III.1.1 元素类型编码所需的最小集）。</summary>
    internal enum IlTypeKind
    {
        Void,
        Boolean,
        Int32,
        Double,
        String,
        Object,
        Class,          // 引用类型（TypeRef/TypeDef）
        SzArray,        // 一维数组（元素为另一个类型）
    }

    /// <summary>自研元数据引用：签名与 token 分配的最小描述。</summary>
    internal sealed class IlType
    {
        public IlType(IlTypeKind kind, IlTypeRef? reference = null, IlType? elementType = null)
        {
            Kind = kind;
            Reference = reference;
            ElementType = elementType;
        }

        public IlTypeKind Kind { get; }
        public IlTypeRef? Reference { get; }
        public IlType? ElementType { get; }

        public static readonly IlType Void = new IlType(IlTypeKind.Void);
        public static readonly IlType Boolean = new IlType(IlTypeKind.Boolean);
        public static readonly IlType Int32 = new IlType(IlTypeKind.Int32);
        public static readonly IlType Double = new IlType(IlTypeKind.Double);
        public static readonly IlType String = new IlType(IlTypeKind.String);
        public static readonly IlType Object = new IlType(IlTypeKind.Object);

        public static IlType Class(IlTypeRef reference) => new IlType(IlTypeKind.Class, reference);
        public static IlType SzArrayOf(IlType elementType) => new IlType(IlTypeKind.SzArray, elementType: elementType);

        /// <summary>CLR 元数据全名（参数类型匹配用）。</summary>
        public string FullName => Kind switch
        {
            IlTypeKind.Void => "System.Void",
            IlTypeKind.Boolean => "System.Boolean",
            IlTypeKind.Int32 => "System.Int32",
            IlTypeKind.Double => "System.Double",
            IlTypeKind.String => "System.String",
            IlTypeKind.Object => "System.Object",
            IlTypeKind.Class => Reference!.FullName,
            IlTypeKind.SzArray => ElementType!.FullName + "[]",
            _ => "?",
        };
    }

    /// <summary>元数据引用基类（作为 Dictionary 键用于去重）。</summary>
    internal abstract class IlReference
    {
    }

    /// <summary>TypeRef：对另一个程序集/模块中类型的引用。</summary>
    internal sealed class IlTypeRef : IlReference
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
    internal sealed class IlAssemblyRef : IlReference
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
    internal sealed class IlMethodRef : IlReference
    {
        public IlMethodRef(IlTypeRef declaringType, string name, IlType returnType, IReadOnlyList<IlType> parameterTypes, bool isStatic = true)
        {
            DeclaringType = declaringType;
            Name = name;
            ReturnType = returnType;
            ParameterTypes = parameterTypes;
            IsStatic = isStatic;
        }

        public IlTypeRef DeclaringType { get; }
        public string Name { get; }
        public IlType ReturnType { get; }
        public IReadOnlyList<IlType> ParameterTypes { get; }
        public bool IsStatic { get; }

        public override bool Equals(object? obj) =>
            obj is IlMethodRef other &&
            other.DeclaringType.Equals(DeclaringType) && other.Name == Name &&
            other.ReturnType.Kind == ReturnType.Kind && other.IsStatic == IsStatic &&
            System.Linq.Enumerable.SequenceEqual(other.ParameterTypes, ParameterTypes, ReferenceEqualityComparer.Instance);

        public override int GetHashCode() => System.HashCode.Combine(DeclaringType, Name, ReturnType.Kind, ParameterTypes.Count, IsStatic);
    }

    /// <summary>自定义特性（CustomAttribute 表行）。</summary>
    internal sealed class IlCustomAttribute
    {
        public IlCustomAttribute(IlMethodRef constructor, byte[] fixedArguments)
        {
            Constructor = constructor;
            FixedArguments = fixedArguments;
        }

        public IlMethodRef Constructor { get; }
        public byte[] FixedArguments { get; }
    }

    /// <summary>StandAloneSig 引用（局部变量签名等），作为 token fixup 的稳定 key。</summary>
    internal sealed class IlStandAloneSig : IlReference
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
    internal sealed class IlInstruction
    {
        public IlInstruction(IlOpCode opCode, object? operand)
        {
            OpCode = opCode;
            Operand = operand;
        }

        public IlOpCode OpCode { get; }
        public object? Operand { get; }

        /// <summary>编码后在方法体中的偏移（分支 fixup 用）。</summary>
        public int Offset { get; set; }
    }

    /// <summary>方法体：指令序列 + 局部变量签名 + 最大栈深度。</summary>
    internal sealed class IlMethodBody
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
