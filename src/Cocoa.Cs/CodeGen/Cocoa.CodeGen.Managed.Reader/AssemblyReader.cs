using Cocoa.CodeGen.Managed.Structure;
using System;
using System.IO;

namespace Cocoa.CodeGen.Managed.Reader
{
    /// <summary>单个引用程序集的元数据读取（TypeDef/TypeRef/MethodDef/AssemblyRef + 方法签名）。</summary>
    internal sealed partial class AssemblyReader
    {
        public static readonly AssemblyReader Empty = new AssemblyReader(null);

        private byte[]? _data;
        private uint _tableRva;
        private uint _stringsRva;
        private uint _blobRva;
        private uint _guidRva;
        private byte _heapSizes;
        private ulong _valid;
        private ulong _sorted;
        private int[] _rowCounts = Array.Empty<int>();
        private int[] _tableOffsets = new int[64];

        private string _assemblyName = "";
        private Version _version = new Version(0, 0, 0, 0);
        private byte[] _publicKeyOrToken = Array.Empty<byte>();
        private string _culture = "";
        private uint _flags;

        public string AssemblyName => _assemblyName;
        public Version Version => _version;
        public byte[] PublicKeyOrToken => _publicKeyOrToken;
        public string Culture => _culture;
        public uint Flags => _flags;

        public string DebugDump()
        {
            if (_data == null) return "no data";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"tableRva={_tableRva} stringsRva={_stringsRva} blobRva={_blobRva} heapSizes=0x{_heapSizes:X2}");
            sb.AppendLine($"valid=0x{_valid:X16}");
            var assemblyOffset = _tableOffsets[0x20];
            sb.AppendLine($"assemblyTableOffset={assemblyOffset}");
            if (assemblyOffset >= 0 && assemblyOffset < _data.Length)
            {
                var row = _data.AsSpan(assemblyOffset, Math.Min(64, _data.Length - assemblyOffset));
                sb.AppendLine($"assemblyRowBytes={BitConverter.ToString(row.ToArray())}");
            }
            return sb.ToString();
        }

        internal AssemblyReader(string? path)
        {
            if (path == null)
            {
                return;
            }

            var data = File.ReadAllBytes(path);
            _data = data;
            Parse(data);
        }
    }
}
