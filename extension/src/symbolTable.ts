import * as vscode from 'vscode';

export interface Parameter {
  name: string;
  type: string;
}

export interface CocoaSymbol {
  name: string;
  kind: 'variable' | 'function' | 'builtin';
  type: string;
  declarationRange: vscode.Range;
  references: vscode.Range[];
  parameters?: Parameter[];
  description?: string;
}

interface SkipRange {
  start: number;
  end: number;
}

interface BuiltinFunc {
  name: string;
  signature: string;
  description: string;
  parameters: Parameter[];
  returnType: string;
}

const BUILTINS: BuiltinFunc[] = [
  {
    name: 'print',
    signature: 'void print(text: string)',
    description: '将字符串打印到控制台',
    parameters: [{ name: 'text', type: 'string' }],
    returnType: 'void',
  },
  {
    name: 'input',
    signature: 'string input()',
    description: '从控制台读取一行用户输入',
    parameters: [],
    returnType: 'string',
  },
  {
    name: 'random',
    signature: 'int random(max: int)',
    description: '返回一个 0 到 max（含）之间的随机整数',
    parameters: [{ name: 'max', type: 'int' }],
    returnType: 'int',
  },
];

const KEYWORDS = new Set([
  'break', 'continue', 'do', 'else', 'false', 'for', 'function',
  'if', 'let', 'return', 'to', 'true', 'var', 'while',
]);

const TYPES = new Set(['int', 'bool', 'string', 'any', 'void']);

export class SymbolTable {
  private symbols: Map<string, CocoaSymbol[]> = new Map();
  private lineOffsets: number[] = [];
  private skipCache: { version: number; ranges: SkipRange[] } | undefined;

  rebuild(document: vscode.TextDocument): void {
    try {
      this.symbols.clear();
      this.skipCache = undefined;
      this.userSymsCache = undefined;
      const text = document.getText();
      this.lineOffsets = this.computeLineOffsets(text);

      for (const b of BUILTINS) {
        this.add({
          name: b.name,
          kind: 'builtin',
          type: b.returnType,
          declarationRange: new vscode.Range(0, 0, 0, 0),
          references: [],
          parameters: b.parameters,
          description: b.description,
        });
      }

      const skipRanges = this.findSkipRanges(text);
      this.scanDeclarations(text, skipRanges);
      this.scanReferences(text, skipRanges);
    } catch (e) {
      console.error('[Cocoa] SymbolTable rebuild error:', e);
    }
  }

  private computeLineOffsets(text: string): number[] {
    const offsets: number[] = [0];
    for (let i = 0; i < text.length; i++) {
      if (text[i] === '\n') offsets.push(i + 1);
    }
    return offsets;
  }

  private lineAt(pos: number): number {
    let lo = 0;
    let hi = this.lineOffsets.length - 1;
    while (lo < hi) {
      const mid = Math.floor((lo + hi + 1) / 2);
      if (this.lineOffsets[mid] <= pos) lo = mid;
      else hi = mid - 1;
    }
    return lo;
  }

  private colAt(pos: number): number {
    return pos - this.lineOffsets[this.lineAt(pos)];
  }

  private add(s: CocoaSymbol): void {
    const arr = this.symbols.get(s.name) || [];
    arr.push(s);
    this.symbols.set(s.name, arr);
  }

  private findSkipRanges(text: string): SkipRange[] {
    const ranges: SkipRange[] = [];
    const combinedRe = /\/\*[\s\S]*?\*\/|\/\/[^\n]*|"[^"]*(""[^"]*)*"/g;
    let m: RegExpExecArray | null;
    while ((m = combinedRe.exec(text)) !== null) {
      ranges.push({ start: m.index, end: m.index + m[0].length });
    }
    return ranges;
  }

  private inRange(pos: number, ranges: SkipRange[]): boolean {
    return ranges.some(r => pos >= r.start && pos < r.end);
  }

  private scanDeclarations(text: string, skip: SkipRange[]): void {
    const combinedRe = /(?:\b(let|var)\s+(\w+)(?:\s*:\s*(\w+))?\s*=|\bfunction\s+(\w+)\s*\(([^)]*)\)(?:\s*:\s*(\w+))?|\bfor\s+(\w+)\s*=)/g;
    let m: RegExpExecArray | null;
    while ((m = combinedRe.exec(text)) !== null) {
      if (this.inRange(m.index, skip)) continue;
      const line = this.lineAt(m.index);
      const col = this.colAt(m.index);

      if (m[1]) {
        this.add({
          name: m[2],
          kind: 'variable',
          type: m[3] || 'inferred',
          declarationRange: new vscode.Range(line, col, line, col + m[2].length),
          references: [],
        });
      } else if (m[4]) {
        const rawParams = m[5];
        const returnType = m[6] || 'void';
        const params: Parameter[] = [];
        if (rawParams.trim()) {
          for (const p of rawParams.split(',')) {
            const parts = p.trim().match(/(\w+)\s*:\s*(\w+)/);
            if (parts) params.push({ name: parts[1], type: parts[2] });
          }
        }
        this.add({
          name: m[4],
          kind: 'function',
          type: returnType,
          declarationRange: new vscode.Range(line, col, line, col + m[4].length),
          references: [],
          parameters: params,
        });
      } else if (m[7]) {
        this.add({
          name: m[7],
          kind: 'variable',
          type: 'int',
          declarationRange: new vscode.Range(line, col, line, col + m[7].length),
          references: [],
        });
      }
    }
  }

