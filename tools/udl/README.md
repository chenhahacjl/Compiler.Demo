# Cocoa 语言 Notepad++ 高亮（UDL）

本目录提供 Cocoa 语言的 Notepad++ 用户自定义语言文件：`Cocoa-Notepad++.xml`（UDL 2.1 格式）。

## 导入步骤

1. 打开 Notepad++，菜单 **语言(Language) → 用户自定义语言(User Defined Language) → 定义(Define...)**
2. 点击 **导入(Import...)**，选择 `Cocoa-Notepad++.xml`
3. 重启 Notepad++（或在语言菜单底部手动选择 **Cocoa**）
4. 打开任意 `.co` 文件即可自动高亮

也可直接将 XML 复制到 `%AppData%\Notepad++\userDefineLangs\` 目录后重启。

## 覆盖范围

依据编译器语法实现生成（`SyntaxFacts.cs` / `Lexer.cs` / `BuiltinFunctions.cs`）：

| 类别 | 内容 | 颜色 |
|---|---|---|
| Keywords1 | 控制流与表达式关键字（`if` `while` `for i = 0 to n step k` `switch` `is` 等）| 蓝色粗体 |
| Keywords2 | 声明与修饰符（`function` `class` `property` `extends` `syscall` 等全部 60 个关键字）| 蓝色粗体 |
| Keywords3 | 内置类型名（CO 方言 `i32`/`f64` + C# 方言 `int`/`double` + 共享 `bool`/`string` 等）| 青色粗体 |
| Keywords4 | 常量 `true` / `false` / `null` | 紫色粗体 |
| Keywords5 | 内置函数（`WriteLine` `Sqrt` `ParseInt64` 等 21 个）| 棕金色 |
| 注释 | `//` 单行、`/* */` 多行，支持折叠 | 绿色 |
| 字符串 | `"..."`、插值 `$"..."`、Verbatim `@"..."`、字符 `'a'` | 暗红 |
| 数字 | 十进制 / 浮点 / 科学计数法 / `0xFF` 十六进制；后缀 `42L` `42u` `1.5f` `0xFFul` | 橙色 |
| 运算符 | 全部运算符与标点（含 `=>` `->` `<<=` 等，共 46 个）| 黑色 |
| 折叠 | `{ }` 代码块、多行注释 | — |

## 已知限制（Notepad++ UDL 本身的局限）

- 原始字符串 `"""..."""` 无法正确定界（内容中出现 `"` 会提前闭合高亮）
- 插值字符串 `$"{expr}"` 中 `{expr}` 表达式整体按字符串着色
- Verbatim 字符串的 `""` 转义引号会提前结束高亮
- 未关联 `.cs` 扩展名：与 Notepad++ 内置 C# 语言冲突；`.cs` 文件请用内置 C# 高亮

## 自定义颜色

菜单 **语言 → 用户自定义语言 → 定义**，选中 **Cocoa** 即可调整各分组颜色并另存。

## 同步维护

此文件为静态导出。若 `SyntaxFacts.cs` 新增关键字或 `BuiltinFunctions.cs` 变更内置函数，
需手动更新对应 `<Keywords name="KeywordsN">` 列表。
