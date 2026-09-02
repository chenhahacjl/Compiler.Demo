using System.Collections.Generic;
using System.Linq;
using Cocoa.CodeAnalysis.Symbols;

namespace Cocoa.CodeAnalysis.Emit.Native
{
    /// <summary>
    /// 6e-M19 M4：native 对象模型静态辅助——实例字段布局、vtable 槽位分配、LirFunction 全局唯一名。
    /// 设计见 docs-dev/对象模型设计.md §8：
    ///   对象布局   [0] vtablePtr(4/8B) [4] pad(4B) [8] 字段…（基类在前、派生在后，8 字节对齐头）
    ///   vtable     [0] typeId:int [4] pad [8] 名字指针 [8+ps·(i+1)] 槽 i（Object 固定 0..3，用户虚方法续接）
    /// </summary>
    internal static class NativeObjectModel
    {
        /// <summary>对象头字节数（vtablePtr + pad；两架构统一 8 字节，对齐 string/array 惯例）。</summary>
        public const int HeaderBytes = 8;

        public const int SlotToString = 0;
        public const int SlotGetHashCode = 1;
        public const int SlotEquals = 2;
        public const int SlotGetType = 3;

        /// <summary>Object 固定槽实现（运行时函数名）；GetType 非虚但占槽（值恒为自身 vtable 指针）。</summary>
        public static readonly string[] ObjectSlotFunctions =
        {
            "ObjectToString",
            "ObjectGetHashCode",
            "ObjectEquals",
            "ObjectGetType",
        };

        // ------------------------------------------------------------------
        // 实例字段布局
        // ------------------------------------------------------------------

        /// <summary>实例字段的存储宽度（字节）。引用类型字段统一 8 字节（x86 高位空洞无副作用）。</summary>
        public static int FieldSize(TypeSymbol type)
        {
            if (type.IsPrimitiveValueType)
            {
                if (type == TypeSymbol.Boolean || type == TypeSymbol.UInt8 || type == TypeSymbol.Int8)
                {
                    return 1;
                }

                if (type == TypeSymbol.Char || type == TypeSymbol.UInt16 || type == TypeSymbol.Int16)
                {
                    return 2;
                }

                if (type == TypeSymbol.Int32 || type == TypeSymbol.UInt32 || type == TypeSymbol.Float)
                {
                    return 4;
                }

                return 8; // long/ulong/double/i128/u128
            }

            if (type is NamedTypeSymbol { TypeKind: TypeKind.Enum })
            {
                return 4;
            }

            return 8; // string/类/接口/delegate/any/数组/用户 struct
        }

        /// <summary>沿继承链（基类在前）计算全部实例字段偏移与实例总尺寸。</summary>
        public static (Dictionary<FieldSymbol, int> Offsets, int InstanceSize) BuildLayout(NamedTypeSymbol classType)
        {
            var chain = new List<NamedTypeSymbol>();
            var seen = new HashSet<NamedTypeSymbol>();
            for (var current = classType; current != null && !current.IsSystemObjectRoot && seen.Add(current); current = current.BaseType)
            {
                chain.Add(current);
            }

            chain.Reverse();

            var offsets = new Dictionary<FieldSymbol, int>();
            var offset = HeaderBytes;
            foreach (var type in chain)
            {
                foreach (var field in type.Fields)
                {
                    if (field.IsStatic)
                    {
                        continue;
                    }

                    var size = FieldSize(field.Type);
                    var alignment = size;
                    var remainder = offset % alignment;
                    if (remainder != 0)
                    {
                        offset += alignment - remainder;
                    }

                    offsets[field] = offset;
                    offset += size;
                }
            }

            return (offsets, offset);
        }

        /// <summary>沿继承链收集全部实例字段（基类在前）。</summary>
        public static List<FieldSymbol> CollectInstanceFields(NamedTypeSymbol classType)
        {
            var chain = new List<NamedTypeSymbol>();
            var seen = new HashSet<NamedTypeSymbol>();
            for (var current = classType; current != null && !current.IsSystemObjectRoot && seen.Add(current); current = current.BaseType)
            {
                chain.Add(current);
            }

            chain.Reverse();

            var fields = new List<FieldSymbol>();
            foreach (var type in chain)
            {
                fields.AddRange(type.Fields.Where(f => !f.IsStatic));
            }

            return fields;
        }

        // ------------------------------------------------------------------
        // 虚方法根与槽位
        // ------------------------------------------------------------------

        /// <summary>虚方法的根（override 链最顶端的 virtual 声明；非 override 虚方法即自身）。</summary>
        public static FunctionSymbol VirtualRoot(FunctionSymbol method)
        {
            var root = method;
            var guard = new HashSet<FunctionSymbol>();
            while (root.OverriddenMethod != null && guard.Add(root))
            {
                root = root.OverriddenMethod;
            }

            return root;
        }

        /// <summary>Object 固定虚根（override 链顶端为这些符号时复用固定槽，不另分配）。</summary>
        public static readonly FunctionSymbol[] ObjectVirtualRoots =
        {
            SystemObjectMembers.ToString,
            SystemObjectMembers.GetHashCode,
            SystemObjectMembers.Equals,
        };

