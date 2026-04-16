using System.Reflection;
using System.Text;
using SkyrimLightingPatcher.Core.Services;

namespace SkyrimLightingPatcher.Tests;

public sealed class Lz4FrameDecoderTests
{
    [Fact]
    public void DecompressLz4_DecodesFrameWithCrossBlockBackReferences()
    {
        var frame = BuildDependentLz4Frame();

        var method = typeof(ScanService).Assembly
            .GetType("SkyrimLightingPatcher.Core.Services.BsaArchiveReader", throwOnError: true)!
            .GetMethod("DecompressLz4", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var output = (byte[])method!.Invoke(null, new object[] { frame, 8 })!;
        Assert.Equal("ABCDABCD", Encoding.ASCII.GetString(output));
    }

    private static byte[] BuildDependentLz4Frame()
    {
        using var stream = new MemoryStream();

        // Magic + FLG(version=01, block-dependent) + BD(max block 64 KB) + HC.
        stream.Write(new byte[] { 0x04, 0x22, 0x4D, 0x18, 0x40, 0x40, 0x00 });

        // Block 1: literal-only block -> "ABCD".
        stream.Write(new byte[] { 0x05, 0x00, 0x00, 0x00 });
        stream.Write(new byte[] { 0x40, (byte)'A', (byte)'B', (byte)'C', (byte)'D' });

        // Block 2: match-only block, offset=4, match length=4.
        stream.Write(new byte[] { 0x03, 0x00, 0x00, 0x00 });
        stream.Write(new byte[] { 0x00, 0x04, 0x00 });

        // End mark.
        stream.Write(new byte[] { 0x00, 0x00, 0x00, 0x00 });

        return stream.ToArray();
    }
}
