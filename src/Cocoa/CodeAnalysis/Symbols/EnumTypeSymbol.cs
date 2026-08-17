using System.Collections.Generic;

namespace Cocoa.CodeAnalysis.Symbols
{
    /// <summary>
    /// 枚举类型：底层值为 int（运行时与 int 同表示），成员名映射到常量值。
    /// </summary>
    public sealed class EnumTypeSymbol : TypeSymbol
    {
        private readonly Dictionary<string, int> _members;

        internal EnumTypeSymbol(string name, Dictionary<string, int> members)
            : base(name)
        {
            _members = members;
        }

        public override SymbolKind Kind => SymbolKind.Enum;

        public bool TryGetMember(string name, out int value) => _members.TryGetValue(name, out value);

        public IReadOnlyCollection<string> MemberNames => _members.Keys;
    }
}