  private scanReferences(text: string, skip: SkipRange[]): void {
    const nameSymbols = new Map<string, CocoaSymbol[]>();
    for (const [name, syms] of this.symbols) {
      if (KEYWORDS.has(name) || TYPES.has(name)) continue;
      const userSyms = syms.filter(s => s.kind !== 'builtin');
      if (userSyms.length > 0) nameSymbols.set(name, userSyms);
    }
    if (nameSymbols.size === 0) return;

    const sortedNames = [...nameSymbols.keys()].sort((a, b) => b.length - a.length);
    const combinedRe = new RegExp(`\\b(${sortedNames.map(n => this.esc(n)).join('|')})\\b`, 'g');

    let m: RegExpExecArray | null;
    while ((m = combinedRe.exec(text)) !== null) {
      if (this.inRange(m.index, skip)) continue;

      const matchedName = m[1];
      const line = this.lineAt(m.index);
      const col = this.colAt(m.index);
      const range = new vscode.Range(line, col, line, col + matchedName.length);
      const userSyms = nameSymbols.get(matchedName)!;

      let closest = userSyms[0];
      let bestDist = Math.abs(closest.declarationRange.start.line - line);
      for (let i = 1; i < userSyms.length; i++) {
        const d = Math.abs(userSyms[i].declarationRange.start.line - line);
        if (d < bestDist) { bestDist = d; closest = userSyms[i]; }
      }

      if (!this.isSamePos(closest.declarationRange.start, range.start)) {
        closest.references.push(range);
      }
    }
  }

  private esc(s: string): string {
    return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  }

  private isSamePos(a: vscode.Position, b: vscode.Position): boolean {
    return a.line === b.line && a.character === b.character;
  }

  getSymbol(name: string, position?: vscode.Position): CocoaSymbol | undefined {
    const arr = this.symbols.get(name);
    if (!arr || arr.length === 0) return undefined;
    if (arr.length === 1) return arr[0];
    if (position) {
      let best = arr[0];
      let bestDist = Infinity;
      for (const s of arr) {
        const d = Math.abs(s.declarationRange.start.line - position.line);
        if (d < bestDist) { bestDist = d; best = s; }
      }
      return best;
    }
    return arr[arr.length - 1];
  }

  findDeclaration(document: vscode.TextDocument, name: string): vscode.Location | undefined {
    const arr = this.symbols.get(name);
    if (!arr || arr.length === 0) return undefined;

    if (arr[0].kind === 'builtin') {
      return this.findBuiltinOccurrence(document, name);
    }

    return new vscode.Location(document.uri, arr[0].declarationRange);
  }

  private findBuiltinOccurrence(document: vscode.TextDocument, name: string): vscode.Location | undefined {
    const text = document.getText();
    const skip = this.findSkipRanges(text);
    const re = new RegExp(`\\b${this.esc(name)}\\b(?=\\s*\\()`, 'g');
    let m: RegExpExecArray | null;
    while ((m = re.exec(text)) !== null) {
      if (this.inRange(m.index, skip)) continue;
      const line = this.lineAt(m.index);
      const col = this.colAt(m.index);
      return new vscode.Location(document.uri, new vscode.Range(line, col, line, col + name.length));
    }
    return undefined;
  }

