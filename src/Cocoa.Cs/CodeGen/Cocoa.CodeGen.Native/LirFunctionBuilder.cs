using Cocoa.CodeAnalysis.Bound;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeGen.Native.Lir;
using System.Collections.Generic;

namespace Cocoa.CodeGen.Native
{
    /// <summary>
    /// 单个方法体的 LIR 构建状态（4.2 试点）：把 <c>MirToLir</c> 的"方法体构建状态"（当前函数、变量槽映射、
    /// 标签映射、this/闭包寄存器、标签计数）收拢为协作对象。MirToLir 保留程序级上下文（分配器/程序/存活类/vtable）
    /// 与逐语句/表达式驱动，经本类维护每个函数的可变构建状态。
    /// </summary>
    internal sealed class LirFunctionBuilder
    {
        /// <summary>当前构建的 LIR 函数。</summary>
        public LirFunction CurrentFunction = null!;

        /// <summary>变量符号 → 虚拟寄存器（当前函数作用域）。</summary>
        public readonly Dictionary<VariableSymbol, LirVirtualRegister> Variables = new();

        /// <summary>BoundLabel → LIR 标签 id（当前函数作用域）。</summary>
        public readonly Dictionary<BoundLabel, int> Labels = new();

        /// <summary>当前函数下一可用标签 id。</summary>
        public int NextLabelId;

        /// <summary>this 指针寄存器（当前函数实例方法；静态函数为 null）。</summary>
        public LirVirtualRegister? ThisRegister;

        /// <summary>闭包环境对象寄存器（当前函数；无捕获为 null）。</summary>
        public LirVirtualRegister? ClosureRegister;

        /// <summary>闭包环境类（当前函数；无捕获为 null）。</summary>
        public NamedTypeSymbol? ClosureClass;

        /// <summary>一条指令（追加到给定指令列表——调用方显式传列表，builder 不持块构建状态）。</summary>
        public static void Add(List<LirInstruction> instructions, LirInstruction instruction)
        {
            instructions.Add(instruction);
        }

        /// <summary>重置为下一函数构建的初始状态。</summary>
        public void Reset(LirFunction function)
        {
            CurrentFunction = function;
            Variables.Clear();
            Labels.Clear();
            NextLabelId = 0;
            ThisRegister = null;
            ClosureRegister = null;
            ClosureClass = null;
        }
    }
}