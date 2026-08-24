using Cocoa.CodeAnalysis.Symbols;
using System.Collections.Immutable;
using System.Linq;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// 泛型类型系统单元测试（6e-M20 G1）：TypeParameterSymbol / 实例化去重 / mangle 命名 / 类型替换 / 惰性物化。
    /// </summary>
    public class GenericTypeTests
    {
        private static (ClassTypeSymbol Definition, TypeParameterSymbol T) DeclareGeneric(string name = "Box")
        {
            var definition = new ClassTypeSymbol(name, "Test", Visibility.Public, declaration: null);
            var t = new TypeParameterSymbol("T", 0, definition);
            definition.TypeParameters = ImmutableArray.Create(t);

            return (definition, t);
        }

        [Fact]
        public void Instantiate_ProducesMangledName()
        {
            var (definition, _) = DeclareGeneric();
            var instantiated = GenericTypeInstantiator.Instantiate(definition, ImmutableArray.Create<TypeSymbol>(TypeSymbol.Int32));

            Assert.IsType<InstantiatedTypeSymbol>(instantiated);
            Assert.Equal("Box_int", instantiated.Name);
            Assert.Equal(definition, Assert.IsType<InstantiatedTypeSymbol>(instantiated).GenericDefinition);
        }

        [Fact]
        public void Instantiate_DeduplicatesByArgumentTuple()
        {
            var (definition, _) = DeclareGeneric();
            var first = GenericTypeInstantiator.Instantiate(definition, ImmutableArray.Create<TypeSymbol>(TypeSymbol.Int32));
            var second = GenericTypeInstantiator.Instantiate(definition, ImmutableArray.Create<TypeSymbol>(TypeSymbol.Int32));
            var other = GenericTypeInstantiator.Instantiate(definition, ImmutableArray.Create<TypeSymbol>(TypeSymbol.String));

            Assert.Same(first, second);
            Assert.NotSame(first, other);
            Assert.Equal("Box_string", other.Name);
        }

        [Fact]
        public void Instantiate_WrongArity_Throws()
        {
            var (definition, _) = DeclareGeneric();

            Assert.Throws<InvalidOperationException>(() => GenericTypeInstantiator.Instantiate(definition, ImmutableArray<TypeSymbol>.Empty));
        }

        [Fact]
        public void Instantiate_NonGenericDefinition_Throws()
        {
            var plain = new ClassTypeSymbol("Plain", "Test", Visibility.Public, declaration: null);

            Assert.Throws<InvalidOperationException>(() => GenericTypeInstantiator.Instantiate(plain, ImmutableArray.Create<TypeSymbol>(TypeSymbol.Int32)));
        }

        [Fact]
        public void LazyMaterialization_SubstitutesMembersAddedAfterInstantiate()
        {
            // 前向引用场景：实例化先于成员声明——首访问时物化才正确
            var (definition, t) = DeclareGeneric();
            var shell = GenericTypeInstantiator.Instantiate(definition, ImmutableArray.Create<TypeSymbol>(TypeSymbol.String));

            definition.AddField(new FieldSymbol("_value", t, Visibility.Private, definition));
            definition.AddMethod(new FunctionSymbol(
                "Get",
                ImmutableArray<ParameterSymbol>.Empty,
                t,
                containingClass: definition));

            Assert.False(Assert.IsType<InstantiatedTypeSymbol>(shell).IsMaterialized);

            var field = shell.GetDeclaredField("_value");
            Assert.NotNull(field);
            Assert.Equal(TypeSymbol.String, field!.Type);

            var method = shell.GetDeclaredMethod("Get");
            Assert.NotNull(method);
            Assert.Equal(TypeSymbol.String, method!.ReturnType);
            Assert.Same(shell, method!.ContainingClass);
            Assert.True(Assert.IsType<InstantiatedTypeSymbol>(shell).IsMaterialized);
        }

        [Fact]
        public void Substitute_ReplacesArraysAndNestedGenerics()
        {
            var (definition, t) = DeclareGeneric("Store");
            var innerDefinition = new ClassTypeSymbol("Wrapper", "Test", Visibility.Public, declaration: null);
            var u = new TypeParameterSymbol("U", 0, innerDefinition);
            innerDefinition.TypeParameters = ImmutableArray.Create(u);

            // Store<T> 字段：T[] 与 Wrapper<List<T>>（嵌套实例化）
            definition.AddField(new FieldSymbol("_items", TypeSymbol.ArrayOf(t), Visibility.Private, definition));
            var listOfT = GenericTypeInstantiator.Instantiate(innerDefinition, ImmutableArray.Create<TypeSymbol>(
                GenericTypeInstantiator.Instantiate(DeclareGeneric().Definition, ImmutableArray.Create<TypeSymbol>(t))));
            definition.AddField(new FieldSymbol("_nested", listOfT, Visibility.Private, definition));

            var instantiated = (InstantiatedTypeSymbol)GenericTypeInstantiator.Instantiate(definition, ImmutableArray.Create<TypeSymbol>(TypeSymbol.Int32));

            var items = instantiated.GetDeclaredField("_items")!;
            Assert.Equal(TypeSymbol.ArrayOf(TypeSymbol.Int32), items.Type);

            // Wrapper<Box<T>> + T=int → Wrapper_Box_int（实参递归替换）
            var nestedField = instantiated.GetDeclaredField("_nested")!;
            var nested = Assert.IsType<InstantiatedTypeSymbol>(nestedField.Type);
            Assert.Equal("Wrapper_Box_int", nested.Name);
            Assert.Equal("Box_int", Assert.IsType<InstantiatedTypeSymbol>(Assert.Single(nested.TypeArguments)).Name);
        }

        [Fact]
        public void SelfReferentialField_MaterializesWithoutRecursion()
        {
            // class Node<T> { _next: Node<T> } —— 缓存槽预留防无限递归
            var (definition, t) = DeclareGeneric("Node");
            definition.AddField(new FieldSymbol("_next", GenericTypeInstantiator.Instantiate(definition, ImmutableArray.Create<TypeSymbol>(t)), Visibility.Private, definition));

            var nodeInt = (InstantiatedTypeSymbol)GenericTypeInstantiator.Instantiate(definition, ImmutableArray.Create<TypeSymbol>(TypeSymbol.Int32));

            var next = nodeInt.GetDeclaredField("_next")!;
            Assert.Equal(nodeInt, next.Type);
            Assert.Equal("Node_int", next.Type.Name);
        }

        [Fact]
        public void MangledName_EncodesArraysAndNesting()
        {
            var (boxDef, boxT) = DeclareGeneric("Box");

            // Box<int[]> → Box_int_Array
            var boxOfArray = GenericTypeInstantiator.Instantiate(boxDef, ImmutableArray.Create<TypeSymbol>(TypeSymbol.ArrayOf(TypeSymbol.Int32)));
            Assert.Equal("Box_int_Array", boxOfArray.Name);

            // Box<Box<int>> → Box_Box_int
            var boxOfBox = GenericTypeInstantiator.Instantiate(boxDef, ImmutableArray.Create<TypeSymbol>(
                GenericTypeInstantiator.Instantiate(boxDef, ImmutableArray.Create<TypeSymbol>(TypeSymbol.Int32))));
            Assert.Equal("Box_Box_int", boxOfBox.Name);
        }

        [Fact]
        public void Instantiate_CopiesBaseAndInterfaces()
        {
            var baseDef = new ClassTypeSymbol("Repo", "Test", Visibility.Public, declaration: null);
            var ifaceDef = new ClassTypeSymbol("IStore", "Test", Visibility.Public, declaration: null) { IsInterface = true };

            var (definition, _) = DeclareGeneric("Db");
            definition.BaseType = baseDef;
            definition.AddInterface(ifaceDef);

            var instantiated = (InstantiatedTypeSymbol)GenericTypeInstantiator.Instantiate(definition, ImmutableArray.Create<TypeSymbol>(TypeSymbol.Int32));

            Assert.Same(baseDef, instantiated.BaseType);
            Assert.Contains(instantiated.GetAllInterfaces(), i => i == ifaceDef);
        }
    }
}
