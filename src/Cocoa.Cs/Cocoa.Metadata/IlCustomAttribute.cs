using System.Collections.Generic;

namespace Cocoa.Metadata
{
    /// <summary>自定义特性（CustomAttribute 表行）。</summary>
    public sealed class IlCustomAttribute
    {
        public IlCustomAttribute(IlMethodRef constructor, byte[] fixedArguments, int parentRow = 0, int parentTag = 0, int ctorRow = 0, int ctorTag = 0)
        {
            Constructor = constructor;
            FixedArguments = fixedArguments;
            ParentRow = parentRow;
            ParentTag = parentTag;
            CtorRow = ctorRow;
            CtorTag = ctorTag;
        }

        public IlMethodRef Constructor { get; }
        public byte[] FixedArguments { get; }

        /// <summary>HasCustomAttribute 父行号（行 1 起；Assembly=1）。</summary>
        public int ParentRow { get; }

        /// <summary>HasCustomAttribute 父 tag（MethodDef=0 / TypeDef=2 / Field=4 / Property=9 / Assembly=14）。</summary>
        public int ParentTag { get; }

        /// <summary>CustomAttributeType 行号（ctor：MethodDef 行 / MemberRef 行）。</summary>
        public int CtorRow { get; }

        /// <summary>CustomAttributeType tag（MethodDef=2 / MemberRef=3）。</summary>
        public int CtorTag { get; }
    }
}
