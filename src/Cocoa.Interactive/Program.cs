namespace Cocoa.Interactive
{
    internal static class Program
    {
        private static void Main()
        {
            var repl = new CocoaRepl();
            repl.Run();
        }
    }
}
