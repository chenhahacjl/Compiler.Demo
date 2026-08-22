// C# 方言样例（6e-M15）：.cs 走严格 C# 方言解析器，与 .co（宽松主方言）共用表达式引擎/Binder/三后端。
// 特性：类型前置参数/局部、分号必选、文件作用域命名空间 namespace X;、foreach (var x in ...)、C 式 for、
// switch + when、字符串插值、const、`for (int i = 0; ...)` 类型前置声明。不绑定 .NET BCL（用内置 print 等）。
// 注：类/自动属性在 native 后端暂不支持（见 docs/类库设计.md），本样例用顶层函数以兼容双后端。

namespace CSharpDialect;

public static void Main()
{
    var numbers = BuildNumbers();
    System.Console.WriteLine(numbers.Length);

    var total = 0;
    foreach (var n in numbers)
    {
        total += n;
    }
    System.Console.WriteLine(total);

    for (int i = 0; i < 3; i++)
    {
        System.Console.WriteLine($"i = {i}");
    }

    System.Console.WriteLine(Add(2, 3));
    System.Console.WriteLine(Multiply(4, 5));
    System.Console.WriteLine(Describe(1));
    System.Console.WriteLine(Describe(2));
    System.Console.WriteLine(Describe(3));
}

public int[] BuildNumbers()
{
    return new int[] { 10, 20, 30, 40 };
}

public int Add(int a, int b)
{
    return a + b;
}

public int Multiply(int a, int b)
{
    const int factor = 2;
    return a * b * factor;
}

// switch 仅支持常量 case + when 守卫（§10.8）；多值 `case 1, 2:` 与叠标 `case 1: case 2:` 均可
public string Describe(int category)
{
    switch (category)
    {
        case 1:
        {
            return "one";
        }
        case 2, 3:
        {
            return "few";
        }
        default:
        {
            return "many";
        }
    }
}
