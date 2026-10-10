using System;
using System.Collections.Generic;

namespace Cocoa.Metadata
{
    /// <summary>
    /// MethodImpl 表行（ECMA-335 II.22.29）：把接口成员的实现重定向到本类的显式实现方法。
    /// MethodDeclaration 二态：本程序集接口 TypeDef 的方法（<see cref="DeclarationDef"/>）或
    /// 外部/facade 接口成员（<see cref="DeclarationRef"/>，MemberRef）。
    /// </summary>
    public sealed class IlMethodImpl
    {
        public IlMethodImpl(IlMethodDef methodBody, IlMethodDef? declarationDef, IlMethodRef? declarationRef)
        {
            MethodBody = methodBody;
            DeclarationDef = declarationDef;
            DeclarationRef = declarationRef;
        }

        /// <summary>MethodBody（MethodDef）：本类显式实现方法。</summary>
        public IlMethodDef MethodBody { get; }

        /// <summary>MethodDeclaration（MethodDef）：本程序集接口 TypeDef 的成员。</summary>
        public IlMethodDef? DeclarationDef { get; }

        /// <summary>MethodDeclaration（MemberRef）：外部/facade 接口成员。</summary>
        public IlMethodRef? DeclarationRef { get; }
    }
}
