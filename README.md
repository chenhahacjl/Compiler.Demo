# Cocoa 编译器

用 C# 编写的 C 系方言编译器，同时具备 **Native 代码生成**（x86 / x64，零依赖、纯自研 PE 输出）与 **IL 代码生成**（ECMA-335）两条后端路径，最终目标是用 Cocoa 语言自身重写编译器（自举）。

> 当前阶段：阶段 6 — 语言扩展 + 互操作 + 输出格式 + 项目系统（见 [`docs/开发计划.md`](docs/开发计划.md)）
> 最新：6e-M10 C# 兼容语法 + 字段/自动属性初始化器已落地（2026-08-20）；6e-M11 规划中（顶层 C# 函数 / const / using 分号 / 类多接口 / 访问器修饰符 / 静态构造 / 表达式体 / 字符串插值 / foreach / switch，见 [`docs/语法手册.md`](docs/语法手册.md) §45）

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
# 构建（Windows）
src\build.cmd

# 编译单文件（默认 exe）
coc hello.co

# 编译仓库自带项目 / 解决方案样例（Tutorial：每功能块一个 exe，coproj 默认 dotnetRuntime = net40，直接运行）
coc build -p samples/Tutorial/Tutorial.cosln
coc build -p samples/Tutorial/HelloWorld/HelloWorld.coproj
./samples/Tutorial/HelloWorld/out/HelloWorld.exe

# C# 式语法对照（Tutorial/CsStyle：C# 式参数/局部变量/分号 ↔ Cocoa 式，native + dotnet 双后端）
coc build -p samples/Tutorial/CsStyle/CsStyle.coproj
./samples/Tutorial/CsStyle/out/CsStyle.exe

# C# 式类语法 + 字段/自动属性初始化器（samples/CSharpClass，仅 IL 后端）
coc build -p samples/CSharpClass/CSharpClass.coproj
./samples/CSharpClass/out/CSharpClass.exe

# 指定输出格式与 .NET 目标框架
coc build -p foo.coproj -f library
coc build -p foo.coproj --dotnet-runtime net8.0

# 类库（dll）→ 消费：写 `using MyLib` + 引用库
coc build -p mylib.coproj -f library
coc build -p app.coproj -r out/mylib.dll

# netfx：产出 .NET Framework 4.x 镜像，直接运行（无需 dotnet 前缀）
coc build -p foo.coproj --dotnet-runtime net40
./foo.exe
```

## 文档

| 文档 | 说明 |
|------|------|
| [`docs/语法手册.md`](docs/语法手册.md) | Cocoa 语言语法参考（状态标记：✅ 已实现 · 🔧 设计中 · 📋 待实现） |
| [`docs/编译手册.md`](docs/编译手册.md) | 编译器使用手册（`coc build` 子命令、构建选项、增量构建） |
| [`docs/类库设计.md`](docs/类库设计.md) | 类库体系设计（class/namespace/using/.NET dll/跨程序集消费/native 后置） |
| [`docs/OOP设计.md`](docs/OOP设计.md) | 完整 OOP 设计（继承/多态/static/属性/native 对象模型后置） |
| [`docs/项目格式规范.md`](docs/项目格式规范.md) | `.coproj` / `.cosln` 轻量文本格式规范、`.cod` 库格式、增量哈希 |
| [`docs/实现目标.md`](docs/实现目标.md) | 架构设计（Native / IR / IL 三路径、ABI、自举设计） |
| [`docs/开发计划.md`](docs/开发计划.md) | 阶段 0-9 路线图与里程碑 |
| [`docs/输出格式.md`](docs/输出格式.md) | exe / dll / cod 三种输出格式规范 |
| [`docs/互操作手册.md`](docs/互操作手册.md) | native DLL / .NET DLL / cod 导入与调用约定 |
| [`docs/IR设计.md`](docs/IR设计.md) | IR 指令集、虚拟寄存器、后端映射（阶段 1-3 落地时细化） |

## 参考实现

- [minsk](https://github.com/terrajobst/minsk) — Immo Landwerth 的教学编译器
- [YouTube 系列](https://www.youtube.com/playlist?list=PLRAdsfhKI4OWNOSfS7EUu5GRAVmze1t2y)
- TinyCC / MinGW-w64 — 阶段 0 黑盒对照参照（`objdump` 反汇编对比）
