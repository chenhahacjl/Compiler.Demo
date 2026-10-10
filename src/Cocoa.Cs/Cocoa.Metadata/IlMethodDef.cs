using System;
using System.Collections.Generic;

namespace Cocoa.Metadata
{
    /// <summary>我们自己的方法定义（MethodDef 表行 + 方法体）。</summary>
    public sealed class IlMethodDef
    {
        public IlMethodDef(string name, IlType returnType, IReadOnlyList<IlType> parameterTypes, IlMethodBody? body, string? dllName = null, string? importName = null, IlCallingConvention callingConvention = IlCallingConvention.Winapi, bool isStatic = true, IlCharSet charSet = IlCharSet.Unicode)
        {
            Name = name;
            ReturnType = returnType;
            ParameterTypes = parameterTypes;
            Body = body;
            DllName = dllName;
            ImportName = importName;
            CallingConvention = callingConvention;
            IsStatic = isStatic;
            CharSet = charSet;
        }

        public string Name { get; }
        public IlType ReturnType { get; }
        public IReadOnlyList<IlType> ParameterTypes { get; }
        public IlMethodBody? Body { get; }

        /// <summary>P/Invoke 目标 DLL（null = 普通方法，不产生 ImplMap 行）。</summary>
        public string? DllName { get; }
        /// <summary>入口点名称（null = 与方法同名）。</summary>
        public string? ImportName { get; }
        public IlCallingConvention CallingConvention { get; }
        /// <summary>实例方法（含 this，签名 HAS_THIS）。</summary>
        public bool IsStatic { get; set; }

        /// <summary>值类型实例方法：this 以 EXPLICITTHIS 显式给出（首位 byref 参数）。</summary>
        public bool IsExplicitThis { get; set; }

        /// <summary>P/Invoke 编码格式（ImplMap CharSet 位）。6e-M17 Step 5。</summary>
        public IlCharSet CharSet { get; }

        public IlVisibility Visibility { get; set; } = IlVisibility.Public;

        public bool IsVirtual { get; set; }

        public bool IsAbstract { get; set; }

        public bool IsSealed { get; set; }

        /// <summary>NewSlot（0x0001 <see cref="MethodAttributes.NewSlot"/>）：委托 Invoke 等 newslot virtual 方法。</summary>
        public bool IsNewSlot { get; set; }

        /// <summary>运行时实现（委托 Invoke 等 CLR 特判方法）：ImplFlags=Runtime(0x0003)，RVA=0。</summary>
        public bool IsRuntimeImplementation { get; set; }
    }
}
