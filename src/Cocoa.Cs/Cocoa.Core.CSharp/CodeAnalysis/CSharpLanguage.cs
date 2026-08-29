using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis
{
    /// <summary>
    /// C# 方言语言（M2 设计 X）：位于独立程序集 Cocoa.Core.CSharp，核心零改动即可挂载。
    /// 内建类型原名映射（int/long/short/.../float/double；与 CO 的简写表（Cocoa.Core.Cocoa 的
    /// CocoaLanguage）解耦为两套词汇，同一 TypeSymbol）。实例经 <see cref="Language"/> 注册表暴露（"csharp"），
    /// 由 <see cref="Syntax.SyntaxTree.Load"/>（.cs 扩展名）/ <c>ParseCs</c> 消费。
    /// </summary>
    public sealed class CSharpLanguage : Language
    {
        public static readonly CSharpLanguage Instance = new CSharpLanguage();

        private CSharpLanguage()
            : base("csharp")
        {
        }

        /// <summary>`.cs` 参数为类型前置 `int x`（参数绿往返源序化依据）。</summary>
        public override bool ParametersAreTypeFirst => true;

        protected override TypeSymbol? LookupSpecificBuiltinType(string name) => name switch
        {
            "int" => TypeSymbol.Int32,
            "long" => TypeSymbol.Int64,
            "short" => TypeSymbol.Int16,
            "ushort" => TypeSymbol.UInt16,
            "uint" => TypeSymbol.UInt32,
            "ulong" => TypeSymbol.UInt64,
            "sbyte" => TypeSymbol.Int8,
            "byte" => TypeSymbol.UInt8,
            "float" => TypeSymbol.Float,
            "double" => TypeSymbol.Double,
            _ => null,
        };

        internal override ParserCore CreateParser(SyntaxTree syntaxTree) => new CSharpParser(syntaxTree);

        internal override ParserCore CreateParser(SyntaxTree syntaxTree, ImmutableArray<SyntaxToken> tokens)
            => new CSharpParser(syntaxTree, tokens);
    }
}