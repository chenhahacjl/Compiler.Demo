using System.Collections.Generic;

namespace Cocoa.Metadata
{
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
}
