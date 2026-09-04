using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Cocoa.CodeAnalysis;
using Cocoa.Cli.Repl.Authoring;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Text;

namespace Cocoa.Cli.Repl;

public sealed class ReplEngine : IDisposable
{
    private static readonly Dictionary<char, char> OpenToClose = new()
    {
        ['('] = ')',
        ['{'] = '}',
        ['['] = ']',
    };

    private static readonly Dictionary<char, char> CloseToOpen = new()
    {
        [')'] = '(',
        ['}'] = '{',
        [']'] = '[',
    };

    private readonly TerminalRenderer _renderer;
    private readonly InputEditor _input;
    private readonly CompletionPopup _popup;
    private readonly SignatureHint _signature;
    private readonly OutputHistory _output;
    private readonly StatusBar _status;
    private readonly HistoryManager _history;
    private readonly InputHandler _keyHandler;
    private readonly CocoaCompletionProvider _completionProvider;
    private readonly CompletionEngine _completion;
    private readonly DiagnosticsService _diagnostics;
    private readonly ReplSession _session;
    private readonly SubmissionStore _submissionStore;
    private readonly MetaCommandExecutor _metaCommands;

    private bool _running;
    private SyntaxTree? _liveTree;
    private Compilation? _liveCompilation;
    private int _diagnosticsVersion;
    private IReadOnlyList<Diagnostic> _currentDiagnostics = Array.Empty<Diagnostic>();

    public ReplEngine()
    {
        _renderer = new TerminalRenderer();
        _input = new InputEditor();
        _popup = new CompletionPopup();
        _signature = new SignatureHint();
        _output = new OutputHistory();
        _status = new StatusBar();
        _history = new HistoryManager();
        _keyHandler = new InputHandler();
        _completionProvider = new CocoaCompletionProvider();
        _completion = new CompletionEngine(_completionProvider);
        _diagnostics = new DiagnosticsService();
        _session = new ReplSession();
        _submissionStore = new SubmissionStore();
        _metaCommands = new MetaCommandExecutor(_session, _output, _submissionStore);

        SetupKeyBindings();
        LoadSubmissions();
    }

    private void SetupKeyBindings()
    {
        _keyHandler.Bind(ConsoleKey.Enter, HandleEnter);
        _keyHandler.Bind(ConsoleKey.Backspace, HandleBackspace);
        _keyHandler.Bind(ConsoleKey.Delete, HandleDelete);
        _keyHandler.Bind(ConsoleKey.LeftArrow, HandleLeft);
        _keyHandler.Bind(ConsoleKey.RightArrow, HandleRight);
        _keyHandler.Bind(ConsoleKey.UpArrow, HandleUp);
        _keyHandler.Bind(ConsoleKey.DownArrow, HandleDown);
        _keyHandler.Bind(ConsoleKey.Home, HandleHome);
        _keyHandler.Bind(ConsoleKey.End, HandleEnd);
        _keyHandler.Bind(ConsoleKey.Tab, HandleTab);
        _keyHandler.Bind(ConsoleKey.Escape, HandleEscape);
        _keyHandler.Bind(ConsoleKey.PageUp, HandlePageUp);
        _keyHandler.Bind(ConsoleKey.PageDown, HandlePageDown);

        _keyHandler.Bind(ConsoleModifiers.Control, ConsoleKey.Enter, HandleControlEnter);
        _keyHandler.Bind(ConsoleModifiers.Control, ConsoleKey.LeftArrow, HandleWordLeft);
        _keyHandler.Bind(ConsoleModifiers.Control, ConsoleKey.RightArrow, HandleWordRight);
        _keyHandler.Bind(ConsoleModifiers.Control, ConsoleKey.Backspace, HandleDeleteWord);
    }

    public void Run()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        _running = true;

        _output.AppendLine("Cocoa REPL — type #help for commands, #keys for keyboard shortcuts");
        _output.AppendLine("");
        RenderFrame();

