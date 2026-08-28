# Samples

Cocoa 编译器官方示例集。所有项目聚合在唯一解决方案 [`samples.cosln`](samples.cosln) 中：

```bash
cocoa build -p samples/samples.cosln               # native 后端（聚合构建容错）
cocoa build -p samples/samples.cosln -b dotnet     # IL/dotnet 后端
```

单个项目构建/运行的通用模板：

```bash
cocoa build -p samples/<路径>/<项目>.cocproj [-b dotnet]
./samples/<路径>/out/<项目>.exe        # coproj 默认 dotnetRuntime = net48，netfx 产物直接运行
```

## 目录结构

| 目录 | 内容 |
|------|------|
| `Tutorial/Basics` | 语言入门：HelloWorld · Types · ControlFlow · Functions |
| `Tutorial/Data` | 数据类型：Arrays · Strings · Doubles · ByteArrays · Enums |
| `Tutorial/Dialects` | 方言对照：CsStyle · TopLevelFunctions · CSharpDialect |
| `Tutorial/Interop` | native DLL 导入（kernel32 import 块） |
| `Libraries/NetLibrary` | .NET dll 库（`output = library`）+ `[references]` 消费方 app |
| `Libraries/CodLibrary` | .cod Cocoa 程序集库（`output = cocoa`）+ 消费方 app |
| `Classes/CSharpClass` | Cocoa 式类语法预览：字段/属性/构造函数/static |

## 示例索引

### Tutorial/Basics — 语言入门

| 示例 | 演示内容 | 关键输出 |
|------|---------|---------|
| HelloWorld | 入口 `Main` + lib 函数调用 | `Hello, Cocoa!`、`42` |
| Types | i32/f64/string/bool 变量与类型推断 | `42`、`3.14`、`Alice` |
| ControlFlow | if/else 分支、while/for 循环、逻辑运算 | `positive`、`15` |
| Functions | `entry = run` 接收命令行参数、递归 Factorial、函数组合 | 运行时带参：`120`、`alpha` |

### Tutorial/Data — 数据类型

| 示例 | 演示内容 | 关键输出 |
|------|---------|---------|
| Arrays | 数组字面量、索引读写、`Length`、求和/查找 | `99`、`139`、`True` |
| Strings | `substring`/索引取字符/char↔i32/`Length`/拼接 | `ell!`、`101` |
| Doubles | f64 字面量、混合运算、比较、截断、类型转换、f64 数组 | `3.75`、`5.5` |
| ByteArrays | byte 数组创建、赋值、溢出回绕 | `65`、`255` |
| Enums | 枚举定义、比较、自定义值 | `404`、`True` |

### Tutorial/Dialects — 方言对照

| 示例 | 演示内容 | 关键输出 |
|------|---------|---------|
| CsStyle | C# 式参数/局部变量/分号 ↔ Cocoa 式同文件对照（双后端） | `42`、`Hi, Cocoa (3)` |
| TopLevelFunctions | C# 式顶层函数（`public static void Main()` 等） | `30`、`ababab` |
| CSharpDialect | 纯 `.cs` 严格 C# 方言：类型前置、分号必选、`namespace X;`、foreach、字符串插值、switch when | `i = 2`、`few` |

### Tutorial/Interop — 互操作

| 示例 | 演示内容 | 关键输出 |
|------|---------|---------|
| Interop | `import kernel32.dll { static extern ... }` import 块 + `System.Runtime.Random` | `True`、`True` |

## 库示例（Libraries）

两种库格式各一套 mylib + app，均可用目录内 [`build.cmd`](Libraries/NetLibrary/build.cmd) 一键构建：

```bash
# .NET dll 库（output = library）：CopyLocal 自动复制 dll 到 app 输出目录（仅 dotnet 后端）
cocoa build -p samples/Libraries/NetLibrary/mylib/MyLib.cocproj -b dotnet
cocoa build -p samples/Libraries/NetLibrary/app/App.cocproj -b dotnet
./samples/Libraries/NetLibrary/app/out/App.exe

# .cod Cocoa 程序集库（output = cocoa）：编译期 IR 合并，native + dotnet 双后端
cocoa build -p samples/Libraries/CodLibrary/mylib/MyLib.cocproj
cocoa build -p samples/Libraries/CodLibrary/app/App.cocproj -b native
./samples/Libraries/CodLibrary/app/out/App.exe
```

## 注意事项

- **聚合构建容错**：`Classes/CSharpClass`（OOP）与 `Libraries/NetLibrary`（dll 库）暂不支持 native 后端，
  `-b native` 聚合构建允许整体退出非零，Tutorial 各块产物不受影响。
- **增量构建**：所有项目已启用 `incremental = true`，二次构建输出 `up to date`；
  清理用 `cocoa clean -p <项目或解决方案>`。
