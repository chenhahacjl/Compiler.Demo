using System;
using System.Collections.Generic;

namespace Cocoa.Metadata
{
    /// <summary>我们自己的类型定义（TypeDef 表行）。顶层函数挂在 Program，class 各占一行。</summary>
    public sealed class IlTypeDef
    {
        public IlTypeDef(string name, string @namespace, IlTypeRef? baseTypeRef, bool isPublic = true, IlTypeDef? baseTypeDef = null)
        {
            Name = name;
            Namespace = @namespace ?? "";
            _baseTypeRef = baseTypeRef;
            _baseTypeDef = baseTypeDef;
            IsPublic = isPublic;
            Fields = new List<IlFieldDef>();
            Methods = new List<IlMethodDef>();
        }

        public string Name { get; }
        public string Namespace { get; }
        private IlTypeRef? _baseTypeRef;
        public IlTypeRef? BaseTypeRef => _baseTypeRef;

        /// <summary>本程序集内的基类 TypeDef（优先于 BaseTypeRef）。</summary>
        private IlTypeDef? _baseTypeDef;
        public IlTypeDef? BaseTypeDef => _baseTypeDef;

        /// <summary>延迟填充基类（6e-M20：泛型实例化类的字段可前向引用兄弟实例化类，壳先注册、基类后填）。</summary>
        public void SetBase(IlTypeRef? baseTypeRef, IlTypeDef? baseTypeDef)
        {
            _baseTypeRef = baseTypeRef;
            _baseTypeDef = baseTypeDef;
        }

        public bool IsPublic { get; }
        public bool IsAbstract { get; set; }
        public bool IsSealed { get; set; }
        public bool IsInterface { get; set; }
        public bool IsValueType { get; set; }
        public List<IlInterfaceImpl> Interfaces { get; } = new List<IlInterfaceImpl>();
        public List<IlFieldDef> Fields { get; }
        public List<IlPropertyDef> Properties { get; } = new List<IlPropertyDef>();
        public List<IlMethodDef> Methods { get; }

        /// <summary>MethodImpl 表行（显式接口实现：Class 内 接口成员 ↔ 本类方法）。</summary>
        public List<IlMethodImpl> MethodImpls { get; } = new List<IlMethodImpl>();
    }
}
