# Cocoa Language

VS Code 语言支持插件 —— Cocoa 编程语言

## 功能

| 功能 | 操作 |
|------|------|
| 语法高亮 | 关键字、类型、字符串、数字、注释、运算符 |
| 代码片段 | `fun` / `main` / `if` / `while` / `for` / `let` / `var` / `print` / `input` |
| 自动补全 | 关键字、内置函数（`print`、`input`、`random`）、用户变量/函数 |
| 悬停提示 | 关键字说明、内置函数签名、用户变量/函数类型和声明位置 |
| 转到定义 | F12 / Ctrl+点击跳转到变量或函数声明 |
| 查找引用 | Shift+F12 列出所有引用位置 |
| 签名帮助 | 输入 `(` 时显示函数参数签名 |
| 文档符号 | Ctrl+Shift+O 大纲视图显示函数和变量 |
| 代码折叠 | `//#region` / `//#endregion` 区域折叠 |

## 文件关联

- 扩展名：`.co`
- 语言 ID：`cocoa`

## 安装

在 VS Code 扩展面板中选择 `Install from VSIX...`，选择 `cocoa-language-*.vsix` 文件。

## 构建

```bash
# Windows
.\build-vsix.ps1

# Linux / macOS
chmod +x build-vsix.sh
./build-vsix.sh
```
