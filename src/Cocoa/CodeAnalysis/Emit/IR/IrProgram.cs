using System.Collections.Generic;

namespace Cocoa.CodeAnalysis.Emit.IR
{
    /// <summary>数据段项语义：Int32 / 指针（平台宽 4/8）/ UTF-16 字符串 / 原始字节。</summary>
    internal enum IrDataKind
    {
        Int32,
        Pointer,
        Utf16,
        Bytes,
    }

    /// <summary>数据段项（运行时数据与字符串字面量统一建模）。</summary>
    internal sealed class IrDataItem
    {
        public IrDataItem(string key, IrDataKind kind, int intValue, string? text, byte[]? bytes)
        {
            Key = key;
            Kind = kind;
            IntValue = intValue;
            Text = text;
            Bytes = bytes;
        }

        public string Key { get; }
        public IrDataKind Kind { get; }
        public int IntValue { get; }
        public string? Text { get; }
        public byte[]? Bytes { get; }

        public static IrDataItem Int32(string key, int value) => new IrDataItem(key, IrDataKind.Int32, value, null, null);
        public static IrDataItem Pointer(string key) => new IrDataItem(key, IrDataKind.Pointer, 0, null, null);
        public static IrDataItem Utf16(string key, string text) => new IrDataItem(key, IrDataKind.Utf16, 0, text, null);
        public static IrDataItem ByteArray(string key, byte[] bytes) => new IrDataItem(key, IrDataKind.Bytes, 0, null, bytes);
    }

    /// <summary>函数参数：仅携带序号（调用约定由后端决定）。</summary>
    internal sealed class IrParameter
    {
        public IrParameter(string? name, int ordinal)
        {
            Name = name;
            Ordinal = ordinal;
        }

        public string? Name { get; }
        public int Ordinal { get; }
    }

    /// <summary>IR 函数：指令列表 + 虚拟寄存器宽度表。</summary>
    internal sealed class IrFunction
    {
        public IrFunction(string name, IReadOnlyList<IrParameter> parameters)
        {
            Name = name;
            Parameters = parameters;
            Instructions = new List<IrInstruction>();
            RegisterSizes = new Dictionary<IrVirtualRegister, int>();
        }

        public string Name { get; }
        public IReadOnlyList<IrParameter> Parameters { get; }
        public List<IrInstruction> Instructions { get; }
        public Dictionary<IrVirtualRegister, int> RegisterSizes { get; }
        public int ReturnSize { get; set; }
        public int EndLabelId { get; set; }

        public int RegisterSize(IrVirtualRegister register) => RegisterSizes[register];
    }

    /// <summary>整个 IR 程序：函数表 + 数据段 + 运行时配置 + 入口函数名。</summary>
    internal sealed class IrProgram
    {
        private readonly Dictionary<string, int> _dataIndex = new();

        public IrProgram(string entryFunctionName)
        {
            EntryFunctionName = entryFunctionName;
            Functions = new List<IrFunction>();
            Data = new Dictionary<string, IrDataItem>();
            DataItems = new List<IrDataItem>();
            Imports = new List<string>();
            SpecialFunctions = new Dictionary<string, IrFunction>();
        }

        public string EntryFunctionName { get; }
        public List<IrFunction> Functions { get; }
        public Dictionary<string, IrDataItem> Data { get; }
        public List<IrDataItem> DataItems { get; }
        public List<string> Imports { get; }
        public Dictionary<string, IrFunction> SpecialFunctions { get; }

        /// <summary>取或建字符串字面量数据项（去重，返回 key）。</summary>
        public string InternString(string text)
        {
            if (!_dataIndex.TryGetValue(text, out _))
            {
                var item = IrDataItem.Utf16(text, text);
                _dataIndex.Add(text, DataItems.Count);
                Data.Add(text, item);
                DataItems.Add(item);
            }

            return text;
        }

        /// <summary>追加数据项（运行时数据），返回其 key。</summary>
        public string AddData(IrDataItem item)
        {
            if (!_dataIndex.TryGetValue(item.Key, out _))
            {
                _dataIndex.Add(item.Key, DataItems.Count);
                DataItems.Add(item);
            }

            return item.Key;
        }
    }
}