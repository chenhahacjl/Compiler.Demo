# Cocoa Language Extension — Design Document

## 1. Overview

Cocoa Language is a Visual Studio Code extension providing rich language support for the Cocoa programming language (`.co` files). It delivers syntax highlighting, code snippets, intelligent completions, hover information, go-to-definition, find references, signature help, document symbols, and code folding — all without an external language server or compiler process.

**Key decisions:**

- **No language server**: All features run in-process via VS Code provider APIs. This eliminates the need for a separate compiler executable (e.g., `coc.exe`) and avoids process-management complexity.
- **Single-pass symbol table**: A combined regex approach scans declarations and references in two linear passes, with caching by document version to avoid redundant work.
- **Chinese descriptions**: All user-facing descriptions (keywords, builtins, types, hover text) are localized in Chinese.

## 2. Language Specification

### 2.1 File Extension and Language ID

| Property | Value |
|----------|-------|
| Language ID | `cocoa` |
| File Extension | `.co` |
| Grammar Scope | `source.cocoa` |
| Activation | `onLanguage:cocoa` |

### 2.2 Keywords (14)

| Category | Tokens |
|----------|--------|
| Control | `if`, `else`, `while`, `do`, `for`, `break`, `continue`, `return` |
| Declaration | `let`, `var`, `function` |
| Literal | `true`, `false` |
| Operator | `to` |

### 2.3 Types (5)

`int`, `bool`, `string`, `any`, `void`

### 2.4 Built-in Functions (3)

| Name | Signature | Description |
|------|-----------|-------------|
| `print` | `void print(text: string)` | 将字符串打印到控制台 |
| `input` | `string input()` | 从控制台读取一行用户输入 |
| `random` | `int random()` | 返回一个随机整数 |

### 2.5 Operators

| Category | Operators |
|----------|-----------|
| Assignment | `=` |
| Comparison | `==`, `!=`, `<`, `>`, `<=`, `>=` |
| Logical | `&&`, `\|\|`, `!` |
| Arithmetic | `+`, `-`, `*`, `/` |
| Bitwise | `&`, `\|`, `^`, `~` |

### 2.6 Comments

- Line: `//` to end of line
- Block: `/* ... */` (nesting not supported)

### 2.7 Strings

- Double-quoted strings with `""` as escape sequence for literal double quotes.

## 3. Extension Architecture

```
extension/
├── package.json                  # Extension manifest, contributes
├── language-configuration.json   # Brackets, comments, folding, indentation
├── tsconfig.json                 # TypeScript compiler configuration
├── .vscodeignore                 # Packaging exclusions
├── .gitignore                    # Version control exclusions
├── LICENSE                       # License file
├── icon.png                      # Extension icon (128×128)
├── README.md                     # Marketplace documentation
├── build-vsix.ps1                # Windows packaging script
├── build-vsix.sh                 # Linux/macOS packaging script
├── syntaxes/
│   └── cocoa.tmLanguage.json     # TextMate grammar (8 categories)
├── snippets/
│   └── cocoa.code-snippets.json  # 11 code snippets
├── src/
│   ├── extension.ts              # Entry point, provider registration
│   ├── symbolTable.ts            # Core symbol extraction and caching
│   ├── completionProvider.ts     # Completion items (keyword/type/func/user)
│   ├── hoverProvider.ts          # Hover information
│   ├── signatureHelpProvider.ts  # Parameter signature popup
│   ├── definitionProvider.ts     # Go-to-definition (F12)
│   ├── referenceProvider.ts      # Find all references (Shift+F12)
│   └── documentSymbolProvider.ts # Outline symbols (Ctrl+Shift+O)
└── out/                          # Compiled JS output
```

### 3.1 Dependency Graph

```
extension.ts
  └── symbolTable.ts
       ├── completionProvider.ts
       ├── hoverProvider.ts
       ├── signatureHelpProvider.ts
       ├── definitionProvider.ts
       ├── referenceProvider.ts
       └── documentSymbolProvider.ts
```

No external runtime dependencies. Dev-only: `typescript` and `@types/vscode`.

## 4. Core Component: SymbolTable

`src/symbolTable.ts` is the central data structure. It:

- Maintains a `Map<string, CocoaSymbol[]>` of all symbols (builtins, user variables, user functions).
- Caches skip ranges (comments, strings) by document version to avoid repeated scanning.
- Caches user symbol info for completions by document version.
- Precomputes line offset array for O(log n) offset-to-position resolution (binary search).

### 4.1 Symbol Lifecycle

```
document open/change/save
        │
        ▼
rebuild(document)
        │
        ├── clear symbols map
        ├── compute line offsets (O(n))
        ├── inject builtins
        ├── findSkipRanges(text) — single regex pass (O(n))
        ├── scanDeclarations(text, skip) — one regex pass (O(n))
        └── scanReferences(text, skip) — one regex pass (O(n))
```

### 4.2 Declaration Scanning

A single combined regex matches all declaration forms in one pass:

```
\b(let|var)\s+(\w+)(?:\s*:\s*(\w+))?\s*=
\bfunction\s+(\w+)\s*\(([^)]*)\)(?:\s*:\s*(\w+))?
\bfor\s+(\w+)\s*=
```

### 4.3 Reference Scanning

A dynamically-built combined regex matches all user symbol names in one pass:

```
\b(name1|name2|name3|...)\b
```

Each match is assigned to the nearest declaration with the same name (by line distance). The declaration position itself is skipped.

### 4.4 Symbol Table Caching Strategy

| Cache | Key | Invalidated When |
|-------|-----|------------------|
| Skip ranges | `document.version` | Document changes |
| User symbol infos | `document.version` | Document changes |
| Line offsets | N/A (rebuilt each rebuild) | Document changes |

