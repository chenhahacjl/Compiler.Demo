using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using Cocoa.CodeAnalysis.Symbols;

namespace Cocoa.CodeAnalysis.Documentation
{
    /// <summary>
    /// 6e-M24：XML 文档文件写入器。
    /// 将符号表中带 <c>DocumentationText</c> 的符号输出为 .NET 风格的 XML documentation 文件。
    ///
    /// 输出格式（UTF-8 无 BOM）：
    /// <code>
    /// &lt;?xml version="1.0" encoding="utf-8"?&gt;
    /// &lt;doc&gt;
    ///   &lt;assembly&gt;
    ///     &lt;name&gt;MyLib&lt;/name&gt;
    ///   &lt;/assembly&gt;
    ///   &lt;members&gt;
    ///     &lt;member name="M:MyLib.Add(i32,i32)"&gt;
    ///       &lt;summary&gt;Adds two numbers.&lt;/summary&gt;
    ///       &lt;param name="a"&gt;First number&lt;/param&gt;
    ///       &lt;param name="b"&gt;Second number&lt;/param&gt;
    ///       &lt;returns&gt;The sum.&lt;/returns&gt;
    ///     &lt;/member&gt;
    ///   &lt;/members&gt;
    /// &lt;/doc&gt;
    /// </code>
    ///
    /// 仅输出带文档的符号；无文档时不写空文件。
    /// </summary>
    public static class DocumentationFileWriter
    {
        /// <summary>
        /// 将符号列表写入 XML documentation 文件。
        /// </summary>
        /// <param name="outputPath">输出文件路径。</param>
        /// <param name="assemblyName">程序集名。</param>
        /// <param name="symbols">需要文档化的符号列表。</param>
        /// <returns>实际写入的成员数。</returns>
        public static int Write(string outputPath, string assemblyName, IEnumerable<Symbol> symbols)
        {
            // 去重：同一 DocID 只输出一次（枚举来源可能重叠，如静态类方法同时出现在顶层函数集与类成员集）
            var byDocId = new Dictionary<string, Symbol>(StringComparer.Ordinal);
            foreach (var symbol in symbols)
            {
                if (string.IsNullOrEmpty(symbol.DocumentationText))
                {
                    continue;
                }

                var docId = DocIdBuilder.GetDocId(symbol);
                if (docId != null && !byDocId.ContainsKey(docId))
                {
                    byDocId[docId] = symbol;
                }
            }

            if (byDocId.Count == 0)
            {
                return 0;
            }

            var ordered = byDocId.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

            var settings = new XmlWriterSettings
            {
                Indent = true,
                IndentChars = "  ",
                Encoding = new UTF8Encoding(false),
                OmitXmlDeclaration = false,
            };

            using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var writer = XmlWriter.Create(stream, settings);

            writer.WriteStartDocument();
            writer.WriteStartElement("doc");

            // <assembly>
            writer.WriteStartElement("assembly");
            writer.WriteElementString("name", assemblyName);
            writer.WriteEndElement();

            // <members>
            writer.WriteStartElement("members");

            foreach (var pair in ordered)
            {
                writer.WriteStartElement("member");
                writer.WriteAttributeString("name", pair.Key);

                // 写入文档内容（已格式化的 XML 片段）
                WriteDocContent(writer, pair.Value.DocumentationText!);

                writer.WriteEndElement(); // </member>
            }

            writer.WriteEndElement(); // </members>
            writer.WriteEndElement(); // </doc>
            writer.WriteEndDocument();

            return ordered.Count;
        }

        private static void WriteDocContent(XmlWriter writer, string docText)
        {
            // 将规范化文档文本解析为 XML 片段：识别 <summary>/<param>/<returns>/<remarks>，
            // 保留开标签属性（尤其 <param name="...">）；标签外文本（含未知标签）按纯文本保留。
            var lines = docText.Split('\n');
            var tagName = (string?)null;
            var opener = (string?)null;
            var content = new StringBuilder();

            foreach (var line in lines)
            {
                var trimmed = line.TrimStart();

                var open = TryParseKnownOpenTag(trimmed);
                if (open != null)
                {
                    FlushTag(writer, tagName, opener, content);
                    tagName = open.Value.Name;
                    opener = open.Value.Opener;

                    // 同一行内闭合（<summary>text</summary>）
                    var closeIdx = trimmed.IndexOf("</", System.StringComparison.Ordinal);
                    if (closeIdx > 0)
                    {
                        var openEnd = trimmed.IndexOf('>');
                        if (openEnd > 0 && openEnd < closeIdx)
                        {
                            content.Append(trimmed.Substring(openEnd + 1, closeIdx - openEnd - 1));
                        }

                        FlushTag(writer, tagName, opener, content);
                        tagName = null;
                        opener = null;
                    }

                    continue;
                }

                // 闭合标签
                if (tagName != null && trimmed.StartsWith("</", System.StringComparison.Ordinal))
                {
                    FlushTag(writer, tagName, opener, content);
                    tagName = null;
                    opener = null;
                    continue;
                }

                // 标签内容行
                if (tagName != null)
                {
                    if (content.Length > 0)
                    {
                        content.Append(' ');
                    }

                    content.Append(trimmed);
                }
                else if (trimmed.Length > 0)
                {
                    // 标签外的散落文本（含未知标签）——宽容保留为纯文本
                    writer.WriteString(trimmed);
                }
            }

            FlushTag(writer, tagName, opener, content);
        }

        private static (string Name, string Opener)? TryParseKnownOpenTag(string line)
        {
            if (!line.StartsWith("<", System.StringComparison.Ordinal))
            {
                return null;
            }

            string name;
            if (line.StartsWith("<summary", System.StringComparison.Ordinal))
            {
                name = "summary";
            }
            else if (line.StartsWith("<param ", System.StringComparison.Ordinal) ||
                     line.StartsWith("<param>", System.StringComparison.Ordinal))
            {
                name = "param";
            }
            else if (line.StartsWith("<returns", System.StringComparison.Ordinal))
            {
                name = "returns";
            }
            else if (line.StartsWith("<remarks", System.StringComparison.Ordinal))
            {
                name = "remarks";
            }
            else
            {
                return null;
            }

            var end = line.IndexOf('>');
            if (end < 0)
            {
                return null;
            }

            return (name, line.Substring(0, end + 1));
        }

        private static void FlushTag(XmlWriter writer, string? tagName, string? opener, StringBuilder content)
        {
            if (tagName == null)
            {
                content.Clear();
                return;
            }

            var text = content.ToString().Trim();

            if (tagName == "param")
            {
                var paramName = ExtractAttribute(opener, "name") ?? "";
                writer.WriteStartElement("param");
                writer.WriteAttributeString("name", paramName);
                writer.WriteString(text);
                writer.WriteEndElement();
            }
            else
            {
                writer.WriteStartElement(tagName);
                writer.WriteString(text);
                writer.WriteEndElement();
            }

            content.Clear();
        }

        private static string? ExtractAttribute(string? opener, string attribute)
        {
            if (opener == null)
            {
                return null;
            }

            var idx = opener.IndexOf(attribute + "=", System.StringComparison.Ordinal);
            if (idx < 0)
            {
                return null;
            }

            var quoteStart = opener.IndexOf('"', idx);
            if (quoteStart < 0)
            {
                return null;
            }

            var quoteEnd = opener.IndexOf('"', quoteStart + 1);
            if (quoteEnd <= quoteStart)
            {
                return null;
            }

            return opener.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);
        }
    }
}
