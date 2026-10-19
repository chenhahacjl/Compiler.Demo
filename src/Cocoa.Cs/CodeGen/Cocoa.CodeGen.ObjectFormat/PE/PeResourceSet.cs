using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cocoa.CodeAnalysis;

namespace Cocoa.CodeGen.ObjectFormat.PE
{
    /// <summary>PE 产物资源集：由 Core 侧 <see cref="PeResourceOptions"/> 转换为 .rsrc 构建输入。</summary>
    public static class PeResourceSet
    {
        /// <summary>合并为单个 .rsrc 节构建结果（版本 + 图标 + 通用资源）。null = 无资源。</summary>
        public static ResourceSectionResult? BuildRsrcSection(PeResourceOptions? options)
        {
            if (options == null || options.IsEmpty)
            {
                return null;
            }

            var items = new List<PeResourceData>(
                options.ExtraResources.Select(r => new PeResourceData(r.Type, r.NameId, r.LanguageId, r.Data)));

            if (options.VersionInfo != null)
            {
                var versionBlob = VersionResourceBuilder.BuildVersionInfoBlob(options.VersionInfo);
                if (versionBlob != null)
                {
                    items.Add(new PeResourceData(ResourceSectionBuilder.RtVersion, 1, ResourceSectionBuilder.DefaultLanguageId, versionBlob));
                }
            }

            if (options.IconData != null && options.IconData.Length > 0)
            {
                // 内嵌字节图标（默认图标兜底）优先
                var icons = IconResourceBuilder.Build(options.IconData);
                if (icons != null)
                {
                    items.AddRange(icons);
                }
            }
            else if (!string.IsNullOrEmpty(options.IconPath) && File.Exists(options.IconPath))
            {
                var icons = IconResourceBuilder.Build(File.ReadAllBytes(options.IconPath));
                if (icons != null)
                {
                    items.AddRange(icons);
                }
            }

            if (items.Count == 0)
            {
                return null;
            }

            return ResourceSectionBuilder.Build(items);
        }

        /// <summary>仅图标 + 通用资源（不含版本 RT_VERSION；IL 产物版本走 Assembly 属性）。null = 无。</summary>
        public static ResourceSectionResult? BuildWin32Only(PeResourceOptions? options)
        {
            if (options == null || (string.IsNullOrEmpty(options.IconPath) && (options.IconData == null || options.IconData.Length == 0) && options.ExtraResources.Count == 0))
            {
                return null;
            }

            var items = new List<PeResourceData>(
                options.ExtraResources.Select(r => new PeResourceData(r.Type, r.NameId, r.LanguageId, r.Data)));

            if (options.IconData != null && options.IconData.Length > 0)
            {
                var icons = IconResourceBuilder.Build(options.IconData);
                if (icons != null)
                {
                    items.AddRange(icons);
                }
            }
            else if (!string.IsNullOrEmpty(options.IconPath) && File.Exists(options.IconPath))
            {
                var icons = IconResourceBuilder.Build(File.ReadAllBytes(options.IconPath));
                if (icons != null)
                {
                    items.AddRange(icons);
                }
            }

            if (items.Count == 0)
            {
                return null;
            }

            return ResourceSectionBuilder.Build(items);
        }
    }
}