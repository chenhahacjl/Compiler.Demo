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
        private static readonly ConcurrentDictionary<string, ClassTypeSymbol?> _cache = new ConcurrentDictionary<string, ClassTypeSymbol?>();

        public static ClassTypeSymbol? TryResolve(string fullName, string[] references)
        {
            if (_cache.TryGetValue(fullName, out var cached))
            {
                return cached;
            }

            var result = Resolve(fullName, references);
            _cache.TryAdd(fullName, result);
            return result;
        }

        private static ClassTypeSymbol? Resolve(string fullName, string[] references)
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

            var classType = new ClassTypeSymbol(name, ns, Visibility.Public, declaration: null, isExternal: true)
            {
                IsInterface = info.IsInterface,
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
                if (classType.GetMethod(methodName) != null)
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
                    visibility: Visibility.Public));
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
                case IlTypeKind.Char: return TypeSymbol.Char;
                case IlTypeKind.U1: return TypeSymbol.Byte;
                case IlTypeKind.Double: return TypeSymbol.Double;
                case IlTypeKind.String: return TypeSymbol.String;
                case IlTypeKind.Object: return TypeSymbol.Any;
                case IlTypeKind.SzArray: return TypeSymbol.ArrayOf(ToTypeSymbol(type.ElementType!));
                default: return TypeSymbol.Any;
            }
        }
    }
}
