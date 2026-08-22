using System.Collections.Generic;

namespace Cocoa.CodeAnalysis.Emit.IL
{
    /// <summary>
    /// ECMA-335 III.2 操作数类型（OperandType）。编码器按此分派，全集一次实现；
    /// opcode 条目按需添加（加指令 = 加一行表条目）。
    /// </summary>
    internal enum IlOperandType
    {
        InlineNone,
        InlineI,            // int32 直接量
        InlineI8,           // int64
        InlineR,            // float64
        ShortInlineR,       // float32
        ShortInlineI,       // int8
        InlineString,       // #US 堆 token（4 字节）
        InlineMethod,       // 方法 token
        InlineType,         // 类型 token
        InlineField,        // 字段 token
        InlineTok,          // 任意 token
        InlineSig,          // 签名 token（StandAloneSig）
        InlineVar,          // uint16 局部/参数索引
        ShortInlineVar,     // uint8 局部/参数索引
        InlineBrTarget,     // int32 分支偏移
        ShortInlineBrTarget,// int8 分支偏移
        InlineSwitch,       // uint32 计数 + int32 偏移表
    }

    internal readonly struct IlOpCode
    {
        public IlOpCode(ushort value, IlOperandType operandType, int size)
        {
            Value = value;
            OperandType = operandType;
            Size = size;
        }

        public ushort Value { get; }
        public IlOperandType OperandType { get; }
        public int Size { get; }

        /// <summary>双字节 opcode（0xFE 前缀）</summary>
        public bool IsTwoByte => Value >= 0xFE00;

        public override string ToString()
        {
            if (IsTwoByte)
            {
                return "0xFE" + (Value & 0xFF).ToString("X2");
            }

            return "0x" + Value.ToString("X2");
        }
    }

    /// <summary>
    /// ECMA-335 III.2 操作码表（条目按需添加，覆盖当前 IlEmitter 全部指令 + 阶段 6 常用扩展）。
    /// </summary>
    internal static class IlOpCodeTable
    {
        private static readonly Dictionary<string, IlOpCode> s_table = new Dictionary<string, IlOpCode>();

        private static void Define(string name, int value, IlOperandType operandType)
        {
            var size = value > 0xFF ? 2 : 1;
            s_table.Add(name, new IlOpCode((ushort)value, operandType, size));
        }

        public static bool TryGet(string name, out IlOpCode opCode) => s_table.TryGetValue(name, out opCode);

        public static IlOpCode Get(string name) => s_table[name];

