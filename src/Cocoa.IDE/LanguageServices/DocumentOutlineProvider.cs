using System.Collections.Immutable;
using System.Linq;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Text;

namespace Cocoa.IDE.LanguageServices;

/// <summary>文档大纲：把语法树顶层成员（函数/类/接口/枚举/命名空间）整理为可导航的符号树。
/// 类/接口/命名空间/枚举展开成员（函数/属性/字段/事件/枚举值），供大纲面板点击跳转。</summary>
public static class DocumentOutlineProvider
{
    public static DocumentOutlineItem[] Build(SyntaxTree tree)
    {
        if (tree?.Root is not CompilationUnitSyntax root) return System.Array.Empty<DocumentOutlineItem>();

        var items = new List<DocumentOutlineItem>();
        foreach (var member in root.Members)
        {
            var item = BuildMember(member);
            if (item != null) items.Add(item);
        }

        return items.ToArray();
    }

    private static DocumentOutlineItem? BuildMember(MemberSyntax member)
    {
        switch (member)
        {
            case FunctionDeclarationSyntax function:
                return Item(OutlineKind.Function, function.Identifier.Text ?? "", function.Identifier.Location);

            case ClassDeclarationSyntax cls:
            {
                var children = BuildMembers(cls.Members);
                return Item(cls.IsStruct ? OutlineKind.Struct : OutlineKind.Class, cls.Identifier.Text ?? "", cls.Identifier.Location, children);
            }

            case InterfaceDeclarationSyntax iface:
                return Item(OutlineKind.Interface, iface.Identifier.Text ?? "", iface.Identifier.Location, BuildMembers(iface.Members));

            case EnumDeclarationSyntax en:
            {
                var children = new List<DocumentOutlineItem>();
                foreach (var m in en.Members)
                    children.Add(Item(OutlineKind.EnumMember, m.Identifier.Text ?? "", m.Identifier.Location));
                return Item(OutlineKind.Enum, en.Identifier.Text ?? "", en.Identifier.Location, children);
            }

            case NamespaceDeclarationSyntax ns:
                return Item(OutlineKind.Namespace, string.Join(".", ns.NameTokens.Select(t => t.Text ?? "")), ns.NameTokens.Length > 0 ? ns.NameTokens[0].Location : ns.NamespaceKeyword.Location, BuildMembers(ns.Members));

            case DelegateDeclarationSyntax del:
                return Item(OutlineKind.Delegate, del.Identifier.Text ?? "", del.Identifier.Location);

            case PropertyDeclarationSyntax prop:
                return Item(OutlineKind.Property, prop.Identifier.Text ?? "", prop.Identifier.Location);

            case ClassFieldDeclarationSyntax field:
                return Item(OutlineKind.Field, field.Identifier.Text ?? "", field.Identifier.Location);

            case EventDeclarationSyntax evt:
                return Item(OutlineKind.Event, evt.Identifier.Text ?? "", evt.Identifier.Location);

            default:
                return null;
        }
    }

    private static List<DocumentOutlineItem>? BuildMembers(ImmutableArray<MemberSyntax> members)
    {
        if (members.Length == 0) return null;

        var items = new List<DocumentOutlineItem>();
        foreach (var member in members)
        {
            var item = BuildMember(member);
            if (item != null) items.Add(item);
        }

        return items.Count > 0 ? items : null;
    }

    private static DocumentOutlineItem Item(OutlineKind kind, string name, TextLocation location, List<DocumentOutlineItem>? children = null)
    {
        return new DocumentOutlineItem(kind, name, location.StartLine + 1, location.StartCharacter + 1, children);
    }
}

/// <summary>大纲条目种类。</summary>
public enum OutlineKind
{
    Namespace,
    Class,
    Struct,
    Interface,
    Enum,
    EnumMember,
    Delegate,
    Function,
    Property,
    Field,
    Event,
}

/// <summary>文档大纲条目：名称 + 声明位置（1 基行/列）+ 子项。</summary>
public sealed record DocumentOutlineItem(OutlineKind Kind, string Name, int Line, int Column, List<DocumentOutlineItem>? Children);