  findAllReferences(document: vscode.TextDocument, name: string): vscode.Location[] {
    const locs: vscode.Location[] = [];
    const arr = this.symbols.get(name);
    if (!arr) return locs;

    const text = document.getText();
    const isBuiltinOnly = arr.every(s => s.kind === 'builtin');

    if (isBuiltinOnly) {
      const skip = this.findSkipRanges(text);
      const re = new RegExp(`\\b${this.esc(name)}\\b(?=\\s*\\()`, 'g');
      let m: RegExpExecArray | null;
      while ((m = re.exec(text)) !== null) {
        if (this.inRange(m.index, skip)) continue;
        const line = this.lineAt(m.index);
        const col = this.colAt(m.index);
        locs.push(new vscode.Location(document.uri, new vscode.Position(line, col)));
      }
      return locs;
    }

    for (const s of arr) {
      locs.push(new vscode.Location(document.uri, s.declarationRange));
      for (const r of s.references) {
        locs.push(new vscode.Location(document.uri, r));
      }
    }
    return locs;
  }

  getAllSymbols(document: vscode.TextDocument): CocoaSymbol[] {
    const result: CocoaSymbol[] = [];
    for (const [, arr] of this.symbols) {
      for (const s of arr) {
        if (s.kind !== 'builtin') result.push(s);
      }
    }
    result.sort((a, b) => {
      const ka = a.kind === 'function' ? 0 : 1;
      const kb = b.kind === 'function' ? 0 : 1;
      if (ka !== kb) return ka - kb;
      return a.declarationRange.start.line - b.declarationRange.start.line;
    });
    return result;
  }

  getSignature(name: string): string | undefined {
    const arr = this.symbols.get(name);
    if (!arr || arr.length === 0) return undefined;
    const s = arr[0];
    if (s.kind === 'builtin') return BUILTINS.find(b => b.name === name)?.signature;
    if (s.kind === 'function' && s.parameters) {
      const params = s.parameters.map(p => `${p.name}: ${p.type}`).join(', ');
      return `${s.type} ${name}(${params})`;
    }
    return undefined;
  }

  getParameters(name: string): Parameter[] | undefined {
    const arr = this.symbols.get(name);
    if (!arr || arr.length === 0) return undefined;
    return arr[0].parameters;
  }

  getBuiltinInfo(name: string): { signature: string; description: string } | undefined {
    const b = BUILTINS.find(b => b.name === name);
    if (!b) return undefined;
    return { signature: b.signature, description: b.description };
  }

  isBuiltin(name: string): boolean {
    const arr = this.symbols.get(name);
    return arr !== undefined && arr.length > 0 && arr[0].kind === 'builtin';
  }

  isKeyword(name: string): boolean {
    return KEYWORDS.has(name);
  }

  getKeywordDoc(name: string): string | undefined {
    const docs: Record<string, string> = {
      break: '退出当前循环',
      continue: '跳到当前循环的下一次迭代',
      do: '开始 do-while 循环体',
      else: 'if 语句的否则分支',
      false: '布尔字面量 false',
      for: 'for 循环：for i = start to end { ... }',
      function: '声明一个函数',
      if: '条件判断语句',
      let: '声明一个不可变的（只读）变量',
      return: '从函数中返回一个值',
      to: 'for 循环中的范围分隔符',
      true: '布尔字面量 true',
      var: '声明一个可变的变量',
      while: 'while 循环：while condition { ... }',
    };
    return docs[name];
  }

  private userSymsCache: { version: number; data: { name: string; type: string; kind: string; detail: string }[] } | undefined;

  getUserSymbolInfos(document: vscode.TextDocument): { name: string; type: string; kind: string; detail: string }[] {
    if (this.userSymsCache && this.userSymsCache.version === document.version) {
      return this.userSymsCache.data;
    }
    const result: { name: string; type: string; kind: string; detail: string }[] = [];
    for (const [, arr] of this.symbols) {
      for (const s of arr) {
        if (s.kind === 'builtin') continue;
        if (s.kind === 'function') {
          const sig = this.getSignature(s.name) || s.name;
          result.push({ name: s.name, type: s.type, kind: 'function', detail: sig });
        } else {
          result.push({ name: s.name, type: s.type, kind: 'variable', detail: `${s.type} 变量` });
        }
      }
    }
    this.userSymsCache = { version: document.version, data: result };
    return result;
  }

  isInSkipRange(document: vscode.TextDocument, position: vscode.Position): boolean {
    const offset = document.offsetAt(position);
    if (!this.skipCache || this.skipCache.version !== document.version) {
      this.skipCache = {
        version: document.version,
        ranges: this.findSkipRanges(document.getText()),
      };
    }
    return this.inRange(offset, this.skipCache.ranges);
  }
}
