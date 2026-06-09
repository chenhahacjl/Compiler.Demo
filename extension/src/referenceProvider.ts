import * as vscode from 'vscode';
import { SymbolTable } from './symbolTable';

export class CocoaReferenceProvider implements vscode.ReferenceProvider {
  constructor(private symbolTable: SymbolTable) {}

  provideReferences(
    document: vscode.TextDocument,
    position: vscode.Position,
    _context: vscode.ReferenceContext,
    _token: vscode.CancellationToken
  ): vscode.ProviderResult<vscode.Location[]> {
    const range = document.getWordRangeAtPosition(position);
    if (!range) return [];

    const word = document.getText(range);
    if (!word) return [];

    return this.symbolTable.findAllReferences(document, word);
  }
}
