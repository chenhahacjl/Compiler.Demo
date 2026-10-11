using Cocoa.CodeAnalysis.Bound;
using Cocoa.CodeAnalysis.Symbols;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 作用域栈（P5 步骤 2）：把 CocoaBinder 的裸 <c>_scope</c> 推弹（<c>_scope = new BoundScope(_scope)</c> →
    /// <c>_scope = _scope.Parent!</c>，及 lambda 体的保存/恢复）收口为显式 Push/Pop。
    /// 只提供 <see cref="Current"/> 只读视图；查找仍走 <see cref="BoundScope"/> 自身方法。
    /// 推弹必须配对（Push 后必有 Pop/恢复），否则作用域链泄漏。
    /// </summary>
    internal sealed class ScopeStack
    {
        public ScopeStack(BoundScope initial)
        {
            Current = initial;
        }

        /// <summary>当前作用域（查找的入口）。</summary>
        public BoundScope Current { get; private set; }

        /// <summary>进入子作用域（父 = 当前），返回新作用域供 TryDeclare 使用。</summary>
        public BoundScope Push()
        {
            Current = new BoundScope(Current);
            return Current;
        }

        /// <summary>退出子作用域（恢复到父）。须与 <see cref="Push"/> 配对。</summary>
        public void Pop()
        {
            Current = Current.Parent!;
        }

        /// <summary>保存当前作用域引用（lambda 体外层保存用）。</summary>
        public BoundScope Save() => Current;

        /// <summary>恢复保存的作用域引用（lambda 体恢复外层用）。</summary>
        public void Restore(BoundScope saved)
        {
            Current = saved;
        }

        /// <summary>构造时以指定父作用域开新栈（初始即一个子作用域）。</summary>
        public static ScopeStack Create(BoundScope? parent)
        {
            return new ScopeStack(new BoundScope(parent));
        }
    }

    /// <summary>
    /// 绑定主体上下文（P5 步骤 2）：随绑定主体切换的可变状态——当前函数、当前类、声明绑定类、
    /// 泛型方法类型参数、环境对象宿主、lambda 体深度。各进入点显式切换（EnterFunction/EnterClass/EnterLambda）。
    /// </summary>
    internal sealed class BindingSubject
    {
        /// <summary>当前绑定函数（null = 全局/脚本）。</summary>
        public FunctionSymbol? Function { get; set; }

        /// <summary>当前绑定类（函数所属类）。</summary>
        public NamedTypeSymbol? CurrentClass { get; set; }

        /// <summary>类/接口声明绑定上下文：成员签名/基类/约束解析期间的类型参数来源。</summary>
        public NamedTypeSymbol? BindingClass { get; set; }

        /// <summary>泛型方法签名绑定上下文：BindFunctionDeclaration / 类方法 / 接口成员签名期间的 T 解析。</summary>
        public ImmutableArray<TypeParameterSymbol> DeclaringMethodTypeParameters { get; set; } = ImmutableArray<TypeParameterSymbol>.Empty;

        /// <summary>环境对象宿主函数（捕获变量承载者；lambda 继承最外层非 lambda 函数）。</summary>
        public FunctionSymbol? EnvironmentOwner { get; set; }

        /// <summary>lambda 体绑定深度：>0 时返回语句按推断语义处理（不套外层签名转换）。</summary>
        public int LambdaBodyDepth { get; set; }

        public bool IsBindingLambdaBody() => LambdaBodyDepth > 0;
    }

    /// <summary>
    /// 泛型绑定上下文（P5 步骤 2）：类型实参名映射（Monomorphizer 重绑泛型定义语法时把 T/U… 解析到具体实参）。
    /// </summary>
    internal sealed class GenericBindingContext
    {
        public System.Collections.Generic.Dictionary<string, TypeSymbol> TypeArgumentsByName { get; } = new();
    }
}