        /// <summary>
        /// 为存活类集合分配 vtable 槽位：Object 三虚根固定 0..2（GetType=3 非虚占位），
        /// 用户虚根按类全名字典序 + 链序（基→派）+ 声明序确定性地自 4 续接。
        /// 返回 根方法 → 槽索引 映射。
        /// </summary>
        public static Dictionary<FunctionSymbol, int> AssignVirtualSlots(IEnumerable<NamedTypeSymbol> liveClasses)
        {
            var slots = new Dictionary<FunctionSymbol, int>();
            for (var i = 0; i < ObjectVirtualRoots.Length; i++)
            {
                slots[ObjectVirtualRoots[i]] = i;
            }

            var next = ObjectSlotFunctions.Length;

            foreach (var classType in liveClasses.OrderBy(c => c.FullName, System.StringComparer.Ordinal))
            {
                foreach (var method in EnumerateChainVirtualMethods(classType))
                {
                    var root = VirtualRoot(method);
                    if (!slots.ContainsKey(root))
                    {
                        slots[root] = next++;
                    }
                }
            }

            return slots;
        }

        /// <summary>根是否为 Object 内建固定槽（vtable 生成时跳过用户续接区）。</summary>
        public static bool IsObjectBuiltinRoot(FunctionSymbol root) => ObjectVirtualRoots.Contains(root);

        /// <summary>沿链（基→派）枚举本类声明为 virtual/override 的方法（去重按根）。</summary>
        private static IEnumerable<FunctionSymbol> EnumerateChainVirtualMethods(NamedTypeSymbol classType)
        {
            var chain = new List<NamedTypeSymbol>();
            var seenTypes = new HashSet<NamedTypeSymbol>();
            for (var current = classType; current != null && !current.IsSystemObjectRoot && seenTypes.Add(current); current = current.BaseType)
            {
                chain.Add(current);
            }

            chain.Reverse();

            var seenRoots = new HashSet<FunctionSymbol>();
            foreach (var type in chain)
            {
                foreach (var method in type.Methods)
                {
                    if (method.IsConstructor || method.IsStatic || (!method.IsVirtual && !method.IsOverride))
                    {
                        continue;
                    }

                    if (seenRoots.Add(VirtualRoot(method)))
                    {
                        yield return method;
                    }
                }
            }
        }

        /// <summary>类 C 对虚根 root 的生效实现：从 C 沿链向上找最近声明（含 C 自身），返回 null = 无用户实现（用运行时默认）。</summary>
        public static FunctionSymbol? FindImplementation(NamedTypeSymbol classType, FunctionSymbol root)
        {
            var seen = new HashSet<NamedTypeSymbol>();
            for (var current = classType; current != null && !current.IsSystemObjectRoot && seen.Add(current); current = current.BaseType)
            {
                foreach (var method in current.Methods)
                {
                    if (method.IsConstructor || method.IsStatic)
                    {
                        continue;
                    }

                    if (VirtualRoot(method) == root)
                    {
                        return method;
                    }
                }
            }

            return null;
        }

        // ------------------------------------------------------------------
        // 命名
        // ------------------------------------------------------------------

        /// <summary>vtable 数据项 key。</summary>
        public static string VTableKey(NamedTypeSymbol classType) => "$vt:" + classType.FullName;

        /// <summary>基元/Type 伪 vtable（System.Type 对象）数据项 key。</summary>
        public static string PseudoVTableKey(string fullName) => "$vt:" + fullName;

        /// <summary>静态字段存储数据项 key。</summary>
        public static string StaticFieldKey(FieldSymbol field) => "$sf:" + field.ContainingClass.FullName + "." + field.Name;

        /// <summary>
        /// LirFunction 全局唯一名。类方法（含构造/静态）以 `Namespace.Class.Name$参数类型` mangle，
        /// 防止跨类同名/重载在 _nameToLabel 平坦命名空间冲突；顶层函数维持裸名
        /// （入口匹配 _program.EntryFunctionName == function.Name 依赖裸名，现状保持）。
        /// </summary>
        public static string FunctionIrName(FunctionSymbol function)
        {
            var containingClass = function.ContainingClass;
            if (containingClass == null)
            {
                // 顶层函数（含入口）：裸名，与入口匹配逻辑一致
                return function.Name;
            }

            var name = containingClass.FullName + "." + (function.IsConstructor ? ".ctor" : function.Name);
            name += "$" + string.Join("$", function.Parameters.Select(p => EncodeTypeName(p.Type)));
            return name;
        }

        private static string EncodeTypeName(TypeSymbol type)
        {
            if (type.ElementType != null)
            {
                return EncodeTypeName(type.ElementType) + "[]";
            }

            // 基元（含 C3 后作为 NamedTypeSymbol）：保持短名编码，ABI 符号名不随 FullName 变化
            if (type.IsPrimitiveValueType || type == TypeSymbol.String)
            {
                return type.Name;
            }

            if (type is NamedTypeSymbol classType)
            {
                return classType.FullName;
            }

            if (type is NamedTypeSymbol { TypeKind: TypeKind.Enum } enumType)
            {
                return enumType.FullName;
            }

            return type.Name;
        }
    }
}
