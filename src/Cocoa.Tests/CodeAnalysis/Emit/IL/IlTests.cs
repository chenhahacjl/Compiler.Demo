using System.Collections.Generic;
using Cocoa.CodeAnalysis.Emit.IL;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Emit.IL
{
    public class IlAssemblerTests
    {
        [Fact]
        public void Assemble_LdcI4_Encodes_Opcode_And_Int32()
        {
            var assembler = new IlAssembler();
            assembler.Emit(IlOpCodes.Get("Ldc_I4"), 42);
            var code = assembler.Assemble(new List<IlInstruction>
            {
                new IlInstruction(IlOpCodes.Get("Ldc_I4"), 42),
            });

            Assert.Equal(new byte[] { 0x20, 0x2A, 0x00, 0x00, 0x00 }, code);
        }

        [Fact]
        public void Assemble_LdcI4_0_Encodes_Single_Byte()
        {
            var assembler = new IlAssembler();
            var code = assembler.Assemble(new List<IlInstruction>
            {
                new IlInstruction(IlOpCodes.Get("Ldc_I4_0"), null),
            });

            Assert.Equal(new byte[] { 0x16 }, code);
        }

        [Fact]
        public void Assemble_Add_Encodes_Single_Byte()
        {
            var assembler = new IlAssembler();
            var code = assembler.Assemble(new List<IlInstruction>
            {
                new IlInstruction(IlOpCodes.Get("Add"), null),
            });

            Assert.Equal(new byte[] { 0x58 }, code);
        }

        [Fact]
        public void Assemble_Ldstr_Reserves_Token_And_Patches_It()
        {
            var assembler = new IlAssembler();
            var instructions = new List<IlInstruction>
            {
                new IlInstruction(IlOpCodes.Get("Ldstr"), "hi"),
            };

            var code = assembler.Assemble(instructions);
            Assert.Equal(new byte[] { 0x72, 0xFF, 0xFF, 0xFF, 0xFF }, code);

            assembler.PatchStrings(code, new Dictionary<string, uint> { ["hi"] = 0x70000001 });
            Assert.Equal(new byte[] { 0x72, 0x01, 0x00, 0x00, 0x70 }, code);
        }

        [Fact]
        public void Assemble_Call_Reserves_Token_And_Patches_It()
        {
            var assembler = new IlAssembler();
            var methodRef = new IlMethodRef(
                new IlTypeRef("System", "Console", null),
                "WriteLine",
                IlType.Void,
                new[] { IlType.Object });
            var instructions = new List<IlInstruction>
            {
                new IlInstruction(IlOpCodes.Get("Call"), methodRef),
            };

            var code = assembler.Assemble(instructions);
            Assert.Equal(new byte[] { 0x28, 0xFF, 0xFF, 0xFF, 0xFF }, code);

            assembler.PatchTokens(code, new Dictionary<object, uint> { [methodRef] = 0x0A000001 });
            Assert.Equal(new byte[] { 0x28, 0x01, 0x00, 0x00, 0x0A }, code);
        }

        [Fact]
        public void Assemble_Br_Fixes_Up_Forward_Branch_Offset()
        {
            var assembler = new IlAssembler();
            var nop = new IlInstruction(IlOpCodes.Get("Nop"), null);
            var br = new IlInstruction(IlOpCodes.Get("Br"), nop);
            var instructions = new List<IlInstruction> { br, nop };

            var code = assembler.Assemble(instructions);
            // br @0（5 字节）→ 目标 nop @5 → rel = 5 - (0+5) = 0
            Assert.Equal(new byte[] { 0x38, 0x00, 0x00, 0x00, 0x00, 0x00 }, code);
        }

        [Fact]
        public void Assemble_Br_Fixes_Backward_Branch_Offset()
        {
            var assembler = new IlAssembler();
            var nop = new IlInstruction(IlOpCodes.Get("Nop"), null);
            var br = new IlInstruction(IlOpCodes.Get("Br"), nop);
            var instructions = new List<IlInstruction> { nop, br };

            var code = assembler.Assemble(instructions);
            // nop @0，br @1（5 字节）→ 目标 nop @0 → rel = 0 - (1+5) = -6
            Assert.Equal(new byte[] { 0x00, 0x38, 0xFA, 0xFF, 0xFF, 0xFF }, code);
        }

        [Fact]
        public void Assemble_ShortBranch_Encodes_SByte_Offset()
        {
            var assembler = new IlAssembler();
            var nop = new IlInstruction(IlOpCodes.Get("Nop"), null);
            var br = new IlInstruction(IlOpCodes.Get("Br_S"), nop);
            var instructions = new List<IlInstruction> { br, nop };

            var code = assembler.Assemble(instructions);
            Assert.Equal(new byte[] { 0x2B, 0x00, 0x00 }, code);
        }

        [Fact]
        public void Assemble_Stloc_Encodes_UInt16_Index()
        {
            var assembler = new IlAssembler();
            var code = assembler.Assemble(new List<IlInstruction>
            {
                new IlInstruction(IlOpCodes.Get("Stloc"), (ushort)3),
            });

            Assert.Equal(new byte[] { 0xFE, 0x0E, 0x03, 0x00 }, code);
        }
    }

    public class MetadataBuilderTests
    {
        [Fact]
        public void EncodeMethodSignature_Encodes_Header_And_Types()
        {
            var builder = new MetadataBuilder("test", "test");
            var signature = builder.EncodeMethodSignature(IlType.Int32, new[] { IlType.Int32, IlType.Boolean });
            Assert.Equal(new byte[] { 0x00, 0x02, 0x08, 0x08, 0x02 }, signature);
        }

        [Fact]
        public void EncodeMethodSignature_Encodes_String_Parameter()
        {
            var builder = new MetadataBuilder("test", "test");
            var signature = builder.EncodeMethodSignature(IlType.Void, new[] { IlType.String });
            Assert.Equal(new byte[] { 0x00, 0x01, 0x01, 0x0E }, signature);
        }

        [Fact]
        public void EncodeLocalVarSignature_Encodes_Header_And_Count()
        {
            var builder = new MetadataBuilder("test", "test");
            var signature = builder.EncodeLocalVarSignature(new[] { IlType.Int32, IlType.String });
            Assert.Equal(new byte[] { 0x07, 0x02, 0x08, 0x0E }, signature);
        }

        [Fact]
        public void GetOrAddUserString_Assigns_Token_And_Encodes_Length()
        {
            var builder = new MetadataBuilder("test", "test");
            var token = builder.GetOrAddUserString("hi");
            Assert.Equal(0x70000001u, token);

            var blobs = builder.Serialize(new Dictionary<IlMethodDef, uint>());
            // #US 流：索引 0 保留 + 长度（2*2+1=5）+ "hi" UTF-16 + 尾字节
            Assert.Equal(0, blobs.Us[0]);
            Assert.Equal(5, blobs.Us[1]);
            Assert.Equal((byte)'h', blobs.Us[2]);
            Assert.Equal(0, blobs.Us[3]);
            Assert.Equal((byte)'i', blobs.Us[4]);
            Assert.Equal(0, blobs.Us[5]);
        }

        [Fact]
        public void Serialize_Writes_Tables_Header_With_BSJB_Stream()
        {
            var builder = new MetadataBuilder("test", "test");
            var blobs = builder.Serialize(new Dictionary<IlMethodDef, uint>());

            // 表流头：reserved(4) + major=2 + minor=0 + heapSizes=0 + reserved=1
            Assert.Equal(0, System.BitConverter.ToInt32(blobs.Tables, 0));
            Assert.Equal(2, blobs.Tables[4]);
            Assert.Equal(0, blobs.Tables[5]);
            Assert.Equal(0, blobs.Tables[6]);
            Assert.Equal(1, blobs.Tables[7]);
            // Valid 位图（offset 8）
            var valid = System.BitConverter.ToUInt64(blobs.Tables, 8);
            Assert.True((valid & (1UL << 0x00)) != 0); // Module
            Assert.True((valid & (1UL << 0x02)) != 0); // TypeDef
            Assert.True((valid & (1UL << 0x06)) != 0); // MethodDef
            Assert.True((valid & (1UL << 0x0A)) != 0); // MemberRef
            Assert.True((valid & (1UL << 0x20)) != 0); // Assembly
            Assert.True((valid & (1UL << 0x23)) != 0); // AssemblyRef
        }
    }
}
