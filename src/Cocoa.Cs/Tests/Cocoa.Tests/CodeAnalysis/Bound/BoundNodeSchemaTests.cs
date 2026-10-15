using System;
using System.Linq;
using Cocoa.CodeAnalysis.Bound;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Bound
{
    /// <summary>
    /// Bound 节点 Schema 一致性（4.1-P4/2.3）：新增 <see cref="BoundNodeKind"/> 未在
    /// <see cref="BoundNodeSchema"/> 登记 → 构建期失败（测试报"枚举值未声明覆盖"）。
    /// </summary>
    public class BoundNodeSchemaTests
    {
        [Fact]
        public void EveryBoundNodeKindHasASchemaAccessor()
        {
            var declared = BoundNodeSchema.DeclaredKinds.ToHashSet();
            var undeclared = Enum.GetValues<BoundNodeKind>()
                .Where(k => !declared.Contains(k))
                .Select(k => k.ToString())
                .ToArray();

            Assert.True(undeclared.Length == 0, $"BoundNode schema 未声明覆盖: {string.Join(", ", undeclared)}");
        }

        [Fact]
        public void SchemaCoversAllDeclaredKinds()
        {
            var declared = BoundNodeSchema.DeclaredKinds.ToArray();
            var all = Enum.GetValues<BoundNodeKind>().ToHashSet();
            var phantom = declared.Where(k => !all.Contains(k)).Select(k => k.ToString()).ToArray();

            Assert.True(phantom.Length == 0, $"BoundNode schema 含不存在的枚举值: {string.Join(", ", phantom)}");
        }

        [Fact]
        public void DeclaredKinds_AreUnique()
        {
            var declared = BoundNodeSchema.DeclaredKinds.ToArray();
            var duplicates = declared.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key.ToString()).ToArray();

            Assert.True(duplicates.Length == 0, $"BoundNode schema 重复登记: {string.Join(", ", duplicates)}");
        }
    }
}