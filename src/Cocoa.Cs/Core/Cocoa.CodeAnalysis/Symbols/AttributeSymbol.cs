using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Symbols
{
    /// <summary>
    /// 6e-M32 Tier-2：声明上绑定的 attribute（`[Name(args)]` 使用实例）。
    /// Type = 属性类（基链含 System.Attribute）；Arguments = 解析后字面量实参（值 + 类型）。
    /// </summary>
    public sealed class AttributeSymbol
    {
        public AttributeSymbol(NamedTypeSymbol type, ImmutableArray<(TypeSymbol Type, object Value)> arguments)
        {
            Type = type;
            Arguments = arguments;
        }

        public NamedTypeSymbol Type { get; }

        /// <summary>声明时用的名字（含省略前全名？= Type.Name；省略后使用名＝来源文本）。</summary>
        public string UsedName => Type.Name;

        public ImmutableArray<(TypeSymbol Type, object Value)> Arguments { get; }
    }
}