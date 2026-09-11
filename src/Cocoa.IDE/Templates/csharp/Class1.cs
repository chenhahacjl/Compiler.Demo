// C# 方言（.cs 严格子集）：类型前置、分号必选
namespace {{Name}};

public static void Main()
{
    Console.WriteLine("Hello from {{Name}}!");
    Console.WriteLine(Add(2, 3));
}

public int Add(int a, int b)
{
    return a + b;
}
