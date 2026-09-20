using System.Collections.Generic;
using System.IO;

namespace Cocoa.CodeAnalysis.Serialization.CoaFormat
{
    /// <summary>.coa 文本写入器：`(kind field...)` 结构括号 + 缩进排版（源 <c>CoaSerializer.Writer</c> 提升）。</summary>
    internal sealed class CoaTextWriter
    {
        private readonly TextWriter _w;
        private readonly List<bool> _hasChild = new();
        private int _depth;
        private bool _lineStart = true;

        public CoaTextWriter(TextWriter writer)
        {
            _w = writer;
        }

        public void Open(string kind)
        {
            if (_hasChild.Count > 0)
            {
                // 标记父节点含子节点：其闭括号换行缩进，而非行内闭合
                _hasChild[_hasChild.Count - 1] = true;
            }

            Indent();
            _w.Write('(');
            _w.Write(kind);
            _lineStart = false;
            _hasChild.Add(false);
            _depth++;
        }

        public void Field(object value)
        {
            _w.Write(' ');
            _w.Write(value);
            _lineStart = false;
        }

        public void End()
        {
            var hasChild = _hasChild[_hasChild.Count - 1];
            _hasChild.RemoveAt(_hasChild.Count - 1);
            _depth--;

            if (hasChild && !_lineStart)
            {
                // 多行节点：先回到行首，闭括号与开括号同列
                _w.WriteLine();
                _w.Write(new string(' ', _depth * 2));
            }

            // 行内闭合（无子节点）或定位后闭合均不主动换行——由下一个 Open/Field/End 按需定位。
            _w.Write(')');
            _lineStart = false;
        }

        /// <summary>子节点开括号前定位到下一行缩进列（已在行首则不再换行）。</summary>
        private void Indent()
        {
            if (_depth == 0)
            {
                return;
            }

            if (!_lineStart)
            {
                _w.WriteLine();
            }

            _w.Write(new string(' ', _depth * 2));
            _lineStart = true;
        }
    }
}
