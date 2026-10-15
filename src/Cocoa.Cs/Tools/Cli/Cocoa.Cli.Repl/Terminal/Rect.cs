namespace Cocoa.Cli.Repl;

internal readonly struct Rect
{
    public readonly int X;
    public readonly int Y;
    public readonly int Width;
    public readonly int Height;

    public Rect(int x, int y, int width, int height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }
}