        while (_running)
        {
            if (_diagnostics.TryTakePendingResult(out var diagVersion, out var pending)
                && diagVersion == _diagnosticsVersion)
            {
                OnDiagnosticsResult(pending!);
                RenderFrame();
            }

            // 轮询代替阻塞读取：后台诊断结果到达后 ~15ms 内即可上屏，无需等下一次按键
            bool hasKey;
            try { hasKey = Console.KeyAvailable; }
            catch { hasKey = true; }

            if (!hasKey)
            {
                Thread.Sleep(15);
                continue;
            }

            var key = Console.ReadKey(intercept: true);

            if (_popup.IsVisible)
            {
                switch (key.Key)
                {
                    case ConsoleKey.UpArrow: _popup.MoveUp(); RenderFrame(); continue;
                    case ConsoleKey.DownArrow: _popup.MoveDown(); RenderFrame(); continue;
                    case ConsoleKey.Tab:
                    case ConsoleKey.Enter: AcceptCompletion(); RenderFrame(); continue;
                    case ConsoleKey.Escape: _popup.Hide(); RenderFrame(); continue;
                }
            }

            if (key.Key == ConsoleKey.Escape) { HandleEscape(); RenderFrame(); continue; }

            if (key.Modifiers == default(ConsoleModifiers) && key.KeyChar >= ' ')
            {
                _input.InsertChar(key.KeyChar);
                OnTextChanged();
                RenderFrame();
                continue;
            }

            if (!_keyHandler.Handle(key) && key.KeyChar >= ' ')
            {
                _input.InsertChar(key.KeyChar);
                OnTextChanged();
            }

            RenderFrame();
        }
    }

    private void OnTextChanged()
    {
        var text = _input.Text;
        var cursor = ComputeCursorOffset();

        _liveTree = SyntaxTree.Parse(text);
        _liveCompilation = Compilation.CreateScript(
            _session.Previous,
            _session.References.Count > 0 ? _session.References.ToArray() : null,
            _liveTree);
        _completionProvider.SetLiveState(_liveTree, _liveCompilation);

        _diagnosticsVersion++;
        if (text.StartsWith("#"))
        {
            // 元命令不是代码：不做实时诊断，并清掉残留的行内诊断
            _currentDiagnostics = Array.Empty<Diagnostic>();
            _status.SetCenter("");
        }
        else
        {
            _diagnostics.RequestDiagnostics(_liveCompilation, _diagnosticsVersion);
        }

        if (text.Length > 0 && text.StartsWith("#") && cursor > 0)
        {
            ShowMetaCompletions(text);
            return;
        }

        if (text.Length > 0 && cursor > 0 &&
            (char.IsLetterOrDigit(text[cursor - 1]) || text[cursor - 1] == '.' || _completionProvider.IsUsingContext(text, cursor)))
        {
            _completion.Trigger(text, cursor);
            if (!_completion.IsVisible)
            {
                _popup.Hide();
            }
            else
            {
                // 前缀已与某候选完全一致（如刚提交的变量 a）→ 收起弹窗，回车直接提交
                var start = cursor;
                while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_'))
                    start--;
                var prefix = text.Substring(start, cursor - start);

                if (_completion.Items.Any(item => item.Text == prefix))
                    _popup.Hide();
                else
                    _popup.Show(_completion.Items, _completion.SelectedIndex);
            }
        }
        else
        {
            _popup.Hide();
        }

        if (text.StartsWith("#"))
        {
            _signature.Clear();
            return;
        }

        var hint = _completionProvider.GetSignatureHint(text, cursor);
        _signature.Set(hint);
    }

    private void ShowMetaCompletions(string text)
    {
        var query = text.Substring(1);
        var items = new List<CompletionItem>();
        foreach (var command in MetaCommandExecutor.Names)
        {
            if (command.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                items.Add(new CompletionItem("#" + command) { Detail = "meta command" });
        }

        // 命令已拼写完整时不再弹窗（再次回车即执行，而不是再次"补全"）
        if (MetaCommandExecutor.Names.Any(c => c.Equals(query, StringComparison.OrdinalIgnoreCase)))
            _popup.Hide();
        else if (items.Count > 0)
            _popup.Show(items, 0);
        else
            _popup.Hide();

        _signature.Clear();
    }

    private void OnDiagnosticsResult(IReadOnlyList<Diagnostic> diagnostics)
    {
        _currentDiagnostics = diagnostics;

        var firstError = diagnostics.FirstOrDefault(d => d.IsError);
        if (firstError != null)
        {
            _status.SetCenter($"error: {firstError.Message}", ConsoleColor.Red);
            return;
        }

        var firstWarning = diagnostics.FirstOrDefault(d => d.IsWarning);
        if (firstWarning != null)
        {
            _status.SetCenter($"warning: {firstWarning.Message}", ConsoleColor.Yellow);
            return;
        }

        _status.SetCenter("");
    }

    private void AcceptCompletion()
    {
        var item = _popup.SelectedItem;
        if (item == null) return;

        _popup.Hide();
        _signature.Clear();

        var line = _input.Lines[_input.CursorLine];
        var col = _input.CursorColumn;

        var start = col;
        while (start > 0 && (char.IsLetterOrDigit(line[start - 1]) || line[start - 1] == '_'))
            start--;

        string insertText;
        int cursorOffset;
        if (!string.IsNullOrEmpty(item.Snippet))
        {
            var snippet = item.Snippet;
            var dollarIndex = snippet.IndexOf('$');
            if (dollarIndex >= 0)
            {
                insertText = snippet.Substring(0, dollarIndex) + snippet.Substring(dollarIndex + 1);
                cursorOffset = dollarIndex;
            }
            else
            {
                insertText = snippet;
                cursorOffset = snippet.Length;
            }
        }
        else
        {
            insertText = item.Text;
            if (!string.IsNullOrEmpty(item.InsertSuffix))
                insertText += item.InsertSuffix;
            cursorOffset = insertText.Length;
        }

        // 元命令补全（"#cls"）应替换已输入的 '#' 前缀，避免拼成 "##cls"
        if (insertText.StartsWith("#") && start > 0 && line[start - 1] == '#')
            start--;

        var before = start > 0 ? line.Substring(0, start) : "";
        var after = line.Substring(col);

        var newLine = before + insertText + after;

        // 完整重组：光标前行 + 改写的当前行 + 光标后行（漏掉光标前行会丢多行内容）
        var head = _input.CursorLine > 0
            ? string.Join(Environment.NewLine, _input.Lines.Take(_input.CursorLine)) + Environment.NewLine
            : "";
        var tail = _input.CursorLine + 1 < _input.Lines.Count
            ? Environment.NewLine + string.Join(Environment.NewLine, _input.Lines.Skip(_input.CursorLine + 1))
            : "";

        var targetLine = _input.CursorLine;
        _input.SetText(head + newLine + tail);
        _input.SetCursor(targetLine, start + cursorOffset);

        OnTextChanged();
    }

    private void HandleEnter()
    {
        var isAtEnd = _input.CursorLine == _input.LineCount - 1
                    && _input.CursorColumn == (_input.Lines.Count > 0 ? _input.Lines[_input.LineCount - 1].Length : 0);

        if (isAtEnd)
        {
            var text = _input.Text;
            if (text.StartsWith("#") || IsCompleteSubmission(text))
            {
                ExecuteSubmission(text);
                return;
            }
        }

        _input.InsertLine();
        OnTextChanged();
        RenderFrame();
    }

    private void HandleControlEnter()
    {
        var text = _input.Text;
        if (text.StartsWith("#") || IsCompleteSubmission(text))
        {
            ExecuteSubmission(text);
            return;
        }

        _input.InsertLine();
        OnTextChanged();
        RenderFrame();
    }

    private void ExecuteSubmission(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var trimmed = text.Trim();

        if (trimmed.StartsWith("#"))
        {
            ExecuteMetaCommand(trimmed, text);
            return;
        }

        EchoSubmission(text);

        try
        {
            if (_session.Evaluate(text, _output))
                _submissionStore.Save(text);
        }
        catch (Exception ex)
        {
            _output.AppendLine($"error: {ex.Message}");
        }

        _history.Add(text);
        ResetInput();
        RenderFrame();
    }

    /// <summary>把已提交的代码按提示符格式回显到输出区（带语法高亮，标准 REPL 行为）。</summary>
    private void EchoSubmission(string text)
    {
        var lines = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);

        SyntaxTree? tree = null;
        try { tree = SyntaxTree.Parse(text); }
        catch { tree = null; }

        var lineStart = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var segments = new List<(string Text, ConsoleColor Fg, ConsoleColor Bg)>
            {
                (i == 0 ? ">>> " : "... ", ConsoleColor.Green, ConsoleColor.Black),
            };

            if (line.Length > 0)
            {
                List<(string, ConsoleColor, ConsoleColor)>? classified = null;
                if (tree != null)
                {
                    try
                    {
                        var spans = Classifier.Classify(tree, TextSpan.FromBounds(lineStart, lineStart + line.Length));
                        if (spans.Length > 0)
                            classified = MapClassifiedSpans(spans, line, lineStart);
                    }
                    catch
                    {
                        // 分类失败回退启发式。
                    }
                }

                if (classified != null)
                    segments.AddRange(classified);
                else
                    segments.AddRange(ClassifyLineFallback(line));
            }

            _output.AppendSegments(segments);
            lineStart += line.Length + Environment.NewLine.Length;
        }
    }

    private void ExecuteMetaCommand(string trimmed, string originalText)
    {
        if (!MetaCommandExecutor.IsKnown(trimmed))
        {
            var spaceIdx = trimmed.IndexOf(' ');
            var query = spaceIdx > 0 ? trimmed.Substring(1, spaceIdx - 1) : trimmed.Substring(1);
            var match = MetaCommandExecutor.Match(query);
            if (match == null)
            {
                _output.AppendLine($"Unknown command: {trimmed}. Type #help for available commands.");
                _history.Add(originalText);
                ResetInput();
                RenderFrame();
                return;
            }
            trimmed = "#" + match + (spaceIdx > 0 ? trimmed.Substring(spaceIdx) : "");
        }

        if (trimmed == "#exit")
        {
            _running = false;
            return;
        }

        if (trimmed == "#cls")
        {
            _output.Clear();
            ResetInput();
            RenderFrame();
            return;
        }

        _metaCommands.TryHandle(trimmed);
        _history.Add(originalText);
        ResetInput();
        RenderFrame();
    }

    private void ResetInput()
    {
        _input.Clear();
        _signature.Clear();
        _popup.Hide();
        _status.SetCenter("");
        _currentDiagnostics = Array.Empty<Diagnostic>();
        _history.Reset();
    }

    private static bool IsCompleteSubmission(string text)
    {
        if (string.IsNullOrEmpty(text)) return true;

        var lines = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        var lastTwoBlank = lines.Length >= 2
            && string.IsNullOrEmpty(lines[^1])
            && string.IsNullOrEmpty(lines[^2]);
        if (lastTwoBlank) return true;

        var opens = 0;
        var inString = false;
        var inComment = false;
        var inLineComment = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inLineComment) { if (c == '\n') inLineComment = false; continue; }
            if (inComment) { if (c == '*' && i + 1 < text.Length && text[i + 1] == '/') { inComment = false; i++; } continue; }
            if (inString) { if (c == '\\') { i++; continue; } if (c == '"') inString = false; continue; }
            if (c == '"') { inString = true; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') { inLineComment = true; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*') { inComment = true; i++; continue; }
            if (c == '{' || c == '(' || c == '[') opens++;
            if (c == '}' || c == ')' || c == ']') opens--;
        }

        return opens == 0 && !inString && !inComment && !inLineComment;
    }

    private void HandleBackspace()
    {
        if (_input.CursorColumn == 0 && _input.CursorLine == 0 && _input.LineCount == 1 && _input.Lines[0].Length == 0) return;
        _input.DeleteCharBackward();
        OnTextChanged();
    }

    private void HandleDelete() { _input.DeleteCharForward(); OnTextChanged(); }
    private void HandleLeft() => _input.MoveLeft();
    private void HandleRight() => _input.MoveRight();
    private void HandleUp() => _input.MoveUp();
    private void HandleDown() => _input.MoveDown();
    private void HandleHome() => _input.MoveHome();
    private void HandleEnd() => _input.MoveEnd();
    private void HandleWordLeft() => _input.MoveWordLeft();
    private void HandleWordRight() => _input.MoveWordRight();

    private void HandleDeleteWord()
    {
        _input.DeleteWordBackward();
        OnTextChanged();
    }

    private void HandlePageUp()
    {
        var entry = _history.MoveOlder();
        if (entry == null) return;
        SetInputFromHistory(entry);
    }

    private void HandlePageDown()
    {
        if (!_history.IsNavigating) return;

        var entry = _history.MoveNewer();
        if (entry == null)
        {
            _input.Clear();
            OnTextChanged();
        }
        else
        {
            SetInputFromHistory(entry);
        }
    }

    private void SetInputFromHistory(string entry)
    {
        _input.SetText(entry);
        _input.SetCursor(_input.LineCount - 1, _input.Lines[_input.LineCount - 1].Length);
        OnTextChanged();
    }

    private void HandleTab()
    {
        if (_popup.IsVisible)
        {
            AcceptCompletion();
            return;
        }

        var text = _input.Text;
        var offset = ComputeCursorOffset();
        _completion.Trigger(text, offset);
        if (_completion.IsVisible)
        {
            _popup.Show(_completion.Items, _completion.SelectedIndex);

            // 成员上下文（Console.）只弹窗供方向键选择；裸名保持"Tab 尝试补全"
            if (!_completionProvider.IsMemberAccessContext(text, offset))
                AcceptCompletion();
            return;
        }

        var start = _input.CursorColumn;
        var remainingSpaces = 4 - start % 4;
        for (var i = 0; i < remainingSpaces; i++)
            _input.InsertChar(' ');
    }

    private void HandleEscape()
    {
        if (_popup.IsVisible) _popup.Hide();
        else
        {
            _input.Clear();
            _signature.Clear();
            OnTextChanged();
        }
    }

    private void RenderFrame()
    {
        var width = Console.WindowWidth;
        var height = Console.WindowHeight;

        _status.SetLeft("Cocoa REPL");
        _status.SetRight($"Ln {_input.CursorLine + 1}, Col {_input.CursorColumn + 1}");

        var outputHeight = Math.Min(_output.LineCount, height / 3);
        var statusStart = height - 1;

        var inputStart = outputHeight;
        var inputHeight = Math.Max(1, _input.LineCount);
        var cursorRow = Math.Min(inputStart + _input.CursorLine, statusStart - 1);

        // 补全弹窗锚定光标（VS 风格）：水平跟随光标列，默认弹在光标行下方，放不下时翻到上方
        var boxWidth = Math.Min(60, width);
        var popupVisible = false;
        var popupX = 0;
        var popupStart = 0;
        var popupHeight = 0;
        if (_popup.IsVisible)
        {
            popupX = Math.Min(4 + _input.CursorColumn, Math.Max(0, width - boxWidth));
            popupHeight = _popup.VisibleRowCount + 2;
            popupStart = cursorRow + 1;
            if (popupStart + popupHeight > statusStart)
                popupStart = Math.Max(0, cursorRow - popupHeight);
            if (popupStart + popupHeight > statusStart)
                popupHeight = Math.Max(0, statusStart - popupStart);
            popupVisible = popupHeight >= 2;
        }

        // 签名提示跟随弹窗下方；弹窗翻转到上方时贴在光标行下方
        var signatureStart = popupVisible ? popupStart + popupHeight : cursorRow + 1;
        if (signatureStart >= statusStart) signatureStart = -1;
        var signatureHeight = _signature.HasContent && signatureStart >= 0 ? 1 : 0;

        _renderer.Draw(frame =>
        {
            if (_output.LineCount > 0)
                _output.Render(frame, new Rect(0, 0, width, outputHeight));

            RenderInputWithHighlight(frame, new Rect(0, inputStart, width, inputHeight));

            if (popupVisible)
                _popup.Render(frame, new Rect(popupX, popupStart, width - popupX, popupHeight));

            if (signatureHeight > 0)
                _signature.Render(frame, new Rect(0, signatureStart, width, 1));

            _status.Render(frame, new Rect(0, statusStart, width, 1));
        });

        _renderer.SetCursorPosition(Math.Min(4 + _input.CursorColumn, width - 1), cursorRow);
        _renderer.SetCursorVisible(true);
    }

    private void RenderInputWithHighlight(Frame frame, Rect area)
    {
        var matchPair = FindMatchingBrackets();
        var highlights = GetDiagnosticHighlights();

        for (var i = 0; i < _input.Lines.Count && i < area.Height; i++)
        {
            var prefix = i == 0 ? ">>> " : "... ";
            frame.WriteString(area.X, area.Y + i, prefix, ConsoleColor.Green, ConsoleColor.Black);

            var line = _input.Lines[i];
            var maxChars = Math.Min(line.Length, area.Width - 4);
            if (maxChars > 0)
            {
                var segments = ClassifyLine(i, line);
                var col = 0;
                foreach (var (text, fg, bg) in segments)
                {
                    var segLen = Math.Min(text.Length, maxChars - col);
                    if (segLen <= 0) break;

                    for (var ci = 0; ci < segLen; ci++)
                    {
                        var globalCol = col + ci;
                        var cellFg = fg;
                        var cellBg = bg;

                        foreach (var hl in highlights)
                        {
                            if (hl.Line == i && globalCol >= hl.StartCol && globalCol < hl.EndCol)
                            {
                                cellFg = hl.Foreground;
                                cellBg = hl.Background;
                                break;
                            }
                        }

                        if (matchPair.HasValue)
                        {
                            var (openLine, openCol, closeLine, closeCol) = matchPair.Value;
                            if ((i == openLine && globalCol == openCol) || (i == closeLine && globalCol == closeCol))
                            {
                                cellFg = ConsoleColor.Black;
                                cellBg = ConsoleColor.DarkCyan;
                            }
                        }

                        frame.SetCell(area.X + 4 + globalCol, area.Y + i, text[ci], cellFg, cellBg);
                    }

                    col += segLen;
                }
            }

            var padLen = area.Width - 4 - Math.Min(line.Length, area.Width - 4);
            if (padLen > 0)
                frame.WriteString(area.X + 4 + Math.Min(line.Length, area.Width - 4), area.Y + i, new string(' ', padLen), ConsoleColor.White, ConsoleColor.Black);
        }
    }

    /// <summary>把每条诊断的跨度映射到输入行：错误=暗红底白字，警告=暗黄底黑字（VS 风格波浪线的终端等价物）。</summary>
    private List<(int Line, int StartCol, int EndCol, ConsoleColor Foreground, ConsoleColor Background)> GetDiagnosticHighlights()
    {
        var result = new List<(int, int, int, ConsoleColor, ConsoleColor)>();

        foreach (var diag in _currentDiagnostics)
        {
            var span = diag.Location.Span;
            if (!TryLocateOffset(span.Start, out var lineIndex, out var startCol)) continue;

            var line = _input.Lines[lineIndex];
            var end = Math.Max(span.End, span.Start + 1);
            var endCol = Math.Min(end - GetLineStartOffset(lineIndex), line.Length);
            if (endCol <= startCol) endCol = Math.Min(startCol + 1, line.Length);
            if (endCol <= startCol) continue;

            if (diag.IsError)
                result.Add((lineIndex, startCol, endCol, ConsoleColor.White, ConsoleColor.DarkRed));
            else
                result.Add((lineIndex, startCol, endCol, ConsoleColor.Black, ConsoleColor.DarkYellow));
        }

        return result;
    }

    private bool TryLocateOffset(int offset, out int lineIndex, out int column)
    {
        lineIndex = 0;
        column = 0;
        if (_input.Lines.Count == 0) return false;

        for (var i = 0; i < _input.Lines.Count; i++)
        {
            var start = GetLineStartOffset(i);
            var end = start + _input.Lines[i].Length;
            if (offset <= end || i == _input.Lines.Count - 1)
            {
                lineIndex = i;
                column = Math.Clamp(offset - start, 0, _input.Lines[i].Length);
                return true;
            }
        }
        return false;
    }

    private (int OpenLine, int OpenCol, int CloseLine, int CloseCol)? FindMatchingBrackets()
    {
        var line = _input.Lines[_input.CursorLine];
        var col = _input.CursorColumn;

        if (col > 0 && col <= line.Length)
        {
            var ch = line[col - 1];
            if (OpenToClose.ContainsKey(ch))
            {
                var match = FindMatchingClose(ch, _input.CursorLine, col - 1);
                if (match.HasValue)
                    return (col - 1, _input.CursorLine, match.Value.col, match.Value.line);
            }
            else if (CloseToOpen.ContainsKey(ch))
            {
                var match = FindMatchingOpen(ch, _input.CursorLine, col - 1);
                if (match.HasValue)
                    return (match.Value.col, match.Value.line, col - 1, _input.CursorLine);
            }
        }

        if (col < line.Length)
        {
            var ch = line[col];
            if (OpenToClose.ContainsKey(ch))
            {
                var match = FindMatchingClose(ch, _input.CursorLine, col);
                if (match.HasValue)
                    return (col, _input.CursorLine, match.Value.col, match.Value.line);
            }
            else if (CloseToOpen.ContainsKey(ch))
            {
                var match = FindMatchingOpen(ch, _input.CursorLine, col);
                if (match.HasValue)
                    return (match.Value.col, match.Value.line, col, _input.CursorLine);
            }
        }

        return null;
    }

    private (int line, int col)? FindMatchingClose(char open, int startLine, int startCol)
    {
        var expected = OpenToClose[open];
        var depth = 0;

        for (var li = startLine; li < _input.Lines.Count; li++)
        {
            var line = _input.Lines[li];
            var startC = li == startLine ? startCol + 1 : 0;
            for (var ci = startC; ci < line.Length; ci++)
            {
                var c = line[ci];
                if (c == open) depth++;
                else if (c == expected)
                {
                    depth--;
                    if (depth == 0) return (li, ci);
                }
            }
        }
        return null;
    }

    private (int line, int col)? FindMatchingOpen(char close, int startLine, int startCol)
    {
        var expected = CloseToOpen[close];
        var depth = 0;

        for (var li = startLine; li >= 0; li--)
        {
            var line = _input.Lines[li];
            var startC = li == startLine ? startCol - 1 : line.Length - 1;
            for (var ci = startC; ci >= 0; ci--)
            {
                var c = line[ci];
                if (c == close) depth++;
                else if (c == expected)
                {
                    depth--;
                    if (depth == 0) return (li, ci);
                }
            }
        }
        return null;
    }

    /// <summary>优先用真实语法树分类（覆盖字符串/注释/数字等完整词法）；无树或异常时回退启发式。</summary>
    private List<(string Text, ConsoleColor Fg, ConsoleColor Bg)> ClassifyLine(int lineIndex, string line)
    {
        var tree = _liveTree;
        if (tree != null && line.Length > 0)
        {
            try
            {
                var start = GetLineStartOffset(lineIndex);
                var spans = Classifier.Classify(tree, TextSpan.FromBounds(start, start + line.Length));
                if (spans.Length > 0)
                    return MapClassifiedSpans(spans, line, start);
            }
            catch
            {
                // 分类失败时回退到启发式。
            }
        }

        return ClassifyLineFallback(line);
    }

    private static List<(string Text, ConsoleColor Fg, ConsoleColor Bg)> MapClassifiedSpans(
        ImmutableArray<ClassifiedSpan> spans, string line, int lineStart)
    {
        var result = new List<(string, ConsoleColor, ConsoleColor)>();
        var pos = 0;

        foreach (var classified in spans)
        {
            var start = Math.Max(classified.Span.Start - lineStart, pos);
            var end = Math.Min(classified.Span.End - lineStart, line.Length);
            if (end <= start) continue;

            if (start > pos)
                result.Add((line.Substring(pos, start - pos), ConsoleColor.White, ConsoleColor.Black));

            result.Add((line.Substring(start, end - start), MapClassification(classified.Classification), ConsoleColor.Black));
            pos = end;
            if (pos >= line.Length) break;
        }

        if (pos < line.Length)
            result.Add((line.Substring(pos), ConsoleColor.White, ConsoleColor.Black));

        return result;
    }

    private static ConsoleColor MapClassification(Classification classification) => classification switch
    {
        Classification.Keyword => ConsoleColor.Blue,
        Classification.Number => ConsoleColor.Cyan,
        Classification.String => ConsoleColor.DarkYellow,
        Classification.Comment => ConsoleColor.DarkGreen,
        Classification.Punctuation or Classification.Operator => ConsoleColor.DarkCyan,
        _ => ConsoleColor.White,
    };

    private static List<(string Text, ConsoleColor Fg, ConsoleColor Bg)> ClassifyLineFallback(string line)
    {
        var result = new List<(string, ConsoleColor, ConsoleColor)>();
        var i = 0;

        while (i < line.Length)
        {
            if (char.IsWhiteSpace(line[i]))
            {
                var start = i;
                while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
                result.Add((line.Substring(start, i - start), ConsoleColor.White, ConsoleColor.Black));
            }
            else if (line[i] == '"' || line[i] == '\'')
            {
                var quote = line[i];
                var start = i;
                i++;
                while (i < line.Length && line[i] != quote) { if (line[i] == '\\') i++; i++; }
                if (i < line.Length) i++;
                result.Add((line.Substring(start, i - start), ConsoleColor.DarkYellow, ConsoleColor.Black));
            }
            else if (char.IsDigit(line[i]) || (line[i] == '.' && i + 1 < line.Length && char.IsDigit(line[i + 1])))
            {
                var start = i;
                while (i < line.Length && (char.IsDigit(line[i]) || line[i] == '.' || line[i] == 'f' || line[i] == 'd' || line[i] == 'l')) i++;
                result.Add((line.Substring(start, i - start), ConsoleColor.Cyan, ConsoleColor.Black));
            }
            else if (char.IsLetter(line[i]) || line[i] == '_')
            {
                var start = i;
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
                var word = line.Substring(start, i - start);
                var fg = IsKeyword(word) ? ConsoleColor.Blue : ConsoleColor.White;
                result.Add((word, fg, ConsoleColor.Black));
            }
            else if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                result.Add((line.Substring(i), ConsoleColor.DarkGreen, ConsoleColor.Black));
                i = line.Length;
            }
            else if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '*')
            {
                var start = i;
                i += 2;
                while (i < line.Length - 1 && !(line[i] == '*' && line[i + 1] == '/')) i++;
                if (i < line.Length - 1) i += 2;
                result.Add((line.Substring(start, i - start), ConsoleColor.DarkGreen, ConsoleColor.Black));
            }
            else if ("{}[]()".Contains(line[i]))
            {
                result.Add((line[i].ToString(), ConsoleColor.DarkCyan, ConsoleColor.Black));
                i++;
            }
            else if (line[i] == ',')
            {
                result.Add((line[i].ToString(), ConsoleColor.DarkGray, ConsoleColor.Black));
                i++;
            }
            else if ("+-*/%=!<>&|^~?:;".Contains(line[i]))
            {
                result.Add((line[i].ToString(), ConsoleColor.DarkCyan, ConsoleColor.Black));
                i++;
            }
            else
            {
                result.Add((line[i].ToString(), ConsoleColor.White, ConsoleColor.Black));
                i++;
            }
        }

        return result;
    }

    private static bool IsKeyword(string word) => word switch
    {
        "abstract" or "as" or "base" or "break" or "case" or "catch" or "cdecl" or
        "class" or "const" or "constructor" or "continue" or "default" or "delegate" or
        "do" or "else" or "enum" or "event" or "extends" or "extern" or "facade" or
        "false" or "finally" or "for" or "foreach" or "function" or "get" or "if" or
        "import" or "in" or "interface" or "internal" or "is" or "let" or "namespace" or
        "new" or "null" or "out" or "override" or "partial" or "private" or "property" or
        "protected" or "public" or "readonly" or "ref" or "return" or "sealed" or "set" or
        "static" or "stdcall" or "step" or "struct" or "switch" or "syscall" or "this" or
        "throw" or "to" or "true" or "try" or "using" or "var" or "virtual" or "when" or
        "where" or "while" => true,
        _ => false
    };

    /// <summary>行在 _input.Text（Environment.NewLine 连接）中的起始偏移。</summary>
    private int GetLineStartOffset(int lineIndex)
    {
        var offset = 0;
        for (var i = 0; i < lineIndex; i++)
            offset += _input.Lines[i].Length + Environment.NewLine.Length;
        return offset;
    }

    private int ComputeCursorOffset() => GetLineStartOffset(_input.CursorLine) + _input.CursorColumn;

    private void LoadSubmissions()
    {
        foreach (var text in _submissionStore.LoadAll())
            _session.Evaluate(text, _output);
    }

    public void Dispose()
    {
        _diagnostics.Dispose();
        _renderer.Dispose();
    }
}
