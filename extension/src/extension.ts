import * as vscode from 'vscode';
import { SymbolTable } from './symbolTable';
import { CocoaCompletionProvider } from './completionProvider';
import { CocoaHoverProvider } from './hoverProvider';
import { CocoaSignatureHelpProvider } from './signatureHelpProvider';
import { CocoaDefinitionProvider } from './definitionProvider';
import { CocoaReferenceProvider } from './referenceProvider';
import { CocoaDocumentSymbolProvider } from './documentSymbolProvider';

export function activate(context: vscode.ExtensionContext) {
  const symbolTable = new SymbolTable();
  let rebuildTimer: number | undefined;

  const rebuildNow = () => {
    const doc = vscode.window.activeTextEditor?.document;
    if (doc && doc.languageId === 'cocoa') {
      symbolTable.rebuild(doc);
    }
  };

  const debouncedRebuild = () => {
    if (rebuildTimer) clearTimeout(rebuildTimer);
    rebuildTimer = setTimeout(rebuildNow, 300);
  };

  rebuildNow();

  const autoRefreshTimer = setInterval(() => {
    const doc = vscode.window.activeTextEditor?.document;
    if (doc && doc.languageId === 'cocoa') {
      symbolTable.rebuild(doc);
    }
  }, 60000);

  context.subscriptions.push(
    new vscode.Disposable(() => clearInterval(autoRefreshTimer)),
    vscode.window.onDidChangeActiveTextEditor(() => rebuildNow()),
    vscode.workspace.onDidChangeTextDocument((e) => {
      if (e.document.languageId === 'cocoa') debouncedRebuild();
    }),
    vscode.workspace.onDidSaveTextDocument((doc) => {
      if (doc.languageId === 'cocoa') rebuildNow();
    }),
    vscode.languages.registerCompletionItemProvider(
      'cocoa',
      new CocoaCompletionProvider(symbolTable),
      ...'abcdefghijklmnopqrstuvwxyz'
    ),
    vscode.languages.registerHoverProvider('cocoa', new CocoaHoverProvider(symbolTable)),
    vscode.languages.registerSignatureHelpProvider(
      'cocoa',
      new CocoaSignatureHelpProvider(symbolTable),
      '(',
      ','
    ),
    vscode.languages.registerDefinitionProvider('cocoa', new CocoaDefinitionProvider(symbolTable)),
    vscode.languages.registerReferenceProvider('cocoa', new CocoaReferenceProvider(symbolTable)),
    vscode.languages.registerDocumentSymbolProvider('cocoa', new CocoaDocumentSymbolProvider(symbolTable))
  );
}

export function deactivate() {
  // clean shutdown
}
