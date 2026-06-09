import * as vscode from 'vscode';
import { SymbolTable } from './symbolTable';

const KEYWORD_ITEMS: vscode.CompletionItem[] = [
  ['break', 'break – 退出当前循环'],
  ['continue', 'continue – 跳过本次循环剩余部分'],
  ['do', 'do – do-while 循环体开始'],
  ['else', 'else – if 语句的分支'],
  ['false', 'false – 布尔假值'],
  ['for', 'for – for 循环：for i = 1 to 10 { ... }'],
  ['function', 'function – 声明函数'],
  ['if', 'if – 条件语句'],
  ['let', 'let – 声明只读变量'],
  ['return', 'return – 从函数返回值'],
  ['to', 'to – for 循环的范围分隔符'],
  ['true', 'true – 布尔真值'],
  ['var', 'var – 声明可变变量'],
  ['while', 'while – while 循环'],
].map(([k, d], i) => {
  const item = new vscode.CompletionItem(k, vscode.CompletionItemKind.Keyword);
  item.detail = d;
  item.sortText = String.fromCharCode(97 + Math.min(i, 25));
  return item;
});

const TYPE_ITEMS: vscode.CompletionItem[] = [
  ['int', 'int – 32位整数类型'],
  ['bool', 'bool – 布尔类型'],
  ['string', 'string – 字符串类型'],
  ['any', 'any – 任意类型（顶级类型）'],
  ['void', 'void – 无返回值类型'],
].map(([t, d]) => {
  const item = new vscode.CompletionItem(t, vscode.CompletionItemKind.TypeParameter);
  item.detail = d;
  item.sortText = '0';
  return item;
});

const FUNC_ITEMS: vscode.CompletionItem[] = [
  { label: 'print', insert: 'print(${1:text})', detail: 'void print(text: string) – 打印字符串到控制台' },
  { label: 'input', insert: 'input()', detail: 'string input() – 从控制台读取一行输入' },
  { label: 'random', insert: 'random(${1:max})', detail: 'int random(max: int) – 返回 0~max 的随机整数' },
].map(({ label, insert, detail }) => {
  const item = new vscode.CompletionItem(label, vscode.CompletionItemKind.Function);
  item.detail = detail;
  item.insertText = new vscode.SnippetString(insert);
  item.sortText = '00';
  item.commitCharacters = ['('];
  return item;
});

export class CocoaCompletionProvider implements vscode.CompletionItemProvider {
  constructor(private symbolTable: SymbolTable) {}

  provideCompletionItems(
    document: vscode.TextDocument,
    position: vscode.Position,
    _token: vscode.CancellationToken,
    _context: vscode.CompletionContext
  ): vscode.ProviderResult<vscode.CompletionItem[] | vscode.CompletionList> {
    if (this.symbolTable.isInSkipRange(document, position)) {
      return [];
    }
    const linePrefix = document.lineAt(position.line).text.substring(0, position.character);

    if (/:\s*[\w]*$/.test(linePrefix)) {
      return [...TYPE_ITEMS];
    }

    const lineNonBlank = linePrefix.trimEnd();
    const afterLetOrVar = /\b(let|var)\s+\w*$/i.test(lineNonBlank);
    if (afterLetOrVar) {
      return [...TYPE_ITEMS, ...FUNC_ITEMS];
    }

    const userSyms = this.symbolTable.getUserSymbolInfos(document).map(s => {
      const kind = s.kind === 'function' ? vscode.CompletionItemKind.Function : vscode.CompletionItemKind.Variable;
      const item = new vscode.CompletionItem(s.name, kind);
      item.detail = s.detail;
      item.sortText = '01';
      return item;
    });

    return [...KEYWORD_ITEMS, ...TYPE_ITEMS, ...FUNC_ITEMS, ...userSyms];
  }
}
