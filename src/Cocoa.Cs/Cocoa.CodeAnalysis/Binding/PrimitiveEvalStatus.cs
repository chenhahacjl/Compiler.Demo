namespace Cocoa.CodeAnalysis.Binding
{
    public enum PrimitiveEvalStatus
    {
        /// <summary>result 有效。</summary>
        Computed,
        /// <summary>整数模零：折叠层跳过折叠、运行时层抛 DivideByZeroException。</summary>
        NotComputable,
        /// <summary>本核不含（string+double 定点拼接、引用相等）：调用方自行处理。</summary>
        Unsupported,
    }
}