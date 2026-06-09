import * as vscode from 'vscode';
import { SymbolTable, CocoaSymbol } from './symbolTable';

export class CocoaDocumentSymbolProvider implements vscode.DocumentSymbolProvider {
  constructor(private symbolTable: SymbolTable) {}

  provideDocumentSymbols(
    document: vscode.TextDocument,
    _token: vscode.CancellationToken
  ): vscode.ProviderResult<vscode.DocumentSymbol[]> {
    const symbols = this.symbolTable.getAllSymbols(document);
    const text = document.getText();

    return symbols.map(s => {
      const kind = s.kind === 'function'
        ? vscode.SymbolKind.Function
        : vscode.SymbolKind.Variable;
      const selRange = s.declarationRange;
      let fullRange = selRange;

      if (s.kind === 'function') {
        const endLine = this.findFunctionEnd(text, s);
        fullRange = new vscode.Range(
          selRange.start.line, 0,
          endLine, 0
        );
      }

      return new vscode.DocumentSymbol(s.name, s.type, kind, fullRange, selRange);
    });
  }

  private findFunctionEnd(text: string, symbol: CocoaSymbol): number {
    const startOffset = this.getOffsetAtLine(text, symbol.declarationRange.start.line);
    let depth = 0;
    let inBlock = false;

    for (let i = startOffset; i < text.length; i++) {
      const ch = text[i];

      if (ch === '"') {
        i++;
        while (i < text.length) {
          if (text[i] === '"') {
            if (i + 1 < text.length && text[i + 1] === '"') {
              i += 2;
              continue;
            }
            break;
          }
          i++;
        }
        continue;
      }

      if (ch === '/') {
        if (i + 1 < text.length) {
          if (text[i + 1] === '/') {
            while (i < text.length && text[i] !== '\n') i++;
            continue;
          }
          if (text[i + 1] === '*') {
            i += 2;
            while (i < text.length && !(text[i] === '*' && i + 1 < text.length && text[i + 1] === '/')) i++;
            i++;
            continue;
          }
        }
      }

      if (ch === '{') {
        inBlock = true;
        depth++;
      } else if (ch === '}') {
        depth--;
        if (inBlock && depth === 0) {
          const sub = text.substring(0, i);
          return sub.split('\n').length - 1;
        }
      }
    }

    return symbol.declarationRange.start.line;
  }

  private getOffsetAtLine(text: string, line: number): number {
    let ln = 0;
    for (let i = 0; i < text.length; i++) {
      if (ln === line) return i;
      if (text[i] === '\n') ln++;
    }
    return 0;
  }
}
