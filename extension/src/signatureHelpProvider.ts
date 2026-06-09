import * as vscode from 'vscode';
import { SymbolTable } from './symbolTable';

export class CocoaSignatureHelpProvider implements vscode.SignatureHelpProvider {
  constructor(private symbolTable: SymbolTable) {}

  provideSignatureHelp(
    document: vscode.TextDocument,
    position: vscode.Position,
    _token: vscode.CancellationToken
  ): vscode.ProviderResult<vscode.SignatureHelp> {
    if (this.symbolTable.isInSkipRange(document, position)) {
      return undefined;
    }

    const text = document.getText();
    const offset = document.offsetAt(position);

    let parenDepth = 0;
    let commaCount = 0;
    let funcName: string | undefined;

    for (let i = offset - 1; i >= 0; i--) {
      const ch = text[i];
      if (ch === ')') {
        parenDepth++;
      } else if (ch === '(') {
        if (parenDepth === 0) {
          let j = i - 1;
          while (j >= 0 && /[a-zA-Z_]/.test(text[j])) j--;
          funcName = text.substring(j + 1, i);
          break;
        }
        parenDepth--;
      } else if (ch === ',' && parenDepth === 0) {
        commaCount++;
      }
    }

    if (!funcName) return undefined;

    const params = this.symbolTable.getParameters(funcName);
    const sig = this.symbolTable.getSignature(funcName);
    if (!params || !sig) return undefined;

    const sigInfo = new vscode.SignatureInformation(sig);
    sigInfo.parameters = params.map(p => new vscode.ParameterInformation(`${p.name}: ${p.type}`));

    const help = new vscode.SignatureHelp();
    help.signatures = [sigInfo];
    help.activeSignature = 0;
    help.activeParameter = Math.min(commaCount, Math.max(0, params.length - 1));
    return help;
  }
}
