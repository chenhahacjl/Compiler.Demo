using System.Collections.Generic;

namespace Cocoa.Metadata
{
    public enum IlOperandType
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
}
