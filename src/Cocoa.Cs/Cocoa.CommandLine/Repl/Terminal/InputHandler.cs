using System;
using System.Collections.Generic;

namespace Cocoa.Compiler.Terminal;

internal sealed class InputHandler
{
    private readonly Dictionary<ConsoleKey, Action> _noModifierBindings = new();
    private readonly Dictionary<ConsoleKey, Action> _controlBindings = new();
    private readonly Dictionary<(ConsoleModifiers, ConsoleKey), Action> _otherBindings = new();

    public void Bind(ConsoleKey key, Action handler)
    {
        _noModifierBindings[key] = handler;
    }

    public void Bind(ConsoleModifiers modifiers, ConsoleKey key, Action handler)
    {
        if (modifiers == ConsoleModifiers.Control)
            _controlBindings[key] = handler;
        else
            _otherBindings[(modifiers, key)] = handler;
    }

    public bool Handle(ConsoleKeyInfo keyInfo)
    {
        if (keyInfo.Modifiers == ConsoleModifiers.Control && _controlBindings.TryGetValue(keyInfo.Key, out var ctrlHandler))
        {
            ctrlHandler();
            return true;
        }

        if (keyInfo.Modifiers == default(ConsoleModifiers) && _noModifierBindings.TryGetValue(keyInfo.Key, out var handler))
        {
            handler();
            return true;
        }

        if (keyInfo.Modifiers != default(ConsoleModifiers) && keyInfo.Modifiers != ConsoleModifiers.Control &&
            _otherBindings.TryGetValue((keyInfo.Modifiers, keyInfo.Key), out var modHandler))
        {
            modHandler();
            return true;
        }

        return false;
    }
}
