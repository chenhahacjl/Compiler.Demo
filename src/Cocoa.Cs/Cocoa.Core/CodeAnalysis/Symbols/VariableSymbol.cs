using Cocoa.CodeAnalysis.Binding;

namespace Cocoa.CodeAnalysis.Symbols
{
    public abstract class VariableSymbol : Symbol
    {
        internal VariableSymbol(string name, bool isReadOnly, TypeSymbol type, BoundConstant? constant)
            : base(name)
        {
            IsReadOnly = isReadOnly;
            Type = type;
            Constant = isReadOnly ? constant : null;
        }

        public bool IsReadOnly { get; }
        public TypeSymbol Type { get; }
        internal BoundConstant? Constant { get; }

        /// <summary>是否被 lambda 捕获（6e-M22 C5）：捕获变量的规范存储移入堆上环境对象字段。</summary>
        public bool IsCaptured { get; internal set; }
    }
}
