using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Cocoa.CodeGen.ObjectFormat.PE
{
    /// <summary>导入表读取器：按 image span + 导入目录 RVA 解析 DLL/函数列表（供自检与测试）。</summary>
    public static class ImportTableReader
    {
        public static IReadOnlyList<(string DllName, IReadOnlyList<(bool ByName, ushort Ordinal, string Name)>)> Read(ReadOnlySpan<byte> image, Func<uint, uint> rvaToOffset)
        {
            var result = new List<(string, IReadOnlyList<(bool, ushort, string)>)>();
            if (image.Length < ImageNtHeaders64.Size + 8 * ImageDataDirectory.SizeOfEntry)
            {
                return result;
            }

            uint importRva = 0;
            uint importSize = 0;
            var headers = ImageNtHeaders64.Read(image.Slice(0, ImageNtHeaders64.Size));
            if (headers.Signature != PeConstants.NtSignature)
            {
                return result;
            }

            importRva = headers.OptionalHeader.DataDirectories[(int)PeDataDirectoryEntry.Import].VirtualAddress;
            importSize = headers.OptionalHeader.DataDirectories[(int)PeDataDirectoryEntry.Import].Size;
            return ReadAt(image, rvaToOffset, importRva, importSize);
        }

        public static IReadOnlyList<(string DllName, IReadOnlyList<(bool ByName, ushort Ordinal, string Name)>)> ReadAt(
            ReadOnlySpan<byte> image,
            Func<uint, uint> rvaToOffset,
            uint importRva,
            uint importSize)
        {
            var result = new List<(string, IReadOnlyList<(bool, ushort, string)>)>();
            if (importRva == 0 || importSize == 0)
            {
                return result;
            }

            // PE32（x86）thunk 项 4 字节、PE32+（x64）8 字节——按镜像 magic 自适应。
            var pe32 = IsPe32(image);
            var thunkSize = pe32 ? ImageThunkData32.SizeOfEntry : ImageThunkData64.SizeOfEntry;

            var baseOffset = rvaToOffset(importRva);

            for (uint index = 0; ; index++)
            {
                var descriptorOffset = baseOffset + index * ImageImportDescriptor.SizeOfEntry;
                if (descriptorOffset + ImageImportDescriptor.SizeOfEntry > (uint)image.Length)
                {
                    break;
                }

                var descriptor = ImageImportDescriptor.Read(image.Slice((int)descriptorOffset, ImageImportDescriptor.SizeOfEntry));
                if (descriptor.IsEndOfArray)
                {
                    break;
                }

                var dllNameOffset = rvaToOffset(descriptor.Name);
                var dllName = ReadAscii(image, dllNameOffset);
                var thunkRva = descriptor.OriginalFirstThunk != 0 ? descriptor.OriginalFirstThunk : descriptor.FirstThunk;
                var entries = new List<(bool, ushort, string)>();
                for (uint i = 0; ; i++)
                {
                    var thunkOffset = rvaToOffset(thunkRva + i * (uint)thunkSize);
                    if (thunkOffset + (uint)thunkSize > (uint)image.Length)
                    {
                        break;
                    }

                    if (pe32)
                    {
                        var thunk = ImageThunkData32.Read(image.Slice((int)thunkOffset, ImageThunkData32.SizeOfEntry));
                        if (thunk.IsNull)
                        {
                            break;
                        }

                        if (thunk.IsOrdinal)
                        {
                            entries.Add((false, thunk.OrdinalNumber, string.Empty));
                        }
                        else
                        {
                            var byName = ImageImportByName.Read(image.Slice((int)rvaToOffset(thunk.AddressOfDataRva)));
                            entries.Add((true, byName.Hint, byName.NameString));
                        }
                    }
                    else
                    {
                        var thunk = ImageThunkData64.Read(image.Slice((int)thunkOffset, ImageThunkData64.SizeOfEntry));
                        if (thunk.IsNull)
                        {
                            break;
                        }

                        if (thunk.IsOrdinal)
                        {
                            entries.Add((false, thunk.OrdinalNumber, string.Empty));
                        }
                        else
                        {
                            var byName = ImageImportByName.Read(image.Slice((int)rvaToOffset(thunk.AddressOfDataRva)));
                            entries.Add((true, byName.Hint, byName.NameString));
                        }
                    }
                }

                result.Add((dllName, entries));
            }

            return result;
        }

        /// <summary>镜像 OptionalHeader magic 判定：PE32（0x10B，x86）为 true；PE32+（0x20B，x64）为 false。</summary>
        private static bool IsPe32(ReadOnlySpan<byte> image)
        {
            if (image.Length < 0x40)
            {
                return false;
            }

            var lfanew = BinaryPrimitives.ReadInt32LittleEndian(image.Slice(0x3C, 4));
            if (lfanew < 0 || (uint)(lfanew + 24 + 2) > (uint)image.Length)
            {
                return false;
            }

            if (BinaryPrimitives.ReadInt32LittleEndian(image.Slice(lfanew, 4)) != PeConstants.NtSignature)
            {
                return false;
            }

            return BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(lfanew + 24, 2)) == (ushort)PeOptionalMagic.Pe32;
        }

        private static string ReadAscii(ReadOnlySpan<byte> image, uint offset)
        {
            var end = (int)offset;
            while (end < image.Length && image[end] != 0)
            {
                end++;
            }

            return Encoding.ASCII.GetString(image.Slice((int)offset, end - (int)offset));
        }
    }
}
