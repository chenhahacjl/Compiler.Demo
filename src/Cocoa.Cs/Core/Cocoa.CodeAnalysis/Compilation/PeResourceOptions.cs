using System;
using System.Collections.Generic;
using System.IO;

namespace Cocoa.CodeAnalysis
{
    /// <summary>PE 资源条目（Core 侧载体；PE 后端转换为 <c>Cocoa.CodeGen.ObjectFormat.PE.PeResourceData</c>）。</summary>
    public readonly record struct PeResourceItem(uint Type, uint NameId, ushort LanguageId, byte[] Data);

    /// <summary>IL 内嵌资源（ManifestResource：名称 + 数据，运行时 GetManifestResourceStream 读取）。</summary>
    public sealed class PeEmbeddedResource
    {
        public PeEmbeddedResource(string name, byte[] data)
        {
            Name = name;
            Data = data;
        }

        public string Name { get; }
        public byte[] Data { get; }
    }

    /// <summary>
    /// PE 产物资源选项（双后端共用）：版本信息（VS_VERSIONINFO / Assembly 属性）+ 应用图标（.ico 路径，
    /// 解析为 RT_GROUP_ICON/RT_ICON）+ 通用资源条目（RT_RCDATA 等）+ IL 内嵌资源（ManifestResource）。
    /// 由 .coproj 构建管线构造。
    /// </summary>
    public sealed class PeResourceOptions
    {
        public PeResourceOptions(PeVersionInfo? versionInfo, string? iconPath = null, IReadOnlyList<PeResourceItem>? extraResources = null, IReadOnlyList<PeEmbeddedResource>? embeddedResources = null)
        {
            VersionInfo = versionInfo;
            IconPath = iconPath;
            ExtraResources = extraResources ?? Array.Empty<PeResourceItem>();
            EmbeddedResources = embeddedResources ?? Array.Empty<PeEmbeddedResource>();
        }

        public PeVersionInfo? VersionInfo { get; }
        public string? IconPath { get; }
        public IReadOnlyList<PeResourceItem> ExtraResources { get; }
        public IReadOnlyList<PeEmbeddedResource> EmbeddedResources { get; }

        public bool IsEmpty =>
            VersionInfo == null && string.IsNullOrWhiteSpace(IconPath) && ExtraResources.Count == 0 && EmbeddedResources.Count == 0;
    }
}