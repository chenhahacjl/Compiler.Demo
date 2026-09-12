using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Documentation
{
    /// <summary>
    /// 6e-M24：从语法 trivia 提取结构化文档注释的纯函数。
    /// 输入：语法节点的前导 trivia（ImmutableArray&lt;SyntaxTrivia&gt;）；
    /// 输出：<see cref="DocComment"/>（结构化标签 + 规范化原文）。
    ///
    /// 提取规则：
    ///   1. 仅处理连续的 <c>SingleLineDocCommentTrivia</c> 块（中间夹空行/空格行视为断开）。
    ///   2. 每行剥 <c>///</c> 前缀后，若紧跟一个空格则再剥该空格。
    ///   3. 支持标签：<c>&lt;summary&gt;</c>、<c>&lt;param name="..."&gt;</c>、<c>&lt;returns&gt;</c>、<c>&lt;remarks&gt;</c>。
    ///   4. 未知标签原样保留在 RawText 中，不产生结构化字段。
    ///   5. RawText 为规范化后的纯文本（多行合并为 \n 分隔）。
    /// </summary>
    public static class DocCommentExtractor
    {
        private static readonly Regex ParamTagRegex = new Regex(
            @"<param\s+name\s*=\s*""([^""]*)""\s*>",
            RegexOptions.Compiled);

        /// <summary>
        /// 从 leading trivia 提取文档注释。无 doc trivia 时返回 null。
        /// </summary>
        public static DocComment? Extract(ImmutableArray<SyntaxTrivia> leadingTrivia)
            => Extract(leadingTrivia, out _);

        /// <summary>
        /// 从 leading trivia 提取文档注释，并输出格式警告（标签未闭合 / &lt;param&gt; 缺 name）。
        /// 无 doc trivia 时返回 null。
        /// </summary>
        public static DocComment? Extract(ImmutableArray<SyntaxTrivia> leadingTrivia, out ImmutableArray<string> warnings)
        {
            var warningsBuilder = ImmutableArray.CreateBuilder<string>();

            if (leadingTrivia.IsDefaultOrEmpty)
            {
                warnings = ImmutableArray<string>.Empty;
                return null;
            }

            var lines = new List<string>();
            var inDocBlock = false;

            foreach (var trivia in leadingTrivia)
            {
                if (trivia.Kind == SyntaxKind.SingleLineDocCommentTrivia)
                {
                    inDocBlock = true;
                    var text = trivia.Text;
                    // 剥离 "///" 前缀
                    if (text.StartsWith("///", StringComparison.Ordinal))
                    {
                        text = text.Substring(3);
                    }

                    // 剥离首个空格（如果存在）
                    if (text.Length > 0 && text[0] == ' ')
                    {
                        text = text.Substring(1);
                    }

                    lines.Add(text);
                }
                else if (trivia.Kind == SyntaxKind.WhitespaceTrivia ||
                         trivia.Kind == SyntaxKind.LineBreakTrivia)
                {
                    // 文档行之间的空白/换行不构成断块（设计 §3.2）
                    continue;
                }
                else if (inDocBlock)
                {
                    // 遇到非 doc 非空白 trivia（普通注释等），doc 块结束
                    break;
                }
            }

            if (lines.Count == 0)
            {
                warnings = ImmutableArray<string>.Empty;
                return null;
            }

            var rawText = string.Join("\n", lines);
            var doc = ParseStructured(rawText, warningsBuilder);
            warnings = warningsBuilder.ToImmutable();
            return doc;
        }

        /// <summary>
        /// 解析规范化文档文本为结构化 DocComment。
        /// </summary>
        private static DocComment ParseStructured(string rawText, ImmutableArray<string>.Builder warnings)
        {
            string? summary = null;
            var parameters = ImmutableArray.CreateBuilder<ParamDoc>();
            string? returns = null;
            string? remarks = null;

            var currentTag = TagType.None;
            var currentParamName = (string?)null;
            var tagContent = new StringBuilder();

            var lines = rawText.Split('\n');

            foreach (var line in lines)
            {
                var trimmed = line.TrimStart();

                // 尝试检测新标签
                if (TryParseOpenTag(trimmed, out var tagType, out var paramName))
                {
                    // 保存前一个标签的内容
                    FlushTag(ref currentTag, ref currentParamName, tagContent, ref summary, ref returns, ref remarks, parameters);

                    currentTag = tagType;
                    currentParamName = paramName;
                    tagContent.Clear();

                    // 如果标签在同一行关闭（如 <summary>text</summary>）
                    var closeIndex = trimmed.IndexOf("</", StringComparison.Ordinal);
                    if (closeIndex > 0)
                    {
                        var openEnd = trimmed.IndexOf('>', StringComparison.Ordinal);
                        if (openEnd > 0 && openEnd < closeIndex)
                        {
                            tagContent.Append(trimmed.Substring(openEnd + 1, closeIndex - openEnd - 1));
                        }

                        FlushTag(ref currentTag, ref currentParamName, tagContent, ref summary, ref returns, ref remarks, parameters);
                    }

                    continue;
                }

                // <param> 缺少 name 属性（未匹配参数名正则）
                if (trimmed.StartsWith("<param", StringComparison.Ordinal))
                {
                    warnings.Add("<param> 缺少 name 属性。");
                }

                // 检测闭合标签
                if (currentTag != TagType.None && trimmed.StartsWith("</", StringComparison.Ordinal))
                {
                    FlushTag(ref currentTag, ref currentParamName, tagContent, ref summary, ref returns, ref remarks, parameters);
                    continue;
                }

                // 追加到当前标签内容
                if (currentTag != TagType.None)
                {
                    if (tagContent.Length > 0)
                    {
                        tagContent.Append('\n');
                    }

                    tagContent.Append(line);
                }
                else
                {
                    // 无标签行 → 归入 summary（若 summary 为空）
                    if (summary == null && trimmed.Length > 0)
                    {
                        summary = trimmed;
                    }
                }
            }

            // 刷新最后一个标签；若仍有未闭合的已识别标签，报告警告
            if (currentTag != TagType.None)
            {
                warnings.Add($"标签 <{currentTag.ToString().ToLowerInvariant()}> 未闭合。");
            }

            FlushTag(ref currentTag, ref currentParamName, tagContent, ref summary, ref returns, ref remarks, parameters);

            return new DocComment(
                summary?.Trim(),
                parameters.ToImmutable(),
                returns?.Trim(),
                remarks?.Trim(),
                rawText);
        }

        private enum TagType
        {
            None,
            Summary,
            Param,
            Returns,
            Remarks,
        }

        private static bool TryParseOpenTag(string line, out TagType tagType, out string? paramName)
        {
            tagType = TagType.None;
            paramName = null;

            if (!line.StartsWith("<", StringComparison.Ordinal))
            {
                return false;
            }

            if (line.StartsWith("<summary", StringComparison.Ordinal))
            {
                tagType = TagType.Summary;
                return true;
            }

            if (line.StartsWith("<param ", StringComparison.Ordinal))
            {
                var match = ParamTagRegex.Match(line);
                if (match.Success)
                {
                    tagType = TagType.Param;
                    paramName = match.Groups[1].Value;
                    return true;
                }
            }

            if (line.StartsWith("<returns", StringComparison.Ordinal))
            {
                tagType = TagType.Returns;
                return true;
            }

            if (line.StartsWith("<remarks", StringComparison.Ordinal))
            {
                tagType = TagType.Remarks;
                return true;
            }

            return false;
        }

        private static void FlushTag(
            ref TagType currentTag, ref string? currentParamName, StringBuilder tagContent,
            ref string? summary, ref string? returns, ref string? remarks, ImmutableArray<ParamDoc>.Builder parameters)
        {
            if (currentTag == TagType.None)
            {
                return;
            }

            var content = tagContent.ToString().Trim();

            switch (currentTag)
            {
                case TagType.Summary:
                    summary = content;
                    break;
                case TagType.Param:
                    if (currentParamName != null)
                    {
                        parameters.Add(new ParamDoc(currentParamName, content));
                    }

                    break;
                case TagType.Returns:
                    returns = content;
                    break;
                case TagType.Remarks:
                    remarks = content;
                    break;
            }

            currentTag = TagType.None;
            currentParamName = null;
            tagContent.Clear();
        }
    }
}
