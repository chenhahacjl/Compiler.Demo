# Cocoa 编译器

基于 C# 13 (.NET 9.0) 的方言编译器，支持完整 .NET 互操作。当前版本 **v0.1**（Evaluator 解释执行）。

## 快速开始

```bash
# 构建
src\build.cmd

# 编译单文件
coc hello.co

# 编译项目
coc MyApp.coproj
```

## 文档

| 文档 | 说明 |
|------|------|
| [`docs/语法手册.md`](docs/语法手册.md) | Cocoa 语言完整语法参考（35 章，C# 13 基准） |
| [`docs/编译手册.md`](docs/编译手册.md) | 编译器使用手册（`.coproj`/`.cosln` 格式、命令行、REPL） |
| [`docs/开发计划.md`](docs/开发计划.md) | 版本路线图（v0.1→v3.10, C# 1.0→13 全特性） |
| [`docs/实现目标.md`](docs/实现目标.md) | 详细设计（架构、代码模式、项目系统、Phase 1-6 设计） |

## 参考实现

- [minsk](https://github.com/terrajobst/minsk) — Immo Landwerth 的教学编译器
- [YouTube 系列](https://www.youtube.com/playlist?list=PLRAdsfhKI4OWNOSfS7EUu5GRAVmze1t2y)
