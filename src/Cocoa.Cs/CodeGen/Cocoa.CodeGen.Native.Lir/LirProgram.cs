using System.Collections.Generic;


namespace Cocoa.CodeGen.Native.Lir
{
    /// <summary>整个 IR 程序：函数表 + 数据段 + 运行时配置 + 入口函数名。</summary>
    public sealed class LirProgram
    {
        private readonly Dictionary<string, int> _dataIndex = new();

        public LirProgram(string entryFunctionName)
        {
            EntryFunctionName = entryFunctionName;
            Functions = new List<LirFunction>();
            Data = new Dictionary<string, LirDataItem>();
            DataItems = new List<LirDataItem>();
            Imports = new List<LirImport>();
            SpecialFunctions = new Dictionary<string, LirFunction>();
        }

        public string EntryFunctionName { get; }
        public List<LirFunction> Functions { get; }
        public Dictionary<string, LirDataItem> Data { get; }
        public List<LirDataItem> DataItems { get; }
        public List<LirImport> Imports { get; }
        public Dictionary<string, LirFunction> SpecialFunctions { get; }

        /// <summary>取或建字符串字面量数据项（去重，返回 key）。</summary>
        public string InternString(string text)
        {
            if (!_dataIndex.TryGetValue(text, out _))
            {
                var item = LirDataItem.Utf16(text, text);
                _dataIndex.Add(text, DataItems.Count);
                Data.Add(text, item);
                DataItems.Add(item);
            }

            return text;
        }

        /// <summary>追加数据项（运行时数据），返回其 key。</summary>
        public string AddData(LirDataItem item)
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