using System.Collections.Generic;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Documentation
{
    /// <summary>
    /// 6e-M24：`///` 文档注释提取后的结构化表示。
    /// 由 <see cref="DocCommentExtractor.Extract"/> 从语法节点前导 trivia 生成；
    /// 符号挂点仅存 <c>DocumentationText</c>（规范化原文），结构化视图用于 XML 输出与 REPL 查询。
    /// </summary>
    public sealed class DocComment
    {
        /// <summary><c>&lt;summary&gt;</c> 内容（多行合并为单行，首尾空白裁剪）。</summary>
        public string? Summary { get; }

        /// <summary><c>&lt;param name="..."&gt;</c> 标签集合（按出现序）。</summary>
        public ImmutableArray<ParamDoc> Parameters { get; }

        /// <summary><c>&lt;returns&gt;</c> 内容。</summary>
        public string? Returns { get; }

        /// <summary><c>&lt;remarks&gt;</c> 内容。</summary>
        public string? Remarks { get; }

        /// <summary>规范化后的原始文档文本（多行合并，供符号挂点存储）。</summary>
        public string RawText { get; }

        public DocComment(string? summary, ImmutableArray<ParamDoc> parameters, string? returns, string? remarks, string rawText)
        {
            Summary = summary;
            Parameters = parameters.IsDefault ? ImmutableArray<ParamDoc>.Empty : parameters;
            Returns = returns;
            Remarks = remarks;
            RawText = rawText;
        }
    }

    /// <summary>
    /// 6e-M24：文档注释中 <c>&lt;param name="..."&gt;</c> 标签的结构化表示。
    /// </summary>
    public sealed class ParamDoc
    {
        /// <summary>参数名（与声明形参名匹配）。</summary>
        public string Name { get; }

        /// <summary>参数描述文本。</summary>
        public string Description { get; }

        public ParamDoc(string name, string description)
        {
            Name = name;
            Description = description;
        }
    }
}
