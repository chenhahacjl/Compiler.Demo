using System;

namespace Cocoa.Compiler.Terminal;

internal readonly struct Cell : IEquatable<Cell>
{
    public static readonly Cell Empty = new(' ', ConsoleColor.Gray, ConsoleColor.Black, false);

    public readonly char Character;
    public readonly ConsoleColor Foreground;
    public readonly ConsoleColor Background;
    public readonly bool Bold;

    public Cell(char character, ConsoleColor foreground, ConsoleColor background, bool bold)
    {
        Character = character;
        Foreground = foreground;
        Background = background;
        Bold = bold;
    }

    public bool Equals(Cell other) =>
        Character == other.Character &&
        Foreground == other.Foreground &&
        Background == other.Background &&
        Bold == other.Bold;

    public override bool Equals(object? obj) => obj is Cell other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Character, Foreground, Background, Bold);

    public static bool operator ==(Cell left, Cell right) => left.Equals(right);
    public static bool operator !=(Cell left, Cell right) => !left.Equals(right);
}
