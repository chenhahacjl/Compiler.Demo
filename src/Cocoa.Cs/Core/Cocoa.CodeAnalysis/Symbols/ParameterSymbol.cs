namespace Cocoa.CodeAnalysis.Symbols
{
    public sealed class ParameterSymbol : LocalVariableSymbol
    {
        private readonly bool _isThis;

        public ParameterSymbol(string name, TypeSymbol type, int ordinal, bool isOut = false, bool isRef = false, bool isThis = false, object? defaultValue = null, bool hasDefault = false, bool isParams = false, bool isReadOnly = false)
            : base(name, isReadOnly: isThis || isReadOnly, type, null)
        {
            Ordinal = ordinal;
            IsOut = isOut;
            IsRef = isRef;
            _isThis = isThis;
            DefaultValue = defaultValue;
            HasDefaultValue = hasDefault;
            IsParams = isParams;
        }

        public override SymbolKind Kind => SymbolKind.Parameter;

        public int Ordinal { get; }

        /// <summary>out 形参（6e-M23）：入口未赋值（DFA 强制出口前赋值），被调方写入调用方实参。</summary>
        public bool IsOut { get; }

        /// <summary>ref 形参（6e-M23）：双向别名，调用方须已赋值。</summary>
        public bool IsRef { get; }

        /// <summary>params 可变参数（语言后置件）：尾参数组，调用多余的实参打包进数组。</summary>
        public bool IsParams { get; }

        public bool IsByRef => IsOut || IsRef;

        /// <summary>实例方法隐式 this 形参（构造 isThis:true 显式注入）。与 <see cref="VariableSymbol.IsReadOnly"/> 解耦
        /// （2026-10-20）：this 恒只读，但只读参数（如后续 `in` 修饰符）不得误判为 this——facade 实例判定
        /// （IlEmitter.Facades.cs）/方法 arity（CocoaBinder.Classes.cs）/`.coa` 序列化（CoaFormatWriter.cs）均以本属性为准。</summary>
        public bool IsThisParameter => _isThis;

        /// <summary>可选参数默认值（语言后置件）：调用缺省实参时补此常量。</summary>
        public object? DefaultValue { get; }

        public bool HasDefaultValue { get; }
    }
}
