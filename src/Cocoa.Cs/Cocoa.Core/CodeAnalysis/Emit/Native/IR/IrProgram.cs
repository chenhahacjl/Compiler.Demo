using System.Collections.Generic;

namespace Cocoa.CodeAnalysis.Emit.Native.IR
{
    /// <summary>数据段项语义：Int32 / 指针（平台宽 4/8）/ UTF-16 字符串 / 原始字节 / vtable 记录（M4）。</summary>
    internal enum IrDataKind
    {
        Int32,
        Pointer,
        Utf16,
        Bytes,
        VTable,
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

        /// <summary>vtable 记录：类型 id（M4；伪记录 -1）。</summary>
        public int TypeId { get; }

        /// <summary>vtable 记录：类型全名字符串的数据 key（名字指针槽重定位目标）。</summary>
        public string? NameKey { get; }

        /// <summary>vtable 记录：函数名槽数组（用户函数 mangle 名或运行时函数名）。</summary>
        public System.Collections.Generic.IReadOnlyList<string>? Slots { get; }

        public IrDataItem(string key, IrDataKind kind, int intValue, string? text, byte[]? bytes,
            int typeId = -1, string? nameKey = null, System.Collections.Generic.IReadOnlyList<string>? slots = null)
        {
            Key = key;
            Kind = kind;
            IntValue = intValue;
            Text = text;
            Bytes = bytes;
            TypeId = typeId;
            NameKey = nameKey;
            Slots = slots;
        }

        public static IrDataItem Int32(string key, int value) => new IrDataItem(key, IrDataKind.Int32, value, null, null);
        public static IrDataItem Pointer(string key) => new IrDataItem(key, IrDataKind.Pointer, 0, null, null);
        public static IrDataItem Utf16(string key, string text) => new IrDataItem(key, IrDataKind.Utf16, 0, text, null);
        public static IrDataItem ByteArray(string key, byte[] bytes) => new IrDataItem(key, IrDataKind.Bytes, 0, null, bytes);

        /// <summary>
        /// vtable 记录（M4，即 System.Type 对象）：[0] typeId:int [4] pad [8] 名字指针（数据重定位）
        /// [8+ps·(i+1)] 槽 i 函数绝对地址（代码重定位）。typeId &lt; 0 = 基元/Type 伪记录
        /// （[0] 为自引用指针，使 ObjectToString 等对 Type 值同样成立）。
        /// </summary>
        public static IrDataItem VTable(string key, int typeId, string nameKey, System.Collections.Generic.IReadOnlyList<string> slots)
            => new IrDataItem(key, IrDataKind.VTable, 0, null, null, typeId, nameKey, slots);
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

    /// <summary>导入规格：DLL 名 + 函数名（DLL 导出名，已含 entry 别名）+ x86 调用约定（cdecl 调用方清理）。x64 约定统一，Cdecl 忽略。</summary>
    internal readonly record struct IrImport(string DllName, string Name, bool Cdecl)
    {
        public override string ToString() => DllName + "!" + Name;
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
            Imports = new List<IrImport>();
            SpecialFunctions = new Dictionary<string, IrFunction>();
        }

        public string EntryFunctionName { get; }
        public List<IrFunction> Functions { get; }
        public Dictionary<string, IrDataItem> Data { get; }
        public List<IrDataItem> DataItems { get; }
        public List<IrImport> Imports { get; }
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