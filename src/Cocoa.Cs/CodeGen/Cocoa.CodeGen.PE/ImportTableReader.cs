using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Cocoa.CodeGen.PE
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
            if (headers.Signature != PeConstants.NtSignature || headers.OptionalHeader.Magic != PeOptionalMagic.Pe32Plus)
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
                    var thunkOffset = rvaToOffset(thunkRva + i * (uint)ImageThunkData64.SizeOfEntry);
                    if (thunkOffset + ImageThunkData64.SizeOfEntry > (uint)image.Length)
                    {
                        break;
                    }

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

                result.Add((dllName, entries));
            }

            return result;
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
