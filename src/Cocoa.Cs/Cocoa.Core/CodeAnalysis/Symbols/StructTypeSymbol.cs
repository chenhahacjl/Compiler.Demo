using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Symbols
{
    /// <summary>
    /// struct（值类型，6e-M26）：字段内联存储、值语义（赋值/传参按值拷贝）。
    /// MVP 范围：值字段 + 构造器 + 字段访问 + 按值传参；IL 按 VALUETYPE/initobj 发射，
    /// native/evaluator 后置。不可继承、不可被继承（隐式 sealed）、不可为接口/abstract/facade。
    /// </summary>
    public sealed class StructTypeSymbol : ClassTypeSymbol
    {
        internal StructTypeSymbol(string name, string @namespace, Visibility visibility, ClassDeclarationSyntax? declaration)
            : base(name, @namespace, visibility, declaration)
        {
            IsSealed = true; // struct 不可被继承
        }

        public override bool IsValueType => true;
    }
}
