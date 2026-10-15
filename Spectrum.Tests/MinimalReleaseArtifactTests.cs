// Developer :: > Gehan Fernando
using System;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Spectrum.Tests;

// Makes sure the release output stays minimal, with no stray files shipping along.
public class MinimalReleaseArtifactTests
{
    private static readonly string[] RequiredFiles =
    {
        "Spectrum.exe", "Spectrum.exe.config", "Bass.Net.dll", "NAudio.dll",
        "bass.dll", "basswasapi.dll", "System.Buffers.dll",
        "System.Memory.dll", "System.Numerics.Vectors.dll", "System.Resources.Extensions.dll",
        "System.Runtime.CompilerServices.Unsafe.dll", "THIRD_PARTY_NOTICES.md"
    };

    private static readonly string[] NoticePackages =
    {
        "System.Buffers.4.5.0", "System.Memory.4.5.3", "System.Numerics.Vectors.4.5.0",
        "System.Resources.Extensions.4.6.0", "System.Runtime.CompilerServices.Unsafe.4.5.2"
    };

    [Fact]
    public void ApplicationReleaseContainsExactlyTwelveEssentialRootFilesAndNoSubdirectories()
    {
        var release = ApplicationRelease();
        Assert.True(File.Exists(Path.Combine(release, "Spectrum.exe")));
        Assert.Empty(Directory.GetDirectories(release, "*", SearchOption.AllDirectories));
        var actual = Directory.GetFiles(release, "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        Assert.Equal(RequiredFiles.OrderBy(name => name, StringComparer.Ordinal), actual);
        Assert.DoesNotContain(actual, name => name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) ||
            name.IndexOf("test", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    [Fact]
    public void CombinedNoticeRetainsTheCompleteOverviewAndAllFiveVerbatimPackagePayloads()
    {
        var root = RepositoryRoot();
        using var expected = new MemoryStream();
        Append(expected, File.ReadAllBytes(Path.Combine(root, "THIRD_PARTY_NOTICES.md")));
        foreach (var package in NoticePackages)
        {
            var delimiter = "\r\n\r\n---\r\n\r\n## Bundled package aggregate: " + package + "\r\n\r\n";
            Append(expected, new UTF8Encoding(false).GetBytes(delimiter));
            Append(expected, File.ReadAllBytes(Path.Combine(root, "packages", package, "THIRD-PARTY-NOTICES.TXT")));
        }
        var actual = File.ReadAllBytes(Path.Combine(ApplicationRelease(), "THIRD_PARTY_NOTICES.md"));
        Assert.Equal(expected.ToArray(), actual);
    }

    [Theory]
    [InlineData("bass.dll")]
    [InlineData("basswasapi.dll")]
    public void BothUnchangedNativeRuntimeLibrariesAreX86Pe32(string name)
    {
        var shipped = File.ReadAllBytes(Path.Combine(ApplicationRelease(), name));
        var source = File.ReadAllBytes(Path.Combine(RepositoryRoot(), "Spectrum", "Ref_Files", name));
        Assert.Equal(source, shipped);
        using var reader = new BinaryReader(new MemoryStream(shipped, false));
        Assert.Equal((ushort)0x5A4D, reader.ReadUInt16());
        reader.BaseStream.Position = 0x3C;
        var peOffset = reader.ReadInt32();
        Assert.InRange(peOffset, 0x40, shipped.Length - 26);
        reader.BaseStream.Position = peOffset;
        Assert.Equal(0x00004550u, reader.ReadUInt32());
        Assert.Equal((ushort)0x014C, reader.ReadUInt16());
        reader.BaseStream.Position = peOffset + 24;
        Assert.Equal((ushort)0x010B, reader.ReadUInt16());
    }

    private static void Append(Stream stream, byte[] bytes) => stream.Write(bytes, 0, bytes.Length);

    private static string ApplicationRelease() =>
        Path.Combine(RepositoryRoot(), "Spectrum", "bin", "Release");

    private static string RepositoryRoot()
    {
        foreach (var candidate in new[]
                 {
                     Path.GetDirectoryName(typeof(MinimalReleaseArtifactTests).Assembly.Location),
                     AppDomain.CurrentDomain.BaseDirectory, Environment.CurrentDirectory
                 })
        {
            var directory = new DirectoryInfo(candidate);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Spectrum.sln")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }
        throw new DirectoryNotFoundException("Release acceptance requires the built Spectrum repository.");
    }
}