        static IlOpCodeTable()
        {
            // 单字节：基本
            Define("Nop", 0x00, IlOperandType.InlineNone);
            Define("Ret", 0x2A, IlOperandType.InlineNone);
            Define("Dup", 0x25, IlOperandType.InlineNone);
            Define("Pop", 0x26, IlOperandType.InlineNone);
            Define("Ldnull", 0x14, IlOperandType.InlineNone);

            // 常量
            Define("Ldc_I4_M1", 0x15, IlOperandType.InlineNone);
            Define("Ldc_I4_0", 0x16, IlOperandType.InlineNone);
            Define("Ldc_I4_1", 0x17, IlOperandType.InlineNone);
            Define("Ldc_I4_2", 0x18, IlOperandType.InlineNone);
            Define("Ldc_I4_3", 0x19, IlOperandType.InlineNone);
            Define("Ldc_I4_4", 0x1A, IlOperandType.InlineNone);
            Define("Ldc_I4_5", 0x1B, IlOperandType.InlineNone);
            Define("Ldc_I4_6", 0x1C, IlOperandType.InlineNone);
            Define("Ldc_I4_7", 0x1D, IlOperandType.InlineNone);
            Define("Ldc_I4_8", 0x1E, IlOperandType.InlineNone);
            Define("Ldc_I4_S", 0x1F, IlOperandType.ShortInlineI);
            Define("Ldc_I4", 0x20, IlOperandType.InlineI);
            Define("Ldc_I8", 0x21, IlOperandType.InlineI8);
            Define("Ldc_R4", 0x22, IlOperandType.ShortInlineR);
            Define("Ldc_R8", 0x23, IlOperandType.InlineR);

            // 参数/局部变量
            Define("Ldarg_0", 0x02, IlOperandType.InlineNone);
            Define("Ldarg_1", 0x03, IlOperandType.InlineNone);
            Define("Ldarg_2", 0x04, IlOperandType.InlineNone);
            Define("Ldarg_3", 0x05, IlOperandType.InlineNone);
            Define("Ldarg_S", 0x0E, IlOperandType.ShortInlineVar);
            Define("Ldloc_S", 0x11, IlOperandType.ShortInlineVar);
            Define("Stloc_S", 0x13, IlOperandType.ShortInlineVar);
            Define("Ldloc_0", 0x06, IlOperandType.InlineNone);
            Define("Ldloc_1", 0x07, IlOperandType.InlineNone);
            Define("Ldloc_2", 0x08, IlOperandType.InlineNone);
            Define("Ldloc_3", 0x09, IlOperandType.InlineNone);
            Define("Stloc_0", 0x0A, IlOperandType.InlineNone);
            Define("Stloc_1", 0x0B, IlOperandType.InlineNone);
            Define("Stloc_2", 0x0C, IlOperandType.InlineNone);
            Define("Stloc_3", 0x0D, IlOperandType.InlineNone);

            // 算术/逻辑
            Define("Add", 0x58, IlOperandType.InlineNone);
            Define("Sub", 0x59, IlOperandType.InlineNone);
            Define("Mul", 0x5A, IlOperandType.InlineNone);
            Define("Div", 0x5B, IlOperandType.InlineNone);
            Define("Rem", 0x5D, IlOperandType.InlineNone);
            Define("And", 0x5F, IlOperandType.InlineNone);
            Define("Or", 0x60, IlOperandType.InlineNone);
            Define("Xor", 0x61, IlOperandType.InlineNone);
            Define("Shl", 0x62, IlOperandType.InlineNone);
            Define("Shr", 0x63, IlOperandType.InlineNone);
            Define("Neg", 0x65, IlOperandType.InlineNone);
            Define("Not", 0x66, IlOperandType.InlineNone);

            // 转换
            Define("Conv_I1", 0x67, IlOperandType.InlineNone);
            Define("Conv_I2", 0x68, IlOperandType.InlineNone);
            Define("Conv_I4", 0x69, IlOperandType.InlineNone);
            Define("Conv_U1", 0xD2, IlOperandType.InlineNone);
            Define("Conv_U2", 0xD4, IlOperandType.InlineNone);
            Define("Conv_I8", 0x6A, IlOperandType.InlineNone);
            Define("Conv_R4", 0x6B, IlOperandType.InlineNone);
            Define("Conv_R8", 0x6C, IlOperandType.InlineNone);
            Define("Conv_U4", 0x6D, IlOperandType.InlineNone);
            Define("Conv_U8", 0x6E, IlOperandType.InlineNone);

            // 比较
            Define("Ceq", 0xFE01, IlOperandType.InlineNone);
            Define("Cgt", 0xFE02, IlOperandType.InlineNone);
            Define("Clt", 0xFE04, IlOperandType.InlineNone);

            // 分支
            Define("Br_S", 0x2B, IlOperandType.ShortInlineBrTarget);
            Define("Brfalse_S", 0x2C, IlOperandType.ShortInlineBrTarget);
            Define("Brtrue_S", 0x2D, IlOperandType.ShortInlineBrTarget);
            Define("Br", 0x38, IlOperandType.InlineBrTarget);
            Define("Brfalse", 0x39, IlOperandType.InlineBrTarget);
            Define("Brtrue", 0x3A, IlOperandType.InlineBrTarget);
            Define("Switch", 0x45, IlOperandType.InlineSwitch);
            Define("Leave", 0xDD, IlOperandType.InlineBrTarget);
            Define("Leave_S", 0xDE, IlOperandType.ShortInlineBrTarget);
            Define("Endfinally", 0xDC, IlOperandType.InlineNone);

            // 调用/对象
            Define("Call", 0x28, IlOperandType.InlineMethod);
            Define("Callvirt", 0x6F, IlOperandType.InlineMethod);
            Define("Newobj", 0x73, IlOperandType.InlineMethod);
            Define("Ldftn", 0xFE06, IlOperandType.InlineMethod);
            Define("Box", 0x8C, IlOperandType.InlineType);
            Define("Newarr", 0x8D, IlOperandType.InlineType);
            Define("Castclass", 0x74, IlOperandType.InlineType);
            Define("Isinst", 0x75, IlOperandType.InlineType);
            Define("Unbox_Any", 0xA5, IlOperandType.InlineType);
            Define("Initobj", 0xFE15, IlOperandType.InlineType);
            Define("Constrained", 0xFE16, IlOperandType.InlineType);

            // 字段/数组
            Define("Ldsfld", 0x7E, IlOperandType.InlineField);
            Define("Ldfld", 0x7B, IlOperandType.InlineField);
            Define("Stsfld", 0x80, IlOperandType.InlineField);
            Define("Stfld", 0x7D, IlOperandType.InlineField);
            Define("Ldlen", 0x8E, IlOperandType.InlineNone);
            Define("Ldelem_I1", 0x90, IlOperandType.InlineNone);
            Define("Ldelem_U1", 0x91, IlOperandType.InlineNone);
            Define("Ldelem_I4", 0x94, IlOperandType.InlineNone);
            Define("Ldelem_I2", 0x92, IlOperandType.InlineNone);
            Define("Ldelem_U2", 0x93, IlOperandType.InlineNone);
            Define("Ldelem_I8", 0x97, IlOperandType.InlineNone);
            Define("Ldelem_R8", 0x99, IlOperandType.InlineNone);
            Define("Ldelem_Ref", 0x9A, IlOperandType.InlineNone);
            Define("Stelem_I1", 0x9C, IlOperandType.InlineNone);
            Define("Stelem_I2", 0x9D, IlOperandType.InlineNone);
            Define("Stelem_I4", 0x9E, IlOperandType.InlineNone);
            Define("Stelem_I8", 0x9F, IlOperandType.InlineNone);
            Define("Stelem_R8", 0xA1, IlOperandType.InlineNone);
            Define("Stelem_Ref", 0xA2, IlOperandType.InlineNone);

            // 字符串
            Define("Ldstr", 0x72, IlOperandType.InlineString);

            // 局部/参数（长形式，2 字节索引）
            Define("Ldarg", 0xFE09, IlOperandType.InlineVar);
            Define("Ldarga", 0xFE0A, IlOperandType.InlineVar);
            Define("Ldloc", 0xFE0C, IlOperandType.InlineVar);
            Define("Ldloca", 0xFE0D, IlOperandType.InlineVar);
            Define("Stloc", 0xFE0E, IlOperandType.InlineVar);
        }
    }
}
