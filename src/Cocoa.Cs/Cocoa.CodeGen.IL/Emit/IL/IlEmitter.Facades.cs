using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Emit.IL;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Emit;

namespace Cocoa.CodeGen.IL
{
    /// <summary>
    /// IL 路径发射器：绑定树 → 自研 IL 组件（IlAssembler/MetadataBuilder/ManagedPEWriter）。
    /// 发射语义与原 Mono.Cecil 实现一致（表达式/语句 → IL 指令序列）。
    /// </summary>
    internal sealed partial class IlEmitter
    {
        private void EmitCallExpression(IlAssembler il, BoundCallExpression node)
        {
            if (node.Function.BuiltinKind != null)
            {
                EmitBuiltinCall(il, node.Function, node.Arguments);
                return;
            }

            // facade 类成员：优先重定向到 BCL（解析失败回退下方 codAssemblies 的 Cocoa 体）
            if (TryEmitFacadeBclCall(il, node))
            {
                return;
            }

            // 动态链接（阶段 A3）：cod 顶层函数 → <CocoaTopLevel>.MemberRef 外部调用
            if (_codAssemblies.TryGetValue(node.Function, out var codAssembly))
            {
                foreach (var argument in node.Arguments)
                {
                    EmitExpression(il, argument);
                }

                il.Emit(IlOpCodeTable.Get("Call"), CodMethodRef(node.Function, codAssembly));
                return;
            }

            var isStructInstance = node.Function.ContainingClass is { IsValueType: true } && !node.Function.IsStatic
                && node.Arguments.Length > 0 && node.Function.Parameters.Length > 0;
            foreach (var argument in node.Arguments)
            {
                if (isStructInstance && argument == node.Arguments[0])
                {
                    // struct 实例方法：this 按托管指针传参（ldarga/ldloca 或临时局部取址）
                    var receiverLocal = AllocateTemporaryLocal(argument, argument.Type);
                    EmitExpression(il, argument);
                    il.Emit(IlOpCodeTable.Get("Stloc"), (ushort)receiverLocal);
                    il.Emit(IlOpCodeTable.Get("Ldloca"), (ushort)receiverLocal);
                }
                else
                {
                    EmitExpression(il, argument);
                }
            }

            var methodDefinition = _methods[node.Function];
            il.Emit(IlOpCodeTable.Get("Call"), methodDefinition);
        }

        /// <summary>
        /// facade 类成员在 IL 端重定向到 BCL：按 ContainingClass.FullName + 方法名 + 实参类型
        /// 调 _framework.FindMethod → callvirt/call 到 BCL；解析失败返回 false（调用方回退到
        /// codAssemblies 的 Cocoa 体）。泛型 facade 的重定向（直构 MemberRef）见后续实现。
        /// 规则见 docs-dev/对象模型设计.md §5.4。
        /// </summary>
        private bool IsFacadeRedirect(NamedTypeSymbol classType)
        {
            if (classType.IsFacadeClass) return true;
            if (classType is InstantiatedTypeSymbol inst && inst.GenericDefinition?.IsFacadeClass == true) return true;
            return false;
        }

        /// <summary>facade 类型运行期映射到的 BCL 全名：优先用 FacadeThisType（struct facade 由此提供 BCL 值类型名；
        /// class facade 的 FacadeThisType 即 BCL 目标，与自身 FullName 一致，故回退到 FullName 等价）。</summary>
        private string FacadeBclFullName(NamedTypeSymbol classType)
            => classType.FacadeThisType is NamedTypeSymbol nts && !nts.IsPrimitiveValueType && nts != TypeSymbol.String
                ? nts.FullName
                : classType.FullName;

        private static bool IsValueTypeSymbol(TypeSymbol type)
            => type.IsValueType;

        /// <summary>
        /// facade BCL 调用时计算实参的 IL 类型序列（用于 FindMethod 形参签名 / 泛型直构 MemberRef）。
        /// arguments 不含实例方法的 this 接收者（其位于 node.Expression）；对应形参下标整体右移 1。
        /// byref 形参（out/ref）追加 &（IlType.ByRefOf），与方法真实签名一致。
        /// </summary>
        private IlType[] GetFacadeArgumentIlTypes(FunctionSymbol method, bool isInstance, IEnumerable<BoundExpression> arguments)
        {
            var args = arguments.ToList();
            var argOffset = isInstance ? 1 : 0;
            var types = new IlType[args.Count];
            for (var i = 0; i < args.Count; i++)
            {
                var p = method.Parameters[i + argOffset];
                var t = ToIlType(args[i].Type);
                if (p.IsByRef)
                {
                    t = IlType.ByRefOf(t);
                }
                types[i] = t;
            }
            return types;
        }

        /// <summary>
        /// 发射 facade 实例调用的接收者：
        /// 引用类型直接入栈 + Callvirt；值类型存入临时局部后取地址（ldloca）+ Call（非虚，this 按托管指针传参）。
        /// </summary>
        private void EmitFacadeInstanceReceiver(IlAssembler il, BoundExpression receiver)
        {
            if (IsValueTypeSymbol(receiver.Type))
            {
                var local = AllocateTemporaryLocal(receiver);
                EmitExpression(il, receiver);
                il.Emit(IlOpCodeTable.Get("Stloc"), (ushort)local);
                il.Emit(IlOpCodeTable.Get("Ldloca"), (ushort)local);
            }
            else
            {
                EmitExpression(il, receiver);
            }
        }

