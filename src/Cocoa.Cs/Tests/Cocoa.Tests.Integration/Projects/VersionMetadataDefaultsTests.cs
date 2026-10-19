using System;
using System.IO;
using System.Linq;
using System.Text;
using Cocoa.Build;
using Xunit;

namespace Cocoa.Tests.Integration.Projects
{
    /// <summary>
    /// 产物版本元数据缺省值：未配置 Description 时 FileDescription 回落为 AssemblyName，
    /// 保证右鍵属性「详细信息」页非空（降误报观感）。
    /// </summary>
    public class VersionMetadataDefaultsTests : IDisposable
    {
        private readonly string _directory;
        private readonly StringWriter _out = new();

        public VersionMetadataDefaultsTests()
        {
            _directory = Path.Combine(Path.GetTempPath(), "cocoa_vermeta_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            File.WriteAllText(Path.Combine(_directory, "main.co"),
                "using System\n\nfunction Main()\n{\n    Console.WriteLine(\"ok\")\n}\n");
        }

        public void Dispose()
        {
            _out.Dispose();
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        [Fact]
        public void DefaultBuild_Fills_FileDescription_From_AssemblyName()
        {
            var xml =
                "<Project Version=\"1\">\n" +
                "  <PropertyGroup Label=\"Language\"><Language>Cocoa</Language></PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Assembly\"><AssemblyName>VersionProbe</AssemblyName></PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Target\"><Backend>native</Backend><Platform>x64</Platform><TargetOS>windows</TargetOS></PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Output\"><OutputType>Executable</OutputType></PropertyGroup>\n" +
                "  <PropertyGroup Label=\"Build\"><OutputPath>out</OutputPath><SharedSystemLibrary>false</SharedSystemLibrary></PropertyGroup>\n" +
                "  <ItemGroup><Source Include=\"*.co\" /></ItemGroup>\n" +
                "</Project>\n";
            var projectPath = Path.Combine(_directory, "VersionProbe.coproj");
            File.WriteAllText(projectPath, xml);
            var project = CocoaProjectFile.Load(projectPath);

            var result = ProjectBuilder.Build(project, new ProjectBuildOptions { NoIncremental = true }, _out);
            Assert.True(result.Success, _out.ToString());

            var outputPath = Path.Combine(_directory, "out", project.GetDefaultOutputFileName());
            Assert.True(File.Exists(outputPath), $"output '{outputPath}' not found");

            var image = File.ReadAllBytes(outputPath);
            var rsrc = ExtractSection(image, ".rsrc");

            // 版本字符串表含 FileDescription 的缺省值 = AssemblyName（VersionProbe，UTF-16LE）
            var fileDescriptionValue = Encoding.Unicode.GetBytes("VersionProbe\0");
            Assert.True(Contains(rsrc, fileDescriptionValue), "FileDescription 缺省值（AssemblyName）缺失于 .rsrc");

            // 未配置任何图标文件 → 内嵌默认图标兜底（RT_GROUP_ICON = 14 在资源根目录）
            Assert.True(HasResourceType(rsrc, 14), "内嵌默认图标（RT_GROUP_ICON）未嵌入");
        }

        /// <summary>资源目录根（.rsrc 首字节）是否含指定类型 ID（按 id 条目表扫描）。</summary>
        private static bool HasResourceType(byte[] rsrc, uint wantedType)
        {
            if (rsrc.Length < 16)
            {
                return false;
            }

            var idCount = BitConverter.ToUInt16(rsrc, 14);
            for (var i = 0; i < idCount; i++)
            {
                var type = BitConverter.ToUInt32(rsrc, 16 + i * 8);
                if (type == wantedType) return true;
            }

            return false;
        }

        private static byte[] ExtractSection(byte[] image, string wantedName)
        {
            var peOffset = BitConverter.ToInt32(image, 0x3C);
            var numSections = BitConverter.ToInt16(image, peOffset + 6);
            var optSize = BitConverter.ToInt16(image, peOffset + 20);
            var sectionTable = peOffset + 24 + optSize;
            for (var i = 0; i < numSections; i++)
            {
                var offset = sectionTable + i * 40;
                var name = Encoding.ASCII.GetString(image, offset, 8).TrimEnd('\0');
                if (name == wantedName)
                {
                    var rawPtr = BitConverter.ToInt32(image, offset + 20);
                    var rawSize = BitConverter.ToInt32(image, offset + 16);
                    return image.Skip(rawPtr).Take(rawSize).ToArray();
                }
            }

            return Array.Empty<byte>();
        }

        private static bool Contains(byte[] haystack, byte[] needle)
        {
            for (var i = 0; i + needle.Length <= haystack.Length; i++)
            {
                var ok = true;
                for (var j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        ok = false;
                        break;
                    }
                }

                if (ok) return true;
            }

            return false;
        }
    }
}