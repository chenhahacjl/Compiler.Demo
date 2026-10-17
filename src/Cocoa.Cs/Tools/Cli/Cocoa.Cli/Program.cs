using Cocoa.CodeGen.Interpreter;
using Cocoa.CodeGen.Managed;
using Cocoa.CodeGen.Native;

namespace Cocoa.Cli
{
    public static class Program
    {
        private static int Main(string[] args)
        {
            // 注册拆分后的 managed/native 后端发射实现与解释器求值实现（Core 不引用后端，经委托接入）
            ManagedBackend.Register();
            NativeBackend.Register();
            InterpreterBackend.Register();

            if (args.Length == 0)
            {
                PrintHelp();
                return 0;
            }

            if (args[0] is "-?" or "-h" or "--help")
            {
                PrintHelp();
                return 0;
            }

            if (args.Any(a => a is "-i" or "--interactive"))
            {
                RunInteractive();
                return 0;
            }

            switch (args[0])
            {
                case "build":
                    return BuildCommand.Run(args.Skip(1).ToArray());
                case "new":
                    return NewCommand.Run(args.Skip(1).ToArray());
                case "list":
                    return ListCommand.Run(args.Skip(1).ToArray());
                case "add":
                    return ReferenceCommand.RunAdd(args.Skip(1).ToArray());
                case "remove":
                    return ReferenceCommand.RunRemove(args.Skip(1).ToArray());
                case "run":
                    return RunCommand.Run(args.Skip(1).ToArray());
                case "clean":
                    return CleanCommand.Run(args.Skip(1).ToArray());
                case "dump":
                    return DumpCommand.Run(args.Skip(1).ToArray());
                case "inspect":
                    return InspectCommand.Run(args.Skip(1).ToArray());
            }

            return CompileCommand.Run(args);
        }

        private static void RunInteractive()
        {
            using var engine = new Cocoa.Cli.Repl.Terminal.ReplEngine();
            engine.Run();
        }

        internal static void PrintHelp()
        {
            Console.WriteLine("usage: cocoa <command> [options]");
            Console.WriteLine("       cocoa <source-paths> [options]");
            Console.WriteLine();
            Console.WriteLine("commands:");
            Console.WriteLine("  new <template> [name]  Creates a new project or solution (console, library, cocoa, solution)");
            Console.WriteLine("  build                  Builds a .coproj project or .cosln solution (see 'cocoa build -h')");
            Console.WriteLine("  run                    Builds and runs a project or solution");
            Console.WriteLine("  list                   Lists templates, projects, or references");
            Console.WriteLine("  add reference          Adds a reference to a project");
            Console.WriteLine("  remove reference       Removes a reference from a project");
            Console.WriteLine("  clean                  Cleans build caches (.cocoa/) and outputs");
            Console.WriteLine("  dump <file.coa>        Prints a readable outline of a .coa assembly");
            Console.WriteLine("  inspect <file.coa>     Inspects a .coa assembly");
            Console.WriteLine("  -i, --interactive      Launches the interactive REPL");
            Console.WriteLine();
            Console.WriteLine("direct compile options:");
            Console.WriteLine("  -r <path>          The path of an assembly to reference");
            Console.WriteLine("  -o <path>          The output path of the assembly to create");
            Console.WriteLine("  -b <name>          The code generation backend: managed-fx, managed-net (default) or native");
            Console.WriteLine("  --platform <arch>  The native target architecture: x86 or x64 (default x64). Only used with -b native");
            Console.WriteLine("  --dotnet-runtime <tfm>  The .NET target framework: net40~net48 (netfx, default net48) or net8.0/net9.0 (netcore). Only used with managed backends");
            Console.WriteLine("  --dotnet-module <name>  The module name (managed backends only; defaults to the output file name)");
            Console.WriteLine("  --nowarn:<codes>  Suppress warnings by diagnostic code (comma-separated, e.g. COC2001)");
            Console.WriteLine("  -i, --interactive   Launches the interactive REPL");
            Console.WriteLine("  -?, -h, --help      Prints help");
        }
    }
}