### 4.5 Rebuild Triggers

| Trigger | Behavior |
|---------|----------|
| Active editor change | Immediate full rebuild |
| Document change (typing) | Debounced 300 ms |
| Document save | Immediate full rebuild |
| Periodic timer | Every 60 seconds |

## 5. Feature Providers

### 5.1 Completion (`CocoaCompletionProvider`)

- **Trigger characters**: All `a-z` letters
- **Items**: 14 keywords, 5 types, 3 builtin functions (with snippet insert), user symbols
- **Context awareness**:
  - Inside string/comment → empty result
  - After `:` → types only
  - After `let`/`var` → types + functions (no keywords)
- **Sorting**: Types (`sortText: '0'`) → functions (`'00'`) → user symbols (`'01'`) → keywords (`'a'`-`'n'`)
- **Builtin functions**: Insert as snippets (`print(${1:text})`, `input()`, `random()`) with `(` as commit character

### 5.2 Hover (`CocoaHoverProvider`)

- **User variables**: Shows type and declaration line/column in Chinese
- **User functions**: Shows signature and declaration location in Chinese
- **Builtin functions**: Shows signature and description in Chinese
- **Keywords**: Shows description in Chinese
- **Priority**: User symbols > builtins > keywords

### 5.3 Signature Help (`CocoaSignatureHelpProvider`)

- **Trigger characters**: `(`, `,`
- **Mechanism**: Walks backward from cursor to find enclosing `()` pair, extracts function name, counts commas to determine active parameter
- **Guarding**: Skip when cursor is inside string/comment

### 5.4 Definition (`CocoaDefinitionProvider`)

- **User symbols**: Returns declaration `Range` (F12 jumps to definition)
- **Builtins**: Finds first occurrence of `name(` in document (skipping comments/strings)
- **Word boundary**: Uses VS Code's `getWordRangeAtPosition` for exact token matching

### 5.5 References (`CocoaReferenceProvider`)

- **User symbols**: Returns declaration range + all reference ranges
- **Builtins**: Finds all `name(` occurrences (skipping comments/strings)

### 5.6 Document Symbols (`CocoaDocumentSymbolProvider`)

- Functions appear first (sorted by `SymbolKind`), then variables
- Within same kind, sorted by declaration line
- Function symbols include full body range (from `function` keyword to matching `}` at depth 0), handling strings and comments inside body
- Variable symbols use declaration range only

## 6. Syntax Grammar

`cocoa.tmLanguage.json` defines 8 pattern categories evaluated in order:

| Order | Category | Scope |
|-------|----------|-------|
| 1 | Comments (line + block) | `comment.line.double-slash.cocoa`, `comment.block.cocoa` |
| 2 | Strings | `string.quoted.double.cocoa` |
| 3 | Keywords (control → declaration → literal → operator) | `keyword.*.cocoa` |
| 4 | Types | `storage.type.cocoa` |
| 5 | Builtin functions | `support.function.cocoa` |
| 6 | Operators (comparison → logical → assignment → arithmetic → bitwise) | `keyword.operator.*.cocoa` |
| 7 | Numbers | `constant.numeric.cocoa` |

Builtin functions use a lookahead `(?=\s*\()` to match only when followed by `(`.

## 7. Code Snippets

11 snippets with 4-space indentation:

| Prefix | Description |
|--------|-------------|
| `main` | Create `main()` function skeleton |
| `fun` | Create a named function |
| `if` | If statement |
| `ifelse` | If-else statement |
| `while` | While loop |
| `dowhile` | Do-while loop |
| `for` | For loop with `i = start to end` |
| `let` | Immutable variable declaration |
| `var` | Mutable variable declaration |
| `print` | `print("text")` |
| `input` | `let name = input()` |

## 8. Language Configuration

`language-configuration.json` provides:

| Feature | Configuration |
|---------|---------------|
| Comments | `//` line, `/* */` block |
| Brackets | `{}`, `()` |
| Auto-closing | `{ }`, `( )`, `" "` (not in string) |
| Surrounding | `( )`, `" "` |
| Indentation | Increase on `{`, decrease on `}` |
| Folding markers | `//#region` / `//#endregion` |

## 9. Performance Considerations

| Strategy | Details |
|----------|---------|
| **Combined regex** | Single pass for all declarations, single pass for all references — avoids per-symbol loops |
| **Line offset array** | Binary search (O(log n)) for position lookups instead of linear scan |
| **Skip range caching** | `findSkipRanges` is called at most once per document version |
| **User symbol caching** | `getUserSymbolInfos` returns cached data if version unchanged |
| **Debounced rebuild** | Typing triggers rebuild after 300 ms silence, not on every keystroke |
| **no LS** | In-process execution avoids IPC overhead of a language server protocol |

## 10. Packaging and Build

- **Compiler**: TypeScript `^5.3.0`, target `es2020`, module `commonjs`
- **Packaging**: `@vscode/vsce` via `npx -y @vscode/vsce package`
- **Pre-publish**: `tsc -p ./` runs automatically
- **Output**: `cocoa-language-{version}.vsix` (25 files, ~27 KB)
- **Excluded from vsix**: TypeScript sources, `node_modules/`, `out/`, `*.vsix`, `.vscode/`, `.gitignore`, build scripts

## 11. Development

- **F5 debug**: `.vscode/launch.json` launches Extension Development Host with `out/` compiled
- **Watch mode**: `tsc -watch -p ./` for incremental compilation
- **One-click build**: `build-vsix.ps1` (PowerShell) or `build-vsix.sh` (bash)
