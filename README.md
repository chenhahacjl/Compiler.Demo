# Cocoa 编译器

用 C# 编写的 C 系方言编译器，同时具备 **Native 代码生成**（x86 / x64，零依赖、纯自研 PE 输出）与 **IL 代码生成**（ECMA-335）两条后端路径，最终目标是用 Cocoa 语言自身重写编译器（自举）。

> 当前阶段：阶段 6 — 语言扩展 + 互操作 + 输出格式 + 项目系统（见 [`docs-dev/开发计划.md`](docs-dev/开发计划.md)）
> 最新：**delegate 真实类型化（6e-M22）已全部落地：IL 真 `MulticastDelegate` 子类 / Evaluator 调用列表 / native 委托对象 + 多播 Combine·Remove·列表相等 + 事件 C# 式 add/remove + 泛型 delegate in/out 型变；全量 41871 绿**。近期流水见 [CHANGELOG.md](CHANGELOG.md)。

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

# 库互操作三形态（.NET dll 库 / .coa 程序集 / native DLL import），命令见 samples/README.md
cocoa build -p samples/Libraries/CodLibrary/app/App.cocproj -b native
./samples/Libraries/CodLibrary/app/out/App.exe

# 指定输出格式与 .NET 目标框架
cocoa build -p foo.cocproj -f library
cocoa build -p foo.cocproj --dotnet-runtime net9.0
# netcore 产物 = 托管 x.dll + 原生 apphost x.exe（SDK 标准布局）：x.exe 直接/双击运行，dotnet x.dll 亦可

# netfx 默认：产出 .NET Framework 4.x 镜像，直接运行（无需 dotnet 前缀）
cocoa build -p foo.cocproj -b dotnet
./foo.exe
```

## 文档

- 正式参考（语言/命令/API/架构）：[`docs/README.md`](docs/README.md) 入口总表 —— 语法手册、语法对照、编译手册、互操作、项目格式、标准库 API、ARCHITECTURE
- 开发文档（路线图/规划/归档）：[`docs-dev/README.md`](docs-dev/README.md) 分类索引 —— [开发计划](docs-dev/开发计划.md)、[自举缺口分析](docs-dev/plan/自举缺口分析.md)、[语义债务清单](docs-dev/plan/语义债务清单.md)、[ADR 决策索引](docs-dev/README.md#4-adr--架构决策记录索引)
- 新手：[快速上手](docs/快速上手.md) → [示例集](samples/README.md)
- 变更流水：[CHANGELOG.md](CHANGELOG.md)

## 参考实现

- [minsk](https://github.com/terrajobst/minsk) — Immo Landwerth 的教学编译器
- [YouTube 系列](https://www.youtube.com/playlist?list=PLRAdsfhKI4OWNOSfS7EUu5GRAVmze1t2y)
- TinyCC / MinGW-w64 — 阶段 0 黑盒对照参照（`objdump` 反汇编对比）
