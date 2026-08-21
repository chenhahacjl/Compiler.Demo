using System.Collections.Generic;
using Cocoa.CodeAnalysis.Emit.Native.IR;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Emit.IR
{
    public class IrVirtualRegisterTests
    {
        [Fact]
        public void Allocator_Issues_Unique_Ids()
        {
            var allocator = new IrVirtualRegisterAllocator();

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
            var register = new IrVirtualRegister(7);

            Assert.Equal("v7", register.ToString());
        }
    }

    public class IrInstructionTests
    {
        [Fact]
        public void Const_Uses_Immediate_OperandA()
        {
            var allocator = new IrVirtualRegisterAllocator();
            var instruction = new IrInstruction(IrOpCode.Const, allocator.Allocate(), IrOperand.Constant(42));

            Assert.Equal(IrOpCode.Const, instruction.OpCode);
            Assert.Equal(42, instruction.A.Imm);
            Assert.Equal(0, instruction.Dst!.Id);
            Assert.True(instruction.B.IsNone);
        }

        [Fact]
        public void Binary_Uses_Two_Operands()
        {
            var allocator = new IrVirtualRegisterAllocator();
            var dst = allocator.Allocate();
            var srcA = allocator.Allocate();
            var srcB = allocator.Allocate();

            var instruction = new IrInstruction(IrOpCode.Add, dst, IrOperand.Reg(srcA), IrOperand.Reg(srcB));

            Assert.Equal(IrOpCode.Add, instruction.OpCode);
            Assert.Equal(srcA, instruction.A.Register);
            Assert.Equal(srcB, instruction.B.Register);
            Assert.Equal(dst, instruction.Dst);
        }

        [Fact]
        public void Memory_Load_Carries_Offset_And_Size()
        {
            var allocator = new IrVirtualRegisterAllocator();
            var dst = allocator.Allocate();
            var baseReg = allocator.Allocate();

            var instruction = IrMem.Load(dst, baseReg, -16, 4);

            Assert.Equal(IrOpCode.Load, instruction.OpCode);
            Assert.Equal(baseReg, instruction.A.Register);
            Assert.Equal(-16, instruction.Offset);
            Assert.Equal(4, instruction.ByteSize);
        }

        [Fact]
        public void Memory_Store_Carries_Offset_And_Size()
        {
            var allocator = new IrVirtualRegisterAllocator();
            var baseReg = allocator.Allocate();
            var src = allocator.Allocate();

            var instruction = IrMem.Store(baseReg, 8, src, 8);

            Assert.Equal(IrOpCode.Store, instruction.OpCode);
            Assert.Equal(src, instruction.B.Register);
            Assert.Equal(8, instruction.Offset);
            Assert.Equal(8, instruction.ByteSize);
        }
    }

    public class IrPrinterTests
    {
        private readonly IrVirtualRegisterAllocator _allocator = new IrVirtualRegisterAllocator();

        [Fact]
        public void Print_Const_Show_Op_And_Imm()
        {
            var text = new IrInstruction(IrOpCode.Const, _allocator.Allocate(), IrOperand.Constant(42)).ToString();

            Assert.Equal("const v0 42", text);
        }

        [Fact]
        public void Print_Binary_Show_Dst_And_Two_Operands()
        {
            var dst = _allocator.Allocate();
            var srcA = _allocator.Allocate();
            var srcB = _allocator.Allocate();

            var text = new IrInstruction(IrOpCode.Add, dst, IrOperand.Reg(srcA), IrOperand.Reg(srcB)).ToString();

            Assert.Equal("add v0 v1, v2", text);
        }

        [Fact]
        public void Print_Load_Shows_Memory_Operand()
        {
            var dst = _allocator.Allocate();
            var baseReg = _allocator.Allocate();

            var text = IrMem.Load(dst, baseReg, -16, 4).ToString();

            Assert.Equal("load v0 [v1-16] :32bit", text);
        }

        [Fact]
        public void Print_Store_Shows_Memory_Operand()
        {
            var baseReg = _allocator.Allocate();
            var src = _allocator.Allocate();

            var text = IrMem.Store(baseReg, 8, src, 8).ToString();

            Assert.Equal("store [v0+8], v1 :64bit", text);
        }

        [Fact]
        public void Print_Label_And_Branch()
        {
            var label = IrOperand.Label(3);

            Assert.Equal("jmp L3", new IrInstruction(IrOpCode.Jmp, label).ToString());
            Assert.Equal("jcc Equal, L3", new IrInstruction(IrOpCode.Jcc, IrOperand.Constant((int)IrCond.Equal), label).ToString());
            Assert.Equal("setcc v0 Greater", new IrInstruction(IrOpCode.Setcc, _allocator.Allocate(), IrOperand.Constant((int)IrCond.Greater)).ToString());
        }

        [Fact]
        public void Print_Function_With_Parameters()
        {
            var function = new IrFunction("main", new List<IrParameter>(new[] { new IrParameter(null, 0) }));
            function.Instructions.Add(new IrInstruction(IrOpCode.Const, _allocator.Allocate(), IrOperand.Constant(1)));
            function.Instructions.Add(new IrInstruction(IrOpCode.Ret));

            var text = IrPrinter.Format(function);

            Assert.Contains("FUNCTION main (p0)", text);
            Assert.Contains("const v0 1", text);
            Assert.Contains("ret", text);
        }

        [Fact]
        public void Print_Program_With_Data()
        {
            var program = new IrProgram("main");
            var key = program.InternString("hello");
            var function = new IrFunction("main", new List<IrParameter>());
            function.Instructions.Add(new IrInstruction(IrOpCode.LeaData, _allocator.Allocate(), IrOperand.Data(key)));
            program.Functions.Add(function);

            var text = IrPrinter.Format(program);

            Assert.Contains("PROGRAM entry = main", text);
            Assert.Contains(".data", text);
            Assert.Contains("D$hello = \"hello\"", text);
            Assert.Contains("leadata v0 D$hello", text);
        }

        [Fact]
        public void InternString_Deduplicates_By_Text()
        {
            var program = new IrProgram("main");

            var key1 = program.InternString("abc");
            var key2 = program.InternString("abc");

            Assert.Equal(key1, key2);
            Assert.Single(program.Data);
        }
    }
}