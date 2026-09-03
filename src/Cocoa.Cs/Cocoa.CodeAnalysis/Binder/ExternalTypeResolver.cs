using Cocoa.CodeAnalysis.Emit.IL;
using Cocoa.CodeAnalysis.Symbols;
using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 从引用程序集解析外部类型（消费 -r 库）。
    /// </summary>
    internal static class ExternalTypeResolver
    {
        private static readonly ConcurrentDictionary<string, NamedTypeSymbol?> _cache = new ConcurrentDictionary<string, NamedTypeSymbol?>();

        public static NamedTypeSymbol? TryResolve(string fullName, string[] references)
        {
            if (_cache.TryGetValue(fullName, out var cached))
            {
                return cached;
            }

            var result = Resolve(fullName, references);
            _cache.TryAdd(fullName, result);
            return result;
        }

        private static NamedTypeSymbol? Resolve(string fullName, string[] references)
        {
            var reader = new MetadataReader(references);
            var info = reader.FindTypeInfo(fullName);
            if (info == null)
            {
                return null;
            }

            var dot = fullName.LastIndexOf('.');
            var ns = dot < 0 ? "" : fullName.Substring(0, dot);
            var name = dot < 0 ? fullName : fullName.Substring(dot + 1);

            var classType = new NamedTypeSymbol(name, ns, Visibility.Public, declaration: null, isExternal: true)
            {
                TypeKind = info.IsInterface ? TypeKind.Interface : TypeKind.Class,
            };

            foreach (var field in info.Fields)
            {
                if (classType.GetField(field.Name) == null)
                {
                    classType.AddField(new FieldSymbol(field.Name, ToTypeSymbol(field.Type), field.IsPublic ? Visibility.Public : Visibility.Private, classType));
                }
            }

            foreach (var method in info.Methods)
            {
                var methodName = method.Name == ".ctor" ? name : method.Name;
                // 1b/B10：按（名字, 元数）去重——旧实现按名字去重会把外部构造器/方法重载整个丢掉
                if (classType.GetMethods(methodName).Any(m => m.Parameters.Length == method.ParameterTypes.Count))
                {
                    continue;
                }

                var parameters = ImmutableArray.CreateBuilder<ParameterSymbol>();
                for (var i = 0; i < method.ParameterTypes.Count; i++)
                {
                    parameters.Add(new ParameterSymbol("p" + i, ToTypeSymbol(method.ParameterTypes[i]), i));
                }

                classType.AddMethod(new FunctionSymbol(
                    methodName,
                    parameters.ToImmutable(),
                    ToTypeSymbol(method.ReturnType),
                    isExtern: false,
                    containingClass: classType,
                    visibility: Visibility.Public)
                {
                    // .ctor 改名为类名登记，但构造器身份必须保留——绑定期构造重载解析按此筛选
                    IsConstructor = method.Name == ".ctor",
                });
            }

            return classType;
        }

        private static TypeSymbol ToTypeSymbol(IlType type)
        {
            switch (type.Kind)
            {
                case IlTypeKind.Void: return TypeSymbol.Void;
                case IlTypeKind.Boolean: return TypeSymbol.Boolean;
                case IlTypeKind.Int32: return TypeSymbol.Int32;
                case IlTypeKind.Int64: return TypeSymbol.Int64;
                case IlTypeKind.Char: return TypeSymbol.Char;
                case IlTypeKind.U1: return TypeSymbol.UInt8;
                case IlTypeKind.Double: return TypeSymbol.Double;
                case IlTypeKind.String: return TypeSymbol.String;
                case IlTypeKind.Object: return TypeSymbol.Any;
                case IlTypeKind.SzArray: return TypeSymbol.ArrayOf(ToTypeSymbol(type.ElementType!));
                default: return TypeSymbol.Any;
            }
        }
    }
}
