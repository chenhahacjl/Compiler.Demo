using System.Collections.Generic;

namespace Cocoa.Metadata
{
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
}
