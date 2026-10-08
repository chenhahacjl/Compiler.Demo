using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeGen.Managed.Structure;
using Cocoa.CodeGen.Managed.Writer;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// 阶段 7 增量五 M5-a3：自举 IlAssembler 与 C# IlAssembler 差分（逐字节 hex）。
    /// 覆盖 InlineNone/InlineI/ShortInlineI/InlineVar/ShortInlineVar/分支/双字节/token 占位。
    /// </summary>
    public class IlAssemblerDifferentialTests
    {
        // 每序列：(name, operandTypeCode, operandValue, targetIndex)
        private static readonly (string Name, int OpType, int Operand, int Target)[][] Sequences =
        {
            new[]
            {
                ("Ldc_I4", 1, 5, -1),
                ("Ret", 0, 0, -1),
            },
            new[]
            {
                ("Ldarg_S", 13, 0, -1),
                ("Stloc_0", 0, 0, -1),
            },
            new[]
            {
                ("Ldloc_S", 13, 2, -1),
                ("Ldc_I4", 1, 42, -1),
                ("Stloc_S", 13, 2, -1),
            },
            new[]
            {
                ("Br_S", 15, 0, 3),
                ("Nop", 0, 0, -1),
                ("Nop", 0, 0, -1),
                ("Ldloc_S", 13, 1, -1),
                ("Ret", 0, 0, -1),
            },
            new[]
            {
                ("Br", 14, 0, 2),
                ("Ldc_I4_S", 5, 7, -1),
                ("Ldc_I4", 1, 9, -1),
                ("Ret", 0, 0, -1),
            },
            new[]
            {
                ("Call", 7, 0, -1),
                ("Ret", 0, 0, -1),
            },
            new[]
            {
                ("Isinst", 8, 0, -1),
                ("Box", 8, 0, -1),
                ("Ldstr", 6, 0, -1),
            },
            new[]
            {
                ("Ceq", 0, 0, -1),
                ("Brfalse_S", 15, 0, 3),
                ("Nop", 0, 0, -1),
                ("Ret", 0, 0, -1),
            },
            new[]
            {
                ("Ldloc", 12, 3, -1),
                ("Ldarg", 12, 1, -1),
                ("Add", 0, 0, -1),
                ("Ret", 0, 0, -1),
            },
            new[]
            {
                ("Switch", 16, 2, 1),
                ("Nop", 0, 0, -1),
                ("Nop", 0, 0, -1),
                ("Ret", 0, 0, -1),
            },
        };

        [Fact]
        public void SelfHosted_Assembler_Matches_CSha()
        {
            var (output, compileErrors) = RunSelfDriver();
            Assert.True(compileErrors.Count == 0, "COCOMPILE-ERROR: " + string.Join(" | ", compileErrors));

            var selfHex = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .ToArray();
            Assert.True(selfHex.Length == Sequences.Length, $"expected {Sequences.Length} sequences, got {selfHex.Length}");

            var failures = new List<string>();
            for (var i = 0; i < Sequences.Length; i++)
            {
                var csharpHex = CSharpAssembleHex(Sequences[i]);
                if (selfHex[i] != csharpHex)
                {
                    failures.Add($"seq-{i}: C#=[{csharpHex}] self=[{selfHex[i]}]");
                }
            }

            Assert.True(failures.Count == 0, "\n" + string.Join("\n", failures));
        }

        private static string CSharpAssembleHex((string Name, int OpType, int Operand, int Target)[] sequence)
        {
            var instructions = new IlInstruction[sequence.Length];
            for (var i = 0; i < sequence.Length; i++)
            {
                var (name, _, operand, _) = sequence[i];
                var op = IlOpCodeTable.Get(name);
                object? operandValue = null;
                switch (op.OperandType)
                {
                    case IlOperandType.InlineI:
                        operandValue = operand;
                        break;
                    case IlOperandType.ShortInlineI:
                        operandValue = (sbyte)operand;
                        break;
                    case IlOperandType.InlineVar:
                        operandValue = (ushort)operand;
                        break;
                    case IlOperandType.ShortInlineVar:
                        operandValue = (byte)operand;
                        break;
                    case IlOperandType.InlineString:
                    case IlOperandType.InlineMethod:
                    case IlOperandType.InlineType:
                        operandValue = null;
                        break;
                }

                instructions[i] = new IlInstruction(op, operandValue);
            }

            for (var i = 0; i < sequence.Length; i++)
            {
                var target = sequence[i].Target;
                if (target >= 0 && sequence[i].OpType != 16)
                {
                    instructions[i] = new IlInstruction(IlOpCodeTable.Get(sequence[i].Name), instructions[target]);
                }
                else if (sequence[i].OpType == 16)
                {
                    var count = sequence[i].Operand;
                    var targets = new IlInstruction[count];
                    for (var k = 0; k < count; k++)
                    {
                        targets[k] = instructions[target + k];
                    }

                    instructions[i] = new IlInstruction(IlOpCodeTable.Get(sequence[i].Name), targets);
                }
            }

            var assembler = new IlAssembler();
            foreach (var instr in instructions)
            {
                assembler.Emit(instr);
            }

            var bytes = assembler.Assemble(instructions.ToList());
            var sb = new StringBuilder();
            foreach (var b in bytes)
            {
                sb.Append(b.ToString("x2"));
            }

            return sb.ToString();
        }

        private static (string Output, List<string> CompileErrors) RunSelfDriver()
        {
            var root = RepoRoot();
            var compilerDir = Path.Combine(root, "src", "Cocoa.Co", "Cocoa.Compiler");
            var trees = ImmutableArray.CreateBuilder<SyntaxTree>();
            foreach (var file in Directory.GetFiles(compilerDir, "*.co", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.Ordinal))
            {
                trees.Add(SyntaxTree.Parse(File.ReadAllText(file)));
            }

            trees.Add(SyntaxTree.Parse(BuildDriverSource()));

            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);

                var compilation = Compilation.Create("Main", new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location }, trees.ToArray());
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                if (result.Diagnostics.HasErrors())
                {
                    return ("", result.Diagnostics.Select(d => d.Message).ToList());
                }

                return (writer.ToString().Replace("\r\n", "\n"), new List<string>());
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        private static string BuildDriverSource()
        {
            var sb = new StringBuilder();
            sb.Append("using Cocoa.CodeGen\nusing System\n\nfunction Main(): i32\n{\n");
            for (var s = 0; s < Sequences.Length; s++)
            {
                sb.Append("    let a").Append(s).Append(" = new Cocoa.CodeGen.IlAssembler()\n");
                foreach (var (name, opType, operand, target) in Sequences[s])
                {
                    sb.Append("    a").Append(s).Append(".Add(\"").Append(name).Append("\", ").Append(opType).Append(", ").Append(operand).Append(", ").Append(target).Append(")\n");
                }

                sb.Append("    System.Console.WriteLine(a").Append(s).Append(".Assemble())\n");
            }

            sb.Append("    return 0\n}\n");
            return sb.ToString();
        }

        private static string RepoRoot()
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "src", "Cocoa.SDK", "System.Core", "String.co")))
            {
                dir = Path.GetDirectoryName(dir);
            }

            Assert.NotNull(dir);
            return dir!;
        }
    }
}