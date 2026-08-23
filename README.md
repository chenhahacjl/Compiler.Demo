# Cocoa 编译器

用 C# 编写的 C 系方言编译器，同时具备 **Native 代码生成**（x86 / x64，零依赖、纯自研 PE 输出）与 **IL 代码生成**（ECMA-335）两条后端路径，最终目标是用 Cocoa 语言自身重写编译器（自举）。

> 当前阶段：阶段 6 — 语言扩展 + 互操作 + 输出格式 + 项目系统（见 [`docs-dev/开发计划.md`](docs-dev/开发计划.md)）
> 最新：6e-M13（2026-08-20）：`coc` / `coi` 合并为单一 `cocoa` 命令（`-i` 进入 REPL）+ dotnet 式子命令（`new` / `list` / `add reference` / `remove reference` / `run` / `clean`）；核心库更名 `Cocoa.Core`，缓存目录 `.cocoa/`（详见 [`docs/编译手册.md`](docs/编译手册.md) §3）；**6e-M15（2026-08-21）：双前端拆分 ✅ — `.co` 宽松主方言 / `.cs` 严格 C# 方言（`cocoa new csharp`、`cocoa app.cs`），按扩展名分派，特性全部共享（详见 [`docs/语法手册.md`](docs/语法手册.md) §46）**；**6e-M19 规划（2026-08-22）：System.Object 基类 + 全类型成员方法（`1.ToString()`/`"ABC".Substring(0,2)`/`arr.Sum()`）+ `System.Type` + `long`/`Int64` + native 对象模型（真 vtable）——设计见 [`docs-dev/对象模型设计.md`](docs-dev/对象模型设计.md)（🔧）**；**6e-M20 规划（2026-08-22）：泛型（类/接口/方法 + 约束）——编译期单态化，解锁 `List<T>` 与枚举器 foreach，设计见 [`docs-dev/泛型设计.md`](docs-dev/泛型设计.md)（🔧）**

## 路线图（摘要）

| 阶段 | 内容 |
|------|------|
| 0 | 修复 x86 Native 崩溃（黑盒对照 TinyCC/gcc） |
| 1-3 | IR 层：三地址码 + 虚拟寄存器，双后端共用 |
| 4 | 运行时 IR 化（x86/x64 合并） |
| 5 | IL 路径自研（移除 Mono.Cecil），零第三方依赖 |
| 6 | 语言扩展 + 互操作（native DLL/.NET DLL/cod）+ 输出格式（exe/dll/cod）+ 项目系统 |
| 7 | 编译器用 Cocoa 语言重写（自举） |
| 8 | 自举验证（B1 ≡ B2） |
| 9 | （可选）Native 路径的 .NET CLR Hosting 互操作 |

## 快速开始

```bash
# 构建（编译器 + 标准库：cod 产物收集至 src\Cocoa.Cs\libs\，构建时自动分发到各 bin）
dotnet build src\Cocoa.Cs\Cocoa.Compiler
tools\build-stdlib.cmd

# 创建新项目（模板 + 名称，仿 dotnet new）：console / library / cocoa / solution
cocoa new console MyApp

# 编译单文件（默认 exe）
cocoa hello.co

# 交互式 REPL
cocoa -i

# C# 方言（.cs 严格子集）：扩展名即语言，.co 严格纯 Cocoa / .cs 严格 C#（详见 docs/语法手册.md §46）
cocoa new csharp MyApp
cocoa hello.cs
cocoa hello.cs -b native

# 构建仓库自带样例（18 项目聚合解决方案；分组结构与逐示例说明见 samples/README.md）
cocoa build -p samples/samples.cosln
./samples/Tutorial/Basics/HelloWorld/out/HelloWorld.exe

# 库互操作三形态（.NET dll 库 / .cod 程序集 / native DLL import），命令见 samples/README.md
cocoa build -p samples/Libraries/CodLibrary/app/App.coproj -b native
./samples/Libraries/CodLibrary/app/out/App.exe

# 指定输出格式与 .NET 目标框架
cocoa build -p foo.coproj -f library
cocoa build -p foo.coproj --dotnet-runtime net9.0
# netcore 产物 = 托管 x.dll + 原生 apphost x.exe（SDK 标准布局）：x.exe 直接/双击运行，dotnet x.dll 亦可

# netfx 默认：产出 .NET Framework 4.x 镜像，直接运行（无需 dotnet 前缀）
cocoa build -p foo.coproj -b dotnet
./foo.exe
```

## 文档

| 文档 | 说明 |
|------|------|
| [`docs/语法手册.md`](docs/语法手册.md) | Cocoa 语言语法参考（状态标记：✅ 已实现 · 🔧 设计中 · 📋 待实现） |
| [`docs/语法对照表.md`](docs/语法对照表.md) | **Cocoa ↔ C# 方言具体拼写对照**（描述 / `.co` 写法 / `.cs` 写法，变体逐行，含 `.cs` 拒绝清单） |
| [`docs/编译手册.md`](docs/编译手册.md) | 编译器使用手册（`cocoa` 子命令：`new`/`build`/`run`/`list`/`add reference`/`remove reference`/`clean`、`-i` REPL、构建选项、增量构建） |
| [`docs-dev/类库设计.md`](docs-dev/类库设计.md) | 类库体系设计（class/namespace/using/三格式分工：`.cod` Cocoa 程序集 / .NET dll 跨语言桥 / native 对象模型规划） |
| [`docs-dev/OOP设计.md`](docs-dev/OOP设计.md) | 完整 OOP 设计（继承/多态/static/属性/native 对象模型规划） |
| [`docs-dev/对象模型设计.md`](docs-dev/对象模型设计.md) | **System.Object 基类 + 全类型成员方法 + System.Type + native vtable 对象模型**（6e-M19 规划） |
| [`docs-dev/泛型设计.md`](docs-dev/泛型设计.md) | **泛型（类/接口/方法 + 约束）编译期单态化设计**（6e-M20 规划） |
| [`docs/项目格式规范.md`](docs/项目格式规范.md) | `.coproj` / `.cosln` 轻量文本格式规范、`.cod` 程序集格式、增量哈希 |
| [`docs-dev/实现目标.md`](docs-dev/实现目标.md) | 架构设计（Native / IR / IL 三路径、ABI、自举设计） |
| [`docs-dev/开发计划.md`](docs-dev/开发计划.md) | 阶段 0-9 路线图与里程碑 |
| [`docs-dev/输出格式.md`](docs-dev/输出格式.md) | executable / library / cocoa 三种输出格式规范 |
| [`docs/互操作手册.md`](docs/互操作手册.md) | native DLL / .NET DLL / `.cod` 程序集导入与调用约定 |
| [`docs-dev/IR设计.md`](docs-dev/IR设计.md) | IR 指令集、虚拟寄存器、后端映射（阶段 1-3 落地时细化） |

## 参考实现

- [minsk](https://github.com/terrajobst/minsk) — Immo Landwerth 的教学编译器
- [YouTube 系列](https://www.youtube.com/playlist?list=PLRAdsfhKI4OWNOSfS7EUu5GRAVmze1t2y)
- TinyCC / MinGW-w64 — 阶段 0 黑盒对照参照（`objdump` 反汇编对比）
