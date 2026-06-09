import * as vscode from 'vscode';
import { SymbolTable } from './symbolTable';

export class CocoaDefinitionProvider implements vscode.DefinitionProvider {
  constructor(private symbolTable: SymbolTable) {}

  provideDefinition(
    document: vscode.TextDocument,
    position: vscode.Position,
    _token: vscode.CancellationToken
  ): vscode.ProviderResult<vscode.Definition | vscode.LocationLink[]> {
    const range = document.getWordRangeAtPosition(position);
    if (!range) return undefined;

    const word = document.getText(range);
    if (!word) return undefined;

    return this.symbolTable.findDeclaration(document, word);
  }
}
