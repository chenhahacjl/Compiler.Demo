namespace Cocoa.CodeGen.Native.Lir
{
    /// <summary>函数参数：仅携带序号（调用约定由后端决定）。</summary>
    public sealed class LirParameter
    {
        public LirParameter(string? name, int ordinal)
        {
            Name = name;
            Ordinal = ordinal;
        }

        public string? Name { get; }
        public int Ordinal { get; }
    }
}