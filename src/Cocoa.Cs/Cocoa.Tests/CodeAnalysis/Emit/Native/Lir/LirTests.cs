using System.Collections.Generic;
using Cocoa.CodeGen.Native;
using Cocoa.CodeGen.PE;
using Cocoa.CodeGen.Native.Lir;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Emit.Native.Lir
{
    public class LirVirtualRegisterTests
    {
        [Fact]
        public void Allocator_Issues_Unique_Ids()
        {
            var allocator = new LirVirtualRegisterAllocator();

            var v1 = allocator.Allocate();
            var v2 = allocator.Allocate();
            var v3 = allocator.Allocate();

            Assert.Equal(0, v1.Id);
            Assert.Equal(1, v2.Id);
            Assert.Equal(2, v3.Id);
            Assert.NotEqual(v1, v2);
        }

        [Fact]
        public void ToString_Shows_V_Prefix()
        {
            var register = new LirVirtualRegister(7, LirType.I32);

            Assert.Equal("v7", register.ToString());
        }
    }

    public class LirInstructionTests
    {
        [Fact]
        public void Const_Uses_Immediate_OperandA()
        {
            var allocator = new LirVirtualRegisterAllocator();
            var instruction = new LirInstruction(LirOpCode.Const, allocator.Allocate(), LirOperand.Constant(42));

            Assert.Equal(LirOpCode.Const, instruction.OpCode);
            Assert.Equal(42, instruction.A.Imm);
            Assert.Equal(0, instruction.Dst!.Id);
            Assert.True(instruction.B.IsNone);
        }

        [Fact]
        public void Binary_Uses_Two_Operands()
        {
            var allocator = new LirVirtualRegisterAllocator();
            var dst = allocator.Allocate();
            var srcA = allocator.Allocate();
            var srcB = allocator.Allocate();

            var instruction = new LirInstruction(LirOpCode.Add, dst, LirOperand.Reg(srcA), LirOperand.Reg(srcB));

            Assert.Equal(LirOpCode.Add, instruction.OpCode);
            Assert.Equal(srcA, instruction.A.Register);
            Assert.Equal(srcB, instruction.B.Register);
            Assert.Equal(dst, instruction.Dst);
        }

        [Fact]
        public void Memory_Load_Carries_Offset_And_Size()
        {
            var allocator = new LirVirtualRegisterAllocator();
            var dst = allocator.Allocate();
            var baseReg = allocator.Allocate();

            var instruction = LirMem.Load(dst, baseReg, -16, 4);

            Assert.Equal(LirOpCode.Load, instruction.OpCode);
            Assert.Equal(baseReg, instruction.A.Register);
            Assert.Equal(-16, instruction.Offset);
            Assert.Equal(4, instruction.ByteSize);
        }

        [Fact]
        public void Memory_Store_Carries_Offset_And_Size()
        {
            var allocator = new LirVirtualRegisterAllocator();
            var baseReg = allocator.Allocate();
            var src = allocator.Allocate();

            var instruction = LirMem.Store(baseReg, 8, src, 8);

            Assert.Equal(LirOpCode.Store, instruction.OpCode);
            Assert.Equal(src, instruction.B.Register);
            Assert.Equal(8, instruction.Offset);
            Assert.Equal(8, instruction.ByteSize);
        }
    }

    public class LirPrinterTests
    {
        private readonly LirVirtualRegisterAllocator _allocator = new LirVirtualRegisterAllocator();

        [Fact]
        public void BuildBlocks_Splits_At_Label()
        {
            var function = new LirFunction("main", new List<LirParameter>());
            function.Instructions.Add(new LirInstruction(LirOpCode.Const, _allocator.Allocate(), LirOperand.Constant(1)));
            function.Instructions.Add(new LirInstruction(LirOpCode.Label, LirOperand.Label(3)));
            function.Instructions.Add(new LirInstruction(LirOpCode.Const, _allocator.Allocate(), LirOperand.Constant(2)));
            function.Instructions.Add(new LirInstruction(LirOpCode.Ret, LirOperand.Label(0)));

            var blocks = function.Blocks;

            Assert.Equal(3, blocks.Count);
            Assert.Equal(0, blocks[0].Labels.Count);
            Assert.Single(blocks[0].Instructions);
            Assert.Null(blocks[0].Terminator);
            Assert.Equal(new[] { 3 }, blocks[1].Labels);
            Assert.Single(blocks[1].Instructions);
            Assert.Null(blocks[1].Terminator);
            Assert.Equal(new[] { 0 }, blocks[2].Labels);
            Assert.Empty(blocks[2].Instructions);
            Assert.Equal(LirTerminatorKind.Return, blocks[2].Terminator!.Kind);
            Assert.Equal(0, blocks[2].Terminator!.TargetLabelId);
        }

        [Fact]
        public void BuildBlocks_Jcc_Becomes_CondJump_Terminator()
        {
            var function = new LirFunction("main", new List<LirParameter>());
            function.Instructions.Add(new LirInstruction(LirOpCode.Cmp, LirOperand.Reg(new LirVirtualRegister(0, LirType.I32)), LirOperand.Constant(0)));
            function.Instructions.Add(new LirInstruction(LirOpCode.Jcc, LirOperand.Constant((int)LirCond.Equal), LirOperand.Label(5)));
            function.Instructions.Add(new LirInstruction(LirOpCode.Label, LirOperand.Label(5)));
            function.Instructions.Add(new LirInstruction(LirOpCode.Ret, LirOperand.Label(0)));

            var blocks = function.Blocks;

            Assert.Equal(2, blocks.Count);
            Assert.Single(blocks[0].Instructions);
            Assert.Equal(LirTerminatorKind.CondJump, blocks[0].Terminator!.Kind);
            Assert.Equal(LirCond.Equal, blocks[0].Terminator!.Cond);
            Assert.Equal(5, blocks[0].Terminator!.TargetLabelId);
            Assert.Equal(new[] { 5, 0 }, blocks[1].Labels);
            Assert.Empty(blocks[1].Instructions);
            Assert.Equal(LirTerminatorKind.Return, blocks[1].Terminator!.Kind);
            Assert.Equal(0, blocks[1].Terminator!.TargetLabelId);
        }

        [Fact]
        public void BuildBlocks_Ret_Gets_Own_Epilog_Block_With_EndLabel()
        {
            var function = new LirFunction("main", new List<LirParameter>());
            function.Instructions.Add(new LirInstruction(LirOpCode.Const, _allocator.Allocate(), LirOperand.Constant(1)));
            function.Instructions.Add(new LirInstruction(LirOpCode.StoreRet, LirOperand.Reg(new LirVirtualRegister(0, LirType.I32))));
            function.Instructions.Add(new LirInstruction(LirOpCode.Ret, LirOperand.Label(7)));

            var blocks = function.Blocks;

            Assert.Equal(2, blocks.Count);
            Assert.Equal(2, blocks[0].Instructions.Count);
            Assert.Null(blocks[0].Terminator);
            Assert.Equal(new[] { 7 }, blocks[1].Labels);
            Assert.Empty(blocks[1].Instructions);
            Assert.Equal(LirTerminatorKind.Return, blocks[1].Terminator!.Kind);
        }

        [Fact]
        public void Optimize_Folds_Adjacent_Const_Mov()
        {
            var function = new LirFunction("main", new List<LirParameter>());
            var c = _allocator.Allocate();
            var x = _allocator.Allocate();
            function.Instructions.Add(new LirInstruction(LirOpCode.Const, c, LirOperand.Constant(42)));
            function.Instructions.Add(new LirInstruction(LirOpCode.Mov, x, LirOperand.Reg(c)));
            function.Instructions.Add(new LirInstruction(LirOpCode.StoreRet, LirOperand.Reg(x)));
            function.Instructions.Add(new LirInstruction(LirOpCode.Ret, LirOperand.Label(0)));

            function.Optimize();

            var block = function.Blocks[0];
            Assert.Equal(2, block.Instructions.Count);
            Assert.Equal(LirOpCode.Const, block.Instructions[0].OpCode);
            Assert.Equal(x, block.Instructions[0].Dst);
            Assert.Equal(42, block.Instructions[0].A.Imm);
            Assert.Equal(LirOpCode.StoreRet, block.Instructions[1].OpCode);
        }

        [Fact]
        public void Optimize_Does_Not_Fold_When_Register_Reused()
        {
            var function = new LirFunction("main", new List<LirParameter>());
            var c = _allocator.Allocate();
            var x = _allocator.Allocate();
            var y = _allocator.Allocate();
            function.Instructions.Add(new LirInstruction(LirOpCode.Const, c, LirOperand.Constant(42)));
            function.Instructions.Add(new LirInstruction(LirOpCode.Mov, x, LirOperand.Reg(c)));
            function.Instructions.Add(new LirInstruction(LirOpCode.Mov, y, LirOperand.Reg(c)));
            function.Instructions.Add(new LirInstruction(LirOpCode.Ret, LirOperand.Label(0)));

            function.Optimize();

            var block = function.Blocks[0];
            Assert.Equal(3, block.Instructions.Count);
            Assert.Equal(LirOpCode.Const, block.Instructions[0].OpCode);
        }

        [Fact]
        public void Print_Const_Show_Op_And_Imm()
        {
            var text = new LirInstruction(LirOpCode.Const, _allocator.Allocate(), LirOperand.Constant(42)).ToString();

            Assert.Equal("const v0 42", text);
        }

        [Fact]
        public void Print_Binary_Show_Dst_And_Two_Operands()
        {
            var dst = _allocator.Allocate();
            var srcA = _allocator.Allocate();
            var srcB = _allocator.Allocate();

            var text = new LirInstruction(LirOpCode.Add, dst, LirOperand.Reg(srcA), LirOperand.Reg(srcB)).ToString();

            Assert.Equal("add v0 v1, v2", text);
        }

        [Fact]
        public void Print_Load_Shows_Memory_Operand()
        {
            var dst = _allocator.Allocate();
            var baseReg = _allocator.Allocate();

            var text = LirMem.Load(dst, baseReg, -16, 4).ToString();

            Assert.Equal("load v0 [v1-16] :32bit", text);
        }

        [Fact]
        public void Print_Store_Shows_Memory_Operand()
        {
            var baseReg = _allocator.Allocate();
            var src = _allocator.Allocate();

            var text = LirMem.Store(baseReg, 8, src, 8).ToString();

            Assert.Equal("store [v0+8], v1 :64bit", text);
        }

        [Fact]
        public void Print_Label_And_Branch()
        {
            var label = LirOperand.Label(3);

            Assert.Equal("jmp L3", new LirInstruction(LirOpCode.Jmp, label).ToString());
            Assert.Equal("jcc Equal, L3", new LirInstruction(LirOpCode.Jcc, LirOperand.Constant((int)LirCond.Equal), label).ToString());
            Assert.Equal("setcc v0 Greater", new LirInstruction(LirOpCode.Setcc, _allocator.Allocate(), LirOperand.Constant((int)LirCond.Greater)).ToString());
        }

        [Fact]
        public void Print_Function_With_Parameters()
        {
            var function = new LirFunction("main", new List<LirParameter>(new[] { new LirParameter(null, 0) }));
            function.Instructions.Add(new LirInstruction(LirOpCode.Const, _allocator.Allocate(), LirOperand.Constant(1)));
            function.Instructions.Add(new LirInstruction(LirOpCode.Ret));

            var text = LirPrinter.Format(function);

            Assert.Contains("FUNCTION main (p0)", text);
            Assert.Contains("const v0 1", text);
            Assert.Contains("ret L0", text);
            Assert.Contains("bb1: #L0", text);
        }

        [Fact]
        public void Print_Program_With_Data()
        {
            var program = new LirProgram("main");
            var key = program.InternString("hello");
            var function = new LirFunction("main", new List<LirParameter>());
            function.Instructions.Add(new LirInstruction(LirOpCode.LeaData, _allocator.Allocate(), LirOperand.Data(key)));
            program.Functions.Add(function);

            var text = LirPrinter.Format(program);

            Assert.Contains("PROGRAM entry = main", text);
            Assert.Contains(".data", text);
            Assert.Contains("D$hello = \"hello\"", text);
            Assert.Contains("leadata v0 D$hello", text);
        }

        [Fact]
        public void InternString_Deduplicates_By_Text()
        {
            var program = new LirProgram("main");

            var key1 = program.InternString("abc");
            var key2 = program.InternString("abc");

            Assert.Equal(key1, key2);
            Assert.Single(program.Data);
        }
    }
}