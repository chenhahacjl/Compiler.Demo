using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeGen.Managed.Structure;
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
    /// 阶段 7 增量五 M5-a3：自举 IlOpCodeTable 与 C# IlOpCodeTable 差分。
    /// 自举侧（Cocoa.CodeGen.IlOpCodeTable）驱动 Main 打印 `name|value|type|size`；
    /// C# 基准 = IlOpCodeTable.Get(name)（value / OperandType / OperandSize）。
    /// </summary>
    public class IlOpCodeTableDifferentialTests
    {
        private static readonly string[] OpcodeNames =
        {
            "Nop", "Ret", "Dup", "Pop", "Ldnull",
            "Ldc_I4_M1", "Ldc_I4_0", "Ldc_I4_1", "Ldc_I4_2", "Ldc_I4_3", "Ldc_I4_4", "Ldc_I4_5",
            "Ldc_I4_6", "Ldc_I4_7", "Ldc_I4_8", "Ldc_I4_S", "Ldc_I4", "Ldc_I8", "Ldc_R4", "Ldc_R8",
            "Ldarg_0", "Ldarg_1", "Ldarg_2", "Ldarg_3", "Ldarg_S", "Ldloc_S", "Stloc_S",
            "Ldloc_0", "Ldloc_1", "Ldloc_2", "Ldloc_3", "Stloc_0", "Stloc_1", "Stloc_2", "Stloc_3",
            "Add", "Sub", "Mul", "Div", "Div_Un", "Rem", "Rem_Un", "And", "Or", "Xor",
            "Shl", "Shr", "Shr_Un", "Neg", "Not",
            "Conv_I1", "Conv_I2", "Conv_I4", "Conv_U1", "Conv_U2", "Conv_I8", "Conv_R4", "Conv_R8",
            "Conv_U4", "Conv_U8", "Conv_I", "Conv_U",
            "Ceq", "Cgt", "Cgt_Un", "Clt", "Clt_Un",
            "Br_S", "Brfalse_S", "Brtrue_S", "Br", "Brfalse", "Brtrue", "Switch", "Leave", "Leave_S",
            "Endfinally", "Throw", "Rethrow",
            "Call", "Callvirt", "Newobj", "Ldftn", "Box", "Newarr", "Castclass", "Isinst",
            "Unbox_Any", "Initobj", "Constrained",
            "Ldsfld", "Ldfld", "Stsfld", "Stfld", "Ldsflda", "Ldflda", "Ldlen",
            "Ldelem_I1", "Ldelem_U1", "Ldelem_I4", "Ldelem_I2", "Ldelem_U2", "Ldelem_I8",
            "Ldelem_R4", "Ldelem_R8", "Ldelem_Ref",
            "Stelem_I1", "Stelem_I2", "Stelem_I4", "Stelem_I8", "Stelem_R4", "Stelem_R8", "Stelem_Ref",
            "Ldind_I1", "Ldind_U1", "Ldind_I2", "Ldind_U2", "Ldind_I4", "Ldind_U4", "Ldind_I8",
            "Ldind_I", "Ldind_R4", "Ldind_R8", "Ldind_Ref",
            "Stind_Ref", "Stind_I1", "Stind_I2", "Stind_I4", "Stind_I8", "Stind_I", "Stind_R4", "Stind_R8",
            "Ldelema", "Ldstr", "Ldarg", "Ldarga", "Ldloc", "Ldloca", "Stloc",
        };

        [Fact]
        public void SelfHosted_OpCodeTable_Matches_CSha()
        {
            var (output, compileErrors) = RunSelfDriver();
            Assert.True(compileErrors.Count == 0, "COCOMPILE-ERROR: " + string.Join(" | ", compileErrors));

            var records = Parse(output);
            var failures = new List<string>();
            foreach (var name in OpcodeNames)
            {
                if (!records.TryGetValue(name, out var self))
                {
                    failures.Add($"{name}: missing from self table");
                    continue;
                }

                var csharp = IlOpCodeTable.Get(name);
                var csharpType = (int)csharp.OperandType;
                var csharpSize = OperandSize(csharp);
                if (self.Value != csharp.Value || self.Type != csharpType || self.Size != csharpSize)
                {
                    failures.Add($"{name}: self=({self.Value},{self.Type},{self.Size}) C#=({csharp.Value},{csharpType},{csharpSize})");
                }
            }

            Assert.True(failures.Count == 0, "\n" + string.Join("\n", failures));
        }

        private static int OperandSize(IlOpCode op)
        {
            return op.OperandType switch
            {
                IlOperandType.InlineNone => 0,
                IlOperandType.InlineI or IlOperandType.InlineMethod or IlOperandType.InlineType
                    or IlOperandType.InlineField or IlOperandType.InlineTok or IlOperandType.InlineSig
                    or IlOperandType.InlineString or IlOperandType.InlineBrTarget => 4,
                IlOperandType.InlineR or IlOperandType.InlineI8 => 8,
                IlOperandType.InlineVar => 2,
                IlOperandType.ShortInlineR => 4,
                IlOperandType.ShortInlineI or IlOperandType.ShortInlineVar
                    or IlOperandType.ShortInlineBrTarget => 1,
                IlOperandType.InlineSwitch => -1,
                _ => -1,
            };
        }

        private static Dictionary<string, (int Value, int Type, int Size)> Parse(string output)
        {
            var result = new Dictionary<string, (int, int, int)>();
            foreach (var line in output.Split('\n'))
            {
                if (line.Length == 0)
                {
                    continue;
                }

                var parts = line.Split('|');
                if (parts.Length == 4)
                {
                    result[parts[0]] = (int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3]));
                }
            }

            return result;
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
            for (var i = 0; i < OpcodeNames.Length; i++)
            {
                var name = OpcodeNames[i];
                sb.Append("    let v").Append(i).Append(" = Cocoa.CodeGen.IlOpCodeTable.OpCodeValue(\"").Append(name).Append("\")\n");
                sb.Append("    let t").Append(i).Append(" = Cocoa.CodeGen.IlOpCodeTable.OpCodeOperandType(\"").Append(name).Append("\")\n");
                sb.Append("    let s").Append(i).Append(" = Cocoa.CodeGen.IlOpCodeTable.OpCodeOperandSize(\"").Append(name).Append("\")\n");
                sb.Append("    System.Console.WriteLine(\"").Append(name).Append("|\" + string(v").Append(i).Append(") + \"|\" + string(t").Append(i).Append(") + \"|\" + string(s").Append(i).Append("))\n");
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