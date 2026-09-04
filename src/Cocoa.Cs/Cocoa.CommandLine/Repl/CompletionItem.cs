namespace Cocoa.Compiler
{
    internal sealed class CompletionItem
    {
        public CompletionItem(string text)
        {
            Text = text;
        }

        /// <summary>插入到输入文本中的标识符名。</summary>
        public string Text { get; }

        /// <summary>弹窗右侧显示的描述（签名/类型/说明）。</summary>
        public string? Detail { get; init; }

        /// <summary>展开模板（可含换行；'$' 为光标落点标记）。非 null 时 Tab 展开而非纯插入。</summary>
        public string? Snippet { get; init; }

        /// <summary>插入 Text 后紧跟的附加文本（如函数的 "()"），光标停在 Text 之后。</summary>
        public string? InsertSuffix { get; init; }
    }
}