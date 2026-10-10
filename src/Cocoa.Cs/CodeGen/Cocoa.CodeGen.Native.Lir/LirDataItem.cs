namespace Cocoa.CodeGen.Native.Lir
{
    /// <summary>数据段项（运行时数据与字符串字面量统一建模）。</summary>
    public sealed class LirDataItem
    {
        public LirDataItem(string key, LirDataKind kind, int intValue, string? text, byte[]? bytes)
        {
            Key = key;
            Kind = kind;
            IntValue = intValue;
            Text = text;
            Bytes = bytes;
        }

        public string Key { get; }
        public LirDataKind Kind { get; }
        public int IntValue { get; }
        public string? Text { get; }
        public byte[]? Bytes { get; }

        /// <summary>vtable 记录：类型 id（M4；伪记录 -1）。</summary>
        public int TypeId { get; }

        /// <summary>vtable 记录：类型全名字符串的数据 key（名字指针槽重定位目标）。</summary>
        public string? NameKey { get; }

        /// <summary>vtable 记录：函数名槽数组（用户函数 mangle 名或运行时函数名）。</summary>
        public System.Collections.Generic.IReadOnlyList<string>? Slots { get; }

        public LirDataItem(string key, LirDataKind kind, int intValue, string? text, byte[]? bytes,
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

        public static LirDataItem Int32(string key, int value) => new LirDataItem(key, LirDataKind.Int32, value, null, null);
        public static LirDataItem Pointer(string key) => new LirDataItem(key, LirDataKind.Pointer, 0, null, null);
        public static LirDataItem Utf16(string key, string text) => new LirDataItem(key, LirDataKind.Utf16, 0, text, null);
        public static LirDataItem ByteArray(string key, byte[] bytes) => new LirDataItem(key, LirDataKind.Bytes, 0, null, bytes);

        /// <summary>
        /// vtable 记录（M4，即 System.Type 对象）：[0] typeId:int [4] pad [8] 名字指针（数据重定位）
        /// [8+ps·(i+1)] 槽 i 函数绝对地址（代码重定位）。typeId &lt; 0 = 基元/Type 伪记录
        /// （[0] 为自引用指针，使 ObjectToString 等对 Type 值同样成立）。
        /// </summary>
        public static LirDataItem VTable(string key, int typeId, string nameKey, System.Collections.Generic.IReadOnlyList<string> slots)
            => new LirDataItem(key, LirDataKind.VTable, 0, null, null, typeId, nameKey, slots);
    }
}