using Cocoa.CodeAnalysis.Symbols;
using System.Collections.Generic;

using Cocoa.CodeAnalysis.Emit.IL;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Emit;

namespace Cocoa.CodeGen.IL
{
    /// <summary>
    /// 委托形状缓存（6e-M22 C4-b）：FunctionTypeSymbol → (实例化 Func/Action Type, .ctor, Invoke)。
    /// 映射规则（对齐 BCL）：void 返回 → Action`N（0 参为无 backtick 的 Action）；否则 Func`(N+1)。
    /// 实例化类型以 TypeSpec 注册（MemberRef 父），.ctor(object, native int) / Invoke 为其实例方法。
    /// </summary>
    internal sealed class DelegateShapeCache
    {
        private readonly MetadataBuilder _metadata;
        private readonly IlFramework _framework;
        private readonly Dictionary<string, (IlType Type, IlMethodRef Ctor, IlMethodRef Invoke)> _cache = new();

        public DelegateShapeCache(MetadataBuilder metadata, IlFramework framework)
        {
            _metadata = metadata;
            _framework = framework;
        }

        public (IlType Type, IlMethodRef Ctor, IlMethodRef Invoke) Resolve(FunctionTypeSymbol functionType, System.Func<TypeSymbol, IlType> mapElementType)
        {
            var key = functionType.Name;

            if (_cache.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var isVoid = functionType.ReturnType == TypeSymbol.Void;
            var arity = functionType.ParameterTypes.Length + (isVoid ? 0 : 1);
            var definitionName = isVoid
                ? (functionType.ParameterTypes.Length == 0 ? "System.Action" : $"System.Action`{arity}")
                : $"System.Func`{arity}";

            var definition = _framework.RequireType(definitionName);

            var arguments = new List<IlType>();
            foreach (var parameter in functionType.ParameterTypes)
            {
                arguments.Add(mapElementType(parameter));
            }

            if (!isVoid)
            {
                arguments.Add(mapElementType(functionType.ReturnType));
            }

            var instantiated = IlType.GenericInstance(definition, arguments);

            // ctor: instance void .ctor(object, native int) —— 父 = TypeSpec(具体实例化)（csc 同构）
            var ctor = _metadata.DefineMethodRef(
                _metadata.DefineTypeSpec(instantiated),
                ".ctor",
                IlType.Void,
                new[] { IlType.Object, IlType.NativeInt },
                isStatic: false);

            var invokeParameterTypes = new List<IlType>();
            for (var i = 0; i < functionType.ParameterTypes.Length; i++)
            {
                // 开放签名（对齐 csc 字节级形态）：!0/!1… 泛型实参变量，父仍为 TypeSpec 实例化
                invokeParameterTypes.Add(IlType.GenericVar(i));
            }

            // invoke: instance !Ret Invoke(!A...)
            var invoke = _metadata.DefineMethodRef(
                _metadata.DefineTypeSpec(instantiated),
                "Invoke",
                isVoid ? IlType.Void : IlType.GenericVar(functionType.ParameterTypes.Length),
                invokeParameterTypes,
                isStatic: false);

            var shape = (instantiated, ctor, invoke);
            _cache[key] = shape;
            return shape;
        }
    }
}
