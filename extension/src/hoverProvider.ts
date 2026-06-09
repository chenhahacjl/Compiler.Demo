import * as vscode from 'vscode';
import { SymbolTable } from './symbolTable';

export class CocoaHoverProvider implements vscode.HoverProvider {
  constructor(private symbolTable: SymbolTable) {}

  provideHover(
    document: vscode.TextDocument,
    position: vscode.Position,
    _token: vscode.CancellationToken
  ): vscode.ProviderResult<vscode.Hover> {
    const range = document.getWordRangeAtPosition(position);
    if (!range) return undefined;

    const word = document.getText(range);
    if (!word) return undefined;

    const symbol = this.symbolTable.getSymbol(word, position);

    if (symbol && symbol.kind !== 'builtin') {
      return this.buildUserHover(symbol);
    }

    const builtin = this.symbolTable.getBuiltinInfo(word);
    if (builtin) {
      const md = new vscode.MarkdownString(
        `**${word}** ⚡ 内置函数\n\n\`${builtin.signature}\`\n\n${builtin.description}`
      );
      return new vscode.Hover(md, range);
    }

    const kwDoc = this.symbolTable.getKeywordDoc(word);
    if (kwDoc) {
      const md = new vscode.MarkdownString(`**${word}** ⚡ 关键字\n\n${kwDoc}`);
      return new vscode.Hover(md, range);
    }

    return undefined;
  }

  private buildUserHover(symbol: { name: string; kind: string; type: string; declarationRange: vscode.Range }): vscode.Hover {
    const loc = symbol.declarationRange.start;
    if (symbol.kind === 'variable') {
      const md = new vscode.MarkdownString(
        `**${symbol.name}** ⚡ 变量\n\n\`${symbol.type}\`\n\n声明位置：第 ${loc.line + 1} 行，第 ${loc.character + 1} 列`
      );
      return new vscode.Hover(md);
    }
    const sig = this.symbolTable.getSignature(symbol.name);
    const md = new vscode.MarkdownString(
      `**${symbol.name}** ⚡ 函数\n\n\`${sig}\`\n\n声明位置：第 ${loc.line + 1} 行，第 ${loc.character + 1} 列`
    );
    return new vscode.Hover(md);
  }

}
