# Samples

Cocoa 编译器官方示例集。所有项目聚合在唯一解决方案 [`samples.cosln`](samples.cosln) 中：

```bash
cocoa build -p samples/samples.cosln               # native 后端（聚合构建容错）
cocoa build -p samples/samples.cosln -b dotnet     # IL/dotnet 后端
```

单个项目构建/运行的通用模板：

```bash
cocoa build -p samples/<路径>/<项目>.coproj [-b dotnet]
./samples/<路径>/out/<项目>.exe        # coproj 默认 dotnetRuntime = net48，netfx 产物直接运行
```

## 目录结构

| 目录 | 内容 |
|------|------|
| `Tutorial/Basics` | 语言入门：HelloWorld · Types · ControlFlow · Functions |
| `Tutorial/Data` | 数据类型：Arrays · Strings · Doubles · ByteArrays · Enums |
| `Tutorial/Dialects` | 语法风格对照：CsStyle · TopLevelFunctions |
| `Tutorial/Interop` | native DLL 导入（kernel32 import 块） |
| `Libraries/NetLibrary` | .NET dll 库（`output = library`）+ `[references]` 消费方 app |
| `Libraries/CoaLibrary` | .coa Cocoa 程序集库（`output = cocoa`）+ 消费方 app |
| `Classes/CSharpClass` | Cocoa 式类语法预览：字段/属性/构造函数/static |
| `Samples/UI` | System.UI 立即模式 UI 库：BasicUI · AdvancedUI · NativeUI · DeclarativeUI |
| `Samples/Linux` | Linux native（`--os linux` ELF）：LibcInterop · LibmMath · CrossPlatform（WSL 实机验证） |

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

### Tutorial/Dialects — 语法风格对照

| 示例 | 演示内容 | 关键输出 |
|------|---------|---------|
| CsStyle | C# 式参数/局部变量/分号 ↔ Cocoa 式同文件对照（双后端） | `42`、`Hi, Cocoa (3)` |
| TopLevelFunctions | C# 式顶层函数（`public static void Main()` 等） | `30`、`ababab` |

> 注：`.cs` 严格 C# 方言已随「去 C# 方言」移除（2026-09-13），`CSharpDialect` 样例同步删除。

### Tutorial/Interop — 互操作

| 示例 | 演示内容 | 关键输出 |
|------|---------|---------|
| Interop | `import kernel32.dll { static extern ... }` import 块 + `System.Runtime.Random` | `True`、`True` |

## UI 示例（Samples/UI）

System.UI 立即模式 UI 库（阶段 1-5，见 [`docs/UI库手册.md`](../docs/UI库手册.md)）：

| 示例 | 演示内容 | 后端 |
|------|---------|------|
| BasicUI | 核心控件集 + Win32 GDI 轮询后端，端到端弹窗渲染 | IL |
| AdvancedUI | 完整控件集 + 输入/滚轮 + 子区域滚动 + 运行期主题切换 | IL |
| NativeUI | native 最小控件集（x64） | native |
| DeclarativeUI | 声明式语法糖：`UIView.Body` + `Ui.VStack/HStack/Group/Panel` | IL |

## Linux 原生示例（Samples/Linux）

Linux native 后端（T3/N3/N4，产出 ELF）专属示例，需 `--os linux` + WSL/VM 实机验证（不在默认 `samples.cosln` 聚合内）：

```bash
# 构建（Windows 宿主交叉产出 ELF）
cocoa build -p samples/Samples/Linux/LibcInterop/LibcInterop.coproj -b native --os linux
cocoa build -p samples/Samples/Linux/LibmMath/LibmMath.coproj -b native --os linux
cocoa build -p samples/Samples/Linux/CrossPlatform/CrossPlatform.coproj -b native --os linux

# WSL 实机运行（tools/verify-linux-wsl.ps1，输出 COCOA_VERIFY_OK）
powershell -File tools/verify-linux-wsl.ps1 samples/Samples/Linux/LibcInterop/out/LibcInterop
powershell -File tools/verify-linux-wsl.ps1 samples/Samples/Linux/LibmMath/out/LibmMath
powershell -File tools/verify-linux-wsl.ps1 samples/Samples/Linux/CrossPlatform/out/CrossPlatform

# 跨平台工程也可直接打 Windows（同一份源码按 $(TargetOS) 选源，产 PE 本机运行）
cocoa build -p samples/Samples/Linux/CrossPlatform/CrossPlatform.coproj -b native --os windows
./samples/Samples/Linux/CrossPlatform/out/CrossPlatform.exe
```

| 示例 | 演示内容 | 关键输出 |
|------|---------|---------|
| LibcInterop | 外部 `.so`（libc.so.6）导入：`puts`/`strlen`（UTF-8 string 编组）、`getpid`、`fflush(NULL)`、System.IO facade 文件往返 | `hello from libc`、`5`、`True`、`True` |
| LibmMath | `libm.so.6` 动态导入 + f64 xmm ABI：`sqrt`/`pow`/`sin` 固定值断言 | `True` × 3 |
| CrossPlatform | 同一工程按目标 OS 选源：`partial class` 门面 `Platform.Pid()`（`extern(entry=…)` 别名），linux→`getpid` / windows→`GetCurrentProcessId`；互斥 import 不触发对称门禁 | linux/windows 本机均 `True` |

## 库示例（Libraries）

两种库格式各一套 mylib + app，均可用目录内 [`build.cmd`](Libraries/NetLibrary/build.cmd) 一键构建：

```bash
# .NET dll 库（output = library）：CopyLocal 自动复制 dll 到 app 输出目录（仅 dotnet 后端）
cocoa build -p samples/Libraries/NetLibrary/mylib/MyLib.coproj -b dotnet
cocoa build -p samples/Libraries/NetLibrary/app/App.coproj -b dotnet
./samples/Libraries/NetLibrary/app/out/App.exe

# .coa Cocoa 程序集库（output = cocoa）：编译期 IR 合并，native + dotnet 双后端
cocoa build -p samples/Libraries/CoaLibrary/mylib/MyLib.coproj
cocoa build -p samples/Libraries/CoaLibrary/app/App.coproj -b native
./samples/Libraries/CoaLibrary/app/out/App.exe
```

## 注意事项

- **聚合构建容错**：`Classes/CSharpClass`（OOP）与 `Libraries/NetLibrary`（dll 库）暂不支持 native 后端，
  `-b native` 聚合构建允许整体退出非零，Tutorial 各块产物不受影响。
- **增量构建**：所有项目已启用 `incremental = true`，二次构建输出 `up to date`；
  清理用 `cocoa clean -p <项目或解决方案>`。
