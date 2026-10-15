using Cocoa.CodeAnalysis.Bound;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.IlMetadata;
using System.Collections.Generic;

namespace Cocoa.CodeGen.Managed.Writer
{
    /// <summary>
    /// 单个方法体的 IL 发射状态（4.1）：把 <c>IlEmitter</c> 的"方法体发射状态"（局部变量槽、临时槽、
    /// 标签占位、异常保护区、return 出口、闭包环境局部）收拢为协作对象，随方法体发射创建、结束时释放。
    /// IlEmitter 保留跨方法共享依赖（MetadataBuilder/token 映射/框架引用/符号 Def 映射/cod 缓存）。
    /// </summary>
    internal sealed class MethodBodyBuilder
    {
        /// <summary>局部变量槽映射（按声明顺序分配索引）。</summary>
        public readonly Dictionary<VariableSymbol, int> Locals = new();

        /// <summary>表达式 → 临时局部槽（发射中途按需分配）。</summary>
        public readonly Dictionary<BoundExpression, int> TemporaryLocalIndices = new();

        /// <summary>非表达式键的合成临时槽（builtin 内部用，如 CopyRange 的 count/dst）。</summary>
        public readonly Dictionary<object, int> SyntheticTemporaryLocalIndices = new();

        /// <summary>当前函数局部类型列表（StandAloneSig 源；发射中途追加）。</summary>
        public List<IlType> CurrentFunctionLocals = new();

        /// <summary>保护区内 return 的出口块（leave 目标）——CLR 要求离开保护区域须 leave，且 finally 须先执行。</summary>
        public IlInstruction? ReturnExitTarget;

        /// <summary>出口块是否带值。</summary>
        public bool ReturnExitHasValue;

        /// <summary>出口值暂存局部槽。</summary>
        public int ReturnExitTemp = -1;

        /// <summary>当前是否在 checked 块内（整数算术发 add.ovf 等）。</summary>
        public bool CheckedArithmetic;

        /// <summary>保护区嵌套深度（try/catch/finally）。</summary>
        public int ProtectedBlockDepth;

        /// <summary>标签占位 fixup（前向引用目标指令对象）。</summary>
        public readonly Dictionary<BoundLabel, IlInstruction> LabelTargets = new();

        /// <summary>当前方法返回类型（return 的 box 逻辑）。</summary>
        public TypeSymbol CurrentMethodReturnType = TypeSymbol.Void;

        /// <summary>当前方法是否实例方法（arg 序号偏移）。</summary>
        public bool CurrentMethodIsInstance;

        /// <summary>入口函数是否为 void main（决定 Main 返回改写）。</summary>
        public bool EntryVoidMain;

        /// <summary>当前方法环境对象局部槽索引（无捕获 = null）。</summary>
        public int? ClosureEnvLocalIndex;

        /// <summary>当前方法闭包环境类（无捕获 = null）。</summary>
        public NamedTypeSymbol? ClosureClass;

        /// <summary>当前闭包环境字段映射（无捕获 = null）。</summary>
        public Dictionary<string, IlFieldDef>? ClosureFieldDefs;
    }
}