        private bool TryEmitFacadeBclCall(IlAssembler il, BoundCallExpression node)
        {
            var fn = node.Function;
            var cc = fn.ContainingClass;
            if (cc == null || !IsFacadeRedirect(cc)) return false;

            // facade 实例方法已降级为静态（首参 = this）；真正静态方法无 this 首参。
            // this 标记经 .coa 序列化保留（IsThisParameter ⇔ IsReadOnly）。
            var isInstance = fn.Parameters.Length > 0 && fn.Parameters[0].IsThisParameter;
            var methodArgs = isInstance ? node.Arguments.Skip(1) : node.Arguments;

            IlMethodRef? methodRef;
            if (cc is InstantiatedTypeSymbol inst)
            {
                // 泛型 facade：直构 MemberRef（绕过 MetadataReader 对 GENERICINST 的解析缺口）
                methodRef = ResolveFacadeGenericMethodRef(inst, fn, methodArgs, isInstance);
            }
            else
            {
                var argTypeNames = GetFacadeArgumentIlTypes(fn, isInstance, methodArgs).Select(t => t.FullName).ToArray();
                methodRef = _framework.FindMethod(FacadeBclFullName(cc), fn.Name, argTypeNames);
            }

            if (methodRef == null)
            {
                // facade 属性可能映射到 BCL 字段（Vector3.X 等可变值类型字段，无 get_X/set_X 方法）：
                // 退化到 ldfld/stfld 重定向。
                if (fn.Name.StartsWith("get_") || fn.Name.StartsWith("set_"))
                {
                    var fieldName = fn.Name.Substring(4);
                    var fieldRef = _framework.FindField(FacadeBclFullName(cc), fieldName);
                    if (fieldRef != null)
                    {
                        var receiver = node.Arguments[0];
                        if (receiver is BoundConversionExpression conversion)
                        {
                            receiver = conversion.Expression;
                        }

                        if (IsValueTypeSymbol(receiver.Type))
                        {
                            EmitValueTypeReceiverAddress(il, receiver);
                        }
                        else
                        {
                            EmitExpression(il, receiver);
                        }

                        if (fn.Name.StartsWith("get_"))
                        {
                            il.Emit(IlOpCodeTable.Get("Ldfld"), fieldRef);
                        }
                        else
                        {
                            EmitExpression(il, methodArgs.First());
                            il.Emit(IlOpCodeTable.Get("Stfld"), fieldRef);
                        }

                        return true;
                    }
                }

                return false;
            }

            if (isInstance) EmitFacadeInstanceReceiver(il, node.Arguments[0]);

            foreach (var a in methodArgs) EmitExpression(il, a);
            var callOp = !isInstance || IsValueTypeSymbol(node.Arguments[0].Type) ? "Call" : "Callvirt";
            il.Emit(IlOpCodeTable.Get(callOp), methodRef);
            return true;
        }

        private IlMethodRef? ResolveFacadeGenericMethodRef(InstantiatedTypeSymbol inst, FunctionSymbol fn, IEnumerable<BoundExpression> methodArgs, bool isInstance)
        {
            var def = inst.GenericDefinition!;
            var openName = def.FullName + "`" + def.TypeParameters.Length;
            var genericDef = _framework.RequireType(openName);
            var declaringSpec = _metadata.DefineTypeSpec(IlType.GenericInstance(genericDef, inst.TypeArguments.Select(ToIlType).ToArray()));
            var returnIlType = ToFacadeIlType(fn.ReturnType, inst);
            var args = methodArgs.ToList();
            var argOffset = isInstance ? 1 : 0;
            var paramIlTypes = args.Select((a, i) =>
            {
                var p = fn.Parameters[i + argOffset];
                var t = ToFacadeIlType(a.Type, inst);
                if (p.IsByRef) t = IlType.ByRefOf(t);
                return t;
            }).ToArray();
            return _metadata.DefineMethodRef(declaringSpec, fn.Name, returnIlType, paramIlTypes, isStatic: !isInstance);
        }

        private IlMethodRef? ResolveFacadeCtor(NamedTypeSymbol classType, ImmutableArray<BoundExpression> arguments)
        {
            var paramTypes = arguments.Select(a => ToIlType(a.Type)).ToArray();
            if (classType is InstantiatedTypeSymbol inst)
            {
                var def = inst.GenericDefinition!;
                var openName = FacadeBclFullName(def) + "`" + def.TypeParameters.Length;
                var genericDef = _framework.RequireType(openName);
                var declaringSpec = _metadata.DefineTypeSpec(IlType.GenericInstance(genericDef, inst.TypeArguments.Select(ToIlType).ToArray()));
                return _metadata.DefineMethodRef(declaringSpec, ".ctor", IlType.Void, paramTypes, isStatic: false);
            }

            var parameterNames = arguments.Select(a => ToIlType(a.Type).FullName).ToArray();
            return _framework.FindMethod(FacadeBclFullName(classType), ".ctor", parameterNames);
        }

        private IlType ToFacadeIlType(TypeSymbol type, InstantiatedTypeSymbol inst)
        {
            if (type is TypeParameterSymbol tp)
            {
                var def = inst.GenericDefinition!;
                for (var i = 0; i < def.TypeParameters.Length; i++)
                {
                    if (def.TypeParameters[i] == tp) return ToIlType(inst.TypeArguments[i]);
                }

                return ToIlType(type);
            }

            if (type.ElementType != null)
            {
                return IlType.SzArrayOf(ToFacadeIlType(type.ElementType, inst));
            }

            return ToIlType(type);
        }


    }
}
