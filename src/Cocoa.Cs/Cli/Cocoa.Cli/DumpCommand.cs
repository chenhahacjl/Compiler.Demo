using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Serialization;
using Cocoa.CodeAnalysis.Symbols;
using System.CodeDom.Compiler;

namespace Cocoa.Cli
{
    /// <summary>
    /// `cocoa dump &lt;file.coa&gt;` —— 把 `.coa` 程序集渲染成可读大纲：依赖清单、枚举/类/全局符号表、
    /// 函数签名清单与函数体伪码（复用 <see cref="Cocoa.CodeAnalysis.Binding.BoundNodePrinter"/>）。
    /// </summary>
    internal static class DumpCommand
    {
        public static int Run(string[] args)
        {
            var path = (string?)null;
            var helpRequested = false;

            for (var i = 0; i < args.Length; i++)
            {
                var (optionName, _) = CliHelper.SplitOption(args[i]);
                switch (optionName)
                {
                    case "-?":
                    case "-h":
                    case "--help":
                        helpRequested = true;
                        break;
                    default:
                        if (args[i].Length > 0 && args[i][0] == '-')
                        {
                            Console.Error.WriteLine($"error: unknown option '{args[i]}'");
                            return 1;
                        }

                        if (path != null)
                        {
                            Console.Error.WriteLine("error: need exactly one .coa file");
                            return 1;
                        }

                        path = args[i];
                        break;
                }
            }

            if (helpRequested)
            {
                PrintHelp();
                return 0;
            }

            if (path == null)
            {
                Console.Error.WriteLine("error: need a .coa file (usage: cocoa dump <file.coa>)");
                return 1;
            }

            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"error: file '{path}' doesn't exist!");
                return 1;
            }

            if (!path.EndsWith(".coa", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"error: '{path}' is not a .coa assembly");
                return 1;
            }

            CoaProgram program;
            try
            {
                program = CoaSerializer.Load(path);
            }
            catch (InvalidDataException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return 1;
            }

            Dump(program);

            return 0;
        }

        private static void Dump(CoaProgram program)
        {
            Console.WriteLine("// cocoa dump");
            Console.WriteLine($"requires  : {program.Requires.ToString().ToLowerInvariant()}");
            Console.WriteLine($"platforms : {JoinOrNone(program.Platforms)}");
            Console.WriteLine($"imports   : {JoinOrNone(program.NativeImports)}");
            Console.WriteLine($"refs dll  : {JoinOrNone(program.DotnetReferences)}");
            Console.WriteLine($"refs cod  : {JoinOrNone(program.CodReferences)}");
            Console.WriteLine($"namespaces: {JoinOrNone(program.Namespaces)}");

            if (program.Enums.Length > 0)
            {
                Console.WriteLine();
                Console.WriteLine("== enums ==");
                foreach (var e in program.Enums)
                {
                    var members = e.MemberNames
                        .Select(n => (e.TryGetMember(n, out var v), n, v))
                        .Where(t => t.Item1)
                        .OrderBy(t => t.v)
                        .Select(t => $"{t.n} = {t.v}");
                    Console.WriteLine($"enum {e.FullName} {{ {string.Join(", ", members)} }}");
                }
            }

            if (program.Classes.Length > 0)
            {
                Console.WriteLine();
                Console.WriteLine("== classes ==");
                foreach (var c in program.Classes)
                {
                    var baseType = c.BaseType != null && c.BaseType != c ? " : " + c.BaseType.FullName : "";
                    Console.WriteLine($"class {c.FullName}{baseType} ({c.Visibility.ToString().ToLowerInvariant()})");
                    foreach (var m in c.Methods.Where(m => m.IsStatic))
                    {
                        Console.WriteLine("    " + Signature(m, includeOwner: false));
                    }
                }
            }

            if (program.Globals.Length > 0)
            {
                Console.WriteLine();
                Console.WriteLine("== globals ==");
                foreach (var g in program.Globals)
                {
                    var initializer = g.Constant?.Value == null ? "" : " = " + FormatConstant(g.Constant.Value);
                    var modifier = g.IsReadOnly ? " (readonly)" : "";
                    Console.WriteLine($"{g.Type.Name} {g.Name}{initializer}{modifier}");
                }
            }

            if (program.Functions.Length > 0)
            {
                Console.WriteLine();
                Console.WriteLine("== functions ==");
                foreach (var f in program.Functions)
                {
                    Console.WriteLine(Signature(f));
                }

                var withBody = program.Functions.Where(f => program.Bodies.ContainsKey(f)).ToArray();
                if (withBody.Length > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("== bodies ==");
                    var first = true;
                    foreach (var f in withBody)
                    {
                        if (!first)
                        {
                            Console.WriteLine();
                        }

                        first = false;
                        Console.WriteLine("; " + Signature(f));
                        program.Bodies[f].WriteTo(new IndentedTextWriter(Console.Out, "  "));
                    }
                }
            }
        }

        private static string Signature(FunctionSymbol fn, bool includeOwner = true)
        {
            var prefix = "";
            if (includeOwner)
            {
                if (fn.ContainingClass != null)
                {
                    prefix = fn.ContainingClass.FullName + ".";
                }
                else if (fn.Namespace.Length > 0)
                {
                    prefix = fn.Namespace + ".";
                }
            }

            var parameters = string.Join(", ", fn.Parameters.Select(p => $"{p.Type.Name} {p.Name}"));
            var text = $"{fn.ReturnType.Name} {prefix}{fn.Name}({parameters})";

            if (fn.IsExtern)
            {
                var entry = string.IsNullOrEmpty(fn.EntryPoint) ? fn.Name : fn.EntryPoint!;
                text += $" [extern {fn.DllName}!{entry}";
                if (fn.CharSet != null)
                {
                    text += $", charset {fn.CharSet.Value.ToString().ToLowerInvariant()}";
                }

                text += "]";
            }
            else if (fn.BuiltinKind != null)
            {
                text += $" [syscall {fn.BuiltinKind.Value}]";
            }

            return text;
        }

        private static string FormatConstant(object value)
        {
            return value switch
            {
                null => "null",
                string s => "\"" + s + "\"",
                char c => "'" + c + "'",
                bool b => b ? "true" : "false",
                _ => value.ToString() ?? "",
            };
        }

        private static string JoinOrNone(System.Collections.Generic.IEnumerable<string> items)
        {
            var joined = string.Join(", ", items);
            return joined.Length == 0 ? "(none)" : joined;
        }

        private static void PrintHelp()
        {
            Console.WriteLine("usage: cocoa dump <file.coa>");
            Console.WriteLine();
            Console.WriteLine("Prints a readable outline of a .coa assembly:");
            Console.WriteLine("manifest, enums, classes, globals, function signatures, and function bodies as pseudocode.");
            Console.WriteLine();
            Console.WriteLine("options:");
            Console.WriteLine("  -?, -h, --help     Prints help");
        }
    }
}
